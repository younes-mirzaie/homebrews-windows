// Runs the mail engine inside the app: rbmail.dll (helper/*.c on NSPR/NSS) is loaded from the
// package and RetrobirdMain runs on a thread of its own, serving the app over loopback inside this
// process. Nothing runs outside the app: no CMD Injector, no SYSTEM process, no shared folder.
// Mail, settings and the log live in the app's own folder (LocalState\data).
using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
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
                var settings = IsolatedStorageSettings.ApplicationSettings;
                string t;
                if (!settings.TryGetValue("token", out t) || string.IsNullOrEmpty(t))
                {
                    var bytes = new byte[16];
                    new RNGCryptoServiceProvider().GetBytes(bytes);
                    var sb = new StringBuilder();
                    foreach (var b in bytes) sb.Append(b.ToString("x2"));
                    t = sb.ToString();
                    settings["token"] = t;
                    settings.Save();
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
                    return "The mail engine stopped (code " + code + "). Details are in " + Path.Combine(DataDir, "rbmaild.log") + ".";
                }
                catch (Exception e)
                {
                    // rbmail.dll or one of the DLLs it needs didn't load: a bug in this build, not
                    // something the user can fix. Keep the details for the developer.
                    WriteLog("engine failed to load: " + e);
                    return "Retrobird's mail engine couldn't load on this phone (" + e.GetType().Name + "). " +
                           "This is a bug in this version of Retrobird, not something wrong with your phone or account. " +
                           "Please report it; an update will fix it.";
                }
            }, TaskCreationOptions.LongRunning);
        }

        /// <summary>App-side notes for the developer, next to the engine's own log.</summary>
        static void WriteLog(string line)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Path.Combine(DataDir, "app.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + "\r\n");
            }
            catch { }
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
            return "Retrobird's mail engine didn't answer. Close the app and open it again.";
        }
    }
}
