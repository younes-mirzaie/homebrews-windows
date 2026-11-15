// Text, hex and image viewer. Text files can be edited line by line (tap a line) and saved
// back through the helper, keeping their encoding and line endings.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using RetroFiles.Core;

namespace RetroFiles
{
    public partial class ViewerPage : PhoneApplicationPage
    {
        enum Mode { Text, Hex, Image }
        enum Enc { Utf8, Utf8Bom, Utf16LE, Utf16BE, Bytes }

        const int TextMax = 2 * 1024 * 1024;     // larger files open read-only (first part)
        const int HexPage = 64 * 1024;
        const int ImageMax = 24 * 1024 * 1024;

        string _path;
        byte[] _data;          // what has been read so far
        ulong _fileSize;
        Mode _mode;
        Enc _enc;
        string _newline = "\r\n";
        ObservableCollection<string> _lines;
        bool _dirty, _truncated;

        static readonly string[] ImageTypes = { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

        public ViewerPage() { InitializeComponent(); }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Metro.TurnstileIn(LayoutRoot, e.NavigationMode == NavigationMode.Back);
            if (e.NavigationMode == NavigationMode.Back && _data != null) return;
            _path = AppState.ViewerPath;
            TitleText.Text = FsItem.NameOf(_path);
            string ext = System.IO.Path.GetExtension(_path).ToLowerInvariant();
            try
            {
                Busy.Visibility = Visibility.Visible;
                bool image = ImageTypes.Contains(ext);
                await ReadAsync(image ? ImageMax : TextMax);
                if (image && ShowImage()) _mode = Mode.Image;
                else _mode = LooksBinary() ? Mode.Hex : Mode.Text;
                Render();
            }
            catch (Exception ex) { InfoText.Text = ex.Message; }
            Busy.Visibility = Visibility.Collapsed;
        }

