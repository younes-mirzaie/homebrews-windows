using System.Linq;
using AppLoader.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unesbrews.Pages
{
    public sealed class FileRow
    {
        public FoundPackage Package;
        public string Name { get; set; }
        public string Detail { get; set; }
    }

    public sealed partial class PackagesPage : Page, ITitled
    {
        public string Title { get { return "Package files"; } }

        public PackagesPage() { InitializeComponent(); }

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
            var found = AppState.Packages;
            if (found == null)
            {
                List.ItemsSource = null;
                Note.Text = AppState.Busy ? "Looking in Downloads and Documents..." : "Tap Refresh to look for package files.";
                return;
            }
            List.ItemsSource = found.Select(p => new FileRow { Package = p, Name = p.Name, Detail = AppState.Size(p.Size) + " · " + p.Folder }).ToList();
            Note.Text = AppState.PackagesProblem ?? (found.Count == 0
                ? "There are no .xap or .appx files in Downloads or Documents, on the phone or the SD card. Download one, then tap Refresh."
                : "Tap a file to install it. Apps you already have are updated in place and keep their data.");
        }

        void Buttons() { RefreshButton.IsEnabled = !AppState.Busy && !AppState.Waiting; }

        async void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as FileRow;
            if (row != null) await AppState.InstallFileAsync(row.Package);
        }

        async void Refresh_Click(object sender, RoutedEventArgs e) { await AppState.RefreshAsync(); }
    }
}
