// An install job: one or more apps, each downloaded (catalog) and installed by TH.exe, all in ONE
// detached batch. The batch runs as SYSTEM outside the app, so it finishes even when the app is
// minimized, the phone is locked, or the app is updating itself. It reports through its run-N.log
// with "UB <item> <step>" lines, which both the app and the background agent read.
//
// Shared with the background agent (AppLoader.Agent): only isolated storage here, no shell.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;

namespace AppLoader.Core
{
    public sealed class InstallItem
    {
        public string ProductId;     // upper case, no braces
        public string Title, Version;
        public string Mode;          // "uda" (update) or "di" (deploy)
        public string File;          // the package: staged copy in the app's folder, or download in the job folder
        public string Url, Sha256;   // set for catalog items: downloaded first
        public long Size;
        public string ExistingTitle;
        public bool ExistingOnSd, KindChange;

        public bool IsUpdate { get { return Mode == "uda"; } }
        public bool Downloads { get { return !string.IsNullOrEmpty(Url); } }
        public string What { get { return Title + " " + Version; } }
    }

    public sealed class InstallJob
    {
        public string Run;
        public List<InstallItem> Items = new List<InstallItem>();

        const string FileName = "job.txt";

        public string Title
        {
            get { return Items.Count == 1 ? Items[0].What : Items.Count + " apps"; }
        }

        public bool Includes(string productId)
        {
            foreach (var i in Items) if (i.ProductId == productId) return true;
            return false;
        }

        /// <summary>The batch lines that download and install every item in turn.</summary>
        public string Commands(string localPath, string jobDir, Func<string, string> quote, string fetchExe)
        {
            var b = new StringBuilder();
            b.Append("if not exist " + quote(jobDir) + " mkdir " + quote(jobDir) + "\n");
            for (int n = 0; n < Items.Count; n++)
            {
                var it = Items[n];
                // downloads go to the job folder, outside the app, so a self-update can't take them away
                string pkg = quote((it.Downloads ? jobDir : localPath) + "\\" + it.File);
                b.Append("echo UB " + n + " begin\n");
                if (it.Downloads)
                {
                    b.Append("echo UB " + n + " download\n");
                    b.Append(quote(fetchExe) + " " + quote(it.Url) + " " + pkg + " --sha256 " + it.Sha256 +
                             " --size " + it.Size + " --max-bytes " + it.Size + "\n");
                    b.Append("if errorlevel 1 goto :fail" + n + "\n");
                }
                b.Append("echo UB " + n + " install\n");
#if WINDOWS_UWP
                // Windows 10 Mobile has no TH.exe: MinDeployAppX.exe (see Installer10.cs)
                b.Append(Installer10.Line(pkg, it.Mode) + "\n");
                b.Append("echo UB " + n + " th %errorlevel%\n");
#else
                b.Append("TH.exe " + pkg + " -" + it.Mode + "\n");
                b.Append("set UBR=%errorlevel%\n");
                b.Append("echo UB " + n + " th %UBR%\n");
                if (it.IsUpdate && it.File.EndsWith(".xap", StringComparison.OrdinalIgnoreCase))
                {
                    // An update that fails partway can leave the app removed: TH -uda once took
                    // Unesbrews off the phone and then stopped with 0x80070006 while updating it
                    // from its own job. If the app's folder is gone, deploy it again from the same
                    // package (the later "th" line is the one that counts).
                    string dir = "{" + it.ProductId + "}\\Install";
                    b.Append("if \"%UBR%\"==\"0\" goto :next" + n + "\n");
                    b.Append("if exist \"C:\\Data\\Programs\\" + dir + "\" goto :next" + n + "\n");
                    b.Append("if exist \"D:\\WPSystem\\Apps\\" + dir + "\" goto :next" + n + "\n");
                    b.Append("echo UB " + n + " redeploy\n");
                    b.Append("ping -n 4 127.0.0.1 >nul\n");
                    b.Append("TH.exe " + pkg + " -di\n");
                    b.Append("echo UB " + n + " th %errorlevel%\n");
                }
#endif
                b.Append("goto :next" + n + "\n");
                b.Append(":fail" + n + "\n");
                b.Append("echo UB " + n + " fetchfail\n");
                b.Append(":next" + n + "\n");
                b.Append("del /f /q " + pkg + " >nul 2>&1\n");
            }
#if WINDOWS_UWP
            // keep this app allowed to reach the shell after it updates itself
            b.Append(Installer10.LoopbackLine() + "\n");
#endif
            b.Append("exit /b 0");
            return b.ToString();
        }

        public void Save()
        {
            var b = new StringBuilder();
            b.Append(Run).Append('\n');
            foreach (var i in Items)
                b.Append(string.Join("\t", new[] {
                    i.ProductId, i.Title, i.Version, i.Mode, i.File, i.Url ?? "", i.Sha256 ?? "", i.Size.ToString(),
                    i.ExistingTitle ?? "", i.ExistingOnSd ? "sd" : "", i.KindChange ? "kind" : "" })).Append('\n');
            using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
            using (var w = new StreamWriter(iso.CreateFile(FileName), new UTF8Encoding(false)))
                w.Write(b.ToString());
        }

