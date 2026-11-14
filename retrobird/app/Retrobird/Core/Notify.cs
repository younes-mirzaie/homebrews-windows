// The live tile, the unread badge and new-mail notifications. Shared by the app (after it checks
// mail itself) and the background task (RetrobirdTasks), which compiles this same file.
using System;
using System.Collections.Generic;
using Windows.Data.Xml.Dom;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Notifications;

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

        static XmlDocument Template(TileTemplateType type, params string[] texts)
        {
            var xml = TileUpdateManager.GetTemplateContent(type);
            var nodes = xml.GetElementsByTagName("text");
            for (int i = 0; i < texts.Length && i < nodes.Length; i++) nodes[i].InnerText = texts[i] ?? "";
            return xml;
        }

        /// <summary>The tile shows the newest unread messages in turn (sender, then subject) and the
        /// unread count; with nothing unread it goes back to the plain logo. The wide tile carries the
        /// same text, which the lock screen shows as its detailed status (newest message last, so
        /// that is what the lock screen shows).</summary>
        public static void UpdateTile(int unread, IList<NewMail> latest)
        {
            try
            {
                var badge = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                if (unread <= 0) badge.Clear();
                else
                {
                    var b = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
                    ((XmlElement)b.SelectSingleNode("/badge")).SetAttribute("value", Math.Min(unread, 99).ToString());
                    badge.Update(new BadgeNotification(b));
                }
                var tile = TileUpdateManager.CreateTileUpdaterForApplication();
                tile.Clear();
                if (unread <= 0 || latest == null || latest.Count == 0) return;
                tile.EnableNotificationQueue(true);
                for (int i = Math.Min(latest.Count, 3) - 1; i >= 0; i--)
                {
                    var m = latest[i];
                    var from = NameOf(m.From);
                    var subject = string.IsNullOrEmpty(m.Subject) ? "(no subject)" : m.Subject;
                    var xml = Template(TileTemplateType.TileWide310x150Text09, from, subject);
                    var square = Template(TileTemplateType.TileSquare150x150Text02, from, subject);
                    var binding = xml.ImportNode(square.GetElementsByTagName("binding")[0], true);
                    xml.GetElementsByTagName("visual")[0].AppendChild(binding);
                    tile.Update(new TileNotification(xml) { Tag = "m" + i });
                }
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
