using System.Linq;
using AppLoader.Core;
using Windows.Storage.Pickers;
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
        public string Title { get { return "Sideload"; } }

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
                Note.Text = AppState.Busy ? "Looking in Downloads and Documents..." : "Tap Refresh to look for package files, or Choose file to pick one.";
                return;
            }
            List.ItemsSource = found.Select(p => new FileRow { Package = p, Name = p.Name, Detail = AppState.Size(p.Size) + " · " + p.Folder }).ToList();
            Note.Text = AppState.PackagesProblem ?? (found.Count == 0
                ? "There are no .xap or .appx files in Downloads or Documents, on the phone or the SD card. Tap Choose file to pick one from anywhere else."
                : "Tap a file to install it, or Choose file to pick one from elsewhere. Apps you already have are updated in place and keep their data.");
        }

        void Buttons()
        {
            bool idle = !AppState.Busy && !AppState.Waiting;
            RefreshButton.IsEnabled = idle;
            ChooseButton.IsEnabled = idle;
        }

        async void Choose_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads, ViewMode = PickerViewMode.List };
            picker.FileTypeFilter.Add(".appx");
            picker.FileTypeFilter.Add(".xap");
            var file = await picker.PickSingleFileAsync();
            if (file != null) await AppState.InstallPickedAsync(file);
        }

        async void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as FileRow;
            if (row != null) await AppState.InstallFileAsync(row.Package);
        }

        async void Refresh_Click(object sender, RoutedEventArgs e) { await AppState.RefreshAsync(); }
    }
}
