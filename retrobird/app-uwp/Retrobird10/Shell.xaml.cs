// The Windows 10 app's frame around every page: top bar, busy line and the navigation pane. The
// pane lists new mail, every inbox (all inboxes first, with two or more accounts) and the current
// account's folders, each with its unread count, and settings at the bottom. Back closes the pane,
// then a message in the right-hand pane, then goes back a page, then leaves the app.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    /// <summary>A row of the navigation pane: a place to go, or a small header.</summary>
    public sealed class NavEntry : INotifyPropertyChanged
    {
        public string Label { get; set; }
        public string Glyph { get; set; }
        public bool IsHeader { get; set; }
        public Action Open;
        public string Key;     // "*", an account id, or "account/folder"
        string _count = "";
        public string Count
        {
            get { return _count; }
            set { if (_count != value) { _count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); } }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Picks the pane row template: header or place.</summary>
    public sealed class NavTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Item { get; set; }
        public DataTemplate Header { get; set; }
        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            var e = item as NavEntry;
            return e != null && e.IsHeader ? Header : Item;
        }
        protected override DataTemplate SelectTemplateCore(object item) { return SelectTemplateCore(item, null); }
    }

    public sealed partial class Shell : Page
    {
        public static Shell Current { get; private set; }

        /// <summary>Where pages open.</summary>
        public Frame ContentFrame { get { return PageFrame; } }

        readonly List<NavEntry> _bottom;

        public Shell()
        {
            InitializeComponent();
            Current = this;
            _bottom = new List<NavEntry>
            {
                new NavEntry { Label = "Settings", Glyph = "", Key = "settings", Open = () => Open(typeof(SettingsPage)) },
            };
            BottomItems.ItemsSource = _bottom;
            TopItems.ContainerContentChanging += HeaderRowsInert;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            Loaded += (s, e) => ContentFrame.Navigate(typeof(InboxPage), null, new SuppressNavigationTransitionInfo());
        }

        // ---------------- top bar and busy line ----------------

        public void SetTitle(string title)
        {
            PageTitle.Text = (title ?? "").ToUpperInvariant();
        }

        public void Busy(string text)
        {
            StatusRow.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            StatusText.Text = text ?? "";
        }

        // ---------------- navigation pane ----------------

        // (SplitView.PaneOpening only exists from build 16299; Windows 10 Mobile is 15063 to 15254)
        void Hamburger_Click(object sender, RoutedEventArgs e)
        {
            if (!Nav.IsPaneOpen) { var t = RefreshPaneAsync(); }
            Nav.IsPaneOpen = !Nav.IsPaneOpen;
        }

        bool Overlaid { get { return Nav.DisplayMode == SplitViewDisplayMode.Overlay || Nav.DisplayMode == SplitViewDisplayMode.CompactOverlay; } }

        void HeaderRowsInert(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            var e = args.Item as NavEntry;
            args.ItemContainer.IsHitTestVisible = e == null || !e.IsHeader;
            args.ItemContainer.MinHeight = e != null && e.IsHeader ? 32 : 48;
        }

        void NavItem_Click(object sender, ItemClickEventArgs e)
        {
            var entry = e.ClickedItem as NavEntry;
            if (entry == null || entry.IsHeader || entry.Open == null) return;
            if (Overlaid) Nav.IsPaneOpen = false;
            entry.Open();
        }

        /// <summary>Rebuilds the inboxes and folders from the engine (when the pane opens, and
        /// after the inbox changed).</summary>
        public async Task RefreshPaneAsync()
        {
            var rows = new List<NavEntry>
            {
                new NavEntry { Label = "New mail", Glyph = "", Key = "new", Open = Compose },
            };
            var accounts = AppState.Accounts.ToList();
            if (accounts.Count > 0) rows.Add(new NavEntry { Label = "Accounts", IsHeader = true });
            if (accounts.Count > 1)
                rows.Add(new NavEntry { Label = "All inboxes", Glyph = "", Key = "*", Count = Count(Inbox.TotalUnread), Open = () => ShowInbox(null) });
            foreach (var a in accounts)
            {
                var id = a.Id;
                rows.Add(new NavEntry { Label = a.ShortName, Glyph = "", Key = id, Count = Count(a.Unseen), Open = () => ShowInbox(id) });
            }
            var acct = AppState.CurrentAccount;
            if (acct != null)
            {
                try
                {
                    var list = await AppState.Client.GetFoldersAsync(acct.Id);
                    var folders = list.Folders.Where(f => !f.NoSelect).ToList();
                    if (folders.Count > 0)
                        rows.Add(new NavEntry { Label = accounts.Count > 1 ? "Folders · " + acct.ShortName : "Folders", IsHeader = true });
                    foreach (var f in folders)
                    {
                        var folder = f;
                        rows.Add(new NavEntry
                        {
                            Label = Format.Sentence(f.Label.Trim()), Glyph = FolderGlyph(f.Role),
                            Key = acct.Id + "/" + f.Name, Count = Count(f.Unseen),
                            Open = () => ShowFolder(acct.Id, folder),
                        });
                    }
                }
                catch (Exception ex) { InAppHelper.Log("pane folders: " + ex.Message); }
            }
            TopItems.ItemsSource = rows;
            MarkCurrent();
        }

        static string Count(int n) { return n > 0 ? n.ToString() : ""; }

        static string FolderGlyph(string role)
        {
            switch (role)
            {
                case "inbox": return "";    // Mail
                case "sent": return "";     // Send
                case "drafts": return "";   // QuickNote
                case "trash": return "";    // Delete
                case "junk": return "";     // Blocked
                case "archive": return "";  // Package
                default: return "";         // Folder
            }
        }

        /// <summary>Highlights the pane row of what the inbox shows.</summary>
        public void MarkCurrent()
        {
            var rows = TopItems.ItemsSource as List<NavEntry>;
            if (rows == null) return;
            string key;
            if (!(ContentFrame.Content is InboxPage)) key = null;
            else if (AppState.AllInboxes) key = "*";
            else if (AppState.CurrentFolderRole == "inbox") key = AppState.CurrentAccountId;
            else key = AppState.CurrentAccountId + "/" + AppState.CurrentFolder;
            TopItems.SelectedItem = rows.FirstOrDefault(r => r.Key == key);
            BottomItems.SelectedItem = ContentFrame.Content is SettingsPage ? _bottom[0] : null;
        }

        void ShowInbox(string account)
        {
            if (account == null) AppState.AllInboxes = true;
            else AppState.SelectAccount(AppState.Accounts.FirstOrDefault(a => a.Id == account));
            OpenInbox();
        }

        void ShowFolder(string account, FolderInfo f)
        {
            AppState.AllInboxes = false;
            AppState.CurrentAccountId = account;
            AppState.CurrentFolder = f.Name;
            AppState.CurrentFolderLabel = f.Label.Trim();
            AppState.CurrentFolderRole = f.Role;
            OpenInbox();
        }

        /// <summary>Back to the inbox (top level), showing what AppState now names.</summary>
        public void OpenInbox()
        {
            while (ContentFrame.CanGoBack && !(ContentFrame.Content is InboxPage)) ContentFrame.GoBack();
            var inbox = ContentFrame.Content as InboxPage;
            if (inbox == null) ContentFrame.Navigate(typeof(InboxPage));
            else inbox.ShowCurrent();
            ContentFrame.BackStack.Clear();
            UpdateBack();
            MarkCurrent();
        }

        void Compose()
        {
            if (AppState.CurrentAccount == null) return;
            AppState.PendingDraft = new Draft();
            ContentFrame.Navigate(typeof(ComposePage), null, new DrillInNavigationTransitionInfo());
        }

        void Open(Type page)
        {
            if (ContentFrame.Content != null && ContentFrame.Content.GetType() == page) return;
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        }

        void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            UpdateBack();
            MarkCurrent();
        }

        public void UpdateBack()
        {
            // the title bar back button on desktop; phones use their own Back key
            var inbox = ContentFrame.Content as InboxPage;
            bool can = ContentFrame.CanGoBack || (inbox != null && inbox.DetailsOpen);
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                can ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
        }

        /// <summary>Back closes the pane, lets the page take it (selection mode, unsent mail), closes
        /// an open message pane, goes back a page, and otherwise leaves the app.</summary>
        void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled) return;
            if (Nav.IsPaneOpen && Overlaid)
            {
                Nav.IsPaneOpen = false;
                e.Handled = true;
                return;
            }
            var page = ContentFrame.Content as RbPage;
            var inbox = page as InboxPage;
            if (inbox != null && inbox.DetailsOpen)
            {
                var details = inbox.DetailsPage;
                if (details == null || !details.HandleBack()) inbox.CloseDetails();
                e.Handled = true;
                return;
            }
            if (page != null && page.HandleBack())
            {
                e.Handled = true;
                return;
            }
            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
                e.Handled = true;
            }
        }
    }
}
