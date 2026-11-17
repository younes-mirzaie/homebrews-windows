using System;
using System.Collections.Generic;
using System.Linq;
using AppLoader.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Unesbrews.Pages
{
    public sealed class InstalledRow
    {
        public PhoneApp App;   // null when the list came from the manifest scan (can't open then)
        public string Name { get; set; }
        public string Detail { get; set; }
        public ImageSource Logo { get; set; }
    }

    public sealed partial class InstalledPage : Page, ITitled
    {
        public string Title { get { return "Installed apps"; } }

        public InstalledPage() { InitializeComponent(); }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            AppState.StatusChanged += Buttons;
            Buttons();
            if (List.ItemsSource == null) await LoadAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            AppState.StatusChanged -= Buttons;
            base.OnNavigatedFrom(e);
        }

        void Buttons() { RefreshButton.IsEnabled = !AppState.Busy && !AppState.Waiting; }

        async System.Threading.Tasks.Task LoadAsync()
        {
            Note.Text = "Reading the installed apps...";
            List<InstalledRow> rows;
            try
            {
                var apps = await AppPackages.ListAsync();
                rows = apps.Select(a => new InstalledRow
                {
                    App = a,
                    Name = a.Name,
                    Detail = a.Version + " · " + (a.OnSd ? "SD card" : "phone") + (string.IsNullOrEmpty(a.Publisher) ? "" : " · " + a.Publisher),
                    Logo = a.Logo != null ? new BitmapImage(a.Logo) : null,
                }).ToList();
                Note.Text = rows.Count + " apps. Tap one to open it.";
            }
            catch (Exception ex)
            {
                // Windows didn't let this app list packages: fall back to what the manifests say
                AppLog.Add("couldn't list apps through Windows, reading their manifests instead", ex.Message);
                var apps = AppState.Installed;
                if (apps == null)
                {
                    try { apps = await Phone.InstalledAppsAsync(); }
                    catch (Exception e2) { Note.Text = Friendly.Of(e2); return; }
                }
                rows = apps.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).Select(a => new InstalledRow
                {
                    Name = a.Title,
                    Detail = (a.OnSd ? "SD card" : "phone") + " · " + a.ProductId,
                }).ToList();
                Note.Text = rows.Count + " apps.";
            }
            List.ItemsSource = rows;
        }

        async void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as InstalledRow;
            if (row == null || row.App == null) return;
            if (!await AppPackages.LaunchAsync(row.App.Package))
                await Dialogs.TellAsync("Couldn't open it", row.Name + " didn't open. Open it from the app list instead.");
        }

        async void Refresh_Click(object sender, RoutedEventArgs e) { await LoadAsync(); }
    }
}
