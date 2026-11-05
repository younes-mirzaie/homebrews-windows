"""Protocol tests for rfxfsd. Runs against a Windows build (Wine on Linux, or the phone).
Usage: python3 test_rfxfsd.py HOST PORT TOKEN WORKDIR   (WORKDIR = a scratch folder as Windows path)"""
import socket, sys

host, port, token, work = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4].rstrip('\\')
passed = 0

class C:
    def __init__(self):
        self.s = socket.create_connection((host, port), timeout=20)
        self.f = self.s.makefile('rb')
    def send(self, *fields):
        self.s.sendall(('\t'.join(fields) + '\n').encode('utf-8'))
    def line(self):
        return self.f.readline().decode('utf-8').rstrip('\n')
    def reply(self, *fields):
        self.send(*fields)
        while True:
            l = self.line()
            if not l.startswith('P\t'):
                return l.split('\t')
    def rows(self, *fields):
        r = self.reply(*fields)
        assert r[0] == 'OK', r
        out = []
        while True:
            l = self.line()
            if l == '':
                return out
            out.append(l.split('\t'))

def check(name, cond, info=''):
    global passed
    if not cond:
        print('FAIL', name, info); sys.exit(1)
    passed += 1
    print('ok  ', name)

bad = C()
check('bad token refused', bad.reply('AUTH', 'nope')[0] == 'ERR')

c = C()
r = c.reply('AUTH', token)
check('auth', r[0] == 'OK' and r[1] == 'rfxfsd', r)
check('ping', c.reply('PING')[0] == 'OK')
drives = c.rows('DRIVES')
check('drives lists C:', any(d[0].upper() == 'C:\\' for d in drives), drives)

c.reply('MKDIR', work)   # WORKDIR may not exist yet (MKDIR doesn't create parents)
d = work + '\\t'
c.reply('DELETE', d)
check('mkdir', c.reply('MKDIR', d)[0] == 'OK')
check('mkdir unicode', c.reply('MKDIR', d + '\\sub ü 文件')[0] == 'OK')
data = bytes(range(256)) * 5000   # 1.28 MB, several chunks
c.send('WRITE', d + '\\sub ü 文件\\a.bin', str(len(data)))
c.s.sendall(data)
check('write', c.line() == 'OK')
r = c.reply('STAT', d + '\\sub ü 文件\\a.bin')
check('stat size', r[0] == 'OK' and int(r[2]) == len(data), r)
c.send('READ', d + '\\sub ü 文件\\a.bin', '1000', '300')
h = c.line().split('\t')
got = c.f.read(int(h[1]))
check('read range', h[0] == 'OK' and int(h[2]) == len(data) and got == data[1000:1300], h)
names = [x[0] for x in c.rows('LIST', d)]
check('list unicode name', names == ['sub ü 文件'], names)
check('copy tree', c.reply('COPY', d + '\\sub ü 文件', d + '\\copy')[0] == 'OK')
check('copy into itself refused', c.reply('COPY', d + '\\copy', d + '\\copy\\x')[0] == 'ERR')
check('copy no overwrite', c.reply('COPY', d + '\\sub ü 文件\\a.bin', d + '\\copy\\a.bin')[0] == 'ERR')
check('copy overwrite', c.reply('COPY', d + '\\sub ü 文件\\a.bin', d + '\\copy\\a.bin', '1')[0] == 'OK')
r = c.reply('DU', d)
check('du', r[0] == 'OK' and int(r[1]) == 2 * len(data) and int(r[2]) == 2, r)
check('attr readonly', c.reply('ATTR', d + '\\copy\\a.bin', '21')[0] == 'OK')
r = c.reply('STAT', d + '\\copy\\a.bin')
check('attr reads back', int(r[1], 16) & 1 == 1, r)
check('rename', c.reply('RENAME', d + '\\copy', d + '\\renamed')[0] == 'OK')
check('move', c.reply('MOVE', d + '\\renamed\\a.bin', d + '\\moved.bin')[0] == 'OK')
check('newfile', c.reply('NEWFILE', d + '\\empty.txt')[0] == 'OK')
check('newfile exists', c.reply('NEWFILE', d + '\\empty.txt')[0] == 'ERR')
check('delete readonly file', c.reply('DELETE', d + '\\moved.bin')[0] == 'OK')
check('delete root refused', c.reply('DELETE', 'C:\\')[0] == 'ERR')
check('rename C:\\Windows refused', c.reply('RENAME', 'C:\\Windows', 'C:\\Windows2')[0] == 'ERR')
r = c.reply('DELETE', d)
check('delete tree', r[0] == 'OK', r)
check('gone', c.reply('STAT', d)[0] == 'ERR')
check('list missing dir errors', c.reply('LIST', d)[0] == 'ERR')
check('unknown command', c.reply('FROB')[0] == 'ERR')
# cancel: a big copy, interrupted by one byte
c.reply('MKDIR', d)
big = bytes(1024 * 1024) * 1
c.send('WRITE', d + '\\big.bin', str(len(big))); c.s.sendall(big); c.line()
for i in range(40):
    c.reply('COPY', d + '\\big.bin', d + '\\b%d.bin' % i)
