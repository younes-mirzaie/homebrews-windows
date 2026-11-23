"""Protocol tests for rbmaild against a real IMAP server (Dovecot) and a fake SMTP server.
Started by run-tests.sh, which sets RB_TEST_DIR (server certs, maildir, outbox).

usage: python3 -I test_rbmaild.py path/to/rbmaild"""
import base64
import glob
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time

RBMAILD = sys.argv[1]
TEST = os.environ["RB_TEST_DIR"]
MAILUSER = os.environ.get("RB_MAIL_USER", "vmail")
MAILDIR = os.path.join(TEST, "mail", "test@localhost")
OUTBOX = os.path.join(TEST, "outbox")
PORT = 18096
TOKEN = "testtoken-0123456789"
PASSWORD = "s3cret p\u00e4ss"
passed = failed = 0


def check(cond, what):
    global passed, failed
    if cond:
        passed += 1
    else:
        failed += 1
        print("FAIL:", what)


def esc(s):
    return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")


def unesc(s):
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == "\\" and i + 1 < len(s):
            i += 1
            c = {"t": "\t", "n": "\n", "r": "\r"}.get(s[i], s[i])
        out.append(c)
        i += 1
    return "".join(out)


class Client:
    def __init__(self, token=TOKEN):
        self.s = socket.create_connection(("127.0.0.1", PORT), timeout=60)
        self.buf = b""
        self.hello = self.cmd("AUTH", token)

    def line(self):
        while b"\n" not in self.buf:
            d = self.s.recv(65536)
            if not d:
                return None
            self.buf += d
        l, self.buf = self.buf.split(b"\n", 1)
        return l.decode("utf-8")

    def send(self, *fields):
        self.s.sendall(("\t".join(esc(str(f)) for f in fields) + "\n").encode("utf-8"))

    def cmd(self, *fields):
        self.send(*fields)
        l = self.line()
        return [unesc(x) for x in l.split("\t")] if l is not None else None

    def rows(self):
        out = []
        while True:
            l = self.line()
            if not l:
                return out
            out.append([unesc(x) for x in l.split("\t")])

    def cmd_rows(self, *fields):
        head = self.cmd(*fields)
        return head, (self.rows() if head and head[0] == "OK" else [])

    def close(self):
        self.s.close()


started = []


def port_free():
    try:
        socket.create_connection(("127.0.0.1", PORT), timeout=1).close()
        return False
    except OSError:
        return True


# --debug logs every IMAP/SMTP line (the password check below needs that); a file, not a
# pipe, so a full pipe can't stall the helper
errlog = tempfile.TemporaryFile()


def start(data, token=TOKEN):
    for _ in range(50):          # the previous helper may still be shutting down
        if port_free():
            break
        time.sleep(0.1)
    p = subprocess.Popen([RBMAILD, "--token", token, "--port", str(PORT), "--data", data,
                          "--sync-minutes", "0", "--debug"], stderr=errlog)
    for _ in range(50):
        try:
            socket.create_connection(("127.0.0.1", PORT), timeout=1).close()
            started.append(p)
            return p
        except OSError:
            time.sleep(0.1)
    errlog.seek(0)
    raise SystemExit("rbmaild didn't start: " + errlog.read().decode(errors="replace")[-2000:])
    


def deliver(raw, name=None):
    """Drops a message straight into the test user's INBOX (maildir/new)."""
    name = name or "%d.%d.rbtest" % (time.time() * 1000000, os.getpid())
    path = os.path.join(MAILDIR, "new", name)
    with open(path, "wb") as f:
        f.write(raw if isinstance(raw, bytes) else raw.encode("utf-8"))
    shutil.chown(path, MAILUSER, MAILUSER)
    return name


def simple(i, subject=None, frm="Sender %d <s%d@example.org>"):
    return ("From: %s\r\nTo: test@localhost\r\nSubject: %s\r\nDate: Thu, 05 Nov 2026 10:%02d:00 +0000\r\n"
            "Message-ID: <m%d@example.org>\r\n\r\nBody of message %d\r\n") % (
        frm % (i, i) if "%d" in frm else frm, subject or "Message %d" % i, i % 60, i, i)


