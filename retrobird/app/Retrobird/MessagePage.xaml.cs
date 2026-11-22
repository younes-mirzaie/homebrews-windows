// One message: headers, attachments, and the body in a web view. Remote pictures stay blocked
// until asked for. Respond (reply, reply all, forward), flag, move, delete. Building replies and
// the body page is shared with Windows 10 (Core/Drafts.cs, Core/MessageHtml.cs).
using System;
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
        AppBarButton _imagesItem, _plainItem, _flag;

        public MessagePage()
        {
            InitializeComponent();
            // respond opens a short menu, as on the phone's own Mail app
            var respond = Ui.Button(this, Symbol.MailReply, "respond", null);
            var menu = new MenuFlyout();
            foreach (var t in new[] { Tuple.Create("reply", 0), Tuple.Create("reply all", 1), Tuple.Create("forward", 2) })
            {
                var item = new MenuFlyoutItem { Text = t.Item1 };
                int which = t.Item2;
                item.Click += (s, e) => { if (which == 2) Forward(); else Reply(which == 1); };
                menu.Items.Add(item);
            }
            respond.Flyout = menu;
            _flag = Ui.Button(this, Symbol.Flag, "flag", (s, e) => ToggleFlag());
            Ui.Button(this, Symbol.MoveToFolder, "move", (s, e) => Go("/FoldersPage.xaml?pick=1"));
            Ui.Button(this, Symbol.Delete, "delete", (s, e) => Delete());
            Ui.Menu(this, "mark as unread", (s, e) => MarkUnread());
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
                // back from "move": move it and go back to the list
                var dest = AppState.PickedFolder;
                AppState.PickedFolder = null;
                await MoveTo(dest);
                return;
            }
            if (_view != null) return;   // back from replying: the message is still shown
            string flags;
            if (Query.TryGetValue("f", out flags)) _flagged = flags.IndexOf('F') >= 0;
            ShowFlag();
            var acct = AppState.CurrentAccount;
            WhereText.Text = (AppState.CurrentFolderLabel + (acct == null ? "" : " · " + acct.ShortName)).ToUpperInvariant();
            _images = AppState.LoadImages;
            await Load();
        }

        void ShowFlag()
        {
            _flag.Label = _flagged ? "unflag" : "flag";
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
            var me = AppState.CurrentAccount == null ? "" : AppState.CurrentAccount.Email;
            FromText.Text = Address.NameOf(_view.From ?? "");
            SubjectText.Text = string.IsNullOrEmpty(_view.Subject) ? "(no subject)" : _view.Subject;
            var to = Drafts.Recipients(_view, me);
            WhenText.Text = Format.LongDate(_view.Date) + (to.Length > 0 ? " · " + to : "");
            var d = new StringBuilder();
            d.Append("From: " + _view.From);
            if (!string.IsNullOrEmpty(_view.To)) d.Append("\nTo: " + _view.To);
            if (!string.IsNullOrEmpty(_view.Cc)) d.Append("\nCc: " + _view.Cc);
            if (!string.IsNullOrEmpty(_view.ReplyTo)) d.Append("\nReply to: " + _view.ReplyTo);
            DetailsText.Text = d.ToString();
            var atts = _view.Attachments.Where(a => !a.Inline).ToList();
            Attachments.ItemsSource = atts;
            Attachments.Visibility = atts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PicturesBand.Visibility = _view.Blocked ? Visibility.Visible : Visibility.Collapsed;
            _imagesItem.IsEnabled = _view.Blocked;
            _plainItem.IsEnabled = !string.IsNullOrEmpty(_view.Text);
            Render();
        }

        // ---------------- body ----------------

        static Color Accent
        {
            get
            {
                var b = Application.Current.Resources["PhoneAccentBrush"] as SolidColorBrush;
                return b != null ? b.Color : Color.FromArgb(255, 0x1B, 0xA1, 0xE2);
            }
        }

        static string Css(Color c) { return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }

        void Render()
        {
            if (_view == null) return;
            _plainItem.Label = _plain ? "show formatted" : "show as plain text";
            var style = new BodyStyle
            {
                Dark = Application.Current.RequestedTheme == ApplicationTheme.Dark,
                Accent = Css(Accent),
                FontSize = 16,           // Silverlight Normal 20 x 0.8
                Padding = "0 19px 24px"  // the page's own margins
            };
            Body.Opacity = 0;
            Body.NavigateToString(MessageHtml.Build(_view, _plain, style));
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
                AppState.PendingDraft = Drafts.FromMailto(u.OriginalString);
                Go("/ComposePage.xaml");
                return;
            }
            if (u.Scheme == "http" || u.Scheme == "https")
                await Windows.System.Launcher.LaunchUriAsync(u);
        }

        async void LoadImages()
        {
            _images = true;
            PicturesBand.Visibility = Visibility.Collapsed;
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

        void Reply(bool all)
        {
            if (_view == null) return;
            var me = AppState.CurrentAccount == null ? "" : AppState.CurrentAccount.Email;
            AppState.PendingDraft = Drafts.Reply(_view, Folder, _uid, all, me);
            Go("/ComposePage.xaml");
        }

        async void Forward()
        {
            if (_view == null) return;
            if (_view.Attachments.Any(a => !a.Inline))
                await Ui.AlertAsync("Attachments can't be forwarded yet, so only the text goes along.", "forward");
            AppState.PendingDraft = Drafts.Forward(_view);
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
                ShowFlag();
                await AppState.Client.SetFlagAsync(Acct, Folder, _uid, 'F', _flagged);
                AppState.ListDirty = true;
            }
            catch (Exception ex) { _flagged = !_flagged; ShowFlag(); await Ui.ErrorAsync(ex); }
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
