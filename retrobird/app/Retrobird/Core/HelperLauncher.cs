// Starts rbmaild as SYSTEM through CMD Injector's shell (telnetd on 127.0.0.1:9999, or 23), the
// same way File Explorer starts its helper. The helper and its NSS/NSPR DLLs ship in the package's
// Helper folder and are copied to C:\Data\SharedData\Retrobird, where the mail cache also lives.
using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Retrobird.Core
{
    public static class HelperLauncher
    {
        /// <summary>Must match RB_VERSION in helper/rb.h: a different running version is replaced.</summary>
        public const string BundledVersion = "1";
        public const string HelperDir = @"C:\Data\SharedData\Retrobird";
        static readonly int[] ShellPorts = { 9999, 23 };
        static readonly string[] Files = { "rbmaild.exe", "nssckbi.dll", "nspr4.dll", "plc4.dll", "plds4.dll" };

        /// <summary>The key the helper only answers to; also seals the stored passwords.</summary>
        public static string Token
        {
            get
            {
                var settings = IsolatedStorageSettings.ApplicationSettings;
                string t;
                if (!settings.TryGetValue("token", out t) || string.IsNullOrEmpty(t))
                {
                    var bytes = new byte[16];
                    new RNGCryptoServiceProvider().GetBytes(bytes);
                    var sb = new StringBuilder();
                    foreach (var b in bytes) sb.Append(b.ToString("x2"));
                    t = sb.ToString();
                    settings["token"] = t;
                    settings.Save();
                }
                return t;
            }
        }

        public static string LastShellOutput;
        const string Marker = "rbstart-finished";

        static string Clean(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\xFF' || c == '\uFFFD') { i += 2; continue; }
                if (c == '\x1B')
                {
                    i++;
                    while (i < s.Length && !char.IsLetter(s[i])) i++;
                    continue;
                }
                if (c == '\r') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string Tail(string s, int n) { return s.Length <= n ? s : "..." + s.Substring(s.Length - n); }

        /// <summary>What's listening: null (nothing), "" (our helper, current), "old" (ours, other
        /// version), "foreign" (something with another key).</summary>
        static async Task<string> Probe(int port)
        {
            var s = await LineSocket.ConnectAsync(MailClient.Host, port, 1500);
            if (s == null) return null;
            using (s)
            {
                await s.SendAsync("AUTH\t" + Token + "\n");
                var r = await s.ReadLineAsync();
                if (r == null || !r.StartsWith("OK\t")) return "foreign";
                var f = r.Split('\t');
                if (f.Length > 2 && f[2] == BundledVersion) return "";
                await s.SendAsync("QUIT\n");
                await s.ReadLineAsync();
                return "old";
            }
        }

        /// <summary>Makes sure a current rbmaild with our key is listening. Returns null when it
        /// is, otherwise a message for the user.</summary>
        public static async Task<string> EnsureRunningAsync(int port, Action<string> status)
        {
            var state = await Probe(port);
            if (state == "") return null;
            bool kill = state == "foreign";
            if (state == "old")
            {
                status("updating the mail helper...");
                await Task.Delay(800);
            }
            else status("starting the mail helper...");

            // The shell echoes typed input slowly and closing the session too early kills what it
            // started, so the commands go into a batch file in our own folder (readable by the
            // SYSTEM shell) and only its path is typed. Its output lands in rbstart.log.
            string pkg = Package.Current.InstalledLocation.Path + @"\Helper";
            string local = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            string args = " --token " + Token + " --port " + port + " --data \"" + HelperDir + "\\data\"";
            var bat = new StringBuilder();
            bat.Append("@echo off\r\n(\r\n");
            if (kill) bat.Append("kill -f rbmaild.exe\r\n");
            bat.Append("mkdir \"" + HelperDir + "\" 2>nul\r\n");
            foreach (var f in Files)
                bat.Append("copy /y \"" + pkg + "\\" + f + "\" \"" + HelperDir + "\\" + f + "\"\r\n");
            // if the copy failed, run it straight from the package (data still goes to SharedData).
            // rbmaild detaches itself from the shell's console and job, then this call returns.
            bat.Append("if exist \"" + HelperDir + "\\rbmaild.exe\" (\"" + HelperDir + "\\rbmaild.exe\"" + args +
                       ") else (\"" + pkg + "\\rbmaild.exe\"" + args + ")\r\n");
            bat.Append(") > \"%~dp0rbstart.log\" 2>&1\r\necho " + Marker + "\r\n");
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                using (var w = new StreamWriter(iso.CreateFile("rbstart.cmd"), new UTF8Encoding(false))) w.Write(bat.ToString());
                if (iso.FileExists("rbstart.log")) iso.DeleteFile("rbstart.log");
            }

            string shellOut = null;
            int shellPort = 0;
            foreach (int sp in ShellPorts)
            {
                var sh = await LineSocket.ConnectAsync("127.0.0.1", sp, 1500);
                if (sh == null) continue;
                using (sh)
                {
                    var log = new StringBuilder();
                    log.Append(await sh.ReadAvailableAsync(800));   // banner and telnet negotiation
                    await sh.SendAsync("\"" + local + "\\rbstart.cmd\"\r\n");
                    // wait for the batch to finish (the marker only appears in its output, not in
                    // the typed line), up to 30 s, before ending the session
                    var until = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < until && log.ToString().IndexOf(Marker, StringComparison.Ordinal) < 0 && !sh.IsClosed)
                        log.Append(await sh.ReadAvailableAsync(1000));
                    await sh.SendAsync("exit\r\n");
                    log.Append(await sh.ReadAvailableAsync(300));
                    shellOut = log.ToString();
                }
                shellPort = sp;
                break;
            }
            if (shellOut == null)
                return "Can't reach CMD Injector's shell on 127.0.0.1:9999 or :23. Retrobird needs an interop-unlocked phone with CMD Injector installed (and rebooted since).";

            for (int i = 0; i < 24; i++)
            {
                await Task.Delay(250);
                var s = await Probe(port);
                if (s == "") return null;
                if (s == "foreign") return "Another program with a different key holds port " + port + ". Restart the phone and try again.";
            }
            LastShellOutput = shellOut;
            string startLog = null;
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    if (iso.FileExists("rbstart.log"))
                        using (var r = new StreamReader(iso.OpenFile("rbstart.log", FileMode.Open, FileAccess.Read))) startLog = r.ReadToEnd();
            }
            catch { }
            bool ran = shellOut.IndexOf(Marker, StringComparison.Ordinal) >= 0;
            return "Asked CMD Injector's shell (port " + shellPort + ") to start the mail helper, but it didn't answer on 127.0.0.1:" + port + ". " +
                   (startLog != null ? "Start log:\n" + Tail(startLog.Trim(), 600)
                    : ran ? "The start script ran but wrote no log."
                    : "The start script didn't run. Shell said:\n" + Tail(Clean(shellOut), 400));
        }
    }
}
