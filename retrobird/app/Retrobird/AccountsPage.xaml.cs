// Accounts (tap one to show its mail, "edit" to change it) and the app settings.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Retrobird.Core;

namespace Retrobird
{
    public partial class AccountsPage : PhoneApplicationPage
    {
        static readonly int[] Intervals = { 5, 15, 30, 60, 0 };
        bool _init;

        public AccountsPage()
        {
            InitializeComponent();
            ApplicationBar = new ApplicationBar { Mode = ApplicationBarMode.Minimized };
            Ui.Menu(ApplicationBar, "restart the mail helper", async (s, e) =>
            {
                try { await AppState.Client.QuitAsync(); } catch { }
                AppState.Client.Close();
                await System.Threading.Tasks.Task.Delay(1500);
                await Refresh();
            });
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _init = true;
            ImagesBox.IsChecked = AppState.LoadImages;
            _init = false;
            ShowInterval();
            await Refresh();
        }

        async System.Threading.Tasks.Task Refresh()
        {
            try
            {
                AppState.Accounts = await AppState.Client.GetAccountsAsync();
                Items.ItemsSource = null;
                Items.ItemsSource = AppState.Accounts;
                var v = await AppState.Client.PingAsync();
                AboutText.Text = "Retrobird 0.1 · mail helper " + v + " on 127.0.0.1:" + AppState.Client.Port +
                                 "\nMail is kept in " + HelperLauncher.HelperDir + "\\data. Everything runs on the phone.";
            }
            catch (Exception ex) { AboutText.Text = Ui.Describe(ex); }
        }

        void ShowInterval()
        {
            int m = AppState.SyncMinutes;
            SyncButton.Content = m == 0 ? "off" : m < 60 ? "every " + m + " minutes" : "every hour";
        }

        async void SyncButton_Click(object sender, RoutedEventArgs e)
        {
            int i = Array.IndexOf(Intervals, AppState.SyncMinutes);
            AppState.SyncMinutes = Intervals[(i + 1) % Intervals.Length];
            ShowInterval();
            try { await AppState.Client.SetSyncMinutesAsync(AppState.SyncMinutes); }
            catch (Exception ex) { Ui.Error(ex); }
        }

        void ImagesBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_init) AppState.LoadImages = ImagesBox.IsChecked == true;
        }

        void Account_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            var a = (sender as FrameworkElement).Tag as AccountInfo;
            if (a == null) return;
            if (a.Id != AppState.CurrentAccountId) AppState.SelectAccount(a);
            if (NavigationService.CanGoBack) NavigationService.GoBack();
        }

        void Edit_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            e.Handled = true;   // not a tap on the account row
            var a = (sender as FrameworkElement).Tag as AccountInfo;
            if (a != null) NavigationService.Navigate(new Uri("/AccountEditPage.xaml?id=" + a.Id, UriKind.Relative));
        }

        void Add_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new Uri("/AccountEditPage.xaml", UriKind.Relative));
        }
    }
}
