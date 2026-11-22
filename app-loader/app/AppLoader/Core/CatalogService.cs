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

        /// <summary>Where "download package" saves files: the phone's Downloads folder, where
        /// file managers and other sideload tools find them.</summary>
        public const string SaveFolder = @"C:\Data\Users\Public\Downloads";

        /// <summary>Downloads a release's package file to the Downloads folder for the user to keep
        /// or sideload another way. Pinned to the catalog's SHA-256 and size like an install.
        /// Returns the saved file's path, or throws with plain words.</summary>
        public static async Task<string> SaveAsync(CatalogApp app, CatalogRelease r)
        {
            string ext = r.Kind == "appxbundle" ? ".appxbundle" : r.Kind == "appx" ? ".appx" : ".xap";
            var name = new StringBuilder();
            foreach (char c in app.Name + "_" + r.Version)
                name.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' ? c : '_');
            string dst = SaveFolder + "\\" + name + ext;
            string fetch = Shell.Quote(Helper("rfxfetch.exe"));
            // about 20 KB/s at worst, plus a minute
            int timeout = 60 + (int)Math.Min(3600, r.Size / 20000);
            var res = await Shell.RunAsync(
                "if not exist " + Shell.Quote(SaveFolder) + " mkdir " + Shell.Quote(SaveFolder) + "\n" +
                fetch + " " + Shell.Quote(r.Url) + " " + Shell.Quote(dst) + " --sha256 " + r.Sha256 +
                    " --size " + r.Size + " --max-bytes " + r.Size + " --quiet\n" +
                "if errorlevel 1 exit /b 1\n", timeout);
            if (!res.Reached) throw new IOException(res.Problem ?? Friendly.Slow);
            if (res.ExitCode != 0) throw new IOException(Friendly.Download(app.Name + " " + r.Version, res.Output));
            AppLog.Add("saved " + app.Name + " " + r.Version, dst);
            return dst;
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

#if !WINDOWS_UWP
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
#endif

        /// <summary>Whether a release can run on this phone's OS (its min_os, e.g. 10.0.15063.0 for
        /// a Windows 10 app). A release without min_os runs everywhere.</summary>
        public static bool RunsHere(CatalogRelease r)
        {
            if (string.IsNullOrEmpty(r.MinOs)) return true;
            Version need, have = OsVersion;
            try { need = new Version(r.MinOs); } catch (Exception) { return true; }
            return have == null || have >= need;
        }

        static Version _os;
        /// <summary>The phone's OS version, or null when it can't be read.</summary>
        public static Version OsVersion
        {
            get
            {
                if (_os != null) return _os;
                try
                {
#if WINDOWS_UWP
                    ulong v = ulong.Parse(Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamilyVersion);
                    _os = new Version((int)(v >> 48), (int)((v >> 32) & 0xFFFF), (int)((v >> 16) & 0xFFFF), (int)(v & 0xFFFF));
#else
                    _os = Environment.OSVersion.Version;
#endif
                }
                catch (Exception) { }
                return _os;
            }
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
                // apps made for a newer OS (Windows 10 versions on an 8.1 phone) aren't shown
                if (!RunsHere(app.Latest)) continue;
#if WINDOWS_UWP
                // the Windows Phone 8.1 Unesbrews: this app replaces it here
                if (app.ProductId == Installer10.ProductId81) continue;
#endif
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

        /// <summary>Installed versions, read from the installed manifests. TH's app list has no
        /// versions. Apps whose manifest isn't found stay unknown.</summary>
        public static async Task<Dictionary<string, string>> InstalledVersionsAsync(List<string> productIds)
        {
            var result = new Dictionary<string, string>();
            foreach (var kv in await InstalledAsync(productIds)) result[kv.Key] = kv.Value.Version;
            return result;
        }

        public sealed class InstalledCopy { public string Version, Kind; }   // Kind: "xap" or "appx"

        /// <summary>Version and kind of each installed app. A sideloaded appx app lives in
        /// C:\Data\SharedData\PhoneTools\Name_Version_... (SD: D:\PhoneTools\AppxLayouts), a Store
        /// one in C:\Data\Programs\WindowsApps, a Silverlight one in C:\Data\Programs\{ID}\Install.
        /// Old copies stay behind (older appx versions, the Silverlight folder of an app that moved
        /// to appx), so the highest version found counts.</summary>
        public static async Task<Dictionary<string, InstalledCopy>> InstalledAsync(List<string> productIds)
        {
            var result = new Dictionary<string, InstalledCopy>();
            var ids = new List<string>();
            foreach (var pid in productIds) if (Regex.IsMatch(pid, "^[0-9A-F-]{36}$")) ids.Add(pid);
            if (ids.Count == 0) return result;
            var cmds = new StringBuilder();
            foreach (var pid in ids)
                foreach (var root in new[] { @"C:\Data\Programs", @"D:\WPSystem\Apps" })
                    foreach (var man in new[] { "AppxManifest.xml", "WMAppManifest.xml" })
                    {
                        string src = root + "\\{" + pid + "}\\Install\\" + man;
                        string dst = Shell.LocalPath + "\\ver-" + pid + "-" + man;
                        cmds.Append("if exist " + Shell.Quote(src) + " copy /y " + Shell.Quote(src) + " " + Shell.Quote(dst) + " >nul\n");
                    }
            // every appx app's manifest (small files), named after its package folder
            Shell.Quote(Shell.LocalPath);   // refuses a folder name a batch line can't hold
            string waDst = "\"" + Shell.LocalPath + "\\wa-%%~nxd.xml\"";
            foreach (var root in new[] { @"C:\Data\SharedData\PhoneTools", @"D:\PhoneTools\AppxLayouts", @"C:\Data\Programs\WindowsApps" })
                cmds.Append("if exist " + Shell.Quote(root) + " for /d %%d in (" + Shell.Quote(root + "\\*") + ") do " +
                            "if exist \"%%d\\AppxManifest.xml\" copy /y \"%%d\\AppxManifest.xml\" " + waDst + " >nul\n");
            await Shell.RunAsync(cmds.ToString(), 90);
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                foreach (var f in iso.GetFileNames("wa-*"))
                {
                    try
                    {
                        string xml = Decode(ReadAll(iso, f));
                        var pm = Regex.Match(xml, @"PhoneProductId\s*=\s*""\{?([0-9A-Fa-f-]{36})\}?""");
                        string v = ManifestVersion(xml, true);
                        if (pm.Success && v != null)
                        {
                            string pid = pm.Groups[1].Value.ToUpperInvariant();
                            InstalledCopy have;
                            if (ids.Contains(pid) && (!result.TryGetValue(pid, out have) || CatalogIndex.CompareVersions(v, have.Version) > 0))
                                result[pid] = new InstalledCopy { Version = v, Kind = "appx" };
                        }
                    }
                    catch (Exception) { }
                    try { iso.DeleteFile(f); } catch (Exception) { }
                }
                foreach (var pid in ids)
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
                    InstalledCopy appx;
                    if (v != null && (!result.TryGetValue(pid, out appx) || CatalogIndex.CompareVersions(v, appx.Version) > 0))
                        result[pid] = new InstalledCopy { Version = v, Kind = "xap" };
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

        /// <summary>Removes copies, downloads and run files left behind by installs and downloads
        /// that ended while the app was away. Leaves whatever a remembered job still needs.</summary>
        public static void CleanUp()
        {
            // a job that is still running, or finished but not yet reported, keeps its files
            if (InstallJob.Load() != null) return;
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    foreach (var pattern in new[] { "staged-*", "dl-*", "ver-*", "wa-*", "notified-*" })
                        foreach (var f in iso.GetFileNames(pattern))
                            try { iso.DeleteFile(f); } catch (Exception) { }
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
