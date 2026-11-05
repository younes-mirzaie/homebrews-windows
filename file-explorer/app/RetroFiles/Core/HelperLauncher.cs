// Starts rfxfsd as SYSTEM through CMD Injector's shell (telnetd on 127.0.0.1:9999, or 23),
// the same way CMD Injector's own app runs privileged commands.
using System;
using System.IO.IsolatedStorage;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace RetroFiles.Core
{
    public static class HelperLauncher
    {
        public const string BundledVersion = "1";
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
            var cmds = new StringBuilder();
            if (restart) cmds.Append("kill -f rfxfsd.exe\r\n");
            cmds.Append("mkdir \"" + HelperDir + "\" 2>nul\r\n");
            cmds.Append("copy /y \"" + src + "\" \"" + dst + "\"\r\n");
            // if the copy failed, run it straight from the app package
            cmds.Append("if exist \"" + dst + "\" (start \"\" \"" + dst + "\" --token " + Token + ") else (start \"\" \"" + src + "\" --token " + Token + ")\r\n");
            cmds.Append("exit\r\n");

            string shellOut = null;
            int shellPort = 0;
            foreach (int port in ShellPorts)
            {
                var sh = await LineSocket.ConnectAsync("127.0.0.1", port, 1500);
                if (sh == null) continue;
                using (sh)
                {
                    await sh.ReadAvailableAsync(800);            // banner and telnet negotiation
                    await sh.SendAsync(cmds.ToString());
                    shellOut = await sh.ReadAvailableAsync(1500);
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
            return "Asked CMD Injector's shell (port " + shellPort + ") to start the helper, but it didn't answer on 127.0.0.1:" + RfxClient.Port +
                   ". Shell said:\n" + Tail(Clean(shellOut), 600);
        }
    }
}
