// The signed catalogue index (see ../DESIGN.md): verify, parse, and check it before the app trusts
// anything in it. The caller stores HighestSerial after a successful load and passes it next time.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AppLoader.Catalog
{
    public sealed class CatalogException : Exception
    {
        public CatalogException(string message) : base(message) { }
    }

    public sealed class CatalogRelease
    {
        public string Version, Kind, Arch, PackageName, ProductId, Publisher, Url, Sha256, MinOs, Notes, Added;
        public long Size;
    }

    public sealed class CatalogApp
    {
        public string Id, Name, Author, Description, License, Source, Icon;
        public List<CatalogRelease> Releases = new List<CatalogRelease>();   // newest first
        public CatalogRelease Latest { get { return Releases[0]; } }
        public string ProductId { get { return Releases[0].ProductId; } }
    }

    public sealed class CatalogIndex
    {
        public const int Schema = 1;
        public long Serial;
        public string Generated, Name;
        public List<CatalogApp> Apps = new List<CatalogApp>();

        static readonly Regex VersionRe = new Regex(@"^\d+\.\d+\.\d+\.\d+$");
        static readonly Regex Sha256Re = new Regex("^[0-9a-f]{64}$");
        static readonly Regex GuidRe = new Regex("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$");
        static readonly Regex IdRe = new Regex("^[a-z0-9][a-z0-9.-]{1,63}$");
        public const int MaxIndexBytes = 1024 * 1024;

        /// <summary>Checks the signature over the exact index bytes, then parses and validates it.
        /// highestSerialSeen: the largest serial this phone accepted before (0 the first time); an
        /// older index is refused so nobody can roll the catalogue back to a known-bad version.</summary>
        public static CatalogIndex Load(byte[] indexBytes, string signatureBase64, byte[] publicKey, long highestSerialSeen)
        {
            if (indexBytes == null || indexBytes.Length == 0 || indexBytes.Length > MaxIndexBytes)
                throw new CatalogException("The catalogue is empty or too large.");
            byte[] sig;
            try { sig = Convert.FromBase64String((signatureBase64 ?? "").Trim()); }
            catch (FormatException) { throw new CatalogException("The catalogue's signature is unreadable."); }
            if (!Ed25519.Verify(sig, indexBytes, publicKey))
                throw new CatalogException("The catalogue's signature doesn't match. It may have been tampered with, so it wasn't used.");

            Dictionary<string, object> root;
            try { root = Json.Parse(Encoding.UTF8.GetString(indexBytes, 0, indexBytes.Length)) as Dictionary<string, object>; }
            catch (FormatException e) { throw new CatalogException("The catalogue is signed but unreadable: " + e.Message); }
            if (root == null) throw new CatalogException("The catalogue is signed but unreadable.");
            if (Num(root, "schema") != Schema)
                throw new CatalogException("This catalogue needs a newer App Loader.");
            var idx = new CatalogIndex { Serial = Num(root, "serial"), Generated = Str(root, "generated"), Name = Str(root, "name") };
            if (idx.Serial < highestSerialSeen)
                throw new CatalogException("This catalogue is older than one the phone has already seen, so it wasn't used.");

            var ids = new HashSet<string>();
            var pids = new HashSet<string>();
            foreach (var a in List(root, "apps"))
            {
                var ao = a as Dictionary<string, object>;
                if (ao == null) throw Bad("an app entry isn't an object");
                var app = new CatalogApp
                {
                    Id = Str(ao, "id"), Name = Str(ao, "name"), Author = Str(ao, "author"),
                    Description = Str(ao, "description"), License = Str(ao, "license"),
                    Source = Str(ao, "source"), Icon = Str(ao, "icon"),
                };
                if (app.Id == null || !IdRe.IsMatch(app.Id)) throw Bad("bad app id");
                if (!ids.Add(app.Id)) throw Bad("app " + app.Id + " is listed twice");
                if (string.IsNullOrEmpty(app.Name)) throw Bad(app.Id + " has no name");
                foreach (var r in List(ao, "releases"))
                {
                    var ro = r as Dictionary<string, object>;
                    if (ro == null) throw Bad(app.Id + ": a release isn't an object");
                    var rel = new CatalogRelease
                    {
                        Version = Str(ro, "version"), Kind = Str(ro, "kind"), Arch = Str(ro, "arch"),
                        PackageName = Str(ro, "package_name"), ProductId = Str(ro, "product_id"),
                        Publisher = Str(ro, "publisher"), Url = Str(ro, "url"), Sha256 = Str(ro, "sha256"),
                        Size = Num(ro, "size"), MinOs = Str(ro, "min_os"), Notes = Str(ro, "notes"), Added = Str(ro, "added"),
                    };
                    if (rel.Version == null || !VersionRe.IsMatch(rel.Version)) throw Bad(app.Id + ": bad version");
                    if (rel.Url == null || !rel.Url.StartsWith("https://", StringComparison.Ordinal)) throw Bad(app.Id + ": url must be https");
                    if (rel.Sha256 == null || !Sha256Re.IsMatch(rel.Sha256)) throw Bad(app.Id + ": bad sha256");
                    if (rel.Size <= 0) throw Bad(app.Id + ": bad size");
                    if (rel.ProductId == null || !GuidRe.IsMatch(rel.ProductId)) throw Bad(app.Id + ": bad product_id");
                    if (rel.Kind != "appx" && rel.Kind != "appxbundle" && rel.Kind != "xap81" && rel.Kind != "xap80")
                        throw Bad(app.Id + ": bad kind");
                    if (app.Releases.Count > 0 && rel.ProductId != app.Releases[0].ProductId)
                        throw Bad(app.Id + ": releases with different product IDs");
                    app.Releases.Add(rel);
                }
                if (app.Releases.Count == 0) throw Bad(app.Id + " has no releases");
                app.Releases.Sort((x, y) => CompareVersions(y.Version, x.Version));
                if (!pids.Add(app.ProductId)) throw Bad("two apps share product ID " + app.ProductId);
                idx.Apps.Add(app);
            }
            return idx;
        }

        public CatalogApp FindByProductId(string productId)
        {
            if (productId == null) return null;
            string p = productId.Trim().Trim('{', '}').ToUpperInvariant();
            foreach (var a in Apps) if (a.ProductId == p) return a;
            return null;
        }

        /// <summary>Compares a.b.c.d versions numerically; -1, 0 or 1.</summary>
        public static int CompareVersions(string x, string y)
        {
            var a = x.Split('.'); var b = y.Split('.');
            for (int i = 0; i < 4; i++)
            {
                long p = i < a.Length ? long.Parse(a[i]) : 0, q = i < b.Length ? long.Parse(b[i]) : 0;
                if (p != q) return p < q ? -1 : 1;
            }
            return 0;
        }

        static CatalogException Bad(string what) { return new CatalogException("The catalogue is signed but invalid: " + what + "."); }

        static string Str(Dictionary<string, object> o, string k)
        {
            object v;
            return o.TryGetValue(k, out v) ? v as string : null;
        }

        static long Num(Dictionary<string, object> o, string k)
        {
            object v;
            return o.TryGetValue(k, out v) && v is long ? (long)v : -1;
        }

        static List<object> List(Dictionary<string, object> o, string k)
        {
            object v;
            var l = o.TryGetValue(k, out v) ? v as List<object> : null;
            if (l == null) throw Bad("'" + k + "' missing");
            return l;
        }
    }
}
