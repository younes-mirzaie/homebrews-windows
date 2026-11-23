// Replies and forwards: who they go to, the subject prefix and the quoted text. Shared by the
// Windows Phone 8.1 and Windows 10 apps, and plain C# so it is tested off the phone too.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Retrobird.Core
{
    public static class Drafts
    {
        /// <summary>"Re:" or "Fwd:" in front, unless it's already there (or a common foreign "Re").</summary>
        public static string Prefixed(string subject, string prefix)
        {
            subject = subject ?? "";
            var s = subject.TrimStart();
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return subject;
            if (prefix == "Re:" && (s.StartsWith("AW:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("SV:", StringComparison.OrdinalIgnoreCase)))
                return subject;
            return prefix + " " + subject;
        }

        public static string Quote(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Select(l => ">" + (l.StartsWith(">") ? "" : " ") + l).ToArray());
        }

        /// <summary>A reply to the sender (or Reply-To), or to everyone except me.</summary>
        public static Draft Reply(MessageView v, string folder, uint uid, bool all, string myEmail)
        {
            var to = !string.IsNullOrEmpty(v.ReplyTo) ? v.ReplyTo : v.From;
            var d = new Draft
            {
                To = to ?? "",
                Subject = Prefixed(v.Subject, "Re:"),
                Body = "\n\nOn " + Format.LongDate(v.Date) + ", " + Address.NameOf(v.From ?? "") + " wrote:\n" + Quote(v.Text),
                InReplyTo = v.MessageId,
                References = ((v.References ?? "") + " " + (v.MessageId ?? "")).Trim(),
                ReplyFolder = folder,
                ReplyUid = uid
            };
            if (!all) return d;
            var already = new HashSet<string>(Address.Split(d.To).Select(a => Address.EmailOf(a).ToLowerInvariant()));
            var cc = new List<string>();
            foreach (var a in Address.Split(v.To).Concat(Address.Split(v.Cc)))
            {
                var em = Address.EmailOf(a).ToLowerInvariant();
                if (string.Equals(em, myEmail ?? "", StringComparison.OrdinalIgnoreCase) || already.Contains(em)) continue;
                already.Add(em);
                cc.Add(a);
            }
            d.Cc = string.Join(", ", cc.ToArray());
            return d;
        }

        /// <summary>The text of a message to forward (attachments don't go along yet).</summary>
        public static Draft Forward(MessageView v)
        {
            return new Draft
            {
                Subject = Prefixed(v.Subject, "Fwd:"),
                Body = "\n\n---------- Forwarded message ----------\nFrom: " + v.From + "\nDate: " + Format.LongDate(v.Date) +
                       "\nSubject: " + v.Subject + "\nTo: " + v.To + (string.IsNullOrEmpty(v.Cc) ? "" : "\nCc: " + v.Cc) +
                       "\n\n" + (v.Text ?? "")
            };
        }

        /// <summary>A new message to the address of a mailto: link.</summary>
        public static Draft FromMailto(string uri)
        {
            var to = Uri.UnescapeDataString(uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? uri.Substring(7) : uri);
            var q = to.IndexOf('?');
            return new Draft { To = q >= 0 ? to.Substring(0, q) : to };
        }

        /// <summary>"to you and Daniel Okafor", "to you, Ann and 3 others": who a message went to,
        /// for the reading page's header.</summary>
        public static string Recipients(MessageView v, string myEmail)
        {
            var names = new List<string>();
            bool me = false;
            foreach (var a in Address.Split(v.To).Concat(Address.Split(v.Cc)))
            {
                if (string.Equals(Address.EmailOf(a), myEmail ?? "", StringComparison.OrdinalIgnoreCase)) { me = true; continue; }
                names.Add(Address.NameOf(a));
            }
            if (me) names.Insert(0, "you");
            if (names.Count == 0) return "";
            if (names.Count == 1) return "to " + names[0];
            if (names.Count == 2) return "to " + names[0] + " and " + names[1];
            if (names.Count == 3) return "to " + names[0] + ", " + names[1] + " and " + names[2];
            return "to " + names[0] + ", " + names[1] + " and " + (names.Count - 2) + " others";
        }
    }
}
