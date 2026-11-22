// The live tile, the unread badge and new-mail notifications. Shared by the app (after it checks
// mail itself) and the background task (RetrobirdTasks), which compiles this same file.
using System;
using System.Collections.Generic;
using Windows.Data.Xml.Dom;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Notifications;
using Windows.UI.StartScreen;
using System.Text;

namespace Retrobird.Core
{
    internal sealed class NewMail
    {
        public string AccountId, From, Subject;
        public uint Uid;
        public long Date;   // Unix seconds
    }

    internal static class Notify
    {
        static IPropertySet Settings { get { return ApplicationData.Current.LocalSettings.Values; } }

        static T Get<T>(string key, T dflt)
        {
            object v;
            return Settings.TryGetValue(key, out v) && v is T ? (T)v : dflt;
        }

        /// <summary>Notifications for new mail (asked for by the user, so on unless turned off).</summary>
        public static bool Enabled { get { return Get("notify", true); } set { Settings["notify"] = value; } }

        /// <summary>How often the background task checks: 30 or 60 minutes, 0 for never.</summary>
        public static int BackgroundMinutes { get { return Get("bgMinutes", 30); } set { Settings["bgMinutes"] = value; } }

        /// <summary>The highest message number already announced (or seen in the app) for an account.</summary>
        public static uint LastNotified(string account) { return Get("notified_" + account, 0u); }
        public static bool HasLastNotified(string account) { return Settings.ContainsKey("notified_" + account); }
        public static void SetLastNotified(string account, uint uid)
        {
            if (uid > LastNotified(account) || !HasLastNotified(account)) Settings["notified_" + account] = uid;
        }

        /// <summary>"Ann Example &lt;ann@x.org&gt;" -> "Ann Example".</summary>
        public static string NameOf(string from)
        {
            from = (from ?? "").Trim();
            int lt = from.LastIndexOf('<');
            if (lt > 0)
            {
                var n = from.Substring(0, lt).Trim().Trim('"').Trim();
                if (n.Length > 0) return n;
            }
            int gt = from.LastIndexOf('>');
            if (lt >= 0 && gt > lt) return from.Substring(lt + 1, gt - lt - 1).Trim();
            return from.Length > 0 ? from : "(unknown sender)";
        }

        /// <summary>Show sender and subject on the tile and the lock screen (off: only the count).</summary>
        public static bool TileDetails { get { return Get("tileDetails", true); } set { Settings["tileDetails"] = value; } }

        /// <summary>The account's short name ("Gmail"), kept so the background task can label mail.</summary>
        public static void RememberAccount(string id, string shortName) { if (id != null) Settings["acctname_" + id] = shortName ?? ""; }
        static string AccountName(string id) { return id == null ? "" : Get("acctname_" + id, ""); }

        /// <summary>The Start tile id of an account's pinned inbox.</summary>
        public static string AccountTileId(string account)
        {
            var b = new StringBuilder("inbox_");
            foreach (var c in account ?? "") b.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '_');
            var id = b.ToString();
            return id.Length > 64 ? id.Substring(0, 64) : id;
        }

