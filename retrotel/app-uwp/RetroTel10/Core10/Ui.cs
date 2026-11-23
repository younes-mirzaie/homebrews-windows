// Small Windows 10 UI helpers: content dialogs (one at a time, as Windows allows), plain-words
// errors from the shared core, and images from downloaded bytes.
using System;
using System.Threading;
using System.Threading.Tasks;
using MetroTelegram.Services;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace RetroTel
{
    public static class Ui
    {
        static readonly SemaphoreSlim _dialog = new SemaphoreSlim(1);

        static async Task<bool> ShowAsync(string text, string title, string ok, string cancel)
        {
            await _dialog.WaitAsync();
            try
            {
                var d = new ContentDialog
                {
                    Title = title ?? "",
                    Content = new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = new TextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    },
                    PrimaryButtonText = ok,
                };
                if (cancel != null) d.SecondaryButtonText = cancel;
                return await d.ShowAsync() == ContentDialogResult.Primary;
            }
            catch (Exception e)
            {
                CrashLog.Write("dialog " + title, e);
                return false;
            }
            finally { _dialog.Release(); }
        }

        public static Task AlertAsync(string text, string title = "RetroTel") { return ShowAsync(text, title, "OK", null); }

        /// <summary>Asks to go ahead: the action's own word on the left, Cancel on the right.</summary>
        public static Task<bool> ConfirmAsync(string text, string title, string action) { return ShowAsync(text, title, action, "Cancel"); }

        /// <summary>A failure in plain words, with what was being tried first.</summary>
        public static Task ErrorAsync(string what, Exception e, string title = "RetroTel")
        {
            string reason = FriendlyErrors.ToText(e);
            return AlertAsync(string.IsNullOrEmpty(what) ? reason : what + " " + reason, title);
        }

        /// <summary>A picture from downloaded bytes, or null when they aren't a picture.</summary>
        public static async Task<BitmapImage> ImageAsync(byte[] bytes, int decodeWidth = 0)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                var stream = new InMemoryRandomAccessStream();
                using (var w = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    w.WriteBytes(bytes);
                    await w.StoreAsync();
                    await w.FlushAsync();
                    w.DetachStream();
                }
                stream.Seek(0);
                var bmp = new BitmapImage();
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                await bmp.SetSourceAsync(stream);
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>Initials for a name: "Ada Lovelace" gives "AL".</summary>
        public static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var words = name.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string s = words.Length >= 2 ? words[0].Substring(0, 1) + words[1].Substring(0, 1) : words[0].Substring(0, 1);
            return s.ToUpperInvariant();
        }

        /// <summary>The keyboard for a text box.</summary>
        public static void Scope(TextBox box, Windows.UI.Xaml.Input.InputScopeNameValue name)
        {
            var scope = new Windows.UI.Xaml.Input.InputScope();
            scope.Names.Add(new Windows.UI.Xaml.Input.InputScopeName { NameValue = name });
            box.InputScope = scope;
        }
    }
}
