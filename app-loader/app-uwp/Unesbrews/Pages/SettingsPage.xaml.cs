using AppLoader.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unesbrews.Pages
{
    public sealed partial class SettingsPage : Page, ITitled
    {
        public string Title { get { return "Settings"; } }

        bool _loading;

        public SettingsPage() { InitializeComponent(); }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _loading = true;
            NotificationsSwitch.IsOn = Prefs.Notifications;
            AutoUpdatesSwitch.IsOn = Prefs.AutoUpdates;
            _loading = false;
            Explain();
        }

        void Explain()
        {
            AutoUpdatesNote.Text = Prefs.AutoUpdates
                ? "Updates of the apps in Installed are downloaded and installed as soon as they're found: when you open " + AppInfo.Name +
                  " and every few hours in the background. " + AppInfo.Name + " itself still asks first, because updating it closes it."
                : "Off: when updates are ready, a notification tells you, and you update them from Installed.";
        }

        void Notifications_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Prefs.Notifications = NotificationsSwitch.IsOn;
            if (!NotificationsSwitch.IsOn) Notify.ClearProgress();
        }

        async void AutoUpdates_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Prefs.AutoUpdates = AutoUpdatesSwitch.IsOn;
            Explain();
            // turned on: updates that are ready now start right away, as the text says
            if (AutoUpdatesSwitch.IsOn) await AppState.CheckUpdatesAsync();
        }

        void Log_Click(object sender, RoutedEventArgs e) { MainPage.Current.DrillIn(typeof(LogPage), null); }

        void About_Click(object sender, RoutedEventArgs e) { MainPage.Current.DrillIn(typeof(AboutPage), null); }
    }
}