        async Task ReadAsync(int limit)
        {
            var parts = new List<byte[]>();
            ulong offset = 0;
            int total = 0;
            for (;;)
            {
                var r = await AppState.Client.ReadAsync(_path, offset, Math.Min(4 * 1024 * 1024, limit - total));
                _fileSize = r.Item2;
                if (r.Item1.Length == 0) break;
                parts.Add(r.Item1);
                total += r.Item1.Length;
                offset += (ulong)r.Item1.Length;
                if (total >= limit || offset >= _fileSize) break;
            }
            _data = new byte[total];
            int at = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, _data, at, p.Length); at += p.Length; }
            _truncated = (ulong)_data.Length < _fileSize;
        }

        bool LooksBinary()
        {
            if (_data.Length >= 2 && ((_data[0] == 0xFF && _data[1] == 0xFE) || (_data[0] == 0xFE && _data[1] == 0xFF))) return false;
            int n = Math.Min(_data.Length, 8192);
            for (int i = 0; i < n; i++) if (_data[i] == 0) return true;
            return false;
        }

        bool ShowImage()
        {
            try
            {
                var bmp = new BitmapImage { DecodePixelWidth = 1600, CreateOptions = BitmapCreateOptions.None };
                bmp.SetSource(new MemoryStream(_data));
                Picture.Source = bmp;
                InfoText.Text = FsItem.FormatSize(_fileSize) + ", " + bmp.PixelWidth + " x " + bmp.PixelHeight;
                return true;
            }
            catch { return false; }
        }

        string Decode()
        {
            byte[] d = _data;
            if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) { _enc = Enc.Utf8Bom; return Encoding.UTF8.GetString(d, 3, d.Length - 3); }
            if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) { _enc = Enc.Utf16LE; return Encoding.Unicode.GetString(d, 2, (d.Length - 2) & ~1); }
            if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) { _enc = Enc.Utf16BE; return Encoding.BigEndianUnicode.GetString(d, 2, (d.Length - 2) & ~1); }
            if (IsValidUtf8(d)) { _enc = Enc.Utf8; return Encoding.UTF8.GetString(d, 0, d.Length); }
            _enc = Enc.Bytes;   // some 8-bit code page: show bytes as Latin-1 and save them back the same way
            var sb = new StringBuilder(d.Length);
            foreach (var b in d) sb.Append((char)b);
            return sb.ToString();
        }

        static bool IsValidUtf8(byte[] d)
        {
            int i = 0;
            while (i < d.Length)
            {
                byte b = d[i];
                int extra = b < 0x80 ? 0 : (b & 0xE0) == 0xC0 ? 1 : (b & 0xF0) == 0xE0 ? 2 : (b & 0xF8) == 0xF0 ? 3 : -1;
                if (extra < 0) return false;
                if (i + extra >= d.Length) return true;   // cut off at the end of what we read
                for (int k = 1; k <= extra; k++) if ((d[i + k] & 0xC0) != 0x80) return false;
                i += extra + 1;
            }
            return true;
        }

        byte[] Encode(string text)
        {
            switch (_enc)
            {
                case Enc.Utf8Bom: return new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
                case Enc.Utf16LE: return new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();
                case Enc.Utf16BE: return new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray();
                case Enc.Bytes: return text.Select(c => (byte)Math.Min((int)c, 255)).ToArray();
                default: return Encoding.UTF8.GetBytes(text);
            }
        }

        void Render()
        {
            ImageView.Visibility = _mode == Mode.Image ? Visibility.Visible : Visibility.Collapsed;
            Lines.Visibility = _mode == Mode.Image ? Visibility.Collapsed : Visibility.Visible;
            if (_mode == Mode.Text)
            {
                string text = Decode();
                _newline = text.Contains("\r\n") ? "\r\n" : text.Contains("\n") ? "\n" : "\r\n";
                _lines = new ObservableCollection<string>(text.Replace("\r\n", "\n").Split('\n'));
                Lines.ItemsSource = _lines;
                InfoText.Text = FsItem.FormatSize(_fileSize) + ", " + _lines.Count + " lines, " + EncName() + (_newline == "\r\n" ? ", CRLF" : ", LF") +
                                (_truncated ? "\nshowing the first " + FsItem.FormatSize((ulong)_data.Length) + " (read-only)" : "\ntap a line to edit it");
            }
            else if (_mode == Mode.Hex) RenderHex(0);
            BuildAppBar();
        }

        string EncName()
        {
            switch (_enc)
            {
                case Enc.Utf8Bom: return "UTF-8 with BOM";
                case Enc.Utf16LE: return "UTF-16 LE";
                case Enc.Utf16BE: return "UTF-16 BE";
                case Enc.Bytes: return "8-bit";
                default: return "UTF-8";
            }
        }

        int _hexShown;

        void RenderHex(int from)
        {
            if (from == 0) _lines = new ObservableCollection<string>();
            int end = Math.Min(_data.Length, from + HexPage);
            var sb = new StringBuilder();
            for (int off = from; off < end; off += 16)
            {
                sb.Clear();
                sb.Append(off.ToString("X8")).Append("  ");
                var chars = new StringBuilder();
                for (int k = 0; k < 16; k++)
                {
                    if (off + k < end)
                    {
                        byte b = _data[off + k];
                        sb.Append(b.ToString("X2")).Append(k == 7 ? "  " : " ");
                        chars.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }
                    else sb.Append(k == 7 ? "    " : "   ");
                }
                sb.Append(' ').Append(chars);
                _lines.Add(sb.ToString());
            }
            _hexShown = end;
            Lines.ItemsSource = _lines;
            InfoText.Text = FsItem.FormatSize(_fileSize) + ", hex, showing " + FsItem.FormatSize((ulong)_hexShown) +
                            (_hexShown < _data.Length || _truncated ? " (more in the menu)" : "");
        }

        void BuildAppBar()
        {
            var bar = new ApplicationBar();
            if (_mode == Mode.Text && _dirty)
            {
                var save = new ApplicationBarIconButton(new Uri("/Assets/AppBar/save.png", UriKind.Relative)) { Text = "save" };
                save.Click += Save_Click;
                bar.Buttons.Add(save);
            }
            if (_mode == Mode.Text && !_truncated)
            {
                var add = new ApplicationBarMenuItem("add a line at the end");
                add.Click += (s, e) => EditLine(_lines.Count, insert: true);
                bar.MenuItems.Add(add);
            }
            var toggle = new ApplicationBarMenuItem(_mode == Mode.Hex ? "view as text" : "view as hex");
            toggle.Click += (s, e) =>
            {
                if (_dirty && MessageBox.Show("Discard your unsaved edits?", "Unsaved", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                _dirty = false;
                _mode = _mode == Mode.Hex ? Mode.Text : Mode.Hex;
                Render();
            };
            bar.MenuItems.Add(toggle);
            if (_mode == Mode.Hex && _hexShown < _data.Length)
            {
                var more = new ApplicationBarMenuItem("show 64 KB more");
                more.Click += (s, e) => RenderHex(_hexShown);
                bar.MenuItems.Add(more);
            }
            bar.IsVisible = bar.Buttons.Count + bar.MenuItems.Count > 0;
            ApplicationBar = bar;
        }

        void Lines_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int i = Lines.SelectedIndex;
            if (i < 0) return;
            Lines.SelectedIndex = -1;
            if (_mode != Mode.Text || _truncated) return;
            EditLine(i, insert: false);
        }

        void EditLine(int index, bool insert)
        {
            string current = insert ? "" : _lines[index];
            Prompt.Show(this, insert ? "new line" : "line " + (index + 1), current, text =>
            {
                if (text == null) return;
                if (insert) _lines.Insert(index, text);
                else if (text.Length == 0 && current.Length > 0 &&
                         MessageBox.Show("Remove line " + (index + 1) + " entirely? Cancel keeps it as an empty line.", "Remove line", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                    _lines.RemoveAt(index);
                else _lines[index] = text;
                _dirty = true;
                BuildAppBar();
            });
        }

        async void Save_Click(object sender, EventArgs e)
        {
            if (AppState.IsSystemArea(_path) &&
                MessageBox.Show("This file is in a system area. A broken edit here can stop the phone from booting. Save anyway?", "System area", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;
            try
            {
                Busy.Visibility = Visibility.Visible;
                byte[] bytes = Encode(string.Join(_newline, _lines));
                await AppState.Client.WriteAsync(_path, bytes);
                _data = bytes;
                _fileSize = (ulong)bytes.Length;
                _dirty = false;
                AppState.FolderChanged = true;
                InfoText.Text = "saved, " + FsItem.FormatSize(_fileSize);
                BuildAppBar();
            }
            catch (Exception ex) { InfoText.Text = ex.Message; }
            Busy.Visibility = Visibility.Collapsed;
        }

        protected override void OnBackKeyPress(System.ComponentModel.CancelEventArgs e)
        {
            base.OnBackKeyPress(e);
            if (e.Cancel) return;
            if (_dirty && MessageBox.Show("Leave without saving your edits?", "Unsaved", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                e.Cancel = true;
        }
    }
}
