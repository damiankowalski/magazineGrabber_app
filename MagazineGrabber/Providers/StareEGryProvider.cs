using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace MagazineGrabber
{
    /// <summary>
    /// Scrapes stare.e-gry.net magazine listing pages (e.g. /czasopisma/reset). The listing
    /// itself is public. Only the actual file download requires a logged-in session - detected
    /// by checking whether the response looks like the site's "please log in" page instead of
    /// the actual file. Login itself goes through an embedded browser (see LoginWebViewDialog)
    /// rather than a guessed POST payload, since a hand-written form post has no reliable way
    /// to know this site's exact field names/CSRF handling from outside.
    /// </summary>
    public class StareEGryProvider : IMagazineProvider
    {
        public string Name => "stare.e-gry.net";
        public int MaxParallelDownloads => 1; // login/session based - safest one file at a time
        public Uri? LoginUrl => new Uri("https://stare.e-gry.net/login");

        private readonly CookieContainer _cookies = new CookieContainer();
        private readonly HttpClient _http;

        public StareEGryProvider()
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
            };
            _http = new HttpClient(handler);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MagazineGrabber/1.0");
        }

        public bool CanHandle(Uri sourceUri) =>
            sourceUri.Host.Contains("e-gry.net", StringComparison.OrdinalIgnoreCase);

        public void ApplyLoginCookies(IEnumerable<Cookie> cookies)
        {
            // Start each login from a clean jar: expire whatever is currently stored - the
            // pre-login anonymous session, or a previous session that has since gone stale - so
            // the fresh logged-in cookies can't end up duplicated next to leftovers.
            //
            // Why it matters: duplicate same-named cookies make the container send
            // "PHPSESSID=old; PHPSESSID=new" on the next request, and the server picks the wrong
            // one. That's the "first download works, the next ones fail until you restart the
            // app" symptom - a restart just happened to start from an empty jar.
            try
            {
                foreach (Cookie existing in _cookies.GetAllCookies())
                    existing.Expired = true;
            }
            catch { /* GetAllCookies is best-effort; ignore if it isn't available */ }

            var siteUri = new Uri("https://stare.e-gry.net/");
            foreach (var c in cookies)
            {
                try
                {
                    // Keep the browser's Domain/Path so the scoping matches the server's own
                    // Set-Cookie on later downloads (no host-only vs domain duplication).
                    if (!string.IsNullOrWhiteSpace(c.Domain))
                        _cookies.Add(c);
                    else
                        _cookies.Add(siteUri, new Cookie(c.Name, c.Value));
                }
                catch
                {
                    try { _cookies.Add(siteUri, new Cookie(c.Name, c.Value)); }
                    catch { /* ignore malformed/duplicate cookie entries */ }
                }
            }
        }

        public async Task<List<MagazineItem>> ListItemsAsync(Uri sourceUri, IProgress<string>? status, CancellationToken ct)
        {
            status?.Report("Fetching listing page...");
            var html = await _http.GetStringAsync(sourceUri, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var items = new List<MagazineItem>();
            var downloadLinks = doc.DocumentNode.SelectNodes("//a[contains(@href,'/czasopisma/download/')]");
            if (downloadLinks is null)
                return items;

            status?.Report($"Parsing {downloadLinks.Count} link(s)...");

            foreach (var link in downloadLinks)
            {
                var href = link.GetAttributeValue("href", "");
                if (string.IsNullOrWhiteSpace(href))
                    continue;

                var absoluteUrl = new Uri(sourceUri, href).ToString();

                var container = link.ParentNode;
                string containerText = "";
                for (int hops = 0; hops < 5 && container != null; hops++)
                {
                    containerText = HtmlEntity.DeEntitize(container.InnerText ?? "");
                    if (containerText.Contains("Format", StringComparison.OrdinalIgnoreCase))
                        break;
                    container = container.ParentNode;
                }

                // [a-z]+ on purpose, not \w+: on this site "djvu" and the next label
                // ("Rozmiar:") sometimes end up with no actual whitespace between them once
                // HtmlAgilityPack's InnerText concatenates the surrounding elements, and \w+
                // happily kept matching straight into "Rozmiar" (-> format "djvurozmiar",
                // which then corrupted the downloaded file's extension). Format values here
                // are always lowercase, so bounding on that stops exactly at the real word.
                var formatMatch = Regex.Match(containerText, @"Format:\s*([a-z]+)");
                var format = formatMatch.Success ? formatMatch.Groups[1].Value.ToLowerInvariant() : "djvu";

                var sizeMatch = Regex.Match(containerText, @"Rozmiar:\s*([\d.,]+)\s*(KB|MB|GB)", RegexOptions.IgnoreCase);
                long? sizeBytes = sizeMatch.Success ? ParseSize(sizeMatch.Groups[1].Value, sizeMatch.Groups[2].Value) : null;

                var formatIdx = containerText.IndexOf("Format:", StringComparison.OrdinalIgnoreCase);
                var title = (formatIdx > 0 ? containerText[..formatIdx] : containerText).Trim();
                if (string.IsNullOrWhiteSpace(title))
                    title = $"item-{items.Count + 1}";

                var sanitizedTitle = FileNaming.Sanitize(title);
                items.Add(new MagazineItem
                {
                    Title = title,
                    Format = format,
                    SizeBytes = sizeBytes,
                    SourceUrl = absoluteUrl,
                    SuggestedFileName = sanitizedTitle,
                    SourceFolderKey = sanitizedTitle,
                });
            }

            return items;
        }

        public async Task<DownloadOutcome> DownloadAsync(MagazineItem item, string rootFolder, IProgress<double> progress, Action<string, LogLevel> log, CancellationToken ct)
        {
            var sourceDir = Path.Combine(rootFolder, "source", FileNaming.Sanitize(item.SourceFolderKey));
            Directory.CreateDirectory(sourceDir);
            var localPath = Path.Combine(sourceDir, FileNaming.Sanitize(item.Title) + "." + item.Format);

            // this site's files are DjVu (or occasionally PDF) - report which so the batch
            // summary can separate "PDFs ready" from "DjVu left for manual conversion".
            var kind = item.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase) ? OutputKind.Pdf : OutputKind.Djvu;

            // Resumability: skip re-downloading if we already grabbed this file in a previous run.
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            {
                log($"{item.Title}: already in source\\{item.SourceFolderKey}, skipping re-download.", LogLevel.Info);
                return kind == OutputKind.Pdf ? DownloadOutcome.Pdf(localPath) : DownloadOutcome.Djvu(localPath);
            }

            // Be gentle on an old, session-limited site: a small spacing between requests and a
            // same-origin Referer make the download look like it came from the listing page and
            // reduce the chance of tripping a per-session throttle.
            await Task.Delay(600, ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, item.SourceUrl);
            request.Headers.Referrer = new Uri("https://stare.e-gry.net/");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";

            // 401/403 (or a redirect that landed back on the login page) means the session is no
            // longer valid - treat it as "needs login" so the manager re-authenticates instead
            // of just failing.
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return DownloadOutcome.AuthRequired;

            if (response.RequestMessage?.RequestUri is { } finalUri &&
                finalUri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase))
                return DownloadOutcome.AuthRequired;

            if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                // Any of the site's "you are not logged in" markers, not just the exact word.
                if (body.Contains("Zaloguj", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("logowanie", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("musisz by", StringComparison.OrdinalIgnoreCase) ||   // "musisz być zalogowany"
                    body.Contains("zalogowa", StringComparison.OrdinalIgnoreCase))      // "zalogować / zalogowany"
                    return DownloadOutcome.AuthRequired;

                return DownloadOutcome.Failed;
            }

            if (!response.IsSuccessStatusCode)
                return DownloadOutcome.Failed;

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;
                if (totalBytes > 0)
                    progress.Report((double)totalRead / totalBytes * 100.0);
            }

            if (kind == OutputKind.Pdf)
            {
                log($"{item.Title}: PDF saved to source\\{item.SourceFolderKey}.", LogLevel.Success);
                return DownloadOutcome.Pdf(localPath);
            }

            // Try automatic DjVu -> PDF if DjVuLibre (ddjvu) is available; otherwise keep the DjVu.
            var finalPdf = Path.Combine(rootFolder, FileNaming.Sanitize(item.SuggestedFileName) + ".pdf");
            if (await DjVuPdfConverter.IsAvailableAsync(ct))
            {
                item.Status = "Converting to PDF...";
                item.IsIndeterminate = true;
                try
                {
                    var ok = await DjVuPdfConverter.ConvertAsync(localPath, finalPdf, (m, l) => log($"{item.Title}: {m}", l), ct);
                    if (ok)
                    {
                        log($"{item.Title}: converted DjVu -> PDF into the download folder.", LogLevel.Success);
                        return DownloadOutcome.Pdf(finalPdf);
                    }
                }
                finally
                {
                    item.IsIndeterminate = false;
                }
            }

            log($"{item.Title}: DjVu saved to source\\{item.SourceFolderKey} - install DjVuLibre to auto-convert, or convert manually.", LogLevel.Warning);
            return DownloadOutcome.Djvu(localPath);
        }

        private static long? ParseSize(string number, string unit)
        {
            if (!double.TryParse(number.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                return null;

            double multiplier = unit.ToUpperInvariant() switch
            {
                "KB" => 1024,
                "MB" => 1024 * 1024,
                "GB" => 1024 * 1024 * 1024,
                _ => 1
            };
            return (long)(value * multiplier);
        }
    }
}
