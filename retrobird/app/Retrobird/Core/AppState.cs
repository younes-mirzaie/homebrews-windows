// State shared between pages, and the few settings kept in the app's local settings.
using System.Collections.Generic;
using Windows.Storage;

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
        public static bool AccountAdded;   // the provider page skips itself on the way back

        static T Get<T>(string key, T dflt)
        {
            object v;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out v) && v is T ? (T)v : dflt;
        }

        static void Set<T>(string key, T v)
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            if (v == null) values.Remove(key);
            else values[key] = v;
        }

        public static string CurrentAccountId { get { return Get<string>("account", null); } set { Set("account", value); } }
        public static string CurrentFolder { get { return Get("folder", "INBOX"); } set { Set("folder", value); } }
        public static string CurrentFolderLabel { get { return Get("folderLabel", "inbox"); } set { Set("folderLabel", value); } }
        public static string CurrentFolderRole { get { return Get("folderRole", "inbox"); } set { Set("folderRole", value); } }
        /// <summary>Pictures from the web show in mail (never in junk). On by default since 0.7.4; a
        /// new key, so the old default (off) doesn't carry over.</summary>
        public static bool LoadImages { get { return Get("images2", true); } set { Set("images2", value); } }
        /// <summary>A Microsoft app ID typed under server settings (overrides the built-in one).</summary>
        public static string MicrosoftClientId
        {
            get { var v = Get<string>("msClient", null); return string.IsNullOrEmpty(v) ? Provider.MicrosoftClientId : v; }
            set { Set("msClient", string.IsNullOrEmpty(value) || value == Provider.MicrosoftClientId ? null : value); }
        }
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

        /// <summary>The list shows every account's inbox together (only with two or more accounts).
        /// The current account is then the one last opened or written from.</summary>
        public static bool AllInboxes
        {
            get { return Get("allInboxes", false) && Accounts.Count > 1; }
            set { Set("allInboxes", value ? (object)true : null); }
        }

        /// <summary>Shows one account's inbox (leaves all inboxes).</summary>
        public static void SelectAccount(AccountInfo a)
        {
            AllInboxes = false;
            UseAccount(a == null ? null : a.Id);
        }

        /// <summary>Makes this the account messages are read and sent with, inbox selected, without
        /// changing what the list shows.</summary>
        public static void UseAccount(string id)
        {
            CurrentAccountId = id;
            CurrentFolder = "INBOX";
            CurrentFolderLabel = "inbox";
            CurrentFolderRole = "inbox";
        }
    }
}
