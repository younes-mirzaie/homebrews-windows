// The message list the app shows: one account's folder, or every account's inbox merged ("all
// inboxes"). Loads from the engine's cache, checks the servers, pages in older mail and keeps the
// tile in step. The pages only lay it out. Shared by the Windows Phone 8.1 and Windows 10 apps.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Retrobird.Core
{
    public enum MailFilter { All, Unread, Flagged }

    public sealed class InboxView
    {
        public List<MessageSummary> Items = new List<MessageSummary>();
        public bool More;                  // older mail exists (cached or on the server)
        public List<string> Problems = new List<string>();
        public bool Empty { get { return Items.Count == 0; } }
    }

    /// <summary>The messages of one day group ("today", "yesterday"...), for a grouped list.</summary>
    public sealed class MailGroup : List<MessageSummary>
    {
        public string Key { get; set; }
    }

    public static class Inbox
    {
        public const int PageSize = 50;

        /// <summary>Account + folder the list shows; a change means a fresh list.</summary>
        public static string Key()
        {
            if (AppState.AllInboxes) return "*/INBOX";
            var acct = AppState.CurrentAccount;
            return acct == null ? "" : acct.Id + "/" + AppState.CurrentFolder;
        }

        public static bool IsInbox { get { return AppState.AllInboxes || AppState.CurrentFolderRole == "inbox"; } }

        /// <summary>What the list shows, lowercase: "all inboxes", "inbox", "sent"...</summary>
        public static string FolderTitle { get { return AppState.AllInboxes ? "all inboxes" : AppState.CurrentFolderLabel; } }

        /// <summary>Whose list it is: "Gmail", or "" for all inboxes.</summary>
        public static string AccountTitle
        {
            get
            {
                var a = AppState.CurrentAccount;
                return AppState.AllInboxes || a == null ? "" : a.ShortName;
            }
        }

        public static int TotalUnread { get { return AppState.Accounts.Sum(a => Math.Max(0, a.Unseen)); } }

        public static IEnumerable<MessageSummary> Filter(IEnumerable<MessageSummary> items, MailFilter f)
        {
            switch (f)
            {
                case MailFilter.Unread: return items.Where(m => m.IsUnread);
                case MailFilter.Flagged: return items.Where(m => m.IsFlagged);
                default: return items;
            }
        }

        /// <summary>Messages grouped by day, newest first (the list is already newest first).
        /// Windows 10 shows the group names in sentence case.</summary>
        public static List<MailGroup> Group(IEnumerable<MessageSummary> items, bool sentenceCase)
        {
            var groups = new List<MailGroup>();
            MailGroup g = null;
            foreach (var m in items)
            {
                var key = m.DayGroup;
                if (sentenceCase) key = Format.Sentence(key);
                if (g == null || g.Key != key)
                {
                    g = groups.FirstOrDefault(x => x.Key == key);
                    if (g == null) { g = new MailGroup { Key = key }; groups.Add(g); }
                }
                g.Add(m);
            }
            return groups;
        }

        /// <summary>The list from the phone's cache (no network).</summary>
        public static async Task<InboxView> LoadAsync(int shown)
        {
            var r = new InboxView();
            if (AppState.AllInboxes)
            {
                var all = new List<MessageSummary>();
                foreach (var a in AppState.Accounts.ToList())
                {
                    try
                    {
                        var list = await AppState.Client.ListAsync(a.Id, "INBOX", 0, shown);
                        foreach (var m in list.Items)
                        {
                            m.AccountId = a.Id;
                            m.AccountLabel = a.ShortName;
                            all.Add(m);
                        }
                        if (list.Cached > list.Items.Count || list.Exists > list.Cached) r.More = true;
                    }
                    catch (Exception ex) { r.Problems.Add(a.ShortName + ": " + Describe(ex)); }
                }
                r.Items = all.OrderByDescending(m => m.Date).Take(shown).ToList();
                if (all.Count > r.Items.Count) r.More = true;
                return r;
            }
            var acct = AppState.CurrentAccount;
            if (acct == null) return r;
            try
            {
                var list = await AppState.Client.ListAsync(acct.Id, AppState.CurrentFolder, 0, shown);
                bool showTo = AppState.CurrentFolderRole == "sent" || AppState.CurrentFolderRole == "drafts";
                foreach (var m in list.Items)
                {
                    m.ShowRecipient = showTo;
                    m.AccountId = acct.Id;
                    r.Items.Add(m);
                }
                r.More = list.Cached > r.Items.Count || list.Exists > list.Cached;
            }
            catch (Exception ex) { r.Problems.Add(Describe(ex)); }
            return r;
        }

        /// <summary>Checks the server(s) for the list being shown; returns what went wrong, if anything.</summary>
        public static async Task<List<string>> SyncAsync()
        {
            var problems = new List<string>();
            if (AppState.AllInboxes)
            {
                foreach (var a in AppState.Accounts.ToList())
                {
                    try { await AppState.Client.SyncAsync(a.Id, "INBOX"); }
                    catch (Exception ex) { problems.Add(a.ShortName + ": " + Describe(ex)); }
                }
            }
            else
            {
                var acct = AppState.CurrentAccount;
                if (acct == null) return problems;
                try { await AppState.Client.SyncAsync(acct.Id, AppState.CurrentFolder); }
                catch (Exception ex) { problems.Add(Describe(ex)); }
            }
            try { AppState.Accounts = await AppState.Client.GetAccountsAsync(); } catch (Exception) { }
            return problems;
        }

        /// <summary>Fetches older messages from the server(s) until the list can show this many.</summary>
        public static async Task MoreAsync(int shown)
        {
            if (AppState.AllInboxes)
            {
                foreach (var a in AppState.Accounts.ToList())
                {
                    var list = await AppState.Client.ListAsync(a.Id, "INBOX", 0, 0);
                    if (list.Cached < shown && list.Exists > list.Cached)
                        await AppState.Client.MoreAsync(a.Id, "INBOX", shown - list.Cached);
                }
                return;
            }
            var acct = AppState.CurrentAccount;
            if (acct == null) return;
            var l = await AppState.Client.ListAsync(acct.Id, AppState.CurrentFolder, 0, 0);
            if (l.Cached < shown) await AppState.Client.MoreAsync(acct.Id, AppState.CurrentFolder, shown - l.Cached);
        }

        /// <summary>After the app showed an inbox: the tiles show the unread count and the newest
        /// unread mail, and mail seen here isn't announced again by the background check.</summary>
        public static void UpdateTiles(IList<MessageSummary> items)
        {
            if (!IsInbox) return;
            var newest = items.Where(m => m.IsUnread).Take(5).Select(m => new NewMail
            {
                AccountId = m.AccountId, Uid = m.Uid, Date = Notify.ToUnix(m.Date), From = m.From, Subject = m.Subject
            }).ToList();
            if (AppState.AllInboxes)
            {
                Notify.UpdateTile(TotalUnread, newest);
                foreach (var a in AppState.Accounts)
                    Notify.UpdateAccountTile(a.Id, a.Unseen, newest.Where(m => m.AccountId == a.Id).ToList());
            }
            else
            {
                var acct = AppState.CurrentAccount;
                if (acct == null) return;
                // the main tile counts every account; only this account's newest mail is known here
                Notify.UpdateTile(TotalUnread, newest);
                Notify.UpdateAccountTile(acct.Id, acct.Unseen, newest);
            }
            foreach (var g in items.Where(m => m.AccountId != null).GroupBy(m => m.AccountId))
                Notify.SetLastNotified(g.Key, g.Max(m => m.Uid));
        }

        /// <summary>A failure in plain words.</summary>
        public static string Describe(Exception e)
        {
            var m = e as MailException;
            if (m == null) return e.Message;
            if (m.Message.StartsWith("Retrobird")) return m.Message;   // already a full sentence about the app itself
            switch (m.Code)
            {
                case "AUTH": return "Couldn't sign in: " + m.Message;
                case "NET": return "Can't connect: " + m.Message;
                default: return m.Message;
            }
        }

        // ---------------- several messages at once (selection mode) ----------------

        /// <summary>Runs an action on each selected message, each with its own account in all
        /// inboxes. Returns the first failure in plain words, or null.</summary>
        public static async Task<string> ForEachAsync(IEnumerable<MessageSummary> items, Func<string, string, MessageSummary, Task> action)
        {
            string problem = null;
            foreach (var m in items.ToList())
            {
                var acct = m.AccountId ?? AppState.CurrentAccountId;
                var folder = AppState.AllInboxes ? "INBOX" : AppState.CurrentFolder;
                try { await action(acct, folder, m); }
                catch (Exception ex) { if (problem == null) problem = Describe(ex); }
            }
            return problem;
        }
    }
}
