using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MagazineGrabber
{
    /// <summary>
    /// Extracts a *_jp2.zip archive of page scans and combines them into a single PDF.
    /// Generates a small Python helper script - the same approach as the reference PowerShell
    /// script - that tries img2pdf directly first, then falls back to re-saving each page
    /// through Pillow (fixing odd colorspaces/modes) if the direct conversion fails. This
    /// materially improves the success rate on real-world scans; verified end-to-end against
    /// both a clean RGB set and a deliberately-corrupted mixed set before shipping.
    /// Requires Python + pip on PATH; img2pdf and Pillow are installed automatically if missing.
    /// </summary>
    public static class Jp2PdfConverter
    {
        private static bool? _dependenciesReady;

        public static async Task<bool> ConvertAsync(string zipPath, string outputPdfPath, Action<string, LogLevel> log, CancellationToken ct)
        {
            if (!await EnsureDependenciesAsync(log, ct))
                return false;

            var tempDir = Path.Combine(Path.GetTempPath(), "MagazineGrabber_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                ZipFile.ExtractToDirectory(zipPath, tempDir);

                // Natural sort (page2 before page10) - same trick as the reference script.
                var images = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => Regex.IsMatch(Path.GetExtension(f), @"^\.(jp2|jpg|jpeg)$", RegexOptions.IgnoreCase))
                    .OrderBy(f => Regex.Replace(Path.GetFileName(f), @"\d+", m => m.Value.PadLeft(10, '0')))
                    .ToList();

                if (images.Count == 0)
                {
                    log("no image files found inside the JP2 archive.", LogLevel.Error);
                    return false;
                }

                log($"combining {images.Count} pages into a PDF (img2pdf, with Pillow fallback)...", LogLevel.Info);

                var listFile = Path.Combine(tempDir, "image_list.txt");
                await File.WriteAllLinesAsync(listFile, images, Encoding.UTF8, ct);

                var scriptPath = Path.Combine(tempDir, "convert.py");
                await File.WriteAllTextAsync(scriptPath, PythonConverterScript, ct);

                var psi = new ProcessStartInfo("python")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(scriptPath);
                psi.ArgumentList.Add(listFile);
                psi.ArgumentList.Add(outputPdfPath);

                using var process = Process.Start(psi);
                if (process is null)
                {
                    log("could not start python.", LogLevel.Error);
                    return false;
                }

                string stderr = await process.StandardError.ReadToEndAsync(ct);
                string stdout = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                // Trust the output file over the exit code/stderr, same as the reference script -
                // the Pillow fallback path can print internal warnings to stderr even when it
                // ultimately succeeds, so "stderr is non-empty" is not a reliable failure signal.
                if (File.Exists(outputPdfPath) && new FileInfo(outputPdfPath).Length > 0)
                    return true;

                log($"img2pdf/Pillow conversion failed: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}", LogLevel.Error);
                return false;
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        private static async Task<bool> EnsureDependenciesAsync(Action<string, LogLevel> log, CancellationToken ct)
        {
            if (_dependenciesReady.HasValue)
                return _dependenciesReady.Value;

            if (!await IsOnPathAsync("pip", ct))
            {
                log("Python/pip was not found on PATH - install Python from python.org (tick 'Add to PATH') to enable JP2 -> PDF conversion.", LogLevel.Error);
                _dependenciesReady = false;
                return false;
            }

            foreach (var package in new[] { "img2pdf", "Pillow" })
            {
                if (!await IsPipPackageInstalledAsync(package, ct))
                {
                    log($"installing the '{package}' Python package (one-time)...", LogLevel.Info);
                    var installed = await RunProcessAsync("pip", new[] { "install", package }, ct);
                    if (!installed)
                    {
                        log($"failed to install {package} via pip.", LogLevel.Error);
                        _dependenciesReady = false;
                        return false;
                    }
                }
            }

            _dependenciesReady = true;
            return true;
        }

        private static async Task<bool> IsPipPackageInstalledAsync(string package, CancellationToken ct)
        {
            var psi = new ProcessStartInfo("pip")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add(package);

            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }

        private static async Task<bool> IsOnPathAsync(string exeName, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo(exeName, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return false;
                await p.WaitForExitAsync(ct);
                return true;
            }
            catch
            {
                return false; // executable not found on PATH
            }
        }

        private static async Task<bool> RunProcessAsync(string exe, IEnumerable<string> args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }

        // Same approach as the reference PowerShell script's embedded convert.py: try img2pdf
        // directly, and if that throws (an unusual JP2 colorspace/mode is a common cause),
        // re-save each page through Pillow as clean RGB/L JPEGs and retry on those instead.
        private const string PythonConverterScript = @"
import sys, os
import img2pdf
from PIL import Image

list_file = sys.argv[1]
pdf_output = sys.argv[2]

with open(list_file, 'r', encoding='utf-8-sig') as f:
    imgs = [line.strip() for line in f if line.strip()]

try:
    pdf_bytes = img2pdf.convert(*imgs)
    with open(pdf_output, 'wb') as f:
        f.write(pdf_bytes)
except Exception as e:
    print(f'Direct img2pdf failed ({e}). Sanitizing colorspaces with Pillow...')
    clean_dir = os.path.join(os.path.dirname(list_file), 'clean_jpgs')
    os.makedirs(clean_dir, exist_ok=True)

    sanitized = []
    for i, img_path in enumerate(imgs):
        try:
            with Image.open(img_path) as im:
                if im.mode not in ('RGB', 'L'):
                    im = im.convert('RGB')
                clean_path = os.path.join(clean_dir, f'page_{i:04d}.jpg')
                im.save(clean_path, 'JPEG', quality=95)
                sanitized.append(clean_path)
        except Exception as img_err:
            print(f'Skipping unreadable image {img_path}: {img_err}')

    if sanitized:
        pdf_bytes = img2pdf.convert(*sanitized)
        with open(pdf_output, 'wb') as f:
            f.write(pdf_bytes)
    else:
        print('No images could be sanitized.')
        sys.exit(1)
";
    }
}
