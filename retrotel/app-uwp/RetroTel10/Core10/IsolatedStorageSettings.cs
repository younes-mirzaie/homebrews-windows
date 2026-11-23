// Windows 10 stand-in for Silverlight's IsolatedStorageSettings, so the shared core
// (Crypto/AuthKeyStorage.cs) runs unchanged. It keeps the values in the app's local settings.
// byte[] values are stored as Base64 text, which local settings always accept.
using System;
using Windows.Storage;

namespace System.IO.IsolatedStorage
{
    public sealed class IsolatedStorageSettings
    {
        const string BytesPrefix = "\u0001b64:";
        static readonly IsolatedStorageSettings _app = new IsolatedStorageSettings();
        public static IsolatedStorageSettings ApplicationSettings { get { return _app; } }

        static ApplicationDataContainer Store { get { return ApplicationData.Current.LocalSettings; } }

        public object this[string key]
        {
            get
            {
                object v;
                if (!Store.Values.TryGetValue(key, out v)) throw new System.Collections.Generic.KeyNotFoundException(key);
                var s = v as string;
                if (s != null && s.StartsWith(BytesPrefix, StringComparison.Ordinal))
                    return Convert.FromBase64String(s.Substring(BytesPrefix.Length));
                return v;
            }
            set
            {
                if (value == null) { Store.Values.Remove(key); return; }
                var bytes = value as byte[];
                Store.Values[key] = bytes != null ? BytesPrefix + Convert.ToBase64String(bytes) : value;
            }
        }

        public bool Contains(string key) { return Store.Values.ContainsKey(key); }
        public bool Remove(string key) { return Store.Values.Remove(key); }
        public void Clear() { Store.Values.Clear(); }
        /// <summary>Local settings are written at once; kept for the shared code.</summary>
        public void Save() { }
    }
}
