using System;

namespace MagazineGrabber
{
    public enum LogLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    public class LogEntry
    {
        public required string Message { get; init; }
        public LogLevel Level { get; init; } = LogLevel.Info;
        public DateTime Timestamp { get; init; } = DateTime.Now;

        // Section headers ("=== Downloading ===") are rendered bold and without a timestamp so
        // the log reads as distinct stages (listing / downloading / summary / results).
        public bool IsSection { get; init; }

        // When set, this log line points at a produced file. The UI renders it like a link and
        // opens the file on double-click (used for the end-of-batch PDF/DjVu results list).
        public string? FilePath { get; init; }
        public bool IsLink => FilePath is not null;

        public string Display => IsSection ? Message : $"{Timestamp:HH:mm:ss}  {Message}";
    }
}
