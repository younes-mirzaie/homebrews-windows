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
    public sealed class CatalogRow
    {
        public CatalogEntry Entry;
        public string Letter { get; set; }
        public ImageSource Icon { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
    }

    public sealed partial class CatalogPage : Page, ITitled
    {
        public const string CatalogMode = "catalog", UpdatesMode = "updates";
        string _mode = CatalogMode;

        public string Title { get { return _mode == UpdatesMode ? "Updates" : "Catalog"; } }

        public CatalogPage() { InitializeComponent(); }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _mode = e.Parameter as string ?? CatalogMode;
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
            bool updates = _mode == UpdatesMode;
            Buttons();
            var entries = AppState.Entries;
            if (entries == null)
            {
                List.ItemsSource = null;
                UpdateAllButton.Visibility = Visibility.Collapsed;
                Note.Text = AppState.CatalogProblem ?? (AppState.Busy ? "Loading the catalog..." : "Tap Refresh to load the catalog.");
                return;
            }
            var shown = entries.Where(x => !updates || x.UpdateAvailable)
                               .OrderBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
                               .Select(x => new CatalogRow
                               {
                                   Entry = x,
                                   Letter = x.App.Name.Substring(0, 1).ToUpperInvariant(),
                                   Icon = AppState.Icon(x.App),
                                   Name = x.App.Name,
                                   Detail = AppState.StateText(x) + " · " + x.App.Latest.Version,
                               }).ToList();
            List.ItemsSource = shown;
            string note;
            if (updates)
                note = shown.Count == 0 ? "Everything from the catalog is up to date."
                     : shown.Count + (shown.Count == 1 ? " update is" : " updates are") + " ready. Tap one for details, or update them all at once.";
            else
                note = entries.Count + " apps. Tap one for details.";
            if (AppState.CatalogNote != null) note = AppState.CatalogNote + "\n\n" + note;
            Note.Text = note;
            UpdateAllButton.Visibility = updates && shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Buttons()
        {
            bool idle = !AppState.Busy && !AppState.Waiting;
            RefreshButton.IsEnabled = idle;
            UpdateAllButton.IsEnabled = idle;
            if (AppState.Entries == null)
                Note.Text = AppState.CatalogProblem ?? (AppState.Busy ? "Loading the catalog..." : "Tap Refresh to load the catalog.");
        }

        void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as CatalogRow;
            if (row != null) MainPage.Current.DrillIn(typeof(AppPage), row.Entry.App.Id);
        }

        async void Refresh_Click(object sender, RoutedEventArgs e) { await AppState.RefreshAsync(); }

        /// <summary>Every available update in one background job; this app goes last.</summary>
        async void UpdateAll_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.Entries == null) return;
            var todo = AppState.Entries.Where(x => x.UpdateAvailable)
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
