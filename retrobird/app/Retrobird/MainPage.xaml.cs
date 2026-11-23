// The inbox: one account's folder or all inboxes together, as a pivot of all, unread and flagged
// mail grouped by day. Shows the phone's cache at once, then checks the server. Loading, checking
// and the tiles are in Core/Inbox.cs (shared with the Windows 10 app); this page lays them out.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class MainPage : RbPage
    {
        static bool _settingsSent;
        int _shown = Inbox.PageSize;
        bool _busy, _started, _selecting;
        string _listKey;
        List<MessageSummary> _items = new List<MessageSummary>();

        public MainPage()
        {
            InitializeComponent();
            NormalBar();
        }

        // ---------------- app bar: browsing, or acting on a selection ----------------

        void NormalBar()
        {
            var bar = Ui.Bar(this);
            bar.PrimaryCommands.Clear();
            bar.SecondaryCommands.Clear();
            Ui.Button(this, Symbol.Add, "new", (s, e) => Compose());
            Ui.Button(this, Symbol.List, "select", (s, e) => Select(true));
            Ui.Button(this, Symbol.Sync, "sync", async (s, e) => await SyncNow());
            Ui.Button(this, Symbol.Folder, "folders", (s, e) => Go("/FoldersPage.xaml"));
            Ui.Menu(this, "settings", (s, e) => Go("/SettingsPage.xaml"));
        }

        void SelectionBar()
        {
            var bar = Ui.Bar(this);
            bar.PrimaryCommands.Clear();
            bar.SecondaryCommands.Clear();
            Ui.Button(this, Symbol.Delete, "delete", async (s, e) => await Bulk("deleting", (a, f, m) => AppState.Client.DeleteAsync(a, f, m.Uid)));
            Ui.Button(this, Symbol.Read, "mark read", async (s, e) => await Bulk(null, (a, f, m) => AppState.Client.SetFlagAsync(a, f, m.Uid, 'S', true)));
            Ui.Button(this, Symbol.Flag, "flag", async (s, e) => await Bulk(null, (a, f, m) => AppState.Client.SetFlagAsync(a, f, m.Uid, 'F', true)));
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

        /// <summary>Selection mode: check boxes on the list, commands for the checked mail.</summary>
        void Select(bool on)
        {
            _selecting = on;
            foreach (var l in new[] { AllList, UnreadList, FlaggedList })
            {
                l.SelectionMode = on && l == CurrentList ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
                l.IsItemClickEnabled = !on;
            }
            // switching views would lose the selection; the pivot stays put meanwhile
            Tabs.IsLocked = on;
            if (on) SelectionBar(); else NormalBar();
        }

        void EnableSelectionCommands()
        {
            bool any = CurrentList.SelectedItems.Count > 0;
            foreach (var c in Ui.Bar(this).PrimaryCommands.OfType<AppBarButton>()) c.IsEnabled = any;
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
                // first run: straight to adding an account (back from there leaves the app)
                Go("/ProviderPage.xaml?first=1");
                return;
            }
            if (AppState.CurrentAccountId != acct.Id) AppState.UseAccount(acct.Id);
            ShowTitle();
            _listKey = Inbox.Key();
            await Reload();
            await SyncNow();
        }

        void ShowTitle()
        {
            var t = AppState.AllInboxes ? "all inboxes"
                  : Inbox.IsInbox ? Inbox.AccountTitle
                  : Inbox.AccountTitle + " · " + Inbox.FolderTitle;
            TitleText.Text = t.ToUpperInvariant();
            TitleChevron.Visibility = AppState.Accounts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
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
        }

        void Fill(CollectionViewSource cvs, TextBlock empty, MailFilter f)
        {
            var groups = Inbox.Group(Inbox.Filter(_items, f), false);
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
                if (problems.Count > 0) Status(string.Join("\n", problems) + (_items.Count > 0 ? " Showing mail saved on the phone." : ""));
                Inbox.UpdateTiles(_items);
            }
            finally
            {
                _busy = false;
                Busy(null);
            }
        }

        /// <summary>Shows the inbox chosen elsewhere (a notification or a pinned inbox was tapped).</summary>
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

        // ---------------- choosing an inbox ----------------

        async void Title_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var accounts = AppState.Accounts;
            if (accounts.Count < 2 || _selecting) return;
            var items = new List<Tuple<string, object>>();
            Func<int, string> count = n => n > 0 ? "  " + n : "";
            items.Add(Tuple.Create("all inboxes" + count(Inbox.TotalUnread), (object)"*"));
            foreach (var a in accounts) items.Add(Tuple.Create(a.ShortName + count(a.Unseen), (object)a.Id));
            var picked = await Ui.PickAsync(TitleText, "inboxes", items) as string;
            if (picked == null) return;
            if (picked == "*") AppState.AllInboxes = true;
            else AppState.SelectAccount(accounts.First(a => a.Id == picked));
            _shown = Inbox.PageSize;
            await Start();
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
