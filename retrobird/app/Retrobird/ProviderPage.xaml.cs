// First step of adding an account: pick a known mail service (its servers are filled in) or
// "other". The account page then only asks for the address and an app password.
using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.Phone.Controls;
using Retrobird.Core;

namespace Retrobird
{
    public partial class ProviderPage : PhoneApplicationPage
    {
        public ProviderPage()
        {
            InitializeComponent();
            Items.ItemsSource = Provider.All;
        }

        void Provider_Tap(object sender, GestureEventArgs e)
        {
            var p = (sender as FrameworkElement).Tag as Provider;
            if (p == null) return;
            if (!p.Works)
            {
                MessageBox.Show(p.Note, p.Name, MessageBoxButton.OK);
                return;
            }
            Open("/AccountEditPage.xaml?provider=" + Uri.EscapeDataString(p.Name));
        }

        void Other_Tap(object sender, GestureEventArgs e) { Open("/AccountEditPage.xaml"); }

        void Open(string uri)
        {
            NavigationService.Navigate(new Uri(uri, UriKind.Relative));
        }

        protected override void OnNavigatedTo(System.Windows.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // once the account is saved, the edit page goes back here: skip this page too
            if (e.NavigationMode == System.Windows.Navigation.NavigationMode.Back && AppState.AccountAdded)
            {
                AppState.AccountAdded = false;
                if (NavigationService.CanGoBack) NavigationService.GoBack();
            }
        }
    }
}
