#!/usr/bin/env python3
"""Self-contained checks for tools/catalog.py. Run: python3 test/test_catalog.py"""
import json, os, subprocess, sys, tempfile, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.join(HERE, "..", "tools", "catalog.py")
APPX = ('<Package xmlns="http://schemas.microsoft.com/appx/2010/manifest">'
        '<Identity Name="Rfx.Demo" Publisher="CN=Test" Version="%s" ProcessorArchitecture="arm"/>'
        '<mp:PhoneIdentity xmlns:mp="http://schemas.microsoft.com/appx/2014/phone/manifest" '
        'PhoneProductId="aaaaaaaa-bbbb-cccc-dddd-%s" PhonePublisherId="00000000-0000-0000-0000-000000000000"/>'
        '<Properties><DisplayName>Demo App</DisplayName></Properties></Package>')
WM = ('<Deployment xmlns="http://schemas.microsoft.com/windowsphone/2012/deployment">'
      '<App ProductID="{11111111-2222-3333-4444-555555555555}" Title="Old Demo" Version="1.0.0.0" Publisher="Me"/>'
      '</Deployment>')
passed = 0


def run(*args, ok=True):
    r = subprocess.run([sys.executable, TOOL] + list(args), capture_output=True, text=True)
    if ok and r.returncode != 0:
        raise AssertionError("failed: %s\n%s%s" % (args, r.stdout, r.stderr))
    if not ok and r.returncode == 0:
        raise AssertionError("should have failed: %s\n%s" % (args, r.stdout))
    return r.stdout + r.stderr


def check(name, cond):
    global passed
    if not cond:
        raise AssertionError(name)
    passed += 1
    print("ok", name)


def pkg(path, member, xml):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(member, xml)
        z.writestr("payload.bin", os.urandom(2048))


with tempfile.TemporaryDirectory() as t:
    apps, dist, key = os.path.join(t, "apps"), os.path.join(t, "dist"), os.path.join(t, "k.pem")
    out = run("keygen", "--out", key)
    pub = out.strip().split()[-1]
    check("keygen prints a 32-byte key", len(pub) == 44)
    run("keygen", "--out", key, ok=False)
    check("keygen refuses to overwrite", True)

    a1, a2 = os.path.join(t, "demo1.appx"), os.path.join(t, "demo2.appx")
    pkg(a1, "AppxManifest.xml", APPX % ("1.0.0.0", "eeeeeeeeeeee"))
    pkg(a2, "AppxManifest.xml", APPX % ("1.10.0.0", "eeeeeeeeeeee"))
    run("add", a1, "--url", "https://example.org/demo1.appx", "--app-dir", apps)
    run("add", a2, "--url", "https://example.org/demo2.appx", "--app-dir", apps, "--notes", "faster")
    app = json.load(open(os.path.join(apps, "rfx.demo.json")))
    check("appx manifest read", app["name"] == "Demo App" and app["releases"][0]["arch"] == "arm")
    check("releases newest first (numeric compare)", [r["version"] for r in app["releases"]] == ["1.10.0.0", "1.0.0.0"])
    check("sha256 + size recorded", len(app["releases"][0]["sha256"]) == 64 and app["releases"][0]["size"] == os.path.getsize(a2))

    x80, x81 = os.path.join(t, "old.xap"), os.path.join(t, "new.xap")
    pkg(x80, "WMAppManifest.xml", WM)
    with zipfile.ZipFile(x81, "w") as z:
        z.writestr("AppxManifest.xml", (APPX % ("2.0.0.0", "ffffffffffff")).replace("Rfx.Demo", "Rfx.Sl81"))
        z.writestr("WMAppManifest.xml", WM.replace("{11111111-", "{22222222-"))
    run("add", x80, "--url", "https://example.org/old.xap", "--id", "old-demo", "--app-dir", apps)
    run("add", x81, "--url", "https://example.org/new.xap", "--app-dir", apps)
    check("xap80 kind", json.load(open(os.path.join(apps, "old-demo.json")))["releases"][0]["kind"] == "xap80")
    r81 = json.load(open(os.path.join(apps, "rfx.sl81.json")))["releases"][0]
    check("xap81 prefers AppxManifest", r81["kind"] == "xap81")
    check("xap81 product id from WMAppManifest", r81["product_id"] == "22222222-2222-3333-4444-555555555555")
    check("appx product id from PhoneIdentity", app["releases"][0]["product_id"] == "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")
    nopid = os.path.join(t, "nopid.appx")
    pkg(nopid, "AppxManifest.xml", '<Package><Identity Name="X.Y" Publisher="CN=T" Version="1.0.0.0"/></Package>')
    run("add", nopid, "--url", "https://example.org/x.appx", "--app-dir", apps, ok=False)
    check("package without phone product id rejected", not os.path.exists(os.path.join(apps, "x.y.json")))
    run("add", a1, "--url", "http://example.org/x.appx", "--id", "plain", "--app-dir", apps, ok=False)
    check("plain http url rejected", not os.path.exists(os.path.join(apps, "plain.json")))

    run("build", "--key", key, "--app-dir", apps, "--out", dist)
    idx, sig = os.path.join(dist, "index.json"), os.path.join(dist, "index.json.sig")
    check("verify good index", "OK" in run("verify", idx, sig, "--pub", pub))
    s1 = json.load(open(idx))["serial"]
    run("build", "--key", key, "--app-dir", apps, "--out", dist)
    check("serial strictly increases", json.load(open(idx))["serial"] > s1)

    data = open(idx, "rb").read()
    open(idx, "wb").write(data.replace(b"faster", b"evil!!"))
    run("verify", idx, sig, "--pub", pub, ok=False)
    check("tampered index rejected", True)
    open(idx, "wb").write(data)
    other = os.path.join(t, "other.pem")
    otherpub = run("keygen", "--out", other).strip().split()[-1]
    run("verify", idx, sig, "--pub", otherpub, ok=False)
    check("wrong key rejected", True)

    bad = json.load(open(os.path.join(apps, "rfx.demo.json")))
    bad["releases"][0]["package_name"] = "Someone.Else"
    json.dump(bad, open(os.path.join(apps, "rfx.demo.json"), "w"))
    run("build", "--key", key, "--app-dir", apps, "--out", dist, ok=False)
    check("mixed package names in one app rejected", True)

    # write a signed index for the C# client test
    bad["releases"][0]["package_name"] = "Rfx.Demo"
    json.dump(bad, open(os.path.join(apps, "rfx.demo.json"), "w"))
    run("build", "--key", key, "--app-dir", apps, "--out", dist)
    vec = os.environ.get("CATALOG_VECTORS")
    if vec:
        import shutil
        os.makedirs(vec, exist_ok=True)
        shutil.copy(idx, vec); shutil.copy(sig, vec)
        open(os.path.join(vec, "pub.txt"), "w").write(pub)

print("%d checks passed" % passed)
