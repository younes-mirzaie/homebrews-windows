// Notifications for installs and downloads that run while the app is minimized or the phone is
// locked: one entry in the action center that updates in place (silently) with the progress, then
// pops up once with the result. A second entry says when updates are available. All of it can be
// turned off on the settings page (Prefs.Notifications). Shared with the background agent.
// Needs ToastCapable="true".
using System;
using System.IO.IsolatedStorage;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AppLoader.Core
{
    public static class Notify
    {
        const string Tag = "job", Group = "unesbrews";
        static string _last;

        /// <summary>Updates the progress entry without a popup or sound (only when the text changed).</summary>
        public static void Progress(string text)
        {
            if (text == _last) return;
            _last = text;
            if (!Prefs.Notifications) return;
            Show(AppInfo.Name, text, true, Tag);
        }

        /// <summary>The result, with a popup: once per run, whoever (app or agent) gets there first.</summary>
        public static void Result(string run, string title, string text)
        {
            string marker = "notified-" + run;
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (iso.FileExists(marker)) return;
                    iso.CreateFile(marker).Dispose();
                }
            }
            catch (Exception) { return; }
            if (!Prefs.Notifications) return;
            Show(title, text, false, Tag);
        }

        /// <summary>"Updates are available" (when automatic updates are off), with a popup.</summary>
        public static void Updates(string title, string text)
        {
            if (!Prefs.Notifications) return;
            Show(title, text, false, "updates");
        }

        static void Show(string title, string text, bool silent, string tag)
        {
            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var t = xml.GetElementsByTagName("text");
                t[0].AppendChild(xml.CreateTextNode(title));
                t[1].AppendChild(xml.CreateTextNode(text));
                var toast = new ToastNotification(xml) { Tag = tag, Group = Group, SuppressPopup = silent };
                ToastNotificationManager.CreateToastNotifier().Show(toast);
            }
            catch (Exception) { }   // notifications are a nicety: never break an install over one
        }

        /// <summary>Takes the progress entry out of the action center.</summary>
        public static void ClearProgress()
        {
            _last = null;
            try { ToastNotificationManager.History.Remove(Tag, Group); } catch (Exception) { }
        }
    }
}
