#!/usr/bin/env python3
"""Homebrew catalogue tool for the WP 8.1 app loader (stage 2).

  catalog.py keygen  --out KEY.pem             new Ed25519 signing key (never commit it)
  catalog.py add     PKG --url URL [--id ID] [--app-dir DIR] [--notes TEXT]
                     [--name N --author A --description D --license L --source URL]
                     [--icons-dir DIR --icons-url URL [--icon-file PNG]]
                                               read PKG's manifest, hash it, add a release
  catalog.py build   [--app-dir DIR] [--out DIR] --key KEY.pem
                                               merge apps/*.json into index.json + index.json.sig
  catalog.py verify  INDEX SIG --pub BASE64    check a signature the way the phone does

The index is canonical JSON (sorted keys, no spaces, UTF-8). The signature is the
raw 64-byte Ed25519 signature over the exact bytes of index.json, base64 encoded.
"""
import argparse, base64, datetime, hashlib, json, os, re, sys, zipfile
import xml.etree.ElementTree as ET

SCHEMA = 1
ID_RE = re.compile(r"^[a-z0-9][a-z0-9.-]{1,63}$")
VER_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+$")


def fail(msg):
    sys.stderr.write("catalog: " + msg + "\n")
    sys.exit(1)


def canonical(obj):
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def local(tag):
    return tag.rsplit("}", 1)[-1]


def find(root, name):
    for el in root.iter():
        if local(el.tag) == name:
            return el
    return None


def zread(z, name):
    """Read a zip member even when the central directory and local header spell the name
    differently (Windows Phone .xap files use '\\' in one and '/' in the other)."""
    import struct, zlib
    info = z.getinfo(name)
    with open(z.filename, "rb") as f:
        f.seek(info.header_offset)
        h = f.read(30)
        if h[:4] != b"PK\x03\x04":
            fail("bad zip entry " + name)
        nlen, xlen = struct.unpack("<HH", h[26:30])
        f.seek(info.header_offset + 30 + nlen + xlen)
        raw = f.read(info.compress_size)
    if info.compress_type == zipfile.ZIP_STORED:
        return raw
    if info.compress_type == zipfile.ZIP_DEFLATED:
        return zlib.decompressobj(-15).decompress(raw)
    fail("unsupported compression in " + name)


def guid(v):
    return v.strip().strip("{}").upper() if v else None


def phone_product_id(root):
    el = find(root, "PhoneIdentity")
    return guid(el.get("PhoneProductId")) if el is not None else None


def read_manifest(path):
    m = _read_manifest(path)
    if not m.get("product_id"):
        fail(path + ": no phone product ID in the manifest (PhoneIdentity/ProductID); the phone can't track it")
    return m


def _read_manifest(path):
    """Return identity facts from an .appx, .appxbundle or .xap."""
    ext = os.path.splitext(path)[1].lower()
    with zipfile.ZipFile(path) as z:
        names = {n.lower(): n for n in z.namelist()}
        if ext == ".appxbundle":
            root = ET.fromstring(zread(z, names["appxmetadata/appxbundlemanifest.xml"]))
            ident = find(root, "Identity")
            archs = sorted({p.get("Architecture", "neutral") for p in root.iter()
                            if local(p.tag) == "Package" and p.get("Type", "application") == "application"})
            pid = None
            for p in root.iter():
                if local(p.tag) == "Package" and p.get("Type", "application") == "application" and p.get("FileName"):
                    inner = names.get(p.get("FileName").lower())
                    if inner:
                        import io
                        with zipfile.ZipFile(io.BytesIO(zread(z, inner))) as iz:
                            n2 = {n.lower(): n for n in iz.namelist()}
                            if "appxmanifest.xml" in n2:
                                pid = phone_product_id(ET.fromstring(iz.read(n2["appxmanifest.xml"])))
                    if pid:
                        break
            return {"kind": "appxbundle", "package_name": ident.get("Name"), "product_id": pid,
                    "publisher": ident.get("Publisher"), "version": ident.get("Version"),
                    "arch": ",".join(archs) or "neutral", "title": None}
        if "appxmanifest.xml" in names:  # .appx, or a Silverlight 8.1 .xap
            root = ET.fromstring(zread(z, names["appxmanifest.xml"]))
            ident = find(root, "Identity")
            disp = find(root, "DisplayName")
            title = disp.text.strip() if disp is not None and disp.text else None
            if title and title.startswith("ms-resource:"):
                title = None
            pid = phone_product_id(root)
            if ext == ".xap" and "wmappmanifest.xml" in names:
                app = find(ET.fromstring(zread(z, names["wmappmanifest.xml"])), "App")
                if app is not None and app.get("ProductID"):
                    pid = guid(app.get("ProductID"))
            return {"kind": "xap81" if ext == ".xap" else "appx", "package_name": ident.get("Name"),
                    "product_id": pid,
                    "publisher": ident.get("Publisher"), "version": ident.get("Version"),
                    "arch": ident.get("ProcessorArchitecture", "neutral"), "title": title}
        if "wmappmanifest.xml" in names:  # WP 8.0 / 7.x .xap
            root = ET.fromstring(zread(z, names["wmappmanifest.xml"]))
            app = find(root, "App")
            return {"kind": "xap80", "package_name": guid(app.get("ProductID")),
                    "product_id": guid(app.get("ProductID")),
                    "publisher": app.get("Publisher"), "version": app.get("Version"),
                    "arch": "neutral", "title": app.get("Title")}
    fail(path + ": no AppxManifest.xml or WMAppManifest.xml (not a phone package?)")


