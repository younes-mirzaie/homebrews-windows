using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using AppLoader.Catalog;
using AppLoader.Core;

namespace AppLoader
{
    public partial class MainPage : PhoneApplicationPage
    {
        public sealed class PackageRow
        {
            public FoundPackage Package;
            public string Name { get; set; }
            public string Detail { get; set; }
        }

        public sealed class AppRow
        {
            public string Title { get; set; }
            public string Detail { get; set; }
        }

        public sealed class CatalogRow
        {
            public CatalogEntry Entry;
            public string Letter { get; set; }
            public System.Windows.Media.ImageSource Icon { get; set; }
            public Visibility LetterVisibility { get { return Icon == null ? Visibility.Visible : Visibility.Collapsed; } }
            public string Name { get; set; }
            public string Detail { get; set; }
            public string State { get; set; }
        }

        /// <summary>The catalogue joined with what's installed, for the details page.</summary>
        public static List<CatalogEntry> Entries;

        bool _busy, _loaded, _waiting;

        public MainPage()
        {
            InitializeComponent();
            Pivots.Title = AppInfo.Name.ToUpperInvariant();
            LogText.Text = AppLog.Text;
            AppLog.Changed += () => Dispatcher.BeginInvoke(() => LogText.Text = AppLog.Text);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // Also on every return to the app: an install started earlier may have finished (or
            // still be running) while the app was away, or App Loader was updating itself
            var job = InstallJob.Load();
            if (job != null && !_waiting)
            {
                var t = FinishAsync(job, null);
                return;
            }
            if (!_loaded)
            {
                _loaded = true;
                var t = RefreshAsync();
            }
        }

        void Busy(string text)
        {
            _busy = text != null;
            Progress.IsVisible = _busy;
            Progress.Text = text ?? "";
        }

        // Keeps the phone from locking during an install (the install itself runs on without the
        // app, but the user sees it through)
        public static void KeepAwake(bool on)
        {
            try
            {
                PhoneApplicationService.Current.UserIdleDetectionMode = on ? IdleDetectionMode.Disabled : IdleDetectionMode.Enabled;
            }
            catch (InvalidOperationException) { }   // can't go back to Enabled once disabled on some builds
        }

        async Task RefreshAsync()
        {
            if (_busy) return;
            try
            {
                Busy("looking for packages...");
                var found = await Phone.FindPackagesAsync();
                PackagesList.ItemsSource = found.Select(p => new PackageRow
                {
                    Package = p,
                    Name = p.Name,
                    Detail = Size(p.Size) + " · " + p.Folder,
                }).ToList();
                PackagesNote.Text = found.Count == 0
                    ? "No .xap or .appx files in Downloads or Documents (on the phone or the SD card). Download one and tap refresh."
                    : "Tap a package to install it. Apps you already have are updated in place, with their data.";

                Busy("reading the installed apps...");
                var apps = await Phone.InstalledAppsAsync();
                InstalledList.ItemsSource = apps
                    .Where(a => !string.IsNullOrEmpty(a.Title))
                    .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(a => new AppRow
                    {
                        Title = a.Title,
                        Detail = (a.OnSd ? "SD card" : "phone") + " · " + a.ProductId,
                    }).ToList();
                InstalledNote.Text = apps.Count + " apps.";

                await RefreshCatalogAsync(apps);
            }
            catch (Exception ex)
            {
                PackagesNote.Text = Friendly.Of(ex);
            }
            finally
            {
                Busy(null);
            }
        }

        public static string Size(long b)
        {
            if (b >= 1024 * 1024) return (b / 1048576.0).ToString("0.0") + " MB";
            return Math.Max(1, b / 1024) + " KB";
        }

