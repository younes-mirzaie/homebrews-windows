// The inbox on Windows 10: one account's folder or all inboxes together, as a pivot of all, unread
// and flagged mail grouped by day. Loading, checking, the tiles and bulk actions are the phone
// app's own code (Core/Inbox.cs); this page lays them out, with the message on the right from
// 720 px. The navigation pane (Shell) switches inboxes and folders.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class InboxPage : RbPage
    {
        public static InboxPage Current { get; private set; }

        /// <summary>Width of the list when the message sits beside it.</summary>
        const double ListWidth = 360;

        static bool _settingsSent;
        int _shown = Inbox.PageSize;
        bool _busy, _started, _selecting;
        string _listKey;
        uint _openUid;
        List<MessageSummary> _items = new List<MessageSummary>();

        public InboxPage()
        {
            InitializeComponent();
            Current = this;
            NormalBar();
            SizeChanged += (s, e) => Layout();
        }

        // ---------------- master/details ----------------

        /// <summary>True when the window is wide enough for the message beside the list.</summary>
        public bool ShowsDetails { get { return ActualWidth >= RbPage.WideBar; } }

        public bool DetailsOpen { get { return ShowsDetails && DetailsFrame.Content != null; } }

        public RbPage DetailsPage { get { return DetailsFrame.Content as RbPage; } }

        void Layout()
        {
            bool wide = ShowsDetails;
            ListColumn.Width = wide ? new GridLength(ListWidth) : new GridLength(1, GridUnitType.Star);
            DetailsColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            DetailsPane.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            // side by side, the open message stays marked in the list
            if (!_selecting)
                foreach (var l in new[] { AllList, UnreadList, FlaggedList })
                    l.SelectionMode = wide ? ListViewSelectionMode.Single : ListViewSelectionMode.None;
            // narrowed with a message open: it carries on as a page of its own
            if (!wide && DetailsFrame.Content != null)
            {
                var arg = "uid=" + _openUid;
                CloseDetails(false);
                Go("/MessagePage.xaml?" + arg);
            }
        }

        public void OpenDetails(string arg)
        {
            string uid = (arg ?? "").Split('&').FirstOrDefault(p => p.StartsWith("uid=")) ?? "uid=0";
            uint.TryParse(uid.Substring(4), out _openUid);
            DetailsFrame.Navigate(typeof(MessagePage), arg, new EntranceNavigationTransitionInfo());
            DetailsFrame.BackStack.Clear();
            NothingOpen.Visibility = Visibility.Collapsed;
            if (Shell.Current != null) Shell.Current.UpdateBack();
        }

        public void CloseDetails() { CloseDetails(true); }

        async void CloseDetails(bool reload)
        {
            DetailsFrame.Content = null;
            DetailsFrame.BackStack.Clear();
            _openUid = 0;
            NothingOpen.Visibility = Visibility.Visible;
            foreach (var l in new[] { AllList, UnreadList, FlaggedList }) if (!_selecting) l.SelectedItem = null;
            if (Shell.Current != null) Shell.Current.UpdateBack();
            if (reload && AppState.ListDirty)
            {
                AppState.ListDirty = false;
                await Reload();
            }
        }

        // ---------------- command bar: browsing, or acting on a selection ----------------

        void NormalBar()
        {
            var bar = Ui.Bar(this);
            bar.PrimaryCommands.Clear();
            bar.SecondaryCommands.Clear();
            Ui.Button(this, Symbol.Add, "new", (s, e) => Compose());
            Ui.Button(this, Symbol.Bullets, "select", (s, e) => Select(true));
            Ui.Button(this, Symbol.Sync, "sync", async (s, e) => await SyncNow());
        }

        void SelectionBar()
        {
            var bar = Ui.Bar(this);
            bar.PrimaryCommands.Clear();
            bar.SecondaryCommands.Clear();
            Ui.Button(this, Symbol.Delete, "delete", async (s, e) => await Bulk("deleting", (a, f, m) => AppState.Client.DeleteAsync(a, f, m.Uid)));
            Ui.Button(this, Symbol.Read, "mark read", async (s, e) => await Bulk(null, (a, f, m) => AppState.Client.SetFlagAsync(a, f, m.Uid, 'S', true)));
            Ui.Button(this, Symbol.Flag, "flag", async (s, e) => await Bulk(null, (a, f, m) => AppState.Client.SetFlagAsync(a, f, m.Uid, 'F', true)));
            Ui.Button(this, Symbol.Cancel, "cancel", (s, e) => Select(false));
            Ui.Menu(this, "mark as unread", async (s, e) => await Bulk(null, (a, f, m) => AppState.Client.SetFlagAsync(a, f, m.Uid, 'S', false)));
            Ui.Menu(this, "unflag", async (s, e) => await Bulk(null, (a, f, m) => AppState.Client.SetFlagAsync(a, f, m.Uid, 'F', false)));
            EnableSelectionCommands();
        }

        ListView CurrentList
        {
            get
            {
                switch (Tabs.SelectedIndex)
                {
                    case 1: return UnreadList;
                    case 2: return FlaggedList;
                    default: return AllList;
                }
            }
        }

        void Select(bool on)
        {
            _selecting = on;
            foreach (var l in new[] { AllList, UnreadList, FlaggedList })
            {
                l.SelectionMode = on && l == CurrentList ? ListViewSelectionMode.Multiple
                                : ShowsDetails ? ListViewSelectionMode.Single : ListViewSelectionMode.None;
                l.IsItemClickEnabled = !on;
            }
            Tabs.IsLocked = on;   // switching views would lose the selection
            if (on) SelectionBar(); else NormalBar();
        }

        void EnableSelectionCommands()
        {
            bool any = CurrentList.SelectedItems.Count > 0;
            foreach (var c in Ui.Bar(this).PrimaryCommands.OfType<AppBarButton>()) c.IsEnabled = any || c.Label == "Cancel";
            foreach (var c in Ui.Bar(this).SecondaryCommands.OfType<AppBarButton>()) c.IsEnabled = any;
        }

        void List_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_selecting) EnableSelectionCommands(); }

        public override bool HandleBack()
        {
            if (!_selecting) return false;
            Select(false);
            return true;
        }

        async Task Bulk(string busy, Func<string, string, MessageSummary, Task> action)
        {
            var picked = CurrentList.SelectedItems.OfType<MessageSummary>().ToList();
            if (picked.Count == 0) return;
            Busy(busy ?? "updating");
            var problem = await Inbox.ForEachAsync(picked, action);
            Busy(null);
            Select(false);
            if (picked.Any(m => m.Uid == _openUid)) CloseDetails(false);
            await Reload();
            if (problem != null) await Ui.AlertAsync(problem, "Retrobird");
        }

        // ---------------- loading ----------------

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (!_started)
            {
                _started = true;
                await Start();
                return;
            }
            ShowTitle();
            if (Inbox.Key() != _listKey || AppState.AccountAdded)
            {
                AppState.AccountAdded = false;
                _shown = Inbox.PageSize;
                await Start();
            }
            else if (AppState.ListDirty)
            {
                AppState.ListDirty = false;
                await Reload();
            }
        }

        async Task Start()
        {
            if (_busy) return;
            _busy = true;
            Busy("starting");
            try
            {
                await AppState.Client.EnsureAsync();
                if (!_settingsSent)
                {
                    await AppState.Client.SetSyncMinutesAsync(AppState.SyncMinutes);
                    _settingsSent = true;
                }
                AppState.Accounts = await AppState.Client.GetAccountsAsync();
                ProblemPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                _busy = false;
                Busy(null);
                ProblemText.Text = Ui.Describe(ex);
                ProblemPanel.Visibility = Visibility.Visible;
                return;
            }
            _busy = false;
            Busy(null);
            foreach (var a in AppState.Accounts) Notify.RememberAccount(a.Id, a.ShortName);
            var acct = AppState.CurrentAccount;
            if (acct == null)
            {
                // first run: straight to adding an account (Back from there leaves the app)
                Go("/ProviderPage.xaml?first=1");
                return;
            }
            if (AppState.CurrentAccountId != acct.Id) AppState.UseAccount(acct.Id);
            CloseDetails(false);
            ShowTitle();
            _listKey = Inbox.Key();
            await Reload();
            if (Shell.Current != null) await Shell.Current.RefreshPaneAsync();
            await SyncNow();
        }

        void ShowTitle()
        {
            var t = AppState.AllInboxes ? "All inboxes"
                  : Inbox.IsInbox ? Inbox.AccountTitle
                  : Inbox.AccountTitle + " · " + Format.Sentence(Inbox.FolderTitle);
            TitleText.Text = string.IsNullOrEmpty(t) ? "Inbox" : t;
            var stale = AppState.AllInboxes ? AppState.Accounts.FirstOrDefault(a => !a.HasPassword) : AppState.CurrentAccount;
            if (stale != null && !stale.HasPassword) Status(stale.ShortName + " needs you to sign in again. Tap here.");
            else Status(null);
        }

        void Status(string text)
        {
            StatusText.Text = text ?? "";
            StatusBand.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Shows the cached list in all three views.</summary>
        async Task Reload()
        {
            var v = await Inbox.LoadAsync(_shown);
            _items = v.Items;
            Fill((CollectionViewSource)Resources["AllSource"], AllEmpty, MailFilter.All);
            Fill((CollectionViewSource)Resources["UnreadSource"], UnreadEmpty, MailFilter.Unread);
            Fill((CollectionViewSource)Resources["FlaggedSource"], FlaggedEmpty, MailFilter.Flagged);
            MoreButton.Visibility = v.More ? Visibility.Visible : Visibility.Collapsed;
            if (v.Problems.Count > 0) Status(string.Join("\n", v.Problems));
            // keep the open message marked in the list
            if (_openUid != 0 && !_selecting)
                foreach (var l in new[] { AllList, UnreadList, FlaggedList })
                    l.SelectedItem = _items.FirstOrDefault(m => m.Uid == _openUid);
        }

        void Fill(CollectionViewSource cvs, TextBlock empty, MailFilter f)
        {
            var groups = Inbox.Group(Inbox.Filter(_items, f), true);
            cvs.Source = groups;
            empty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        async Task SyncNow()
        {
            if (_busy || AppState.CurrentAccount == null) return;
            _busy = true;
            Busy("checking for mail");
            try
            {
                var problems = await Inbox.SyncAsync();
                await Reload();
                ShowTitle();
                if (problems.Count > 0) Status(string.Join("\n", problems) + (_items.Count > 0 ? " Showing mail saved on this device." : ""));
                Inbox.UpdateTiles(_items);
                if (Shell.Current != null) await Shell.Current.RefreshPaneAsync();
            }
            finally
            {
                _busy = false;
                Busy(null);
            }
        }

        /// <summary>Shows the inbox chosen elsewhere (the pane, a notification or a pinned inbox).</summary>
        public async void ShowCurrent()
        {
            if (_selecting) Select(false);
            _shown = Inbox.PageSize;
            await Start();
        }

        async void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _shown += Inbox.PageSize;
            _busy = true;
            Busy("getting older mail");
            try { await Inbox.MoreAsync(_shown); }
            catch (Exception ex) { Status(Ui.Describe(ex)); }
            finally { _busy = false; Busy(null); }
            await Reload();
        }

        void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        // ---------------- opening mail ----------------

        void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            var m = e.ClickedItem as MessageSummary;
            if (m == null) return;
            if (AppState.CurrentFolderRole == "drafts" && !AppState.AllInboxes)
            {
                OpenDraft(m);   // a draft opens as a message to finish
                return;
            }
            // in all inboxes, the message's own account is the one to read and reply with
            if (AppState.AllInboxes && m.AccountId != null) AppState.UseAccount(m.AccountId);
            if (m.IsUnread) m.Flags += "S";   // the engine marks it read when it opens
            Go("/MessagePage.xaml?uid=" + m.Uid + "&f=" + Uri.EscapeDataString(m.Flags));
        }

        async void OpenDraft(MessageSummary m)
        {
            var acct = AppState.CurrentAccount;
            try
            {
                Busy("opening draft");
                var v = await AppState.Client.GetMessageAsync(acct.Id, AppState.CurrentFolder, m.Uid, false);
                AppState.PendingDraft = new Draft { To = v.To ?? "", Cc = v.Cc ?? "", Subject = v.Subject ?? "", Body = v.Text ?? "" };
                Go("/ComposePage.xaml");
            }
            catch (Exception ex) { var unused = Ui.ErrorAsync(ex); }
            finally { Busy(null); }
        }

        void Compose()
        {
            if (AppState.CurrentAccount == null) return;
            AppState.PendingDraft = new Draft();
            Go("/ComposePage.xaml");
        }

        void StatusBand_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var acct = AppState.AllInboxes ? AppState.Accounts.FirstOrDefault(a => !a.HasPassword) : AppState.CurrentAccount;
            if (acct != null && !acct.HasPassword) Go("/AccountEditPage.xaml?id=" + Uri.EscapeDataString(acct.Id));
            else Status(null);
        }

        async void RetryButton_Click(object sender, RoutedEventArgs e) { await Start(); }
    }
}