MAX_ICON = 256 * 1024


def extract_icon(path):
    """The app's tile/list icon (PNG bytes) from its package: the largest-scale variant of the
    tile logo, else the app list icon. None if the package has none."""
    with zipfile.ZipFile(path) as z:
        names = {n.replace("\\", "/").lower(): n for n in z.namelist()}
        cands = []
        if "appxmanifest.xml" in names:
            root = ET.fromstring(zread(z, names["appxmanifest.xml"]))
            ve = find(root, "VisualElements")
            for attr in ("Square150x150Logo", "Square71x71Logo", "Square44x44Logo"):
                if ve is not None and ve.get(attr):
                    cands.append(ve.get(attr))
            logo = find(root, "Logo")
            if logo is not None and logo.text:
                cands.append(logo.text.strip())
        if "wmappmanifest.xml" in names:
            root = ET.fromstring(zread(z, names["wmappmanifest.xml"]))
            for tag in ("BackgroundImageURI", "IconPath"):
                el = find(root, tag)
                if el is not None and el.text and el.text.strip():
                    cands.append(el.text.strip())
        for c in cands:
            base = c.replace("\\", "/").lstrip("/").lower()
            stem, ext = os.path.splitext(base)
            if ext != ".png":
                continue
            # exact file, or resource-qualified variants: Logo.scale-240.png, Logo.targetsize-44.png
            variants = [n for n in names if n == base or
                        (n.startswith(stem + ".") and n.endswith(".png") and re.search(r"\.(scale|targetsize)-\d+", n[len(stem):]))]
            if not variants:
                continue
            def score(n):
                m = re.search(r"(?:scale|targetsize)-(\d+)", n[len(stem):])
                return int(m.group(1)) if m else 100
            best = max(variants, key=score)
            data = zread(z, names[best])
            if data[:8] == b"\x89PNG\r\n\x1a\n" and len(data) <= MAX_ICON:
                return data
    return None


def set_icon(app, data, icons_dir, icons_url):
    os.makedirs(icons_dir, exist_ok=True)
    with open(os.path.join(icons_dir, app["id"] + ".png"), "wb") as f:
        f.write(data)
    app["icon"] = icons_url.rstrip("/") + "/" + app["id"] + ".png"
    app["icon_sha256"] = hashlib.sha256(data).hexdigest()
    app["icon_size"] = len(data)


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def validate_app(app, fname):
    def need(cond, msg):
        if not cond:
            fail("%s: %s" % (fname, msg))
    need(ID_RE.match(app.get("id", "")), "id must match " + ID_RE.pattern)
    need(app.get("name"), "name missing")
    need(isinstance(app.get("releases"), list) and app["releases"], "no releases")
    if app.get("icon"):
        need(app["icon"].startswith("https://"), "icon must be an https URL")
        need(re.fullmatch(r"[0-9a-f]{64}", app.get("icon_sha256", "")), "icon needs icon_sha256 (use --icons-dir)")
        need(isinstance(app.get("icon_size"), int) and 0 < app["icon_size"] <= MAX_ICON, "icon_size must be 1..%d" % MAX_ICON)
    pkgs = set()
    for r in app["releases"]:
        need(VER_RE.match(r.get("version", "")), "bad version %r" % r.get("version"))
        need(r.get("url", "").startswith("https://"), "url must be https")
        need(re.fullmatch(r"[0-9a-f]{64}", r.get("sha256", "")), "sha256 must be 64 hex chars")
        need(isinstance(r.get("size"), int) and r["size"] > 0, "size must be a positive int")
        need(r.get("kind") in ("appx", "appxbundle", "xap81", "xap80"), "bad kind")
        need(r.get("package_name"), "package_name missing")
        need(re.fullmatch(r"[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}", r.get("product_id") or ""),
             "product_id must be an upper-case GUID without braces")
        pkgs.add((r["package_name"], r["product_id"]))
    need(len(pkgs) == 1, "all releases of one app must share package_name and product_id (got %s)" % sorted(pkgs))


