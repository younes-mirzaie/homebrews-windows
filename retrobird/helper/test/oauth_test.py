"""Microsoft sign-in (device code flow, refresh-token rotation, XOAUTH2 for IMAP and SMTP)
against fake_oauth.py, Dovecot's oauth2 passdb and fake_smtp.py. Started by run-tests.sh.
usage: python3 -I oauth_test.py path/to/rbmaild OAUTH_PORT"""
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time

RBMAILD, OPORT = sys.argv[1], int(sys.argv[2])
PORT, TOKEN, CLIENT = 18100, "oauth-token-0123456789", "test-client-id"
passed = failed = 0


def check(cond, what):
    global passed, failed
    passed += bool(cond)
    failed += not cond
    if not cond:
        print("FAIL:", what)


def esc(s):
    return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")


class Client:
    def __init__(self):
        self.s = socket.create_connection(("127.0.0.1", PORT), timeout=60)
        self.buf = b""
        self.cmd("AUTH", TOKEN)

    def line(self):
        while b"\n" not in self.buf:
            d = self.s.recv(65536)
            if not d:
                return None
            self.buf += d
        l, self.buf = self.buf.split(b"\n", 1)
        return l.decode()

    def cmd(self, *f):
        self.s.sendall(("\t".join(esc(str(x)) for x in f) + "\n").encode())
        return self.line().split("\t")

    def rows(self):
        out = []
        while True:
            l = self.line()
            if not l:
                return out
            out.append(l.split("\t"))


def start(data):
    env = dict(os.environ, RB_OAUTH_SERVER="127.0.0.1:%d" % OPORT)
    p = subprocess.Popen([RBMAILD, "--token", TOKEN, "--port", str(PORT), "--data", data, "--sync-minutes", "0",
                          "--debug"], env=env, stderr=subprocess.DEVNULL)
    for _ in range(50):
        try:
            socket.create_connection(("127.0.0.1", PORT), timeout=1).close()
            return p
        except OSError:
            time.sleep(0.1)
    raise SystemExit("rbmaild didn't start")


def fields(**over):
    f = dict(name="Outlook", email="test@localhost", imap_host="localhost", imap_port="10993", imap_sec="tls",
             smtp_host="localhost", smtp_port="10465", smtp_sec="tls", user="test@localhost", auth="microsoft",
             oauth_client=CLIENT)
    f.update(over)
    return ["%s=%s" % (k.rstrip("_"), v) for k, v in f.items()]


data = tempfile.mkdtemp(prefix="rboauth.")
p = start(data)
try:
    c = Client()
    r = c.cmd("OAUTHSTART", "wrong-client")
    check(r[:2] == ["ERR", "OAUTH"] and "recognize" in r[2], "unknown app ID explained: %r" % r)
    r = c.cmd("OAUTHSTART", CLIENT)
    check(r[0] == "OK" and r[1] == "ABCD-EFGH" and r[2] == "https://microsoft.com/link" and r[3] == "DEV-123"
          and r[4] == "1", "device code: %r" % r)
    r = c.cmd("OAUTHPOLL", CLIENT, "DEV-expired")
    check(r[:2] == ["ERR", "OAUTH"] and "expired" in r[2], "expired code: %r" % r)
    r = c.cmd("OAUTHPOLL", CLIENT, "DEV-123")
    check(r == ["OK", "wait"], "pending (chunked reply): %r" % r)
    r = c.cmd("OAUTHPOLL", CLIENT, "DEV-123")
    check(r[:2] == ["OK", "done"] and r[2] == "RT-1" and r[3] == "test@localhost", "signed in: %r" % r)
    refresh = r[2]

    r = c.cmd("SAVE", "", *fields(pass_=refresh))
    check(r[:2] == ["ERR", "CERT"], "certificate first: %r" % r)
    fp = r[3]
    r = c.cmd("SAVE", "", *fields(pass_="RT-bogus", imap_pin=fp, smtp_pin=fp))
    check(r[:2] == ["ERR", "AUTH"] and "sign in again" in r[2], "dead refresh token: %r" % r)
    r = c.cmd("SAVE", "", *fields(pass_=refresh, imap_pin=fp, smtp_pin=fp))
    check(r[0] == "OK", "account saved with XOAUTH2 on IMAP and SMTP: %r" % r)
    aid = r[1]
    r = c.cmd("GET", aid)
    kv = dict(x[:2] for x in c.rows())
    check(kv.get("auth") == "microsoft" and kv.get("oauth_client") == CLIENT and kv.get("has_pass") == "1",
          "GET: %r" % kv)
    r = c.cmd("SYNC", aid, "INBOX")
    check(r[0] == "OK", "sync over XOAUTH2: %r" % r)
    c.s.sendall(("SEND\t%s\nto\tsomeone@example.org\nsubject\tvia oauth\nbody\thello\n\n" % aid).encode())
    r = c.line().split("\t")
    check(r[0] == "OK", "send over XOAUTH2: %r" % r)
    c.s.close()
    conf = open(os.path.join(data, "accounts.conf"), encoding="utf-8").read()
    check("auth=microsoft" in conf and "oauth_client=" + CLIENT in conf and "RT-" not in conf,
          "accounts.conf keeps the mode and only a sealed token")
    p.terminate()
    p.wait()
    # a fresh start must use the rotated refresh token (RT-1 is spent by now)
    p = start(data)
    c = Client()
    r = c.cmd("SYNC", aid, "INBOX")
    check(r[0] == "OK", "after restart, the rotated refresh token works: %r" % r)
    log = open(os.path.join(data, "rbmaild.log"), encoding="utf-8", errors="replace").read()
    check("AT-ok" not in log and "RT-" not in log, "tokens never logged")
finally:
    p.terminate()
    p.wait()
    if failed:
        try:
            print(open(os.path.join(data, "rbmaild.log"), encoding="utf-8", errors="replace").read()[-4000:])
        except OSError:
            pass
    shutil.rmtree(data, ignore_errors=True)
print("oauth: %d passed, %d failed" % (passed, failed))
sys.exit(1 if failed else 0)
