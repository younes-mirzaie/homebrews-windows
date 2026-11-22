// The choices on the settings page. Kept in a small text file (prefs.txt) in the app's storage,
// not in IsolatedStorageSettings, so the background agent reads the same values as the app.
// Shared with the background agent: only isolated storage here.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;

namespace AppLoader.Core
{
    public static class Prefs
    {
        const string FileName = "prefs.txt";
        static readonly object _lock = new object();

        /// <summary>Notifications for install progress, results and available updates. On by default.</summary>
        public static bool Notifications
        {
            get { return Get("notifications", "on") == "on"; }
            set { Set("notifications", value ? "on" : "off"); }
        }

        /// <summary>Install updates of the apps in the library without asking. Off by default.</summary>
        public static bool AutoUpdates
        {
            get { return Get("autoupdates", "off") == "on"; }
            set { Set("autoupdates", value ? "on" : "off"); }
        }

        /// <summary>The updates the last "updates available" notification was about, so the same
        /// ones aren't announced again.</summary>
        public static string UpdatesAnnounced
        {
            get { return Get("announced", ""); }
            set { Set("announced", value ?? ""); }
        }

        static string Get(string key, string fallback)
        {
            string v;
            return Load().TryGetValue(key, out v) ? v : fallback;
        }

        static void Set(string key, string value)
        {
            lock (_lock)
            {
                var all = Load();
                all[key] = value;
                var b = new StringBuilder();
                foreach (var kv in all) b.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                try
                {
                    using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    using (var w = new StreamWriter(iso.CreateFile(FileName), new UTF8Encoding(false)))
                        w.Write(b.ToString());
                }
                catch (Exception) { }
            }
        }

        static Dictionary<string, string> Load()
        {
            var all = new Dictionary<string, string>();
            lock (_lock)
            {
                try
                {
                    using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    {
                        if (!iso.FileExists(FileName)) return all;
                        using (var r = new StreamReader(iso.OpenFile(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                            foreach (var raw in r.ReadToEnd().Split('\n'))
                            {
                                int eq = raw.IndexOf('=');
                                if (eq > 0) all[raw.Substring(0, eq).Trim()] = raw.Substring(eq + 1).Trim();
                            }
                    }
                }
                catch (Exception) { }
            }
            return all;
        }
    }
}
