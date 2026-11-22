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
        public string Title { get { return "Library"; } }

        public CatalogPage() { InitializeComponent(); }

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
            var entries = AppState.Entries;
            if (entries == null)
            {
                List.ItemsSource = null;
                Note.Text = AppState.CatalogProblem ?? (AppState.Busy ? "Loading the library..." : "Tap Refresh to load the library.");
                return;
            }
            List.ItemsSource = Rows(entries.OrderBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase));
            string note = entries.Count + " apps. Tap one for details.";
            if (AppState.CatalogNote != null) note = AppState.CatalogNote + "\n\n" + note;
            Note.Text = note;
        }

        public static List<CatalogRow> Rows(IEnumerable<CatalogEntry> entries)
        {
            return entries.Select(x => new CatalogRow
            {
                Entry = x,
                Letter = x.App.Name.Substring(0, 1).ToUpperInvariant(),
                Icon = AppState.Icon(x.App),
                Name = x.App.Name,
                Detail = AppState.StateText(x) + " · " + x.App.Latest.Version,
            }).ToList();
        }

        void Buttons()
        {
            RefreshButton.IsEnabled = !AppState.Busy && !AppState.Waiting;
            if (AppState.Entries == null)
                Note.Text = AppState.CatalogProblem ?? (AppState.Busy ? "Loading the library..." : "Tap Refresh to load the library.");
        }

        void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as CatalogRow;
            if (row != null) MainPage.Current.DrillIn(typeof(AppPage), row.Entry.App.Id);
        }

        async void Refresh_Click(object sender, RoutedEventArgs e) { await AppState.RefreshAsync(); }
    }
}
