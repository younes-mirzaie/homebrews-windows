// Retrobird for Windows Phone 8.1 (WinRT app). Starts the in-app mail engine on demand, keeps
// the back key going back one page, and shows any stray failure in plain words.
using System;
using Retrobird.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
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
            // the engine connection doesn't survive being suspended; it reconnects on the next call
            Suspending += (s, e) => AppState.Client.Close();
            HardwareButtons.BackPressed += OnBackPressed;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
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
            if (RootFrame.Content == null) RootFrame.Navigate(typeof(MainPage));
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