        static string X(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        const string Icon = "ms-appx:///Assets/TileIcon.png";

        static string Line3(NewMail m)
        {
            var name = AccountName(m.AccountId);
            var when = m.Date > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(m.Date).ToLocalTime().ToString("t") : "";
            return name.Length > 0 && when.Length > 0 ? name + " \u00B7 " + when : name + when;
        }

        static string Subject(NewMail m) { return string.IsNullOrEmpty(m.Subject) ? "(no subject)" : m.Subject; }

        /// <summary>One tile notification for the newest unread mail at index i (the queue cycles
        /// through them). Without details it only says how many there are.</summary>
        static string TileXml(IList<NewMail> mail, int i, int unread, bool details)
        {
            var m = mail[i];
            string from = details ? NameOf(m.From) : unread + (unread == 1 ? " unread message" : " unread messages");
            string subject = details ? Subject(m) : "";
            string line3 = details ? Line3(m) : "";
#if WINDOWS_UWP
            // Windows 10: adaptive tiles; the system draws the name bottom left and the badge bottom right
            var x = new StringBuilder("<tile><visual branding=\"name\">");
            if (details)
            {
                x.Append("<binding template=\"TileMedium\"><text hint-style=\"base\">" + X(from) + "</text>" +
                         "<text hint-style=\"caption\" hint-wrap=\"true\" hint-maxLines=\"2\">" + X(subject) + "</text>" +
                         "<text hint-style=\"captionSubtle\">" + X(line3) + "</text></binding>");
                x.Append("<binding template=\"TileWide\"><text hint-style=\"base\">" + X(from) + "</text>" +
                         "<text hint-style=\"caption\">" + X(subject) + "</text>" +
                         "<text hint-style=\"captionSubtle\">" + X(line3) + "</text></binding>");
                x.Append("<binding template=\"TileLarge\">");
                for (int k = i; k < mail.Count && k < i + 3; k++)
                    x.Append("<group><subgroup><text hint-style=\"base\">" + X(NameOf(mail[k].From)) + "</text>" +
                             "<text hint-style=\"caption\">" + X(Subject(mail[k])) + "</text>" +
                             "<text hint-style=\"captionSubtle\">" + X(Line3(mail[k])) + "</text></subgroup></group>" +
                             (k + 1 < mail.Count && k + 1 < i + 3 ? "<text />" : ""));
                x.Append("</binding>");
            }
            else
            {
                foreach (var t in new[] { "TileMedium", "TileWide", "TileLarge" })
                    x.Append("<binding template=\"" + t + "\"><text hint-style=\"base\" hint-wrap=\"true\">" + X(from) + "</text></binding>");
            }
            x.Append("</visual></tile>");
            return x.ToString();
#else
            // Windows Phone 8.1: the phone's own icon templates, the bird with the count beside it
            // (as the system Mail tile); the wide tile adds sender and subject, which the lock
            // screen shows as its detailed status
            return "<tile><visual version=\"2\">" +
                   "<binding template=\"TileSquare71x71IconWithBadge\"><image id=\"1\" src=\"" + Icon + "\"/></binding>" +
                   "<binding template=\"TileSquare150x150IconWithBadge\"><image id=\"1\" src=\"" + Icon + "\"/></binding>" +
                   "<binding template=\"TileWide310x150IconWithBadgeAndText\"><image id=\"1\" src=\"" + Icon + "\"/>" +
                   "<text id=\"1\">" + X(from) + "</text><text id=\"2\">" + X(subject) + "</text><text id=\"3\">" + X(line3) + "</text></binding>" +
                   "</visual></tile>";
#endif
        }

        static void Fill(TileUpdater tile, BadgeUpdater badge, int unread, IList<NewMail> latest)
        {
            if (unread <= 0) badge.Clear();
            else
            {
                var b = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
                ((XmlElement)b.SelectSingleNode("/badge")).SetAttribute("value", Math.Min(unread, 99).ToString());
                badge.Update(new BadgeNotification(b));
            }
            tile.Clear();
            if (unread <= 0) return;   // back to the plain bird
            var mail = latest == null || latest.Count == 0
                ? new List<NewMail> { new NewMail() }
                : latest;
            bool details = TileDetails && latest != null && latest.Count > 0;
            tile.EnableNotificationQueue(true);
            int n = details ? Math.Min(mail.Count, 5) : 1;
            // newest last, so it is the one shown first and on the lock screen
            for (int i = n - 1; i >= 0; i--)
            {
                var xml = new XmlDocument();
                xml.LoadXml(TileXml(mail, i, unread, details));
                tile.Update(new TileNotification(xml) { Tag = "m" + i });
            }
        }

        /// <summary>The app's tile: the unread count of every account and the newest unread mail
        /// in turn; with nothing unread it goes back to the plain bird.</summary>
        public static void UpdateTile(int unread, IList<NewMail> latest)
        {
            try { Fill(TileUpdateManager.CreateTileUpdaterForApplication(), BadgeUpdateManager.CreateBadgeUpdaterForApplication(), unread, latest); }
            catch (Exception) { }
        }

        /// <summary>The same for an account's inbox pinned to Start, if it is pinned.</summary>
        public static void UpdateAccountTile(string account, int unread, IList<NewMail> latest)
        {
            try
            {
                var id = AccountTileId(account);
                if (!SecondaryTile.Exists(id)) return;
                Fill(TileUpdateManager.CreateTileUpdaterForSecondaryTile(id), BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(id), unread, latest);
            }
            catch (Exception) { }
        }

        /// <summary>One notification per new message (sender and subject); more than three become
        /// one "N new messages". Tapping it opens that account's inbox.</summary>
        public static void Toast(IList<NewMail> fresh, string accountEmail)
        {
            if (!Enabled || fresh == null || fresh.Count == 0) return;
            try
            {
                var notifier = ToastNotificationManager.CreateToastNotifier();
                if (fresh.Count > 3)
                {
                    notifier.Show(MakeToast(fresh.Count + " new messages", accountEmail, fresh[0].AccountId));
                    return;
                }
                foreach (var m in fresh)
                    notifier.Show(MakeToast(NameOf(m.From), string.IsNullOrEmpty(m.Subject) ? "(no subject)" : m.Subject, m.AccountId));
            }
            catch (Exception) { }
        }

        static ToastNotification MakeToast(string title, string text, string account)
        {
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var nodes = xml.GetElementsByTagName("text");
            nodes[0].InnerText = title ?? "";
            nodes[1].InnerText = text ?? "";
            ((XmlElement)xml.SelectSingleNode("/toast")).SetAttribute("launch", "account=" + Uri.EscapeDataString(account ?? ""));
            return new ToastNotification(xml);
        }

        /// <summary>The app says when it's on screen, so the background task leaves the mail alone
        /// while the app's own engine works on it.</summary>
        static long Now { get { return ToUnix(DateTime.UtcNow); } }

        public static long ToUnix(DateTime d)
        {
            if (d == DateTime.MinValue) return 0;
            return (long)(d.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static void AppVisible(bool visible)
        {
            Settings["appVisible"] = visible;
            Settings["appSeen"] = Now;
        }

        public static bool AppProbablyOpen
        {
            get
            {
                // a crash can leave "visible" set: trust it for ten minutes only
                return Get("appVisible", false) && Now - Get("appSeen", 0L) < 600;
            }
        }
    }
}
