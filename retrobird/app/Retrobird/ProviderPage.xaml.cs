// First step of adding an account: choose a known mail service (its servers are filled in) or
// "other mail service". The account page then only asks for the address and an app password.
using System;
using System.Collections.Generic;
using Retrobird.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class ProviderPage : RbPage
    {
        static readonly Provider Other = new Provider { Name = "other mail service", Domains = new[] { "any service with IMAP and SMTP" } };

        public ProviderPage()
        {
            InitializeComponent();
            var list = new List<Provider>(Provider.All);
            list.Add(Other);
            Items.ItemsSource = list;
        }

        async void Items_ItemClick(object sender, ItemClickEventArgs e)
        {
            var p = e.ClickedItem as Provider;
            InAppHelper.Log("provider tapped: " + (p == null ? "(none)" : p.Name));
            if (p == null) return;
            if (p == Other) { Go("/AccountEditPage.xaml"); return; }
            if (!p.Works)
            {
                await Ui.AlertAsync(p.Note, p.Name);
                return;
            }
            Go("/AccountEditPage.xaml?provider=" + Uri.EscapeDataString(p.Name));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // once the account is saved, the edit page goes back here: skip this page too
            if (e.NavigationMode == NavigationMode.Back && AppState.AccountAdded)
            {
                AppState.AccountAdded = false;
                var unused = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, GoBack);
            }
        }
    }
}
