using System;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using RetroFiles.Core;

namespace RetroFiles
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
                Deployment.Current.Dispatcher.BeginInvoke(() => MessageBox.Show(msg, "File Explorer", MessageBoxButton.OK));
            };
            InitializeComponent();
            AppState.Client = new RfxClient(HelperLauncher.Token);

            RootFrame = new PhoneApplicationFrame();
            RootFrame.Navigated += CompleteInit;
            RootFrame.NavigationFailed += (s, e) =>
            {
                e.Handled = true;
                var msg = e.Exception != null ? e.Exception.Message : "something went wrong";
                Deployment.Current.Dispatcher.BeginInvoke(() => MessageBox.Show("Couldn't open " + e.Uri + ":\n" + msg, "File Explorer", MessageBoxButton.OK));
            };
        }

        void CompleteInit(object sender, NavigationEventArgs e)
        {
            if (RootVisual != RootFrame) RootVisual = RootFrame;
            RootFrame.Navigated -= CompleteInit;
        }
    }
}
