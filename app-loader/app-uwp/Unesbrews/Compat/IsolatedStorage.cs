// The Windows Phone 8.1 core (..\..\app\AppLoader\Core) keeps its files and settings in Silverlight's
// isolated storage. A UWP app has no such thing, so these two classes give the shared code the same
// calls on top of the app's local folder (ApplicationData.LocalFolder, the same folder the phone's
// system shell writes run logs into) and local settings.
//
// They are declared in the Silverlight namespace on purpose, so the shared files compile unchanged.
// .NET Standard 2.0 also has an IsolatedStorageFile; this source type wins (warning CS0436, which the
// project silences), and that one would point to a different folder than Shell.LocalPath.
using System.Collections.Generic;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace System.IO.IsolatedStorage
{
    public sealed class IsolatedStorageFile : IDisposable
    {
        readonly string _root = ApplicationData.Current.LocalFolder.Path;

        public static IsolatedStorageFile GetUserStoreForApplication() { return new IsolatedStorageFile(); }

        string Full(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (Path.IsPathRooted(name) || name.Contains("..")) throw new ArgumentException("Not a file of this app: " + name);
            return Path.Combine(_root, name);
        }

        public bool FileExists(string name) { return File.Exists(Full(name)); }
        public bool DirectoryExists(string name) { return Directory.Exists(Full(name)); }
        public void CreateDirectory(string name) { Directory.CreateDirectory(Full(name)); }
        public void DeleteFile(string name) { File.Delete(Full(name)); }
        public void MoveFile(string from, string to) { File.Move(Full(from), Full(to)); }

        public Stream CreateFile(string name)
        {
            return new FileStream(Full(name), FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        }

        public Stream OpenFile(string name, FileMode mode, FileAccess access)
        {
            return new FileStream(Full(name), mode, access, access == FileAccess.Read ? FileShare.ReadWrite : FileShare.Read);
        }

        public Stream OpenFile(string name, FileMode mode, FileAccess access, FileShare share)
        {
            return new FileStream(Full(name), mode, access, share);
        }

        /// <summary>File names (without folders) matching a pattern such as "wa-*" or
        /// "install-logs\*.log", like Silverlight's.</summary>
        public string[] GetFileNames(string pattern)
        {
            string dir = Path.GetDirectoryName(pattern) ?? "";
            string mask = Path.GetFileName(pattern);
            string full = dir.Length == 0 ? _root : Full(dir);
            if (!Directory.Exists(full)) return new string[0];
            var names = new List<string>();
            foreach (var f in Directory.GetFiles(full, mask)) names.Add(Path.GetFileName(f));
            return names.ToArray();
        }

        public void Dispose() { }
    }

    /// <summary>Silverlight's app settings on top of ApplicationData.LocalSettings. Values are the
    /// simple kinds the shared code stores (int, long, string, bool).</summary>
    public sealed class IsolatedStorageSettings
    {
        public static readonly IsolatedStorageSettings ApplicationSettings = new IsolatedStorageSettings();

        static IPropertySet Values { get { return ApplicationData.Current.LocalSettings.Values; } }

        public bool TryGetValue<T>(string key, out T value)
        {
            object v;
            if (Values.TryGetValue(key, out v) && v is T) { value = (T)v; return true; }
            value = default(T);
            return false;
        }

        public bool Contains(string key) { return Values.ContainsKey(key); }
        public bool Remove(string key) { return Values.Remove(key); }

        public object this[string key]
        {
            get { return Values[key]; }
            set { Values[key] = value; }
        }

        public void Save() { }   // LocalSettings saves at once
    }
}
