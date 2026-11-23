// Background agent: while Unesbrews is minimized or closed and a job (download + install) is
// running, it updates the progress notification and pops up the result. The job itself runs
// as SYSTEM outside the app (see Core/Jobs.cs); this only reads its log.
//
// Started a few seconds after the app leaves (ScheduledActionService.LaunchForTest, which works
// for sideloaded apps), then it reschedules itself the same way until the job is done. Without
// that, the system's periodic run (about every 30 minutes) still reports the result.
using System;
using System.Threading;
using Microsoft.Phone.Scheduler;
using AppLoader.Core;

namespace AppLoader.Agent
{
    public class ScheduledAgent : ScheduledTaskAgent
    {
        protected override void OnInvoke(ScheduledTask task)
        {
            try
            {
                var job = InstallJob.Load();
                if (job != null)
                {
                    // a run may last 25 seconds; leave some margin
                    var end = DateTime.UtcNow.AddSeconds(18);
                    bool done = false;
                    while (DateTime.UtcNow < end)
                    {
                        var states = JobLog.Parse(job, JobLog.Read(job));
                        if (JobLog.IsDone(job))
                        {
                            Notify.Result(job.Run, JobLog.AllOk(states) ? "done" : "not everything installed",
                                          JobLog.Summary(job, states) + " Open " + AppInfo.Name + " for details.");
                            done = true;
                            break;
                        }
                        Notify.Progress(JobLog.Describe(job, states));
                        Thread.Sleep(2000);
                    }
                    if (!done)
                    {
                        try { ScheduledActionService.LaunchForTest(task.Name, TimeSpan.FromSeconds(5)); }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            NotifyComplete();
        }
    }
}
