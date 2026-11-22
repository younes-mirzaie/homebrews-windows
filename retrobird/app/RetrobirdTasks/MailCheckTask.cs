// Checks every account's inbox while Retrobird is closed (Windows runs it about every 30
// minutes, no sooner), then updates the live tile and shows a notification for new mail.
// Everything happens on the phone: the engine (rbmail.dll) does one check in this process and
// stops. There is no push server.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Retrobird.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Background;
using Windows.Storage;

namespace RetrobirdTasks
{
    public sealed class MailCheckTask : IBackgroundTask
    {
        [DllImport("rbmail.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int RetrobirdCheck(int argc,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv,
            byte[] output, int outlen);

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            var deferral = taskInstance.GetDeferral();
            try { Check(); }
            catch (Exception) { }
            finally { deferral.Complete(); }
        }

        static void Check()
        {
            // the app's own engine is working on the same mail while it's open
            if (Notify.AppProbablyOpen) return;
            object t;
            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue("token", out t) || !(t is string)) return;
            string[] args =
            {
                "rbmail", "--token", (string)t,
                "--data", Path.Combine(ApplicationData.Current.LocalFolder.Path, "data"),
                "--roots", Path.Combine(Package.Current.InstalledLocation.Path, "nssckbi.dll")
            };
            var buf = new byte[64 * 1024];
            if (RetrobirdCheck(args.Length, args, buf, buf.Length) != 0) return;
            int len = Array.IndexOf(buf, (byte)0);
            var text = Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);

            var accounts = new List<AccountMail>();
            AccountMail current = null;
            foreach (var line in text.Split('\n'))
            {
                var f = line.Split('\t');
                if (f[0] == "A" && f.Length >= 4)
                {
                    int n;
                    int.TryParse(f[3], out n);
                    current = new AccountMail { Id = f[1], Email = f[2], Unread = Math.Max(0, n) };
                    accounts.Add(current);
                }
                else if (f[0] == "M" && f.Length >= 5 && current != null)
                {
                    uint uid;
                    long date;
                    uint.TryParse(f[1], out uid);
                    long.TryParse(f[2], out date);
                    current.Mail.Add(new NewMail { AccountId = current.Id, Uid = uid, Date = date, From = f[3], Subject = f[4] });
                }
            }
            Notify.Report(accounts);
        }
    }
}
