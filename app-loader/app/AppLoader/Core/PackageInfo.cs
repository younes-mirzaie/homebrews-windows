// What a package says about itself: its product ID (how the phone knows an app), name, version,
// and whether it refuses the SD card. Read from the manifest inside the .xap/.appx.
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AppLoader.Core
{
    public sealed class PackageInfo
    {
        public string Kind;         // "xap" or "appx"
        public string ProductId;    // upper case, no braces
        public string Title;
        public string Version;
        public bool SdOptOut;       // the package must be installed in phone storage

        public static PackageInfo Read(Stream package)
        {
            var info = new PackageInfo();
            // A WP 8.1 Silverlight .xap carries both manifests; WMAppManifest.xml has the product ID
            // and SD opt-out there
            var wm = Zip.ReadEntry(package, "WMAppManifest.xml");
            if (wm != null)
            {
                string x = Text(wm);
                string app = Match(x, @"<App\b[^>]*>");
                info.Kind = "xap";
                info.ProductId = Guid(Attr(app, "ProductID"));
                info.Title = Attr(app, "Title");
                info.Version = Attr(app, "Version");
                info.SdOptOut = string.Equals(Attr(app, "SDOptOut"), "true", StringComparison.OrdinalIgnoreCase);
                return info;
            }
            var ax = Zip.ReadEntry(package, "AppxManifest.xml");
            if (ax == null)
                throw new InvalidDataException("No app manifest inside: not a phone app package (bundles aren't supported yet).");
            {
                string x = Text(ax);
                info.Kind = "appx";
                string id = Match(x, @"<Identity\b[^>]*>");
                string phone = Match(x, @"<(?:\w+:)?PhoneIdentity\b[^>]*>");
                info.ProductId = Guid(Attr(phone, "PhoneProductId"));
                info.Version = Attr(id, "Version");
                info.Title = InnerText(x, "DisplayName") ?? Attr(id, "Name");
                // Not seen in appx packages so far; honoured if a package declares it
                info.SdOptOut = Regex.IsMatch(x, @"SDOptOut\s*=\s*""true""", RegexOptions.IgnoreCase);
                return info;
            }
        }

        static string Text(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(b, skip, b.Length - skip);
        }

        static string Match(string s, string pattern)
        {
            var m = Regex.Match(s, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Value : "";
        }

        static string Attr(string element, string name)
        {
            var m = Regex.Match(element, @"\b" + name + @"\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        static string InnerText(string s, string element)
        {
            var m = Regex.Match(s, "<" + element + @">([^<]*)</" + element + ">");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        public static string Guid(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return s.Trim().Trim('{', '}').ToUpperInvariant();
        }
    }
}
