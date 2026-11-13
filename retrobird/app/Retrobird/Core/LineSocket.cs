// A small async line-oriented TCP client for the in-app mail engine on 127.0.0.1:8097.
// On the phone (WinRT 8.1, NETFX_CORE) it runs on a StreamSocket; elsewhere (the Mono tests) on
// System.Net.Sockets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if NETFX_CORE
using Windows.Networking;
using Windows.Networking.Sockets;
#else
using System.Net.Sockets;
#endif

namespace Retrobird.Core
{
    public sealed class LineSocket : IDisposable
    {
#if NETFX_CORE
        readonly StreamSocket _socket = new StreamSocket();
#else
        readonly TcpClient _socket = new TcpClient();
#endif
        Stream _in, _out;
        readonly byte[] _buf = new byte[64 * 1024];
        int _start, _end;   // unread bytes in _buf
        bool _closed;
        Task<bool> _pending;   // a receive still running after ReadAvailableAsync timed out

        public bool IsClosed { get { return _closed; } }

        public static async Task<LineSocket> ConnectAsync(string host, int port, int timeoutMs)
        {
            var s = new LineSocket();
            try
            {
#if NETFX_CORE
                s._socket.Control.NoDelay = true;
                var cts = new CancellationTokenSource(timeoutMs);
                await s._socket.ConnectAsync(new HostName(host), port.ToString()).AsTask(cts.Token);
                s._in = s._socket.InputStream.AsStreamForRead(0);
                s._out = s._socket.OutputStream.AsStreamForWrite(0);
#else
                var connect = s._socket.ConnectAsync(host, port);
                if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect) throw new TimeoutException();
                await connect;
                s._in = s._out = s._socket.GetStream();
#endif
                return s;
            }
            catch (Exception)
            {
                s.Dispose();
                return null;
            }
        }

        public Task SendAsync(string text) { return SendAsync(Encoding.UTF8.GetBytes(text)); }

        public async Task SendAsync(byte[] data, int offset = 0, int count = -1)
        {
            if (count < 0) count = data.Length - offset;
            try
            {
                await _out.WriteAsync(data, offset, count);
                await _out.FlushAsync();
            }
            catch (Exception)
            {
                _closed = true;
                throw new MailException("NET", "the connection to the mail engine was lost");
            }
        }

        async Task<bool> FillAsync()
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start; _start = 0;
            }
            if (_end == _buf.Length) return false;
            int n;
            try { n = await _in.ReadAsync(_buf, _end, _buf.Length - _end); }
            catch (Exception) { n = -1; }
            if (n <= 0) { _closed = true; return false; }
            _end += n;
            return true;
        }

        /// <summary>Reads one \n-terminated UTF-8 line (without the \n), or null when the connection closes.</summary>
        public async Task<string> ReadLineAsync()
        {
            for (;;)
            {
                int nl = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
                if (nl >= 0)
                {
                    int len = nl - _start;
                    if (len > 0 && _buf[nl - 1] == '\r') len--;
                    string s = Encoding.UTF8.GetString(_buf, _start, len);
                    _start = nl + 1;
                    return s;
                }
                if (!await FillAsync()) return null;
            }
        }

        /// <summary>Reads whatever arrives within the time limit (for the telnet shell).</summary>
        public async Task<string> ReadAvailableAsync(int quietMs)
        {
            var sb = new StringBuilder();
            for (;;)
            {
                if (_end > _start)
                {
                    sb.Append(Encoding.UTF8.GetString(_buf, _start, _end - _start));
                    _start = _end = 0;
                }
                var fill = _pending ?? FillAsync();
                _pending = null;
                if (await Task.WhenAny(fill, Task.Delay(quietMs)) != fill)
                {
                    _pending = fill;   // picked up by the next ReadAvailableAsync
                    return sb.ToString();
                }
                if (!fill.Result) return sb.ToString();
            }
        }

        public async Task<byte[]> ReadExactAsync(int count)
        {
            var result = new byte[count];
            int got = 0;
            while (got < count)
            {
                if (_end == _start && !await FillAsync()) throw new MailException("NET", "the connection to the mail engine was lost");
                int n = Math.Min(count - got, _end - _start);
                Buffer.BlockCopy(_buf, _start, result, got, n);
                _start += n; got += n;
            }
            return result;
        }

        public void Dispose()
        {
            _closed = true;
            try { _socket.Dispose(); } catch { }
        }
    }

    public class MailException : Exception
    {
        /// <summary>AUTH, CERT, NET, SERVER, ARG, NOTFOUND, IO (see PROTOCOL.md)</summary>
        public string Code { get; private set; }
        /// <summary>For CERT: the certificate's SHA-256 fingerprint.</summary>
        public string Fingerprint { get; set; }
        /// <summary>For CERT: "imap" or "smtp".</summary>
        public string Side { get; set; }
        public MailException(string code, string message) : base(message) { Code = code; }
    }
}
