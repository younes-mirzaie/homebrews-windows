// The message list of the current account and folder, or of all inboxes together. Shows the
// local cache at once, then checks the server for new mail.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class MainPage : RbPage
    {
        const int PageSize = 50;
        static bool _settingsSent;
        int _shown = PageSize;
        bool _busy, _started;
        string _listKey;   // account + folder the list shows
        ObservableCollection<MessageSummary> _items = new ObservableCollection<MessageSummary>();
        AppBarButton _inboxes, _folders;

        static string ListKey()
        {
            if (AppState.AllInboxes) return "*/INBOX";
            var acct = AppState.CurrentAccount;
            return acct == null ? "" : acct.Id + "/" + AppState.CurrentFolder;
        }

        public MainPage()
        {
            InitializeComponent();
            Items.ItemsSource = _items;
            Ui.Button(this, Symbol.Add, "new", (s, e) => Compose());
            Ui.Button(this, Symbol.Refresh, "check", async (s, e) => await SyncNow(true));
            _inboxes = Ui.Button(this, Symbol.Mail, "inboxes", async (s, e) => await PickInbox());
            _folders = Ui.Button(this, Symbol.Folder, "folders", (s, e) => Go("/FoldersPage.xaml"));
            Ui.Menu(this, "settings", (s, e) => Go("/AccountsPage.xaml"));
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (!_started)
            {
                _started = true;
                await Start();
                return;
            }
            if (ListKey() != _listKey)
            {
                _shown = PageSize;
                await Start();
            }
            else if (AppState.ListDirty)
            {
                AppState.ListDirty = false;
                await LoadFromCache();
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
                RetryButton.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                _busy = false;
                Busy(null);
                ShowEmpty(Ui.Describe(ex), true);
                return;
            }
            _busy = false;
            Busy(null);
            var acct = AppState.CurrentAccount;
            if (acct == null)
            {
                ShowEmpty("Add a mail account to get started. Retrobird works with any IMAP mail service.", false);
                return;
            }
            if (AppState.CurrentAccountId != acct.Id) AppState.UseAccount(acct.Id);
            EmptyPanel.Visibility = Visibility.Collapsed;
            Items.Visibility = Visibility.Visible;
            BottomAppBar.Visibility = Visibility.Visible;
            _inboxes.Visibility = AppState.Accounts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _folders.Visibility = AppState.AllInboxes ? Visibility.Collapsed : Visibility.Visible;
            Status(null);
            if (AppState.AllInboxes)
            {
                AccountText.Text = "RETROBIRD";
                FolderText.Text = "all inboxes";
                var stale = AppState.Accounts.FirstOrDefault(a => !a.HasPassword);
                if (stale != null) Status(stale.Title + " needs you to sign in again (tap here).");
            }
            else
            {
                AccountText.Text = "RETROBIRD · " + acct.Title.ToUpperInvariant();
                FolderText.Text = AppState.CurrentFolderLabel;
                if (!acct.HasPassword) Status("This account needs you to sign in again (tap here).");
            }
            _listKey = ListKey();
            await LoadFromCache();
            await SyncNow(false);
        }

        void ShowEmpty(string text, bool retry)
        {
            EmptyText.Text = text;
            EmptyPanel.Visibility = Visibility.Visible;
            AddButton.Visibility = retry ? Visibility.Collapsed : Visibility.Visible;
            RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            Items.Visibility = Visibility.Collapsed;
            BottomAppBar.Visibility = retry ? Visibility.Collapsed : Visibility.Visible;
            AccountText.Text = "RETROBIRD";
            FolderText.Text = "welcome";
        }

        void Status(string text)
        {
            StatusText.Text = text ?? "";
            StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        async Task LoadFromCache()
        {
            if (AppState.AllInboxes) { await LoadAllFromCache(); return; }
            var acct = AppState.CurrentAccount;
            if (acct == null) return;
            try
            {
                var list = await AppState.Client.ListAsync(acct.Id, AppState.CurrentFolder, 0, _shown);
                bool showTo = AppState.CurrentFolderRole == "sent" || AppState.CurrentFolderRole == "drafts";
                _items.Clear();
                foreach (var m in list.Items)
                {
                    m.ShowRecipient = showTo;
                    _items.Add(m);
                }
                MoreButton.Visibility = list.Cached > _items.Count || list.Exists > list.Cached ? Visibility.Visible : Visibility.Collapsed;
                if (_items.Count == 0 && list.Exists == 0 && StatusText.Visibility == Visibility.Collapsed)
                    Status("No messages here.");
            }
            catch (Exception ex) { Status(Ui.Describe(ex)); }
        }

        /// <summary>Every account's cached inbox, merged newest first.</summary>
        async Task LoadAllFromCache()
        {
            var all = new List<MessageSummary>();
            bool more = false;
            foreach (var a in AppState.Accounts.ToList())
            {
                try
                {
                    var list = await AppState.Client.ListAsync(a.Id, "INBOX", 0, _shown);
                    foreach (var m in list.Items)
                    {
                        m.AccountId = a.Id;
                        m.AccountLabel = a.Title;
                        all.Add(m);
                    }
                    if (list.Cached > list.Items.Count || list.Exists > list.Cached) more = true;
                }
                catch (Exception ex) { Status(a.Title + ": " + Ui.Describe(ex)); }
            }
            _items.Clear();
            foreach (var m in all.OrderByDescending(m => m.Date).Take(_shown)) _items.Add(m);
            MoreButton.Visibility = more || all.Count > _items.Count ? Visibility.Visible : Visibility.Collapsed;
            if (_items.Count == 0 && StatusText.Visibility == Visibility.Collapsed) Status("No messages here.");
        }

        async Task SyncAll(bool userAsked)
        {
            _busy = true;
            Busy("checking for mail");
            var problems = new List<string>();
            try
            {
                foreach (var a in AppState.Accounts.ToList())
                {
                    try { await AppState.Client.SyncAsync(a.Id, "INBOX"); }
                    catch (Exception ex) { problems.Add(a.Title + ": " + Ui.Describe(ex)); }
                }
                try { AppState.Accounts = await AppState.Client.GetAccountsAsync(); } catch (Exception) { }
                Status(problems.Count > 0 ? string.Join("\n", problems) : null);
                await LoadAllFromCache();
                UpdateTileAll();
            }
            finally
            {
                _busy = false;
                Busy(null);
            }
        }

        void UpdateTileAll()
        {
            var latest = _items.Where(m => m.IsUnread).Take(3)
                               .Select(m => new NewMail { AccountId = m.AccountId, Uid = m.Uid, Date = Notify.ToUnix(m.Date), From = m.From, Subject = m.Subject })
                               .ToList();
            Notify.UpdateTile(AppState.Accounts.Sum(a => Math.Max(0, a.Unseen)), latest);
            foreach (var g in _items.GroupBy(m => m.AccountId)) Notify.SetLastNotified(g.Key, g.Max(m => m.Uid));
        }

        /// <summary>Switches between all inboxes and each account's inbox.</summary>
        async Task PickInbox()
        {
            var accounts = AppState.Accounts;
            if (accounts.Count == 0) { Go("/AccountsPage.xaml"); return; }
            var items = new List<Tuple<string, object>>();
            Func<int, string> count = n => n > 0 ? " (" + n + ")" : "";
            if (accounts.Count > 1) items.Add(Tuple.Create("all inboxes" + count(accounts.Sum(a => Math.Max(0, a.Unseen))), (object)"*"));
            foreach (var a in accounts) items.Add(Tuple.Create(a.Title + count(a.Unseen), (object)a.Id));
            items.Add(Tuple.Create("settings", (object)"settings"));
            var picked = await Ui.PickAsync(AccountText, "show", items) as string;
            if (picked == null) return;
            if (picked == "settings") { Go("/AccountsPage.xaml"); return; }
            if (picked == "*") AppState.AllInboxes = true;
            else AppState.SelectAccount(accounts.First(a => a.Id == picked));
            _shown = PageSize;
            await Start();
        }

        async Task SyncNow(bool userAsked)
        {
            if (_busy) return;
            if (AppState.AllInboxes) { await SyncAll(userAsked); return; }
            var acct = AppState.CurrentAccount;
            if (acct == null) return;
            _busy = true;
            Busy("checking for mail");
            try
            {
                var r = await AppState.Client.SyncAsync(acct.Id, AppState.CurrentFolder);
                Status(null);
                if (r.Added > 0 || userAsked || _items.Count == 0 || AppState.CurrentFolderRole == "inbox") await LoadFromCache();
                if (AppState.CurrentFolderRole == "inbox" && r.Unseen >= 0) UpdateTile(r.Unseen);
            }
            catch (Exception ex)
            {
                Status(Ui.Describe(ex) + (_items.Count > 0 ? " Showing saved mail." : ""));
            }
            finally
            {
                _busy = false;
                Busy(null);
            }
        }

        /// <summary>The tile shows the unread count and the newest unread messages. Mail seen
        /// here isn't announced again by the background check.</summary>
        void UpdateTile(int unread)
        {
            var acct = AppState.CurrentAccount;
            if (acct == null) return;
            var latest = _items.Where(m => m.IsUnread).Take(3)
                               .Select(m => new NewMail { AccountId = acct.Id, Uid = m.Uid, Date = Notify.ToUnix(m.Date), From = m.From, Subject = m.Subject })
                               .ToList();
            // other accounts' unread mail counts too
            int total = unread + AppState.Accounts.Where(a => a.Id != acct.Id).Sum(a => Math.Max(0, a.Unseen));
            Notify.UpdateTile(total, latest);
            if (_items.Count > 0) Notify.SetLastNotified(acct.Id, _items.Max(m => m.Uid));
        }

        /// <summary>Shows the account and folder chosen elsewhere (a notification was tapped).</summary>
        public async void ShowCurrent()
        {
            _shown = PageSize;
            await Start();
        }

        async void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var acct = AppState.CurrentAccount;
            if (acct == null || _busy) return;
            _shown += PageSize;
            if (AppState.AllInboxes)
            {
                _busy = true;
                Busy("loading older messages");
                try
                {
                    foreach (var a in AppState.Accounts.ToList())
                    {
                        var list = await AppState.Client.ListAsync(a.Id, "INBOX", 0, 0);
                        if (list.Cached < _shown && list.Exists > list.Cached)
                            await AppState.Client.MoreAsync(a.Id, "INBOX", _shown - list.Cached);
                    }
                }
                catch (Exception ex) { Status(Ui.Describe(ex)); }
                finally { _busy = false; Busy(null); }
                await LoadAllFromCache();
                return;
            }
            _busy = true;
            Busy("loading older messages");
            try
            {
                var list = await AppState.Client.ListAsync(acct.Id, AppState.CurrentFolder, 0, 0);
                if (list.Cached < _shown) await AppState.Client.MoreAsync(acct.Id, AppState.CurrentFolder, _shown - list.Cached);
            }
            catch (Exception ex) { Status(Ui.Describe(ex)); }
            finally { _busy = false; Busy(null); }
            await LoadFromCache();
        }

        void Items_ItemClick(object sender, ItemClickEventArgs e)
        {
            var m = e.ClickedItem as MessageSummary;
            if (m == null) return;
            if (AppState.CurrentFolderRole == "drafts")
            {
                // drafts open as a new message with the draft's text
                OpenDraft(m);
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

        async void FolderText_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (AppState.AllInboxes) await PickInbox();
            else if (AppState.CurrentAccount != null) Go("/FoldersPage.xaml");
        }

        async void AccountText_Tapped(object sender, TappedRoutedEventArgs e) { await PickInbox(); }

        void StatusText_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var acct = AppState.AllInboxes ? AppState.Accounts.FirstOrDefault(a => !a.HasPassword) : AppState.CurrentAccount;
            if (acct != null && !acct.HasPassword) Go("/AccountEditPage.xaml?id=" + acct.Id);
        }

        void AddButton_Click(object sender, RoutedEventArgs e) { Go("/ProviderPage.xaml"); }

        async void RetryButton_Click(object sender, RoutedEventArgs e) { await Start(); }
    }
}
