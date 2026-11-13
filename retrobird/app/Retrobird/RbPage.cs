// The base of every Retrobird page: navigation by page name with a query string (as the pages
// were written for Silverlight), the hardware back key, and the progress bar in the status bar.
using System;
using System.Collections.Generic;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public class RbPage : Page
    {
        /// <summary>The query string this page was opened with ("uid=12&amp;f=S").</summary>
        protected Dictionary<string, string> Query = new Dictionary<string, string>();

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Query = Parse(e.Parameter as string);
        }

        static Dictionary<string, string> Parse(string q)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(q)) return d;
            foreach (var part in q.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq < 0) d[Uri.UnescapeDataString(part)] = "";
                else d[Uri.UnescapeDataString(part.Substring(0, eq))] = Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return d;
        }

        static Type PageType(string name)
        {
            switch (name)
            {
                case "MainPage": return typeof(MainPage);
                case "MessagePage": return typeof(MessagePage);
                case "ComposePage": return typeof(ComposePage);
                case "FoldersPage": return typeof(FoldersPage);
                case "AccountsPage": return typeof(AccountsPage);
                case "AccountEditPage": return typeof(AccountEditPage);
                case "ProviderPage": return typeof(ProviderPage);
                case "LicensePage": return typeof(LicensePage);
            }
            throw new ArgumentException("no page " + name);
        }

        /// <summary>Opens "/MessagePage.xaml?uid=12".</summary>
        protected void Go(string target)
        {
            var q = target.IndexOf('?');
            var path = q >= 0 ? target.Substring(0, q) : target;
            var name = path.TrimStart('/');
            if (name.EndsWith(".xaml")) name = name.Substring(0, name.Length - 5);
            Frame.Navigate(PageType(name), q >= 0 ? target.Substring(q + 1) : null);
        }

        protected bool CanGoBack { get { return Frame != null && Frame.CanGoBack; } }

        protected void GoBack()
        {
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
        }

        /// <summary>True when the page took the back key itself.</summary>
        public virtual bool HandleBack() { return false; }

        /// <summary>The progress bar in the status bar, with a short text, or nothing (null).</summary>
        protected async void Busy(string text)
        {
            try
            {
                var pi = StatusBar.GetForCurrentView().ProgressIndicator;
                if (text == null)
                {
                    await pi.HideAsync();
                    return;
                }
                pi.Text = text;
                pi.ProgressValue = null;   // indeterminate
                await pi.ShowAsync();
            }
            catch (Exception) { }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Busy(null);
        }
    }
}
