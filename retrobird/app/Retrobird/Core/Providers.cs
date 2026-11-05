// Server settings for common mail providers, picked from the e-mail address.
using System;
using System.Collections.Generic;

namespace Retrobird.Core
{
    public class Provider
    {
        public string Name;
        public string[] Domains;
        public string ImapHost, SmtpHost;
        public int ImapPort = 993, SmtpPort = 465;
        public string ImapSec = "tls", SmtpSec = "tls";
        public bool SaveSent = true;   // false where the server files sent mail itself (Gmail)
        public string Note;

        public static readonly List<Provider> All = new List<Provider>
        {
            new Provider { Name = "Gmail", Domains = new[] { "gmail.com", "googlemail.com" },
                ImapHost = "imap.gmail.com", SmtpHost = "smtp.gmail.com", SaveSent = false,
                Note = "Gmail needs an app password: Google Account > Security > 2-Step Verification > App passwords." },
            new Provider { Name = "Outlook.com", Domains = new[] { "outlook.com", "hotmail.com", "live.com", "msn.com", "outlook.de", "hotmail.de", "hotmail.co.uk", "live.de" },
                ImapHost = "outlook.office365.com", SmtpHost = "smtp-mail.outlook.com", SmtpPort = 587, SmtpSec = "starttls", SaveSent = false,
                Note = "Microsoft now only lets mail apps in with its own sign-in (OAuth2), which Retrobird doesn't have yet. A password will probably be refused." },
            new Provider { Name = "Yahoo", Domains = new[] { "yahoo.com", "yahoo.de", "yahoo.co.uk", "yahoo.fr", "ymail.com", "rocketmail.com" },
                ImapHost = "imap.mail.yahoo.com", SmtpHost = "smtp.mail.yahoo.com",
                Note = "Yahoo needs an app password: Account Security > Generate app password." },
            new Provider { Name = "iCloud", Domains = new[] { "icloud.com", "me.com", "mac.com" },
                ImapHost = "imap.mail.me.com", SmtpHost = "smtp.mail.me.com", SmtpPort = 587, SmtpSec = "starttls",
                Note = "iCloud needs an app-specific password from appleid.apple.com." },
            new Provider { Name = "GMX", Domains = new[] { "gmx.de", "gmx.net", "gmx.at", "gmx.ch", "gmx.com" },
                ImapHost = "imap.gmx.net", SmtpHost = "mail.gmx.net", SmtpPort = 587, SmtpSec = "starttls",
                Note = "Turn on IMAP access in the GMX web mail settings first." },
            new Provider { Name = "WEB.DE", Domains = new[] { "web.de" },
                ImapHost = "imap.web.de", SmtpHost = "smtp.web.de", SmtpPort = 587, SmtpSec = "starttls",
                Note = "Turn on IMAP access in the WEB.DE settings first." },
            new Provider { Name = "Fastmail", Domains = new[] { "fastmail.com", "fastmail.fm" },
                ImapHost = "imap.fastmail.com", SmtpHost = "smtp.fastmail.com",
                Note = "Fastmail needs an app password (Settings > Privacy & Security)." },
            new Provider { Name = "Yandex", Domains = new[] { "yandex.com", "yandex.ru", "ya.ru" },
                ImapHost = "imap.yandex.com", SmtpHost = "smtp.yandex.com",
                Note = "Yandex needs an app password for mail apps." },
            new Provider { Name = "Zoho", Domains = new[] { "zoho.com", "zohomail.com" },
                ImapHost = "imap.zoho.com", SmtpHost = "smtp.zoho.com" },
            new Provider { Name = "Proton Mail", Domains = new[] { "proton.me", "protonmail.com", "pm.me" },
                ImapHost = "127.0.0.1", SmtpHost = "127.0.0.1",
                Note = "Proton Mail only works through Proton Bridge on a PC, which Retrobird can't use." },
        };

        public static string Domain(string email)
        {
            var at = (email ?? "").LastIndexOf('@');
            return at < 0 ? "" : email.Substring(at + 1).Trim().ToLowerInvariant();
        }

        /// <summary>The known provider for this address, or a guess (imap./smtp. + domain).</summary>
        public static Provider ForEmail(string email)
        {
            var d = Domain(email);
            foreach (var p in All)
                foreach (var x in p.Domains)
                    if (d == x) return p;
            if (d.Length == 0) return null;
            return new Provider { Name = "", Domains = new[] { d }, ImapHost = "imap." + d, SmtpHost = "smtp." + d };
        }
    }
}
