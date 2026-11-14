"""A stand-in for Microsoft's sign-in server (device code flow and refresh tokens) for rbmaild's
tests, plus a token check Dovecot's oauth2 passdb calls. Usage: fake_oauth.py PORT EMAIL"""
import base64
import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

PORT, EMAIL = int(sys.argv[1]), sys.argv[2]
CLIENT = "test-client-id"
lock = threading.Lock()
state = {"polls": 0, "n": 0, "refresh": set()}


def jwt(claims):
    enc = lambda d: base64.urlsafe_b64encode(json.dumps(d).encode()).rstrip(b"=").decode()
    return enc({"alg": "none"}) + "." + enc(claims) + ".sig"


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def reply(self, code, obj, chunked=False):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        if chunked:
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
            for i in range(0, len(body), 7):
                part = body[i:i + 7]
                self.wfile.write(b"%x\r\n%s\r\n" % (len(part), part))
            self.wfile.write(b"0\r\n\r\n")
        else:
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    def do_GET(self):
        # Dovecot: GET /tokeninfo?access_token=...
        q = parse_qs(urlparse(self.path).query)
        tok = q.get("access_token", [""])[0]
        if tok.startswith("AT-ok"):
            self.reply(200, {"email": EMAIL, "active": True})
        else:
            self.reply(401, {"error": "invalid_token"})

    def do_POST(self):
        n = int(self.headers.get("Content-Length", "0"))
        f = {k: v[0] for k, v in parse_qs(self.rfile.read(n).decode()).items()}
        path = urlparse(self.path).path
        if f.get("client_id") != CLIENT:
            return self.reply(400, {"error": "unauthorized_client", "error_description": "AADSTS700016: unknown app"})
        if path == "/consumers/oauth2/v2.0/devicecode":
            if "IMAP.AccessAsUser.All" not in f.get("scope", "") or "offline_access" not in f.get("scope", ""):
                return self.reply(400, {"error": "invalid_scope"})
            return self.reply(200, {"device_code": "DEV-123", "user_code": "ABCD-EFGH",
                                    "verification_uri": "https://microsoft.com/link", "expires_in": 900,
                                    "interval": 1, "message": "go"})
        if path != "/consumers/oauth2/v2.0/token":
            return self.reply(404, {"error": "not_found"})
        with lock:
            g = f.get("grant_type")
            if g == "urn:ietf:params:oauth:grant-type:device_code":
                if f.get("device_code") == "DEV-expired":
                    return self.reply(400, {"error": "expired_token"})
                if f.get("device_code") != "DEV-123":
                    return self.reply(400, {"error": "invalid_grant"})
                state["polls"] += 1
                if state["polls"] < 2:
                    return self.reply(400, {"error": "authorization_pending"}, chunked=True)
            elif g == "refresh_token":
                if f.get("refresh_token") not in state["refresh"]:
                    return self.reply(400, {"error": "invalid_grant", "error_description": "AADSTS70000: expired"})
                state["refresh"].discard(f["refresh_token"])   # rotate: each refresh token works once
            else:
                return self.reply(400, {"error": "unsupported_grant_type"})
            state["n"] += 1
            rt = "RT-%d" % state["n"]
            state["refresh"].add(rt)
            self.reply(200, {"token_type": "Bearer", "expires_in": 3600, "access_token": "AT-ok-%d" % state["n"],
                             "refresh_token": rt, "id_token": jwt({"preferred_username": EMAIL, "name": "Test"})},
                       chunked=(state["n"] % 2 == 0))


ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