k = C(); k.reply('AUTH', token)
k.send('COPY', d, work + '\\t2')
k.s.sendall(b'X')
r = None
while True:
    l = k.line()
    if not l.startswith('P\t'):
        r = l.split('\t'); break
check('cancel stops a copy', r[0] == 'ERR' and r[1] == '1223', r)
c.reply('DELETE', work + '\\t2'); c.reply('DELETE', d)
# web access
import base64, json, urllib.request, urllib.error
wport = port + 2
r = c.reply('WEB', 'status')
check('web off by default', r[0] == 'OK' and r[1] == 'off', r)
r = c.reply('WEB', 'on', str(wport), 'sesame42')
check('web on', r[0] == 'OK' and r[1] == 'on' and r[2] == str(wport), r)
base = 'http://%s:%d' % (host, wport)
def web(path, method='GET', data=None, pw='sesame42', xrfx=True):
    rq = urllib.request.Request(base + path, data=data, method=method)
    if pw: rq.add_header('Authorization', 'Basic ' + base64.b64encode(b'u:' + pw.encode()).decode())
    if xrfx: rq.add_header('X-RFX', '1')
    try:
        with urllib.request.urlopen(rq, timeout=20) as resp: return resp.status, resp.read()
    except urllib.error.HTTPError as e: return e.code, e.read()
from urllib.parse import quote
st, _ = web('/', pw=None)
check('web needs password', st == 401)
st, _ = web('/', pw='wrong')
check('web rejects wrong password', st == 401)
st, body = web('/')
check('web page served', st == 200 and b'LUMIA FILES' in body)
wd = work + '\\w'
st, _ = web('/api/mkdir?p=' + quote(wd), 'POST', b'')
check('web mkdir', st == 200)
st, _ = web('/api/mkdir?p=' + quote(wd + '\\x'), 'POST', b'', xrfx=False)
check('web POST needs X-RFX', st == 403)
payload = bytes(range(256)) * 3000
st, _ = web('/api/upload?p=' + quote(wd) + '&n=' + quote('up ü.bin'), 'POST', payload)
check('web upload', st == 200)
st, body = web('/api/list?p=' + quote(wd))
lst = json.loads(body)
check('web list', st == 200 and lst[0]['n'] == 'up ü.bin' and lst[0]['s'] == len(payload), lst)
st, body = web('/dl?p=' + quote(wd + '\\up ü.bin'))
check('web download', st == 200 and body == payload)
st, _ = web('/api/copy?p=' + quote(wd + '\\up ü.bin') + '&to=' + quote(wd + '\\c.bin'), 'POST', b'')
check('web copy', st == 200)
st, _ = web('/api/rename?p=' + quote(wd + '\\c.bin') + '&to=' + quote(wd + '\\r.bin'), 'POST', b'')
check('web rename', st == 200)
st, _ = web('/api/delete?p=C%3A%5C', 'POST', b'')
check('web refuses deleting a drive', st == 403)
st, body = web('/api/drives')
check('web drives', st == 200 and len(json.loads(body)) > 0)
st, _ = web('/api/delete?p=' + quote(wd), 'POST', b'')
check('web delete tree', st == 200)
r = c.reply('WEB', 'off')
check('web off', r[0] == 'OK' and r[1] == 'off', r)
try:
    socket.create_connection((host, wport), timeout=3); off_ok = False
except OSError: off_ok = True
check('web port closed when off', off_ok)
print(passed, 'checks passed')
