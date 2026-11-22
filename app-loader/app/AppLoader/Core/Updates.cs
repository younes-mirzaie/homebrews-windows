// Updates of the apps in the library (the ones installed through Unesbrews). Checked whenever the
// catalog is refreshed: when the app opens or comes back, and on Windows 10 also every few hours
// in the background. With automatic updates on they are installed without asking; with them off
// (the default) a notification says they are ready.
using System;
using System.Collections.Generic;
using System.Linq;

namespace AppLoader.Core
{
    public static class Updates
    {
        /// <summary>Library apps on the phone with a newer version in the catalog. Apps whose
        /// installed version can't be read are left out, so nothing gets reinstalled over and over.</summary>
        public static List<CatalogEntry> Pending(IEnumerable<CatalogEntry> entries)
        {
            var lib = new HashSet<string>(Library.All().Select(a => a.ProductId));
            return entries.Where(e => e.UpdateAvailable && e.InstalledVersion != null && lib.Contains(e.App.ProductId)).ToList();
        }

        /// <summary>After a catalog refresh. With automatic updates on, returns the updates to
        /// install now: all but Unesbrews itself, which asks first because updating it closes it.
        /// The rest are announced with a notification (each update once), unless the app is in
        /// front, where the installed section shows them anyway.</summary>
        public static List<CatalogEntry> Check(IEnumerable<CatalogEntry> entries, bool inFront)
        {
            var pending = Pending(entries);
            bool auto = Prefs.AutoUpdates && Phone.Busy() == null;
            // each update is tried automatically once per run of the app: one that fails waits for the user
            var now = pending.Where(e => auto && e.App.ProductId != Phone.OwnProductId && !_tried.Contains(Key(e))).ToList();
            foreach (var e in now) _tried.Add(Key(e));
            Announce(pending.Where(e => !now.Contains(e)).ToList(), inFront);
            return now;
        }

        static readonly HashSet<string> _tried = new HashSet<string>();
        static string Key(CatalogEntry e) { return e.App.ProductId + "=" + e.App.Latest.Version; }

        static void Announce(List<CatalogEntry> list, bool inFront)
        {
            var keys = list.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var before = new HashSet<string>((Prefs.UpdatesAnnounced ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            bool anyNew = keys.Any(k => !before.Contains(k));
            Prefs.UpdatesAnnounced = string.Join(";", keys.ToArray());
            if (!anyNew || inFront) return;
            if (list.Count == 1)
                Notify.Updates("Update ready: " + list[0].App.Name,
                               list[0].App.Name + " " + list[0].App.Latest.Version + " is ready. Open " + AppInfo.Name + " to update it.");
            else
                Notify.Updates(list.Count + " updates ready",
                               string.Join(", ", list.Select(e => e.App.Name).ToArray()) + ". Open " + AppInfo.Name + " to update them.");
        }
    }
}
