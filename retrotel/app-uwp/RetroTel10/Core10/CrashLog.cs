// Windows 10 version of the 8.1 app's Services/CrashLog.cs: unexpected failures go to crash.txt
// in the app's local folder and are shown once on the next start. The file stays on the device.
using System;
using System.IO;
using Windows.Storage;

namespace MetroTelegram.Services
{
    public static class CrashLog
    {
        const string FileName = "crash.txt";
        static readonly object _lock = new object();

        static string PathOf() { return Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName); }

        public static void Write(string where, Exception ex)
        {
            try
            {
                lock (_lock)
                    File.AppendAllText(PathOf(), "=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + where + "\r\n" +
                        (ex == null ? "(no exception)" : ex.ToString()) + "\r\n\r\n");
            }
            catch { }
        }

        public static void Mark(string step) { }

        /// <summary>The last crash report, removed once read; null when there is none.</summary>
        public static string TakeLast()
        {
            try
            {
                lock (_lock)
                {
                    string p = PathOf();
                    if (!File.Exists(p)) return null;
                    string text = File.ReadAllText(p);
                    File.Delete(p);
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
            }
            catch { return null; }
        }
    }
}
