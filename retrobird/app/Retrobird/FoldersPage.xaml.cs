// The folders of every account (all inboxes) or of the current one, with unread counts. In pick
// mode (?pick=1) it chooses where "move" puts a message instead of switching the list.
using System;
using System.Collections.Generic;
using System.Linq;
using Retrobird.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    /// <summary>A folder with the account it belongs to.</summary>
    public sealed class FolderRow
    {
        public AccountInfo Account { get; set; }
        public FolderInfo Folder { get; set; }
    }

    public sealed class FolderGroup : List<FolderRow>
    {
        public string Key { get; set; }
    }

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
            var current = AppState.CurrentAccount;
            if (current == null) return;
            var accounts = _pick || !AppState.AllInboxes ? new List<AccountInfo> { current } : AppState.Accounts.ToList();
            AccountText.Text = accounts.Count == 1 ? accounts[0].ShortName.ToUpperInvariant() : "RETROBIRD";
            TitleText.Text = _pick ? "move to" : "folders";
            Busy("getting folders");
            var groups = new List<FolderGroup>();
            var problems = new List<string>();
            foreach (var a in accounts)
            {
                try
                {
                    var list = await AppState.Client.GetFoldersAsync(a.Id);
                    var g = new FolderGroup { Key = accounts.Count > 1 ? a.ShortName.ToLowerInvariant() : "" };
                    foreach (var f in list.Folders)
                    {
                        if (_pick && (f.NoSelect || f.Name == AppState.CurrentFolder)) continue;
                        g.Add(new FolderRow { Account = a, Folder = f });
                    }
                    groups.Add(g);
                    if (!list.Online) problems.Add("Can't reach " + a.ShortName + ", so these are its folders saved on the phone. " + list.Error);
                }
                catch (Exception ex) { problems.Add(a.ShortName + ": " + Ui.Describe(ex)); }
            }
            Busy(null);
            ((CollectionViewSource)Resources["FolderSource"]).Source = groups;
            if (problems.Count > 0)
            {
                StatusText.Text = string.Join("\n", problems);
                StatusText.Visibility = Visibility.Visible;
            }
        }

        void Items_ItemClick(object sender, ItemClickEventArgs e)
        {
            var r = e.ClickedItem as FolderRow;
            if (r == null || r.Folder.NoSelect) return;
            if (_pick) AppState.PickedFolder = r.Folder;
            else
            {
                AppState.AllInboxes = false;   // a folder of one account
                AppState.CurrentAccountId = r.Account.Id;
                AppState.CurrentFolder = r.Folder.Name;
                AppState.CurrentFolderLabel = r.Folder.Label.Trim();
                AppState.CurrentFolderRole = r.Folder.Role;
            }
            GoBack();
        }
    }
}
