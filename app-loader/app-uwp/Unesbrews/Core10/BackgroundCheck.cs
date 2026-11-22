// Every few hours, while the app isn't open: a quick look at the catalog for updates of the apps in
// the library (Library.cs). With automatic updates on, they are started right away (the install
// itself runs outside the app, like every install); otherwise a notification says they are ready.
// It also reports on an install that was still running when the app was closed.
//
// A timer background task in the app's own process (OnBackgroundActivated in App.xaml.cs), so it
// uses the same shared core. Windows gives it about half a minute, so it only reads what it needs:
// the catalog and the versions of the library apps, not the whole installed list.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AppLoader.Catalog;
using Windows.ApplicationModel.Background;

namespace AppLoader.Core
{
    public static class BackgroundCheck
    {
        public const string TaskName = "UnesbrewsUpdates";

        /// <summary>Signs up for the timer once (Windows keeps it across restarts).</summary>
        public static async Task RegisterAsync()
        {
            try
            {
                foreach (var t in BackgroundTaskRegistration.AllTasks)
                    if (t.Value.Name == TaskName) return;
                var access = await BackgroundExecutionManager.RequestAccessAsync();
                if (access == BackgroundAccessStatus.DeniedByUser || access == BackgroundAccessStatus.DeniedBySystemPolicy ||
                    access == BackgroundAccessStatus.Unspecified)
                {
                    AppLog.Add("Windows doesn't allow update checks in the background", "Updates are still checked whenever the app opens. (" + access + ")");
                    return;
                }
                var b = new BackgroundTaskBuilder { Name = TaskName };
                b.SetTrigger(new TimeTrigger(240, false));   // every 4 hours
                b.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
                b.Register();
            }
            catch (Exception e) { AppLog.Add("update checks in the background aren't available", e.Message); }
        }

        public static async Task RunAsync()
        {
            var job = InstallJob.Load();
            if (job != null)
            {
                var states = JobLog.Parse(job, JobLog.Read(job));
                if (JobLog.IsDone(job))
                    Notify.Result(job.Run, JobLog.Title(job, states), JobLog.Summary(job, states) + " Open " + AppInfo.Name + " for details.");
                else
                    Notify.Progress(Unesbrews.AppState.Capital(JobLog.Describe(job, states)));
                return;
            }
            var lib = Library.All();
            if (lib.Count == 0 || !CatalogService.Configured) return;
            CatalogIndex idx;
            try { idx = await CatalogService.RefreshAsync(); }
            catch (Exception e) { AppLog.Add("background update check: no catalog", e.Message); return; }
            var have = await CatalogService.InstalledAsync(lib.Select(a => a.ProductId).ToList());
            var entries = new List<CatalogEntry>();
            foreach (var app in idx.Apps)
            {
                CatalogService.InstalledCopy copy;
                var mine = lib.FirstOrDefault(a => a.ProductId == app.ProductId);
                if (mine == null || !CatalogService.RunsHere(app.Latest) || !have.TryGetValue(app.ProductId, out copy)) continue;
                entries.Add(new CatalogEntry
                {
                    App = app,
                    Installed = new InstalledApp { Title = mine.Title, ProductId = app.ProductId },
                    InstalledVersion = copy.Version,
                });
            }
            var now = Updates.Check(entries, false);
            if (now.Count == 0) return;
            var outcome = await Phone.StartCatalogAsync(now, now.Select(e => e.App.Latest).ToList(), s => { });
            if (outcome.Started) Notify.Progress("Updating " + outcome.Job.Title + "...");
            else if (outcome.Message != null) AppLog.Add("automatic update didn't start", outcome.Message);
        }
    }
}
