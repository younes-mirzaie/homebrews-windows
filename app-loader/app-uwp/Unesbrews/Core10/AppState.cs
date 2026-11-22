// What every page shows, kept in one place: the package files found on the phone, the installed
// apps, the catalog joined with them, and the install job that may be running. The pages only draw
// it; the work is done by the shared core (Phone, CatalogService, Jobs), exactly as on 8.1.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AppLoader.Catalog;
using AppLoader.Core;
using Windows.Storage;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unesbrews
{
    public static class AppState
    {
        public static List<FoundPackage> Packages;      // null until the first refresh
        public static string PackagesProblem;
        public static List<InstalledApp> Installed;
        public static List<CatalogEntry> Entries;        // catalog joined with what's installed
        public static string CatalogNote;                // e.g. "Offline, showing the library from last time"
        public static string CatalogProblem;             // why there is no catalog at all
        public static DateTimeOffset? LastRefresh;

        /// <summary>Status line while something runs (null when idle).</summary>
        public static string Status { get; private set; }
        public static bool Busy { get { return Status != null; } }
        /// <summary>An install job is running (the result dialog comes when it ends).</summary>
        public static bool Waiting { get; private set; }

        /// <summary>Something on screen should be drawn again (raised on the UI thread).</summary>
        public static event Action Changed;
        public static event Action StatusChanged;

        static void SetStatus(string s)
        {
            Status = s;
            StatusChanged?.Invoke();
        }

        static void Raise() { Changed?.Invoke(); }

        /// <summary>Updates ready for the apps in the library (the installed section).</summary>
        public static List<CatalogEntry> PendingUpdates
        {
            get { return Entries == null ? new List<CatalogEntry>() : Updates.Pending(Entries); }
        }

        /// <summary>The app is on screen (set by App), so the installed section shows updates
        /// instead of a notification.</summary>
        public static bool InFront;

        /// <summary>Started at launch and when the app comes back: picks up a job that was running,
        /// or does the first refresh.</summary>
        public static async Task ResumeAsync()
        {
            var job = InstallJob.Load();
            if (job != null && !Waiting) { await FinishAsync(job); return; }
            if (LastRefresh == null)
            {
                CatalogService.CleanUp();
                await RefreshAsync();
            }
        }

        /// <summary>Reads everything again: package files, installed apps, the catalog.</summary>
        public static async Task RefreshAsync()
        {
            if (Busy || Waiting) return;
            try
            {
                SetStatus("Looking for package files...");
                try
                {
                    Packages = await Phone.FindPackagesAsync();
                    PackagesProblem = null;
                }
                catch (Exception e) { PackagesProblem = Friendly.Of(e); Packages = Packages ?? new List<FoundPackage>(); }
                Raise();

                SetStatus("Reading the installed apps...");
                try { Installed = await Phone.InstalledAppsAsync(); }
                catch (Exception e)
                {
                    // without the shell nothing else works: say so once, on every page
                    CatalogProblem = Friendly.Of(e);
                    Installed = null;
                    Raise();
                    return;
                }
                Raise();
                await RefreshCatalogAsync();
                LastRefresh = DateTimeOffset.Now;
            }
            finally
            {
                SetStatus(null);
                Raise();
            }
            await CheckUpdatesAsync();
        }

        /// <summary>Automatic updates (when they're on) of the apps in the library.</summary>
        public static async Task CheckUpdatesAsync()
        {
            if (Entries == null || Busy || Waiting) return;
            var now = Updates.Check(Entries, InFront);
            if (now.Count == 0) return;
            InstallOutcome outcome;
            try { outcome = await Phone.StartCatalogAsync(now, now.Select(e => e.App.Latest).ToList(), s => SetStatus(Capital(s))); }
            catch (Exception e) { outcome = new InstallOutcome { Message = Friendly.Of(e) }; }
            finally { SetStatus(null); }
            if (!outcome.Started) { AppLog.Add("automatic update didn't start", outcome.Message); return; }
            await FinishAsync(outcome.Job, true);
        }

        static async Task RefreshCatalogAsync()
        {
            CatalogNote = null;
            CatalogProblem = null;
            if (!CatalogService.Configured)
            {
                CatalogProblem = "The online library isn't set up in this version of " + AppInfo.Name + ".";
                return;
            }
            CatalogIndex idx;
            string problem = null;
            try
            {
                SetStatus("Downloading the library...");
                idx = await CatalogService.RefreshAsync();
            }
            catch (Exception e)
            {
                problem = Friendly.Of(e);
                idx = CatalogService.LoadCached();
            }
            if (idx == null) { CatalogProblem = problem ?? "The library isn't loaded yet."; return; }
            if (problem != null) CatalogNote = "You're seeing the library from your last visit. " + problem;
            SetStatus("Checking installed versions...");
            Entries = await CatalogService.EntriesAsync(idx, Installed ?? new List<InstalledApp>());
            if (Installed != null) Library.StartWith(Entries);
            Raise();
            SetStatus("Getting app icons...");
            await CatalogService.FetchIconsAsync(idx);
            _icons.Clear();
        }

        static readonly Dictionary<string, ImageSource> _icons = new Dictionary<string, ImageSource>();

        /// <summary>The app's icon from the cache (downloaded and checked by CatalogService), or null.</summary>
        public static ImageSource Icon(CatalogApp app)
        {
            string f = CatalogService.IconFile(app);
            if (f == null) return null;
            ImageSource img;
            if (_icons.TryGetValue(f, out img)) return img;
            string path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, f);
            img = System.IO.File.Exists(path) ? new BitmapImage(new Uri("ms-appdata:///local/" + f)) : null;
            _icons[f] = img;
            return img;
        }

        // ---- installs ----

        /// <summary>Installs a package file found on the phone, after asking.</summary>
        public static async Task InstallFileAsync(FoundPackage p)
        {
            if (Busy || Waiting) { await Dialogs.TellAsync("Please wait", Friendly.Busy); return; }
            if (!await Dialogs.AskAsync("Install " + p.Name + "?",
                    "If you have this app already, it's updated in place and keeps its data. Nothing is ever uninstalled.",
                    "Install"))
                return;
            InstallOutcome outcome;
            try { outcome = await Phone.InstallAsync(p, s => SetStatus(Capital(s))); }
            catch (Exception e) { outcome = new InstallOutcome { Message = Friendly.Of(e) }; }
            finally { SetStatus(null); }
            if (!outcome.Started) { await Dialogs.TellAsync("Not installed", outcome.Message); return; }
            await AfterStartAsync(outcome);
        }

        /// <summary>Downloads and installs catalog releases (one, or every update), after asking.</summary>
        public static async Task InstallFromCatalogAsync(List<CatalogEntry> entries, List<CatalogRelease> releases, string title, string question, string action)
        {
            if (Busy || Waiting) { await Dialogs.TellAsync("Please wait", Friendly.Busy); return; }
            bool self = entries.Any(e => e.App.ProductId == Phone.OwnProductId);
            if (!await Dialogs.AskAsync(title, question +
                    "\n\nEverything is checked against the library's signed list before it's installed. Apps you have are updated in place and keep their data. " +
                    "It carries on if you leave " + AppInfo.Name + " or lock the phone, and a notification says how it went." +
                    (self ? "\n\n" + AppInfo.Name + " updates itself last and closes. Open it again afterwards." : ""),
                    action))
                return;
            InstallOutcome outcome;
            try { outcome = await Phone.StartCatalogAsync(entries, releases, s => SetStatus(Capital(s))); }
            catch (Exception e) { outcome = new InstallOutcome { Message = Friendly.Of(e) }; }
            finally { SetStatus(null); }
            if (!outcome.Started) { await Dialogs.TellAsync("Nothing was installed", outcome.Message ?? Friendly.Unchanged); return; }
            if (outcome.Message != null) await Dialogs.TellAsync("Some apps were skipped", outcome.Message + "\n\nThe others are being installed now.");
            await AfterStartAsync(outcome);
        }

        static async Task AfterStartAsync(InstallOutcome outcome)
        {
            if (outcome.Job.Includes(Phone.OwnProductId) && outcome.Job.Items.Count == 1)
                await Dialogs.TellAsync("Updating " + AppInfo.Name, AppInfo.Name + " is updating itself, so it will close in a moment. Open it again afterwards to see how it went.");
            await FinishAsync(outcome.Job, false);
        }

        /// <summary>Follows a running job to its end, then says how it went and refreshes.
        /// Automatic updates (quiet) only say something when one didn't work.</summary>
        static async Task FinishAsync(InstallJob job, bool quiet = false)
        {
            Waiting = true;
            Raise();
            SetStatus("Working on " + job.Title + "...");
            InstallOutcome outcome;
            try { outcome = await Phone.FinishAsync(job, s => SetStatus(Capital(s))); }
            catch (Exception e) { outcome = new InstallOutcome { Job = job, Message = Friendly.Of(e) }; }
            finally { SetStatus(null); Waiting = false; Raise(); }
            if (outcome.Succeeded) AppLog.Add(outcome.Message, null);
            if (!(quiet && outcome.Succeeded))
                await Dialogs.TellAsync(outcome.Succeeded ? "Done" : outcome.StillRunning ? "Still working" : "Not everything was installed", outcome.Message);
            if (!outcome.StillRunning) await RefreshAsync();
        }

        /// <summary>Saves a release's package file to Downloads (for sideloading it another way).</summary>
        public static async Task SavePackageAsync(CatalogApp app, CatalogRelease r)
        {
            if (Busy || Waiting) { await Dialogs.TellAsync("Please wait", Friendly.Busy); return; }
            string path;
            try
            {
                SetStatus("Downloading " + app.Name + " " + r.Version + "...");
                path = await CatalogService.SaveAsync(app, r);
            }
            catch (Exception e)
            {
                SetStatus(null);
                await Dialogs.TellAsync("Not downloaded", Friendly.Of(e));
                return;
            }
            SetStatus(null);
            await Dialogs.TellAsync("Downloaded", app.Name + " " + r.Version + " is in Downloads as " + System.IO.Path.GetFileName(path) +
                ". It was checked against the library's signed list. You can install it from Sideload, or with any other tool.");
        }

        /// <summary>Installs a package file chosen with the file picker: copied into the app's
        /// folder first, so the installer can read it wherever it was.</summary>
        public static async Task InstallPickedAsync(StorageFile file)
        {
            if (Busy || Waiting) { await Dialogs.TellAsync("Please wait", Friendly.Busy); return; }
            StorageFile copy;
            try
            {
                SetStatus("Copying " + file.Name + "...");
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("picked", CreationCollisionOption.OpenIfExists);
                var name = new System.Text.StringBuilder();
                foreach (char c in file.Name)
                    name.Append(c < 127 && (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') ? c : '_');
                copy = await file.CopyAsync(folder, name.ToString(), NameCollisionOption.ReplaceExisting);
            }
            catch (Exception e)
            {
                SetStatus(null);
                AppLog.Add("couldn't copy the chosen file", e.Message);
                await Dialogs.TellAsync("Not installed", "Couldn't read " + file.Name + ". The phone may be out of storage space. " + Friendly.Unchanged);
                return;
            }
            SetStatus(null);
            try
            {
                var props = await copy.GetBasicPropertiesAsync();
                await InstallFileAsync(new FoundPackage { Path = copy.Path, Size = (long)props.Size });
            }
            finally
            {
                try { await copy.DeleteAsync(); } catch (Exception) { }
            }
        }

        /// <summary>The shared core writes status in lower case (8.1 style); Windows 10 uses sentence case.</summary>
        public static string Capital(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = 0;
            while (i < s.Length && !char.IsLetter(s[i])) i++;   // "2/3: downloading" keeps its prefix
            return i < s.Length ? s.Substring(0, i) + char.ToUpperInvariant(s[i]) + s.Substring(i + 1) : s;
        }

        public static string Size(long b) { return JobLog.Size(b); }

        public static string StateText(CatalogEntry x)
        {
            if (!x.Installable) return "Can't be installed by " + AppInfo.Name + " yet";
            if (x.Installed == null) return "Not installed";
            if (x.InstalledVersion == null) return "Installed, version unknown";
            if (x.UpdateAvailable) return "Update to " + x.App.Latest.Version;
            return "Installed";
        }
    }
}
