// Server settings for common mail providers: offered as a list when adding an account, and
// picked from the email address. Providers based in sanctioned countries are left out on purpose.
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
        /// <summary>False while Retrobird can't sign in to it (shown, but explained).</summary>
        public bool Works = true;
        /// <summary>How to get an app password (or what to turn on first), in plain words.</summary>
        public string Note;
        /// <summary>Where the app password is made, opened from the account page.</summary>
        public string AppPasswordUrl;
        /// <summary>Signs in on Microsoft's page (OAuth) instead of with a password.</summary>
        public bool Microsoft;

        /// <summary>The app (client) ID registered with Microsoft for Retrobird's Outlook sign-in.
        /// Can be overridden in the account's server settings.</summary>
        public const string MicrosoftClientId = "";

        /// <summary>The name, for binding (bindings need a property).</summary>
        public string Title { get { return Name; } }
        /// <summary>Shown under the name in the list.</summary>
        public string Hint { get { return Works ? string.Join(", ", Domains) : "not supported yet"; } }
        public double ItemOpacity { get { return Works ? 1.0 : 0.5; } }

        public static readonly List<Provider> All = new List<Provider>
        {
            new Provider { Name = "Gmail", Domains = new[] { "gmail.com", "googlemail.com" },
                ImapHost = "imap.gmail.com", SmtpHost = "smtp.gmail.com", SaveSent = false,
                Note = "Gmail needs an app password. Turn on 2-Step Verification in your Google Account, then create an app password and use it here.",
                AppPasswordUrl = "https://myaccount.google.com/apppasswords" },
            new Provider { Name = "Outlook.com / Hotmail", Domains = new[] { "outlook.com", "hotmail.com", "live.com", "msn.com", "outlook.de", "hotmail.de", "hotmail.co.uk", "live.de" },
                ImapHost = "outlook.office365.com", SmtpHost = "smtp-mail.outlook.com", SmtpPort = 587, SmtpSec = "starttls", SaveSent = false,
                Microsoft = true,
                Note = "Outlook.com doesn't take passwords from mail apps. You sign in on Microsoft's own page instead: Retrobird shows a short code, you open the page in Retrofox, sign in and enter the code." },
            new Provider { Name = "Yahoo Mail", Domains = new[] { "yahoo.com", "yahoo.de", "yahoo.co.uk", "yahoo.fr", "ymail.com", "rocketmail.com" },
                ImapHost = "imap.mail.yahoo.com", SmtpHost = "smtp.mail.yahoo.com",
                Note = "Yahoo needs an app password: Account Security > Generate app password.",
                AppPasswordUrl = "https://login.yahoo.com/account/security/app-passwords" },
            new Provider { Name = "iCloud Mail", Domains = new[] { "icloud.com", "me.com", "mac.com" },
                ImapHost = "imap.mail.me.com", SmtpHost = "smtp.mail.me.com", SmtpPort = 587, SmtpSec = "starttls",
                Note = "iCloud needs an app-specific password: Apple Account > Sign-In and Security > App-Specific Passwords.",
                AppPasswordUrl = "https://account.apple.com" },
            new Provider { Name = "AOL Mail", Domains = new[] { "aol.com", "aim.com" },
                ImapHost = "imap.aol.com", SmtpHost = "smtp.aol.com",
                Note = "AOL needs an app password: Account Security > Generate app password.",
                AppPasswordUrl = "https://login.aol.com/account/security/app-passwords" },
            new Provider { Name = "GMX", Domains = new[] { "gmx.de", "gmx.net", "gmx.at", "gmx.ch", "gmx.com" },
                ImapHost = "imap.gmx.net", SmtpHost = "mail.gmx.net", SmtpPort = 587, SmtpSec = "starttls",
                Note = "Turn on \"POP3/IMAP access\" in the GMX web mail settings first. With two-factor sign-in on, use an app password." },
            new Provider { Name = "WEB.DE", Domains = new[] { "web.de" },
                ImapHost = "imap.web.de", SmtpHost = "smtp.web.de", SmtpPort = 587, SmtpSec = "starttls",
                Note = "Turn on \"POP3/IMAP access\" in the WEB.DE settings first. With two-factor sign-in on, use an app password." },
            new Provider { Name = "Fastmail", Domains = new[] { "fastmail.com", "fastmail.fm" },
                ImapHost = "imap.fastmail.com", SmtpHost = "smtp.fastmail.com",
                Note = "Fastmail needs an app password: Settings > Privacy & Security > App passwords.",
                AppPasswordUrl = "https://app.fastmail.com/settings/security/apps" },
            new Provider { Name = "Zoho Mail", Domains = new[] { "zoho.com", "zohomail.com" },
                ImapHost = "imap.zoho.com", SmtpHost = "smtp.zoho.com",
                Note = "Turn on IMAP access in Zoho Mail's settings first. With two-factor sign-in on, use an app password.",
                AppPasswordUrl = "https://accounts.zoho.com/home#security/app_password" },
            new Provider { Name = "Proton Mail", Domains = new[] { "proton.me", "protonmail.com", "pm.me" },
                ImapHost = "127.0.0.1", SmtpHost = "127.0.0.1", Works = false,
                Note = "Proton Mail only works with other mail apps through Proton Bridge on a computer, which Retrobird can't use." },
        };

        public static Provider ByName(string name)
        {
            foreach (var p in All) if (p.Name == name) return p;
            return null;
        }

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
