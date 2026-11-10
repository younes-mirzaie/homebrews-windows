// Stage 2: the online catalog (../../../catalog/DESIGN.md).
//
//  - The index and its signature are downloaded by Helper\rfxfetch.exe (NSS, works on Wi-Fi and
//    mobile data), run as SYSTEM through the shell, into the app's folder.
//  - CatalogIndex.Load checks the Ed25519 signature with the key built into the app and refuses
//    an index older than the newest one this phone has accepted.
//  - A package is downloaded by rfxfetch (detached, so it survives a screen lock), pinned by the
//    index's SHA-256 and size, and handed to Phone.InstallAsync with the expected product ID and
//    SHA-256, which checks them again on the staged copy.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AppLoader.Catalog;
using Windows.ApplicationModel;

namespace AppLoader.Core
{
    public static class CatalogConfig
    {
        // GitHub Pages of the public catalog repo
        public const string IndexUrl = "https://younes-mirzaie.github.io/wp-homebrew/dist/index.json";
        // Ed25519 public key (base64, 32 bytes) printed by `catalog.py keygen`. Empty until the
        // signing key exists: the catalog panes then say it isn't set up yet.
        public const string PublicKey = "1cxjX1wOkgO6WMJaGNdR589sCrfYyfUgiO+gT052gGU=";
    }

    /// <summary>A catalog app as the app shows it, with what's installed.</summary>
    public sealed class CatalogEntry
    {
        public CatalogApp App;
        public InstalledApp Installed;     // null when not installed
        public string InstalledVersion;    // null when unknown
        public bool Installable { get { return CatalogService.Supported(App.Latest); } }
        public bool UpdateAvailable
        {
            get
            {
                if (Installed == null || !Installable) return false;
                return InstalledVersion == null || CatalogIndex.CompareVersions(App.Latest.Version, InstalledVersion) > 0;
            }
        }
    }

    /// <summary>A package download that has been started (kept in settings until it is used).</summary>
    public sealed class DownloadJob
    {
        public string Run, AppId, Version, File;
        const string Key = "download";

        public void Save()
        {
            IsolatedStorageSettings.ApplicationSettings[Key] = string.Join("\n", new[] { Run, AppId, Version, File });
            IsolatedStorageSettings.ApplicationSettings.Save();
        }

        public static DownloadJob Load()
        {
            object v;
            if (!IsolatedStorageSettings.ApplicationSettings.TryGetValue(Key, out v) || !(v is string)) return null;
            var f = ((string)v).Split('\n');
            return f.Length < 4 ? null : new DownloadJob { Run = f[0], AppId = f[1], Version = f[2], File = f[3] };
        }

        public static void Clear()
        {
            IsolatedStorageSettings.ApplicationSettings.Remove(Key);
            IsolatedStorageSettings.ApplicationSettings.Save();
        }
    }

    public static class CatalogService
    {
        const string SerialKey = "catalogSerial";
        const string IndexFile = "catalog-index.json", SigFile = "catalog-index.json.sig";
        public static CatalogIndex Current { get; private set; }

        public static bool Configured { get { return CatalogConfig.PublicKey.Length > 0; } }

        // Run from the copy on C: (Shell.HelperDir): the shell batches put it there first
        static string Helper(string exe) { return Shell.Helper(exe); }

        static long HighestSerial
        {
            get { object v; return IsolatedStorageSettings.ApplicationSettings.TryGetValue(SerialKey, out v) && v is long ? (long)v : 0; }
            set { IsolatedStorageSettings.ApplicationSettings[SerialKey] = value; IsolatedStorageSettings.ApplicationSettings.Save(); }
        }

        public static bool Supported(CatalogRelease r)
        {
            return r.Kind == "appx" || r.Kind == "xap81" || r.Kind == "xap80";   // stage 1 has no bundles yet
        }

