// What's on the phone (packages waiting in Downloads, installed apps) and the install decisions.
//
// Rules (learned the hard way: CMD Injector disappeared after a refused update):
//  - An app that is installed gets an in-place update (-uda); one that isn't gets a deploy (-di).
//  - Never uninstall, not even as a fallback.
//  - A package that refuses the SD card is not installed over a copy that lives on the SD card:
//    the phone refuses that update and the app can be lost. The user moves the app first.
//  - The package is copied to a plain path in the app's own folder first (ASCII, no spaces), and
//    read from there, so the checks look at exactly what gets installed.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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
        public bool Succeeded;
        public string Message;      // plain words for the user
        public PackageInfo Package;
        public ThResult Th;         // what TH said, when it ran
    }

    public static class Phone
    {
        // Where downloads and copied files usually land
        static readonly string[] Roots =
        {
            @"D:\Downloads", @"C:\Data\Users\Public\Downloads",
            @"D:\Documents", @"C:\Data\Users\Public\Documents",
        };

        public static async Task<List<FoundPackage>> FindPackagesAsync()
        {
            var cmds = new System.Text.StringBuilder();
            foreach (var root in Roots)
                cmds.Append("if exist " + Shell.Quote(root) + " for /r " + Shell.Quote(root) +
                            " %%f in (*.xap *.appx) do echo pkg^|%%~zf^|%%f\n");
            cmds.Append("for %%f in (D:\\*.xap D:\\*.appx C:\\Data\\*.xap C:\\Data\\*.appx) do echo pkg^|%%~zf^|%%f\n");
            var r = await Shell.RunAsync(cmds.ToString(), 60);
            if (!r.Reached) throw new IOException(r.Problem ?? "The shell didn't answer.");
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
            if (!r.Reached) throw new IOException(r.Problem ?? "The shell didn't answer.");
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

        /// <summary>Copies the package into the app's folder under a plain name, reads its manifest
        /// there and checks, then installs it: update if installed, deploy if not.</summary>
        public static async Task<InstallOutcome> InstallAsync(FoundPackage found, Action<string> status)
        {
            var outcome = new InstallOutcome();
            string ext = found.Path.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) ? ".appx" : ".xap";
            string stageName = "staged" + ext;
            string staged = Shell.LocalPath + "\\" + stageName;
            if (!IsPlain(staged))
            {
                outcome.Message = "The app's own folder has spaces or unusual characters in its path (" + staged +
                                  "), so the installer can't be given a safe copy. Nothing was installed.";
                return outcome;
            }

            status("copying " + found.Name + "...");
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                if (iso.FileExists("staged.xap")) iso.DeleteFile("staged.xap");
                if (iso.FileExists("staged.appx")) iso.DeleteFile("staged.appx");
            }
            var copy = await Shell.RunAsync("copy /y " + Shell.Quote(found.Path) + " " + Shell.Quote(staged), 120);
            bool exists;
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication()) exists = iso.FileExists(stageName);
            if (!copy.Reached || !exists)
            {
                outcome.Message = "Couldn't copy the package: " + (copy.Problem ?? copy.Output.Trim());
                return outcome;
            }

            status("reading the package...");
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                using (var s = iso.OpenFile(stageName, FileMode.Open, FileAccess.Read))
                    outcome.Package = PackageInfo.Read(s);
            }
            catch (Exception e)
            {
                outcome.Message = "This isn't a package the phone can install: " + e.Message;
                return outcome;
            }
            var pkg = outcome.Package;
            if (pkg.ProductId == null)
            {
                outcome.Message = "The package has no product ID in its manifest, so it can't be checked. Nothing was installed.";
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

            string mode = existing != null ? PackageInstaller.Update : PackageInstaller.Deploy;
            status((existing != null ? "updating " : "installing ") + pkg.Title + "...");
            outcome.Th = await PackageInstaller.RunAsync(staged, mode);
            outcome.Succeeded = outcome.Th.Succeeded;
            outcome.Message = Explain(outcome.Th, pkg, existing);

            // the staged copy isn't needed any more
            try { await Shell.RunAsync("del /f /q " + Shell.Quote(staged), 30); } catch (Exception) { }
            return outcome;
        }

        static bool IsPlain(string path)
        {
            foreach (char c in path)
                if (c > 126 || c == ' ' || c == '%' || c == '^' || c == '&' || c == '"') return false;
            return true;
        }

        public static string Explain(ThResult th, PackageInfo pkg, InstalledApp existing)
        {
            string what = pkg.Title + " " + pkg.Version;
            if (th.ShellProblem != null && th.Output.Length == 0) return th.ShellProblem;
            if (th.Succeeded)
                return existing != null ? what + " is updated." : what + " is installed.";
            switch (th.PacManCode)
            {
                case "0x8103012e":
                    return what + " refuses the SD card. Set Settings > storage sense > store new apps on: phone, " +
                           "then try again. Nothing was changed.";
                case "0x81030137":
                    return existing != null
                        ? "The phone didn't take this as an update of " + existing.Title + ": it may be the same version " +
                          "as the one installed, or not newer. Nothing was changed."
                        : "The phone says this package isn't valid or isn't applicable. Nothing was installed.";
                case "0x800b0100":
                    return "The phone wants a signed package for this kind of install. Nothing was installed.";
                case "0x80070005":
                    return "The phone said access is denied. Unlock the phone and try again.";
            }
            return "The install didn't complete. The installer's last words are in the log tab.";
        }
    }
}
