using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AppLoader.Catalog;
using AppLoader.Core;
using AppInfo = AppLoader.Core.AppInfo;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unesbrews.Pages
{
    public sealed class VersionRow
    {
        public CatalogRelease Release;
        public string Title { get; set; }
        public string Detail { get; set; }
    }

    public sealed partial class AppPage : Page, ITitled
    {
        CatalogEntry _entry;
        Package _installed;

        public string Title { get { return _entry != null ? _entry.App.Name : "App"; } }

        public AppPage() { InitializeComponent(); }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Find(e.Parameter as string);
            AppState.Changed += Refresh;
            AppState.StatusChanged += Buttons;
            Draw();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            AppState.Changed -= Refresh;
            AppState.StatusChanged -= Buttons;
            base.OnNavigatedFrom(e);
        }

        void Find(string id)
        {
            _entry = AppState.Entries == null ? null : AppState.Entries.FirstOrDefault(x => x.App.Id == id);
        }

        void Refresh()
        {
            if (_entry != null) Find(_entry.App.Id);
            Draw();
        }

        async void Draw()
        {
            if (_entry == null)
            {
                AppName.Text = "This app isn't in the library any more.";
                MainAction.Visibility = Visibility.Collapsed;
                SaveButton.IsEnabled = false;
                return;
            }
            var app = _entry.App;
            var latest = app.Latest;
            AppName.Text = app.Name;
            AppBy.Text = string.IsNullOrEmpty(app.Author) ? "" : "by " + app.Author;
            var icon = AppState.Icon(app);
            AppIcon.Source = icon;
            AppLetter.Text = icon == null ? app.Name.Substring(0, 1).ToUpperInvariant() : "";
            Description.Text = string.IsNullOrEmpty(app.Description) ? "No description." : app.Description;

            var d = new StringBuilder();
            if (!string.IsNullOrEmpty(app.License)) d.AppendLine("License: " + app.License);
            d.AppendLine("Latest version: " + latest.Version + ", " + AppState.Size(latest.Size));
            if (!string.IsNullOrEmpty(latest.Arch)) d.AppendLine("Processor: " + latest.Arch);
            d.Append("Product ID: " + app.ProductId);
            Details.Text = d.ToString();
            Uri source;
            SourceLink.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(app.Source) && Uri.TryCreate(app.Source, UriKind.Absolute, out source) && source.Scheme == "https")
            {
                SourceLink.NavigateUri = source;
                SourceLink.Content = source.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) ? "Source code on GitHub" : "Source code";
                SourceLink.Visibility = Visibility.Visible;
            }

            string installed = _entry.Installed == null ? null : (_entry.InstalledVersion ?? "a version " + AppInfo.Name + " can't read");
            if (!_entry.Installable)
            {
                StateLine.Text = "This kind of package (" + latest.Kind + ") can't be installed by " + AppInfo.Name + " yet.";
                SetAction(null);
            }
            else if (installed == null)
            {
                StateLine.Text = "Not installed.";
                SetAction("Install");
            }
            else if (_entry.UpdateAvailable)
            {
                StateLine.Text = "You have " + installed + ". Version " + latest.Version + " is ready.";
                SetAction("Update");
            }
            else
            {
                StateLine.Text = "Installed and up to date (" + installed + ").";
                // the phone refuses to reinstall the same version of an appx app
                SetAction(latest.Kind == "appx" && _entry.InstalledVersion != null ? null : "Reinstall");
            }

            Versions.ItemsSource = app.Releases.Select(r => new VersionRow
            {
                Release = r,
                Title = r.Version,
                Detail = (string.IsNullOrEmpty(r.Notes) ? "" : r.Notes + "\n") + AppState.Size(r.Size) +
                         (string.IsNullOrEmpty(r.Added) ? "" : " · " + r.Added),
            }).ToList();

            // "Open" for an installed app other than this one
            _installed = null;
            OpenButton.IsEnabled = false;
            if (_entry.Installed != null && app.ProductId != Phone.OwnProductId)
            {
                _installed = await AppPackages.FindAsync(app.ProductId);
                OpenButton.IsEnabled = _installed != null;
            }
        }

        string _action;

        // Typed out rather than in XAML: the label changes with what's installed
        void SetAction(string label)
        {
            _action = label;
            MainAction.Content = label ?? "";
            MainAction.Visibility = label == null ? Visibility.Collapsed : Visibility.Visible;
            Buttons();
        }

        void Buttons()
        {
            bool idle = !AppState.Busy && !AppState.Waiting;
            MainAction.IsEnabled = idle && _action != null;
            SaveButton.IsEnabled = idle && _entry != null;
            Versions.IsItemClickEnabled = idle;
        }

        async void MainAction_Click(object sender, RoutedEventArgs e)
        {
            if (_entry != null && _action != null) await Install(_entry.App.Latest, _action);
        }

        async void Versions_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as VersionRow;
            if (row != null && _entry != null) await Install(row.Release, "Install");
        }

        async System.Threading.Tasks.Task Install(CatalogRelease r, string verb)
        {
            if (!CatalogService.Supported(r))
            {
                await Dialogs.TellAsync("Can't install this", "This kind of package (" + r.Kind + ") can't be installed by " + AppInfo.Name + " yet.");
                return;
            }
            await AppState.InstallFromCatalogAsync(new List<CatalogEntry> { _entry }, new List<CatalogRelease> { r },
                verb + " " + _entry.App.Name + " " + r.Version + "?",
                _entry.App.Name + " " + r.Version + " (" + AppState.Size(r.Size) + ") is downloaded and installed.",
                verb);
        }

        /// <summary>The latest package file to Downloads, for sideloading it another way.</summary>
        async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_entry != null) await AppState.SavePackageAsync(_entry.App, _entry.App.Latest);
        }

        async void Open_Click(object sender, RoutedEventArgs e)
        {
            if (_installed == null) return;
            if (!await AppPackages.LaunchAsync(_installed))
                await Dialogs.TellAsync("Couldn't open it", _entry.App.Name + " didn't open. Open it from the app list instead.");
        }
    }
}