        /// <summary>The last index this phone accepted, checked again, without going online.</summary>
        public static CatalogIndex LoadCached()
        {
            if (!Configured) return null;
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!iso.FileExists(IndexFile) || !iso.FileExists(SigFile)) return null;
                    byte[] index = ReadAll(iso, IndexFile);
                    byte[] sigBytes = ReadAll(iso, SigFile);
                    string sig = Encoding.UTF8.GetString(sigBytes, 0, sigBytes.Length);
                    return Current = CatalogIndex.Load(index, sig, Convert.FromBase64String(CatalogConfig.PublicKey), HighestSerial);
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>Downloads the index and its signature, checks them, and keeps them.</summary>
        public static async Task<CatalogIndex> RefreshAsync()
        {
            if (!Configured) throw new CatalogException("The online catalog isn't set up in this version of " + AppInfo.Name + ".");
            string tmpIndex = Shell.LocalPath + "\\catalog-new.json", tmpSig = tmpIndex + ".sig";
            string fetch = Shell.Quote(Helper("rfxfetch.exe"));
            var r = await Shell.RunAsync(
                fetch + " " + Shell.Quote(CatalogConfig.IndexUrl) + " " + Shell.Quote(tmpIndex) +
                    " --max-bytes " + CatalogIndex.MaxIndexBytes + " --quiet\n" +
                "if errorlevel 1 exit /b 1\n" +
                fetch + " " + Shell.Quote(CatalogConfig.IndexUrl + ".sig") + " " + Shell.Quote(tmpSig) + " --max-bytes 4096 --quiet\n", 120);
            if (!r.Reached) throw new CatalogException(r.Problem ?? Friendly.Slow);
            if (r.ExitCode != 0) throw new CatalogException(Friendly.Download("the catalog", r.Output));

            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                byte[] index = ReadAll(iso, "catalog-new.json");
                byte[] sigBytes = ReadAll(iso, "catalog-new.json.sig");
                var idx = CatalogIndex.Load(index, Encoding.UTF8.GetString(sigBytes, 0, sigBytes.Length),
                                            Convert.FromBase64String(CatalogConfig.PublicKey), HighestSerial);
                // accepted: keep it, and never accept an older one again
                if (iso.FileExists(IndexFile)) iso.DeleteFile(IndexFile);
                if (iso.FileExists(SigFile)) iso.DeleteFile(SigFile);
                iso.MoveFile("catalog-new.json", IndexFile);
                iso.MoveFile("catalog-new.json.sig", SigFile);
                if (idx.Serial > HighestSerial) HighestSerial = idx.Serial;
                return Current = idx;
            }
        }

        /// <summary>The cached icon file for an app (named by its hash), or null if it has none.</summary>
        public static string IconFile(CatalogApp app)
        {
            return app.IconSha256 == null ? null : "icon-" + app.IconSha256.Substring(0, 16) + ".png";
        }

        /// <summary>Downloads the icons that aren't cached yet (each pinned by the index's SHA-256).
        /// Failures only mean a letter is shown instead.</summary>
        public static async Task FetchIconsAsync(CatalogIndex idx)
        {
            var cmds = new StringBuilder();
            var wanted = new List<string>();
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                foreach (var app in idx.Apps)
                {
                    string f = IconFile(app);
                    if (f == null || iso.FileExists(f)) continue;
                    try
                    {
                        cmds.Append(Shell.Quote(Shell.Helper("rfxfetch.exe")) + " " + Shell.Quote(app.Icon) + " " +
                                    Shell.Quote(Shell.LocalPath + "\\" + f) + " --sha256 " + app.IconSha256 +
                                    " --size " + app.IconSize + " --max-bytes " + app.IconSize + " --quiet --timeout 20\n");
                        wanted.Add(app.Id);
                    }
                    catch (ArgumentException) { }
                }
            if (wanted.Count == 0) return;
            var r = await Shell.RunAsync(cmds.ToString(), 30 + 20 * wanted.Count);
            if (!r.Reached || r.Output.Contains("ERROR ")) AppLog.Add("some app icons didn't download", r.Problem ?? r.Output);
        }

