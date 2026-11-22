using System;
using AppLoader.Core;
using AppInfo = AppLoader.Core.AppInfo;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unesbrews
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            UnhandledException += (s, e) =>
            {
                // keep the app alive on a stray async failure, and say what happened in plain words
                e.Handled = true;
                var msg = Friendly.Of(e.Exception);
                var t = Dialogs.TellAsync(AppInfo.Name, msg);
            };
            Resuming += (s, e) => { var t = AppState.ResumeAsync(); };
        }

        bool _watching;

        void Shown()
        {
            AppState.InFront = true;
            if (_watching) return;
            _watching = true;
            Window.Current.VisibilityChanged += (s, e) => AppState.InFront = e.Visible;
            var t = BackgroundCheck.RegisterAsync();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var root = Window.Current.Content as MainPage;
            if (root == null)
            {
                root = new MainPage();
                Window.Current.Content = root;
            }
            Window.Current.Activate();
            Shown();
            if (!e.PrelaunchActivated) { var t = AppState.ResumeAsync(); }
        }

        // A tap on a notification opens the app like a launch
        protected override void OnActivated(IActivatedEventArgs args)
        {
            if (!(Window.Current.Content is MainPage)) Window.Current.Content = new MainPage();
            Window.Current.Activate();
            Shown();
            var t = AppState.ResumeAsync();
        }

        // The update check every few hours (BackgroundCheck), in this process. Skipped while the
        // app is on screen or busy: it does the same itself then.
        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            var deferral = args.TaskInstance.GetDeferral();
            try
            {
                if (!AppState.InFront && !AppState.Busy && !AppState.Waiting) await BackgroundCheck.RunAsync();
            }
            catch (Exception e) { AppLog.Add("background update check stopped", e.Message); }
            finally { deferral.Complete(); }
        }
    }
}
