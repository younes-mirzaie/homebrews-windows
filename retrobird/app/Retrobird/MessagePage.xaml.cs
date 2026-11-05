// One message: headers, attachments, and the body in a script-free WebBrowser. Remote pictures
// stay blocked until asked for. Reply, reply all, forward, delete, move, flag.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Microsoft.Phone.Tasks;
using Retrobird.Core;
using Windows.Storage;

namespace Retrobird
{
    public partial class MessagePage : PhoneApplicationPage
    {
        uint _uid;
        MessageView _view;
        bool _plain, _images, _loaded, _flagged;
        string _pendingHtml;
        ApplicationBarMenuItem _imagesItem, _plainItem, _flagItem;

        public MessagePage()
        {
            InitializeComponent();
            ApplicationBar = new ApplicationBar();
            Ui.Button(ApplicationBar, "reply", "reply", (s, e) => Reply(false));
            Ui.Button(ApplicationBar, "replyall", "reply all", (s, e) => Reply(true));
            Ui.Button(ApplicationBar, "forward", "forward", (s, e) => Forward());
            Ui.Button(ApplicationBar, "delete", "delete", (s, e) => Delete());
            Ui.Menu(ApplicationBar, "mark as unread", (s, e) => MarkUnread());
            _flagItem = Ui.Menu(ApplicationBar, "flag", (s, e) => ToggleFlag());
            Ui.Menu(ApplicationBar, "move to...", (s, e) => NavigationService.Navigate(new Uri("/FoldersPage.xaml?pick=1", UriKind.Relative)));
            _plainItem = Ui.Menu(ApplicationBar, "show as plain text", (s, e) => { _plain = !_plain; Render(); });
            _imagesItem = Ui.Menu(ApplicationBar, "show pictures", (s, e) => LoadImages());
        }

        static string Acct { get { return AppState.CurrentAccountId; } }
        static string Folder { get { return AppState.CurrentFolder; } }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (AppState.PickedFolder != null)
            {
                var dest = AppState.PickedFolder;
                AppState.PickedFolder = null;
                await MoveTo(dest);
                return;
            }
            if (_view != null) return;
            string uid;
            if (!NavigationContext.QueryString.TryGetValue("uid", out uid) || !uint.TryParse(uid, out _uid)) return;
            string flags;
            if (NavigationContext.QueryString.TryGetValue("f", out flags)) _flagged = flags.IndexOf('F') >= 0;
            _flagItem.Text = _flagged ? "unflag" : "flag";
            _images = AppState.LoadImages;
            await Load();
        }

        async System.Threading.Tasks.Task Load()
        {
            Ui.Busy(this, "opening");
            try
            {
                _view = await AppState.Client.GetMessageAsync(Acct, Folder, _uid, _images);
                AppState.ListDirty = true;   // it's read now
            }
            catch (Exception ex)
            {
                ErrorText.Text = Ui.Describe(ex);
                ErrorText.Visibility = Visibility.Visible;
                Ui.Busy(this, null);
                return;
            }
            Ui.Busy(this, null);
            SubjectText.Text = string.IsNullOrEmpty(_view.Subject) ? "(no subject)" : _view.Subject;
            FromText.Text = Address.NameOf(_view.From ?? "");
            DateText.Text = Format.ShortDate(_view.Date);
            var d = new StringBuilder();
            d.Append("from: " + _view.From);
            if (!string.IsNullOrEmpty(_view.To)) d.Append("\nto: " + _view.To);
            if (!string.IsNullOrEmpty(_view.Cc)) d.Append("\ncc: " + _view.Cc);
            if (!string.IsNullOrEmpty(_view.ReplyTo)) d.Append("\nreply to: " + _view.ReplyTo);
            d.Append("\n" + Format.LongDate(_view.Date));
            DetailsText.Text = d.ToString();
            var atts = _view.Attachments.Where(a => !a.Inline).ToList();
            Attachments.ItemsSource = atts;
            AttachmentsScroll.Visibility = atts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ImagesButton.Visibility = _view.Blocked ? Visibility.Visible : Visibility.Collapsed;
            _imagesItem.IsEnabled = _view.Blocked;
            _plainItem.IsEnabled = !string.IsNullOrEmpty(_view.Text);
            Render();
        }

        // ---------------- body ----------------

        static bool DarkTheme
        {
            get { return (Visibility)Application.Current.Resources["PhoneDarkThemeVisibility"] == Visibility.Visible; }
        }

        static string Css(Color c) { return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }

        /// <summary>WebBrowser.NavigateToString garbles non-ASCII text, so everything above 127
        /// goes in as a numeric character reference.</summary>
        static string ToEntities(string s)
        {
            var b = new StringBuilder(s.Length + 64);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 128) { b.Append(c); continue; }
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    cp = char.ConvertToUtf32(c, s[++i]);
                b.Append("&#").Append(cp.ToString(CultureInfo.InvariantCulture)).Append(';');
            }
            return b.ToString();
        }

        static string HtmlEscape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        void Render()
        {
            if (_view == null) return;
            string content = _view.Html ?? "";
            bool plainMail = content.StartsWith("<div class=\"rb-plain\">");
            if (_plain && !string.IsNullOrEmpty(_view.Text))
            {
                content = "<div class=\"rb-plain\">" + HtmlEscape(_view.Text) + "</div>";
                plainMail = true;
            }
            _plainItem.Text = _plain ? "show formatted" : "show as plain text";
            string bg = "#ffffff", fg = "#000000";
            if (plainMail && DarkTheme) { bg = "#000000"; fg = "#ffffff"; }
            string accent = Css((Color)Application.Current.Resources["PhoneAccentColor"]);
            var page = "<!DOCTYPE html><html><head><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>" +
                       "html,body{margin:0;padding:0}" +
                       "body{font-family:'Segoe WP','Segoe UI',sans-serif;font-size:15px;line-height:1.35;word-wrap:break-word;" +
                       "padding:8px 12px;background:" + bg + ";color:" + fg + "}" +
                       "img{max-width:100%;height:auto}table{max-width:100%}" +
                       ".rb-plain{white-space:pre-wrap;font-family:inherit}" +
                       "a{color:" + (plainMail ? accent : "#0645ad") + "}" +
                       "blockquote{margin:0 0 0 6px;padding-left:8px;border-left:2px solid #888}" +
                       "</style></head><body>" + content + "</body></html>";
            Show(ToEntities(page));
        }

        void Show(string html)
        {
            if (!_loaded) { _pendingHtml = html; return; }
            Body.Opacity = 0;
            Body.NavigateToString(html);
        }

        void Body_Loaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            if (_pendingHtml != null)
            {
                var h = _pendingHtml;
                _pendingHtml = null;
                Show(h);
            }
        }

        void Body_LoadCompleted(object sender, NavigationEventArgs e) { Body.Opacity = 1; }

        /// <summary>Links open outside: web pages in the browser, mailto: in a new message.</summary>
        void Body_Navigating(object sender, NavigatingEventArgs e)
        {
            var u = e.Uri;
            if (u == null || !u.IsAbsoluteUri) return;
            if (u.Scheme == "about") return;
            e.Cancel = true;
            if (u.Scheme == "mailto")
            {
                var to = Uri.UnescapeDataString(u.OriginalString.Substring(7));
                var q = to.IndexOf('?');
                AppState.PendingDraft = new Draft { To = q >= 0 ? to.Substring(0, q) : to };
                NavigationService.Navigate(new Uri("/ComposePage.xaml", UriKind.Relative));
                return;
            }
            if (u.Scheme == "http" || u.Scheme == "https")
                new WebBrowserTask { Uri = u }.Show();
        }

        async void LoadImages()
        {
            _images = true;
            ImagesButton.Visibility = Visibility.Collapsed;
            _imagesItem.IsEnabled = false;
            _view = null;
            await Load();
        }

        void ImagesButton_Click(object sender, RoutedEventArgs e) { LoadImages(); }

        void Header_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            DetailsText.Visibility = DetailsText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------------- attachments ----------------

        async void Attachment_Click(object sender, RoutedEventArgs e)
        {
            var a = (sender as Button).Tag as Attachment;
            if (a == null) return;
            string path;
            Ui.Busy(this, "getting " + a.Name);
            try
            {
                var dir = ApplicationData.Current.LocalFolder.Path + "\\Attachments";
                path = await AppState.Client.SaveAttachmentAsync(Acct, Folder, _uid, a.Index, dir);
                var file = await StorageFile.GetFileFromPathAsync(path);
                Ui.Busy(this, null);
                if (await Windows.System.Launcher.LaunchFileAsync(file)) return;
            }
            catch (Exception ex)
            {
                Ui.Busy(this, null);
                Ui.Error(ex);
                return;
            }
            // nothing on the phone opens it: offer the Documents folder instead
            if (Ui.Confirm("No app on this phone opens " + a.Name + ". Save it to Documents\\Retrobird instead?", "attachment"))
            {
                try
                {
                    var saved = await AppState.Client.SaveAttachmentAsync(Acct, Folder, _uid, a.Index, @"C:\Data\Users\Public\Documents\Retrobird");
                    MessageBox.Show("Saved as " + saved, "attachment", MessageBoxButton.OK);
                }
                catch (Exception ex) { Ui.Error(ex); }
            }
        }

        // ---------------- actions ----------------

        static string Prefixed(string subject, string prefix)
        {
            subject = subject ?? "";
            var s = subject.TrimStart();
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return subject;
            if (prefix == "Re:" && (s.StartsWith("AW:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("SV:", StringComparison.OrdinalIgnoreCase)))
                return subject;
            return prefix + " " + subject;
        }

        static string Quote(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Select(l => ">" + (l.StartsWith(">") ? "" : " ") + l).ToArray());
        }

        static bool IsMe(string mailbox)
        {
            var me = AppState.CurrentAccount;
            return me != null && string.Equals(Address.EmailOf(mailbox), me.Email, StringComparison.OrdinalIgnoreCase);
        }

        void Reply(bool all)
        {
            if (_view == null) return;
            var to = !string.IsNullOrEmpty(_view.ReplyTo) ? _view.ReplyTo : _view.From;
            var d = new Draft
            {
                To = to ?? "",
                Subject = Prefixed(_view.Subject, "Re:"),
                Body = "\n\nOn " + Format.LongDate(_view.Date) + ", " + Address.NameOf(_view.From ?? "") + " wrote:\n" + Quote(_view.Text),
                InReplyTo = _view.MessageId,
                References = ((_view.References ?? "") + " " + (_view.MessageId ?? "")).Trim(),
                ReplyFolder = Folder,
                ReplyUid = _uid
            };
            if (all)
            {
                var already = new HashSet<string>(Address.Split(d.To).Select(a => Address.EmailOf(a).ToLowerInvariant()));
                var cc = new List<string>();
                foreach (var a in Address.Split(_view.To).Concat(Address.Split(_view.Cc)))
                {
                    var em = Address.EmailOf(a).ToLowerInvariant();
                    if (IsMe(a) || already.Contains(em)) continue;
                    already.Add(em);
                    cc.Add(a);
                }
                d.Cc = string.Join(", ", cc.ToArray());
            }
            AppState.PendingDraft = d;
            NavigationService.Navigate(new Uri("/ComposePage.xaml", UriKind.Relative));
        }

        void Forward()
        {
            if (_view == null) return;
            if (_view.Attachments.Any(a => !a.Inline))
                MessageBox.Show("Attachments aren't forwarded yet; only the text goes along.", "forward", MessageBoxButton.OK);
            AppState.PendingDraft = new Draft
            {
                Subject = Prefixed(_view.Subject, "Fwd:"),
                Body = "\n\n---------- Forwarded message ----------\nFrom: " + _view.From + "\nDate: " + Format.LongDate(_view.Date) +
                       "\nSubject: " + _view.Subject + "\nTo: " + _view.To + (string.IsNullOrEmpty(_view.Cc) ? "" : "\nCc: " + _view.Cc) +
                       "\n\n" + (_view.Text ?? "")
            };
            NavigationService.Navigate(new Uri("/ComposePage.xaml", UriKind.Relative));
        }

        async void Delete()
        {
            Ui.Busy(this, "deleting");
            try
            {
                await AppState.Client.DeleteAsync(Acct, Folder, _uid);
                AppState.ListDirty = true;
                if (NavigationService.CanGoBack) NavigationService.GoBack();
            }
            catch (Exception ex) { Ui.Error(ex); }
            finally { Ui.Busy(this, null); }
        }

        async void MarkUnread()
        {
            try
            {
                await AppState.Client.SetFlagAsync(Acct, Folder, _uid, 'S', false);
                AppState.ListDirty = true;
                if (NavigationService.CanGoBack) NavigationService.GoBack();
            }
            catch (Exception ex) { Ui.Error(ex); }
        }

        async void ToggleFlag()
        {
            try
            {
                _flagged = !_flagged;
                await AppState.Client.SetFlagAsync(Acct, Folder, _uid, 'F', _flagged);
                _flagItem.Text = _flagged ? "unflag" : "flag";
                AppState.ListDirty = true;
            }
            catch (Exception ex) { _flagged = !_flagged; Ui.Error(ex); }
        }

        async System.Threading.Tasks.Task MoveTo(FolderInfo dest)
        {
            if (dest.Name == Folder) return;
            Ui.Busy(this, "moving to " + dest.Label.Trim());
            try
            {
                await AppState.Client.MoveAsync(Acct, Folder, _uid, dest.Name);
                AppState.ListDirty = true;
                if (NavigationService.CanGoBack) NavigationService.GoBack();
            }
            catch (Exception ex) { Ui.Error(ex); }
            finally { Ui.Busy(this, null); }
        }
    }
}