        /// <summary>The cached icon as an image, or null.</summary>
        public static System.Windows.Media.Imaging.BitmapImage LoadIcon(CatalogApp app)
        {
            string f = IconFile(app);
            if (f == null) return null;
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!iso.FileExists(f)) return null;
                    using (var s = iso.OpenFile(f, FileMode.Open, FileAccess.Read))
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.SetSource(s);
                        return bmp;
                    }
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>Joins the catalog with the installed apps and their versions.</summary>
        public static async Task<List<CatalogEntry>> EntriesAsync(CatalogIndex idx, List<InstalledApp> installed)
        {
            var list = new List<CatalogEntry>();
            var byId = new Dictionary<string, InstalledApp>();
            foreach (var a in installed) if (a.ProductId != null) byId[a.ProductId] = a;
            var wanted = new List<string>();
            foreach (var app in idx.Apps)
            {
                InstalledApp inst;
                byId.TryGetValue(app.ProductId, out inst);
                list.Add(new CatalogEntry { App = app, Installed = inst });
                if (inst != null) wanted.Add(app.ProductId);
            }
            var versions = await InstalledVersionsAsync(wanted);
            foreach (var e in list)
            {
                string v;
                if (e.Installed != null && versions.TryGetValue(e.App.ProductId, out v)) e.InstalledVersion = v;
            }
            return list;
        }

        /// <summary>Installed versions, read from the installed manifests (phone and SD card). TH's
        /// app list has no versions. Apps whose manifest isn't where we look stay unknown.</summary>
        public static async Task<Dictionary<string, string>> InstalledVersionsAsync(List<string> productIds)
        {
            var result = new Dictionary<string, string>();
            if (productIds.Count == 0) return result;
            var cmds = new StringBuilder();
            foreach (var pid in productIds)
            {
                if (!Regex.IsMatch(pid, "^[0-9A-F-]{36}$")) continue;
                foreach (var root in new[] { @"C:\Data\Programs", @"D:\WPSystem\Apps" })
                    foreach (var man in new[] { "AppxManifest.xml", "WMAppManifest.xml" })
                    {
                        string src = root + "\\{" + pid + "}\\Install\\" + man;
                        string dst = Shell.LocalPath + "\\ver-" + pid + "-" + man;
                        cmds.Append("if exist " + Shell.Quote(src) + " copy /y " + Shell.Quote(src) + " " + Shell.Quote(dst) + " >nul\n");
                    }
            }
            await Shell.RunAsync(cmds.ToString(), 60);
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                foreach (var pid in productIds)
                {
                    string v = null;
                    foreach (var man in new[] { "AppxManifest.xml", "WMAppManifest.xml" })
                    {
                        string f = "ver-" + pid + "-" + man;
                        if (!iso.FileExists(f)) continue;
                        try
                        {
                            if (v == null) v = ManifestVersion(Decode(ReadAll(iso, f)), man == "AppxManifest.xml");
                        }
                        catch (Exception) { }
                        try { iso.DeleteFile(f); } catch (Exception) { }
                    }
                    if (v != null) result[pid] = v;
                }
            }
            return result;
        }

        static string ManifestVersion(string xml, bool appx)
        {
            var el = Regex.Match(xml, appx ? @"<(?:\w+:)?Identity\b[^>]*>" : @"<App\b[^>]*>");
            if (!el.Success) return null;
            var m = Regex.Match(el.Value, @"\bVersion\s*=\s*""(\d+\.\d+\.\d+\.\d+)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Where a release's verified download goes (named by its hash, so a finished
        /// download is never mistaken for another one).</summary>
        public static string DownloadName(CatalogRelease r)
        {
            return "dl-" + r.Sha256.Substring(0, 16) + (r.Kind == "appx" ? ".appx" : ".xap");
        }

        public static bool IsDownloaded(CatalogRelease r)
        {
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                return iso.FileExists(DownloadName(r));
        }

        /// <summary>Starts downloading a release in the background (rfxfetch, detached).</summary>
        public static async Task<DownloadJob> StartDownloadAsync(CatalogApp app, CatalogRelease r)
        {
            var pending = DownloadJob.Load();
            if (pending != null && !Shell.IsDone(pending.Run))
                throw new CatalogException("Still downloading " + pending.AppId + " " + pending.Version + ". Wait for it to finish, then try again.");
            string file = DownloadName(r);
            string cmds = Shell.Quote(Helper("rfxfetch.exe")) + " " + Shell.Quote(r.Url) + " " +
                          Shell.Quote(Shell.LocalPath + "\\" + file) +
                          " --sha256 " + r.Sha256 + " --size " + r.Size + " --max-bytes " + r.Size;
            string run = await Shell.StartDetachedAsync(cmds);
            var job = new DownloadJob { Run = run, AppId = app.Id, Version = r.Version, File = file };
            job.Save();
            return job;
        }

        /// <summary>(bytes so far, total) from rfxfetch's progress lines.</summary>
        public static void Progress(DownloadJob job, out long done, out long total)
        {
            done = 0; total = -1;
            var log = Shell.Read(job.Run).Output;
            int at = log.LastIndexOf("PROGRESS ", StringComparison.Ordinal);
            if (at < 0) return;
            var parts = log.Substring(at + 9).Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) { long.TryParse(parts[0], out done); long.TryParse(parts[1], out total); }
        }

        /// <summary>Waits for a download; returns null on success, or what went wrong.</summary>
        public static async Task<string> WaitDownloadAsync(DownloadJob job, string what, Action<long, long> progress)
        {
            while (!Shell.IsDone(job.Run))
            {
                long d, t;
                Progress(job, out d, out t);
                if (progress != null) progress(d, t);
                await Task.Delay(1000);
            }
            var r = Shell.Read(job.Run);
            Shell.Delete(job.Run);
            DownloadJob.Clear();
            if (r.Reached && r.ExitCode == 0) return null;
            return Friendly.Download(what, r.Output);
        }

        /// <summary>Installs a downloaded release through stage 1 (which checks product ID and
        /// SHA-256 again on its staged copy). The download is removed once TH has started.</summary>
        public static async Task<InstallOutcome> InstallDownloadedAsync(CatalogApp app, CatalogRelease r, Action<string> status)
        {
            string file = DownloadName(r);
            var found = new FoundPackage { Path = Shell.LocalPath + "\\" + file, Size = r.Size };
            var outcome = await Phone.InstallAsync(found, status, r.ProductId, r.Sha256);
            if (outcome.Started || outcome.HashMismatch)
            {
                try
                {
                    using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                        if (iso.FileExists(file)) iso.DeleteFile(file);
                }
                catch (Exception) { }
            }
            return outcome;
        }

        /// <summary>Removes copies, downloads and run files left behind by installs and downloads
        /// that ended while the app was away. Leaves whatever a remembered job still needs.</summary>
        public static void CleanUp()
        {
            var job = InstallJob.Load();
            var dl = DownloadJob.Load();
            if ((job != null && !Shell.IsDone(job.Run)) || (dl != null && !Shell.IsDone(dl.Run))) return;
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    foreach (var pattern in new[] { "staged-*", "dl-*", "ver-*" })
                        foreach (var f in iso.GetFileNames(pattern))
                        {
                            if (job != null && f == job.Staged) continue;
                            if (dl != null && f == dl.File) continue;
                            try { iso.DeleteFile(f); } catch (Exception) { }
                        }
            }
            catch (Exception e) { AppLog.Add("cleanup skipped", e.Message); }
        }

        static byte[] ReadAll(IsolatedStorageFile iso, string name)
        {
            using (var s = iso.OpenFile(name, FileMode.Open, FileAccess.Read))
            {
                var b = new byte[s.Length];
                int n = 0;
                while (n < b.Length) { int k = s.Read(b, n, b.Length - n); if (k <= 0) break; n += k; }
                return b;
            }
        }

        static string Decode(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(b, skip, b.Length - skip);
        }
    }
}
