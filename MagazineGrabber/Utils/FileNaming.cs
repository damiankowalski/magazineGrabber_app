using System.IO;

namespace MagazineGrabber
{
    public static class FileNaming
    {
        public static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
