using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MagazineGrabber
{
    public enum DownloadResult
    {
        Success,
        AuthRequired,
        Failed
    }

    /// <summary>What actually landed on disk for a successfully processed item.</summary>
    public enum OutputKind
    {
        None,
        Pdf,    // a final PDF is sitting in the download folder (converted, copied, or already there)
        Djvu    // raw DjVu saved to source\ - still needs manual conversion
    }

    /// <summary>
    /// Result of DownloadAsync. Result drives the retry/summary logic; Output + OutputPath let
    /// the manager tally "PDFs generated" vs "DjVu left for manual conversion" and list the
    /// concrete files produced at the end of a batch.
    /// </summary>
    public sealed record DownloadOutcome(DownloadResult Result, OutputKind Output = OutputKind.None, string? OutputPath = null)
    {
        public static readonly DownloadOutcome AuthRequired = new(DownloadResult.AuthRequired);
        public static readonly DownloadOutcome Failed = new(DownloadResult.Failed);
        public static DownloadOutcome Pdf(string path) => new(DownloadResult.Success, OutputKind.Pdf, path);
        public static DownloadOutcome Djvu(string path) => new(DownloadResult.Success, OutputKind.Djvu, path);
    }

    /// <summary>
    /// One implementation per source site. To support a new site, implement this
    /// interface and add an instance in MainWindow's provider list.
    /// </summary>
    public interface IMagazineProvider
    {
        string Name { get; }

        /// <summary>
        /// Upper bound on how many of this provider's items should download at once, regardless
        /// of the UI setting. archive.org tolerates a few parallel streams; stare.e-gry.net is
        /// session/login-based and is safest one-at-a-time, so it caps this at 1.
        /// </summary>
        int MaxParallelDownloads { get; }

        bool CanHandle(Uri sourceUri);

        /// <summary>
        /// Enumerates downloadable items for a listing/search/item/collection URL. Optionally
        /// reports human-readable progress lines (e.g. "reading metadata 12/50") the UI can
        /// surface while a long listing is in flight.
        /// </summary>
        Task<List<MagazineItem>> ListItemsAsync(Uri sourceUri, IProgress<string>? status, CancellationToken ct);

        /// <summary>
        /// Downloads (and, where relevant, converts) everything for one item. rootFolder is
        /// the folder the user chose in the UI - the provider organizes its own layout under
        /// it (e.g. rootFolder/source/&lt;SourceFolderKey&gt;/ for raw files, with the best
        /// available result copied/converted directly into rootFolder).
        /// Returns AuthRequired instead of throwing when a logged-in session turns out to be needed.
        /// </summary>
        Task<DownloadOutcome> DownloadAsync(MagazineItem item, string rootFolder, IProgress<double> progress, Action<string, LogLevel> log, CancellationToken ct);

        /// <summary>URL to open in an embedded browser for interactive login, or null if this provider never needs it.</summary>
        Uri? LoginUrl { get; }

        /// <summary>Called with the cookies harvested after the user finishes logging in interactively.</summary>
        void ApplyLoginCookies(IEnumerable<Cookie> cookies);
    }
}