MULTI = (
    "From: =?UTF-8?Q?J=C3=BCrgen_M=C3=BCller?= <jm@example.org>\r\n"
    "To: Test <test@localhost>\r\nCc: other@example.org\r\n"
    "Subject: =?UTF-8?B?UXVhcnRhbHNiZXJpY2h0IOKCrA==?=\r\n"
    "Date: Thu, 05 Nov 2026 11:59:00 +0100\r\nMessage-ID: <multi@example.org>\r\n"
    "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n"
    "--b1\r\nContent-Type: multipart/related; boundary=\"b2\"\r\n\r\n"
    "--b2\r\nContent-Type: text/html; charset=windows-1252\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n"
    "<html><body><p>Gr=FC=DFe, see <img src=3D\"cid:chart@x\"> and <img src=3D\"https://t.example/o.gif\">"
    "<script>alert(1)</script></p></body></html>\r\n"
    "--b2\r\nContent-Type: image/png\r\nContent-ID: <chart@x>\r\nContent-Transfer-Encoding: base64\r\n\r\n"
    "iVBORw0KGgoAAAANSUhEUg==\r\n--b2--\r\n"
    "--b1\r\nContent-Type: application/octet-stream\r\n"
    "Content-Disposition: attachment; filename*=UTF-8''Bericht%20%E2%82%AC.csv\r\n"
    "Content-Transfer-Encoding: base64\r\n\r\n" + base64.b64encode(b"a;b\r\n1;2\r\n").decode() + "\r\n"
    "--b1--\r\n")


def main():
    data = tempfile.mkdtemp(prefix="rbdata.")
    attdir = tempfile.mkdtemp(prefix="rbatt.")
    for i in range(1, 151):
        deliver(simple(i), "%010d.m%d.rbtest" % (i, i))
    deliver(MULTI, "%010d.multi.rbtest" % 200)
    p = start(data)
    try:
        run(data, attdir)
    finally:
        try:
            c = Client()
            c.cmd("QUIT")
        except OSError:
            pass
        for sp in started:
            try:
                sp.wait(timeout=5)
            except subprocess.TimeoutExpired:
                sp.kill()
                check(False, "helper didn't exit after QUIT")
        log = open(os.path.join(data, "rbmaild.log"), encoding="utf-8", errors="replace").read()
        check(PASSWORD not in log and base64.b64encode(("\0test@localhost\0" + PASSWORD).encode()).decode() not in log and
              base64.b64encode(PASSWORD.encode()).decode() not in log,
              "password must not appear in the log")
        shutil.rmtree(attdir, ignore_errors=True)
        if failed:
            print("--- rbmaild.log ---\n" + log[-4000:])
        shutil.rmtree(data, ignore_errors=True)
    print("%d passed, %d failed" % (passed, failed))
    sys.exit(1 if failed else 0)


def account_fields(**over):
    f = dict(name="Test User", email="test@localhost", imap_host="localhost", imap_port="10993",
             imap_sec="tls", smtp_host="localhost", smtp_port="10465", smtp_sec="tls",
             user="test@localhost", pass_=PASSWORD, save_sent="1")
    f.update(over)
    return ["%s=%s" % (k.rstrip("_"), v) for k, v in f.items()]


