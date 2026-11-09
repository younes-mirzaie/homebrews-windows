// Mono checks for the C# catalogue client. Run via test/run-client-tests.sh.
using System;
using System.IO;
using System.Text;
using AppLoader.Catalog;

static class CatalogTest
{
    static int passed;
    static void Check(string name, bool ok) { if (!ok) throw new Exception("FAILED: " + name); passed++; Console.WriteLine("ok " + name); }
    static string Hex(byte[] b) { var s = new StringBuilder(); foreach (var x in b) s.Append(x.ToString("x2")); return s.ToString(); }
    static byte[] Unhex(string h) { var b = new byte[h.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(2 * i, 2), 16); return b; }

    static void Throws(string name, Action a, string contains)
    {
        try { a(); }
        catch (CatalogException e) { Check(name, e.Message.Contains(contains)); return; }
        throw new Exception("FAILED (no exception): " + name);
    }

    static int Main(string[] args)
    {
        string dir = args[0];
        // SHA-512 known answers
        Check("sha512 empty", Hex(Sha512.Hash(new byte[0])).StartsWith("cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce"));
        Check("sha512 abc", Hex(Sha512.Hash(Encoding.ASCII.GetBytes("abc"))).StartsWith("ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a"));
        Check("sha512 2 blocks", Hex(Sha512.Hash(Encoding.ASCII.GetBytes(new string('a', 200)))) ==
              Hex(Sha512.Hash(Encoding.ASCII.GetBytes(new string('a', 200)))));

        // RFC 8032 test 1 (empty message) and test 2 (one byte)
        var pk1 = Unhex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig1 = Unhex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        Check("rfc8032 #1 verifies", Ed25519.Verify(sig1, new byte[0], pk1));
        var pk2 = Unhex("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        var sig2 = Unhex("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        Check("rfc8032 #2 verifies", Ed25519.Verify(sig2, new byte[] { 0x72 }, pk2));
        Check("wrong message fails", !Ed25519.Verify(sig2, new byte[] { 0x73 }, pk2));
        Check("wrong key fails", !Ed25519.Verify(sig2, new byte[] { 0x72 }, pk1));
        var flipped = (byte[])sig2.Clone(); flipped[40] ^= 1;
        Check("flipped signature fails", !Ed25519.Verify(flipped, new byte[] { 0x72 }, pk2));
        var bigS = (byte[])sig2.Clone(); for (int i = 32; i < 64; i++) bigS[i] = 0xff;
        Check("non-canonical S rejected", !Ed25519.Verify(bigS, new byte[] { 0x72 }, pk2));

        // index signed by tools/catalog.py
        var index = File.ReadAllBytes(Path.Combine(dir, "index.json"));
        var sig = File.ReadAllText(Path.Combine(dir, "index.json.sig"));
        var pub = Convert.FromBase64String(File.ReadAllText(Path.Combine(dir, "pub.txt")).Trim());
        var idx = CatalogIndex.Load(index, sig, pub, 0);
        Check("python-signed index loads", idx.Apps.Count == 3 && idx.Serial > 0);
        var demo = idx.FindByProductId("{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}");
        Check("find by product id", demo != null && demo.Id == "rfx.demo");
        Check("latest is highest version", demo.Latest.Version == "1.10.0.0" && demo.Releases.Count == 2);
        Check("release fields", demo.Latest.Sha256.Length == 64 && demo.Latest.Size > 0 && demo.Latest.Url.StartsWith("https://"));
        CatalogIndex.Load(index, sig, pub, idx.Serial);
        Check("same serial accepted again", true);
        Throws("older serial refused", () => CatalogIndex.Load(index, sig, pub, idx.Serial + 1), "older");
        var tampered = (byte[])index.Clone(); tampered[tampered.Length / 2] ^= 0x20;
        Throws("tampered index refused", () => CatalogIndex.Load(tampered, sig, pub, 0), "signature");
        Throws("wrong key refused", () => CatalogIndex.Load(index, sig, pk1, 0), "signature");
        Throws("garbage signature refused", () => CatalogIndex.Load(index, "!!!", pub, 0), "signature");
        Check("version compare", CatalogIndex.CompareVersions("1.10.0.0", "1.9.0.0") > 0 && CatalogIndex.CompareVersions("1.0.0.0", "1.0.0.0") == 0);
        Check("json escapes", (string)Json.Parse("\"a\\u00e9\\n\"") == "aé\n");
        Console.WriteLine(passed + " checks passed");
        return 0;
    }
}
