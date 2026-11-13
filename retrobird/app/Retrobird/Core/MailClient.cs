// Client for rbmaild (../../../PROTOCOL.md): one authenticated loopback connection, one request
// at a time. Fields are TAB-separated with \\ \t \n \r escaped.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Retrobird.Core
{
    public sealed class MailClient
    {
        public const string Host = "127.0.0.1";
        public const int DefaultPort = 8097;

        readonly string _token;
        int _port;
        readonly SemaphoreSlim _lock = new SemaphoreSlim(1);
        LineSocket _s;

        public MailClient(string token, int port = DefaultPort) { _token = token; _port = port; }

        /// <summary>Changing it drops the current connection (the in-app helper and the separate
        /// one listen on different ports).</summary>
        public int Port
        {
            get { return _port; }
            set { if (value != _port) { _port = value; Close(); } }
        }
        public string ServerVersion { get; private set; }

        /// <summary>Called when nothing answers on the port: should start the helper and return
        /// null, or return a message for the user.</summary>
        public Func<Task<string>> StartHelper;

        // ---------------- framing ----------------

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var b = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': b.Append("\\\\"); break;
                    case '\t': b.Append("\\t"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\0': b.Append(' '); break;
                    default: b.Append(c); break;
                }
            }
            return b.ToString();
        }

        public static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var b = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    c = s[++i];
                    b.Append(c == 't' ? '\t' : c == 'n' ? '\n' : c == 'r' ? '\r' : c);
                }
                else b.Append(c);
            }
            return b.ToString();
        }

        static string[] Fields(string line)
        {
            var f = line.Split('\t');
            for (int i = 0; i < f.Length; i++) f[i] = Unescape(f[i]);
            return f;
        }

        static string Line(params object[] fields)
        {
            var b = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) b.Append('\t');
                b.Append(Escape(Convert.ToString(fields[i], System.Globalization.CultureInfo.InvariantCulture)));
            }
            b.Append('\n');
            return b.ToString();
        }

        static string F(string[] f, int i) { return i < f.Length ? f[i] : ""; }
        static int I(string[] f, int i) { int v; int.TryParse(F(f, i), out v); return v; }

        static string[] Check(string line)
        {
            if (line == null) throw new MailException("NET", "connection to the mail helper was lost");
            var f = Fields(line);
            if (f[0] == "OK") return f;
            if (f[0] == "ERR")
            {
                var e = new MailException(F(f, 1), F(f, 2).Length > 0 ? F(f, 2) : "the mail engine couldn't finish that");
                if (e.Code == "CERT") { e.Fingerprint = F(f, 3); e.Side = F(f, 4); }
                throw e;
            }
            throw new MailException("NET", "unexpected reply from the mail helper: " + line);
        }

        // ---------------- connection ----------------

        /// <summary>Connects and authenticates. Returns false if nothing listens.</summary>
        async Task<bool> OpenAsync()
        {
            if (_s != null && !_s.IsClosed) return true;
            if (_s != null) _s.Dispose();
            _s = await LineSocket.ConnectAsync(Host, _port, 1500);
            if (_s == null) return false;
            await _s.SendAsync(Line("AUTH", _token));
            var r = await _s.ReadLineAsync();
            if (r == null || !r.StartsWith("OK\t"))
            {
                _s.Dispose();
                _s = null;
                throw new MailException("AUTH", "the running mail helper has a different key (restart the phone)");
            }
            var f = Fields(r);
            ServerVersion = F(f, 2);
            return true;
        }

        public async Task EnsureAsync()
        {
            if (await OpenAsync()) return;
            if (StartHelper == null) throw new MailException("NET", "the mail helper isn't running");
            var msg = await StartHelper();
            if (msg != null) throw new MailException("NET", msg);
            if (!await OpenAsync()) throw new MailException("NET", "the mail helper didn't answer");
        }

        /// <summary>Connects without starting anything (used by the launcher).</summary>
        public async Task<bool> TryConnectAsync()
        {
            await _lock.WaitAsync();
            try { return await OpenAsync(); }
            finally { _lock.Release(); }
        }

        public void Close()
        {
            if (_s != null) _s.Dispose();
            _s = null;
        }

        /// <summary>Runs one exchange under the lock; reconnects once if the helper went away.</summary>
        async Task<T> Run<T>(Func<LineSocket, Task<T>> exchange)
        {
            await _lock.WaitAsync();
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    await EnsureAsync();
                    try { return await exchange(_s); }
                    catch (MailException e)
                    {
                        if (attempt == 0 && _s != null && _s.IsClosed && e.Code == "NET") { Close(); continue; }
                        throw;
                    }
                }
            }
            finally { _lock.Release(); }
        }

        async Task<string[]> Simple(params object[] fields)
        {
            return await Run(async s =>
            {
                await s.SendAsync(Line(fields));
                return Check(await s.ReadLineAsync());
            });
        }

        static async Task<List<string[]>> Rows(LineSocket s)
        {
            var rows = new List<string[]>();
            for (;;)
            {
                var l = await s.ReadLineAsync();
                if (l == null) throw new MailException("NET", "connection to the mail helper was lost");
                if (l.Length == 0) return rows;
                rows.Add(Fields(l));
            }
        }

        async Task<Tuple<string[], List<string[]>>> WithRows(params object[] fields)
        {
            return await Run(async s =>
            {
                await s.SendAsync(Line(fields));
                var head = Check(await s.ReadLineAsync());
                return Tuple.Create(head, await Rows(s));
            });
        }

        // ---------------- commands ----------------

        public async Task<string> PingAsync()
        {
            var f = await Simple("PING");
            return F(f, 2);
        }

        public Task QuitAsync() { return Simple("QUIT"); }

        public async Task<List<AccountInfo>> GetAccountsAsync()
        {
            var r = await WithRows("ACCOUNTS");
            var list = new List<AccountInfo>();
            foreach (var f in r.Item2)
                list.Add(new AccountInfo
                {
                    Id = F(f, 1), Name = F(f, 2), Email = F(f, 3), Unseen = I(f, 4),
                    LastSync = Format.FromUnix(F(f, 5)), LastError = F(f, 6), HasPassword = F(f, 7) == "1"
                });
            return list;
        }

        public async Task<Dictionary<string, string>> GetAccountAsync(string id)
        {
            var r = await WithRows("GET", id);
            var d = new Dictionary<string, string>();
            foreach (var f in r.Item2) d[F(f, 0)] = F(f, 1);
            return d;
        }

        /// <summary>Creates (id null) or updates an account. Unless check is false the helper first
        /// logs in to both servers; a CERT error carries the fingerprint to pin.</summary>
        public async Task<string> SaveAccountAsync(string id, IDictionary<string, string> fields, bool check = true)
        {
            var args = new List<object> { "SAVE", id ?? "" };
            foreach (var kv in fields) args.Add(kv.Key + "=" + (kv.Value ?? ""));
            if (!check) args.Add("check=0");
            var f = await Simple(args.ToArray());
            return F(f, 1);
        }

        public Task DeleteAccountAsync(string id) { return Simple("DELACCT", id); }

        public async Task<FolderList> GetFoldersAsync(string id)
        {
            var r = await WithRows("FOLDERS", id);
            var fl = new FolderList { Online = F(r.Item1, 1) == "online", Error = F(r.Item1, 2) };
            foreach (var f in r.Item2)
                fl.Folders.Add(new FolderInfo
                {
                    Name = F(f, 1), Display = F(f, 2), Role = F(f, 3), Unseen = I(f, 4), Total = I(f, 5), NoSelect = F(f, 6) == "1"
                });
            return fl;
        }

        /// <summary>Fetches new headers. Returns how many arrived and the folder's unread count
        /// (-1 if the server didn't say).</summary>
        public async Task<SyncResult> SyncAsync(string id, string folder)
        {
            var f = await Simple("SYNC", id, folder);
            return new SyncResult { Added = I(f, 1), Unseen = f.Length > 2 ? I(f, 2) : -1 };
        }

        /// <summary>Fetches up to count older headers.</summary>
        public async Task<int> MoreAsync(string id, string folder, int count)
        {
            var f = await Simple("MORE", id, folder, count);
            return I(f, 1);
        }

        public async Task<MessageList> ListAsync(string id, string folder, int offset, int count)
        {
            var r = await WithRows("LIST", id, folder, offset, count);
            var ml = new MessageList { Cached = I(r.Item1, 1), Exists = I(r.Item1, 2) };
            foreach (var f in r.Item2)
            {
                uint uid;
                long size;
                uint.TryParse(F(f, 1), out uid);
                long.TryParse(F(f, 4), out size);
                ml.Items.Add(new MessageSummary
                {
                    Uid = uid, Flags = F(f, 2), Date = Format.FromUnix(F(f, 3)), Size = size,
                    HasAttachment = F(f, 5) == "1", From = F(f, 6), To = F(f, 7), Subject = F(f, 8)
                });
            }
            return ml;
        }

        public async Task<MessageView> GetMessageAsync(string id, string folder, uint uid, bool images)
        {
            var r = await WithRows("MSG", id, folder, uid, images ? 1 : 0);
            var v = new MessageView();
            foreach (var f in r.Item2)
            {
                var k = F(f, 0);
                var val = F(f, 1);
                switch (k)
                {
                    case "from": v.From = val; break;
                    case "to": v.To = val; break;
                    case "cc": v.Cc = val; break;
                    case "replyto": v.ReplyTo = val; break;
                    case "subject": v.Subject = val; break;
                    case "date": v.Date = Format.FromUnix(val); break;
                    case "msgid": v.MessageId = val; break;
                    case "references": v.References = val; break;
                    case "blocked": v.Blocked = val == "1"; break;
                    case "text": v.Text = val; break;
                    case "html": v.Html = val; break;
                    case "att":
                        long size;
                        long.TryParse(F(f, 4), out size);
                        v.Attachments.Add(new Attachment { Index = I(f, 1), Name = F(f, 2), ContentType = F(f, 3), Size = size, Inline = F(f, 5) == "1" });
                        break;
                }
            }
            return v;
        }

        /// <summary>Writes an attachment into dir (made if needed); returns the full path.</summary>
        public async Task<string> SaveAttachmentAsync(string id, string folder, uint uid, int index, string dir)
        {
            var f = await Simple("SAVEATT", id, folder, uid, index, dir);
            return F(f, 1);
        }

        /// <summary>flag: 'S' seen, 'F' flagged, 'A' answered.</summary>
        public Task SetFlagAsync(string id, string folder, uint uid, char flag, bool on)
        {
            return Simple("FLAG", id, folder, uid, (on ? "+" : "-") + flag);
        }

        /// <summary>Moves to Trash (or expunges in Trash). Returns "trash" or "expunged".</summary>
        public async Task<string> DeleteAsync(string id, string folder, uint uid)
        {
            var f = await Simple("DELETE", id, folder, uid);
            return F(f, 1);
        }

        public Task MoveAsync(string id, string folder, uint uid, string dest) { return Simple("MOVE", id, folder, uid, dest); }

        /// <summary>Sends; returns a warning (e.g. the Sent copy failed) or "".</summary>
        public async Task<string> SendAsync(string id, Draft d)
        {
            var f = await Run(async s =>
            {
                var b = new StringBuilder();
                b.Append(Line("SEND", id));
                b.Append(Line("to", d.To));
                b.Append(Line("cc", d.Cc));
                b.Append(Line("bcc", d.Bcc));
                b.Append(Line("subject", d.Subject));
                b.Append(Line("body", d.Body));
                if (!string.IsNullOrEmpty(d.InReplyTo)) b.Append(Line("in_reply_to", d.InReplyTo));
                if (!string.IsNullOrEmpty(d.References)) b.Append(Line("references", d.References));
                if (!string.IsNullOrEmpty(d.ReplyFolder) && d.ReplyUid != 0)
                {
                    b.Append(Line("reply_folder", d.ReplyFolder));
                    b.Append(Line("reply_uid", d.ReplyUid));
                }
                b.Append('\n');
                await s.SendAsync(b.ToString());
                return Check(await s.ReadLineAsync());
            });
            return F(f, 1);
        }

        /// <summary>Background check interval in minutes (0 = off). Returns what the helper uses.</summary>
        public async Task<int> SetSyncMinutesAsync(int minutes)
        {
            var f = await Simple("SETTINGS", "sync_minutes", minutes);
            return I(f, 1);
        }
    }
}