def run(data, attdir):
    # --- handshake
    bad = Client("wrong-token-000")
    check(bad.hello[:2] == ["ERR", "AUTH"], "wrong key refused: %r" % bad.hello)
    check(bad.line() is None, "connection closed after a wrong key")
    c = Client()
    check(c.hello[0] == "OK" and c.hello[1] == "rbmaild", "hello: %r" % c.hello)
    check(c.cmd("PING")[0] == "OK", "ping")
    check(c.cmd("NOPE")[:2] == ["ERR", "ARG"], "unknown command")

    # --- adding an account: certificate, then password checks
    r = c.cmd("SAVE", "", *account_fields())
    check(r[:2] == ["ERR", "CERT"] and len(r) >= 5 and len(r[3]) == 64 and r[4] == "imap",
          "self-signed IMAP certificate reported with fingerprint: %r" % r)
    fp = r[3] if len(r) > 3 else ""
    r = c.cmd("SAVE", "", *account_fields(imap_pin=fp, pass_="wrong"))
    check(r[:2] == ["ERR", "AUTH"], "wrong password: %r" % r)
    r = c.cmd("SAVE", "", *account_fields(imap_pin=fp))
    check(r[:2] == ["ERR", "CERT"] and r[-1] == "smtp", "SMTP certificate reported separately: %r" % r)
    r = c.cmd("SAVE", "", *account_fields(imap_pin=fp, smtp_pin=fp, imap_port="10994"))
    check(r[:2] == ["ERR", "NET"], "closed port is a network error: %r" % r)
    r = c.cmd("SAVE", "", *account_fields(imap_pin=fp, smtp_pin=fp))
    check(r[0] == "OK" and len(r[1]) == 12, "account saved: %r" % r)
    aid = r[1]
    head, rows = c.cmd_rows("ACCOUNTS")
    check(len(rows) == 1 and rows[0][1] == aid and rows[0][3] == "test@localhost" and rows[0][7] == "1",
          "accounts list: %r" % rows)
    head, rows = c.cmd_rows("GET", aid)
    kv = {r[0]: r[1] for r in rows}
    check(kv.get("imap_pin") == fp and kv.get("has_pass") == "1" and "pass" not in kv, "GET: %r" % kv)
    conf = open(os.path.join(data, "accounts.conf"), encoding="utf-8").read()
    check(PASSWORD not in conf and "pass_sealed=" in conf, "password sealed on disk")

    # --- folders
    head, rows = c.cmd_rows("FOLDERS", aid)
    check(head[:2] == ["OK", "online"], "folders online: %r" % head)
    by_name = {r[1]: r for r in rows}
    check(rows and rows[0][1] == "INBOX" and rows[0][3] == "inbox", "INBOX first: %r" % rows[:2])
    check(by_name.get("Sent", [None] * 4)[3] == "sent" and by_name.get("Trash", [None] * 4)[3] == "trash",
          "special-use roles: %r" % rows)
    check(by_name.get("INBOX", [0] * 6)[5] == "151" and by_name["INBOX"][4] == "151", "INBOX counts: %r" % by_name.get("INBOX"))
    names = [r[2] for r in rows]
    check("Entw\u00fcrfe alt" in names, "modified UTF-7 folder name decoded: %r" % names)

    # --- sync and paging
    r = c.cmd("SYNC", aid, "INBOX")
    check(r == ["OK", "100", "151"], "first sync fetches the newest 100, all 151 unread: %r" % r)
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 20)
    check(head[:3] == ["OK", "100", "151"] and len(rows) == 20, "list head: %r" % head)
    newest = rows[0]
    check(newest[6].startswith("J\u00fcrgen M\u00fcller <jm@example.org>") and newest[8] == "Quartalsbericht \u20ac"
          and newest[5] == "1", "newest is the multipart one, decoded, with attachment: %r" % newest)
    check(rows[1][8] == "Message 150" and rows[1][2] == "", "second newest unread: %r" % rows[1])
    check(len(rows[1]) == 10 and rows[1][9] == "Body of message 150", "preview line: %r" % rows[1])
    check(newest[9] != "" and "\t" not in newest[9], "multipart preview: %r" % newest[9])
    r = c.cmd("MORE", aid, "INBOX", 30)
    check(r[:2] == ["OK", "30"], "more fetches 30 older: %r" % r)
    r = c.cmd("MORE", aid, "INBOX", 100)
    check(r[:2] == ["OK", "21"], "more stops at the oldest: %r" % r)
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 140, 50)
    check(head[1] == "151" and len(rows) == 11 and rows[-1][8] == "Message 1", "list tail: %r" % (rows[-1:],))
    r = c.cmd("SYNC", aid, "INBOX")
    check(r[:2] == ["OK", "0"], "nothing new: %r" % r)

    # --- an index from before previews (nine fields) loads, and a sync fills in the newest 50
    idx = [os.path.join(d, "index") for d, _, f in os.walk(data) if "index" in f and os.path.basename(d).lower() == "494e424f58"]
    check(len(idx) == 1, "one INBOX index: %r" % idx)
    with open(idx[0], encoding="utf-8") as f:
        lines = f.read().split("\n")
    old = [lines[0]] + ["\t".join(l.split("\t")[:9]) if l else l for l in lines[1:]]
    with open(idx[0], "w", encoding="utf-8") as f:
        f.write("\n".join(old))
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 60)
    check(len(rows) == 60 and all(r[9] == "" for r in rows), "old index lists with empty previews")
    r = c.cmd("SYNC", aid, "INBOX")
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 60)
    check(rows[1][9] == "Body of message 150" and rows[49][9] != "" and rows[50][9] == "",
          "sync backfills the newest 50 previews: %r" % [r[9] for r in rows[48:52]])

    # --- reading a message
    uid = newest[1]
    head, rows = c.cmd_rows("MSG", aid, "INBOX", uid)
    kv = {}
    atts = []
    for r in rows:
        if r[0] == "att":
            atts.append(r)
        else:
            kv[r[0]] = r[1]
    check(head[0] == "OK", "msg ok: %r" % head)
    check(kv.get("from") == "J\u00fcrgen M\u00fcller <jm@example.org>" and kv.get("cc") == "other@example.org",
          "msg headers: %r" % kv)
    check(kv.get("subject") == "Quartalsbericht \u20ac" and kv.get("msgid") == "<multi@example.org>", "subject/msgid")
    check(kv.get("date") == str(1793876340), "date: %r" % kv.get("date"))
    html = kv.get("html", "")
    check("Gr\u00fc\u00dfe" in html and "<script" not in html and "alert" not in html, "html cleaned: %r" % html[:300])
    check("data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==" in html, "inline image embedded")
    check("t.example" not in html and kv.get("blocked") == "1", "remote image blocked")
    check(kv.get("text", "").startswith("Gr\u00fc\u00dfe, see"), "text version: %r" % kv.get("text"))
    check(len(atts) == 2, "two parts listed: %r" % atts)
    csv = [a for a in atts if a[2] == "Bericht \u20ac.csv"]
    inline = [a for a in atts if a[5] == "1"]
    check(len(csv) == 1 and len(inline) == 1, "attachment names / inline flag: %r" % atts)
    head, rows = c.cmd_rows("MSG", aid, "INBOX", uid, 1)
    kv2 = {r[0]: r[1] for r in rows if r[0] != "att"}
    check("t.example" in kv2.get("html", "") and kv2.get("blocked") == "0", "images on request")
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 1)
    check("S" in rows[0][2], "opened message marked read: %r" % rows[0])
    if csv:
        r = c.cmd("SAVEATT", aid, "INBOX", uid, csv[0][1], attdir)
        check(r[0] == "OK" and open(r[1], "rb").read() == b"a;b\r\n1;2\r\n", "attachment saved: %r" % r)
        r2 = c.cmd("SAVEATT", aid, "INBOX", uid, csv[0][1], attdir)
        check(r2[0] == "OK" and r2[1].endswith("Bericht \u20ac (2).csv"), "no overwrite: %r" % r2)

    # --- flags
    r = c.cmd("FLAG", aid, "INBOX", rows[0][1], "+F")
    check(r == ["OK"], "flag: %r" % r)
    r = c.cmd("FLAG", aid, "INBOX", rows[0][1], "-S")
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 1)
    check("F" in rows[0][2] and "S" not in rows[0][2], "flags in cache: %r" % rows[0])
    r = c.cmd("SYNC", aid, "INBOX")
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 1)
    check("F" in rows[0][2] and "S" not in rows[0][2], "flags survive a sync (server agrees): %r" % rows[0])

    # --- sending
    c.send("SEND", aid)
    for k, v in [("to", "Ann Example <ann@example.com>"), ("cc", "\"Doe, John\" <john@example.com>"),
                 ("bcc", "hidden@example.com"), ("subject", "Re: Quartalsbericht \u20ac"),
                 ("body", "Danke!\n\n> quoted\n.\nEnde \u00e4\u00f6\u00fc"), ("in_reply_to", "<multi@example.org>"),
                 ("references", "<multi@example.org>"), ("reply_folder", "INBOX"), ("reply_uid", uid)]:
        c.send(k, v)
    c.send("")
    r = [unesc(x) for x in c.line().split("\t")]
    check(r[0] == "OK", "send: %r" % r)
    sent = sorted(glob.glob(os.path.join(OUTBOX, "*.eml")))
    check(len(sent) == 1, "one message at the SMTP server")
    if sent:
        m = open(sent[0], encoding="utf-8", newline="").read()
        check("X-Env-To: ann@example.com,john@example.com,hidden@example.com" in m, "envelope recipients incl. bcc")
        check("hidden@example.com" not in m.split("\r\n\r\n", 1)[1] and "Bcc:" not in m, "bcc not in headers")
        check("In-Reply-To: <multi@example.org>" in m and "=?UTF-8?B?" in m and "Ende =C3=A4=C3=B6=C3=BC" in m,
              "built message: %s" % m[:600])
        check("\r\n..\r\n" not in m and "\r\n.\r\n" not in m, "dot line encoded")
    r = c.cmd("SYNC", aid, "Sent")
    check(r == ["OK", "1", "0"], "copy saved in Sent (read): %r" % r)
    head, rows = c.cmd_rows("LIST", aid, "Sent", 0, 5)
    check(rows and rows[0][8] == "Re: Quartalsbericht \u20ac" and "S" in rows[0][2] and
          rows[0][7].startswith("Ann Example <ann@example.com>"), "Sent entry: %r" % rows)
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 1)
    check("A" in rows[0][2], "original marked answered: %r" % rows[0])
    c.send("SEND", aid)
    c.send("to", "nobody@refuse.test")
    c.send("subject", "x")
    c.send("")
    r = [unesc(x) for x in c.line().split("\t")]
    check(r[0] == "ERR" and "nobody@refuse.test" in r[2], "refused recipient reported: %r" % r)
    c.send("SEND", aid)
    c.send("subject", "no one")
    c.send("")
    r = [unesc(x) for x in c.line().split("\t")]
    check(r[:2] == ["ERR", "ARG"], "no recipients: %r" % r)

    # --- delete (to Trash), move, external changes
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 5, 1)
    victim = rows[0]
    r = c.cmd("DELETE", aid, "INBOX", victim[1])
    check(r == ["OK", "trash"], "delete moves to Trash: %r" % r)
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 500)
    check(head[1] == "150" and victim[1] not in [x[1] for x in rows], "gone from the INBOX cache")
    check(c.cmd("SYNC", aid, "Trash")[:2] == ["OK", "1"], "arrived in Trash")
    head, rows = c.cmd_rows("LIST", aid, "Trash", 0, 5)
    r = c.cmd("DELETE", aid, "Trash", rows[0][1])
    check(r == ["OK", "expunged"], "delete in Trash expunges: %r" % r)
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 7, 1)
    r = c.cmd("MOVE", aid, "INBOX", rows[0][1], "Entw&APw-rfe alt")
    check(r == ["OK", "moved"], "move: %r" % r)
    check(c.cmd("SYNC", aid, "Entw&APw-rfe alt")[:2] == ["OK", "1"], "moved message arrived")

    deliver(simple(500, subject="Fresh one"))
    r = c.cmd("SYNC", aid, "INBOX")
    check(r[:2] == ["OK", "1"], "new mail picked up: %r" % r)
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 1)
    check(rows[0][8] == "Fresh one", "new mail on top: %r" % rows[0])
    gone = sorted(glob.glob(os.path.join(MAILDIR, "cur", "*m3.rbtest*")))
    for g in gone:
        os.remove(g)
    r = c.cmd("SYNC", aid, "INBOX")
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 500)
    check(gone and "Message 3" not in [x[8] for x in rows], "deleted elsewhere drops out of the cache")

    # --- editing the account keeps the password; restart keeps everything
    r = c.cmd("SAVE", aid, "name=Renamed", "pass=", "check=0")
    check(r == ["OK", aid], "edit: %r" % r)
    check(c.cmd("SYNC", aid, "INBOX")[0] == "OK", "still logs in after edit")
    head, rows = c.cmd_rows("ACCOUNTS")
    check(rows[0][2] == "Renamed", "renamed")
    c.cmd("QUIT")
    c.close()
    p = start(data)
    c = Client()
    head, rows = c.cmd_rows("ACCOUNTS")
    check(len(rows) == 1 and rows[0][7] == "1", "account and sealed password survive a restart: %r" % rows)
    head, rows = c.cmd_rows("LIST", aid, "INBOX", 0, 1)
    check(rows and rows[0][8] == "Fresh one", "cache works offline after restart")
    check(c.cmd("SYNC", aid, "INBOX")[0] == "OK", "sync after restart")
    c.cmd("QUIT")
    c.close()
    p.wait(timeout=10)
    p = start(data, token="another-token-xyz")
    c = Client("another-token-xyz")
    head, rows = c.cmd_rows("ACCOUNTS")
    check(rows and rows[0][7] == "0", "a different app key can't open the password")
    r = c.cmd("SYNC", aid, "INBOX")
    check(r[:2] == ["ERR", "AUTH"], "and asks for it again: %r" % r)
    c.cmd("QUIT")
    c.close()
    p.wait(timeout=10)
    p = start(data)
    c = Client()

    # --- STARTTLS account on the plain ports, offline folders, removal
    r = c.cmd("SAVE", "", *account_fields(imap_port="10143", imap_sec="starttls", smtp_port="10587",
                                          smtp_sec="starttls", imap_pin=fp, smtp_pin=fp, name="Second"))
    check(r[0] == "OK", "STARTTLS account: %r" % r)
    aid2 = r[1] if r[0] == "OK" else ""
    check(c.cmd("SYNC", aid2, "INBOX")[0] == "OK", "STARTTLS sync")
    head, rows = c.cmd_rows("FOLDERS", aid2)
    check(head[:2] == ["OK", "online"], "STARTTLS folders")
    # Dovecot trusts loopback for plain IMAP logins; the fake SMTP server never does
    r = c.cmd("SAVE", aid2, "imap_sec=plain", "smtp_sec=plain")
    check(r[0] == "ERR" and "SMTP" in r[2], "plain SMTP login refused by the server: %r" % r)
    r = c.cmd("SAVE", aid2, "imap_port=10995", "check=0")
    head, rows = c.cmd_rows("FOLDERS", aid2)
    check(head[:2] == ["OK", "offline"] and len(rows) >= 4, "folders from cache when offline: %r" % head)
    check(c.cmd("DELACCT", aid2) == ["OK"], "delete account")
    check(not os.path.exists(os.path.join(data, "mail", aid2)), "its cache is removed")
    head, rows = c.cmd_rows("ACCOUNTS")
    check(len(rows) == 1, "one account left")

    # --- concurrency: two app connections at once
    c2 = Client()
    c.send("SYNC", aid, "INBOX")
    c2.send("LIST", aid, "INBOX", 0, 3)
    r1 = c.line()
    r2 = c2.line()
    rest = c2.rows()
    check(r1.startswith("OK") and r2.startswith("OK") and len(rest) == 3, "parallel requests")
    c2.close()
    check(c.cmd("SETTINGS", "sync_minutes", "1") == ["OK", "5"], "sync interval clamps to 5 minutes")
    c.cmd("QUIT")
    c.close()
    p.wait(timeout=10)


if __name__ == "__main__":
    main()
