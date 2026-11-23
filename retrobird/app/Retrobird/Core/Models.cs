// Data shown by the pages. Kept free of XAML types other than Visibility/FontWeights so the
// protocol code can be compiled and tested off the phone (see ../../../tests/ClientTest).
using System;
using System.Collections.Generic;
using System.ComponentModel;
#if WINDOWS_PHONE_APP || WINDOWS_UWP
using Windows.UI.Text;
using Windows.UI.Xaml;
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
        /// <summary>A short name for the account: the provider's name for a known service
        /// ("Gmail"), else the address's domain.</summary>
        public string ShortName
        {
            get
            {
                var p = Provider.ForEmail(Email);
                if (p != null && !string.IsNullOrEmpty(p.Name)) return p.ShortName;
                var d = Provider.Domain(Email);
                return d.Length > 0 ? d : Title;
            }
        }
        /// <summary>One line on how the account is doing, for the settings list.</summary>
        public string StateText
        {
            get
            {
                if (!HasPassword) return "Sign in again: the saved sign-in can't be used any more.";
                if (!string.IsNullOrEmpty(LastError)) return LastError;
                if (LastSync != DateTime.MinValue) return "Checked at " + LastSync.ToLocalTime().ToString("t");
                return Unseen > 0 ? Unseen + " unread" : "Not checked yet";
            }
        }
        public bool NeedsAttention { get { return !HasPassword || !string.IsNullOrEmpty(LastError); } }
#if WINDOWS_PHONE_APP || WINDOWS_UWP
        public Visibility AttentionVisibility { get { return NeedsAttention ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility OkVisibility { get { return NeedsAttention ? Visibility.Collapsed : Visibility.Visible; } }
#endif
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
        public string AccountId;                 // set in all inboxes
        /// <summary>The account's short name, shown on each row in all inboxes (a property, so
        /// the list template can bind to it).</summary>
        public string AccountLabel { get; set; }
        /// <summary>The first words of the text, one line (empty until the engine fetched it).</summary>
        public string Preview { get; set; }

        public string Flags
        {
            get { return _flags; }
            set { _flags = value ?? ""; Changed("Flags"); Changed("IsUnread"); Changed("IsFlagged"); Changed("Glyphs");
#if WINDOWS_PHONE_APP || WINDOWS_UWP
                  Changed("UnreadVisibility"); Changed("ReadVisibility"); Changed("SenderWeight");
                  Changed("FlagVisibility"); Changed("RepliedVisibility");
#endif
            }
        }
        public bool IsUnread { get { return _flags.IndexOf('S') < 0; } }
        public bool IsFlagged { get { return _flags.IndexOf('F') >= 0; } }
        public bool IsAnswered { get { return _flags.IndexOf('A') >= 0; } }

        public string Sender { get { return ShowRecipient ? "to " + Address.DisplayName(To) : Address.DisplayName(From); } }
        public string SubjectText { get { return string.IsNullOrEmpty(Subject) ? "(no subject)" : Subject; } }
        public string DateText { get { return Format.ShortDate(Date); } }
        /// <summary>The time on today's and this week's mail, the date on older mail (the list is
        /// grouped by day, so the day itself is in the group header).</summary>
        public string TimeText { get { return Format.ListTime(Date); } }
        /// <summary>The day group this message is listed under ("today", "yesterday", "monday"...).</summary>
        public string DayGroup { get { return Format.DayGroup(Date); } }

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
#if WINDOWS_PHONE_APP || WINDOWS_UWP
        static Visibility V(bool b) { return b ? Visibility.Visible : Visibility.Collapsed; }
        public Visibility UnreadVisibility { get { return V(IsUnread); } }
        public Visibility ReadVisibility { get { return V(!IsUnread); } }
        public Visibility FlagVisibility { get { return V(IsFlagged); } }
        public Visibility RepliedVisibility { get { return V(IsAnswered); } }
        public Visibility AttachmentVisibility { get { return V(HasAttachment); } }
        public Visibility AccountVisibility { get { return V(!string.IsNullOrEmpty(AccountLabel)); } }
        public Visibility PreviewVisibility { get { return V(!string.IsNullOrEmpty(Preview)); } }
#if WINDOWS_UWP
        // Windows 10: unread mail has the sender in Semibold (the Mail app's convention)
        public FontWeight SenderWeight { get { return IsUnread ? FontWeights.SemiBold : FontWeights.Normal; } }
#else
        // Metro: the sender is light (Semilight); unread mail gets Regular plus an accent subject
        public FontWeight SenderWeight { get { return IsUnread ? FontWeights.Normal : FontWeights.SemiLight; } }
#endif
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
        public string SizeText { get { return Format.Size(Size) + " \u00B7 tap to open"; } }
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

        /// <summary>Time for mail of the last week, else the date (the list's day group says the day).</summary>
        public static string ListTime(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            var local = d.ToLocalTime();
            if ((DateTime.Now.Date - local.Date).TotalDays < 7) return local.ToString("t");
            return local.Year == DateTime.Now.Year ? local.ToString("MMM d") : local.ToString("d");
        }

        /// <summary>The day group a message is listed under, lowercase (Metro casing; Windows 10
        /// capitalizes it): today, yesterday, the weekday for this week, then "last week", the
        /// month this year, and the year for older mail.</summary>
        public static string DayGroup(DateTime d)
        {
            if (d == DateTime.MinValue) return "older";
            var day = d.ToLocalTime().Date;
            var today = DateTime.Now.Date;
            double ago = (today - day).TotalDays;
            if (ago <= 0) return "today";
            if (ago < 2) return "yesterday";
            if (ago < 7) return day.ToString("dddd").ToLowerInvariant();
            if (ago < 14) return "last week";
            if (day.Year == today.Year) return day.ToString("MMMM").ToLowerInvariant();
            return day.Year.ToString();
        }

        /// <summary>"last week" -> "Last week" (Windows 10 sentence case).</summary>
        public static string Sentence(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
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

    public sealed class OAuthDevice
    {
        public string UserCode, Url, DeviceCode;
        public int Interval, ExpiresIn;
    }
}
