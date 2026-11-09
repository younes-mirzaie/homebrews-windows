// Runs the mail helper inside the app: rbmail.dll (the same C code as rbmaild.exe) is loaded from
// the package and RetrobirdMain runs on a thread of its own, serving the app over loopback inside
// this process. No CMD Injector, no SYSTEM process, and the data stays in the app's own folder.
// If the DLL can't be loaded, the app falls back to the separate rbmaild.exe (HelperLauncher).
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;

namespace Retrobird.Core
{
    public static class InAppHelper
    {
        /// <summary>Not the separate helper's 8096, so a leftover one can't answer instead.</summary>
        public const int Port = 8097;

        [DllImport("rbmail.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int RetrobirdMain(int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv);

        static Task<string> _ended;    // completes with why the helper stopped (null: never started)
        static bool _failed;           // the DLL couldn't run here: use the separate helper

        public static string DataDir { get { return Path.Combine(ApplicationData.Current.LocalFolder.Path, "data"); } }

        static void Start()
        {
            string pkg = Package.Current.InstalledLocation.Path;
            string[] args =
            {
                "rbmail", "--token", HelperLauncher.Token, "--port", Port.ToString(),
                "--data", DataDir, "--roots", Path.Combine(pkg, "nssckbi.dll")
            };
            _ended = Task.Factory.StartNew(() =>
            {
                try
                {
                    int code = RetrobirdMain(args.Length, args);
                    return "the mail helper stopped (" + code + "); see " + Path.Combine(DataDir, "rbmaild.log");
                }
                catch (Exception e)
                {
                    // the DLL didn't load, an entry point is missing, or P/Invoke isn't allowed
                    _failed = true;
                    return "rbmail.dll couldn't run inside the app: " + e.GetType().Name + ": " + e.Message;
                }
            }, TaskCreationOptions.LongRunning);
        }

        /// <summary>StartHelper for MailClient: null once a helper answers, else a message.</summary>
        public static async Task<string> EnsureRunningAsync(MailClient client)
        {
            if (!_failed)
            {
                if (_ended == null || _ended.IsCompleted) Start();
                // up to 10 s: the first start sets up NSS and the cache
                for (int i = 0; i < 40; i++)
                {
                    if (_ended.IsCompleted) break;
                    if (await HelperLauncher.Probe(Port) == "") { client.Port = Port; return null; }
                    await Task.Delay(250);
                }
                if (!_failed)
                    return _ended.IsCompleted ? _ended.Result : "The mail helper inside the app didn't answer on 127.0.0.1:" + Port + ".";
            }
            // fallback: the separate helper through CMD Injector
            client.Port = MailClient.DefaultPort;
            var msg = await HelperLauncher.EnsureRunningAsync(MailClient.DefaultPort, _ => { });
            return msg == null ? null : _ended.Result + "\n\nThe fallback also failed: " + msg;
        }
    }
}
