// One message: headers, attachments, and the body in a web view (the engine strips scripts). Remote pictures
// stay blocked until asked for. Reply, reply all, forward, delete, move, flag.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class MessagePage : RbPage
    {
        uint _uid;
        MessageView _view;
        bool _plain, _images, _flagged;
        AppBarButton _imagesItem, _plainItem, _flagItem;

        public MessagePage()
        {
            InitializeComponent();
            Ui.Button(this, Symbol.MailReply, "reply", (s, e) => Reply(false));
            Ui.Button(this, Symbol.MailReplyAll, "reply all", (s, e) => Reply(true));
            Ui.Button(this, Symbol.MailForward, "forward", (s, e) => Forward());
            Ui.Button(this, Symbol.Delete, "delete", (s, e) => Delete());
            Ui.Menu(this, "mark as unread", (s, e) => MarkUnread());
            _flagItem = Ui.Menu(this, "flag", (s, e) => ToggleFlag());
            Ui.Menu(this, "move to...", (s, e) => Go("/FoldersPage.xaml?pick=1"));
            _plainItem = Ui.Menu(this, "show as plain text", (s, e) => { _plain = !_plain; Render(); });
            _imagesItem = Ui.Menu(this, "show pictures", (s, e) => LoadImages());
        }

        static string Acct { get { return AppState.CurrentAccountId; } }
        static string Folder { get { return AppState.CurrentFolder; } }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            string uid;
            if (!Query.TryGetValue("uid", out uid) || !uint.TryParse(uid, out _uid)) return;
            if (AppState.PickedFolder != null)
            {
                // back from "move to...": move it and go back to the list
                var dest = AppState.PickedFolder;
                AppState.PickedFolder = null;
                await MoveTo(dest);
                return;
            }
            string flags;
            if (Query.TryGetValue("f", out flags)) _flagged = flags.IndexOf('F') >= 0;
            _flagItem.Label = _flagged ? "unflag" : "flag";
            _images = AppState.LoadImages;
            await Load();
        }

        async Task Load()
        {
            Busy("opening");
            try
            {
                _view = await AppState.Client.GetMessageAsync(Acct, Folder, _uid, _images);
                AppState.ListDirty = true;   // it's read now
            }
            catch (Exception ex)
            {
                ErrorText.Text = Ui.Describe(ex);
                ErrorText.Visibility = Visibility.Visible;
                Busy(null);
                return;
            }
            Busy(null);
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
            Attachments.Visibility = atts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ImagesButton.Visibility = _view.Blocked ? Visibility.Visible : Visibility.Collapsed;
            _imagesItem.IsEnabled = _view.Blocked;
            _plainItem.IsEnabled = !string.IsNullOrEmpty(_view.Text);
            Render();
        }

        // ---------------- body ----------------

        static bool DarkTheme { get { return Application.Current.RequestedTheme == ApplicationTheme.Dark; } }

        static Color Accent
        {
            get
            {
                var b = Application.Current.Resources["PhoneAccentBrush"] as SolidColorBrush;
                return b != null ? b.Color : Color.FromArgb(255, 0x1B, 0xA1, 0xE2);
            }
        }

        static string Css(Color c) { return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }

        /// <summary>Everything above 127 goes in as a numeric character reference, so the text
        /// survives whatever encoding the web view assumes.</summary>
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
            _plainItem.Label = _plain ? "show formatted" : "show as plain text";
            string bg = "#ffffff", fg = "#000000";
            if (plainMail && DarkTheme) { bg = "#000000"; fg = "#ffffff"; }
            string accent = Css(Accent);
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
            Body.Opacity = 0;
            Body.NavigateToString(html);
        }

        void Body_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs e) { Body.Opacity = 1; }

        /// <summary>Links open outside: web pages in the browser, mailto: in a new message.</summary>
        async void Body_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs e)
        {
            var u = e.Uri;
            if (u == null || !u.IsAbsoluteUri) return;   // our own NavigateToString
            if (u.Scheme == "about") return;
            e.Cancel = true;
            if (u.Scheme == "mailto")
            {
                var to = Uri.UnescapeDataString(u.OriginalString.Substring(7));
                var q = to.IndexOf('?');
                AppState.PendingDraft = new Draft { To = q >= 0 ? to.Substring(0, q) : to };
                Go("/ComposePage.xaml");
                return;
            }
            if (u.Scheme == "http" || u.Scheme == "https")
                await Windows.System.Launcher.LaunchUriAsync(u);
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

        void Header_Tapped(object sender, TappedRoutedEventArgs e)
        {
            DetailsText.Visibility = DetailsText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------------- attachments ----------------

        async void Attachments_ItemClick(object sender, ItemClickEventArgs e)
        {
            var a = e.ClickedItem as Attachment;
            if (a == null) return;
            Busy("getting " + a.Name);
            try
            {
                var dir = ApplicationData.Current.LocalFolder.Path + "\\Attachments";
                var path = await AppState.Client.SaveAttachmentAsync(Acct, Folder, _uid, a.Index, dir);
                var file = await StorageFile.GetFileFromPathAsync(path);
                Busy(null);
                if (await Windows.System.Launcher.LaunchFileAsync(file)) return;
            }
            catch (Exception ex)
            {
                Busy(null);
                await Ui.ErrorAsync(ex);
                return;
            }
            await Ui.AlertAsync("No app on this phone opens " + a.Name + ". You can install one from the Store and try again.", "attachment");
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
            Go("/ComposePage.xaml");
        }

        async void Forward()
        {
            if (_view == null) return;
            if (_view.Attachments.Any(a => !a.Inline))
                await Ui.AlertAsync("Attachments can't be forwarded yet, so only the text goes along.", "forward");
            AppState.PendingDraft = new Draft
            {
                Subject = Prefixed(_view.Subject, "Fwd:"),
                Body = "\n\n---------- Forwarded message ----------\nFrom: " + _view.From + "\nDate: " + Format.LongDate(_view.Date) +
                       "\nSubject: " + _view.Subject + "\nTo: " + _view.To + (string.IsNullOrEmpty(_view.Cc) ? "" : "\nCc: " + _view.Cc) +
                       "\n\n" + (_view.Text ?? "")
            };
            Go("/ComposePage.xaml");
        }

        async void Delete()
        {
            Busy("deleting");
            try
            {
                await AppState.Client.DeleteAsync(Acct, Folder, _uid);
                AppState.ListDirty = true;
                GoBack();
            }
            catch (Exception ex) { await Ui.ErrorAsync(ex); }
            finally { Busy(null); }
        }

        async void MarkUnread()
        {
            try
            {
                await AppState.Client.SetFlagAsync(Acct, Folder, _uid, 'S', false);
                AppState.ListDirty = true;
                GoBack();
            }
            catch (Exception ex) { await Ui.ErrorAsync(ex); }
        }

        async void ToggleFlag()
        {
            try
            {
                _flagged = !_flagged;
                await AppState.Client.SetFlagAsync(Acct, Folder, _uid, 'F', _flagged);
                _flagItem.Label = _flagged ? "unflag" : "flag";
                AppState.ListDirty = true;
            }
            catch (Exception ex) { _flagged = !_flagged; await Ui.ErrorAsync(ex); }
        }

        async Task MoveTo(FolderInfo dest)
        {
            if (dest.Name == Folder) return;
            Busy("moving to " + dest.Label.Trim());
            try
            {
                await AppState.Client.MoveAsync(Acct, Folder, _uid, dest.Name);
                AppState.ListDirty = true;
                GoBack();
            }
            catch (Exception ex) { await Ui.ErrorAsync(ex); }
            finally { Busy(null); }
        }
    }
}
