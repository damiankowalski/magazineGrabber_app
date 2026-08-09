using System;
using System.Collections.Generic;
using System.Net;
using System.Windows;

namespace MagazineGrabber
{
    public partial class LoginWebViewDialog : Window
    {
        public List<Cookie>? HarvestedCookies { get; private set; }

        private readonly Uri _loginUrl;

        public LoginWebViewDialog(Uri loginUrl)
        {
            InitializeComponent();
            _loginUrl = loginUrl;
            Loaded += LoginWebViewDialog_Loaded;
        }

        private async void LoginWebViewDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await Browser.EnsureCoreWebView2Async();
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
                // Keep only name/value here - the System.Net.Cookie 4-arg ctor throws on some
                // real-world domain/path values, and the provider re-scopes these to the right
                // host anyway (see StareEGryProvider.ApplyLoginCookies).
                try { HarvestedCookies.Add(new Cookie(c.Name, c.Value)); }
                catch { /* skip anything that won't construct as a cookie */ }
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
