// While a job runs: the app keeps running under the lock screen, and a background agent
// (AppLoader.Agent) reports progress and the result when the app is minimized or closed.
using System;
using Microsoft.Phone.Scheduler;
using Microsoft.Phone.Shell;

namespace AppLoader.Core
{
    public static class Background
    {
        public const string AgentName = "UnesbrewsAgent";

        /// <summary>Called when a job starts.</summary>
        public static void Watch()
        {
            // Keep running when the phone locks (it can't be turned back on until the app restarts,
            // which is fine: with no job running the app does nothing under the lock screen)
            try { PhoneApplicationService.Current.ApplicationIdleDetectionMode = IdleDetectionMode.Disabled; }
            catch (InvalidOperationException) { }
            try
            {
                if (ScheduledActionService.Find(AgentName) != null) ScheduledActionService.Remove(AgentName);
                ScheduledActionService.Add(new PeriodicTask(AgentName)
                {
                    Description = "Shows how installs and downloads started in " + AppInfo.Name + " are going.",
                });
            }
            catch (Exception e) { AppLog.Add("background progress isn't available", e.Message); }
        }

        /// <summary>Called when the app is minimized or closed with a job running: the agent runs
        /// within seconds (sideloaded apps may do this), then keeps rescheduling itself.</summary>
        public static void Nudge()
        {
            var job = InstallJob.Load();
            if (job == null || JobLog.IsDone(job)) return;
            try { ScheduledActionService.LaunchForTest(AgentName, TimeSpan.FromSeconds(5)); }
            catch (Exception) { }   // the periodic run (every 30 minutes) still comes
        }

        /// <summary>Called when a job has finished and its result is shown.</summary>
        public static void Stop()
        {
            try { if (ScheduledActionService.Find(AgentName) != null) ScheduledActionService.Remove(AgentName); }
            catch (Exception) { }
        }
    }
}
