// Retrobird for Windows Phone 8.1 (WinRT app). Starts the in-app mail engine on demand, keeps
// the back key going back one page, and shows any stray failure in plain words.
using System;
using System.Linq;
using Retrobird.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.Phone.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Retrobird
{
    public sealed partial class App : Application
    {
        public static Frame RootFrame { get; private set; }

        public App()
        {
            InitializeComponent();
            UnhandledException += (s, e) =>
            {
                // keep the app alive on a stray async failure; say what happened
                e.Handled = true;
                InAppHelper.Log("unhandled: " + e.Exception);
                var unused = Ui.AlertAsync(e.Message, "Retrobird");
            };
            // the mail engine runs inside the app (rbmail.dll)
            AppState.Client = new MailClient(InAppHelper.Token, InAppHelper.Port);
            AppState.Client.StartHelper = () => InAppHelper.EnsureRunningAsync(AppState.Client);
            // the engine connection doesn't survive being suspended; it reconnects on the next call.
            // While the app is on screen the background check leaves the mail alone.
            Suspending += (s, e) => { AppState.Client.Close(); Notify.AppVisible(false); };
            Resuming += (s, e) => Notify.AppVisible(true);
            _heartbeat.Interval = TimeSpan.FromMinutes(2);
            _heartbeat.Tick += (s, e) => Notify.AppVisible(true);
            HardwareButtons.BackPressed += OnBackPressed;
        }

        readonly DispatcherTimer _heartbeat = new DispatcherTimer();
        const string TaskName = "RetrobirdMailCheck";

        /// <summary>(Re)registers the background mail check at the chosen interval, or removes it.</summary>
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
                if (access == BackgroundAccessStatus.Denied)
                {
                    InAppHelper.Log("background check not allowed: " + access);
                    return;
                }
                var b = new BackgroundTaskBuilder { Name = TaskName, TaskEntryPoint = "RetrobirdTasks.MailCheckTask" };
                b.SetTrigger(new TimeTrigger((uint)Math.Max(30, minutes), false));
                b.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
                b.Register();
            }
            catch (Exception ex) { InAppHelper.Log("background check not scheduled: " + ex); }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Notify.AppVisible(true);
            _heartbeat.Start();
            // a new-mail notification opens that account's inbox
            if (!string.IsNullOrEmpty(e.Arguments) && e.Arguments.StartsWith("account="))
            {
                AppState.CurrentAccountId = Uri.UnescapeDataString(e.Arguments.Substring(8));
                AppState.CurrentFolder = "INBOX";
                AppState.CurrentFolderLabel = "inbox";
                AppState.CurrentFolderRole = "inbox";
            }
            RootFrame = Window.Current.Content as Frame;
            if (RootFrame == null)
            {
                // the stock frame brings the phone's turnstile page transition
                RootFrame = new Frame { Language = "en-US" };
                // a page that can't be built would otherwise just not open
                RootFrame.NavigationFailed += (s, ev) =>
                {
                    ev.Handled = true;
                    InAppHelper.Log("couldn't open " + ev.SourcePageType + ": " + ev.Exception);
                    var unused = Ui.AlertAsync("That page couldn't open. " + (ev.Exception == null ? "" : ev.Exception.Message), "Retrobird");
                };
                Window.Current.Content = RootFrame;
            }
            if (RootFrame.Content == null)
            {
                RootFrame.Navigate(typeof(MainPage));
                ScheduleBackgroundCheck();
            }
            else if (!string.IsNullOrEmpty(e.Arguments))
            {
                // already running: back to the inbox it asked for
                while (RootFrame.CanGoBack) RootFrame.GoBack();
                var main = RootFrame.Content as MainPage;
                if (main != null) main.ShowCurrent();
            }
            Window.Current.Activate();
        }

        // Back goes back one page; from the first page it leaves the app (the system does that
        // when we don't handle it). A page can take it first, e.g. to ask before discarding.
        void OnBackPressed(object sender, BackPressedEventArgs e)
        {
            var page = RootFrame == null ? null : RootFrame.Content as RbPage;
            if (page != null && page.HandleBack())
            {
                e.Handled = true;
                return;
            }
            if (RootFrame != null && RootFrame.CanGoBack)
            {
                e.Handled = true;
                RootFrame.GoBack();
            }
        }
    }
}
