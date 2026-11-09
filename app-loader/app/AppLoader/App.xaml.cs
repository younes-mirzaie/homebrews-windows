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
                var msg = e.ExceptionObject.Message;
                Deployment.Current.Dispatcher.BeginInvoke(() => MessageBox.Show(msg, "App Loader error", MessageBoxButton.OK));
            };
            InitializeComponent();
            RootFrame = new PhoneApplicationFrame();
            RootFrame.Navigated += CompleteInit;
            RootFrame.NavigationFailed += (s, e) =>
            {
                e.Handled = true;
                var msg = e.Exception != null ? e.Exception.Message : "unknown error";
                Deployment.Current.Dispatcher.BeginInvoke(() => MessageBox.Show(msg, "App Loader error", MessageBoxButton.OK));
            };
        }

        void CompleteInit(object sender, NavigationEventArgs e)
        {
            if (RootVisual != RootFrame) RootVisual = RootFrame;
            RootFrame.Navigated -= CompleteInit;
        }
    }
}