def vkey(v):
    return tuple(int(x) for x in v.split("."))


# Ed25519 through the "cryptography" package, or the openssl command (3.x) when that package
# isn't installed. Both use the same key file (PKCS#8 PEM) and raw 64-byte signatures.

def have_cryptography():
    try:
        import cryptography.hazmat.primitives.asymmetric.ed25519  # noqa: F401
        return True
    except ImportError:
        return False


def openssl(args, data=None):
    import subprocess
    try:
        r = subprocess.run(["openssl"] + args, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except FileNotFoundError:
        fail("needs the Python package 'cryptography' or the openssl command (3.x)")
    if r.returncode != 0:
        fail("openssl %s: %s" % (args[0], r.stderr.decode(errors="replace").strip()))
    return r.stdout


# DER prefix of an Ed25519 SubjectPublicKeyInfo; the raw 32-byte key follows it
ED25519_SPKI = bytes.fromhex("302a300506032b6570032100")


def ed_public_raw(pem_path):
    der = openssl(["pkey", "-in", pem_path, "-pubout", "-outform", "DER"])
    if len(der) != 44 or not der.startswith(ED25519_SPKI):
        fail(pem_path + " is not an Ed25519 key")
    return der[12:]


def ed_sign(pem_path, data):
    if have_cryptography():
        from cryptography.hazmat.primitives.serialization import load_pem_private_key
        with open(pem_path, "rb") as f:
            return load_pem_private_key(f.read(), password=None).sign(data)
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        msg = os.path.join(d, "msg")
        with open(msg, "wb") as f:
            f.write(data)
        sig = openssl(["pkeyutl", "-sign", "-inkey", pem_path, "-rawin", "-in", msg])
    if len(sig) != 64:
        fail("openssl gave a %d-byte signature, expected 64" % len(sig))
    return sig


def ed_verify(pub_raw, sig, data):
    if have_cryptography():
        from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
        from cryptography.exceptions import InvalidSignature
        try:
            Ed25519PublicKey.from_public_bytes(pub_raw).verify(sig, data)
            return True
        except InvalidSignature:
            return False
    import subprocess, tempfile
    with tempfile.TemporaryDirectory() as d:
        paths = {}
        for name, content in (("pub.der", ED25519_SPKI + pub_raw), ("msg", data), ("sig", sig)):
            paths[name] = os.path.join(d, name)
            with open(paths[name], "wb") as f:
                f.write(content)
        r = subprocess.run(["openssl", "pkeyutl", "-verify", "-pubin", "-keyform", "DER", "-inkey", paths["pub.der"],
                            "-rawin", "-in", paths["msg"], "-sigfile", paths["sig"]],
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        return r.returncode == 0


def cmd_keygen(a):
    if os.path.exists(a.out):
        fail(a.out + " exists; refusing to overwrite a signing key")
    if have_cryptography():
        from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
        from cryptography.hazmat.primitives import serialization as s
        k = Ed25519PrivateKey.generate()
        fd = os.open(a.out, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "wb") as f:
            f.write(k.private_bytes(s.Encoding.PEM, s.PrivateFormat.PKCS8, s.NoEncryption()))
        pub = k.public_key().public_bytes(s.Encoding.Raw, s.PublicFormat.Raw)
    else:
        pem = openssl(["genpkey", "-algorithm", "ed25519"])
        fd = os.open(a.out, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "wb") as f:
            f.write(pem)
        pub = ed_public_raw(a.out)
    print("public key (put in the app):", base64.b64encode(pub).decode())


def cmd_add(a):
    m = read_manifest(a.package)
    if not VER_RE.match(m["version"] or ""):
        fail("manifest version %r is not a.b.c.d" % m["version"])
    app_id = a.id or re.sub(r"[^a-z0-9.-]", "-", (m["package_name"] or "").lower()).strip("-")
    path = os.path.join(a.app_dir, app_id + ".json")
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            app = json.load(f)
    else:
        app = {"id": app_id, "name": m["title"] or app_id, "author": "", "description": "",
               "license": "", "source": "", "releases": []}
    rel = {"version": m["version"], "kind": m["kind"], "arch": m["arch"],
           "package_name": m["package_name"], "product_id": m["product_id"], "publisher": m["publisher"],
           "url": a.url, "sha256": sha256_file(a.package), "size": os.path.getsize(a.package),
           "min_os": a.min_os, "notes": a.notes or "",
           "added": datetime.date.today().isoformat()}
    for field in ("name", "author", "description", "license", "source"):
        v = getattr(a, field, None)
        if v is not None:
            app[field] = v
    if a.icons_dir:
        if not a.icons_url:
            fail("--icons-dir needs --icons-url")
        if a.icon_file:
            with open(a.icon_file, "rb") as f:
                data = f.read()
            if data[:8] != b"\x89PNG\r\n\x1a\n" or len(data) > MAX_ICON:
                fail(a.icon_file + ": icon must be a PNG of at most %d bytes" % MAX_ICON)
        else:
            data = extract_icon(a.package)
        if data:
            set_icon(app, data, a.icons_dir, a.icons_url)
        else:
            print("note: no PNG icon found in the package; pass --icon-file")
    app["releases"] = [r for r in app["releases"] if r["version"] != rel["version"]] + [rel]
    app["releases"].sort(key=lambda r: vkey(r["version"]), reverse=True)
    validate_app(app, path)
    os.makedirs(a.app_dir, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(app, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print("%s %s -> %s" % (app_id, rel["version"], path))


def load_apps(app_dir):
    apps = []
    for fn in sorted(os.listdir(app_dir)):
        if not fn.endswith(".json"):
            continue
        p = os.path.join(app_dir, fn)
        with open(p, encoding="utf-8") as f:
            app = json.load(f)
        validate_app(app, p)
        if app["id"] + ".json" != fn:
            fail("%s: file name must be <id>.json" % p)
        app["releases"].sort(key=lambda r: vkey(r["version"]), reverse=True)
        apps.append(app)
    for key in ("package_name", "product_id"):
        names = [r[key] for a in apps for r in a["releases"][:1]]
        dup = {n for n in names if names.count(n) > 1}
        if dup:
            fail("%s used by more than one app: %s" % (key, sorted(dup)))
    return apps


def cmd_build(a):
    apps = load_apps(a.app_dir)
    old_serial = 0
    prev = os.path.join(a.out, "index.json")
    if os.path.exists(prev):
        with open(prev, "rb") as f:
            old_serial = json.loads(f.read()).get("serial", 0)
    now = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0)
    serial = max(old_serial + 1, int(now.timestamp()))  # never goes backwards
    index = {"schema": SCHEMA, "serial": serial, "generated": now.isoformat().replace("+00:00", "Z"),
             "name": a.name, "apps": apps}
    data = canonical(index)
    sig = base64.b64encode(ed_sign(a.key, data))
    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.out, "index.json"), "wb") as f:
        f.write(data)
    with open(os.path.join(a.out, "index.json.sig"), "wb") as f:
        f.write(sig + b"\n")
    print("index.json: %d apps, serial %d, %d bytes" % (len(apps), serial, len(data)))


def cmd_verify(a):
    with open(a.index, "rb") as f:
        data = f.read()
    with open(a.sig, "rb") as f:
        sig = base64.b64decode(f.read().strip())
    if not ed_verify(base64.b64decode(a.pub), sig, data):
        fail("BAD SIGNATURE")
    idx = json.loads(data)
    if idx.get("schema") != SCHEMA:
        fail("unknown schema %r" % idx.get("schema"))
    print("OK serial %d, %d apps" % (idx["serial"], len(idx["apps"])))


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    k = sub.add_parser("keygen"); k.add_argument("--out", required=True); k.set_defaults(fn=cmd_keygen)
    d = sub.add_parser("add"); d.add_argument("package"); d.add_argument("--url", required=True)
    d.add_argument("--id"); d.add_argument("--notes"); d.add_argument("--min-os", default="8.10.0.0")
    for field in ("name", "author", "description", "license", "source"):
        d.add_argument("--" + field, help="set the app's %s (kept for later releases)" % field)
    d.add_argument("--icons-dir", help="write the app's icon here (taken from the package, or --icon-file)")
    d.add_argument("--icons-url", help="https base URL the icons dir is served at")
    d.add_argument("--icon-file", help="use this PNG as the icon instead of the package's")
    d.add_argument("--app-dir", default=os.path.join(here, "..", "apps")); d.set_defaults(fn=cmd_add)
    b = sub.add_parser("build"); b.add_argument("--key", required=True)
    b.add_argument("--app-dir", default=os.path.join(here, "..", "apps"))
    b.add_argument("--out", default=os.path.join(here, "..", "dist"))
    b.add_argument("--name", default="wp-homebrew"); b.set_defaults(fn=cmd_build)
    v = sub.add_parser("verify"); v.add_argument("index"); v.add_argument("sig")
    v.add_argument("--pub", required=True); v.set_defaults(fn=cmd_verify)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
