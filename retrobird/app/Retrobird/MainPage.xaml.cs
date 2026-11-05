// The message list of the current account and folder. Shows the local cache at once, then
// checks the server for new mail.
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Retrobird.Core;

namespace Retrobird
{
    public partial class MainPage : PhoneApplicationPage
    {
        const int PageSize = 50;
        static bool _settingsSent;
        int _shown = PageSize;
        bool _busy, _started;
        string _listKey;   // account + folder the list shows
        ObservableCollection<MessageSummary> _items = new ObservableCollection<MessageSummary>();

        public MainPage()
        {
            InitializeComponent();
            Items.ItemsSource = _items;
            ApplicationBar = new ApplicationBar();
            Ui.Button(ApplicationBar, "add", "new", (s, e) => Compose());
            Ui.Button(ApplicationBar, "refresh", "check", async (s, e) => await SyncNow(true));
            Ui.Button(ApplicationBar, "folder", "folders", (s, e) => Go("/FoldersPage.xaml"));
            Ui.Menu(ApplicationBar, "accounts and settings", (s, e) => Go("/AccountsPage.xaml"));
        }

        void Go(string page) { NavigationService.Navigate(new Uri(page, UriKind.Relative)); }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (!_started)
            {
                _started = true;
                await Start();
                return;
            }
            var acct = AppState.CurrentAccount;
            var key = acct == null ? "" : acct.Id + "/" + AppState.CurrentFolder;
            if (key != _listKey)
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
            Ui.Busy(this, "starting");
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
                Ui.Busy(this, null);
                ShowEmpty(Ui.Describe(ex), true);
                return;
            }
            _busy = false;
            Ui.Busy(this, null);
            var acct = AppState.CurrentAccount;
            if (acct == null)
            {
                ShowEmpty("Add a mail account to get started. Retrobird works with any IMAP mail service.", false);
                return;
            }
            if (AppState.CurrentAccountId != acct.Id) AppState.SelectAccount(acct);
            EmptyPanel.Visibility = Visibility.Collapsed;
            Items.Visibility = Visibility.Visible;
            ApplicationBar.IsVisible = true;
            AccountText.Text = "RETROBIRD · " + acct.Title.ToUpperInvariant();
            FolderText.Text = AppState.CurrentFolderLabel;
            _listKey = acct.Id + "/" + AppState.CurrentFolder;
            if (!acct.HasPassword) Status("The password for this account needs to be entered again (tap here).");
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
            ApplicationBar.IsVisible = !retry;
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

        async Task SyncNow(bool userAsked)
        {
            var acct = AppState.CurrentAccount;
            if (acct == null || _busy) return;
            _busy = true;
            Ui.Busy(this, "checking for mail");
            try
            {
                var r = await AppState.Client.SyncAsync(acct.Id, AppState.CurrentFolder);
                Status(null);
                if (r.Added > 0 || userAsked || _items.Count == 0) await LoadFromCache();
                if (AppState.CurrentFolderRole == "inbox" && r.Unseen >= 0) UpdateTile(r.Unseen);
            }
            catch (Exception ex)
            {
                Status(Ui.Describe(ex) + (_items.Count > 0 ? " Showing saved mail." : ""));
            }
            finally
            {
                _busy = false;
                Ui.Busy(this, null);
            }
        }

        static void UpdateTile(int unread)
        {
            try
            {
                var tile = ShellTile.ActiveTiles.FirstOrDefault();
                if (tile != null) tile.Update(new FlipTileData { Count = unread });
            }
            catch { }
        }

        async void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var acct = AppState.CurrentAccount;
            if (acct == null || _busy) return;
            _shown += PageSize;
            _busy = true;
            Ui.Busy(this, "loading older messages");
            try
            {
                var list = await AppState.Client.ListAsync(acct.Id, AppState.CurrentFolder, 0, 0);
                if (list.Cached < _shown) await AppState.Client.MoreAsync(acct.Id, AppState.CurrentFolder, _shown - list.Cached);
            }
            catch (Exception ex) { Status(Ui.Describe(ex)); }
            finally { _busy = false; Ui.Busy(this, null); }
            await LoadFromCache();
        }

        void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var m = Items.SelectedItem as MessageSummary;
            if (m == null) return;
            Items.SelectedItem = null;
            if (AppState.CurrentFolderRole == "drafts")
            {
                // drafts open as a new message with the draft's text
                OpenDraft(m);
                return;
            }
            if (m.IsUnread) m.Flags += "S";   // the helper marks it read when it opens
            Go("/MessagePage.xaml?uid=" + m.Uid + "&f=" + m.Flags);
        }

        async void OpenDraft(MessageSummary m)
        {
            var acct = AppState.CurrentAccount;
            try
            {
                Ui.Busy(this, "opening draft");
                var v = await AppState.Client.GetMessageAsync(acct.Id, AppState.CurrentFolder, m.Uid, false);
                AppState.PendingDraft = new Draft { To = v.To ?? "", Cc = v.Cc ?? "", Subject = v.Subject ?? "", Body = v.Text ?? "" };
                Go("/ComposePage.xaml");
            }
            catch (Exception ex) { Ui.Error(ex); }
            finally { Ui.Busy(this, null); }
        }

        void Compose()
        {
            if (AppState.CurrentAccount == null) return;
            AppState.PendingDraft = new Draft();
            Go("/ComposePage.xaml");
        }

        void FolderText_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            if (AppState.CurrentAccount != null) Go("/FoldersPage.xaml");
        }

        void AccountText_Tap(object sender, System.Windows.Input.GestureEventArgs e) { Go("/AccountsPage.xaml"); }

        void StatusText_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            var acct = AppState.CurrentAccount;
            if (acct != null && !acct.HasPassword) Go("/AccountEditPage.xaml?id=" + acct.Id);
        }

        void AddButton_Click(object sender, RoutedEventArgs e) { Go("/AccountEditPage.xaml"); }

        async void RetryButton_Click(object sender, RoutedEventArgs e) { await Start(); }
    }
}
