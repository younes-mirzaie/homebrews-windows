// Runs the mail engine inside the app: rbmail.dll (helper/*.c on NSPR/NSS) is loaded from the
// package and RetrobirdMain runs on a thread of its own, serving the app over loopback inside this
// process. Nothing runs outside the app: no CMD Injector, no SYSTEM process, no shared folder.
// Mail, settings and the log live in the app's own folder (LocalState\data).
// This needs a WinRT (appx) app: Windows Phone 8.1 Silverlight apps can't DllImport their own DLLs
// ("DllImport cannot be used on user-defined methods"), which is why Retrobird moved off Silverlight.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Security.Cryptography;
using Windows.Storage;

namespace Retrobird.Core
{
    public static class InAppHelper
    {
        public const int Port = 8097;

        [DllImport("rbmail.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int RetrobirdMain(int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv);

        static Task<string> _ended;    // completes with why the engine stopped

        public static string DataDir { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "data"); } }

        /// <summary>The key the engine only answers to; also seals the stored passwords.</summary>
        public static string Token
        {
            get
            {
                var settings = ApplicationData.Current.LocalSettings.Values;
                object v;
                var t = settings.TryGetValue("token", out v) ? v as string : null;
                if (string.IsNullOrEmpty(t))
                {
                    t = CryptographicBuffer.EncodeToHexString(CryptographicBuffer.GenerateRandom(16));
                    settings["token"] = t;
                }
                return t;
            }
        }

        static void Start()
        {
            string pkg = Package.Current.InstalledLocation.Path;
            string[] args =
            {
                "rbmail", "--token", Token, "--port", Port.ToString(),
                "--data", DataDir, "--roots", Path.Combine(pkg, "nssckbi.dll")
            };
            _ended = Task.Factory.StartNew(() =>
            {
                try
                {
                    int code = RetrobirdMain(args.Length, args);
                    Log("engine stopped with code " + code);
                    return "Retrobird's mail engine stopped. Close Retrobird and open it again.";
                }
                catch (Exception e)
                {
                    // rbmail.dll or a DLL it needs didn't load. Nothing the user can do about it;
                    // the details are kept for the developer.
                    Log("engine failed to load: " + e);
                    return "Retrobird's mail engine couldn't start on this phone, so it can't check or send mail.";
                }
            }, TaskCreationOptions.LongRunning);
        }

        // one write at a time: overlapping appends to the same file fail, and lines got lost
        static readonly SemaphoreSlim _logLock = new SemaphoreSlim(1);

        /// <summary>App-side notes for the developer (LocalState\data\app.log), next to the engine's own log.</summary>
        public static async void Log(string line)
        {
            line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + "\r\n";
            await _logLock.WaitAsync();
            try
            {
                var dir = await ApplicationData.Current.LocalFolder.CreateFolderAsync("data", CreationCollisionOption.OpenIfExists);
                var f = await dir.CreateFileAsync("app.log", CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(f, line);
            }
            catch (Exception) { }
            finally { _logLock.Release(); }
        }

        /// <summary>Whether our engine answers on the port.</summary>
        static async Task<bool> Answers()
        {
            var s = await LineSocket.ConnectAsync(MailClient.Host, Port, 1500);
            if (s == null) return false;
            using (s)
            {
                await s.SendAsync("AUTH\t" + Token + "\n");
                var r = await s.ReadLineAsync();
                return r != null && r.StartsWith("OK\t");
            }
        }

        /// <summary>StartHelper for MailClient: null once the engine answers, else a message.</summary>
        public static async Task<string> EnsureRunningAsync(MailClient client)
        {
            if (_ended == null || _ended.IsCompleted) Start();
            // up to 10 s: the first start sets up NSS and the cache
            for (int i = 0; i < 40; i++)
            {
                if (_ended.IsCompleted) return _ended.Result;
                if (await Answers()) return null;
                await Task.Delay(250);
            }
            return "Retrobird's mail engine didn't answer. Close Retrobird and open it again.";
        }
    }
}
