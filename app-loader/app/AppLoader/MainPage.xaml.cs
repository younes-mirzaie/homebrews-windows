using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
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

        bool _busy, _loaded;

        public MainPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
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

        async System.Threading.Tasks.Task RefreshAsync()
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
            if (row == null || _busy) return;
            if (MessageBox.Show("Install " + row.Name + "?\n\nIf you have this app already, it's updated in place " +
                                "and keeps its data. Nothing is ever uninstalled.",
                                "install", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;
            InstallOutcome outcome = null;
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
            var log = new StringBuilder();
            log.AppendLine(DateTime.Now.ToString("HH:mm:ss") + " " + row.Name);
            if (outcome.Package != null)
                log.AppendLine(outcome.Package.Title + " " + outcome.Package.Version + " (" + outcome.Package.ProductId + ")" +
                               (outcome.Package.SdOptOut ? ", phone storage only" : ""));
            if (outcome.Th != null)
            {
                log.AppendLine("TH.exe -" + outcome.Th.Mode + ", exit " + outcome.Th.ExitCode);
                log.AppendLine(outcome.Th.Output.Trim());
            }
            log.AppendLine(outcome.Message);
            LogText.Text = log.ToString();
            MessageBox.Show(outcome.Message, outcome.Succeeded ? "done" : "not installed", MessageBoxButton.OK);
            if (outcome.Succeeded) { var t = RefreshAsync(); }
            else if (outcome.Th != null) Pivots.SelectedIndex = 2;
        }

        void Refresh_Click(object sender, EventArgs e)
        {
            var t = RefreshAsync();
        }
    }
}
