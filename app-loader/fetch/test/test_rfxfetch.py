#!/usr/bin/env python3
"""Checks for rfxfetch against a local HTTPS server with its own CA.
Run: make && python3 test/test_rfxfetch.py [path/to/rfxfetch]"""
import datetime, hashlib, http.server, os, socket, ssl, subprocess, sys, tempfile, threading
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "build", "rfxfetch")
DATA = os.urandom(700 * 1024 + 13)
SHA = hashlib.sha256(DATA).hexdigest()
passed = 0


def make_ca(tmp):
    now = datetime.datetime.now(datetime.timezone.utc)
    ca_key = ec.generate_private_key(ec.SECP256R1())
    ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "rfxfetch test CA")])
    ca = (x509.CertificateBuilder().subject_name(ca_name).issuer_name(ca_name)
          .public_key(ca_key.public_key()).serial_number(1)
          .not_valid_before(now - datetime.timedelta(days=1)).not_valid_after(now + datetime.timedelta(days=30))
          .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
          .add_extension(x509.KeyUsage(False, False, False, False, False, True, True, False, False), critical=True)
          .sign(ca_key, hashes.SHA256()))
    key = ec.generate_private_key(ec.SECP256R1())
    leaf = (x509.CertificateBuilder().subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")]))
            .issuer_name(ca_name).public_key(key.public_key()).serial_number(2)
            .not_valid_before(now - datetime.timedelta(days=1)).not_valid_after(now + datetime.timedelta(days=30))
            .add_extension(x509.SubjectAlternativeName([x509.DNSName("localhost")]), critical=False)
            .add_extension(x509.ExtendedKeyUsage([x509.oid.ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
            .sign(ca_key, hashes.SHA256()))
    paths = {n: os.path.join(tmp, n) for n in ("ca.pem", "leaf.pem", "leaf.key")}
    open(paths["ca.pem"], "wb").write(ca.public_bytes(serialization.Encoding.PEM))
    open(paths["leaf.pem"], "wb").write(leaf.public_bytes(serialization.Encoding.PEM))
    open(paths["leaf.key"], "wb").write(key.private_bytes(serialization.Encoding.PEM,
                                        serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    return paths


class H(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def send(self, code, body=b"", headers=()):
        self.send_response(code)
        for k, v in headers:
            self.send_header(k, v)
        if not any(k.lower() == "content-length" for k, _ in headers):
            self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        p, port = self.path, self.server.server_address[1]
        if p == "/file":
            self.send(200, DATA)
        elif p == "/chunked":
            self.send_response(200)
            self.send_header("Transfer-Encoding", "chunked")
            self.send_header("Connection", "close")
            self.end_headers()
            for i in range(0, len(DATA), 50000):
                part = DATA[i:i + 50000]
                self.wfile.write(b"%x\r\n" % len(part) + part + b"\r\n")
            self.wfile.write(b"0\r\n\r\n")
        elif p == "/rel":
            self.send(302, headers=[("Location", "/file")])
        elif p == "/abs":
            self.send(301, headers=[("Location", "https://localhost:%d/rel" % port)])
        elif p == "/tohttp":
            self.send(302, headers=[("Location", "http://localhost:%d/file" % port)])
        elif p == "/loop":
            self.send(302, headers=[("Location", "/loop")])
        elif p == "/short":
            self.send_response(200)
            self.send_header("Content-Length", str(len(DATA)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(DATA[:1000])
        else:
            self.send(404, b"no")


def run(args, code):
    r = subprocess.run([EXE] + args, capture_output=True, text=True, timeout=60)
    if r.returncode != code:
        raise AssertionError("%s: exit %d, wanted %d\n%s%s" % (args, r.returncode, code, r.stdout, r.stderr))
    return r.stdout


def check(name, cond):
    global passed
    if not cond:
        raise AssertionError(name)
    passed += 1
    print("ok", name)


with tempfile.TemporaryDirectory() as t:
    pki = make_ca(t)
    class Srv(http.server.ThreadingHTTPServer):
        def handle_error(self, request, client_address):
            pass   # rfxfetch hanging up mid-body (size limit) is expected

    srv = Srv(("127.0.0.1", 0), H)
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(pki["leaf.pem"], pki["leaf.key"])
    srv.socket = ctx.wrap_socket(srv.socket, server_side=True)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    base = "https://localhost:%d" % srv.server_address[1]
    out = os.path.join(t, "out.bin")
    ca = ["--ca", pki["ca.pem"]]

    def fresh():
        for f in (out, out + ".part"):
            if os.path.exists(f):
                os.remove(f)

    def got():
        return open(out, "rb").read() if os.path.exists(out) else None

    fresh(); o = run([base + "/file", out, "--sha256", SHA, "--size", str(len(DATA))] + ca, 0)
    check("download with length, hash + size verified", got() == DATA and ("OK %s %d" % (SHA, len(DATA))) in o)
    check("progress lines", "PROGRESS" in o and not os.path.exists(out + ".part"))
    fresh(); run([base + "/chunked", out, "--sha256", SHA] + ca, 0)
    check("chunked body", got() == DATA)
    fresh(); run([base + "/abs", out, "--sha256", SHA] + ca, 0)
    check("absolute then relative redirect", got() == DATA)
    fresh(); o = run([base + "/file", out, "--sha256", "0" * 64] + ca, 2)
    check("hash mismatch: exit 2, nothing left behind", got() is None and not os.path.exists(out + ".part") and "SHA-256 mismatch" in o)
    fresh(); run([base + "/file", out, "--size", "5"] + ca, 2)
    check("size mismatch rejected", got() is None)
    fresh(); o = run([base + "/file", out, "--max-bytes", "1000"] + ca, 1)
    check("max-bytes enforced", got() is None and "limit" in o)
    fresh(); o = run([base + "/tohttp", out] + ca, 1)
    check("redirect to http refused", got() is None and "https" in o)
    fresh(); o = run([base + "/loop", out] + ca, 1)
    check("redirect loop stops", "too many redirects" in o)
    fresh(); o = run([base + "/short", out] + ca, 1)
    check("truncated body rejected", got() is None and "closed before" in o)
    fresh(); o = run([base + "/nothing", out] + ca, 1)
    check("HTTP 404 reported", "HTTP 404" in o and got() is None)
    fresh(); o = run([base + "/file", out], 1)
    check("untrusted CA refused", got() is None and "ERROR" in o)
    fresh(); o = run([base.replace("localhost", "127.0.0.1") + "/file", out] + ca, 1)
    check("wrong host name refused", got() is None and "different server name" in o)
    fresh(); o = run(["http://localhost/file", out] + ca, 1)
    check("plain http refused", "only https" in o)
    open(out, "wb").write(b"old")
    run([base + "/file", out, "--sha256", "1" * 64] + ca, 2)
    check("failed download keeps the old file", got() == b"old")
    run([base + "/file", out, "--sha256", SHA] + ca, 0)
    check("successful download replaces the old file", got() == DATA)
    srv.shutdown()

print("%d checks passed" % passed)
