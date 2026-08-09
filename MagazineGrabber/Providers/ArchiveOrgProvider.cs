using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MagazineGrabber
{
    /// <summary>
    /// Talks directly to archive.org's public Advanced Search API and Metadata API - no HTML
    /// scraping. A pasted /details/&lt;id&gt; URL (single item OR collection - we don't need to
    /// know which up front) is turned into the query (identifier:"id" OR collection:"id") and
    /// run through the normal search flow. This is the same trick the reference PowerShell
    /// script uses, and it's simpler and more robust than trying to detect the id's type first.
    ///
    /// For every archive.org item, files are grouped by their base name (stripping known
    /// suffixes like _jp2/_djvu) so pdf/djvu/jp2.zip variants of the same document become one
    /// row, while an item that bundles several distinct documents under one identifier (e.g.
    /// 12 issues uploaded together) becomes several rows - one per document.
    /// </summary>
    public class ArchiveOrgProvider : IMagazineProvider
    {
        public string Name => "Internet Archive";
        public int MaxParallelDownloads => 5;
        public Uri? LoginUrl => null; // items used here are public - never needed

        private readonly HttpClient _http = new HttpClient();
        private static readonly string[] KnownSuffixes = { "_jp2", "_djvu", "_abbyy", "_djvutxt", "_chocr", "_text" };

        public bool CanHandle(Uri sourceUri) =>
            sourceUri.Host.Contains("archive.org", StringComparison.OrdinalIgnoreCase);

        public void ApplyLoginCookies(IEnumerable<Cookie> cookies) { /* never needed for archive.org */ }

        public async Task<List<MagazineItem>> ListItemsAsync(Uri sourceUri, IProgress<string>? status, CancellationToken ct)
        {
            var detailsMatch = Regex.Match(sourceUri.AbsolutePath, @"/details/([^/?#]+)");
            if (detailsMatch.Success)
            {
                var id = Uri.UnescapeDataString(detailsMatch.Groups[1].Value);
                // Works whether id is a single item or a whole collection - see class remarks.
                var query = $"(identifier:\"{id}\" OR collection:\"{id}\")";
                return await ListFromSearchAsync(query, status, ct);
            }

            return await ListFromSearchAsync(ExtractQuery(sourceUri), status, ct);
        }

        private async Task<List<MagazineItem>> ListFromSearchAsync(string query, IProgress<string>? status, CancellationToken ct)
        {
            status?.Report("Searching archive.org...");
            var identifiers = new List<(string Id, string Title)>();

            int page = 1;
            const int rows = 100;
            int numFound = int.MaxValue;

            while ((page - 1) * rows < numFound)
            {
                var url = "https://archive.org/advancedsearch.php" +
                           $"?q={Uri.EscapeDataString(query)}" +
                           "&fl[]=identifier&fl[]=title" +
                           $"&rows={rows}&page={page}&output=json";

                using var response = await _http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var responseObj = doc.RootElement.GetProperty("response");
                numFound = responseObj.GetProperty("numFound").GetInt32();

                var docsEl = responseObj.GetProperty("docs");
                if (docsEl.GetArrayLength() == 0)
                    break;

                foreach (var d in docsEl.EnumerateArray())
                {
                    var id = d.GetProperty("identifier").GetString() ?? "";
                    var title = d.TryGetProperty("title", out var t) ? (t.GetString() ?? id) : id;
                    identifiers.Add((id, title));
                }

                page++;
            }

            status?.Report($"Found {identifiers.Count} record(s), reading metadata...");

            var items = new List<MagazineItem>();
            int done = 0;
            foreach (var (id, title) in identifiers)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                status?.Report($"Reading metadata {done}/{identifiers.Count}...");
                var files = await FetchItemFilesAsync(id, ct);
                if (files != null)
                    items.AddRange(BuildItemsFromFiles(id, title, files));
            }
            return items;
        }

        /// <summary>
        /// Fetches metadata/{id} and returns a plain, already-materialized file list - nothing
        /// JsonElement-shaped escapes this method, so there's no JsonDocument lifetime to worry
        /// about. Returns null if id has no usable files (including plain collection records,
        /// which carry no "files" of their own).
        /// </summary>
        private async Task<List<(string Name, long? Size)>?> FetchItemFilesAsync(string id, CancellationToken ct)
        {
            var url = $"https://archive.org/metadata/{Uri.EscapeDataString(id)}";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("files", out var filesEl) || filesEl.GetArrayLength() == 0)
                return null;

            var fileList = new List<(string, long?)>();
            foreach (var file in filesEl.EnumerateArray())
            {
                var name = file.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null) continue;
                fileList.Add((name, ReadSize(file)));
            }
            return fileList;
        }

        private List<MagazineItem> BuildItemsFromFiles(string identifier, string itemTitle, List<(string Name, long? Size)> files)
        {
            var groups = new Dictionary<string, FileGroup>();

            foreach (var (name, size) in files)
            {
                if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var g = GetOrAdd(groups, GetGroupKey(name));
                    g.Pdf = name; g.PdfSize = size;
                }
                else if (name.EndsWith(".djvu", StringComparison.OrdinalIgnoreCase))
                {
                    var g = GetOrAdd(groups, GetGroupKey(name));
                    g.Djvu = name; g.DjvuSize = size;
                }
                else if (Regex.IsMatch(name, @"_jp2\.zip$", RegexOptions.IgnoreCase))
                {
                    var g = GetOrAdd(groups, GetGroupKey(name));
                    g.Jp2 = name; g.Jp2Size = size;
                }
            }

            var items = new List<MagazineItem>();
            bool singleGroup = groups.Count <= 1;

            foreach (var (key, g) in groups)
            {
                if (g.Pdf is null && g.Djvu is null && g.Jp2 is null)
                    continue;

                var title = singleGroup ? itemTitle : key;
                var format = g.Jp2 != null ? "jp2" : g.Djvu != null ? "djvu" : "pdf";
                var size = g.Jp2 != null ? g.Jp2Size : g.Djvu != null ? g.DjvuSize : g.PdfSize;
                var folderKey = singleGroup ? identifier : $"{identifier}_{FileNaming.Sanitize(key)}";

                items.Add(new MagazineItem
                {
                    Title = title,
                    Format = format,
                    SizeBytes = size,
                    SourceUrl = $"https://archive.org/details/{identifier}",
                    SuggestedFileName = FileNaming.Sanitize(title),
                    SourceFolderKey = folderKey,
                    ArchiveIdentifier = identifier,
                    ArchivePdfFile = g.Pdf,
                    ArchiveDjvuFile = g.Djvu,
                    ArchiveJp2ZipFile = g.Jp2,
                });
            }

            return items;
        }

        private static FileGroup GetOrAdd(Dictionary<string, FileGroup> groups, string key)
        {
            if (!groups.TryGetValue(key, out var g))
            {
                g = new FileGroup();
                groups[key] = g;
            }
            return g;
        }

        private class FileGroup
        {
            public string? Pdf; public long? PdfSize;
            public string? Djvu; public long? DjvuSize;
            public string? Jp2; public long? Jp2Size;
        }

        private static string GetGroupKey(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            foreach (var suffix in KnownSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return name[..^suffix.Length];
            }
            return name;
        }

        private static long? ReadSize(JsonElement file)
        {
            if (!file.TryGetProperty("size", out var s))
                return null;
            if (s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var num))
                return num;
            if (s.ValueKind == JsonValueKind.String && long.TryParse(s.GetString(), out var parsed))
                return parsed;
            return null;
        }

        public async Task<DownloadOutcome> DownloadAsync(MagazineItem item, string rootFolder, IProgress<double> progress, Action<string, LogLevel> log, CancellationToken ct)
        {
            if (item.ArchiveIdentifier is null)
                return DownloadOutcome.Failed;

            // Resumability: if the final PDF is already sitting in the download folder from a
            // previous run, don't redo the work (mirrors the reference script's "PDF already
            // exists, skipping" check).
            var finalPath = Path.Combine(rootFolder, item.SuggestedFileName + ".pdf");
            if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
            {
                log($"{item.Title}: PDF already exists in the download folder, skipping.", LogLevel.Info);
                return DownloadOutcome.Pdf(finalPath);
            }

            var sourceDir = Path.Combine(rootFolder, "source", FileNaming.Sanitize(item.SourceFolderKey));
            Directory.CreateDirectory(sourceDir);

            // Always fetch JP2 and DjVu together when present (matches the reference script's
            // two --glob filters). Only fetch the plain PDF when neither exists - it's solely a
            // last-resort copy, so there's no reason to pull it down redundantly otherwise.
            var toFetch = new List<(string name, string kind)>();
            if (item.ArchiveJp2ZipFile != null) toFetch.Add((item.ArchiveJp2ZipFile, "jp2"));
            if (item.ArchiveDjvuFile != null) toFetch.Add((item.ArchiveDjvuFile, "djvu"));
            if (item.ArchiveJp2ZipFile is null && item.ArchiveDjvuFile is null && item.ArchivePdfFile != null)
                toFetch.Add((item.ArchivePdfFile, "pdf"));

            if (toFetch.Count == 0)
                return DownloadOutcome.Failed;

            var localPaths = new Dictionary<string, string>();
            for (int i = 0; i < toFetch.Count; i++)
            {
                var (name, kind) = toFetch[i];

                // archive.org item files can carry nested path segments in their "name"
                // (e.g. "Click! - Skany/Click! (2006)/Click! 11.2006_jp2.zip"). Save locally
                // under the item's source folder using just the file's own name, so we don't try
                // to write into deep subfolders that were never created (the old code did - hence
                // "Could not find a part of the path"). The full nested name still builds the URL.
                var localFileName = FileNaming.Sanitize(name.Replace('\\', '/').Split('/')[^1]);
                var localPath = Path.Combine(sourceDir, localFileName);
                int capturedIndex = i;
                IProgress<double> sub = new Progress<double>(p => progress.Report((capturedIndex * 100.0 + p) / toFetch.Count));

                if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                {
                    log($"{item.Title}: {localFileName} already in source\\, skipping re-download.", LogLevel.Info);
                    localPaths[kind] = localPath;
                    sub.Report(100);
                    continue;
                }

                // Escape each path segment but keep the '/' separators - archive.org needs the
                // real nested path, not a single %2F-encoded blob.
                var fileUrl = $"https://archive.org/download/{item.ArchiveIdentifier}/{EncodeArchivePath(name)}";
                var ok = await DownloadFileAsync(fileUrl, localPath, sub, ct);
                if (ok)
                    localPaths[kind] = localPath;
                else
                    log($"{item.Title}: failed to download {localFileName}", LogLevel.Error);
            }

            // Priority for the "best" result that lands directly in the root folder: JP2 (best
            // quality, gets converted) > DjVu-only (left for manual conversion) > PDF-only (already usable).
            if (localPaths.TryGetValue("jp2", out var jp2Path))
            {
                log($"{item.Title}: JP2 source found, converting to PDF (best quality)...", LogLevel.Info);

                // No page-by-page feedback from the external converter, so switch the row bar
                // to a marquee for the duration of the conversion.
                item.Status = "Converting to PDF...";
                item.IsIndeterminate = true;
                try
                {
                    var converted = await Jp2PdfConverter.ConvertAsync(jp2Path, finalPath, (msg, lvl) => log($"{item.Title}: {msg}", lvl), ct);
                    if (!converted)
                    {
                        log($"{item.Title}: JP2 -> PDF conversion failed - raw files are still in source\\{item.SourceFolderKey}", LogLevel.Error);
                        return DownloadOutcome.Failed;
                    }
                }
                finally
                {
                    item.IsIndeterminate = false;
                }

                log($"{item.Title}: converted PDF saved to the download folder.", LogLevel.Success);
                return DownloadOutcome.Pdf(finalPath);
            }

            if (localPaths.TryGetValue("djvu", out var djvuPath))
            {
                // Try automatic DjVu -> PDF if DjVuLibre (ddjvu) is available; else keep the DjVu.
                if (await DjVuPdfConverter.IsAvailableAsync(ct))
                {
                    item.Status = "Converting to PDF...";
                    item.IsIndeterminate = true;
                    try
                    {
                        var ok = await DjVuPdfConverter.ConvertAsync(djvuPath, finalPath, (m, l) => log($"{item.Title}: {m}", l), ct);
                        if (ok)
                        {
                            log($"{item.Title}: converted DjVu -> PDF into the download folder.", LogLevel.Success);
                            return DownloadOutcome.Pdf(finalPath);
                        }
                    }
                    finally
                    {
                        item.IsIndeterminate = false;
                    }
                }

                log($"{item.Title}: no JP2 source - left the DjVu in source\\{item.SourceFolderKey}. Install DjVuLibre to auto-convert.", LogLevel.Warning);
                return DownloadOutcome.Djvu(djvuPath);
            }

            if (localPaths.TryGetValue("pdf", out var pdfPath))
            {
                File.Copy(pdfPath, finalPath, overwrite: true);
                log($"{item.Title}: no JP2 or DjVu source - this item was already a PDF, copied to the download folder.", LogLevel.Info);
                return DownloadOutcome.Pdf(finalPath);
            }

            return DownloadOutcome.Failed;
        }

        // Escapes each path segment of an archive.org file name while preserving the '/'
        // separators, so nested item files (foo/bar/baz_jp2.zip) resolve to a valid download
        // URL instead of collapsing every slash into %2F.
        private static string EncodeArchivePath(string name)
        {
            var segments = name.Replace('\\', '/').Split('/');
            for (int i = 0; i < segments.Length; i++)
                segments[i] = Uri.EscapeDataString(segments[i]);
            return string.Join('/', segments);
        }

        private async Task<bool> DownloadFileAsync(string url, string destinationPath, IProgress<double> progress, CancellationToken ct)
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return false;

            // Belt and suspenders: make sure the target directory exists before writing.
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

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

            return true;
        }

        private static string ExtractQuery(Uri sourceUri)
        {
            var qs = sourceUri.Query.TrimStart('?');
            if (string.IsNullOrEmpty(qs))
                return sourceUri.ToString();

            foreach (var pair in qs.Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "query")
                    return Uri.UnescapeDataString(kv[1].Replace('+', ' '));
            }

            return Uri.UnescapeDataString(qs);
        }
    }
}
