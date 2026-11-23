// A photo at full size. The chat's small copy shows at once while the large one downloads.
using System;
using System.Runtime.InteropServices.WindowsRuntime;
using MetroTelegram;
using MetroTelegram.TL;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace RetroTel
{
    public sealed class PhotoArgs
    {
        public long PhotoId;
        public long AccessHash;
        public byte[] FileReference;
        public string Size;
        public BitmapImage Preview;
        public string Title;
    }

    public sealed partial class PhotoPage : Page
    {
        byte[] _bytes;
        PhotoArgs _args;

        public PhotoPage() { InitializeComponent(); }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _args = e.Parameter as PhotoArgs;
            if (_args == null) return;
            if (_args.Preview != null) Picture.Source = _args.Preview;
            try
            {
                await TgSession.EnsureConnectedAsync();
                _bytes = await new MediaService(TgSession.RpcEngine).LoadPhotoBytesAsync(_args.PhotoId, _args.AccessHash, _args.FileReference, _args.Size);
                var bmp = await Ui.ImageAsync(_bytes);
                if (bmp != null) { Picture.Source = bmp; SaveButton.IsEnabled = true; }
            }
            catch (Exception ex) { await Ui.ErrorAsync("Couldn't load the photo.", ex); }
            finally { Busy.Visibility = Visibility.Collapsed; }
        }

        async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_bytes == null) return;
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = "RetroTel " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") };
            picker.FileTypeChoices.Add("JPEG picture", new[] { ".jpg" });
            StorageFile file = await picker.PickSaveFileAsync();
            if (file == null) return;
            try { await FileIO.WriteBufferAsync(file, _bytes.AsBuffer()); }
            catch (Exception ex) { await Ui.ErrorAsync("Couldn't save the photo.", ex); }
        }
    }
}
