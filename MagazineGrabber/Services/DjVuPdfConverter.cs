using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MagazineGrabber
{
    /// <summary>
    /// Optional DjVu -> PDF conversion via DjVuLibre's <c>ddjvu</c> command-line tool.
    ///
    /// This is a soft dependency, mirroring the Python/img2pdf path used for JP2: if
    /// <c>ddjvu</c> isn't on PATH (it is NOT present on a vanilla Windows install), conversion
    /// is skipped and the raw .djvu file is left in place for manual conversion - the app keeps
    /// working, it just doesn't produce a PDF for that item.
    ///
    /// We deliberately do NOT bundle DjVuLibre: it's GPL-licensed, so shipping its binaries
    /// would impose GPL terms on this project. Users who want automatic DjVu -> PDF install
    /// DjVuLibre themselves (see README).
    /// </summary>
    public static class DjVuPdfConverter
    {
        private static bool? _available;

        /// <summary>Cached check for whether ddjvu can be invoked from PATH.</summary>
        public static async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            if (_available.HasValue)
                return _available.Value;

            _available = await IsOnPathAsync("ddjvu", ct);
            return _available.Value;
        }

        /// <summary>
        /// Converts a single .djvu file to a PDF at <paramref name="outputPdfPath"/>.
        /// Returns false (without throwing) when ddjvu is unavailable or the conversion fails,
        /// so callers can fall back to keeping the raw DjVu.
        /// </summary>
        public static async Task<bool> ConvertAsync(string djvuPath, string outputPdfPath, Action<string, LogLevel> log, CancellationToken ct)
        {
            if (!await IsAvailableAsync(ct))
            {
                log("DjVuLibre (ddjvu) not found on PATH - keeping the DjVu file. Install DjVuLibre to enable automatic DjVu -> PDF.", LogLevel.Warning);
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo("ddjvu")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                // -format=pdf: render every page straight into a single PDF (DjVuLibre >= 3.5.22).
                psi.ArgumentList.Add("-format=pdf");
                psi.ArgumentList.Add("-quality=85");
                psi.ArgumentList.Add(djvuPath);
                psi.ArgumentList.Add(outputPdfPath);

                using var process = Process.Start(psi);
                if (process is null)
                {
                    log("could not start ddjvu.", LogLevel.Error);
                    return false;
                }

                string stderr = await process.StandardError.ReadToEndAsync(ct);
                string stdout = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                // Trust the output file: some ddjvu builds print progress/notes to stderr even on success.
                if (File.Exists(outputPdfPath) && new FileInfo(outputPdfPath).Length > 0)
                    return true;

                log($"ddjvu conversion failed: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}", LogLevel.Error);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"ddjvu conversion error: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private static async Task<bool> IsOnPathAsync(string exeName, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo(exeName, "--help")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return false;
                await p.WaitForExitAsync(ct);
                return true; // it launched, so it's on PATH (ddjvu --help exits non-zero but that's fine)
            }
            catch
            {
                return false; // executable not found on PATH
            }
        }
    }
}
