"""A tiny SMTP submission server for tests: implicit TLS on one port, STARTTLS on another,
AUTH PLAIN/LOGIN against one user, and each accepted message saved to OUTBOX/<n>.eml with an
envelope header block (X-Env-From / X-Env-To) on top.

usage: python3 -I fake_smtp.py CERT KEY OUTBOX USER PASSWORD TLSPORT STARTTLSPORT"""
import base64
import os
import socket
import ssl
import sys
import threading

cert, key, outbox, user, password = sys.argv[1:6]
tls_port, starttls_port = int(sys.argv[6]), int(sys.argv[7])
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
ctx.load_cert_chain(cert, key)
counter = [0]
lock = threading.Lock()


class Session:
    def __init__(self, sock, tls):
        self.sock, self.tls, self.buf = sock, tls, b""
        self.authed, self.mail_from, self.rcpts = False, None, []

    def send(self, line):
        self.sock.sendall(line.encode() + b"\r\n")

    def readline(self):
        while b"\r\n" not in self.buf:
            d = self.sock.recv(65536)
            if not d:
                raise EOFError
            self.buf += d
        line, self.buf = self.buf.split(b"\r\n", 1)
        return line.decode("utf-8", "replace")

    def ehlo(self):
        caps = ["fake.test", "SIZE 10000000", "8BITMIME"]
        if self.tls:
            # the STARTTLS port offers only LOGIN so both client paths get exercised
            caps.append("AUTH PLAIN LOGIN" if self.sock.getsockname()[1] == tls_port else "AUTH LOGIN")
        else:
            caps.append("STARTTLS")
        for i, c in enumerate(caps):
            self.send(("250 " if i == len(caps) - 1 else "250-") + c)

    def check(self, u, p):
        if u == user and p == password:
            self.authed = True
            self.send("235 2.7.0 ok")
        else:
            self.send("535 5.7.8 bad credentials")

    def run(self):
        self.send("220 fake.test ESMTP")
        while True:
            line = self.readline()
            verb = line.split(" ", 1)[0].upper()
            arg = line[len(verb):].strip()
            if verb in ("EHLO", "HELO"):
                self.ehlo()
            elif verb == "STARTTLS" and not self.tls:
                self.send("220 go ahead")
                self.sock = ctx.wrap_socket(self.sock, server_side=True)
                self.tls, self.buf = True, b""
            elif verb == "AUTH" and self.tls:
                parts = arg.split()
                if parts[0].upper() == "PLAIN":
                    raw = base64.b64decode(parts[1]).split(b"\0")
                    self.check(raw[1].decode(), raw[2].decode())
                elif parts[0].upper() == "LOGIN":
                    self.send("334 VXNlcm5hbWU6")
                    u = base64.b64decode(self.readline()).decode()
                    self.send("334 UGFzc3dvcmQ6")
                    p = base64.b64decode(self.readline()).decode()
                    self.check(u, p)
                else:
                    self.send("504 unsupported")
            elif verb == "MAIL":
                if not self.authed:
                    self.send("530 5.7.0 authentication required")
                    continue
                self.mail_from, self.rcpts = arg.split(":", 1)[1].split()[0].strip("<>"), []
                self.send("250 ok")
            elif verb == "RCPT":
                addr = arg.split(":", 1)[1].strip().strip("<>")
                if addr.endswith("@refuse.test"):
                    self.send("550 5.1.1 no such user")
                else:
                    self.rcpts.append(addr)
                    self.send("250 ok")
            elif verb == "DATA":
                self.send("354 end with .")
                lines = []
                while True:
                    l = self.readline()
                    if l == ".":
                        break
                    lines.append(l[1:] if l.startswith(".") else l)
                with lock:
                    counter[0] += 1
                    n = counter[0]
                body = "X-Env-From: %s\r\nX-Env-To: %s\r\n" % (self.mail_from, ",".join(self.rcpts))
                body += "\r\n".join(lines) + "\r\n"
                with open(os.path.join(outbox, "%d.eml" % n), "w", encoding="utf-8", newline="") as f:
                    f.write(body)
                self.send("250 queued as %d" % n)
            elif verb == "QUIT":
                self.send("221 bye")
                return
            elif verb in ("RSET", "NOOP"):
                self.send("250 ok")
            else:
                self.send("502 unknown")


def serve(port, implicit):
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("127.0.0.1", port))
    s.listen(16)
    while True:
        c, _ = s.accept()
        if implicit:
            try:
                c = ctx.wrap_socket(c, server_side=True)
            except Exception:
                c.close()
                continue

        def go(c=c):
            try:
                Session(c, implicit).run()
            except Exception:
                pass
            finally:
                c.close()
        threading.Thread(target=go, daemon=True).start()


threading.Thread(target=serve, args=(starttls_port, False), daemon=True).start()
serve(tls_port, True)
