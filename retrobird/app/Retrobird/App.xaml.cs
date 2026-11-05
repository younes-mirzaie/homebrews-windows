using System;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Retrobird.Core;

namespace Retrobird
{
    public partial class App : Application
    {
        public static PhoneApplicationFrame RootFrame { get; private set; }

        public App()
        {
            UnhandledException += (s, e) =>
            {
                // keep the app alive on a stray async failure; show what happened
                e.Handled = true;
                var msg = e.ExceptionObject.Message;
                Deployment.Current.Dispatcher.BeginInvoke(() => MessageBox.Show(msg, "Retrobird error", MessageBoxButton.OK));
            };
            InitializeComponent();
            AppState.Client = new MailClient(HelperLauncher.Token);
            AppState.Client.StartHelper = () => HelperLauncher.EnsureRunningAsync(AppState.Client.Port, _ => { });

            RootFrame = new PhoneApplicationFrame();
            RootFrame.Navigated += CompleteInit;
            RootFrame.NavigationFailed += (s, e) =>
            {
                e.Handled = true;
                var msg = e.Exception != null ? e.Exception.Message : "unknown error";
                Deployment.Current.Dispatcher.BeginInvoke(() => MessageBox.Show("Couldn't open " + e.Uri + ":\n" + msg, "Retrobird error", MessageBoxButton.OK));
            };
            // the helper connection doesn't survive being suspended
            Microsoft.Phone.Shell.PhoneApplicationService.Current.Deactivated += (s, e) => AppState.Client.Close();
        }

        void CompleteInit(object sender, NavigationEventArgs e)
        {
            if (RootVisual != RootFrame) RootVisual = RootFrame;
            RootFrame.Navigated -= CompleteInit;
        }
    }
}
