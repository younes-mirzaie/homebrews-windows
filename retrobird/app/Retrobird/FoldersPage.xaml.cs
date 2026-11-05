// The account's folders with unread counts. In pick mode (?pick=1) it chooses a target folder
// for "move to..." instead of switching the list.
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Retrobird.Core;

namespace Retrobird
{
    public partial class FoldersPage : PhoneApplicationPage
    {
        bool _pick, _loaded;

        public FoldersPage() { InitializeComponent(); }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_loaded) return;
            _loaded = true;
            string p;
            _pick = NavigationContext.QueryString.TryGetValue("pick", out p) && p == "1";
            var acct = AppState.CurrentAccount;
            if (acct == null) return;
            AccountText.Text = "RETROBIRD · " + acct.Title.ToUpperInvariant();
            TitleText.Text = _pick ? "move to" : "folders";
            Ui.Busy(this, "getting folders");
            try
            {
                var list = await AppState.Client.GetFoldersAsync(acct.Id);
                Items.ItemsSource = _pick ? list.Folders.Where(f => !f.NoSelect && f.Name != AppState.CurrentFolder).ToList() : list.Folders;
                if (!list.Online)
                {
                    StatusText.Text = "Offline: " + list.Error;
                    StatusText.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = Ui.Describe(ex);
                StatusText.Visibility = Visibility.Visible;
            }
            finally { Ui.Busy(this, null); }
        }

        void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var f = Items.SelectedItem as FolderInfo;
            if (f == null) return;
            Items.SelectedItem = null;
            if (f.NoSelect) return;
            if (_pick) AppState.PickedFolder = f;
            else
            {
                AppState.CurrentFolder = f.Name;
                AppState.CurrentFolderLabel = f.Label.Trim();
                AppState.CurrentFolderRole = f.Role;
            }
            if (NavigationService.CanGoBack) NavigationService.GoBack();
        }
    }
}
