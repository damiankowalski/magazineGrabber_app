using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MagazineGrabber
{
    public partial class LoginWebViewDialog : Window
    {
        public List<Cookie>? HarvestedCookies { get; private set; }

        private readonly Uri _loginUrl;

        // One shared WebView2 environment pointed at a persistent profile folder. Because the
        // profile persists, once you log in the embedded browser stays logged in across dialog
        // opens and app restarts - so re-authenticating is at most a single "I'm logged in"
        // click, never re-typing your password.
        private static CoreWebView2Environment? _sharedEnv;

        public LoginWebViewDialog(Uri loginUrl)
        {
            InitializeComponent();
            _loginUrl = loginUrl;
            Loaded += LoginWebViewDialog_Loaded;
        }

        private static async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (_sharedEnv is not null)
                return _sharedEnv;

            var profileFolder = Path.Combine(AppPaths.DataFolder, "webview2");
            Directory.CreateDirectory(profileFolder);
            _sharedEnv = await CoreWebView2Environment.CreateAsync(userDataFolder: profileFolder);
            return _sharedEnv;
        }

        private async void LoginWebViewDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var env = await GetEnvironmentAsync();
                await Browser.EnsureCoreWebView2Async(env);
                Browser.CoreWebView2.Navigate(_loginUrl.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Couldn't start the embedded browser (WebView2 Runtime may be missing - " +
                    "it's normally bundled with Windows/Edge already).\n\n" + ex.Message,
                    "Login window", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (Browser.CoreWebView2 is null)
            {
                DialogResult = false;
                return;
            }

            var origin = $"{_loginUrl.Scheme}://{_loginUrl.Host}";
            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(origin);

            HarvestedCookies = new List<Cookie>();
            foreach (var c in cookies)
            {
                // Preserve the browser's own Domain/Path scoping - it matches what the server
                // set, so later Set-Cookie responses update the same entry instead of piling up
                // a duplicate (which is what broke the 2nd+ download until an app restart).
                try
                {
                    var cookie = new Cookie(c.Name, c.Value);
                    if (!string.IsNullOrWhiteSpace(c.Domain)) cookie.Domain = c.Domain;
                    cookie.Path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path;
                    HarvestedCookies.Add(cookie);
                }
                catch
                {
                    // Fall back to a bare name/value cookie if the reported domain/path won't validate.
                    try { HarvestedCookies.Add(new Cookie(c.Name, c.Value)); }
                    catch { /* skip anything that won't construct as a cookie */ }
                }
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
