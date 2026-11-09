// Runs commands as SYSTEM through the phone's system shell: the telnet service CMD Injector set up
// (127.0.0.1:9999, or 23). It is a system service, so it keeps working when the CMD Injector app
// itself is gone. The same trick File Explorer and Retrobird use to start their helpers.
using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;
using System.Threading.Tasks;

namespace AppLoader.Core
{
    public sealed class ShellResult
    {
        public bool Reached;        // the shell answered and ran the batch
        public string Output = "";  // what the commands printed (stdout and stderr)
        public int ExitCode = -1;   // %errorlevel% after the last command
        public string Problem;      // why it couldn't run, for the user
    }

    public static class Shell
    {
        static readonly int[] Ports = { 9999, 23 };
        const string Marker = "apploader-batch-finished";

        // The app's own folder: the SYSTEM shell can write and read it, and so can the app
        public static string LocalPath
        {
            get { return Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
        }

        static readonly System.Threading.SemaphoreSlim _one = new System.Threading.SemaphoreSlim(1);

        /// <summary>Runs the commands (batch file lines) as SYSTEM and returns what they printed.
        /// The shell echoes typed input slowly and drops what was typed if the session closes early,
        /// so the commands go into a batch file in the app's folder and only its path is typed; the
        /// output goes to a log file next to it.</summary>
        public static async Task<ShellResult> RunAsync(string commands, int timeoutSeconds)
        {
            await _one.WaitAsync();
            try { return await RunLockedAsync(commands, timeoutSeconds); }
            finally { _one.Release(); }
        }

        static async Task<ShellResult> RunLockedAsync(string commands, int timeoutSeconds)
        {
            var result = new ShellResult();
            var bat = new StringBuilder();
            bat.Append("@echo off\r\n");
            bat.Append("call :main > \"%~dp0run.log\" 2>&1\r\n");
            // redirection first: "echo exit=1>> f" would read "1>>" as a redirection of stream 1
            bat.Append(">> \"%~dp0run.log\" echo exit=%errorlevel%\r\n");
            bat.Append("echo " + Marker + "\r\n");
            bat.Append("goto :eof\r\n:main\r\n");
            bat.Append(commands.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            bat.Append("\r\ngoto :eof\r\n");
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                using (var w = new StreamWriter(iso.CreateFile("run.cmd"), new UTF8Encoding(false)))
                    w.Write(bat.ToString());
                if (iso.FileExists("run.log")) iso.DeleteFile("run.log");
            }

            string typed = null;
            foreach (int port in Ports)
            {
                var sh = await LineSocket.ConnectAsync("127.0.0.1", port, 1500);
                if (sh == null) continue;
                using (sh)
                {
                    var log = new StringBuilder();
                    log.Append(await sh.ReadAvailableAsync(800));   // banner, telnet negotiation
                    await sh.SendAsync("\"" + LocalPath + "\\run.cmd\"\r\n");
                    var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                    while (DateTime.UtcNow < until && !sh.IsClosed &&
                           log.ToString().IndexOf(Marker, StringComparison.Ordinal) < 0)
                        log.Append(await sh.ReadAvailableAsync(1000));
                    try { await sh.SendAsync("exit\r\n"); } catch (RfxException) { }
                    typed = log.ToString();
                }
                break;
            }
            if (typed == null)
            {
                result.Problem = "Can't reach the phone's system shell (telnet on 127.0.0.1:9999 or :23). " +
                                 "It is set up by CMD Injector and starts with the phone; try restarting the phone.";
                return result;
            }
            if (typed.IndexOf(Marker, StringComparison.Ordinal) < 0)
                result.Problem = "The command didn't finish within " + timeoutSeconds + " s.";
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    if (iso.FileExists("run.log"))
                        using (var r = new StreamReader(iso.OpenFile("run.log", FileMode.Open, FileAccess.Read)))
                            result.Output = r.ReadToEnd();
            }
            catch (Exception e)
            {
                result.Problem = (result.Problem ?? "") + " Can't read the output: " + e.Message;
            }
            // the last line is ours: exit=N
            int at = result.Output.LastIndexOf("exit=", StringComparison.Ordinal);
            if (at >= 0)
            {
                int code;
                var tail = result.Output.Substring(at + 5).Trim();
                if (int.TryParse(tail, out code)) result.ExitCode = code;
                result.Output = result.Output.Substring(0, at).TrimEnd();
                result.Reached = true;
            }
            return result;
        }

        /// <summary>Quotes a path for a batch line. Refuses characters a batch file would treat
        /// specially, rather than escaping them.</summary>
        public static string Quote(string path)
        {
            if (path.IndexOfAny(new[] { '"', '%', '^', '&', '|', '<', '>', '\r', '\n' }) >= 0)
                throw new ArgumentException("This path has characters the installer can't pass on safely: " + path);
            return "\"" + path + "\"";
        }
    }
}
