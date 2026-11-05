// State shared between pages, and the few settings kept in isolated storage.
using System.Collections.Generic;
using System.IO.IsolatedStorage;

namespace Retrobird.Core
{
    public static class AppState
    {
        public static MailClient Client;
        public static List<AccountInfo> Accounts = new List<AccountInfo>();

        /// <summary>Set by a page before navigating to ComposePage.</summary>
        public static Draft PendingDraft;
        /// <summary>Set by FoldersPage in pick mode; read by the page that asked.</summary>
        public static FolderInfo PickedFolder;
        /// <summary>Set when a page changed messages so the list should reload from the cache.</summary>
        public static bool ListDirty;

        static T Get<T>(string key, T dflt)
        {
            T v;
            return IsolatedStorageSettings.ApplicationSettings.TryGetValue(key, out v) ? v : dflt;
        }

        static void Set<T>(string key, T v)
        {
            IsolatedStorageSettings.ApplicationSettings[key] = v;
            IsolatedStorageSettings.ApplicationSettings.Save();
        }

        public static string CurrentAccountId { get { return Get<string>("account", null); } set { Set("account", value); } }
        public static string CurrentFolder { get { return Get("folder", "INBOX"); } set { Set("folder", value); } }
        public static string CurrentFolderLabel { get { return Get("folderLabel", "inbox"); } set { Set("folderLabel", value); } }
        public static string CurrentFolderRole { get { return Get("folderRole", "inbox"); } set { Set("folderRole", value); } }
        public static bool LoadImages { get { return Get("images", false); } set { Set("images", value); } }
        public static int SyncMinutes { get { return Get("syncMinutes", 15); } set { Set("syncMinutes", value); } }

        public static AccountInfo CurrentAccount
        {
            get
            {
                var id = CurrentAccountId;
                foreach (var a in Accounts) if (a.Id == id) return a;
                return Accounts.Count > 0 ? Accounts[0] : null;
            }
        }

        public static void SelectAccount(AccountInfo a)
        {
            CurrentAccountId = a == null ? null : a.Id;
            CurrentFolder = "INBOX";
            CurrentFolderLabel = "inbox";
            CurrentFolderRole = "inbox";
        }
    }
}
