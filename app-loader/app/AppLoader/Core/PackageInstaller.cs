// The install step on its own: runs the phone's installer (TH.exe) on a package that is already
// at a plain local path, and returns what TH said. No decisions here (which mode, whether it is
// safe): those are in Phone. It never uninstalls.
//
// TH runs detached from the app (Shell.StartDetachedAsync), so an install keeps going if the app
// is suspended or replaced; Start returns the run's name and Result reads it, now or after the
// app comes back.
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AppLoader.Core
{
    public sealed class ThResult
    {
        public string Mode;         // "uda" (update a deployed app) or "di" (deploy)
        public bool Finished;       // TH has finished (the run's done marker is there)
        public string Output = "";  // TH.exe's output
        public int ExitCode = -1;
        public string Problem;      // couldn't start it or read it
        // TH prints to the console, not to the redirected output, so the log usually holds only
        // the exit code. Success is exit 0; Phone also checks the installed version afterwards.
        public bool Succeeded { get { return Finished && ExitCode == 0 && !Output.Contains("ERROR:"); } }
        public string PacManCode    // e.g. "0x8103012e", or null
        {
            get
            {
                var m = Regex.Match(Output, @"PacMan Error Code\s*=\s*(0x[0-9a-fA-F]{8})");
                if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
                if (Finished && ExitCode != 0 && ExitCode != -1) return "0x" + ((uint)ExitCode).ToString("x8");
                return null;
            }
        }
    }

    public static class PackageInstaller
    {
        public const string Update = "uda", Deploy = "di";

        /// <summary>Starts TH.exe on the package; returns the run's name. The batch deletes the
        /// package when TH is done (it is the app's staged copy).</summary>
        public static Task<string> StartAsync(string packagePath, string mode, bool deleteAfter)
        {
            if (mode != Update && mode != Deploy)
                throw new ArgumentException("mode must be uda or di");
            string cmds = "TH.exe " + Shell.Quote(packagePath) + " -" + mode + "\n" +
                          "set rc=%errorlevel%\n" +
                          (deleteAfter ? "del /f /q " + Shell.Quote(packagePath) + " >nul 2>&1\n" : "") +
                          "exit /b %rc%";
            return Shell.StartDetachedAsync(cmds);
        }

        public static ThResult Result(string run, string mode)
        {
            var r = Shell.Read(run);
            return new ThResult { Mode = mode, Finished = r.Reached, Output = r.Output, ExitCode = r.ExitCode, Problem = r.Problem };
        }

        /// <summary>Starts TH.exe and waits until it has finished (up to the time limit).</summary>
        public static async Task<ThResult> RunAsync(string packagePath, string mode, int timeoutSeconds = 900)
        {
            string run;
            try { run = await StartAsync(packagePath, mode, false); }
            catch (Exception e) { return new ThResult { Mode = mode, Problem = e.Message }; }
            return await WaitAsync(run, mode, timeoutSeconds);
        }

        public static async Task<ThResult> WaitAsync(string run, string mode, int timeoutSeconds)
        {
            var end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (!Shell.IsDone(run) && DateTime.UtcNow < end)
                await Task.Delay(1000);
            var result = Result(run, mode);
            if (!result.Finished && result.Problem == null)
                result.Problem = "The installer is still running after " + timeoutSeconds + " s.";
            return result;
        }
    }
}
