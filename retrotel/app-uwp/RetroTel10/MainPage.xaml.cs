// Chats and contacts. The lists come from the shared core (TL/DialogsService.cs,
// TL/ContactsService.cs); profile pictures load one by one after the list shows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MetroTelegram;
using MetroTelegram.Services;
using MetroTelegram.TL;
using MetroTelegram.ViewModels;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace RetroTel
{
    /// <summary>What a chat page opens with.</summary>
    public sealed class ChatArgs
    {
        public long Id;
        public long AccessHash;
        public int PeerType;      // 1 user, 2 group, 3 channel
        public string Title;
    }

    public sealed partial class MainPage : Page
    {
        /// <summary>The chats and contacts, kept while the app runs (the chat page updates unread counts).</summary>
        public static readonly MainViewModel Model = new MainViewModel();

        bool _loading, _loadedOnce;

        public MainPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            ChatsList.ItemsSource = Model.Dialogs;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            string crash = CrashLog.TakeLast();
            if (crash != null)
            {
                if (crash.Length > 600) crash = crash.Substring(0, 600) + "...";
                await Ui.AlertAsync("RetroTel closed unexpectedly last time. Details for the developer:\n\n" + crash);
            }
            if (!_loadedOnce || e.NavigationMode != NavigationMode.Back) await LoadAsync();
        }

        void Refresh_Click(object sender, RoutedEventArgs e) { var unused = LoadAsync(); }

        void Settings_Click(object sender, RoutedEventArgs e) { Frame.Navigate(typeof(SettingsPage)); }

        void Chat_Click(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatItemViewModel;
            if (chat == null) return;
            Frame.Navigate(typeof(ChatPage), new ChatArgs { Id = chat.Id, AccessHash = chat.AccessHash, PeerType = chat.PeerType, Title = chat.Title });
        }

        void Contact_Click(object sender, ItemClickEventArgs e)
        {
            var c = e.ClickedItem as ContactItemViewModel;
            if (c == null) return;
            Frame.Navigate(typeof(ChatPage), new ChatArgs { Id = c.UserId, AccessHash = c.AccessHash, PeerType = 1, Title = c.FullName });
        }

        async Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;
            SetBusy(true, "Connecting...");
            try
            {
                await TgSession.EnsureConnectedAsync(s => SetBusy(true, s));
                SetBusy(true, "Updating chats...");
                var dialogs = await new DialogsService(TgSession.RpcEngine).GetDialogsAsync(50);
                await OnUi(() =>
                {
                    Model.Dialogs.Clear();
                    foreach (var d in dialogs) Model.Dialogs.Add(d);
                    ChatsEmpty.Visibility = Model.Dialogs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                });
                _loadedOnce = true;

                SetBusy(true, "Updating contacts...");
                try
                {
                    List<ContactItemViewModel> contacts = await new ContactsService(TgSession.RpcEngine).GetContactsAsync();
                    var groups = AlphaKeyGroup<ContactItemViewModel>.CreateGroups(contacts);
                    await OnUi(() =>
                    {
                        ContactGroups.Source = groups;
                        ContactsEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    });
                }
                catch (Exception ex) { CrashLog.Write("contacts", ex); }

                SetBusy(false);
                await LoadAvatarsAsync(dialogs);
            }
            catch (Exception ex)
            {
                SetBusy(false);
                string raw = ex.Message ?? "";
                if (raw.Contains("AUTH_KEY_UNREGISTERED") || raw.Contains("SESSION_REVOKED") || raw.Contains("UNAUTHORIZED"))
                {
                    TgSession.Disconnect();
                    TgSession.Storage.Clear();
                    await Ui.AlertAsync("You were signed out of Telegram on this device. Sign in again.", "Signed out");
                    App.StartOver(typeof(SignInPage));
                    return;
                }
                TgSession.Disconnect();
                await Ui.ErrorAsync("Couldn't update your chats.", ex);
            }
            finally { _loading = false; }
        }

        async Task LoadAvatarsAsync(List<ChatItemViewModel> dialogs)
        {
            var media = new MediaService(TgSession.RpcEngine);
            foreach (var d in dialogs.Where(x => x.PhotoId != 0 && x.AvatarImage == null))
            {
                try
                {
                    byte[] bytes = await media.LoadAvatarBytesAsync(d.Id, d.AccessHash, d.PeerType, d.PhotoId);
                    await OnUi(async () => d.AvatarImage = await Ui.ImageAsync(bytes, 80));
                }
                catch { /* a missing picture keeps the initials */ }
            }
        }

        void SetBusy(bool busy, string text = "")
        {
            var unused = OnUi(() =>
            {
                Busy.IsIndeterminate = busy;
                Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                StatusText.Text = busy ? (text ?? "") : "";
                RefreshButton.IsEnabled = !busy;
            });
        }

        Task OnUi(DispatchedHandler action)
        {
            if (Dispatcher.HasThreadAccess) { action(); return Task.CompletedTask; }
            return Dispatcher.RunAsync(CoreDispatcherPriority.Normal, action).AsTask();
        }

        Task OnUi(Func<Task> action)
        {
            var done = new TaskCompletionSource<bool>();
            var unused = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try { await action(); done.TrySetResult(true); }
                catch (Exception ex) { done.TrySetException(ex); }
            });
            return done.Task;
        }
    }
}
