using System;
using System.IO;

namespace MagazineGrabber
{
    /// <summary>
    /// Where the app keeps its persistent data (the encrypted saved session and the WebView2
    /// profile). Prefers a folder next to the EXE so the app stays portable; falls back to
    /// %LOCALAPPDATA% if that location isn't writable (e.g. installed under Program Files).
    /// </summary>
    public static class AppPaths
    {
        public static string DataFolder { get; } = ResolveDataFolder();

        private static string ResolveDataFolder()
        {
            try
            {
                var beside = Path.Combine(AppContext.BaseDirectory, "MagazineGrabber-data");
                Directory.CreateDirectory(beside);

                // Prove it's actually writable before committing to it.
                var probe = Path.Combine(beside, ".writable");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return beside;
            }
            catch
            {
                var appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MagazineGrabber");
                Directory.CreateDirectory(appData);
                return appData;
            }
        }
    }
}
