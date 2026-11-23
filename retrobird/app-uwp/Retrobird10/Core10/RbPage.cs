// The base of every Retrobird page on Windows 10. It offers the pages the same calls as the
// Windows Phone 8.1 base (..\..\app\Retrobird\RbPage.cs), so most page code is shared: navigation
// by page name with a query string, Back, and the busy line. What differs:
// - pages open in the shell's content frame (Shell.xaml), or in the inbox's right-hand pane when
//   the window is wide enough for list and message side by side;
// - the page title goes to the shell's top bar (a page's TitleText element names it);
// - each page has its own CommandBar ("Bar"), at the bottom of a phone screen and at the top of a
//   wider window, as the Windows 10 guidelines place it.
using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public class RbPage : Page
    {
        /// <summary>Window width from which the command bar moves to the top.</summary>
        public const double WideBar = 720;

        /// <summary>The query string this page was opened with ("uid=12&amp;f=S").</summary>
        protected Dictionary<string, string> Query = new Dictionary<string, string>();

        public RbPage()
        {
            Loaded += (s, e) => PlaceBar();
            SizeChanged += (s, e) => PlaceBar();
        }

        /// <summary>True while the page sits in the inbox's right-hand pane.</summary>
        public bool InDetails { get { return Frame != null && (Frame.Tag as string) == "details"; } }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Core.InAppHelper.Log("showing " + GetType().Name + " (" + e.NavigationMode + ")");
            Query = Parse(e.Parameter as string);
            if (!InDetails) WatchTitle();
            if (Shell.Current != null) Shell.Current.UpdateBack();
        }

        long _titleToken = -1;

        /// <summary>The top bar shows the page's TitleText, now and whenever the page changes it.</summary>
        void WatchTitle()
        {
            var t = FindName("TitleText") as TextBlock;
            if (Shell.Current == null) return;
            Shell.Current.SetTitle(t == null ? "Retrobird" : t.Text);
            if (t != null && _titleToken < 0)
                _titleToken = t.RegisterPropertyChangedCallback(TextBlock.TextProperty, (s, dp) =>
                {
                    if (Frame != null && Frame.Content == this && !InDetails) Shell.Current.SetTitle(t.Text);
                });
        }

        /// <summary>The page's command bar: bottom row on a phone, top row from 720 px. Pages lay
        /// out a root Grid of three rows (Auto, *, Auto) with the bar in the last.</summary>
        void PlaceBar()
        {
            var bar = FindName("Bar") as CommandBar;
            if (bar == null || !(bar.Parent is Grid)) return;
            var rows = ((Grid)bar.Parent).RowDefinitions.Count;
            if (rows < 3) return;
            bool top = Window.Current.Bounds.Width >= WideBar;
            Grid.SetRow(bar, top ? 0 : rows - 1);
            // no commands: no empty bar
            bar.Visibility = bar.PrimaryCommands.Count + bar.SecondaryCommands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

        public static Type PageType(string name)
        {
            switch (name)
            {
                case "MainPage": return typeof(InboxPage);
                case "InboxPage": return typeof(InboxPage);
                case "MessagePage": return typeof(MessagePage);
                case "ComposePage": return typeof(ComposePage);
                case "SettingsPage": return typeof(SettingsPage);
                case "AccountEditPage": return typeof(AccountEditPage);
                case "ProviderPage": return typeof(ProviderPage);
                case "LicensePage": return typeof(LicensePage);
            }
            throw new ArgumentException("no page " + name);
        }

        /// <summary>Opens "/MessagePage.xaml?uid=12". A message opened from the inbox goes to its
        /// right-hand pane when the window is wide; everything else opens full size.</summary>
        protected void Go(string target)
        {
            var q = target.IndexOf('?');
            var path = q >= 0 ? target.Substring(0, q) : target;
            var name = path.TrimStart('/');
            if (name.EndsWith(".xaml")) name = name.Substring(0, name.Length - 5);
            var arg = q >= 0 ? target.Substring(q + 1) : null;
            try
            {
                Core.InAppHelper.Log("opening " + target);
                var type = PageType(name);
                var inbox = this as InboxPage;
                if (type == typeof(MessagePage) && inbox != null && inbox.ShowsDetails)
                {
                    inbox.OpenDetails(arg);
                    return;
                }
                var frame = Shell.Current != null ? Shell.Current.ContentFrame : Frame;
                if (!frame.Navigate(type, arg, new DrillInNavigationTransitionInfo()))
                {
                    var unused = Ui.AlertAsync("That page couldn't open.", "Retrobird");
                }
            }
            catch (Exception e)
            {
                Core.InAppHelper.Log("navigation to " + target + " failed: " + e);
                var unused = Ui.AlertAsync("That page couldn't open. " + e.Message, "Retrobird");
            }
        }

        protected bool CanGoBack { get { return InDetails || (Frame != null && Frame.CanGoBack); } }

        /// <summary>Back a page; a message in the right-hand pane closes instead.</summary>
        protected void GoBack()
        {
            if (InDetails)
            {
                if (InboxPage.Current != null) InboxPage.Current.CloseDetails();
                return;
            }
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
        }

        /// <summary>True when the page took Back itself.</summary>
        public virtual bool HandleBack() { return false; }

        /// <summary>The busy line under the top bar, with a short text, or nothing (null).</summary>
        protected void Busy(string text)
        {
            if (Shell.Current != null) Shell.Current.Busy(text == null ? null : Core.Format.Sentence(text));
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Busy(null);
        }
    }
}
