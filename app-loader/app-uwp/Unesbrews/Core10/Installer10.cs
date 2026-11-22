// What differs on Windows 10 Mobile, for the shared core (..\..\app\AppLoader\Core):
//
//  - There is no TH.exe. The phone's own installer is MinDeployAppX.exe (CMD Injector puts it in
//    System32), the same one CMD Injector's app manager runs. /DeploymentOption:32768 skips the
//    signature check, so homebrew packages that aren't signed by a trusted certificate install too.
//  - TH's app list is gone with it, so the installed apps are read from their manifests.
//  - A UWP app may only connect to the phone itself (the system shell on 127.0.0.1) once it is
//    exempt from loopback isolation. The exemption is set by CheckNetIsolation.exe as SYSTEM: the
//    first time by the user in CMD Injector's app manager ("Enable Loopback"), and again by every
//    install job, so it survives this app updating itself.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace AppLoader.Core
{
    public static class Installer10
    {
        /// <summary>This app's product ID (Package.appxmanifest, mp:PhoneIdentity).</summary>
        public const string ProductId = "8524EBD4-8E06-4F85-B48A-9165C43837EF";

        /// <summary>The Windows Phone 8.1 Unesbrews (.xap). A separate app: it stays in the catalog
        /// for 8.1 phones, and is left alone if it is installed here.</summary>
        public const string ProductId81 = "0145BB18-825E-4266-9094-5CA73FEF56A9";

        /// <summary>The batch line that installs (mode "di") or updates ("uda") a package, given as a
        /// quoted path.</summary>
        public static string Line(string quotedPackage, string mode)
        {
            string verb = mode == PackageInstaller.Update ? "/Update" : "/Add";
            return "MinDeployAppX.exe " + verb + " /PackagePath:" + quotedPackage + " /DeploymentOption:32768";
        }

        /// <summary>The batch line that lets this app reach the system shell (see above).</summary>
        public static string LoopbackLine()
        {
            string family = Package.Current.Id.FamilyName;
            if (!Regex.IsMatch(family, @"^[A-Za-z0-9._-]+$")) return "rem loopback: unusual package name";
            return "CheckNetIsolation.exe loopbackexempt -a -n=" + family + " >nul 2>&1";
        }

        /// <summary>Folder names of installed apps by product ID (a package's full name for appx apps,
        /// "{GUID}" for Silverlight ones), from the last InstalledAppsAsync. Used to open an app.</summary>
        public static readonly Dictionary<string, string> Folders = new Dictionary<string, string>();

        // Where apps live: Silverlight apps in Programs\{ID}\Install, appx apps in a folder named
        // after the package's full name. D: is the SD card.
        static readonly string[] GuidRoots = { @"C:\Data\Programs", @"D:\WPSystem\Apps" };
        static readonly string[] AppxRoots =
        {
            @"C:\Data\Programs\WindowsApps", @"D:\WindowsApps", @"D:\WPSystem\Apps\WindowsApps",
            @"C:\Data\SharedData\PhoneTools", @"D:\PhoneTools\AppxLayouts",
        };

        /// <summary>The installed apps (title, product ID, phone or SD card), read from their
        /// manifests through the system shell. Apps without a product ID (system parts,
        /// frameworks) are left out.</summary>
        public static async Task<List<InstalledApp>> InstalledAppsAsync()
        {
            Shell.Quote(Shell.LocalPath);   // refuses a folder name a batch line can't hold
            string at = Shell.LocalPath + "\\";
            var cmds = new StringBuilder();
            cmds.Append("del /f /q \"" + at + "ia-*.xml\" >nul 2>&1\n");
            for (int r = 0; r < GuidRoots.Length; r++)
                foreach (var man in new[] { "WMAppManifest.xml", "AppxManifest.xml" })
                    cmds.Append("if exist " + Shell.Quote(GuidRoots[r]) + " for /d %%d in (" + Shell.Quote(GuidRoots[r] + "\\{*}") + ") do " +
                                "if exist \"%%d\\Install\\" + man + "\" copy /y \"%%d\\Install\\" + man + "\" \"" + at +
                                "ia-g" + r + "-" + (man[0] == 'W' ? "wm" : "ax") + "-%%~nxd.xml\" >nul\n");
            for (int r = 0; r < AppxRoots.Length; r++)
                cmds.Append("if exist " + Shell.Quote(AppxRoots[r]) + " for /d %%d in (" + Shell.Quote(AppxRoots[r] + "\\*") + ") do " +
                            "if exist \"%%d\\AppxManifest.xml\" copy /y \"%%d\\AppxManifest.xml\" \"" + at +
                            "ia-a" + r + "-ax-%%~nxd.xml\" >nul\n");
            var res = await Shell.RunAsync(cmds.ToString(), 120);
            if (!res.Reached) throw new IOException(res.Problem ?? Friendly.Slow);

            var found = new Dictionary<string, InstalledApp>();
            var versions = new Dictionary<string, string>();
            lock (Folders) Folders.Clear();
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                foreach (var f in iso.GetFileNames("ia-*.xml"))
                {
                    try
                    {
                        // ia-<root kind><root index>-<wm|ax>-<folder>.xml
                        var m = Regex.Match(f, @"^ia-([ga])(\d)-(wm|ax)-(.+)\.xml$");
                        if (!m.Success) continue;
                        bool guidRoot = m.Groups[1].Value == "g";
                        string root = guidRoot ? GuidRoots[int.Parse(m.Groups[2].Value)] : AppxRoots[int.Parse(m.Groups[2].Value)];
                        string folder = m.Groups[4].Value;
                        string xml;
                        using (var s = iso.OpenFile(f, FileMode.Open, FileAccess.Read))
                        using (var r = new StreamReader(s, true))
                            xml = r.ReadToEnd();
                        var app = m.Groups[3].Value == "wm" ? FromWm(xml) : FromAppx(xml, folder);
                        if (app == null) continue;
                        app.Location = root.StartsWith("D:", StringComparison.OrdinalIgnoreCase) ? "SD" : "Internal";
                        app.InstallType = m.Groups[3].Value == "wm" ? "xap" : "appx";
                        string version = app.InstallType == "xap" ? Attr(Match(xml, @"<App\b[^>]*>"), "Version")
                                                                  : Attr(Match(xml, @"<(?:\w+:)?Identity\b[^>]*>"), "Version");
                        string have;
                        if (found.ContainsKey(app.ProductId) && versions.TryGetValue(app.ProductId, out have) && version != null &&
                            CompareSafe(version, have) <= 0)
                            continue;
                        found[app.ProductId] = app;
                        if (version != null) versions[app.ProductId] = version;
                        lock (Folders) Folders[app.ProductId] = folder;
                    }
                    catch (Exception e) { AppLog.Add("couldn't read an app's manifest", f + ": " + e.Message); }
                    finally { try { iso.DeleteFile(f); } catch (Exception) { } }
                }
            return new List<InstalledApp>(found.Values);
        }

        static InstalledApp FromWm(string xml)
        {
            string app = Match(xml, @"<App\b[^>]*>");
            string pid = PackageInfo.Guid(Attr(app, "ProductID"));
            if (pid == null) return null;
            string title = Attr(app, "Title");
            if (string.IsNullOrEmpty(title) || title.StartsWith("@")) title = pid;   // localized titles live in a resource DLL
            return new InstalledApp { Title = title, ProductId = pid };
        }

        static InstalledApp FromAppx(string xml, string folder)
        {
            if (Regex.IsMatch(xml, @"<(?:\w+:)?Framework>\s*true\s*<", RegexOptions.IgnoreCase)) return null;
            if (Regex.IsMatch(xml, @"<(?:\w+:)?ResourcePackage>\s*true\s*<", RegexOptions.IgnoreCase)) return null;
            string pid = PackageInfo.Guid(Attr(Match(xml, @"<(?:\w+:)?PhoneIdentity\b[^>]*>"), "PhoneProductId"));
            if (pid == null) return null;
            var dn = Regex.Match(xml, @"<(?:\w+:)?DisplayName>\s*([^<]*?)\s*</");
            string title = dn.Success ? System.Net.WebUtility.HtmlDecode(dn.Groups[1].Value) : "";
            if (title.Length == 0 || title.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                // a localized name: show the package name instead ("Contoso.Notes_1.0.0.0_arm__xyz" -> "Notes")
                string name = folder.Split('_')[0];
                int dot = name.LastIndexOf('.');
                title = dot >= 0 && dot < name.Length - 1 ? name.Substring(dot + 1) : name;
            }
            return new InstalledApp { Title = title, ProductId = pid };
        }

        static int CompareSafe(string a, string b)
        {
            try { return AppLoader.Catalog.CatalogIndex.CompareVersions(a, b); }
            catch (Exception) { return 0; }
        }

        static string Match(string s, string pattern)
        {
            var m = Regex.Match(s ?? "", pattern);
            return m.Success ? m.Value : "";
        }

        static string Attr(string element, string name)
        {
            var m = Regex.Match(element ?? "", @"\b" + name + @"\s*=\s*""([^""]*)""");
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
