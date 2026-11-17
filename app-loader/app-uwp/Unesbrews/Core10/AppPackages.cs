// The phone's app list as Windows shows it (names and icons the way Start shows them), and opening
// an app. Needs the packageManagement capability; when Windows refuses it, the installed page falls
// back to the manifest scan (Installer10.InstalledAppsAsync).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace AppLoader.Core
{
    public sealed class PhoneApp
    {
        public Package Package;
        public string Name, Publisher, Version, FullName;
        public Uri Logo;
        public bool OnSd;
    }

    public static class AppPackages
    {
        /// <summary>The user's apps, without frameworks, resource packs and system parts. Throws
        /// when Windows doesn't allow this app to list them.</summary>
        public static Task<List<PhoneApp>> ListAsync()
        {
            return Task.Run(() =>
            {
                var list = new List<PhoneApp>();
                foreach (var p in new PackageManager().FindPackagesForUser(""))
                {
                    try
                    {
                        if (p.IsFramework || p.IsResourcePackage || p.IsBundle) continue;
                        if (p.SignatureKind == PackageSignatureKind.System) continue;
                        var v = p.Id.Version;
                        string name = p.DisplayName;
                        if (string.IsNullOrEmpty(name)) name = p.Id.Name;
                        string path = "";
                        try { path = p.InstalledLocation.Path; } catch (Exception) { }
                        Uri logo = null;
                        try { logo = p.Logo; } catch (Exception) { }
                        list.Add(new PhoneApp
                        {
                            Package = p,
                            Name = name,
                            Publisher = p.PublisherDisplayName,
                            Version = v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision,
                            FullName = p.Id.FullName,
                            Logo = logo,
                            OnSd = path.StartsWith("D:", StringComparison.OrdinalIgnoreCase),
                        });
                    }
                    catch (Exception) { }   // a half-installed package: leave it out
                }
                return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            });
        }

        /// <summary>Opens an app. False when it can't be found or doesn't open.</summary>
        public static async Task<bool> LaunchAsync(Package p)
        {
            try
            {
                var entries = await p.GetAppListEntriesAsync();
                return entries.Count > 0 && await entries[0].LaunchAsync();
            }
            catch (Exception e)
            {
                AppLog.Add("couldn't open " + p.Id.Name, e.Message);
                return false;
            }
        }

        /// <summary>The installed package of a catalog app (by product ID), or null. Uses the folder
        /// the manifest scan found: a package full name, or "{GUID}" for a Silverlight app.</summary>
        public static Task<Package> FindAsync(string productId)
        {
            string folder;
            lock (Installer10.Folders)
                if (!Installer10.Folders.TryGetValue(productId, out folder)) return Task.FromResult<Package>(null);
            return Task.Run(() =>
            {
                try
                {
                    var pm = new PackageManager();
                    if (!folder.StartsWith("{"))
                    {
                        var p = pm.FindPackageForUser("", folder);
                        if (p != null) return p;
                    }
                    string guid = "{" + productId + "}";
                    foreach (var p in pm.FindPackagesForUser(""))
                    {
                        try
                        {
                            if (p.InstalledLocation.Path.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0) return p;
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception e) { AppLog.Add("couldn't look up an installed app", e.Message); }
                return null;
            });
        }
    }
}
