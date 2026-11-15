using System.Collections.Generic;

namespace RetroFiles.Core
{
    public static class AppState
    {
        public static RfxClient Client;
        public static readonly List<string> Clipboard = new List<string>();
        public static bool ClipboardIsCut;
        public static string PropertiesPath;   // handed to PropertiesPage
        public static string ViewerPath;       // handed to ViewerPage
        public static bool FolderChanged;      // a sub-page changed something: refresh on return

        /// <summary>Areas where a wrong delete or edit can stop the phone from booting.</summary>
        public static bool IsSystemArea(string path)
        {
            string p = path.ToUpperInvariant().TrimEnd('\\') + "\\";
            string[] risky = { @"C:\WINDOWS\", @"C:\EFIESP\", @"C:\DPP\", @"C:\PROGRAMS\", @"C:\MMOS\", @"C:\CRASHDUMP\" };
            foreach (var r in risky) if (p.StartsWith(r)) return true;
            return p.Length <= 3;   // a drive root
        }
    }
}
