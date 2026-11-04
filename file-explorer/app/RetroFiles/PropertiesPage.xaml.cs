using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using RetroFiles.Core;

namespace RetroFiles
{
    public partial class PropertiesPage : PhoneApplicationPage
    {
        string _path;
        StatInfo _stat;

        public PropertiesPage() { InitializeComponent(); }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _path = AppState.PropertiesPath;
            NameText.Text = FsItem.NameOf(_path);
            try
            {
                _stat = await AppState.Client.StatAsync(_path);
                Show();
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }

        static string When(DateTime t) { return t == DateTime.MinValue ? "-" : t.ToString("yyyy-MM-dd HH:mm:ss"); }

        void Show()
        {
            bool folder = (_stat.Attributes & FileAttr.Directory) != 0;
            InfoText.Text = "location: " + (FsItem.ParentOf(_path) ?? _path) +
                            "\ntype: " + (folder ? ((_stat.Attributes & FileAttr.ReparsePoint) != 0 ? "folder link / mount point" : "folder") : "file") +
                            (folder ? "" : "\nsize: " + FsItem.FormatSize(_stat.Size) + " (" + _stat.Size.ToString("N0") + " bytes)") +
                            "\ncreated: " + When(_stat.Created) + "\nmodified: " + When(_stat.Modified) + "\naccessed: " + When(_stat.Accessed);
            ReadOnlyBox.IsChecked = (_stat.Attributes & FileAttr.ReadOnly) != 0;
            HiddenBox.IsChecked = (_stat.Attributes & FileAttr.Hidden) != 0;
            SystemBox.IsChecked = (_stat.Attributes & FileAttr.System) != 0;
            ArchiveBox.IsChecked = (_stat.Attributes & FileAttr.Archive) != 0;
            var other = new List<string>();
            if ((_stat.Attributes & FileAttr.ReparsePoint) != 0) other.Add("reparse point");
            if ((_stat.Attributes & FileAttr.Compressed) != 0) other.Add("compressed");
            if ((_stat.Attributes & FileAttr.Encrypted) != 0) other.Add("encrypted");
            if ((_stat.Attributes & FileAttr.Temporary) != 0) other.Add("temporary");
            if ((_stat.Attributes & FileAttr.Offline) != 0) other.Add("offline");
            if ((_stat.Attributes & FileAttr.NotContentIndexed) != 0) other.Add("not indexed");
            OtherAttrs.Text = other.Count > 0 ? "also: " + string.Join(", ", other) + "  (0x" + ((uint)_stat.Attributes).ToString("x") + ")" : "";
            SizeButton.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        }

        async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_stat == null) return;
            var a = _stat.Attributes & ~(FileAttr.ReadOnly | FileAttr.Hidden | FileAttr.System | FileAttr.Archive | FileAttr.Directory);
            if (ReadOnlyBox.IsChecked == true) a |= FileAttr.ReadOnly;
            if (HiddenBox.IsChecked == true) a |= FileAttr.Hidden;
            if (SystemBox.IsChecked == true) a |= FileAttr.System;
            if (ArchiveBox.IsChecked == true) a |= FileAttr.Archive;
            if (AppState.IsSystemArea(_path) &&
                MessageBox.Show("This is a system area. Changing attributes here can break things. Continue?", "System area", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                return;
            try
            {
                await AppState.Client.SetAttributesAsync(_path, a);
                _stat = await AppState.Client.StatAsync(_path);
                Show();
                StatusText.Text = "attributes saved";
                AppState.FolderChanged = true;
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }

        async void Size_Click(object sender, RoutedEventArgs e)
        {
            SizeButton.IsEnabled = false;
            StatusText.Text = "measuring...";
            try
            {
                var r = await AppState.Client.LongAsync(new Progress<OpProgress>(_ => { }), CancellationToken.None, "DU", _path);
                ulong b = ulong.Parse(r[1]);
                StatusText.Text = "contents: " + FsItem.FormatSize(b) + " (" + b.ToString("N0") + " bytes), " + r[2] + " files, " + r[3] + " folders";
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            SizeButton.IsEnabled = true;
        }
    }
}
