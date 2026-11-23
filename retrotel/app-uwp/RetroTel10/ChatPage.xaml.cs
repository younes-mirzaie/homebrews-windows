// One chat: history, live messages, typing and read marks from the shared core
// (TL/MessagesServices.cs, TL/UpdatesService.cs through TgSession), sending text and photos.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MetroTelegram;
using MetroTelegram.Services;
using MetroTelegram.TL;
using MetroTelegram.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace RetroTel
{
    public sealed partial class ChatPage : Page
    {
        readonly ChatViewModel _chat = new ChatViewModel();
        readonly DispatcherTimer _typingReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        string _subtitle = "";
        DateTime _lastTypingSent = DateTime.MinValue;
        MessageItemViewModel _replyTo;
        bool _busy;

        public ChatPage()
        {
            InitializeComponent();
            MessagesList.ItemsSource = _chat.Messages;
            _typingReset.Tick += (s, e) => { _typingReset.Stop(); SubtitleText.Text = _subtitle; };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            TgSession.MessageReceived += OnMessage;
            TgSession.OutboxRead += OnOutboxRead;
            TgSession.UserTyping += OnTyping;

            if (e.NavigationMode == NavigationMode.Back && _chat.Messages.Count > 0) return;
            var args = e.Parameter as ChatArgs;
            if (args == null) return;
            _chat.PeerId = args.Id;
            _chat.AccessHash = args.AccessHash;
            _chat.PeerType = args.PeerType;
            _chat.Title = args.Title ?? "Chat";
            TitleText.Text = _chat.Title;
            _subtitle = args.PeerType == 3 ? "Channel" : args.PeerType == 2 ? "Group" : "";
            SubtitleText.Text = _subtitle;

            var row = MainPage.Model.Dialogs.FirstOrDefault(d => Math.Abs(d.Id) == Math.Abs(_chat.PeerId));
            if (row != null) row.UnreadCount = 0;

            await LoadHistoryAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            TgSession.MessageReceived -= OnMessage;
            TgSession.OutboxRead -= OnOutboxRead;
            TgSession.UserTyping -= OnTyping;
            _typingReset.Stop();
            base.OnNavigatedFrom(e);
        }

        MessagesService Messages { get { return new MessagesService(TgSession.RpcEngine); } }

        async Task LoadHistoryAsync()
        {
            SetBusy(true);
            try
            {
                await TgSession.EnsureConnectedAsync();
                List<MessageItemViewModel> history = await Messages.GetHistoryAsync(_chat.PeerId, _chat.AccessHash, _chat.PeerType, 50);
                _chat.Messages.Clear();
                foreach (var m in history) _chat.Messages.Add(m);
                ScrollToEnd();
                SetBusy(false);

                var last = history.LastOrDefault();
                if (last != null && TgSession.Updates != null)
                    await TgSession.Updates.ReadHistoryAsync(_chat.PeerId, _chat.AccessHash, _chat.PeerType, (int)last.Id);

                var media = new MediaService(TgSession.RpcEngine);
                foreach (var m in history.Where(x => x.PhotoId != 0).Reverse())
                    await LoadPhotoAsync(media, m);
            }
            catch (Exception ex)
            {
                SetBusy(false);
                TgSession.Disconnect();
                await Ui.ErrorAsync("Couldn't load messages.", ex);
            }
        }

        async Task LoadPhotoAsync(MediaService media, MessageItemViewModel m)
        {
            try
            {
                byte[] bytes = await media.LoadPhotoBytesAsync(m.PhotoId, m.PhotoAccessHash, m.PhotoFileReference, m.PhotoThumbSize);
                await OnUi(async () => m.PhotoImage = await Ui.ImageAsync(bytes, 560));
            }
            catch { /* the bubble keeps its text */ }
        }

        // ---------------- live updates (raised off the UI thread) ----------------

        void OnMessage(object sender, IncomingMessageEventArgs e)
        {
            if (Math.Abs(e.PeerId) != Math.Abs(_chat.PeerId)) return;
            var unused = OnUi(async () =>
            {
                if (e.MsgId != 0 && _chat.Messages.Any(m => m.Id == e.MsgId)) return;
                string author = "";
                if (!e.IsOut && (_chat.PeerType == 2 || _chat.PeerType == 3)) author = TgSession.GetUserName(e.FromId);
                string replyAuthor = null, replyText = null;
                if (e.ReplyToMsgId > 0)
                {
                    var original = _chat.Messages.FirstOrDefault(m => m.Id == e.ReplyToMsgId);
                    replyAuthor = original == null ? "" : original.IsOutgoing ? "You" : (original.AuthorName ?? "");
                    replyText = original == null ? "Message" : Preview(original);
                }
                var msg = new MessageItemViewModel
                {
                    Id = e.MsgId, FromId = e.FromId, AuthorName = author, Text = e.Text, Date = e.Date,
                    IsOutgoing = e.IsOut, DeliveryStatus = 1, ReplyToMsgId = e.ReplyToMsgId,
                    ReplyAuthor = replyAuthor, ReplyText = replyText,
                    PhotoId = e.PhotoId, PhotoAccessHash = e.PhotoAccessHash, PhotoFileReference = e.PhotoFileReference,
                    PhotoThumbSize = e.PhotoThumbSize, PhotoFullThumbSize = e.PhotoFullThumbSize,
                };
                // our own message comes back from the server: replace the "sending" copy
                var mine = e.IsOut ? _chat.Messages.LastOrDefault(m => m.Id == 0 && m.IsOutgoing && m.Text == e.Text) : null;
                if (mine != null) { mine.Id = e.MsgId; mine.DeliveryStatus = 1; return; }
                _chat.Messages.Add(msg);
                ScrollToEnd();
                if (e.PhotoId != 0) await LoadPhotoAsync(new MediaService(TgSession.RpcEngine), msg);
                if (!e.IsOut && e.MsgId > 0 && TgSession.Updates != null)
                    await TgSession.Updates.ReadHistoryAsync(_chat.PeerId, _chat.AccessHash, _chat.PeerType, (int)e.MsgId);
            });
        }

        void OnOutboxRead(object sender, OutboxReadEventArgs e)
        {
            if (e.PeerId != 0 && Math.Abs(e.PeerId) != Math.Abs(_chat.PeerId)) return;
            var unused = OnUi(() =>
            {
                foreach (var m in _chat.Messages)
                    if (m.IsOutgoing && m.Id > 0 && (e.MaxId == 0 || m.Id <= e.MaxId)) m.DeliveryStatus = 2;
            });
        }

        void OnTyping(object sender, UserTypingEventArgs e)
        {
            if (Math.Abs(e.PeerId) != Math.Abs(_chat.PeerId)) return;
            var unused = OnUi(() =>
            {
                SubtitleText.Text = _chat.PeerType == 1 ? "typing..." : TgSession.GetUserName(e.UserId) + " is typing...";
                _typingReset.Stop();
                _typingReset.Start();
            });
        }

        // ---------------- sending ----------------

        async void Send_Click(object sender, RoutedEventArgs e)
        {
            string text = (ComposeBox.Text ?? "").Trim();
            if (text.Length == 0) return;
            ComposeBox.Text = "";
            var reply = _replyTo;
            CancelReply_Click(null, null);

            var local = new MessageItemViewModel
            {
                Id = 0, Text = text, Date = DateTime.Now, IsOutgoing = true, DeliveryStatus = 0,
                ReplyToMsgId = reply != null ? (int)reply.Id : 0,
                ReplyAuthor = reply != null ? AuthorOf(reply) : null,
                ReplyText = reply != null ? Preview(reply) : null,
            };
            _chat.Messages.Add(local);
            ScrollToEnd();
            try
            {
                await TgSession.EnsureConnectedAsync();
                await Messages.SendMessageAsync(_chat.PeerId, _chat.AccessHash, _chat.PeerType, text, local.ReplyToMsgId);
                if (local.DeliveryStatus == 0) local.DeliveryStatus = 1;
            }
            catch (Exception ex)
            {
                _chat.Messages.Remove(local);
                ComposeBox.Text = text;
                TgSession.Disconnect();
                await Ui.ErrorAsync("Couldn't send.", ex, "Not sent");
            }
        }

        void ComposeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(ComposeBox.Text) || (DateTime.Now - _lastTypingSent).TotalSeconds < 5) return;
            _lastTypingSent = DateTime.Now;
            var rpc = TgSession.RpcEngine;
            if (rpc == null) return;
            var unused = Task.Run(async () =>
            {
                try { await new MessagesService(rpc).SetTypingAsync(_chat.PeerId, _chat.AccessHash, _chat.PeerType); } catch { }
            });
        }

        async void Attach_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string caption = (ComposeBox.Text ?? "").Trim();
            MessageItemViewModel local = null;
            try
            {
                SetBusy(true);
                byte[] jpeg = await ShrinkAsync(file);
                ComposeBox.Text = "";
                local = new MessageItemViewModel
                {
                    Id = 0, Text = caption, Date = DateTime.Now, IsOutgoing = true, DeliveryStatus = 0,
                    PhotoId = 1, PhotoImage = await Ui.ImageAsync(jpeg, 560),
                };
                _chat.Messages.Add(local);
                ScrollToEnd();

                await TgSession.EnsureConnectedAsync();
                var upload = new MediaUploadService(TgSession.RpcEngine);
                const int chunk = 32768;
                int parts = (jpeg.Length + chunk - 1) / chunk;
                long fileId = await upload.UploadPhotoBytesAsync(jpeg);
                await upload.SendPhotoMessageAsync(_chat.PeerId, _chat.AccessHash, _chat.PeerType, fileId, parts, jpeg, caption);
                local.DeliveryStatus = 1;
            }
            catch (Exception ex)
            {
                if (local != null) _chat.Messages.Remove(local);
                ComposeBox.Text = caption;
                TgSession.Disconnect();
                await Ui.ErrorAsync("Couldn't send the photo.", ex, "Not sent");
            }
            finally { SetBusy(false); }
        }

        /// <summary>The picture as a JPEG no larger than 1280 px on its long side (as Telegram's apps send it).</summary>
        static async Task<byte[]> ShrinkAsync(StorageFile file)
        {
            using (IRandomAccessStream input = await file.OpenReadAsync())
            {
                var decoder = await BitmapDecoder.CreateAsync(input);
                uint w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
                double scale = Math.Min(1.0, 1280.0 / Math.Max(w, h));
                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    new BitmapTransform
                    {
                        ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                        ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                        InterpolationMode = BitmapInterpolationMode.Fant,
                    },
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                var output = new InMemoryRandomAccessStream();
                var props = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.82, Windows.Foundation.PropertyType.Single) } };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, props);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    (uint)Math.Max(1, Math.Round(w * scale)), (uint)Math.Max(1, Math.Round(h * scale)), 96, 96, pixels.DetachPixelData());
                await encoder.FlushAsync();
                var bytes = new byte[output.Size];
                using (var reader = new DataReader(output.GetInputStreamAt(0)))
                {
                    await reader.LoadAsync((uint)output.Size);
                    reader.ReadBytes(bytes);
                }
                return bytes;
            }
        }

        // ---------------- message menu ----------------

        void Message_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var msg = element != null ? element.DataContext as MessageItemViewModel : null;
            if (msg == null) return;
            e.Handled = true;
            var menu = new MenuFlyout();
            if (msg.Id > 0) Add(menu, "Reply", () => StartReply(msg));
            if (!string.IsNullOrEmpty(msg.Text)) Add(menu, "Copy", () =>
            {
                var data = new DataPackage();
                data.SetText(msg.Text);
                Clipboard.SetContent(data);
            });
            if (msg.Id > 0)
            {
                Add(menu, "Delete for me", () => { var u = DeleteAsync(msg, false); });
                if (msg.IsOutgoing || _chat.PeerType == 1) Add(menu, "Delete for everyone", () => { var u = DeleteAsync(msg, true); });
            }
            if (menu.Items.Count > 0) menu.ShowAt(element, e.GetPosition(element));
        }

        static void Add(MenuFlyout menu, string text, Action click)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (s, a) => click();
            menu.Items.Add(item);
        }

        void StartReply(MessageItemViewModel msg)
        {
            _replyTo = msg;
            ReplyAuthorText.Text = AuthorOf(msg);
            ReplyMessageText.Text = Preview(msg);
            ReplyBar.Visibility = Visibility.Visible;
            ComposeBox.Focus(FocusState.Programmatic);
        }

        void CancelReply_Click(object sender, RoutedEventArgs e)
        {
            _replyTo = null;
            ReplyBar.Visibility = Visibility.Collapsed;
        }

        async Task DeleteAsync(MessageItemViewModel msg, bool everyone)
        {
            string question = everyone ? "Delete this message for everyone in the chat?" : "Delete this message on your devices? Others in the chat still see it.";
            if (!await Ui.ConfirmAsync(question, "Delete message", "Delete")) return;
            try
            {
                await TgSession.EnsureConnectedAsync();
                await Messages.DeleteMessagesAsync(_chat.PeerId, _chat.AccessHash, _chat.PeerType, (int)msg.Id, everyone);
                _chat.Messages.Remove(msg);
            }
            catch (Exception ex) { await Ui.ErrorAsync("Couldn't delete.", ex); }
        }

        string AuthorOf(MessageItemViewModel m)
        {
            if (m.IsOutgoing) return "You";
            if (!string.IsNullOrEmpty(m.AuthorName)) return m.AuthorName;
            return _chat.Title;
        }

        static string Preview(MessageItemViewModel m)
        {
            return !string.IsNullOrEmpty(m.Text) ? m.Text : m.HasPhoto ? "Photo" : "Message";
        }

        // ---------------- photos ----------------

        void Photo_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var msg = (sender as FrameworkElement)?.DataContext as MessageItemViewModel;
            if (msg == null || msg.PhotoId <= 1) return;
            e.Handled = true;
            Frame.Navigate(typeof(PhotoPage), new PhotoArgs
            {
                PhotoId = msg.PhotoId, AccessHash = msg.PhotoAccessHash, FileReference = msg.PhotoFileReference,
                Size = !string.IsNullOrEmpty(msg.PhotoFullThumbSize) ? msg.PhotoFullThumbSize : "y",
                Preview = msg.PhotoImage, Title = _chat.Title,
            });
        }

        // ---------------- helpers ----------------

        void ScrollToEnd()
        {
            if (_chat.Messages.Count > 0) MessagesList.ScrollIntoView(_chat.Messages[_chat.Messages.Count - 1]);
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            Busy.IsIndeterminate = busy;
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            AttachButton.IsEnabled = !busy;
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
                catch (Exception ex) { CrashLog.Write("chat ui", ex); done.TrySetResult(false); }
            });
            return done.Task;
        }
    }
}
