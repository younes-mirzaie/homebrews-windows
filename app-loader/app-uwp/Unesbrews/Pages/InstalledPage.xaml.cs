using System;
using System.Collections.Generic;
using System.Linq;
using AppLoader.Catalog;
using AppLoader.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Unesbrews.Pages
{
    public sealed class InstalledRow
    {
        public CatalogEntry Entry;   // null for an app sideloaded from a file that isn't in the catalog
        public LibraryApp App;
        public string Letter { get; set; }
        public ImageSource Icon { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>The apps installed with Unesbrews (catalog or Sideload), updates first.</summary>
    public sealed partial class InstalledPage : Page, ITitled
    {
        public string Title { get { return "Installed"; } }

        public InstalledPage() { InitializeComponent(); }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            AppState.Changed += Draw;
            AppState.StatusChanged += Buttons;
            Draw();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            AppState.Changed -= Draw;
            AppState.StatusChanged -= Buttons;
            base.OnNavigatedFrom(e);
        }

        void Draw()
        {
            Buttons();
            var onPhone = AppState.Installed == null ? null : new HashSet<string>(AppState.Installed.Select(a => a.ProductId));
            var entries = AppState.Entries ?? new List<CatalogEntry>();
            var pending = AppState.PendingUpdates;
            var rows = Library.All()
                .Where(a => onPhone == null || onPhone.Contains(a.ProductId))   // uninstalled since: left out
                .Select(a =>
                {
                    var entry = entries.FirstOrDefault(x => x.App.ProductId == a.ProductId);
                    bool update = pending.Contains(entry);
                    string name = entry != null ? entry.App.Name : a.Title;
                    string have = entry != null && entry.InstalledVersion != null ? entry.InstalledVersion : a.Version;
                    return new InstalledRow
                    {
                        Entry = entry,
                        App = a,
                        Name = name,
                        Letter = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant(),
                        Icon = entry != null ? AppState.Icon(entry.App) : null,
                        Detail = update ? "Update to " + entry.App.Latest.Version + " · you have " + have
                                        : (string.IsNullOrEmpty(have) ? "Installed" : "Version " + have) + (entry == null ? " · sideloaded" : ""),
                    };
                })
                .OrderBy(r => pending.Contains(r.Entry) ? 0 : 1)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            List.ItemsSource = rows;
            UpdateAllButton.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (rows.Count == 0)
                Note.Text = AppState.CatalogProblem ?? (AppState.Busy ? "Reading the installed apps..."
                    : "Apps you install with " + AppInfo.Name + ", from the library or with Sideload, show up here.");
            else if (pending.Count > 0)
                Note.Text = pending.Count + (pending.Count == 1 ? " update is" : " updates are") + " ready. Tap Update all, or tap an app for details." +
                            (Prefs.AutoUpdates ? "" : " Automatic updates can be turned on in Settings.");
            else
                Note.Text = rows.Count + (rows.Count == 1 ? " app" : " apps") + ", all up to date.";
        }

        void Buttons()
        {
            bool idle = !AppState.Busy && !AppState.Waiting;
            RefreshButton.IsEnabled = idle;
            UpdateAllButton.IsEnabled = idle;
        }

        async void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as InstalledRow;
            if (row == null) return;
            if (row.Entry != null) { MainPage.Current.DrillIn(typeof(AppPage), row.Entry.App.Id); return; }
            // not in the catalog: open it
            var p = await AppPackages.FindAsync(row.App.ProductId);
            if (p == null || !await AppPackages.LaunchAsync(p))
                await Dialogs.TellAsync("Couldn't open it", row.Name + " didn't open. Open it from the app list instead.");
        }

        async void Refresh_Click(object sender, RoutedEventArgs e) { await AppState.RefreshAsync(); }

        /// <summary>Every update of the apps here in one background job; this app goes last.</summary>
        async void UpdateAll_Click(object sender, RoutedEventArgs e)
        {
            var todo = AppState.PendingUpdates
                .OrderBy(x => x.App.ProductId == Phone.OwnProductId ? 1 : 0)
                .ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (todo.Count == 0) return;
            long total = todo.Sum(x => x.App.Latest.Size);
            await AppState.InstallFromCatalogAsync(todo, todo.Select(x => x.App.Latest).ToList(),
                "Update " + (todo.Count == 1 ? todo[0].App.Name : todo.Count + " apps") + "?",
                string.Join(", ", todo.Select(x => x.App.Name + " " + x.App.Latest.Version)) + " (" + AppState.Size(total) + "). Close these apps first.",
                "Update");
        }
    }
}
