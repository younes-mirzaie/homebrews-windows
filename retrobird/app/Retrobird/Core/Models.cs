// Data shown by the pages. Kept free of XAML types other than Visibility/FontWeights so the
// protocol code can be compiled and tested off the phone (see ../../../tests/ClientTest).
using System;
using System.Collections.Generic;
using System.ComponentModel;
#if WINDOWS_PHONE
using System.Windows;
#endif

namespace Retrobird.Core
{
    public class AccountInfo
    {
        public string Id, Name, Email, LastError;
        public int Unseen;
        public DateTime LastSync;
        public bool HasPassword;

        public string Title { get { return string.IsNullOrEmpty(Name) ? Email : Name; } }
        public string SubText
        {
            get
            {
                if (!HasPassword) return Email + " · password needed";
                if (!string.IsNullOrEmpty(LastError)) return Email + " · " + LastError;
                return Email + (Unseen > 0 ? " · " + Unseen + " unread" : "");
            }
        }
    }

    public class FolderInfo
    {
        public string Name, Display, Role;
        public int Unseen, Total;
        public bool NoSelect;

        /// <summary>Last path segment, indented by depth.</summary>
        public string Label
        {
            get
            {
                var parts = (Display ?? Name).Split('/');
                var last = parts[parts.Length - 1];
                if (Role == "inbox") last = "inbox";
                return new string(' ', (parts.Length - 1) * 4) + last.ToLowerInvariant();
            }
        }
        public string CountText { get { return Unseen > 0 ? Unseen.ToString() : ""; } }
        public double ItemOpacity { get { return NoSelect ? 0.5 : 1.0; } }
    }

    public class FolderList
    {
        public bool Online;
        public string Error;
        public List<FolderInfo> Folders = new List<FolderInfo>();
    }

    public class MessageSummary : INotifyPropertyChanged
    {
        public uint Uid;
        public long Size;
        public bool HasAttachment;
        public string From, To, Subject;
        public DateTime Date;
        string _flags = "";
        public bool ShowRecipient;   // Sent/Drafts lists show who it went to

        public string Flags
        {
            get { return _flags; }
            set { _flags = value ?? ""; Changed("Flags"); Changed("IsUnread"); Changed("Glyphs");
#if WINDOWS_PHONE
                  Changed("UnreadVisibility"); Changed("SenderWeight");
#endif
            }
        }
        public bool IsUnread { get { return _flags.IndexOf('S') < 0; } }
        public bool IsFlagged { get { return _flags.IndexOf('F') >= 0; } }
        public bool IsAnswered { get { return _flags.IndexOf('A') >= 0; } }

        public string Sender { get { return ShowRecipient ? "to " + Address.DisplayName(To) : Address.DisplayName(From); } }
        public string SubjectText { get { return string.IsNullOrEmpty(Subject) ? "(no subject)" : Subject; } }
        public string DateText { get { return Format.ShortDate(Date); } }

        /// <summary>Segoe UI Symbol glyphs: flag, replied, attachment.</summary>
        public string Glyphs
        {
            get
            {
                var s = "";
                if (IsFlagged) s += " ";
                if (IsAnswered) s += " ";
                if (HasAttachment) s += "";
                return s.TrimEnd();
            }
        }
#if WINDOWS_PHONE
        public Visibility UnreadVisibility { get { return IsUnread ? Visibility.Visible : Visibility.Collapsed; } }
        public FontWeight SenderWeight { get { return IsUnread ? FontWeights.SemiBold : FontWeights.Normal; } }
#endif

        public event PropertyChangedEventHandler PropertyChanged;
        void Changed(string name) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name)); }
    }

    public class SyncResult
    {
        public int Added, Unseen;
    }

    public class MessageList
    {
        public int Cached, Exists;
        public List<MessageSummary> Items = new List<MessageSummary>();
    }

    public class Attachment
    {
        public int Index;
        public string Name, ContentType;
        public long Size;
        public bool Inline;
        public string Label { get { return Name + "  (" + Format.Size(Size) + ")"; } }
    }

    public class MessageView
    {
        public string From, To, Cc, ReplyTo, Subject, MessageId, References, Html, Text;
        public DateTime Date;
        public bool Blocked;
        public List<Attachment> Attachments = new List<Attachment>();
    }

    public class Draft
    {
        public string To = "", Cc = "", Bcc = "", Subject = "", Body = "";
        public string InReplyTo, References, ReplyFolder;
        public uint ReplyUid;
    }

    public static class Format
    {
        public static string ShortDate(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            var local = d.ToLocalTime();
            var now = DateTime.Now;
            if (local.Date == now.Date) return local.ToString("t");
            if ((now.Date - local.Date).TotalDays < 7) return local.ToString("ddd");
            if (local.Year == now.Year) return local.ToString("MMM d");
            return local.ToString("d");
        }

        public static string LongDate(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            return d.ToLocalTime().ToString("f");
        }

        public static string Size(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024) + " KB";
            return (b / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }

        public static DateTime FromUnix(string s)
        {
            long v;
            if (!long.TryParse(s, out v) || v <= 0) return DateTime.MinValue;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(v);
        }
    }

    public static class Address
    {
        /// <summary>"Ann Example &lt;ann@x.org&gt;, bob@y.org" -> "Ann Example, bob@y.org" (first two).</summary>
        public static string DisplayName(string list)
        {
            if (string.IsNullOrEmpty(list)) return "(unknown)";
            var names = new List<string>();
            foreach (var a in Split(list))
            {
                var n = NameOf(a);
                if (n.Length > 0) names.Add(n);
                if (names.Count == 2) break;
            }
            return names.Count == 0 ? list : string.Join(", ", names.ToArray());
        }

        public static string NameOf(string mailbox)
        {
            var lt = mailbox.LastIndexOf('<');
            if (lt > 0)
            {
                var n = mailbox.Substring(0, lt).Trim().Trim('"').Trim();
                if (n.Length > 0) return n;
            }
            return EmailOf(mailbox);
        }

        public static string EmailOf(string mailbox)
        {
            var lt = mailbox.LastIndexOf('<');
            var gt = mailbox.LastIndexOf('>');
            if (lt >= 0 && gt > lt) return mailbox.Substring(lt + 1, gt - lt - 1).Trim();
            return mailbox.Trim();
        }

        /// <summary>Splits an address list on commas outside quotes and angle brackets.</summary>
        public static List<string> Split(string list)
        {
            var r = new List<string>();
            if (string.IsNullOrEmpty(list)) return r;
            bool q = false, ang = false;
            int start = 0;
            for (int i = 0; i < list.Length; i++)
            {
                char c = list[i];
                if (c == '"') q = !q;
                else if (!q && c == '<') ang = true;
                else if (!q && c == '>') ang = false;
                else if (!q && !ang && (c == ',' || c == ';'))
                {
                    var part = list.Substring(start, i - start).Trim();
                    if (part.Length > 0 && part != "...") r.Add(part);
                    start = i + 1;
                }
            }
            var last = list.Substring(start).Trim();
            if (last.Length > 0 && last != "...") r.Add(last);
            return r;
        }
    }
}
