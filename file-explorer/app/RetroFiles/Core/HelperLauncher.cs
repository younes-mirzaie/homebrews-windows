// Starts rfxfsd as SYSTEM through CMD Injector's shell (telnetd on 127.0.0.1:9999, or 23),
// the same way CMD Injector's own app runs privileged commands.
using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace RetroFiles.Core
{
    public static class HelperLauncher
    {
        public const string BundledVersion = "2";
        public const string HelperDir = @"C:\Data\SharedData\RetroFiles";
        static readonly int[] ShellPorts = { 9999, 23 };

        public static string Token
        {
            get
            {
                var settings = IsolatedStorageSettings.ApplicationSettings;
                string t;
                if (!settings.TryGetValue("token", out t) || string.IsNullOrEmpty(t))
                {
                    var bytes = new byte[16];
                    new RNGCryptoServiceProvider().GetBytes(bytes);   // not IDisposable on WP Silverlight
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
        const string Marker = "rfxstart-finished";

        /// <summary>Drops telnet negotiation bytes and terminal escapes so the shell's reply is readable.</summary>
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

        /// <summary>Makes sure a current rfxfsd with our token is listening. Returns null when it is,
        /// otherwise a message for the user.</summary>
        public static async Task<string> EnsureRunningAsync(RfxClient client, Action<string> status)
        {
            bool restart = false;
            try
            {
                if (await client.ConnectAsync())
                {
                    if (client.ServerVersion == BundledVersion) return null;
                    status("updating the helper...");
                    try { await client.QuitServerAsync(); } catch { }
                    client.Close();
                    await Task.Delay(500);
                }
            }
            catch (RfxException)
            {
                // a helper from an earlier install (different key) holds the port
                restart = true;
            }

            status("starting the helper...");
            string src = Package.Current.InstalledLocation.Path + @"\Helper\rfxfsd.exe";
            string dst = HelperDir + @"\rfxfsd.exe";
            // The shell echoes typed input slowly, and closing the session too early loses what was
            // typed, so the commands go into a batch file in our own folder (the SYSTEM shell can read
            // it) and only its path is typed. Its output lands in rfxstart.log next to it.
            string local = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            string args = " --token " + Token;
            var bat = new StringBuilder();
            bat.Append("@echo off\r\n(\r\n");
            if (restart) bat.Append("kill -f rfxfsd.exe\r\n");
            bat.Append("mkdir \"" + HelperDir + "\" 2>nul\r\n");
            bat.Append("copy /y \"" + src + "\" \"" + dst + "\"\r\n");
            // if the copy failed, run it straight from the app package. rfxfsd detaches itself from
            // the shell's console and job, then this call returns.
            bat.Append("if exist \"" + dst + "\" (\"" + dst + "\"" + args + ") else (\"" + src + "\"" + args + ")\r\n");
            bat.Append(") > \"%~dp0rfxstart.log\" 2>&1\r\necho " + Marker + "\r\n");
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                using (var w = new StreamWriter(iso.CreateFile("rfxstart.cmd"), new UTF8Encoding(false))) w.Write(bat.ToString());
                if (iso.FileExists("rfxstart.log")) iso.DeleteFile("rfxstart.log");
            }

            string shellOut = null;
            int shellPort = 0;
            foreach (int port in ShellPorts)
            {
                var sh = await LineSocket.ConnectAsync("127.0.0.1", port, 1500);
                if (sh == null) continue;
                using (sh)
                {
                    var log = new StringBuilder();
                    log.Append(await sh.ReadAvailableAsync(800));   // banner and telnet negotiation
                    await sh.SendAsync("\"" + local + "\\rfxstart.cmd\"\r\n");
                    // wait for the batch to finish (the marker only appears in its output, not in the
                    // typed line), up to 30 s, before ending the session
                    var until = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < until && log.ToString().IndexOf(Marker, StringComparison.Ordinal) < 0 && !sh.IsClosed)
                        log.Append(await sh.ReadAvailableAsync(1000));
                    await sh.SendAsync("exit\r\n");
                    log.Append(await sh.ReadAvailableAsync(300));
                    shellOut = log.ToString();
                }
                shellPort = port;
                break;
            }
            if (shellOut == null)
                return "Can't reach CMD Injector's shell on 127.0.0.1:9999 or :23. Is CMD Injector installed and the phone rebooted since?";

            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(250);
                try { if (await client.ConnectAsync()) return null; }
                catch (RfxException e) { return "The helper is running with a different key (" + e.Message + "). Reboot the phone and try again."; }
            }
            LastShellOutput = shellOut;
            string startLog = null;
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    if (iso.FileExists("rfxstart.log"))
                        using (var r = new StreamReader(iso.OpenFile("rfxstart.log", FileMode.Open, FileAccess.Read))) startLog = r.ReadToEnd();
            }
            catch { }
            bool ran = shellOut.IndexOf(Marker, StringComparison.Ordinal) >= 0;
            return "Asked CMD Injector's shell (port " + shellPort + ") to start the helper, but it didn't answer on 127.0.0.1:" + RfxClient.Port + ". " +
                   (startLog != null ? "Start log:\n" + Tail(startLog.Trim(), 600)
                    : ran ? "The start script ran but wrote no log."
                    : "The start script didn't run. Shell said:\n" + Tail(Clean(shellOut), 400));
        }
    }
}
