// Client for rfxfsd (daemon/PROTOCOL.md). One shared connection for quick commands;
// long operations (copy, move, delete, folder size) get their own connection so they can be
// cancelled without disturbing browsing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace RetroFiles.Core
{
    public class OpProgress
    {
        public ulong Bytes;
        public ulong Files;
        public string Current;
    }

    public class DriveInfo
    {
        public string Root, Label, FileSystem;
        public ulong Total, Free;
    }

    public class StatInfo
    {
        public FileAttr Attributes;
        public ulong Size;
        public DateTime Created, Accessed, Modified;
    }

    public sealed class RfxClient
    {
        public const string Host = "127.0.0.1";
        public const int Port = 8093;

        readonly string _token;
        readonly SemaphoreSlim _lock = new SemaphoreSlim(1);
        LineSocket _main;

        public RfxClient(string token) { _token = token; }

        public string ServerVersion { get; private set; }

        /// <summary>Opens and authenticates a connection. Returns null if nothing listens;
        /// throws if something answers but refuses the token.</summary>
        public async Task<LineSocket> OpenAsync()
        {
            var s = await LineSocket.ConnectAsync(Host, Port, 1500);
            if (s == null) return null;
            await s.SendAsync("AUTH\t" + _token + "\n");
            var r = await s.ReadLineAsync();
            if (r == null || !r.StartsWith("OK\t"))
            {
                s.Dispose();
                throw new RfxException(5, "the running helper has a different key");
            }
            var f = r.Split('\t');
            ServerVersion = f.Length > 2 ? f[2] : "?";
            return s;
        }

        public async Task<bool> ConnectAsync()
        {
            if (_main != null && !_main.IsClosed) return true;
            _main = await OpenAsync();
            return _main != null;
        }

        static string Req(params string[] fields) { return string.Join("\t", fields) + "\n"; }

        static string[] Check(string line)
        {
            if (line == null) throw new RfxException(0, "connection to the helper was lost");
            var f = line.Split('\t');
            if (f[0] == "OK") return f;
            if (f[0] == "ERR")
            {
                uint code;
                uint.TryParse(f.Length > 1 ? f[1] : "0", out code);
                throw new RfxException(code, f.Length > 2 ? f[2] : "error");
            }
            throw new RfxException(0, "unexpected reply: " + line);
        }

        async Task<T> OnMain<T>(Func<LineSocket, Task<T>> work)
        {
            await _lock.WaitAsync();
            try
            {
                if (_main == null || _main.IsClosed)
                {
                    _main = await OpenAsync();
                    if (_main == null) throw new RfxException(0, "the helper isn't running");
                }
                try { return await work(_main); }
                catch (RfxException e)
                {
                    if (_main.IsClosed) _main = null;
                    if (e.Code == 0 && _main == null) throw;
                    throw;
                }
            }
            finally { _lock.Release(); }
        }

        Task<string[]> Simple(params string[] fields)
        {
            return OnMain(async s => { await s.SendAsync(Req(fields)); return Check(await s.ReadLineAsync()); });
        }

        static async Task<List<string[]>> Rows(LineSocket s)
        {
            var rows = new List<string[]>();
            for (;;)
            {
                var l = await s.ReadLineAsync();
                if (l == null) throw new RfxException(0, "connection to the helper was lost");
                if (l.Length == 0) return rows;
                rows.Add(l.Split('\t'));
            }
        }

        static ulong U(string s) { ulong v; ulong.TryParse(s, out v); return v; }
        static FileAttr Hex(string s) { return (FileAttr)uint.Parse(s, NumberStyles.HexNumber); }
        static DateTime Ft(string s)
        {
            long v;
            if (!long.TryParse(s, out v) || v <= 0) return DateTime.MinValue;
            try { return DateTime.FromFileTimeUtc(v).ToLocalTime(); } catch { return DateTime.MinValue; }
        }

        public Task<List<FsItem>> ListAsync(string dir)
        {
            return OnMain(async s =>
            {
                await s.SendAsync(Req("LIST", dir));
                Check(await s.ReadLineAsync());
                var items = new List<FsItem>();
                foreach (var r in await Rows(s))
                {
                    if (r.Length < 5) continue;
                    items.Add(new FsItem
                    {
                        Name = r[0], FullPath = FsItem.Combine(dir, r[0]), Attributes = Hex(r[1]),
                        Size = U(r[2]), Modified = Ft(r[3]), Created = Ft(r[4])
                    });
                }
                return items;
            });
        }

        public Task<List<DriveInfo>> DrivesAsync()
        {
            return OnMain(async s =>
            {
                await s.SendAsync(Req("DRIVES"));
                Check(await s.ReadLineAsync());
                var list = new List<DriveInfo>();
                foreach (var r in await Rows(s))
                    if (r.Length >= 5)
                        list.Add(new DriveInfo { Root = r[0], Label = r[1], FileSystem = r[2], Total = U(r[3]), Free = U(r[4]) });
                return list;
            });
        }

        public async Task<StatInfo> StatAsync(string path)
        {
            var f = await Simple("STAT", path);
            return new StatInfo
            {
                Attributes = Hex(f[1]), Size = U(f[2]), Created = Ft(f[3]), Accessed = Ft(f[4]), Modified = Ft(f[5])
            };
        }

        public Task MkdirAsync(string path) { return Simple("MKDIR", path); }
        public Task NewFileAsync(string path) { return Simple("NEWFILE", path); }
        public Task RenameAsync(string from, string to) { return Simple("RENAME", from, to); }
        public Task SetAttributesAsync(string path, FileAttr a) { return Simple("ATTR", path, ((uint)a).ToString("x")); }
        public Task QuitServerAsync() { return Simple("QUIT"); }

        /// <summary>Web access over Wi-Fi: turns it on/off or asks its state. Returns (on, port, phone IP).</summary>
        public async Task<Tuple<bool, int, string>> WebAsync(bool? on, int port = 0, string password = null)
        {
            string[] f = on == true ? await Simple("WEB", "on", port.ToString(), password)
                       : on == false ? await Simple("WEB", "off")
                       : await Simple("WEB", "status");
            int p;
            int.TryParse(f[2], out p);
            return Tuple.Create(f[1] == "on", p, f.Length > 3 ? f[3] : "");
        }

        /// <summary>Reads part of a file. Returns the bytes and the file's full size.</summary>
        public Task<Tuple<byte[], ulong>> ReadAsync(string path, ulong offset, int count)
        {
            return OnMain(async s =>
            {
                await s.SendAsync(Req("READ", path, offset.ToString(), count.ToString()));
                var f = Check(await s.ReadLineAsync());
                var data = await s.ReadExactAsync(int.Parse(f[1]));
                return Tuple.Create(data, U(f[2]));
            });
        }

        public Task WriteAsync(string path, byte[] data)
        {
            return OnMain(async s =>
            {
                await s.SendAsync(Req("WRITE", path, data.Length.ToString()));
                await s.SendAsync(data);
                return Check(await s.ReadLineAsync());
            });
        }

        /// <summary>Runs COPY/MOVE/DELETE/DU on its own connection, reporting progress.
        /// Cancelling sends one byte, which rfxfsd treats as "stop".</summary>
        public async Task<string[]> LongAsync(IProgress<OpProgress> progress, CancellationToken cancel, params string[] fields)
        {
            var s = await OpenAsync();
            if (s == null) throw new RfxException(0, "the helper isn't running");
            using (s)
            using (cancel.Register(() => { var _ = s.SendAsync(new byte[] { (byte)'X' }); }))
            {
                await s.SendAsync(Req(fields));
                for (;;)
                {
                    var l = await s.ReadLineAsync();
                    if (l != null && l.StartsWith("P\t"))
                    {
                        var p = l.Split('\t');
                        if (progress != null && p.Length >= 4)
                            progress.Report(new OpProgress { Bytes = U(p[1]), Files = U(p[2]), Current = p[3] });
                        continue;
                    }
                    return Check(l);
                }
            }
        }

        public void Close()
        {
            if (_main != null) _main.Dispose();
            _main = null;
        }
    }
}
