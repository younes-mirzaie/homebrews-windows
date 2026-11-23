using System;
using MetroTelegram;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace RetroTel
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage() { InitializeComponent(); }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var s = TgSession.Storage;
            s.Load();
            NameText.Text = string.IsNullOrEmpty(s.CurrentUserName) ? "Telegram user" : s.CurrentUserName;
            PhoneText.Text = string.IsNullOrEmpty(s.CurrentUserPhone) ? "" : "+" + s.CurrentUserPhone;
            var v = Package.Current.Id.Version;
            VersionText.Text = string.Format("RetroTel {0}.{1}.{2}", v.Major, v.Minor, v.Build);
        }

        async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            if (!await Ui.ConfirmAsync("Sign out of Telegram on this device? Your chats stay in your Telegram account.", "Sign out", "Sign out")) return;
            SignOutButton.IsEnabled = false;
            try { await TgSession.SignOutAsync(); }
            catch { /* signed out here even if Telegram couldn't be told */ }
            MainPage.Model.Dialogs.Clear();
            App.StartOver(typeof(SignInPage));
        }
    }
}
