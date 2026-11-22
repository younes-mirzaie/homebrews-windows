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

        public sealed class CatalogRow
        {
            public CatalogEntry Entry;     // null on the installed pivot for a sideloaded app that isn't in the catalog
            public LibraryApp Library;
            public string Letter { get; set; }
            public System.Windows.Media.ImageSource Icon { get; set; }
            public Visibility LetterVisibility { get { return Icon == null ? Visibility.Visible : Visibility.Collapsed; } }
            public string Name { get; set; }
            public string Detail { get; set; }
            public string State { get; set; }
            public bool Highlight;   // an update is ready: the state line is in the accent color
            public Visibility DetailVisibility { get { return string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible; } }
            public System.Windows.Media.Brush StateBrush
            {
                get { return (System.Windows.Media.Brush)Application.Current.Resources[Highlight ? "PhoneAccentBrush" : "PhoneSubtleBrush"]; }
            }
        }

        /// <summary>The catalog joined with what's installed, for the details page.</summary>
        public static List<CatalogEntry> Entries;

        bool _busy, _loaded, _waiting;
        List<InstalledApp> _onPhone;

        public MainPage()
        {
            InitializeComponent();
            _refresh = new ApplicationBarIconButton(new Uri("/Assets/AppBar/refresh.png", UriKind.Relative)) { Text = "refresh" };
            _refresh.Click += Refresh_Click;
            _updateAll = new ApplicationBarIconButton(new Uri("/Assets/AppBar/updateall.png", UriKind.Relative)) { Text = "update all" };
            _updateAll.Click += UpdateAll_Click;
            UpdateAppBar();
            Pivots.Title = AppInfo.Name.ToUpperInvariant();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Pivots.RenderTransform = null;
            Turnstile.In(Pivots, e.NavigationMode == NavigationMode.Back);
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
                CatalogService.CleanUp();
                var t = RefreshAsync();
            }
        }

        void Busy(string text)
        {
            _busy = text != null;
            Progress.IsVisible = _busy;
            Progress.Text = text ?? "";
            UpdateAppBar();
        }

        // Keeps the phone from locking during an install (the install itself runs on without the
        // app, but the user sees it through)
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
                    : "Tap a package to install it, the same way library apps are installed. Apps you already have are updated in place, with their data.";

                Busy("reading the installed apps...");
                var apps = await Phone.InstalledAppsAsync();
                _onPhone = apps;

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
            DrawInstalled();
            await AutoUpdateAsync();
        }

        /// <summary>Automatic updates (when they're on) of the apps on the installed pivot.</summary>
        async Task AutoUpdateAsync()
        {
            if (Entries == null || _busy || _waiting) return;
            var now = Updates.Check(Entries, true);
            if (now.Count == 0) return;
            InstallOutcome outcome;
            try { outcome = await Phone.StartCatalogAsync(now, now.Select(x => x.App.Latest).ToList(), s => Busy(s)); }
            catch (Exception ex) { outcome = new InstallOutcome { Message = Friendly.Of(ex) }; }
            finally { Busy(null); }
            if (!outcome.Started) { AppLog.Add("automatic update didn't start", outcome.Message); return; }
            await FinishAsync(outcome.Job, null, true);
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
                Show(row.Name, outcome);
                return;
            }
            if (outcome.Job.Includes(Phone.OwnProductId))
                MessageBox.Show(AppInfo.Name + " is updating itself, so it will close in a moment. Open it again " +
                                "afterwards to see how it went.", "updating " + AppInfo.Name, MessageBoxButton.OK);
            await FinishAsync(outcome.Job, outcome.Package);
        }

        async Task FinishAsync(InstallJob job, PackageInfo pkg, bool quiet = false)
        {
            _waiting = true;
            Busy("working on " + job.Title + "...");
            InstallOutcome outcome;
            try
            {
                outcome = await Phone.FinishAsync(job, s => Busy(s));
                outcome.Package = pkg;
            }
            catch (Exception ex)
            {
                outcome = new InstallOutcome { Job = job, Message = Friendly.Of(ex) };
            }
            finally
            {
                Busy(null);
                _waiting = false;
            }
            if (!(quiet && outcome.Succeeded)) Show(job.Title, outcome);
            else AppLog.Add(outcome.Message, null);
            if (!outcome.StillRunning) { _loaded = true; var t = RefreshAsync(); }
        }

        void Show(string what, InstallOutcome outcome)
        {
            if (outcome.Succeeded) AppLog.Add(outcome.Message, null);
            MessageBox.Show(outcome.Message, outcome.Succeeded ? "done" : outcome.StillRunning ? "still working" : "not installed", MessageBoxButton.OK);
        }

        // ---- catalog (stage 2) ----

        async Task RefreshCatalogAsync(List<InstalledApp> installed)
        {
            if (!CatalogService.Configured)
            {
                CatalogNote.Text = "The online library isn't set up in this version of " + AppInfo.Name + ".";
                return;
            }
            CatalogIndex idx = null;
            string problem = null;
            try
            {
                Busy("downloading the library...");
                idx = await CatalogService.RefreshAsync();
            }
            catch (Exception ex)
            {
                problem = Friendly.Of(ex);
                idx = CatalogService.LoadCached();
            }
            if (idx == null)
            {
                CatalogNote.Text = problem ?? "The library isn't loaded yet.";
                return;
            }
            Busy("checking installed versions...");
            Entries = await CatalogService.EntriesAsync(idx, installed);
            Library.StartWith(Entries);
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
                Highlight = x.UpdateAvailable,
            }).ToList();
            CatalogList.ItemsSource = rows;
            CatalogNote.Text = (problem != null ? "Offline, showing the library from last time (" + problem + "). " : "") +
                               rows.Count + " apps. Tap one for details.";
        }

        /// <summary>The installed pivot: apps installed with Unesbrews (catalog or sideload) that
        /// are still on the phone, updates first.</summary>
        void DrawInstalled()
        {
            var onPhone = _onPhone == null ? null : new HashSet<string>(_onPhone.Select(a => a.ProductId));
            var entries = Entries ?? new List<CatalogEntry>();
            var pending = Updates.Pending(entries);
            var rows = Library.All()
                .Where(a => onPhone == null || onPhone.Contains(a.ProductId))
                .Select(a =>
                {
                    var x = entries.FirstOrDefault(e => e.App.ProductId == a.ProductId);
                    string name = x != null ? x.App.Name : a.Title;
                    string have = x != null && x.InstalledVersion != null ? x.InstalledVersion : a.Version;
                    return new CatalogRow
                    {
                        Entry = x,
                        Library = a,
                        Name = name,
                        Letter = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant(),
                        Icon = x != null ? CatalogService.LoadIcon(x.App) : null,
                        Highlight = pending.Contains(x),
                        State = pending.Contains(x) ? "update to " + x.App.Latest.Version + " · you have " + have
                              : (string.IsNullOrEmpty(have) ? "installed" : "version " + have) + (x == null ? " · sideloaded" : ""),
                    };
                })
                .OrderBy(r => pending.Contains(r.Entry) ? 0 : 1)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var updates = rows.Where(r => r.Highlight).ToList();
            var current = rows.Where(r => !r.Highlight).ToList();
            UpdatesList.ItemsSource = updates;
            InstalledList.ItemsSource = current;
            UpdatesHeader.Visibility = updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CurrentHeader.Visibility = updates.Count > 0 && current.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _updatesReady = pending.Count;
            UpdateAppBar();
            if (rows.Count == 0)
                InstalledNote.Text = "Apps you install with " + AppInfo.Name + ", from the library or the sideload page, show up here.";
            else if (pending.Count > 0)
                InstalledNote.Text = pending.Count + (pending.Count == 1 ? " update is" : " updates are") + " ready." +
                                     (Prefs.AutoUpdates ? "" : " Automatic updates can be turned on in settings.");
            else
                InstalledNote.Text = rows.Count + (rows.Count == 1 ? " app" : " apps") + ", all up to date.";
        }

        /// <summary>Downloads and installs every available update, one after the other, then says
        /// how each one went. Unesbrews itself goes last, because updating it closes the app.</summary>
        async void UpdateAll_Click(object sender, EventArgs e)
        {
            if (_busy || _waiting || Entries == null) return;
            var todo = Updates.Pending(Entries)
                              .OrderBy(x => x.App.ProductId == Phone.OwnProductId ? 1 : 0)
                              .ThenBy(x => x.App.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (todo.Count == 0) return;
            bool self = todo.Any(x => x.App.ProductId == Phone.OwnProductId);
            long total = todo.Sum(x => x.App.Latest.Size);
            if (MessageBox.Show("Update " + string.Join(", ", todo.Select(x => x.App.Name)) + " (" + Size(total) + ")?\n\n" +
                                "Each one is checked against the library's signed list, then updated in place with its data. " +
                                "Close those apps first. It carries on if you leave " + AppInfo.Name + " or lock the phone, " +
                                "and a notification says how it went." +
                                (self ? " " + AppInfo.Name + " updates itself last and closes; open it again afterwards." : ""),
                                "update all", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;

            InstallOutcome outcome;
            try
            {
                outcome = await Phone.StartCatalogAsync(todo, todo.Select(x => x.App.Latest).ToList(), s => Busy(s));
            }
            catch (Exception ex) { outcome = new InstallOutcome { Message = Friendly.Of(ex) }; }
            finally { Busy(null); }
            if (!outcome.Started)
            {
                MessageBox.Show(outcome.Message ?? "Nothing was updated.", "update all", MessageBoxButton.OK);
                return;
            }
            if (outcome.Message != null) MessageBox.Show(outcome.Message + "\n\nThe others are being updated now.", "update all", MessageBoxButton.OK);
            await FinishAsync(outcome.Job, null);
        }

        static string StateText(CatalogEntry x)
        {
            if (!x.Installable) return "can't be installed by " + AppInfo.Name + " yet";
            if (x.Installed == null) return "not installed · " + x.App.Latest.Version;
            if (x.InstalledVersion == null) return "installed (version unknown) · " + x.App.Latest.Version + " available";
            if (x.UpdateAvailable) return "installed " + x.InstalledVersion + " · update to " + x.App.Latest.Version;
            return "installed " + x.InstalledVersion;
        }

        bool _leaving;

        void CatalogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var list = (ListBox)sender;
            var row = list.SelectedItem as CatalogRow;
            list.SelectedItem = null;
            if (row != null) OpenDetails(row.Entry, Continuum.FindText(list.ItemContainerGenerator.ContainerFromItem(row), row.Name));
        }

        /// <summary>An app's page, with the turnstile out.</summary>
        void OpenDetails(CatalogEntry entry, FrameworkElement title)
        {
            if (_waiting)
            {
                MessageBox.Show("An install is still running. Try again once it's done.", "busy", MessageBoxButton.OK);
                return;
            }
            var uri = new Uri("/CatalogPage.xaml?id=" + Uri.EscapeDataString(entry.App.Id), UriKind.Relative);
            if (_leaving) return;
            _leaving = true;
            // continuum: the tapped name flies into the app page's title
            Continuum.FlyOut(title, Pivots, () => { _leaving = false; NavigationService.Navigate(uri); });
        }

        void InstalledRow_Tap(object sender, System.Windows.Input.GestureEventArgs e)
        {
            var row = ((FrameworkElement)sender).DataContext as CatalogRow;
            if (row == null) return;
            if (row.Entry != null) { OpenDetails(row.Entry, Continuum.FindText((DependencyObject)sender, row.Name)); return; }
            // a sideloaded app that isn't in the library: open it
            try
            {
                foreach (var p in Windows.Phone.Management.Deployment.InstallationManager.FindPackages())
                    if (PackageInfo.Guid(p.Id.ProductId) == row.Library.ProductId) { p.Launch(""); return; }
            }
            catch (Exception ex) { AppLog.Add("couldn't open " + row.Name, ex.Message); }
            MessageBox.Show(row.Name + " didn't open. Open it from the app list instead.", "open", MessageBoxButton.OK);
        }

        void Settings_Click(object sender, EventArgs e)
        {
            if (_leaving) return;
            _leaving = true;
            Turnstile.Out(Pivots, false, () => { _leaving = false; NavigationService.Navigate(new Uri("/SettingsPage.xaml", UriKind.Relative)); });
        }

        void Log_Click(object sender, EventArgs e)
        {
            if (_leaving) return;
            _leaving = true;
            Turnstile.Out(Pivots, false, () => { _leaving = false; NavigationService.Navigate(new Uri("/LogPage.xaml", UriKind.Relative)); });
        }

        readonly ApplicationBarIconButton _refresh, _updateAll;
        int _updatesReady;

        /// <summary>The app bar's buttons for the pivot page shown: "update all" only on the
        /// installed page while updates are ready, "refresh" everywhere.</summary>
        void UpdateAppBar()
        {
            var bar = ApplicationBar;
            if (bar == null || _refresh == null) return;
            bool updates = Pivots.SelectedIndex == 1 && _updatesReady > 0;
            bool has = bar.Buttons.Contains(_updateAll);
            if (has != updates || !bar.Buttons.Contains(_refresh))
            {
                bar.Buttons.Clear();
                if (updates) bar.Buttons.Add(_updateAll);
                bar.Buttons.Add(_refresh);
            }
            _refresh.IsEnabled = !_busy && !_waiting;
            _updateAll.IsEnabled = !_busy && !_waiting;
        }

        void Pivots_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateAppBar(); }

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
