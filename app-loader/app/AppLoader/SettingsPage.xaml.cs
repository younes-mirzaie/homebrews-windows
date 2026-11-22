using System;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using AppLoader.Core;

namespace AppLoader
{
    public partial class SettingsPage : PhoneApplicationPage
    {
        bool _leaving;

        public SettingsPage()
        {
            InitializeComponent();
            AppTitle.Text = AppInfo.Name.ToUpperInvariant();
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            AboutText.Text = AppInfo.Name + " " + v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision +
                             "\nHomebrew apps by Younes\n\nInstalls apps with the phone's own installer, through the system shell " +
                             "CMD Injector sets up. Apps you have are updated in place and keep their data; nothing is ever uninstalled.";
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            NotificationsBox.IsChecked = Prefs.Notifications;
            AutoUpdatesBox.IsChecked = Prefs.AutoUpdates;
            Explain();
            Turnstile.FeatherIn(LayoutRoot, e.NavigationMode == NavigationMode.Back);
        }

        void Explain()
        {
            AutoUpdatesNote.Text = Prefs.AutoUpdates
                ? "Updates of the apps on the installed page are downloaded and installed when you open " + AppInfo.Name +
                  ". " + AppInfo.Name + " itself still asks first, because updating it closes it."
                : "Off: updates that are ready are shown on the installed page, and you update them from there.";
        }

        void Notifications_Click(object sender, RoutedEventArgs e)
        {
            Prefs.Notifications = NotificationsBox.IsChecked == true;
            if (!Prefs.Notifications) Notify.ClearProgress();
        }

        void AutoUpdates_Click(object sender, RoutedEventArgs e)
        {
            Prefs.AutoUpdates = AutoUpdatesBox.IsChecked == true;
            Explain();
        }

        protected override void OnBackKeyPress(System.ComponentModel.CancelEventArgs e)
        {
            base.OnBackKeyPress(e);
            if (e.Cancel || !NavigationService.CanGoBack) return;
            e.Cancel = true;
            if (_leaving) return;
            _leaving = true;
            Turnstile.FeatherOut(LayoutRoot, true, () => NavigationService.GoBack());
        }
    }
}
