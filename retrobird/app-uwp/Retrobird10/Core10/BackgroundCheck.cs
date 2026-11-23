// The check for new mail while Retrobird is closed, on Windows 10. Windows 10 runs it in the app's
// own process (OnBackgroundActivated), so it uses the in-app engine the way the app does: check
// every inbox, then read each account's unread mail from the cache. The tiles and notifications
// are the phone app's own code (Notify.Report), so both apps announce mail the same way.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Retrobird.Core;

namespace Retrobird
{
    static class BackgroundCheck
    {
        public static async Task RunAsync()
        {
            // on screen the inbox checks by itself
            if (Notify.AppProbablyOpen) return;
            var c = AppState.Client;
            await c.EnsureAsync();
            var accounts = await c.GetAccountsAsync();
            foreach (var a in accounts)
            {
                try { await c.SyncAsync(a.Id, "INBOX"); }
                catch (Exception e) { InAppHelper.Log("background check of " + a.ShortName + ": " + e.Message); }
            }
            accounts = await c.GetAccountsAsync();
            var report = new List<AccountMail>();
            foreach (var a in accounts)
            {
                var r = new AccountMail { Id = a.Id, Email = a.Email, Unread = Math.Max(0, a.Unseen) };
                try
                {
                    var list = await c.ListAsync(a.Id, "INBOX", 0, 20);
                    r.Mail = list.Items.Where(m => m.IsUnread).Select(m => new NewMail
                    {
                        AccountId = a.Id, Uid = m.Uid, Date = Notify.ToUnix(m.Date), From = m.From, Subject = m.Subject
                    }).ToList();
                }
                catch (Exception e) { InAppHelper.Log("background list of " + a.ShortName + ": " + e.Message); }
                report.Add(r);
            }
            Notify.Report(report);
        }
    }
}
