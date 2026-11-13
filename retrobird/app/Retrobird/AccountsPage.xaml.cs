// Settings: the accounts (tap one to show its mail, "edit" to change it), how often to check for
// mail, pictures, and about. Settings apply at once.
using System;
using System.Threading.Tasks;
using Retrobird.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class AccountsPage : RbPage
    {
        static readonly int[] Intervals = { 5, 15, 30, 60, 0 };
        bool _init;

        public AccountsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _init = true;
            ImagesSwitch.IsOn = AppState.LoadImages;
            _init = false;
            ShowInterval();
            await Refresh();
        }

        async Task Refresh()
        {
            try
            {
                AppState.Accounts = await AppState.Client.GetAccountsAsync();
                Items.ItemsSource = null;
                Items.ItemsSource = AppState.Accounts;
                var v = await AppState.Client.PingAsync();
                AboutText.Text = "Retrobird " + AppVersion() + " · mail engine " + v +
                                 "\nYour mail is kept in Retrobird's own storage, and everything runs inside the app.";
            }
            catch (Exception ex) { AboutText.Text = Ui.Describe(ex); }
        }

        void LicensesButton_Click(object sender, RoutedEventArgs e) { Go("/LicensePage.xaml"); }

        static string AppVersion()
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return v.Major + "." + v.Minor + "." + v.Build;
        }

        void ShowInterval()
        {
            int m = AppState.SyncMinutes;
            SyncButton.Content = m == 0 ? "only when I ask" : m < 60 ? "every " + m + " minutes" : "every hour";
        }

        async void SyncButton_Click(object sender, RoutedEventArgs e)
        {
            int i = Array.IndexOf(Intervals, AppState.SyncMinutes);
            AppState.SyncMinutes = Intervals[(i + 1) % Intervals.Length];
            ShowInterval();
            try { await AppState.Client.SetSyncMinutesAsync(AppState.SyncMinutes); }
            catch (Exception ex) { await Ui.ErrorAsync(ex); }
        }

        void ImagesSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_init) AppState.LoadImages = ImagesSwitch.IsOn;
        }

        void Items_ItemClick(object sender, ItemClickEventArgs e)
        {
            var a = e.ClickedItem as AccountInfo;
            if (a == null) return;
            if (a.Id != AppState.CurrentAccountId) AppState.SelectAccount(a);
            GoBack();
        }

        void Edit_Click(object sender, RoutedEventArgs e)
        {
            var a = (sender as FrameworkElement).Tag as AccountInfo;
            if (a != null) Go("/AccountEditPage.xaml?id=" + Uri.EscapeDataString(a.Id));
        }

        void Add_Click(object sender, RoutedEventArgs e) { Go("/ProviderPage.xaml"); }
    }
}
