using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using AppLoader.Catalog;
using AppLoader.Core;

namespace AppLoader
{
    /// <summary>One catalogue app: description, versions, install/update.</summary>
    public partial class CatalogPage : PhoneApplicationPage
    {
        public sealed class VersionRow
        {
            public CatalogRelease Release;
            public string Title { get; set; }
            public string Detail { get; set; }
        }

        CatalogApp _app;
        CatalogEntry _entry;
        bool _busy;

        public CatalogPage()
        {
            InitializeComponent();
            AppTitle.Text = AppInfo.Name.ToUpperInvariant();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            string id;
            NavigationContext.QueryString.TryGetValue("id", out id);
            var idx = CatalogService.Current ?? CatalogService.LoadCached();
            _app = idx == null ? null : idx.Apps.FirstOrDefault(a => a.Id == id);
            if (_app == null) { if (NavigationService.CanGoBack) NavigationService.GoBack(); return; }
            _entry = MainPage.Entries == null ? null : MainPage.Entries.FirstOrDefault(x => x.App.Id == id);
            Show();
            // a download of this app that was running when the app was left
            var dl = DownloadJob.Load();
            if (dl != null && dl.AppId == _app.Id)
            {
                var rel = _app.Releases.FirstOrDefault(r => r.Version == dl.Version);
                if (rel != null) await DownloadAndInstallAsync(rel, dl);
            }
        }

        void Show()
        {
            var latest = _app.Latest;
            AppName.Text = _app.Name;
            var icon = CatalogService.LoadIcon(_app);
            AppIcon.Source = icon;
            AppLetter.Text = icon == null ? _app.Name.Substring(0, 1).ToUpperInvariant() : "";
            Description.Text = string.IsNullOrEmpty(_app.Description) ? "No description." : _app.Description;
            var d = new StringBuilder();
            if (!string.IsNullOrEmpty(_app.Author)) d.AppendLine("by " + _app.Author);
            if (!string.IsNullOrEmpty(_app.License)) d.AppendLine("license: " + _app.License);
            if (!string.IsNullOrEmpty(_app.Source)) d.AppendLine("source: " + _app.Source);
            d.AppendLine("latest: " + latest.Version + ", " + MainPage.Size(latest.Size) + ", " + latest.Arch);
            d.Append("product ID: " + _app.ProductId);
            Details.Text = d.ToString();

            string installed = _entry == null || _entry.Installed == null ? null
                : (_entry.InstalledVersion ?? "a version App Loader can't read");
            if (!CatalogService.Supported(latest))
            {
                Status.Text = "This kind of package (" + latest.Kind + ") can't be installed by " + AppInfo.Name + " yet.";
                InstallButton.Visibility = Visibility.Collapsed;
            }
            else if (installed == null)
            {
                Status.Text = "Not installed.";
                InstallButton.Content = "install " + latest.Version;
            }
            else if (_entry.UpdateAvailable)
            {
                Status.Text = "Installed: " + installed + ". Version " + latest.Version + " is available.";
                InstallButton.Content = "update to " + latest.Version;
            }
            else
            {
                Status.Text = "Installed and up to date (" + installed + ").";
                InstallButton.Content = "reinstall " + latest.Version;
            }
            Versions.ItemsSource = _app.Releases.Select(r => new VersionRow
            {
                Release = r,
                Title = r.Version,
                Detail = (string.IsNullOrEmpty(r.Notes) ? "" : r.Notes + "\n") +
                         MainPage.Size(r.Size) + (string.IsNullOrEmpty(r.Added) ? "" : " · " + r.Added),
            }).ToList();
        }

        void Busy(string text)
        {
            _busy = text != null;
            Progress.IsVisible = _busy;
            Progress.Text = text ?? "";
            InstallButton.IsEnabled = !_busy;
        }

        async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) await ConfirmAndInstallAsync(_app.Latest);
        }

        async void Version_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            var row = ((FrameworkElement)sender).DataContext as VersionRow;
            if (row != null && !_busy) await ConfirmAndInstallAsync(row.Release);
        }

        async Task ConfirmAndInstallAsync(CatalogRelease r)
        {
            if (!CatalogService.Supported(r))
            {
                MessageBox.Show("This kind of package (" + r.Kind + ") can't be installed by " + AppInfo.Name + " yet.", "not supported", MessageBoxButton.OK);
                return;
            }
            string what = _app.Name + " " + r.Version + " (" + MainPage.Size(r.Size) + ")";
            if (MessageBox.Show("Download and install " + what + "?\n\nIt's checked against the signed catalogue " +
                                "before anything is installed. An installed app is updated in place and keeps its data.",
                                "install", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;
            await DownloadAndInstallAsync(r, null);
        }

        async Task DownloadAndInstallAsync(CatalogRelease r, DownloadJob running)
        {
            MainPage.KeepAwake(true);
            try
            {
                if (running != null || !CatalogService.IsDownloaded(r))
                {
                    Busy("starting the download...");
                    var job = running ?? await CatalogService.StartDownloadAsync(_app, r);
                    var err = await CatalogService.WaitDownloadAsync(job, _app.Name + " " + r.Version, (d, t) =>
                        Busy("downloading " + MainPage.Size(d) + (t > 0 ? " of " + MainPage.Size(t) : "") + "..."));
                    if (err != null)
                    {
                        Busy(null);
                        MessageBox.Show(err + " " + Friendly.Unchanged, "download", MessageBoxButton.OK);
                        return;
                    }
                }
                var outcome = await CatalogService.InstallDownloadedAsync(_app, r, s => Busy(s));
                Busy(null);
                if (!outcome.Started)
                {
                    MessageBox.Show(outcome.Message ?? "Nothing was installed.", "not installed", MessageBoxButton.OK);
                    return;
                }
                if (outcome.Job.ProductId == Phone.OwnProductId)
                    MessageBox.Show(AppInfo.Name + " is updating itself, so it will close in a moment. Open it again " +
                                    "afterwards to see how it went.", "updating " + AppInfo.Name, MessageBoxButton.OK);
                // the main page picks the running install up and shows the result
                if (NavigationService.CanGoBack) NavigationService.GoBack();
            }
            catch (Exception ex)
            {
                Busy(null);
                MessageBox.Show(Friendly.Of(ex), "not installed", MessageBoxButton.OK);
            }
            finally
            {
                MainPage.KeepAwake(false);
            }
        }
    }
}
