using System;
using System.Collections.Generic;
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
    /// <summary>One catalog app: description, versions, install/update.</summary>
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
            if (Continuum.Pending && e.NavigationMode == NavigationMode.New) { LayoutRoot.Opacity = 1; LayoutRoot.Projection = null; Continuum.FlyIn(AppName, AppTitle, IconBox, AppBy, Body); }
            else Turnstile.FeatherIn(LayoutRoot, e.NavigationMode == NavigationMode.Back);
            string id;
            NavigationContext.QueryString.TryGetValue("id", out id);
            var idx = CatalogService.Current ?? CatalogService.LoadCached();
            _app = idx == null ? null : idx.Apps.FirstOrDefault(a => a.Id == id);
            if (_app == null) { if (NavigationService.CanGoBack) NavigationService.GoBack(); return; }
            _entry = MainPage.Entries == null ? null : MainPage.Entries.FirstOrDefault(x => x.App.Id == id);
            Show();
            await Task.Yield();
        }

        void Show()
        {
            var latest = _app.Latest;
            AppName.Text = _app.Name;
            AppBy.Text = string.IsNullOrEmpty(_app.Author) ? "" : "by " + _app.Author;
            var icon = CatalogService.LoadIcon(_app);
            AppIcon.Source = icon;
            AppLetter.Text = icon == null ? _app.Name.Substring(0, 1).ToUpperInvariant() : "";
            Description.Text = string.IsNullOrEmpty(_app.Description) ? "No description." : _app.Description;
            var d = new StringBuilder();
            if (!string.IsNullOrEmpty(_app.License)) d.AppendLine("license: " + _app.License);
            d.AppendLine("latest: " + latest.Version + ", " + MainPage.Size(latest.Size) + ", " + latest.Arch);
            d.Append("product ID: " + _app.ProductId);
            Details.Text = d.ToString();
            Uri source;
            if (!string.IsNullOrEmpty(_app.Source) && Uri.TryCreate(_app.Source, UriKind.Absolute, out source) && source.Scheme == "https")
            {
                SourceLink.NavigateUri = source;
                SourceLink.Content = source.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) ? "source code on GitHub" : "source code";
                SourceLink.Visibility = Visibility.Visible;
            }

            string installed = _entry == null || _entry.Installed == null ? null
                : (_entry.InstalledVersion ?? "a version " + AppInfo.Name + " can't read");
            if (!CatalogService.Supported(latest))
            {
                Status.Text = "This kind of package (" + latest.Kind + ") can't be installed by " + AppInfo.Name + " yet.";
                SetInstall(null);
            }
            else if (installed == null)
            {
                Status.Text = "Not installed.";
                SetInstall("install");
            }
            else if (_entry.UpdateAvailable)
            {
                Status.Text = "Installed: " + installed + ". Version " + latest.Version + " is available.";
                SetInstall("update");
            }
            else
            {
                Status.Text = "Installed and up to date (" + installed + ").";
                // the phone refuses to reinstall the same version of an appx app
                SetInstall(latest.Kind == "appx" && _entry.InstalledVersion != null ? null : "reinstall");
            }
            FindLaunchable();
            Versions.ItemsSource = _app.Releases.Select(r => new VersionRow
            {
                Release = r,
                Title = r.Version,
                Detail = (string.IsNullOrEmpty(r.Notes) ? "" : r.Notes + "\n") +
                         MainPage.Size(r.Size) + (string.IsNullOrEmpty(r.Added) ? "" : " · " + r.Added),
            }).ToList();
        }

        bool _leaving;

        /// <summary>Back to the list with the turnstile, like the system pages.</summary>
        void Leave()
        {
            if (_leaving || !NavigationService.CanGoBack) return;
            _leaving = true;
            Turnstile.FeatherOut(LayoutRoot, true, () => NavigationService.GoBack());
        }

        protected override void OnBackKeyPress(System.ComponentModel.CancelEventArgs e)
        {
            base.OnBackKeyPress(e);
            if (e.Cancel || !NavigationService.CanGoBack) return;
            e.Cancel = true;
            Leave();
        }

        string _installLabel;
        Windows.ApplicationModel.Package _launchable;

        ApplicationBarIconButton InstallBar { get { return (ApplicationBarIconButton)ApplicationBar.Buttons[0]; } }
        ApplicationBarIconButton OpenBar { get { return (ApplicationBarIconButton)ApplicationBar.Buttons[1]; } }

        /// <summary>The app bar's install button: "install", "update", "reinstall", or null (none).</summary>
        void SetInstall(string label)
        {
            _installLabel = label;
            InstallBar.Text = label ?? "install";
            InstallBar.IsEnabled = label != null && !_busy;
        }

        /// <summary>Finds the installed app so "open" can start it. Apps from another publisher
        /// may not be visible to this app; open stays disabled then.</summary>
        void FindLaunchable()
        {
            _launchable = null;
            if (_entry != null && _entry.Installed != null)
            {
                try
                {
                    foreach (var p in Windows.Phone.Management.Deployment.InstallationManager.FindPackagesForCurrentPublisher())
                        if (PackageInfo.Guid(p.Id.ProductId) == _app.ProductId) { _launchable = p; break; }
                }
                catch (Exception e) { AppLog.Add("couldn't list this publisher's apps", e.Message); }
                if (_launchable == null)
                {
                    try
                    {
                        foreach (var p in Windows.Phone.Management.Deployment.InstallationManager.FindPackages())
                            if (PackageInfo.Guid(p.Id.ProductId) == _app.ProductId) { _launchable = p; break; }
                    }
                    catch (Exception e) { AppLog.Add("couldn't list the installed apps", e.Message); }
                }
            }
            OpenBar.IsEnabled = _launchable != null && _app.ProductId != Phone.OwnProductId;
        }

        void Open_Click(object sender, EventArgs e)
        {
            if (_launchable == null) return;
            try { _launchable.Launch(""); }
            catch (Exception ex)
            {
                AppLog.Add("couldn't open " + _app.Name, ex.Message);
                MessageBox.Show(_app.Name + " didn't open. Open it from the app list instead.", "open", MessageBoxButton.OK);
            }
        }

        void Busy(string text)
        {
            _busy = text != null;
            Progress.IsVisible = _busy;
            Progress.Text = text ?? "";
            InstallBar.IsEnabled = !_busy && _installLabel != null;
        }

        /// <summary>The latest package file to Downloads, for sideloading it another way.</summary>
        async void Save_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            var r = _app.Latest;
            try
            {
                Busy("downloading " + _app.Name + " " + r.Version + "...");
                string path = await CatalogService.SaveAsync(_app, r);
                Busy(null);
                MessageBox.Show(_app.Name + " " + r.Version + " is in Downloads as " + System.IO.Path.GetFileName(path) +
                                ". It was checked against the library's signed list. You can install it from the sideload page, or with any other tool.",
                                "downloaded", MessageBoxButton.OK);
            }
            catch (Exception ex)
            {
                Busy(null);
                MessageBox.Show(Friendly.Of(ex), "not downloaded", MessageBoxButton.OK);
            }
        }

        async void Install_Click(object sender, EventArgs e)
        {
            if (!_busy && _installLabel != null) await ConfirmAndInstallAsync(_app.Latest);
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
            if (MessageBox.Show("Download and install " + what + "?\n\nIt's checked against the library's signed list " +
                                "before anything is installed. An installed app is updated in place and keeps its data. " +
                                "It carries on if you leave " + AppInfo.Name + " or lock the phone, and a notification says how it went.",
                                "install", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;
            await DownloadAndInstallAsync(r);
        }

        async Task DownloadAndInstallAsync(CatalogRelease r)
        {
            if (_entry == null)
            {
                MessageBox.Show("Go back, tap refresh, then try again: " + AppInfo.Name + " needs to check what's installed first.", "install", MessageBoxButton.OK);
                return;
            }
            try
            {
                Busy("starting...");
                var outcome = await Phone.StartCatalogAsync(new List<CatalogEntry> { _entry }, new List<CatalogRelease> { r }, s => Busy(s));
                Busy(null);
                if (!outcome.Started)
                {
                    MessageBox.Show(outcome.Message ?? "Nothing was installed.", "not installed", MessageBoxButton.OK);
                    return;
                }
                if (outcome.Job.Includes(Phone.OwnProductId))
                    MessageBox.Show(AppInfo.Name + " is updating itself, so it will close once the download is done. Open it again " +
                                    "afterwards to see how it went.", "updating " + AppInfo.Name, MessageBoxButton.OK);
                // the main page follows the running job and shows the result
                Leave();
            }
            catch (Exception ex)
            {
                Busy(null);
                MessageBox.Show(Friendly.Of(ex), "not installed", MessageBoxButton.OK);
            }
        }
    }
}
