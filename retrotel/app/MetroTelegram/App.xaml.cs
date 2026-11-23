using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using MetroTelegram.Crypto;
using MetroTelegram.TL;
using MetroTelegram.Transport;
using MetroTelegram.ViewModels;

namespace MetroTelegram
{
    public partial class App : Application
    {
        public static PhoneApplicationFrame RootFrame { get; private set; }
        public static MainViewModel ViewModel { get; private set; }

        // The Telegram session lives in the shared core (Core/TgSession.cs); these forward to it.
        public static AuthKeyStorage Storage => TgSession.Storage;
        public static MtprotoTcpTransport Transport => TgSession.Transport;
        public static TelegramRpcEngine RpcEngine => TgSession.RpcEngine;
        public static UpdatesService Updates => TgSession.Updates;

        public static event EventHandler<IncomingMessageEventArgs> LiveMessageReceived;
        public static event EventHandler<OutboxReadEventArgs> LiveOutboxRead
        {
            add { TgSession.OutboxRead += value; }
            remove { TgSession.OutboxRead -= value; }
        }
        public static event EventHandler<UserTypingEventArgs> LiveUserTyping
        {
            add { TgSession.UserTyping += value; }
            remove { TgSession.UserTyping -= value; }
        }

        public static Dictionary<long, string> UsersCache => TgSession.UsersCache;
        public static void CacheUser(long id, string name) { TgSession.CacheUser(id, name); }
        public static void CacheAccessHash(long id, long accessHash) { TgSession.CacheAccessHash(id, accessHash); }
        public static long GetAccessHash(long id) { return TgSession.GetAccessHash(id); }
        public static string GetUserName(long id) { return TgSession.GetUserName(id); }

        public static Task EnsureTelegramConnectedAsync(Action<string> statusCallback = null)
        {
            return TgSession.EnsureConnectedAsync(statusCallback);
        }

        public App()
        {
            MetroTelegram.Services.CrashLog.Mark("App() start");
            UnhandledException += Application_UnhandledException;

            try
            {
                InitializeComponent();
                MetroTelegram.Services.CrashLog.Mark("App.xaml loaded");
                InitializePhoneApplication();
                MetroTelegram.Services.CrashLog.Mark("frame ready");

                ViewModel = new MainViewModel();
                TgSession.Init();
                TgSession.MessageReceived += OnGlobalMessageReceived;
                MetroTelegram.Services.CrashLog.Mark("storage loaded");
            }
            catch (Exception ex)
            {
                MetroTelegram.Services.CrashLog.Write("App()", ex);
                throw;
            }

            if (Debugger.IsAttached)
            {
                Application.Current.Host.Settings.EnableFrameRateCounter = true;
            }
        }

        private static void OnGlobalMessageReceived(object sender, IncomingMessageEventArgs e)
        {
            LiveMessageReceived?.Invoke(sender, e);

            Deployment.Current.Dispatcher.BeginInvoke(() =>
            {
                long targetAbsId = Math.Abs(e.PeerId);

                var existingDialog = ViewModel.Dialogs.FirstOrDefault(d =>
                    d.Id == e.PeerId ||
                    Math.Abs(d.Id) == targetAbsId);

                int pinnedCount = ViewModel.Dialogs.Count(d => d.IsPinned);

                if (existingDialog != null)
                {
                    existingDialog.LastMessage = e.Text;
                    existingDialog.Date = e.Date;
                    if (!e.IsOut) existingDialog.UnreadCount++;

                    int oldIndex = ViewModel.Dialogs.IndexOf(existingDialog);
                    int targetIndex = existingDialog.IsPinned ? 0 : pinnedCount;

                    if (oldIndex != targetIndex && oldIndex >= 0)
                    {
                        ViewModel.Dialogs.RemoveAt(oldIndex);
                        if (targetIndex > ViewModel.Dialogs.Count) targetIndex = ViewModel.Dialogs.Count;
                        ViewModel.Dialogs.Insert(targetIndex, existingDialog);
                    }
                }
                else
                {
                    string title = e.PeerType == 3 ? "Channel " + e.PeerId : (e.PeerType == 2 ? "Group " + e.PeerId : "Chat " + e.PeerId);
                    var newDialog = new ChatItemViewModel
                    {
                        Id = e.PeerId,
                        AccessHash = App.GetAccessHash(e.PeerId),
                        PeerType = e.PeerType,
                        Title = title,
                        LastMessage = e.Text,
                        Date = e.Date,
                        UnreadCount = e.IsOut ? 0 : 1,
                        AvatarInitials = "TG",
                        IsPinned = false,
                        IsChannel = (e.PeerType == 3)
                    };

                    int insertIndex = pinnedCount <= ViewModel.Dialogs.Count ? pinnedCount : ViewModel.Dialogs.Count;
                    ViewModel.Dialogs.Insert(insertIndex, newDialog);
                }

                int totalUnread = ViewModel.Dialogs.Sum(d => d.UnreadCount);
                var topChat = ViewModel.Dialogs.FirstOrDefault();
                string senderName = topChat != null ? topChat.Title : "";
                string lastMsg = topChat != null ? topChat.LastMessage : "";

                TileService.UpdatePrimaryTile(totalUnread, senderName, lastMsg);
            });
        }

        private void Application_Launching(object sender, LaunchingEventArgs e) { }
        private void Application_Activated(object sender, ActivatedEventArgs e) { }
        private void Application_Deactivated(object sender, DeactivatedEventArgs e) { }
        private void Application_Closing(object sender, ClosingEventArgs e) { }

        private void RootFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            MetroTelegram.Services.CrashLog.Write("NavigationFailed " + e.Uri, e.Exception);
            if (Debugger.IsAttached) Debugger.Break();
        }

        private void Application_UnhandledException(object sender, ApplicationUnhandledExceptionEventArgs e)
        {
            MetroTelegram.Services.CrashLog.Write("UnhandledException", e.ExceptionObject);
            if (Debugger.IsAttached) Debugger.Break();
        }

        #region Phone application initialization
        private bool _phoneApplicationInitialized = false;

        private void InitializePhoneApplication()
        {
            if (_phoneApplicationInitialized) return;

            RootFrame = new TransitionFrame();
            RootFrame.Navigated += CompleteInitializePhoneApplication;
            RootFrame.NavigationFailed += RootFrame_NavigationFailed;

            Microsoft.Phone.Controls.TiltEffect.SetIsTiltEnabled(RootFrame, true);

            _phoneApplicationInitialized = true;
        }

        private void CompleteInitializePhoneApplication(object sender, NavigationEventArgs e)
        {
            MetroTelegram.Services.CrashLog.Mark("first navigation done: " + e.Uri);
            if (RootVisual != RootFrame) RootVisual = RootFrame;
            RootFrame.Navigated -= CompleteInitializePhoneApplication;
        }
        #endregion
    }
}