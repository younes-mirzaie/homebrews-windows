using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
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

        bool _busy, _loaded, _waiting;

        public MainPage()
        {
            InitializeComponent();
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
        static void KeepAwake(bool on)
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
            }
            catch (Exception ex)
            {
                PackagesNote.Text = ex.Message;
            }
            finally
            {
                Busy(null);
            }
        }

        static string Size(long b)
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
                outcome = new InstallOutcome { Message = ex.Message };
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
                MessageBox.Show("App Loader is updating itself, so it will close in a moment. Open it again " +
                                "afterwards to see how it went.", "updating App Loader", MessageBoxButton.OK);
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
                outcome = new InstallOutcome { Job = job, Message = ex.Message };
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
            var log = new StringBuilder();
            log.AppendLine(DateTime.Now.ToString("HH:mm:ss") + " " + what);
            if (outcome.Package != null)
                log.AppendLine(outcome.Package.Title + " " + outcome.Package.Version + " (" + outcome.Package.ProductId + ")" +
                               (outcome.Package.SdOptOut ? ", phone storage only" : ""));
            if (outcome.Th != null)
            {
                log.AppendLine("TH.exe -" + outcome.Th.Mode + (outcome.Th.Finished ? ", exit " + outcome.Th.ExitCode : ", still running"));
                log.AppendLine(outcome.Th.Output.Trim());
            }
            log.AppendLine(outcome.Message);
            LogText.Text = log.ToString();
            MessageBox.Show(outcome.Message, outcome.Succeeded ? "done" : "not installed", MessageBoxButton.OK);
            if (!outcome.Succeeded && outcome.Th != null) Pivots.SelectedIndex = 2;
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
