// The account's folders with unread counts. In pick mode (?pick=1) it chooses a target folder
// for "move to..." instead of switching the list.
using System;
using System.Linq;
using Retrobird.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class FoldersPage : RbPage
    {
        bool _pick, _loaded;

        public FoldersPage() { InitializeComponent(); }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_loaded) return;
            _loaded = true;
            string p;
            _pick = Query.TryGetValue("pick", out p) && p == "1";
            var acct = AppState.CurrentAccount;
            if (acct == null) return;
            AccountText.Text = "RETROBIRD · " + acct.Title.ToUpperInvariant();
            TitleText.Text = _pick ? "move to" : "folders";
            Busy("getting folders");
            try
            {
                var list = await AppState.Client.GetFoldersAsync(acct.Id);
                Items.ItemsSource = _pick ? list.Folders.Where(f => !f.NoSelect && f.Name != AppState.CurrentFolder).ToList() : list.Folders;
                if (!list.Online)
                {
                    StatusText.Text = "Can't reach the server, so these are the folders saved on the phone. " + list.Error;
                    StatusText.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = Ui.Describe(ex);
                StatusText.Visibility = Visibility.Visible;
            }
            finally { Busy(null); }
        }

        void Items_ItemClick(object sender, ItemClickEventArgs e)
        {
            var f = e.ClickedItem as FolderInfo;
            if (f == null || f.NoSelect) return;
            if (_pick) AppState.PickedFolder = f;
            else
            {
                AppState.AllInboxes = false;   // a folder of one account
                AppState.CurrentFolder = f.Name;
                AppState.CurrentFolderLabel = f.Label.Trim();
                AppState.CurrentFolderRole = f.Role;
            }
            GoBack();
        }
    }
}
