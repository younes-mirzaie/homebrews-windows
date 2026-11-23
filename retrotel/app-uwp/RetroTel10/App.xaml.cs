// RetroTel for Windows 10 (UWP). The Telegram code (MTProto 2.0, login, chats, messages, photos)
// is the shared core of the Windows Phone 8.1 app, linked from ..\..\app\MetroTelegram. This
// project adds the Windows 10 pages. One frame holds the pages; Back goes back a page, then
// leaves the app.
using System;
using MetroTelegram;
using MetroTelegram.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace RetroTel
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            UnhandledException += (s, e) =>
            {
                // keep the app alive on a stray async failure, and say what happened
                e.Handled = true;
                CrashLog.Write("unhandled", e.Exception);
                var unused = Ui.AlertAsync("Something went wrong in RetroTel. Go back and try again. If it keeps happening, close RetroTel and open it again.");
            };
            // the connection doesn't survive being suspended; pages reconnect when they need it
            Suspending += (s, e) => TgSession.Disconnect();
            // back on screen: reconnect so new messages arrive again
            Resuming += async (s, e) =>
            {
                if (TgSession.Storage == null || !TgSession.Storage.IsAuthorized) return;
                try { await TgSession.EnsureConnectedAsync(); }
                catch (Exception ex) { CrashLog.Write("reconnect", ex); }
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
                SystemNavigationManager.GetForCurrentView().BackRequested += (s, a) =>
                {
                    if (a.Handled || !frame.CanGoBack) return;
                    a.Handled = true;
                    frame.GoBack();
                };
                frame.Navigated += (s, a) =>
                    SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                        frame.CanGoBack ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
            }
            if (frame.Content == null)
            {
                try { TgSession.Init(); }
                catch (Exception ex) { CrashLog.Write("session init", ex); }
                bool signedIn = TgSession.Storage != null && TgSession.Storage.IsAuthorized;
                frame.Navigate(signedIn ? typeof(MainPage) : typeof(SignInPage));
            }
            if (e.PrelaunchActivated) return;
            Window.Current.Activate();
        }

        /// <summary>After signing in or out: open that page and forget the pages before it.</summary>
        public static void StartOver(Type page)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null) return;
            frame.Navigate(page);
            frame.BackStack.Clear();
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;
        }
    }
}