        public static InstallJob Load()
        {
            try
            {
                string text;
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!iso.FileExists(FileName)) return null;
                    using (var r = new StreamReader(iso.OpenFile(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                        text = r.ReadToEnd();
                }
                var lines = text.Split('\n');
                var job = new InstallJob { Run = lines[0].Trim() };
                for (int n = 1; n < lines.Length; n++)
                {
                    var f = lines[n].Split('\t');
                    if (f.Length < 11) continue;
                    long size;
                    long.TryParse(f[7], out size);
                    job.Items.Add(new InstallItem
                    {
                        ProductId = f[0], Title = f[1], Version = f[2], Mode = f[3], File = f[4],
                        Url = f[5].Length > 0 ? f[5] : null, Sha256 = f[6].Length > 0 ? f[6] : null, Size = size,
                        ExistingTitle = f[8].Length > 0 ? f[8] : null, ExistingOnSd = f[9] == "sd", KindChange = f[10].Trim() == "kind",
                    });
                }
                return job.Run.Length == 0 || job.Items.Count == 0 ? null : job;
            }
            catch (Exception) { return null; }
        }

        public static void Clear()
        {
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    if (iso.FileExists(FileName)) iso.DeleteFile(FileName);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Where one item of a running job is, read from the batch's log.</summary>
    public sealed class ItemState
    {
        public string Phase = "waiting";   // waiting, downloading, installing, failed-download, finished
        public long Done, Total = -1;      // download progress
        public int ThExit = -1;
        public string Output = "";         // this item's part of the log
    }

    public static class JobLog
    {
        public static bool IsDone(InstallJob job) { return Exists(job.Run + ".done"); }

        static bool Exists(string f)
        {
            try { using (var iso = IsolatedStorageFile.GetUserStoreForApplication()) return iso.FileExists(f); }
            catch (Exception) { return false; }
        }

        public static string Read(InstallJob job)
        {
            try
            {
                using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!iso.FileExists(job.Run + ".log")) return "";
                    using (var r = new StreamReader(iso.OpenFile(job.Run + ".log", FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                        return r.ReadToEnd();
                }
            }
            catch (Exception) { return ""; }
        }

        public static List<ItemState> Parse(InstallJob job, string log)
        {
            var states = new List<ItemState>();
            foreach (var i in job.Items) states.Add(new ItemState());
            int cur = -1;
            var outputs = new StringBuilder[job.Items.Count];
            for (int n = 0; n < outputs.Length; n++) outputs[n] = new StringBuilder();
            foreach (var raw in (log ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("UB ", StringComparison.Ordinal))
                {
                    var p = line.Split(' ');
                    int n;
                    if (p.Length >= 3 && int.TryParse(p[1], out n) && n >= 0 && n < states.Count)
                    {
                        cur = n;
                        var s = states[n];
                        if (p[2] == "download") s.Phase = "downloading";
                        else if (p[2] == "install") s.Phase = "installing";
                        else if (p[2] == "fetchfail") s.Phase = "failed-download";
                        else if (p[2] == "th")
                        {
                            s.Phase = "finished";
                            int code;
                            if (p.Length >= 4 && int.TryParse(p[3], out code)) s.ThExit = code;
                        }
                        continue;
                    }
                }
                if (cur < 0) continue;
                outputs[cur].Append(line).Append('\n');
                if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
                {
                    var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    long d, t;
                    if (p.Length >= 3 && long.TryParse(p[1], out d) && long.TryParse(p[2], out t))
                    {
                        states[cur].Done = d;
                        states[cur].Total = t;
                    }
                }
            }
            for (int n = 0; n < states.Count; n++) states[n].Output = outputs[n].ToString();
            return states;
        }

        public static string Size(long b)
        {
            if (b >= 1024 * 1024) return (b / 1048576.0).ToString("0.0") + " MB";
            return Math.Max(1, b / 1024) + " KB";
        }

        /// <summary>One line on what the job is doing now, e.g. "2/3: downloading Retrobird, 1.2 of 4.0 MB".</summary>
        public static string Describe(InstallJob job, List<ItemState> states)
        {
            for (int n = 0; n < states.Count; n++)
            {
                var s = states[n];
                var it = job.Items[n];
                string step = states.Count > 1 ? (n + 1) + "/" + states.Count + ": " : "";
                if (s.Phase == "downloading")
                    return step + "downloading " + it.Title + (s.Total > 0 ? ", " + Size(s.Done) + " of " + Size(s.Total) : "...");
                if (s.Phase == "installing")
                    return step + (it.IsUpdate ? "updating " : "installing ") + it.Title + "...";
            }
            foreach (var s in states) if (s.Phase == "waiting") return "starting...";
            return "finishing...";
        }

        /// <summary>A short result without checking the phone (for the background agent): the app
        /// checks the installed versions when it's opened.</summary>
        public static string Summary(InstallJob job, List<ItemState> states)
        {
            var parts = new List<string>();
            for (int n = 0; n < states.Count; n++)
            {
                var it = job.Items[n];
                var s = states[n];
                if (s.Phase == "failed-download") parts.Add(it.Title + " didn't download");
                else if (s.Phase == "finished" && s.ThExit == 0) parts.Add(it.What + (it.IsUpdate ? " updated" : " installed"));
                else parts.Add(it.Title + " didn't install");
            }
            return string.Join(", ", parts.ToArray()) + ".";
        }

        /// <summary>The result's headline, e.g. "Retrobird 0.4 updated" or "Couldn't install Retrobird".</summary>
        public static string Title(InstallJob job, List<ItemState> states)
        {
            bool ok = AllOk(states);
            if (job.Items.Count == 1)
            {
                var it = job.Items[0];
                return ok ? it.What + (it.IsUpdate ? " updated" : " installed")
                          : (it.IsUpdate ? "Couldn't update " : "Couldn't install ") + it.Title;
            }
            return ok ? job.Items.Count + " apps done" : "Not everything installed";
        }

        public static bool AllOk(List<ItemState> states)
        {
            foreach (var s in states) if (s.Phase != "finished" || s.ThExit != 0) return false;
            return true;
        }
    }
}
