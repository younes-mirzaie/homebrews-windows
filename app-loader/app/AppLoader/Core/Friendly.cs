// Everything the user reads when something goes wrong, in plain words: what happened, whether
// anything changed, and what to do next. Technical details (installer output, error codes, shell
// replies) go to AppLog, shown in the log pane, never as the main message.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AppLoader.Core
{
    public static class AppInfo
    {
        // The name people see. Same product ID whatever it's called, so updates keep applying.
        public const string Name = "Unesbrews";
    }

    /// <summary>Recent technical details for the log pane (newest first, kept in memory).</summary>
    public static class AppLog
    {
        static readonly List<string> _entries = new List<string>();
        public static event Action Changed;

        public static void Add(string what, string detail)
        {
            var e = DateTime.Now.ToString("HH:mm:ss") + "  " + what + (string.IsNullOrEmpty(detail) ? "" : "\n" + detail.Trim());
            _entries.Insert(0, e);
            if (_entries.Count > 30) _entries.RemoveAt(_entries.Count - 1);
            var c = Changed;
            if (c != null) c();
        }

        public static string Text
        {
            get { return _entries.Count == 0 ? "Details about installs and downloads show up here." : string.Join("\n\n", _entries); }
        }
    }

    public static class Friendly
    {
        public const string Unchanged = "Nothing on the phone was changed.";

        public const string NoShell =
            "The phone's system shell didn't answer. Try again in a moment. If it never answers, " + AppInfo.Name +
            " needs a phone unlocked with CMD Injector, and restarting the phone brings the shell back.";

        public const string Slow = "The phone took too long to answer. Try again in a moment.";

        public const string Busy = "Another install or download is still running. Wait for it to finish, then try again.";

        /// <summary>A download (rfxfetch) that failed: its output in, plain words out.</summary>
        public static string Download(string what, string fetchOutput)
        {
            string err = "";
            foreach (var line in (fetchOutput ?? "").Split('\n'))
                if (line.StartsWith("ERROR ", StringComparison.Ordinal)) err = line.Substring(6).Trim();
            AppLog.Add("download failed: " + what, fetchOutput);
            string e = err.ToLowerInvariant();
            string why;
            if (e.Contains("not found") || e.Contains("unreachable") || e.Contains("timed out") || e.Contains("refused") ||
                e.Contains("reset") || e.Contains("closed") || e.Contains("connecting") || e.Contains("looking up"))
                why = "The phone couldn't reach the server. Check that Wi-Fi or mobile data is on, then try again.";
            else if (e.Contains("certificate") || e.Contains("secure connection"))
                why = "The server's security check failed. Make sure the phone's date and time are right, then try again.";
            else if (e.Contains("http 404") || e.Contains("http 410"))
                why = "The file isn't on the server any more. Refresh the catalog and try again.";
            else if (e.Contains("http "))
                why = "The server didn't hand over the file right now. Try again later.";
            else if (e.Contains("mismatch") || e.Contains("larger than the limit"))
                why = "What arrived wasn't the exact file the catalog promised, so it was thrown away. Try again; " +
                      "if it keeps happening, the catalog may be in the middle of an update.";
            else if (e.Contains("can't write") || e.Contains("can't create") || e.Contains("can't rename"))
                why = "The file couldn't be saved. The phone may be out of storage space.";
            else
                why = "The download didn't complete. Try again.";
            return "Couldn't download " + what + ". " + why;
        }

        /// <summary>An install TH.exe refused, in plain words.</summary>
        public static string InstallFailed(ThResult th, InstallJob job)
        {
            string what = job.Title + " " + job.Version;
            string code = th.PacManCode ?? HResult(th.Output);
            string why = Reason(code, job);
            return (job.IsUpdate ? "Couldn't update " : "Couldn't install ") + what + ". " + why;   // the code itself is in the log
        }

        static string Reason(string code, InstallJob job)
        {
            switch (code)
            {
                case "0x8103012e":
                    return "This app can't live on the SD card, and the phone is set to put new apps there. " +
                           "Go to Settings > storage sense, set \"store new apps on\" to phone, then try again. " + Unchanged;
                case "0x81030137":
                    return job.IsUpdate
                        ? "The phone didn't accept it as an update of " + (job.ExistingTitle ?? "the installed app") +
                          ". It may be the same version you already have, or older. " + Unchanged
                        : "The phone says this package doesn't fit this phone. " + Unchanged;
                case "0x800b0100":
                    return "The phone only accepts signed packages for this kind of install. " + Unchanged;
                case "0x80070005":
                    return "The phone refused access. Unlock the screen, keep " + AppInfo.Name + " open, and try again. " + Unchanged;
                case "0x80070070":
                    return "The phone is out of storage space. Free some space (Settings > storage sense) and try again.";
                case "0x80073d02":
                    return "The app is running. Close it (hold Back and swipe it away), then try again. " + Unchanged;
                case "0x80073d06":
                    return "A newer version is already installed. " + Unchanged;
                case "0x80073cf3":
                    return "The app needs something the phone doesn't have (usually a framework package such as " +
                           "Microsoft Visual C++ libraries), or the update doesn't match the installed app. " + Unchanged;
                case "0x80073cf0":
                    return "The package file is damaged or isn't a phone app. " + Unchanged;
                case "0x80073cfb":
                    return "This exact version is already installed, but with different contents. " + Unchanged;
                case "0x80073cf9":
                    if (job.IsUpdate)
                    {
                        string app = job.ExistingTitle ?? job.Title;
                        return "Close " + app + " if it's open. Then check Settings > storage sense: \"store new apps on\" " +
                               "has to match where " + app + " is installed now (" + (job.ExistingOnSd ? "SD card" : "phone") +
                               "). Then try again. " + Unchanged;
                    }
                    return "Close the app if it's open, check that the phone has free storage, then try again. " + Unchanged;
                case "0x80073cf6":
                    return "The phone's installer gave up partway. Try again in a moment.";
            }
            return code != null
                ? "The phone's installer refused it. Try again in a moment. " + Unchanged
                : "The phone's installer stopped without saying why. Try again in a moment.";
        }

        /// <summary>An exception as the user should see it: our own messages as they are, anything
        /// else (a bug) as a plain apology, with the details in the log.</summary>
        public static string Of(Exception e)
        {
            if (e is System.IO.IOException || e is ArgumentException || e is AppLoader.Catalog.CatalogException)
                return e.Message;
            AppLog.Add("unexpected error: " + e.GetType().Name, e.ToString());
            return "Something went wrong inside " + AppInfo.Name + ". Try again; if it keeps happening, restart the app. " +
                   "Details are in the log pane.";
        }

        static string HResult(string output)
        {
            var m = Regex.Match(output ?? "", @"\b0x8[0-9a-fA-F]{7}\b");
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }
    }
}
