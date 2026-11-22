// The apps installed through Unesbrews (from the catalog or with its sideload tool): the
// "installed" section lists these and nothing else, and automatic updates only touch these.
// Kept in library.txt, one app per line: product ID, name, version, when (UTC).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;

namespace AppLoader.Core
{
    public sealed class LibraryApp
    {
        public string ProductId;   // upper case, no braces
        public string Title, Version;
        public DateTime Added;
    }

    public static class Library
    {
        const string FileName = "library.txt";
        static readonly object _lock = new object();

        /// <summary>Whether the library was ever written (false on the first run of this version).</summary>
        public static bool Exists
        {
            get
            {
                try { using (var iso = IsolatedStorageFile.GetUserStoreForApplication()) return iso.FileExists(FileName); }
                catch (Exception) { return false; }
            }
        }

        public static List<LibraryApp> All()
        {
            var list = new List<LibraryApp>();
            lock (_lock)
            {
                try
                {
                    using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    {
                        if (!iso.FileExists(FileName)) return list;
                        using (var r = new StreamReader(iso.OpenFile(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                            foreach (var raw in r.ReadToEnd().Split('\n'))
                            {
                                var f = raw.TrimEnd('\r').Split('\t');
                                if (f.Length < 4 || f[0].Length == 0) continue;
                                long ticks;
                                long.TryParse(f[3], out ticks);
                                list.Add(new LibraryApp { ProductId = f[0], Title = f[1], Version = f[2], Added = new DateTime(Math.Max(0, ticks), DateTimeKind.Utc) });
                            }
                    }
                }
                catch (Exception) { }
            }
            return list;
        }

        public static bool Contains(string productId)
        {
            foreach (var a in All()) if (a.ProductId == productId) return true;
            return false;
        }

        /// <summary>Records an app that Unesbrews installed or updated (replaces an older line).</summary>
        public static void Add(string productId, string title, string version)
        {
            if (string.IsNullOrEmpty(productId)) return;
            var list = All();
            list.RemoveAll(a => a.ProductId == productId);
            list.Add(new LibraryApp { ProductId = productId, Title = title ?? "", Version = version ?? "", Added = DateTime.UtcNow });
            Save(list);
        }

        /// <summary>First run of a version with a library: the catalog apps already on the phone
        /// were installed through Unesbrews (it's the only place they come from), so they start
        /// the library. Runs once; after that only new installs are added.</summary>
        public static void StartWith(IEnumerable<CatalogEntry> entries)
        {
            if (Exists) return;
            var list = new List<LibraryApp>();
            foreach (var e in entries)
                if (e.Installed != null)
                    list.Add(new LibraryApp { ProductId = e.App.ProductId, Title = e.App.Name, Version = e.InstalledVersion ?? "", Added = DateTime.UtcNow });
            Save(list);
        }

        static void Save(List<LibraryApp> list)
        {
            var b = new StringBuilder();
            foreach (var a in list)
                b.Append(a.ProductId).Append('\t').Append(Clean(a.Title)).Append('\t').Append(Clean(a.Version)).Append('\t')
                 .Append(a.Added.Ticks).Append('\n');
            lock (_lock)
            {
                try
                {
                    using (var iso = IsolatedStorageFile.GetUserStoreForApplication())
                    using (var w = new StreamWriter(iso.CreateFile(FileName), new UTF8Encoding(false)))
                        w.Write(b.ToString());
                }
                catch (Exception e) { AppLog.Add("couldn't save the list of installed apps", e.Message); }
            }
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }
    }
}
