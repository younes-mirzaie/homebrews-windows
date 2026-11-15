// What's on the phone (packages waiting in Downloads, installed apps) and the install decisions.
//
// Rules (learned the hard way: CMD Injector disappeared after a refused update):
//  - An app that is installed gets an in-place update (-uda); one that isn't gets a deploy (-di).
//  - Never uninstall, not even as a fallback.
//  - A package that refuses the SD card is not installed over a copy that lives on the SD card:
//    the phone refuses that update and the app can be lost. The user moves the app first.
//  - The package is copied to a plain path in the app's own folder first (ASCII, no spaces), and
//    checked and installed from there, so the checks look at exactly what gets installed.
//  - One install at a time: a new one waits until the last one's done marker is there.
//  - TH runs detached and the install is remembered (settings "job"), so it survives the app
//    being suspended or updating itself, and the result is shown when the app comes back.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AppLoader.Catalog;

namespace AppLoader.Core
{
    public sealed class FoundPackage
    {
        public string Path;
        public long Size;
        public string Name { get { return System.IO.Path.GetFileName(Path); } }
        public string Folder { get { return System.IO.Path.GetDirectoryName(Path); } }
    }

    public sealed class InstalledApp
    {
        public string Title;
        public string ProductId;    // upper case, no braces
        public string Location;     // "SD" or "Internal"
        public string InstallType;
        public bool OnSd { get { return string.Equals(Location, "SD", StringComparison.OrdinalIgnoreCase); } }
    }

    public sealed class InstallOutcome
    {
        public bool Started;        // the job is running (Job), the result comes from FinishAsync
        public bool StillRunning;   // FinishAsync gave up waiting; the job is still remembered
        public bool Succeeded;
        public string Message;      // plain words for the user
        public PackageInfo Package;
        public InstallJob Job;
        public bool HashMismatch;   // a catalog download didn't match its SHA-256
    }

    public static class Phone
    {
        // App Loader's own product ID: updating it ends this app while TH runs
        public const string OwnProductId = "0145BB18-825E-4266-9094-5CA73FEF56A9";

        // Where downloads and copied files usually land
        static readonly string[] Roots =
        {
            @"D:\Downloads", @"C:\Data\Users\Public\Downloads",
            @"D:\Documents", @"C:\Data\Users\Public\Documents",
        };

