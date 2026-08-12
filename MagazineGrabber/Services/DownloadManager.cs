using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MagazineGrabber
{
    /// <summary>One produced file, for the end-of-batch results list.</summary>
    public sealed record BatchOutput(string Title, OutputKind Kind, string Path);

    /// <summary>Tallies + concrete outputs for a finished batch, so the UI can print a real
    /// "recognized vs downloaded vs converted" summary and list every file produced.</summary>
    public sealed class BatchResult
    {
        public int Total { get; init; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int PdfCount { get; set; }
        public int DjvuCount { get; set; }
        public List<BatchOutput> Outputs { get; } = new();
    }

    /// <summary>
    /// Runs a batch of downloads through a single provider with limited concurrency.
    /// If a provider reports AuthRequired, only the first caller shows the login dialog (via
    /// the loginGate semaphore) - everyone else waits for that same login attempt instead of
    /// popping up duplicate dialogs. Returns a BatchResult the caller turns into a summary.
    /// </summary>
    public class DownloadManager
    {
        private readonly SemaphoreSlim _loginGate = new SemaphoreSlim(1, 1);
        private volatile bool _isAuthenticated;
        private int _succeeded;
        private int _failed;
        private int _pdfCount;
        private int _djvuCount;
        private readonly ConcurrentBag<BatchOutput> _outputs = new();

        public async Task<BatchResult> RunAsync(
            IReadOnlyList<MagazineItem> items,
            IMagazineProvider provider,
            string rootFolder,
            int maxConcurrency,
            IProgress<(int completed, int total)> overallProgress,
            Func<Uri, Task<List<Cookie>?>> requestLogin,
            Action<string, LogLevel> log,
            CancellationToken ct)
        {
            Directory.CreateDirectory(rootFolder);

            var selected = items.Where(i => i.IsSelected).ToList();
            int total = selected.Count;
            int completed = 0;
            _succeeded = 0;
            _failed = 0;
            _pdfCount = 0;
            _djvuCount = 0;
            _outputs.Clear();
            overallProgress.Report((0, total));

            using var throttle = new SemaphoreSlim(Math.Max(1, maxConcurrency));

            var tasks = selected.Select(async item =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    await DownloadOneAsync(item, provider, rootFolder, requestLogin, log, ct);
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    overallProgress.Report((done, total));
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

            var result = new BatchResult
            {
                Total = total,
                Succeeded = _succeeded,
                Failed = _failed,
                PdfCount = _pdfCount,
                DjvuCount = _djvuCount,
            };
            result.Outputs.AddRange(_outputs.OrderBy(o => o.Title, StringComparer.OrdinalIgnoreCase));
            return result;
        }

        private async Task DownloadOneAsync(
            MagazineItem item,
            IMagazineProvider provider,
            string rootFolder,
            Func<Uri, Task<List<Cookie>?>> requestLogin,
            Action<string, LogLevel> log,
            CancellationToken ct)
        {
            item.Status = "Downloading...";
            item.Progress = 0;
            item.IsIndeterminate = false;

            bool sessionBased = provider.LoginUrl is not null;
            int logins = 0;              // real (re)logins performed for THIS item
            const int maxLogins = 2;
            int retries = 0;             // plain retries without a new login
            const int maxRetries = 3;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var progress = new Progress<double>(p => item.Progress = p);
                DownloadOutcome outcome;

                try
                {
                    outcome = await provider.DownloadAsync(item, rootFolder, progress, log, ct);
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Cancelled";
                    item.IsIndeterminate = false;
                    throw;
                }
                catch (Exception ex)
                {
                    item.Status = "Failed";
                    item.IsIndeterminate = false;
                    log($"{item.Title}: {ex.Message}", LogLevel.Error);
                    Interlocked.Increment(ref _failed);
                    return;
                }

                switch (outcome.Result)
                {
                    case DownloadResult.Success:
                        item.IsIndeterminate = false;
                        item.Progress = 100;
                        RecordSuccess(item, outcome);
                        return;

                    case DownloadResult.AuthRequired:
                        if (logins >= maxLogins)
                        {
                            item.Status = "Failed";
                            log($"{item.Title}: still not authenticated after re-login.", LogLevel.Error);
                            Interlocked.Increment(ref _failed);
                            return;
                        }
                        // If we already believed we were logged in, the session went stale - force
                        // a brand-new login (same effect as the user restarting the app, which
                        // they found works). WebView2 keeps the browser session, so this is
                        // usually just one "I'm logged in" click, not a full re-type.
                        bool force = _isAuthenticated;
                        item.Status = force ? "Session expired - re-logging in..." : "Logging in...";
                        logins++;
                        if (!await EnsureLoggedInAsync(provider, requestLogin, log, ct, forceNew: force))
                        {
                            item.Status = "Login failed";
                            Interlocked.Increment(ref _failed);
                            return;
                        }
                        item.Status = "Retrying...";
                        await Task.Delay(500, ct);
                        continue;

                    default: // DownloadResult.Failed
                        if (retries < maxRetries)
                        {
                            retries++;
                            // On a login-based site a plain failure is almost always a dead or
                            // throttled session; refresh it once (mimics the manual restart) then
                            // retry. On public sites, just retry.
                            if (sessionBased && logins < maxLogins)
                            {
                                logins++;
                                item.Status = "Refreshing session...";
                                if (!await EnsureLoggedInAsync(provider, requestLogin, log, ct, forceNew: true))
                                {
                                    item.Status = "Login failed";
                                    Interlocked.Increment(ref _failed);
                                    return;
                                }
                            }
                            item.Status = "Retrying...";
                            await Task.Delay(1200, ct);
                            continue;
                        }
                        item.Status = "Failed";
                        log($"{item.Title}: download failed", LogLevel.Error);
                        Interlocked.Increment(ref _failed);
                        return;
                }
            }
        }

        private void RecordSuccess(MagazineItem item, DownloadOutcome outcome)
        {
            Interlocked.Increment(ref _succeeded);

            switch (outcome.Output)
            {
                case OutputKind.Pdf:
                    Interlocked.Increment(ref _pdfCount);
                    item.Status = "Done (PDF)";
                    break;
                case OutputKind.Djvu:
                    Interlocked.Increment(ref _djvuCount);
                    item.Status = "Done (DjVu)";
                    break;
                default:
                    item.Status = "Done";
                    break;
            }

            if (outcome.Output != OutputKind.None && outcome.OutputPath is not null)
                _outputs.Add(new BatchOutput(item.Title, outcome.Output, outcome.OutputPath));
        }

        private async Task<bool> EnsureLoggedInAsync(
            IMagazineProvider provider,
            Func<Uri, Task<List<Cookie>?>> requestLogin,
            Action<string, LogLevel> log,
            CancellationToken ct,
            bool forceNew = false)
        {
            await _loginGate.WaitAsync(ct);
            try
            {
                // Normal case: reuse the session another item already established. When forceNew
                // is set (the server told us the current session is stale), skip that shortcut
                // and log in again from scratch.
                if (_isAuthenticated && !forceNew)
                    return true;

                if (provider.LoginUrl is null)
                    return true; // shouldn't happen, but don't get stuck if it does

                if (forceNew)
                    _isAuthenticated = false;

                var cookies = await requestLogin(provider.LoginUrl);
                if (cookies is null || cookies.Count == 0)
                {
                    log("Login was cancelled or no session cookie was found.", LogLevel.Warning);
                    return false;
                }

                provider.ApplyLoginCookies(cookies);
                _isAuthenticated = true;
                return true;
            }
            finally
            {
                _loginGate.Release();
            }
        }
    }
}
