// The install step on its own: runs the phone's installer (TH.exe) on a package that is already
// at a plain local path, and returns what TH said. No decisions here (which mode, whether it is
// safe): those are in Phone. It never uninstalls.
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AppLoader.Core
{
    public sealed class ThResult
    {
        public string Mode;         // "uda" (update a deployed app) or "di" (deploy)
        public string Output = "";  // TH.exe's output
        public int ExitCode = -1;
        public string ShellProblem; // the shell couldn't run it at all
        public bool Succeeded { get { return Output.Contains("Completed Successfully") && !Output.Contains("ERROR:"); } }
        public string PacManCode    // e.g. "0x8103012e", or null
        {
            get
            {
                var m = Regex.Match(Output, @"PacMan Error Code\s*=\s*(0x[0-9a-fA-F]{8})");
                return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
            }
        }
    }

    public static class PackageInstaller
    {
        public const string Update = "uda", Deploy = "di";

        public static async Task<ThResult> RunAsync(string packagePath, string mode)
        {
            if (mode != Update && mode != Deploy)
                throw new ArgumentException("mode must be uda or di");
            var result = new ThResult { Mode = mode };
            var shell = await Shell.RunAsync("TH.exe " + Shell.Quote(packagePath) + " -" + mode, 600);
            result.Output = shell.Output;
            result.ExitCode = shell.ExitCode;
            result.ShellProblem = shell.Problem;
            return result;
        }
    }
}
