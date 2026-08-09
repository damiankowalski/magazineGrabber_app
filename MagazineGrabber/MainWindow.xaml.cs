using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace MagazineGrabber
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<MagazineItem> Items { get; } = new();
        public ObservableCollection<LogEntry> LogEntries { get; } = new();

        private readonly List<IMagazineProvider> _providers;
        private readonly DownloadManager _downloadManager = new();
        private IMagazineProvider? _activeProvider;
        private string? _downloadFolder;
        private CancellationTokenSource? _cts;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // Add a new provider here (and implement IMagazineProvider) to support another site.
            _providers = new List<IMagazineProvider>
            {
                new ArchiveOrgProvider(),
                new StareEGryProvider(),
            };
        }

        private async void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            var text = SourceUrlTextBox.Text.Trim();

            if (string.IsNullOrEmpty(text))
            {
                Log("Enter a URL first (an archive.org search/details page, or a stare.e-gry.net listing).", LogLevel.Error);
                return;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                Log($"That doesn't look like a valid URL: \"{text}\". Include the https:// prefix.", LogLevel.Error);
                return;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                Log($"Only http/https URLs are supported (got '{uri.Scheme}').", LogLevel.Error);
                return;
            }

            var provider = _providers.FirstOrDefault(p => p.CanHandle(uri));
            if (provider is null)
            {
                Log($"No provider handles '{uri.Host}'. Supported sites: archive.org and stare.e-gry.net.", LogLevel.Error);
                return;
            }

            _activeProvider = provider;

            LogSection($"Loading list  ·  {uri.Host}");
            Log($"Using the {provider.Name} provider...");

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true);
            OverallProgressBar.IsIndeterminate = true;

            // Live status while a long collection is being read (e.g. "Reading metadata 12/50").
            var status = new Progress<string>(s => ProgressText.Text = s);

            try
            {
                var items = await provider.ListItemsAsync(uri, status, token);
                Items.Clear();
                foreach (var item in items)
                    Items.Add(item);

                // Recognized-count breakdown by format (req: show what was recognized).
                int jp2 = items.Count(i => i.Format.Equals("jp2", StringComparison.OrdinalIgnoreCase));
                int djvu = items.Count(i => i.Format.Equals("djvu", StringComparison.OrdinalIgnoreCase));
                int pdf = items.Count(i => i.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase));

                Log($"Recognized {items.Count} item(s): {jp2} JP2, {djvu} DjVu, {pdf} PDF.",
                    items.Count > 0 ? LogLevel.Success : LogLevel.Warning);

                if (items.Count == 0)
                    Log("Nothing to download here. Make sure the URL is a search/collection, an /details/ item, or a stare.e-gry.net magazine listing.", LogLevel.Warning);

                ProgressText.Text = $"0 / {items.Count}";
                OverallProgressBar.Value = 0;
                OverallProgressBar.Maximum = Math.Max(1, items.Count);
            }
            catch (OperationCanceledException)
            {
                Log("Loading cancelled.", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                Log($"Failed to load list: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                OverallProgressBar.IsIndeterminate = false;
                SetBusy(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in Items) item.IsSelected = true;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in Items) item.IsSelected = false;
        }

        private void ChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Choose download folder" };
            if (dialog.ShowDialog() == true)
            {
                _downloadFolder = dialog.FolderName;
                FolderPathText.Text = _downloadFolder;
                OpenFolderButton.IsEnabled = true;
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_downloadFolder) || !Directory.Exists(_downloadFolder))
            {
                Log("No download folder to open yet.", LogLevel.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_downloadFolder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"Couldn't open the folder: {ex.Message}", LogLevel.Error);
            }
        }

        private async void StartDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_activeProvider is null)
            {
                Log("Load a list first.", LogLevel.Error);
                return;
            }
            if (string.IsNullOrEmpty(_downloadFolder))
            {
                Log("Choose a download folder first.", LogLevel.Error);
                return;
            }
            int selectedCount = Items.Count(i => i.IsSelected);
            if (selectedCount == 0)
            {
                Log("Nothing is selected.", LogLevel.Error);
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true);

            // Respect the UI slider, but never exceed what the provider considers safe
            // (stare.e-gry.net caps itself at 1 because of its login/session model).
            int requested = (int)ConcurrencySlider.Value;
            int concurrency = Math.Clamp(requested, 1, _activeProvider.MaxParallelDownloads);

            LogSection($"Downloading  ·  {selectedCount} selected of {Items.Count} recognized");
            if (concurrency != requested)
                Log($"{_activeProvider.Name} limits parallel downloads to {concurrency}; using {concurrency} instead of {requested}.", LogLevel.Info);
            else
                Log($"Parallel downloads: {concurrency}.", LogLevel.Info);

            var overallProgress = new Progress<(int completed, int total)>(p =>
            {
                OverallProgressBar.Maximum = Math.Max(1, p.total);
                OverallProgressBar.Value = p.completed;
                ProgressText.Text = $"{p.completed} / {p.total}";
            });

            try
            {
                var result = await _downloadManager.RunAsync(
                    Items.ToList(),
                    _activeProvider,
                    _downloadFolder,
                    maxConcurrency: concurrency,
                    overallProgress,
                    RequestLoginAsync,
                    Log,
                    token);

                WriteSummary(result);
                WriteResults(result);
                OpenFolderButton.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                Log("Download cancelled.", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                Log($"Download run failed: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                SetBusy(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("Cancelling...", LogLevel.Warning);
        }

        // req: compare recognized/selected vs what was actually downloaded/converted.
        private void WriteSummary(BatchResult r)
        {
            LogSection("Summary");
            Log($"Selected: {r.Total}");
            Log($"Downloaded OK: {r.Succeeded} / {r.Total}", r.Succeeded == r.Total ? LogLevel.Success : LogLevel.Warning);
            Log($"PDFs ready: {r.PdfCount}", r.PdfCount > 0 ? LogLevel.Success : LogLevel.Info);
            if (r.DjvuCount > 0)
                Log($"DjVu (manual conversion needed): {r.DjvuCount}", LogLevel.Warning);
            if (r.Failed > 0)
                Log($"Failed: {r.Failed}", LogLevel.Error);
        }

        // req: at the end, list every generated PDF, and list the DjVu files left behind.
        // Each file line carries its path so it renders as a link and opens on double-click.
        private void WriteResults(BatchResult r)
        {
            var pdfs = r.Outputs.Where(o => o.Kind == OutputKind.Pdf).ToList();
            var djvus = r.Outputs.Where(o => o.Kind == OutputKind.Djvu).ToList();

            LogSection("Results");

            if (pdfs.Count > 0)
            {
                Log($"PDFs generated ({pdfs.Count})  -  double-click a line to open:", LogLevel.Success);
                foreach (var o in pdfs)
                    Log($"   \u2022 {o.Path}", LogLevel.Success, o.Path);
            }

            if (djvus.Count > 0)
            {
                Log($"DjVu files for manual conversion ({djvus.Count})  -  double-click to open:", LogLevel.Warning);
                foreach (var o in djvus)
                    Log($"   \u2022 {o.Path}", LogLevel.Warning, o.Path);
            }

            if (pdfs.Count == 0 && djvus.Count == 0)
                Log("No files were produced.", LogLevel.Warning);
        }

        // Double-click an openable log line (a produced PDF/DjVu) to open it in the default app.
        private void LogListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (LogListBox.SelectedItem is not LogEntry entry || entry.FilePath is null)
                return;

            if (!File.Exists(entry.FilePath))
            {
                Log($"File no longer exists: {entry.FilePath}", LogLevel.Error);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(entry.FilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"Couldn't open the file: {ex.Message}", LogLevel.Error);
            }
        }

        // Called by DownloadManager the first time a provider reports it needs a logged-in
        // session. Shows an embedded browser pointed at the real login page and hands back
        // whatever session cookies resulted, once the user confirms they're logged in.
        private Task<List<Cookie>?> RequestLoginAsync(Uri loginUrl)
        {
            return Dispatcher.InvokeAsync<List<Cookie>?>(() =>
            {
                var dialog = new LoginWebViewDialog(loginUrl) { Owner = this };
                if (dialog.ShowDialog() == true)
                    return dialog.HarvestedCookies;
                return null;
            }).Task;
        }

        // Toggle button state around a long-running load/download so the user can cancel and
        // can't start a second run on top of the first.
        private void SetBusy(bool busy)
        {
            LoadButton.IsEnabled = !busy;
            StartButton.IsEnabled = !busy;
            CancelButton.IsEnabled = busy;
        }

        private void LogSection(string title)
        {
            void Do()
            {
                LogEntries.Add(new LogEntry { Message = $"=== {title} ===", Level = LogLevel.Info, IsSection = true });
                LogListBox.ScrollIntoView(LogEntries[^1]);
            }

            if (Dispatcher.CheckAccess()) Do();
            else Dispatcher.Invoke(Do);
        }

        // Kept as a 2-parameter overload so it still converts to Action<string, LogLevel>
        // (the delegate the DownloadManager/providers log through). The 3-arg overload adds an
        // openable file path without changing that conversion.
        private void Log(string message, LogLevel level = LogLevel.Info)
            => AddLog(message, level, null);

        private void Log(string message, LogLevel level, string? filePath)
            => AddLog(message, level, filePath);

        private void AddLog(string message, LogLevel level, string? filePath)
        {
            void DoLog()
            {
                LogEntries.Add(new LogEntry { Message = message, Level = level, FilePath = filePath });
                if (LogEntries.Count > 0)
                    LogListBox.ScrollIntoView(LogEntries[^1]);
            }

            if (Dispatcher.CheckAccess())
                DoLog();
            else
                Dispatcher.Invoke(DoLog);
        }
    }
}
