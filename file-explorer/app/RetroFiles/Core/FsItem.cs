using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace RetroFiles.Core
{
    [Flags]
    public enum FileAttr : uint
    {
        ReadOnly = 0x1, Hidden = 0x2, System = 0x4, Directory = 0x10, Archive = 0x20,
        Normal = 0x80, Temporary = 0x100, ReparsePoint = 0x400, Compressed = 0x800,
        Offline = 0x1000, NotContentIndexed = 0x2000, Encrypted = 0x4000
    }

    /// <summary>One row in a folder listing (or a drive/bookmark on the Places screen).</summary>
    public class FsItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public FileAttr Attributes { get; set; }
        public ulong Size { get; set; }
        public DateTime Modified { get; set; }
        public DateTime Created { get; set; }
        public string Detail { get; set; }    // overrides the second line (drives, bookmarks)
        public bool IsPlace { get; set; }

        public bool IsFolder { get { return (Attributes & FileAttr.Directory) != 0; } }
        public bool IsLink { get { return (Attributes & FileAttr.ReparsePoint) != 0; } }

        // which one-color icon the list draws (drives and places, link, folder, file)
        public Visibility PlaceIcon { get { return IsPlace ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility FolderIcon { get { return !IsPlace && IsFolder ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility LinkIcon { get { return !IsPlace && IsFolder && IsLink ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility FileIcon { get { return !IsPlace && !IsFolder ? Visibility.Visible : Visibility.Collapsed; } }

        public string SubText
        {
            get
            {
                if (Detail != null) return Detail;
                string flags = AttrLetters(Attributes);
                string when = Modified == DateTime.MinValue ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
                if (IsFolder) return (IsLink ? "link  " : "") + when + (flags.Length > 0 ? "  " + flags : "");
                return FormatSize(Size) + "  " + when + (flags.Length > 0 ? "  " + flags : "");
            }
        }

        /// <summary>Hidden and system items are drawn dimmer, like Explorer.</summary>
        public double ItemOpacity
        {
            get { return (Attributes & (FileAttr.Hidden | FileAttr.System)) != 0 ? 0.6 : 1.0; }
        }

        public static string AttrLetters(FileAttr a)
        {
            return ((a & FileAttr.ReadOnly) != 0 ? "R" : "") + ((a & FileAttr.Hidden) != 0 ? "H" : "") +
                   ((a & FileAttr.System) != 0 ? "S" : "") + ((a & FileAttr.Archive) != 0 ? "A" : "");
        }

        public static string FormatSize(ulong b)
        {
            if (b < 1024) return b + " B";
            double v = b / 1024.0;
            string[] units = { "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return v.ToString(v < 10 ? "0.0" : "0") + " " + units[i];
        }

        public static string NameOf(string path)
        {
            string p = path.TrimEnd('\\');
            int i = p.LastIndexOf('\\');
            return i < 0 ? p : p.Substring(i + 1);
        }

        public static string ParentOf(string path)
        {
            string p = path.TrimEnd('\\');
            int i = p.LastIndexOf('\\');
            if (i < 0) return null;
            if (i == 2 && p[1] == ':') return p.Substring(0, 3);   // C:\folder -> C:\
            return p.Substring(0, i);
        }

        public static string Combine(string dir, string name)
        {
            return dir.EndsWith("\\") ? dir + name : dir + "\\" + name;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }
}