        public static async Task<List<FoundPackage>> FindPackagesAsync()
        {
            var cmds = new StringBuilder();
            foreach (var root in Roots)
                cmds.Append("if exist " + Shell.Quote(root) + " for /r " + Shell.Quote(root) +
                            " %%f in (*.xap *.appx) do echo pkg^|%%~zf^|%%f\n");
            cmds.Append("for %%f in (D:\\*.xap D:\\*.appx C:\\Data\\*.xap C:\\Data\\*.appx) do echo pkg^|%%~zf^|%%f\n");
            var r = await Shell.RunAsync(cmds.ToString(), 60);
            if (!r.Reached) throw new IOException(r.Problem ?? Friendly.Slow);
            var list = new List<FoundPackage>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in r.Output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("pkg|", StringComparison.Ordinal)) continue;
                var parts = line.Split(new[] { '|' }, 3);
                long size;
                if (parts.Length < 3 || !long.TryParse(parts[1], out size)) continue;
                if (!seen.Add(parts[2])) continue;
                list.Add(new FoundPackage { Path = parts[2], Size = size });
            }
            return list;
        }

        /// <summary>The installed apps, from the list TH.exe prints with its help.</summary>
        public static async Task<List<InstalledApp>> InstalledAppsAsync()
        {
            var r = await Shell.RunAsync("TH.exe -?", 90);
            if (!r.Reached) throw new IOException(r.Problem ?? Friendly.Slow);
            var apps = new Dictionary<string, InstalledApp>();
            var order = new List<string>();
            foreach (var raw in r.Output.Split('\n'))
            {
                var m = Regex.Match(raw, @"^\s*\[(\d+)\]\s*(\w+)\s*:\s*(.*?)\s*$");
                if (!m.Success) continue;
                string n = m.Groups[1].Value, key = m.Groups[2].Value, val = m.Groups[3].Value;
                InstalledApp a;
                if (!apps.TryGetValue(n, out a)) { a = new InstalledApp(); apps[n] = a; order.Add(n); }
                if (key == "AppTitle") a.Title = val;
                else if (key == "ProductID") a.ProductId = PackageInfo.Guid(val);
                else if (key == "InstallLocation") a.Location = val;
                else if (key == "InstallType") a.InstallType = val;
            }
            var list = new List<InstalledApp>();
            foreach (var n in order)
                if (apps[n].ProductId != null) list.Add(apps[n]);
            return list;
        }

        public static Task<InstallOutcome> InstallAsync(FoundPackage found, Action<string> status)
        {
            return InstallAsync(found, status, null, null);
        }

        /// <summary>Copies the package into the app's folder under a plain name, reads and checks
        /// it there, then starts TH: update if the app is installed, deploy if not. For a package
        /// from a catalog, expectedProductId and expectedSha256 (hex) must match the staged copy.
        /// Returns with Started set once TH is running; FinishAsync gives the result.</summary>
        public static async Task<InstallOutcome> InstallAsync(FoundPackage found, Action<string> status,
                                                              string expectedProductId, string expectedSha256)
        {
            var outcome = new InstallOutcome();
            var busy = Busy();
            if (busy != null) { outcome.Message = busy; return outcome; }

            string ext = found.Path.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) ? ".appx" : ".xap";
            string stageName = "staged-" + DateTime.UtcNow.Ticks + ext;
            string staged = Shell.LocalPath + "\\" + stageName;
            if (!IsPlain(staged))
            {
                AppLog.Add("unusable app folder", staged);
                outcome.Message = AppInfo.Name + "'s storage folder has an unusual name, so it can't hand the package to the installer safely. Reinstalling " + AppInfo.Name + " may help. " + Friendly.Unchanged;
                return outcome;
            }
            try
            {
                status("copying " + found.Name + "...");
                var copy = await Shell.RunAsync("copy /y " + Shell.Quote(found.Path) + " " + Shell.Quote(staged), 120);
                bool exists;
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication()) exists = iso.FileExists(stageName);
                if (!copy.Reached || !exists)
                {
                    AppLog.Add("copy failed: " + found.Path, copy.Problem ?? copy.Output);
                    outcome.Message = copy.Problem == Friendly.NoShell ? Friendly.NoShell
                        : "Couldn't make a working copy of " + found.Name + ". The phone may be out of storage space, or the file was moved. " + Friendly.Unchanged;
                    return outcome;
                }

                status("reading the package...");
                try
                {
                    using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    using (var s = iso.OpenFile(stageName, FileMode.Open, FileAccess.Read))
                    {
                        if (expectedSha256 != null)
                        {
                            string hash = Sha256Hex(s);
                            if (!string.Equals(hash, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                AppLog.Add("SHA-256 mismatch: " + found.Name, "expected " + expectedSha256 + "\ngot " + hash);
                                outcome.Message = "The downloaded file isn't exactly the one the catalog lists, so it was thrown away. " +
                                                  "Try again. " + Friendly.Unchanged;
                                outcome.HashMismatch = true;
                                return outcome;
                            }
                            s.Seek(0, SeekOrigin.Begin);
                        }
                        outcome.Package = PackageInfo.Read(s);
                    }
                }
                catch (Exception e)
                {
                    AppLog.Add("unreadable package: " + found.Name, e.Message);
                    outcome.Message = found.Name + " isn't an app package this phone can install (it may be damaged, or made for a different phone). " + Friendly.Unchanged;
                    return outcome;
                }
                var pkg = outcome.Package;
                if (pkg.ProductId == null)
                {
                    outcome.Message = found.Name + " doesn't say which app it is, so it can't be installed safely. " + Friendly.Unchanged;
                    return outcome;
                }
                if (expectedProductId != null && PackageInfo.Guid(expectedProductId) != pkg.ProductId)
                {
                    AppLog.Add("product ID mismatch: " + found.Name, "expected " + expectedProductId + "\ngot " + pkg.ProductId);
                    outcome.Message = "The downloaded file is a different app (" + pkg.Title + ") than the catalog says, " +
                                      "so it wasn't installed. " + Friendly.Unchanged;
                    return outcome;
                }

                status("checking what's installed...");
                InstalledApp existing = null;
                foreach (var a in await InstalledAppsAsync())
                    if (a.ProductId == pkg.ProductId) { existing = a; break; }

                if (existing != null && existing.OnSd && pkg.SdOptOut)
                {
                    outcome.Message = pkg.Title + " " + pkg.Version + " can't be installed on the SD card, and the " +
                        existing.Title + " you have now is on the SD card. The phone would refuse this update, and an app " +
                        "can get lost that way. Move " + existing.Title + " to the phone first (Settings > storage sense > " +
                        "SD card > apps+games > select it > move to phone), then try again. Nothing was changed.";
                    return outcome;
                }

                string existingKind = null, existingVersion = null;
                if (existing != null)
                {
                    try
                    {
                        CatalogService.InstalledCopy have;
                        if ((await CatalogService.InstalledAsync(new List<string> { pkg.ProductId })).TryGetValue(pkg.ProductId, out have))
                        {
                            existingKind = have.Kind;
                            existingVersion = have.Version;
                        }
                    }
                    catch (Exception) { }
                }

                // The phone only updates an appx app to a higher version (the same version fails
                // with 0x81030137). Uninstalling first would lose the app's data, so it isn't tried.
                if (pkg.Kind == "appx" && existingVersion != null && pkg.Version != null)
                {
                    int cmp;
                    try { cmp = AppLoader.Catalog.CatalogIndex.CompareVersions(pkg.Version, existingVersion); }
                    catch (Exception) { cmp = 1; }
                    if (cmp <= 0)
                    {
                        outcome.Message = cmp == 0
                            ? pkg.Title + " " + pkg.Version + " is already installed, so there's nothing to update. " + Friendly.Unchanged
                            : "You have a newer " + pkg.Title + " (" + existingVersion + ") than this one (" + pkg.Version +
                              "), and the phone doesn't go back to older versions of this kind of app. " + Friendly.Unchanged;
                        return outcome;
                    }
                }

                string mode = existing != null ? PackageInstaller.Update : PackageInstaller.Deploy;
                status((existing != null ? "updating " : "installing ") + pkg.Title + "...");
                var job = new InstallJob();
                job.Items.Add(new InstallItem
                {
                    ProductId = pkg.ProductId, Title = pkg.Title, Version = pkg.Version, Mode = mode, File = stageName,
                    ExistingTitle = existing != null ? existing.Title : null,
                    ExistingOnSd = existing != null && existing.OnSd,
                    // a Silverlight app replaced by an appx one with the same product ID
                    KindChange = existingKind == "xap" && pkg.Kind == "appx",
                });
                string problem = await StartAsync(job);
                if (problem != null) { outcome.Message = problem; return outcome; }
                outcome.Job = job;
                outcome.Started = true;
                return outcome;
            }
            finally
            {
                // The staged copy goes once it isn't needed: on every early return here, and after
                // TH by the install batch itself
                if (!outcome.Started)
                {
                    try
                    {
                        using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                            if (iso.FileExists(stageName)) iso.DeleteFile(stageName);
                    }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>Null if a new install can start, otherwise why not.</summary>
        public static string Busy()
        {
            var pending = InstallJob.Load();
            if (pending != null && !JobLog.IsDone(pending))
                return "The phone is still installing " + pending.Title + ". Wait for it to finish, then try again.";
            return null;
        }

        /// <summary>Starts the job's batch in the background and remembers the job. Null when it
        /// started, otherwise what went wrong.</summary>
        public static async Task<string> StartAsync(InstallJob job)
        {
            try
            {
                job.Run = await Shell.StartDetachedAsync(job.Commands(Shell.LocalPath, Shell.Quote, Shell.Helper("rfxfetch.exe")));
            }
            catch (Exception e)
            {
                AppLog.Add("couldn't start the installer", e.Message);
                return e is IOException ? e.Message + " " + Friendly.Unchanged
                    : e is ArgumentException ? e.Message
                    : "Couldn't start the phone's installer. Try again in a moment. " + Friendly.Unchanged;
            }
            job.Save();
            Notify.ClearProgress();
            Background.Watch();
            return null;
        }

        /// <summary>Downloads and installs catalog releases, one after the other, in one background
        /// task: it carries on when the app is minimized or the phone is locked. Each download is
        /// pinned to the SHA-256 in the signed catalog. Unesbrews itself goes last.</summary>
        public static async Task<InstallOutcome> StartCatalogAsync(List<CatalogEntry> entries, List<CatalogRelease> releases, Action<string> status)
        {
            var outcome = new InstallOutcome();
            var busy = Busy();
            if (busy != null) { outcome.Message = busy; return outcome; }
            status("checking what's installed...");
            var ids = new List<string>();
            foreach (var e in entries) ids.Add(e.App.ProductId);
            Dictionary<string, CatalogService.InstalledCopy> have;
            try { have = await CatalogService.InstalledAsync(ids); }
            catch (Exception) { have = new Dictionary<string, CatalogService.InstalledCopy>(); }

            var job = new InstallJob();
            var skipped = new List<string>();
            InstallItem self = null;
            for (int n = 0; n < entries.Count; n++)
            {
                var e = entries[n];
                var r = releases[n];
                CatalogService.InstalledCopy copy;
                have.TryGetValue(e.App.ProductId, out copy);
                if (!CatalogService.Supported(r))
                {
                    skipped.Add("This kind of package (" + r.Kind + ") can't be installed by " + AppInfo.Name + " yet.");
                    continue;
                }
                // the phone only updates an appx app to a higher version (0x81030137 otherwise)
                if (r.Kind == "appx" && copy != null)
                {
                    int cmp;
                    try { cmp = AppLoader.Catalog.CatalogIndex.CompareVersions(r.Version, copy.Version); }
                    catch (Exception) { cmp = 1; }
                    if (cmp <= 0)
                    {
                        skipped.Add(cmp == 0
                            ? e.App.Name + " " + r.Version + " is already installed."
                            : "You have a newer " + e.App.Name + " (" + copy.Version + ") than " + r.Version +
                              ", and the phone doesn't go back to older versions of this kind of app.");
                        continue;
                    }
                }
                var item = new InstallItem
                {
                    ProductId = e.App.ProductId, Title = e.App.Name, Version = r.Version,
                    Mode = e.Installed != null ? PackageInstaller.Update : PackageInstaller.Deploy,
                    File = "dl-" + r.Sha256.Substring(0, 16) + (r.Kind == "appx" ? ".appx" : ".xap"),
                    Url = r.Url, Sha256 = r.Sha256, Size = r.Size,
                    ExistingTitle = e.Installed != null ? e.Installed.Title : null,
                    ExistingOnSd = e.Installed != null && e.Installed.OnSd,
                    KindChange = copy != null && copy.Kind == "xap" && r.Kind == "appx",
                };
                if (item.ProductId == OwnProductId) self = item; else job.Items.Add(item);
            }
            if (self != null) job.Items.Add(self);
            if (job.Items.Count == 0)
            {
                outcome.Message = string.Join("\n\n", skipped.ToArray()) + " " + Friendly.Unchanged;
                return outcome;
            }
            string problem = await StartAsync(job);
            if (problem != null) { outcome.Message = problem; return outcome; }
            if (skipped.Count > 0) AppLog.Add("skipped", string.Join("\n", skipped.ToArray()));
            outcome.Job = job;
            outcome.Started = true;
            outcome.Message = skipped.Count > 0 ? string.Join("\n\n", skipped.ToArray()) : null;
            return outcome;
        }

        /// <summary>Waits for a started job, telling progress (also to the notification), then checks
        /// what's really installed. Clears the remembered job once it has finished.</summary>
        public static async Task<InstallOutcome> FinishAsync(InstallJob job, Action<string> progress)
        {
            var outcome = new InstallOutcome { Job = job };
            var end = DateTime.UtcNow.AddMinutes(30);
            while (!JobLog.IsDone(job) && DateTime.UtcNow < end)
            {
                string now = JobLog.Describe(job, JobLog.Parse(job, JobLog.Read(job)));
                if (progress != null) progress(now);
                Notify.Progress(now);
                await Task.Delay(1000);
            }
            string log = JobLog.Read(job);
            var states = JobLog.Parse(job, log);
            if (!JobLog.IsDone(job))
            {
                outcome.StillRunning = true;
                outcome.Message = "The phone is still working on " + job.Title + ". The result shows up when it's done.";
                return outcome;   // still remembered: picked up again next time
            }
            if (progress != null) progress("checking what's installed...");
            var ids = new List<string>();
            foreach (var i in job.Items) ids.Add(i.ProductId);
            var installed = await CatalogService.InstalledVersionsAsync(ids);

            var messages = new List<string>();
            bool all = true;
            for (int n = 0; n < job.Items.Count; n++)
            {
                var it = job.Items[n];
                var st = states[n];
                string v;
                installed.TryGetValue(it.ProductId, out v);
                bool ok;
                messages.Add(Explain(it, st, v, out ok));
                all &= ok;
            }
            outcome.Succeeded = all;
            outcome.Message = string.Join("\n\n", messages.ToArray());
            Notify.Result(job.Run, all ? "done" : "not everything installed", outcome.Message);
            InstallJob.Clear();
            Shell.Archive(job.Run, outcome.Message);
            Shell.Delete(job.Run);
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    foreach (var it in job.Items) if (iso.FileExists(it.File)) iso.DeleteFile(it.File);
                    if (iso.FileExists("notified-" + job.Run)) iso.DeleteFile("notified-" + job.Run);
                }
            }
            catch (Exception) { }
            Background.Stop();
            return outcome;
        }

        /// <summary>One item's result in plain words; what's installed now decides, not only TH.</summary>
        static string Explain(InstallItem it, ItemState st, string installed, out bool ok)
        {
            ok = false;
            AppLog.Add("finished: " + it.What, "phase " + st.Phase + ", TH exit " + st.ThExit + ", installed version now " +
                       (installed ?? "unknown") + (st.Output.Trim().Length > 0 ? "\n" + st.Output.Trim() : ""));
            if (st.Phase == "failed-download") return Friendly.Download(it.What, st.Output) + " " + Friendly.Unchanged;
            if (installed != null && SameVersion(installed, it.Version))
            {
                ok = true;
                return it.IsUpdate ? it.Title + " is updated to " + it.Version + "." : it.What + " is installed.";
            }
            if (st.Phase != "finished")
                return "The installer for " + it.Title + " stopped before it finished. Try again in a moment.";
            if (st.ThExit == 0 && installed == null && !it.IsUpdate)
            {
                ok = true;   // a fresh install whose version can't be read: trust the installer
                return it.What + " is installed.";
            }
            if (st.ThExit == 0)
                return "The phone's installer finished, but " + it.Title + " is " +
                       (installed != null ? "still version " + installed : "not showing up as installed") +
                       ". Close " + it.Title + " if it's open, then try again.";
            return Friendly.InstallFailed(st.ThExit, st.Output, it);
        }

        /// <summary>The installed version of an app, or null when it can't be read.</summary>
        public static async Task<string> InstalledVersionAsync(string productId)
        {
            try
            {
                string v;
                var all = await CatalogService.InstalledVersionsAsync(new List<string> { productId });
                return all.TryGetValue(productId, out v) ? v : null;
            }
            catch (Exception) { return null; }
        }

        static bool SameVersion(string a, string b)
        {
            try { return AppLoader.Catalog.CatalogIndex.CompareVersions(a, b) == 0; }
            catch (Exception) { return string.Equals(a, b, StringComparison.Ordinal); }
        }

        static string Sha256Hex(Stream s)
        {
            using (var sha = new SHA256Managed())
            {
                var hash = sha.ComputeHash(s);
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static bool IsPlain(string path)
        {
            foreach (char c in path)
                if (c > 126 || c == ' ' || c == '%' || c == '^' || c == '&' || c == '"') return false;
            return true;
        }
    }
}
