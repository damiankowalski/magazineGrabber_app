using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MagazineGrabber
{
    public class MagazineItem : INotifyPropertyChanged
    {
        public required string Title { get; set; }
        public required string Format { get; set; }            // display hint: "jp2" / "djvu" / "pdf"
        public long? SizeBytes { get; set; }
        public required string SourceUrl { get; set; }          // direct file URL (stare.e-gry.net) or item page (archive.org, informational)
        public required string SuggestedFileName { get; set; }  // final converted/plain PDF name, no extension
        public required string SourceFolderKey { get; set; }    // unique subfolder name under source\ for this row

        // archive.org-specific (null for other providers)
        public string? ArchiveIdentifier { get; set; }
        public string? ArchivePdfFile { get; set; }
        public string? ArchiveDjvuFile { get; set; }
        public string? ArchiveJp2ZipFile { get; set; }

        public string SizeDisplay => SizeBytes is null ? "?" : FormatBytes(SizeBytes.Value);

        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        private string _status = "Pending";
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        // 0..100 for the per-row progress bar in the grid. Providers report into this via the
        // IProgress<double> handed to DownloadAsync.
        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        // True for phases where a percentage isn't known (e.g. JP2 -> PDF conversion runs an
        // external tool with no page-by-page feedback), so the row bar shows a marquee instead.
        private bool _isIndeterminate;
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set { _isIndeterminate = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:0.#} {units[unitIndex]}";
        }
    }
}
