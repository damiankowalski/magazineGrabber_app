using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace MagazineGrabber
{
    /// <summary>
    /// Saves and restores a site's login *session cookies* between runs, encrypted with the
    /// Windows Data Protection API (DPAPI) scoped to the current user.
    ///
    /// We deliberately persist the session, not the username/password: the encrypted blob is
    /// useless on another machine or under another Windows account, and a stored session can't
    /// be turned back into your password. Combined with the WebView2 profile (which keeps you
    /// logged in inside the embedded browser), this means you log in once and stay logged in
    /// until the site's session actually expires - at which point one click re-establishes it.
    /// </summary>
    public static class SessionStore
    {
        private static string FileFor(string key) =>
            Path.Combine(AppPaths.DataFolder, $"session-{SanitizeKey(key)}.bin");

        public static void Save(string key, CookieCollection cookies)
        {
            try
            {
                var dto = cookies
                    .Cast<Cookie>()
                    .Where(c => !c.Expired && !string.IsNullOrEmpty(c.Value))
                    .Select(c => new CookieDto(c.Name, c.Value, c.Domain, c.Path))
                    .ToList();

                if (dto.Count == 0)
                    return;

                var json = JsonSerializer.SerializeToUtf8Bytes(dto);
                var encrypted = ProtectedData.Protect(json, optionalEntropy: null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(FileFor(key), encrypted);
            }
            catch
            {
                // Persistence is best-effort - never let it break a download.
            }
        }

        public static CookieCollection? Load(string key)
        {
            try
            {
                var path = FileFor(key);
                if (!File.Exists(path))
                    return null;

                var encrypted = File.ReadAllBytes(path);
                var json = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
                var dto = JsonSerializer.Deserialize<List<CookieDto>>(json);
                if (dto is null || dto.Count == 0)
                    return null;

                var jar = new CookieCollection();
                foreach (var d in dto)
                {
                    try
                    {
                        var cookie = new Cookie(d.Name, d.Value)
                        {
                            Path = string.IsNullOrWhiteSpace(d.Path) ? "/" : d.Path
                        };
                        if (!string.IsNullOrWhiteSpace(d.Domain))
                            cookie.Domain = d.Domain;
                        jar.Add(cookie);
                    }
                    catch { /* skip a cookie that won't reconstruct */ }
                }
                return jar.Count > 0 ? jar : null;
            }
            catch
            {
                return null; // corrupt/undecryptable (e.g. copied from another machine) -> ignore
            }
        }

        public static void Clear(string key)
        {
            try
            {
                var path = FileFor(key);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { /* ignore */ }
        }

        private static string SanitizeKey(string s) =>
            new string(s.Where(char.IsLetterOrDigit).ToArray());

        private record CookieDto(string Name, string Value, string Domain, string Path);
    }
}
