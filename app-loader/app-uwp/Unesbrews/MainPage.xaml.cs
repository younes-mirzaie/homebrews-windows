using System;
using System.Collections.Generic;
using System.ComponentModel;
using AppLoader.Core;
using Unesbrews.Pages;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Unesbrews
{
    /// <summary>A destination in the navigation pane.</summary>
    public sealed class NavEntry : INotifyPropertyChanged
    {
        public string Label { get; set; }
        public string Glyph { get; set; }
        public Type Page;
        public object Parameter;
        string _count = "";
        /// <summary>A number after the label (updates waiting), or empty.</summary>
        public string Count
        {
            get { return _count; }
            set { if (_count != value) { _count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); } }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Pages tell the top bar their title.</summary>
    public interface ITitled { string Title { get; } }

    public sealed partial class MainPage : Page
    {
        public static MainPage Current { get; private set; }

        readonly List<NavEntry> _top, _bottom;
        readonly NavEntry _installed;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            _installed = new NavEntry { Label = "Installed", Glyph = "\uE71D", Page = typeof(InstalledPage) };
            _top = new List<NavEntry>
            {
                new NavEntry { Label = "Catalog", Glyph = "\uE719", Page = typeof(CatalogPage) },
                _installed,
                new NavEntry { Label = "Sideload", Glyph = "\uE8B5", Page = typeof(PackagesPage) },
            };
            _bottom = new List<NavEntry>
            {
                new NavEntry { Label = "Settings", Glyph = "\uE713", Page = typeof(SettingsPage) },
            };
            TopItems.ItemsSource = _top;
            BottomItems.ItemsSource = _bottom;

            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            AppState.StatusChanged += ShowStatus;
            AppState.Changed += () =>
            {
                int n = AppState.PendingUpdates.Count;
                _installed.Count = n > 0 ? n.ToString() : "";
            };
            Loaded += (s, e) => Go(_top[0], false);
        }

        void ShowStatus()
        {
            StatusRow.Visibility = AppState.Busy ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = AppState.Status ?? "";
        }

        void Hamburger_Click(object sender, RoutedEventArgs e) { Nav.IsPaneOpen = !Nav.IsPaneOpen; }

        void NavItem_Click(object sender, ItemClickEventArgs e)
        {
            var entry = e.ClickedItem as NavEntry;
            if (entry != null) Go(entry, true);
        }

        /// <summary>Opens a top-level page. Peers don't stack up: Back from one leaves the app.</summary>
        void Go(NavEntry entry, bool animate)
        {
            if (Nav.DisplayMode == SplitViewDisplayMode.Overlay || Nav.DisplayMode == SplitViewDisplayMode.CompactOverlay)
                Nav.IsPaneOpen = false;
            var current = ContentFrame.Content as Page;
            bool same = current != null && current.GetType() == entry.Page && Equals(ContentFrame.Tag, entry.Parameter);
            if (!same)
            {
                ContentFrame.Tag = entry.Parameter;
                ContentFrame.Navigate(entry.Page, entry.Parameter,
                    animate ? (NavigationTransitionInfo)new EntranceNavigationTransitionInfo() : new SuppressNavigationTransitionInfo());
            }
            ContentFrame.BackStack.Clear();
            UpdateBack();
        }

        /// <summary>Goes deeper (an app's details): Back returns to the list.</summary>
        public void DrillIn(Type page, object parameter)
        {
            ContentFrame.Navigate(page, parameter, new DrillInNavigationTransitionInfo());
            UpdateBack();
        }

        void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            var titled = e.Content as ITitled;
            PageTitle.Text = (titled != null ? titled.Title : AppInfo.Name).ToUpperInvariant();
            // mark the pane entry of the page shown (or none, on a details page)
            SelectEntry(TopItems, _top, e);
            SelectEntry(BottomItems, _bottom, e);
            UpdateBack();
        }

        void SelectEntry(ListView list, List<NavEntry> entries, NavigationEventArgs e)
        {
            NavEntry match = null;
            foreach (var n in entries)
                if (n.Page == e.SourcePageType && Equals(n.Parameter, e.Parameter)) match = n;
            list.SelectedItem = match;
        }

        void UpdateBack()
        {
            // the title bar back button on desktop; phones use their own Back key
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                ContentFrame.CanGoBack ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
        }

        /// <summary>Back closes the open pane first, then goes back a page, then leaves the app.</summary>
        void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled) return;
            if (Nav.IsPaneOpen && Nav.DisplayMode != SplitViewDisplayMode.CompactInline && Nav.DisplayMode != SplitViewDisplayMode.Inline)
            {
                Nav.IsPaneOpen = false;
                e.Handled = true;
                return;
            }
            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
                e.Handled = true;
            }
        }
    }
}
