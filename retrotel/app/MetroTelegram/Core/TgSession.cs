using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using MetroTelegram.Crypto;
using MetroTelegram.TL;
using MetroTelegram.Transport;

namespace MetroTelegram
{
    /// <summary>
    /// The shared Telegram session: login key storage, the MTProto connection, update events and
    /// the name/access-hash caches. Used by both front ends (Windows Phone 8.1 and Windows 10);
    /// it has no UI code.
    /// </summary>
    public static class TgSession
    {
        public static AuthKeyStorage Storage { get; private set; }
        public static MtprotoTcpTransport Transport { get; private set; }
        public static TelegramRpcEngine RpcEngine { get; private set; }
        public static UpdatesService Updates { get; private set; }

        /// <summary>Raised on a background thread for every new message pushed by the server.</summary>
        public static event EventHandler<IncomingMessageEventArgs> MessageReceived;
        public static event EventHandler<OutboxReadEventArgs> OutboxRead;
        public static event EventHandler<UserTypingEventArgs> UserTyping;

        public static readonly Dictionary<long, string> UsersCache = new Dictionary<long, string>();
        public static readonly Dictionary<long, long> AccessHashCache = new Dictionary<long, long>();

        public static void Init()
        {
            if (Storage != null) return;
            Storage = new AuthKeyStorage();
            Storage.Load();
        }

        public static void CacheUser(long id, string name)
        {
            if (id == 0 || string.IsNullOrEmpty(name)) return;
            lock (UsersCache)
            {
                UsersCache[id] = name;
                UsersCache[Math.Abs(id)] = name;
            }
        }

        public static void CacheAccessHash(long id, long accessHash)
        {
            if (id == 0 || accessHash == 0) return;
            lock (AccessHashCache) { AccessHashCache[id] = accessHash; }
        }

        public static long GetAccessHash(long id)
        {
            lock (AccessHashCache)
            {
                long hash;
                return AccessHashCache.TryGetValue(id, out hash) ? hash : 0;
            }
        }

        public static string GetUserName(long id)
        {
            if (id == 0) return string.Empty;
            lock (UsersCache)
            {
                string name;
                if (UsersCache.TryGetValue(id, out name) || UsersCache.TryGetValue(Math.Abs(id), out name))
                {
                    return name;
                }
            }
            return "Member " + id;
        }

        public static async Task EnsureConnectedAsync(Action<string> statusCallback = null)
        {
            Init();
            if (Transport == null || !Transport.IsConnected)
            {
                Storage.Load();
                DataCenter dc = DataCenter.GetDc(Storage.CurrentDcId);

                statusCallback?.Invoke("Connecting...");

                Transport = new MtprotoTcpTransport();
                await Transport.ConnectAsync(dc);

                if (!Storage.HasAuthKey)
                {
                    statusCallback?.Invoke("Setting up encryption...");
                    var handshake = new AuthKeyHandshake(Transport);
                    await handshake.ExecuteAsync(Storage);
                }

                RpcEngine = new TelegramRpcEngine(Transport, Storage);

                Updates?.Dispose();
                Updates = new UpdatesService(RpcEngine);
                Updates.MessageReceived += (s, e) => { MessageReceived?.Invoke(s, e); };
                Updates.OutboxRead += (s, e) => { OutboxRead?.Invoke(s, e); };
                Updates.UserTyping += (s, e) => { UserTyping?.Invoke(s, e); };

                statusCallback?.Invoke("Connecting...");
                byte[] configQuery;
                using (var writer = new TlBinaryWriter())
                {
                    writer.WriteUInt32(0xc4f9186b); // help.getConfig
                    configQuery = writer.ToByteArray();
                }

                await RpcEngine.SendRpcQueryAsync(configQuery, wrapInitConnection: true);
                Debug.WriteLine("[TgSession] connected");
            }
        }

        /// <summary>Logs out on the server (best effort), drops the connection and forgets the login key.</summary>
        public static async Task SignOutAsync()
        {
            try
            {
                if (RpcEngine != null && Transport != null && Transport.IsConnected)
                {
                    byte[] logoutQuery;
                    using (var writer = new TlBinaryWriter())
                    {
                        writer.WriteUInt32(0x3e72ba19); // auth.logOut
                        logoutQuery = writer.ToByteArray();
                    }
                    await RpcEngine.SendRpcQueryAsync(logoutQuery, wrapInitConnection: false, timeoutMs: 5000);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[TgSession] logOut: " + ex.Message);
            }
            Disconnect();
            Storage?.Clear();
            lock (UsersCache) { UsersCache.Clear(); }
            lock (AccessHashCache) { AccessHashCache.Clear(); }
        }

        public static void Disconnect()
        {
            Updates?.Dispose();
            Updates = null;
            Transport?.Disconnect();
            Transport = null;
            RpcEngine = null;
        }
    }
}
