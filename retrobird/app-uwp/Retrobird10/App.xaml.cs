// Retrobird for Windows 10 (UWP). The same in-app mail engine as the Windows Phone 8.1 app
// (rbmail.dll, started on demand by Core/InAppHelper.cs), the shell with its navigation pane, and
// the background check run in this process (Core10/BackgroundCheck.cs).
using System;
using System.Linq;
using Retrobird.Core;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.UI.Xaml;

namespace Retrobird
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            UnhandledException += (s, e) =>
            {
                // keep the app alive on a stray async failure; say what happened
                e.Handled = true;
                InAppHelper.Log("unhandled: " + e.Exception);
                var unused = Ui.AlertAsync("Something went wrong in Retrobird. Go back and try again. If it keeps happening, close Retrobird and open it again.", "Retrobird");
            };
            // the mail engine runs inside the app (rbmail.dll)
            AppState.Client = new MailClient(InAppHelper.Token, InAppHelper.Port);
            AppState.Client.StartHelper = () => InAppHelper.EnsureRunningAsync(AppState.Client);
            MailClient.Log = InAppHelper.Log;
            // the engine connection doesn't survive being suspended; it reconnects on the next call
            Suspending += (s, e) => { AppState.Client.Close(); Notify.AppVisible(false); };
            Resuming += (s, e) => Notify.AppVisible(true);
            _heartbeat.Interval = TimeSpan.FromMinutes(2);
            _heartbeat.Tick += (s, e) => Notify.AppVisible(Window.Current.Visible);
        }

        readonly DispatcherTimer _heartbeat = new DispatcherTimer();
        const string TaskName = "RetrobirdMailCheck";

        /// <summary>(Re)registers the background mail check at the chosen interval, or removes it.
        /// It runs in this process (OnBackgroundActivated).</summary>
        public static async void ScheduleBackgroundCheck(bool changed = false)
        {
            try
            {
                int minutes = Notify.BackgroundMinutes;
                bool there = BackgroundTaskRegistration.AllTasks.Values.Any(t => t.Name == TaskName);
                if (there && !changed && minutes > 0) return;   // keep its timer running
                foreach (var t in BackgroundTaskRegistration.AllTasks.Values)
                    if (t.Name == TaskName) t.Unregister(true);
                if (minutes <= 0) return;
                var access = await BackgroundExecutionManager.RequestAccessAsync();
                if (access == BackgroundAccessStatus.Denied || access == BackgroundAccessStatus.DeniedByUser ||
                    access == BackgroundAccessStatus.DeniedBySystemPolicy)
                {
                    InAppHelper.Log("background check not allowed: " + access);
                    return;
                }
                var b = new BackgroundTaskBuilder { Name = TaskName };   // no entry point: in this process
                b.SetTrigger(new TimeTrigger((uint)Math.Max(15, minutes), false));
                b.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
                b.Register();
            }
            catch (Exception ex) { InAppHelper.Log("background check not scheduled: " + ex); }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Show(e.Arguments, e.PrelaunchActivated);
        }

        // a tap on a new-mail notification
        protected override void OnActivated(IActivatedEventArgs args)
        {
            var toast = args as ToastNotificationActivatedEventArgs;
            Show(toast != null ? toast.Argument : null, false);
        }

        void Show(string arguments, bool prelaunch)
        {
            // a new-mail notification opens that account's inbox; all inboxes stays as it is
            if (!string.IsNullOrEmpty(arguments) && arguments.StartsWith("account="))
                AppState.UseAccount(Uri.UnescapeDataString(arguments.Substring(8)));
            // an inbox pinned to Start opens just that inbox
            else if (!string.IsNullOrEmpty(arguments) && arguments.StartsWith(Pins.Prefix))
            {
                AppState.AllInboxes = false;
                AppState.UseAccount(Uri.UnescapeDataString(arguments.Substring(Pins.Prefix.Length)));
            }
            var shell = Window.Current.Content as Shell;
            if (shell == null)
            {
                shell = new Shell();
                Window.Current.Content = shell;
                ScheduleBackgroundCheck();
            }
            else if (!string.IsNullOrEmpty(arguments)) shell.OpenInbox();
            if (prelaunch) return;
            Notify.AppVisible(true);
            _heartbeat.Start();
            Window.Current.Activate();
        }

        /// <summary>The background check, in this process. Skipped while the app is on screen,
        /// where the inbox checks by itself.</summary>
        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            var deferral = args.TaskInstance.GetDeferral();
            try { await BackgroundCheck.RunAsync(); }
            catch (Exception e) { InAppHelper.Log("background check stopped: " + e.Message); }
            finally { deferral.Complete(); }
        }
    }
}