        async void PackagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = PackagesList.SelectedItem as PackageRow;
            PackagesList.SelectedItem = null;
            if (row == null || _busy || _waiting) return;
            if (MessageBox.Show("Install " + row.Name + "?\n\nIf you have this app already, it's updated in place " +
                                "and keeps its data. Nothing is ever uninstalled.",
                                "install", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;
            InstallOutcome outcome;
            KeepAwake(true);
            try
            {
                outcome = await Phone.InstallAsync(row.Package, s => Busy(s));
            }
            catch (Exception ex)
            {
                outcome = new InstallOutcome { Message = Friendly.Of(ex) };
            }
            finally
            {
                Busy(null);
            }
            if (!outcome.Started)
            {
                KeepAwake(false);
                Show(row.Name, outcome);
                return;
            }
            if (outcome.Job.ProductId == Phone.OwnProductId)
                MessageBox.Show(AppInfo.Name + " is updating itself, so it will close in a moment. Open it again " +
                                "afterwards to see how it went.", "updating " + AppInfo.Name, MessageBoxButton.OK);
            await FinishAsync(outcome.Job, outcome.Package);
        }

        async Task FinishAsync(InstallJob job, PackageInfo pkg)
        {
            _waiting = true;
            KeepAwake(true);
            Busy((job.IsUpdate ? "updating " : "installing ") + job.Title + "...");
            InstallOutcome outcome;
            try
            {
                outcome = await Phone.FinishAsync(job);
                outcome.Package = pkg;
            }
            catch (Exception ex)
            {
                outcome = new InstallOutcome { Job = job, Message = Friendly.Of(ex) };
            }
            finally
            {
                Busy(null);
                KeepAwake(false);
                _waiting = false;
            }
            Show(job.Title + " " + job.Version, outcome);
            if (outcome.Succeeded || outcome.Th == null || outcome.Th.Finished) { _loaded = true; var t = RefreshAsync(); }
        }

        void Show(string what, InstallOutcome outcome)
        {
            if (outcome.Succeeded) AppLog.Add(outcome.Message, null);
            MessageBox.Show(outcome.Message, outcome.Succeeded ? "done" : "not installed", MessageBoxButton.OK);
        }

        // ---- catalogue (stage 2) ----

        async Task RefreshCatalogAsync(List<InstalledApp> installed)
        {
            if (!CatalogService.Configured)
            {
                CatalogNote.Text = "The online catalogue isn't set up in this version of " + AppInfo.Name + ".";
                UpdatesNote.Text = CatalogNote.Text;
                return;
            }
            CatalogIndex idx = null;
            string problem = null;
            try
            {
                Busy("downloading the catalogue...");
                idx = await CatalogService.RefreshAsync();
            }
            catch (Exception ex)
            {
                problem = Friendly.Of(ex);
                idx = CatalogService.LoadCached();
            }
            if (idx == null)
            {
                CatalogNote.Text = problem ?? "No catalogue yet.";
                UpdatesNote.Text = CatalogNote.Text;
                return;
            }
            Busy("checking installed versions...");
            Entries = await CatalogService.EntriesAsync(idx, installed);
            Busy("getting app icons...");
            await CatalogService.FetchIconsAsync(idx);
            var rows = Entries.OrderBy(x => x.App.Name, StringComparer.OrdinalIgnoreCase).Select(x => new CatalogRow
            {
                Entry = x,
                Letter = x.App.Name.Substring(0, 1).ToUpperInvariant(),
                Icon = CatalogService.LoadIcon(x.App),
                Name = x.App.Name,
                Detail = string.IsNullOrEmpty(x.App.Description) ? x.App.Latest.Version : x.App.Description,
                State = StateText(x),
            }).ToList();
            CatalogList.ItemsSource = rows;
            var updates = rows.Where(r => r.Entry.UpdateAvailable).ToList();
            UpdatesList.ItemsSource = updates;
            CatalogNote.Text = (problem != null ? "Offline, showing the last catalogue (" + problem + "). " : "") +
                               idx.Apps.Count + " apps. Tap one for details.";
            UpdatesNote.Text = updates.Count == 0 ? "Everything from the catalogue is up to date."
                             : updates.Count + (updates.Count == 1 ? " update" : " updates") + ". Tap one to update it.";
        }

        static string StateText(CatalogEntry x)
        {
            if (!x.Installable) return "can't be installed by " + AppInfo.Name + " yet";
            if (x.Installed == null) return "not installed · " + x.App.Latest.Version;
            if (x.InstalledVersion == null) return "installed (version unknown) · " + x.App.Latest.Version + " available";
            if (x.UpdateAvailable) return "installed " + x.InstalledVersion + " · update to " + x.App.Latest.Version;
            return "installed " + x.InstalledVersion;
        }

        void CatalogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var list = (ListBox)sender;
            var row = list.SelectedItem as CatalogRow;
            list.SelectedItem = null;
            if (row == null) return;
            if (_waiting)
            {
                MessageBox.Show("An install is still running. Try again once it's done.", "busy", MessageBoxButton.OK);
                return;
            }
            NavigationService.Navigate(new Uri("/CatalogPage.xaml?id=" + Uri.EscapeDataString(row.Entry.App.Id), UriKind.Relative));
        }

        void Refresh_Click(object sender, EventArgs e)
        {
            if (_waiting)
            {
                MessageBox.Show("An install is still running; this refreshes once it's done.", "busy", MessageBoxButton.OK);
                return;
            }
            var t = RefreshAsync();
        }
    }
}
