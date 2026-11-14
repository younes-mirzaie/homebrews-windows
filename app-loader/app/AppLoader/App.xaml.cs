using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;

namespace AppLoader
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
                var msg = AppLoader.Core.Friendly.Of(e.ExceptionObject);
                Deployment.Current.Dispatcher.BeginInvoke(() => MessageBox.Show(msg, AppLoader.Core.AppInfo.Name, MessageBoxButton.OK));
            };
            InitializeComponent();
            RootFrame = new PhoneApplicationFrame();
            RootFrame.Navigated += CompleteInit;
            RootFrame.NavigationFailed += (s, e) =>
            {
                e.Handled = true;
                var msg = e.Exception != null ? AppLoader.Core.Friendly.Of(e.Exception) : "Something went wrong opening that page.";
                Deployment.Current.Dispatcher.BeginInvoke(() => MessageBox.Show(msg, AppLoader.Core.AppInfo.Name, MessageBoxButton.OK));
            };
        }

        // a job still running: let the background agent report on it
        void Service_Deactivated(object sender, Microsoft.Phone.Shell.DeactivatedEventArgs e) { AppLoader.Core.Background.Nudge(); }
        void Service_Closing(object sender, Microsoft.Phone.Shell.ClosingEventArgs e) { AppLoader.Core.Background.Nudge(); }

        void CompleteInit(object sender, NavigationEventArgs e)
        {
            if (RootVisual != RootFrame) RootVisual = RootFrame;
            RootFrame.Navigated -= CompleteInit;
        }
    }
}
