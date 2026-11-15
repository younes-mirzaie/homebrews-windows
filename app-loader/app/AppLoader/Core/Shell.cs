// Runs commands as SYSTEM through the phone's system shell: the telnet service CMD Injector set up
// (127.0.0.1:9999, or 23). It is a system service, so it keeps working when the CMD Injector app
// itself is gone. The same trick File Explorer and Retrobird use to start their helpers.
//
// Two ways:
//  - RunAsync: short commands (listing files, reading the app list, copying). The app waits on the
//    telnet session for them.
//  - StartDetachedAsync: an install. The batch is started by Helper\aldetach.exe outside the
//    telnet session's console and job, so it carries on if the app is suspended (screen lock,
//    leaving the app) or replaced (App Loader updating itself). It reports only through files:
//    run-N.log, and run-N.done when it has finished. The app polls for those and can pick a
//    running install up again after a restart.
// Every run has its own file names, so a run that outlives its timeout can't be overwritten.
using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace AppLoader.Core
{
    public sealed class ShellResult
    {
        public bool Reached;        // the batch ran to its end
        public string Output = "";  // what the commands printed (stdout and stderr)
        public int ExitCode = -1;   // %errorlevel% after the commands
        public string Problem;      // why it couldn't run or didn't finish, for the user
    }

    public static class Shell
    {
        static readonly int[] Ports = { 9999, 23 };
        const string Marker = "apploader-batch-finished";
        static readonly SemaphoreSlim _one = new SemaphoreSlim(1);

        // The app's own folder: the SYSTEM shell can write and read it, and so can the app
        public static string LocalPath
        {
            get { return Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
        }

        static int NextRun()
        {
            var s = IsolatedStorageSettings.ApplicationSettings;
            object v;
            int n = s.TryGetValue("run", out v) && v is int ? (int)v + 1 : 1;
            s["run"] = n;
            s.Save();
            return n;
        }

        // The helper programs (aldetach, rfxfetch and its DLLs) run from a copy here, not from the
        // app's package: when the app is installed on the SD card (D:\WPSystem\Apps\...), the
        // phone refuses to start programs from there ("The system cannot execute the specified
        // program"). File Explorer's helper does the same (C:\Data\SharedData\RetroFiles).
        public const string HelperDir = @"C:\Data\SharedData\AppLoader";

        public static string Helper(string exe) { return HelperDir + "\\" + exe; }

        /// <summary>Batch lines that copy the package's helpers to HelperDir, once per app version.</summary>
        static string StageLines()
        {
            var v = Package.Current.Id.Version;
            string stamp = HelperDir + "\\version-" + v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision + ".txt";
            string src = Package.Current.InstalledLocation.Path + "\\Helper";
            return "if not exist " + Quote(HelperDir) + " mkdir " + Quote(HelperDir) + "\r\n" +
                   "if not exist " + Quote(stamp) + " copy /y " + Quote(src + "\\*.*") + " " + Quote(HelperDir + "\\") +
                   " >nul && > " + Quote(stamp) + " echo staged\r\n";
        }

        static string Batch(string name, string commands)
        {
            var bat = new StringBuilder();
            bat.Append("@echo off\r\n");
            bat.Append("call :main > \"%~dp0" + name + ".log\" 2>&1\r\n");
            // redirection first: "echo exit=1>> f" would read "1>>" as a redirection of stream 1
            bat.Append(">> \"%~dp0" + name + ".log\" echo exit=%errorlevel%\r\n");
            bat.Append("> \"%~dp0" + name + ".done\" echo done\r\n");
            bat.Append("echo " + Marker + "\r\n");
            bat.Append("goto :eof\r\n:main\r\n");
            bat.Append(StageLines());
            bat.Append(commands.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            bat.Append("\r\ngoto :eof\r\n");
            return bat.ToString();
        }

        static void WriteFile(string file, string text)
        {
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
            using (var w = new StreamWriter(iso.CreateFile(file), new UTF8Encoding(false)))
                w.Write(text);
        }

        /// <summary>Types one line into the shell and reads what it says until the marker shows
        /// up or the time is up. Null if the shell can't be reached.</summary>
        static async Task<string> TypeAsync(string line, string until, int timeoutSeconds)
        {
            // The shell can be slow to answer, especially right after an install: a few tries
            for (int attempt = 0; attempt < 3; attempt++)
            foreach (int port in Ports)
            {
                if (attempt > 0 && port == Ports[0]) await Task.Delay(2000);
                var sh = await LineSocket.ConnectAsync("127.0.0.1", port, 3500);
                if (sh == null) continue;
                using (sh)
                {
                    var log = new StringBuilder();
                    log.Append(await sh.ReadAvailableAsync(800));   // banner, telnet negotiation
                    await sh.SendAsync(line + "\r\n");
                    var end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                    while (DateTime.UtcNow < end && !sh.IsClosed &&
                           log.ToString().IndexOf(until, StringComparison.Ordinal) < 0)
                        log.Append(await sh.ReadAvailableAsync(1000));
                    try { await sh.SendAsync("exit\r\n"); } catch (RfxException) { }
                    return log.ToString();
                }
            }
            return null;
        }

        const string NoShell = Friendly.NoShell;

        /// <summary>Runs short commands (batch file lines) as SYSTEM and waits for what they print.
        /// The shell echoes typed input slowly and drops what was typed if the session closes
        /// early, so the commands go into a batch file and only its path is typed.</summary>
        public static async Task<ShellResult> RunAsync(string commands, int timeoutSeconds)
        {
            await _one.WaitAsync();
            try
            {
                string name = "run-" + NextRun();
                WriteFile(name + ".cmd", Batch(name, commands));
                var typed = await TypeAsync(Quote(LocalPath + "\\" + name + ".cmd"), Marker, timeoutSeconds);
                if (typed == null) return new ShellResult { Problem = NoShell };
                var result = Read(name);
                if (!result.Reached) { result.Problem = Friendly.Slow; AppLog.Add("shell command timed out after " + timeoutSeconds + " s", commands); }
                else Delete(name);
                return result;
            }
            finally { _one.Release(); }
        }

        /// <summary>Starts commands as SYSTEM, detached from the shell, and returns the run's name
        /// (for Read/IsDone), or throws with a message for the user.</summary>
        public static async Task<string> StartDetachedAsync(string commands)
        {
            await _one.WaitAsync();
            try
            {
                string name = "run-" + NextRun();
                WriteFile(name + ".cmd", Batch(name, commands));
                // A small batch puts the helpers in place and starts aldetach from there; only its
                // path is typed
                string launch = "launch-" + name;
                WriteFile(launch + ".cmd", "@echo off\r\n" + StageLines() +
                          Quote(Helper("aldetach.exe")) + " " + Quote(LocalPath + "\\" + name + ".cmd") + "\r\n" +
                          "echo aldetach: finished\r\n");
                string line = Quote(LocalPath + "\\" + launch + ".cmd");
                var typed = await TypeAsync(line, "aldetach: ", 60);
                Delete(launch);
                if (typed == null) throw new IOException(NoShell);
                if (typed.IndexOf("aldetach: started", StringComparison.Ordinal) < 0)
                {
                    // the reply can get lost while the task did start: its log shows up then
                    for (int i = 0; i < 10 && !Exists(name + ".log"); i++) await Task.Delay(500);
                    if (!Exists(name + ".log"))
                    {
                        AppLog.Add("couldn't start the background task", ShellReply(typed, line));
                        throw new IOException("The phone's system shell didn't start the task. Try again in a moment.");
                    }
                }
                return name;
            }
            finally { _one.Release(); }
        }

        public static bool IsDone(string name) { return Exists(name + ".done"); }

        static bool Exists(string file)
        {
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                return iso.FileExists(file);
        }

        /// <summary>What a run printed so far, and its exit code once it has finished.</summary>
        public static ShellResult Read(string name)
        {
            var result = new ShellResult();
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (iso.FileExists(name + ".log"))
                        using (var r = new StreamReader(iso.OpenFile(name + ".log", FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                            result.Output = r.ReadToEnd();
                    result.Reached = iso.FileExists(name + ".done");
                }
            }
            catch (Exception e)
            {
                AppLog.Add("couldn't read a task's output", e.Message);
                result.Problem = "Couldn't read what the phone answered. Try again.";
                return result;
            }
            int at = result.Output.LastIndexOf("exit=", StringComparison.Ordinal);
            if (result.Reached && at >= 0)
            {
                int code;
                if (int.TryParse(result.Output.Substring(at + 5).Trim(), out code)) result.ExitCode = code;
                result.Output = result.Output.Substring(0, at).TrimEnd();
            }
            return result;
        }

        const string LogFolder = "install-logs";
        const int KeepLogs = 5;

        /// <summary>Keeps a finished run's output and its result in install-logs\, the newest five,
        /// so a refused install can still be read after the run's own files are gone.</summary>
        public static void Archive(string name, string result)
        {
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    string output = "";
                    if (iso.FileExists(name + ".log"))
                        using (var r = new StreamReader(iso.OpenFile(name + ".log", FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                            output = r.ReadToEnd();
                    if (!iso.DirectoryExists(LogFolder)) iso.CreateDirectory(LogFolder);
                    string file = LogFolder + "\\" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + name + ".log";
                    using (var w = new StreamWriter(iso.CreateFile(file)))
                    {
                        w.WriteLine("Result:");
                        w.WriteLine(result ?? "");
                        w.WriteLine();
                        w.WriteLine("Output:");
                        w.Write(output);
                    }
                    var old = iso.GetFileNames(LogFolder + "\\*.log");
                    Array.Sort(old, StringComparer.Ordinal);
                    for (int i = 0; i < old.Length - KeepLogs; i++) iso.DeleteFile(LogFolder + "\\" + old[i]);
                }
            }
            catch (Exception) { }
        }

        public static void Delete(string name)
        {
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    foreach (var ext in new[] { ".cmd", ".log", ".done" })
                        if (iso.FileExists(name + ext)) iso.DeleteFile(name + ext);
            }
            catch (Exception) { }
        }

        /// <summary>Quotes a path for a batch line. Refuses characters a batch file would treat
        /// specially, rather than escaping them.</summary>
        public static string Quote(string path)
        {
            if (path.IndexOfAny(new[] { '"', '%', '^', '&', '|', '<', '>', '\r', '\n' }) >= 0)
                throw new ArgumentException("The file name or folder \"" + path + "\" has characters (like & or %) the installer can't handle. Rename the file or move it to Downloads, then try again.");
            return "\"" + path + "\"";
        }

        /// <summary>Drops telnet negotiation bytes and terminal escapes so the shell's reply is readable.</summary>
        static string Clean(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\xFF' || c == '�') { i += 2; continue; }
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

        /// <summary>What the shell answered to a typed line, without the echo: the shell redraws
        /// the line as each character arrives, so the reply starts after the last copy of the
        /// line's end.</summary>
        static string ShellReply(string typed, string line)
        {
            string text = Clean(typed);
            string end = line.Length > 24 ? line.Substring(line.Length - 24) : line;
            int at = text.LastIndexOf(end, StringComparison.Ordinal);
            if (at >= 0) text = text.Substring(at + end.Length);
            text = text.Trim();
            if (text.EndsWith(">")) text = text.Substring(0, text.LastIndexOf('\n') > 0 ? text.LastIndexOf('\n') : text.Length).Trim();
            return text.Length == 0 ? "(nothing)" : Tail(text, 400);
        }
    }
}
