using System;
using System.IO;
using System.IO.IsolatedStorage;

namespace MetroTelegram.Services
{
    /// <summary>
    /// Writes unhandled exceptions to isolated storage (crash.txt) so a startup crash can be read
    /// on the next launch or pulled from the phone.
    /// </summary>
    public static class CrashLog
    {
        private const string FileName = "crash.txt";

        public static void Write(string where, Exception ex)
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                using (var stream = store.OpenFile(FileName, FileMode.Append, FileAccess.Write))
                using (var writer = new StreamWriter(stream))
                {
                    writer.WriteLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + where);
                    writer.WriteLine(ex == null ? "(no exception)" : ex.ToString());
                    writer.WriteLine();
                }
            }
            catch
            {
                // Never let logging crash the app.
            }
        }

        /// <summary>Returns the saved crash text and deletes it, or null if there is none.</summary>
        public static string TakeLast()
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.FileExists(FileName)) return null;
                    string text;
                    using (var stream = store.OpenFile(FileName, FileMode.Open, FileAccess.Read))
                    using (var reader = new StreamReader(stream))
                    {
                        text = reader.ReadToEnd();
                    }
                    store.MoveFile(FileName, "crash-shown.txt.tmp");
                    if (store.FileExists("crash-shown.txt")) store.DeleteFile("crash-shown.txt");
                    store.MoveFile("crash-shown.txt.tmp", "crash-shown.txt");
                    return text;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
