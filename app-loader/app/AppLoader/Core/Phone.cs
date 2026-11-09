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

    /// <summary>An install that has been started: kept in the settings until its result is shown.</summary>
    public sealed class InstallJob
    {
        public string Run, Mode, ProductId, Title, Version, ExistingTitle, Staged;
        public bool ExistingOnSd;   // the installed copy (if any) lives on the SD card
        public bool IsUpdate { get { return Mode == PackageInstaller.Update; } }

        const string Key = "job";

        public void Save()
        {
            IsolatedStorageSettings.ApplicationSettings[Key] = string.Join("\n",
                new[] { Run, Mode, ProductId, Title, Version, ExistingTitle ?? "", Staged, ExistingOnSd ? "sd" : "" });
            IsolatedStorageSettings.ApplicationSettings.Save();
        }

        public static InstallJob Load()
        {
            object v;
            if (!IsolatedStorageSettings.ApplicationSettings.TryGetValue(Key, out v) || !(v is string)) return null;
            var f = ((string)v).Split('\n');
            if (f.Length < 7) return null;
            return new InstallJob { Run = f[0], Mode = f[1], ProductId = f[2], Title = f[3], Version = f[4],
                                    ExistingTitle = f[5].Length > 0 ? f[5] : null, Staged = f[6],
                                    ExistingOnSd = f.Length > 7 && f[7] == "sd" };
        }

        public static void Clear()
        {
            IsolatedStorageSettings.ApplicationSettings.Remove(Key);
            IsolatedStorageSettings.ApplicationSettings.Save();
        }
    }

    public sealed class InstallOutcome
    {
        public bool Started;        // TH is running (Job), the result comes from FinishAsync
        public bool Succeeded;
        public string Message;      // plain words for the user
        public PackageInfo Package;
        public InstallJob Job;
        public ThResult Th;         // what TH said, once it has finished
        public bool HashMismatch;   // a catalogue download didn't match its SHA-256
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
        /// from a catalogue, expectedProductId and expectedSha256 (hex) must match the staged copy.
        /// Returns with Started set once TH is running; FinishAsync gives the result.</summary>
        public static async Task<InstallOutcome> InstallAsync(FoundPackage found, Action<string> status,
                                                              string expectedProductId, string expectedSha256)
        {
            var outcome = new InstallOutcome();
            var pending = InstallJob.Load();
            if (pending != null && !Shell.IsDone(pending.Run))
            {
                outcome.Message = "The phone is still installing " + pending.Title + ". Wait for it to finish, then try again.";
                return outcome;
            }

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
                                outcome.Message = "The downloaded file isn't exactly the one the catalogue lists, so it was thrown away. " +
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
                    outcome.Message = "The downloaded file is a different app (" + pkg.Title + ") than the catalogue says, " +
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

                string mode = existing != null ? PackageInstaller.Update : PackageInstaller.Deploy;
                status((existing != null ? "updating " : "installing ") + pkg.Title + "...");
                string run;
                try { run = await PackageInstaller.StartAsync(staged, mode, true); }
                catch (Exception e)
                {
                    AppLog.Add("couldn't start the installer", e.Message);
                    outcome.Message = e is IOException ? e.Message + " " + Friendly.Unchanged
                        : "Couldn't start the phone's installer. Restart the phone and try again. " + Friendly.Unchanged;
                    return outcome;
                }
                outcome.Job = new InstallJob
                {
                    Run = run, Mode = mode, ProductId = pkg.ProductId, Title = pkg.Title, Version = pkg.Version,
                    ExistingTitle = existing != null ? existing.Title : null, Staged = stageName,
                    ExistingOnSd = existing != null && existing.OnSd,
                };
                outcome.Job.Save();
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

        /// <summary>Waits for a started install to finish and turns TH's answer into words.
        /// Clears the remembered job once it has finished.</summary>
        public static async Task<InstallOutcome> FinishAsync(InstallJob job)
        {
            var outcome = new InstallOutcome { Job = job };
            var th = await PackageInstaller.WaitAsync(job.Run, job.Mode, 900);
            outcome.Th = th;
            if (!th.Finished)
            {
                outcome.Message = "The phone is still installing " + job.Title + ". Its result shows up when it's done.";
                return outcome;   // still remembered: picked up again next time
            }
            outcome.Succeeded = th.Succeeded;
            outcome.Message = Explain(th, job);
            InstallJob.Clear();
            Shell.Delete(job.Run);
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    if (iso.FileExists(job.Staged)) iso.DeleteFile(job.Staged);
            }
            catch (Exception) { }
            return outcome;
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

        public static string Explain(ThResult th, InstallJob job)
        {
            string what = job.Title + " " + job.Version;
            if (th.Succeeded)
                return job.IsUpdate ? what + " is updated." : what + " is installed.";
            AppLog.Add((job.IsUpdate ? "update failed: " : "install failed: ") + what,
                       "TH.exe -" + th.Mode + ", exit " + th.ExitCode + "\n" + th.Output);
            return Friendly.InstallFailed(th, job);
        }
    }
}
