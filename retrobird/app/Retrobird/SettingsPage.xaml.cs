// Settings: accounts (tap to change, hold to pin to Start or remove), options (how often to check,
// notifications, what the tile shows, pictures) and about. Settings apply at once.
using System;
using System.Linq;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class SettingsPage : RbPage
    {
        static readonly int[] SyncIntervals = { 5, 15, 30, 60, 0 };
        static readonly string[] SyncLabels = { "every 5 minutes", "every 15 minutes", "every 30 minutes", "every hour", "only when I ask" };
        static readonly int[] BackgroundIntervals = { 30, 60, 0 };
        static readonly string[] BackgroundLabels = { "about every 30 minutes", "about every hour", "never" };
        bool _init;

        public SettingsPage()
        {
            InitializeComponent();
            foreach (var l in SyncLabels) SyncBox.Items.Add(Ui.Text(l));
            foreach (var l in BackgroundLabels) BackgroundBox.Items.Add(Ui.Text(l));
            Ui.Button(this, Symbol.Add, "add", (s, e) => Go("/ProviderPage.xaml"));
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _init = true;
            ImagesSwitch.IsOn = AppState.LoadImages;
            NotifySwitch.IsOn = Notify.Enabled;
            TileSwitch.IsOn = Notify.TileDetails;
            SyncBox.SelectedIndex = Math.Max(0, Array.IndexOf(SyncIntervals, AppState.SyncMinutes));
            BackgroundBox.SelectedIndex = Math.Max(0, Array.IndexOf(BackgroundIntervals, Notify.BackgroundMinutes));
            NotifySwitch.IsEnabled = Notify.BackgroundMinutes != 0;
            _init = false;
            VersionText.Text = "Retrobird " + AppVersion();
            await Refresh();
        }

        async Task Refresh()
        {
            try
            {
                AppState.Accounts = await AppState.Client.GetAccountsAsync();
                Accounts.ItemsSource = null;
                Accounts.ItemsSource = AppState.Accounts;
                EngineText.Text = Ui.Text("mail engine ") + await AppState.Client.PingAsync();
            }
            catch (Exception ex) { EngineText.Text = Ui.Describe(ex); }
        }

        static string AppVersion()
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return v.Major + "." + v.Minor + "." + v.Build;
        }

        void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // the app bar ("add") belongs to the accounts list
            if (BottomAppBar != null) BottomAppBar.Visibility = Tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------- accounts ----------------

        void Accounts_ItemClick(object sender, ItemClickEventArgs e)
        {
            var a = e.ClickedItem as AccountInfo;
            if (a != null) Go("/AccountEditPage.xaml?id=" + Uri.EscapeDataString(a.Id));
        }

        void Account_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == Windows.UI.Input.HoldingState.Started) ShowAccountMenu(sender as FrameworkElement);
        }

        // a right click (Windows 10 with a mouse) opens the same menu
        void Account_RightTapped(object sender, RightTappedRoutedEventArgs e) { ShowAccountMenu(sender as FrameworkElement); }

        void ShowAccountMenu(FrameworkElement el)
        {
            var a = el == null ? null : el.DataContext as AccountInfo;
            var menu = FlyoutBase.GetAttachedFlyout(el) as MenuFlyout;
            if (a == null || menu == null) return;
            ((MenuFlyoutItem)menu.Items[0]).Text = Ui.Text(Pins.IsPinned(a) ? "unpin from Start" : "pin to Start");
            // a flyout isn't in the list's visual tree, so it gets its account here
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>()) item.Tag = a;
            FlyoutBase.ShowAttachedFlyout(el);
        }

        async void Pin_Click(object sender, RoutedEventArgs e)
        {
            var a = (sender as FrameworkElement).Tag as AccountInfo;
            if (a == null) return;
            try
            {
                if (Pins.IsPinned(a)) await Pins.UnpinAsync(a);
                else await Pins.PinAsync(a);
            }
            catch (Exception ex) { await Ui.AlertAsync("That inbox couldn't be pinned. " + ex.Message, "pin to start"); }
        }

        async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var a = (sender as FrameworkElement).Tag as AccountInfo;
            if (a == null) return;
            if (!await Ui.ConfirmAsync("Remove " + a.Email + " from this phone? Your mail stays on the server.", "remove account")) return;
            try
            {
                await Pins.UnpinAsync(a);
                await AppState.Client.DeleteAccountAsync(a.Id);
                AppState.Accounts = await AppState.Client.GetAccountsAsync();
                if (AppState.CurrentAccountId == a.Id) AppState.SelectAccount(AppState.Accounts.FirstOrDefault());
                AppState.ListDirty = true;
                await Refresh();
            }
            catch (Exception ex) { await Ui.ErrorAsync(ex); }
        }

        // ---------------- options ----------------

        async void SyncBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_init || SyncBox.SelectedIndex < 0) return;
            AppState.SyncMinutes = SyncIntervals[SyncBox.SelectedIndex];
            try { await AppState.Client.SetSyncMinutesAsync(AppState.SyncMinutes); }
            catch (Exception ex) { await Ui.ErrorAsync(ex); }
        }

        void BackgroundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_init || BackgroundBox.SelectedIndex < 0) return;
            Notify.BackgroundMinutes = BackgroundIntervals[BackgroundBox.SelectedIndex];
            NotifySwitch.IsEnabled = Notify.BackgroundMinutes != 0;
            App.ScheduleBackgroundCheck(true);
        }

        void NotifySwitch_Toggled(object sender, RoutedEventArgs e) { if (!_init) Notify.Enabled = NotifySwitch.IsOn; }

        void TileSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            Notify.TileDetails = TileSwitch.IsOn;
            AppState.ListDirty = true;   // the inbox redraws the tile next time
        }

        void ImagesSwitch_Toggled(object sender, RoutedEventArgs e) { if (!_init) AppState.LoadImages = ImagesSwitch.IsOn; }

        // ---------------- about ----------------

        void LicensesButton_Click(object sender, RoutedEventArgs e) { Go("/LicensePage.xaml"); }
    }
}
