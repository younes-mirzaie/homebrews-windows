// Runs the app's protocol client (Core/MailClient.cs, LineSocket.cs, Models.cs, Providers.cs) on
// desktop Mono against a live rbmaild, so the C# side is tested without a phone.
// Built and run by helper/test/run-tests.sh: mcs ClientTest.cs ../Retrobird/Core/{...}.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Retrobird.Core;

static class ClientTest
{
    static int pass, fail;
    static void Check(bool ok, string what) { if (ok) pass++; else { fail++; Console.WriteLine("FAIL: " + what); } }

    static int Main(string[] args)
    {
        int port = int.Parse(args[0]);
        string token = args[1], password = args[2];
        try { Run(port, token, password).Wait(); }
        catch (AggregateException e) { fail++; Console.WriteLine("FAIL: exception " + e.InnerException); }
        Console.WriteLine("C# client: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static async Task Run(int port, string token, string password)
    {
        Check(MailClient.Unescape(MailClient.Escape("a\tb\\c\nd\re")) == "a\tb\\c\nd\re", "escape round trip");
        Check(Address.DisplayName("\"Doe, John\" <j@x.org>, ann@y.org, c@z.org") == "Doe, John, ann@y.org", "display names");
        Check(Address.EmailOf("Ann <ann@x.org>") == "ann@x.org" && Address.Split("a@b, \"c, d\" <e@f>").Count == 2, "address split");
        Check(Provider.ForEmail("me@gmail.com").ImapHost == "imap.gmail.com" && Provider.ForEmail("x@example.net").SmtpHost == "smtp.example.net", "providers");

        var bad = new MailClient("not-the-key-123", port);
        try { await bad.PingAsync(); Check(false, "wrong key must fail"); }
        catch (MailException e) { Check(e.Code == "AUTH", "wrong key: " + e.Code); }

        var c = new MailClient(token, port);
        Check(await c.PingAsync() == "1", "ping version");
        var fields = new Dictionary<string, string> {
            { "name", "Mono Test" }, { "email", "test@localhost" }, { "user", "test@localhost" }, { "pass", password },
            { "imap_host", "localhost" }, { "imap_port", "10993" }, { "imap_sec", "tls" },
            { "smtp_host", "localhost" }, { "smtp_port", "10465" }, { "smtp_sec", "tls" } };
        string id = null;
        for (int i = 0; i < 3 && id == null; i++)
        {
            try { id = await c.SaveAccountAsync(null, fields); }
            catch (MailException e)
            {
                if (e.Code != "CERT") { Check(false, "save: " + e.Code + " " + e.Message); return; }
                Check(e.Fingerprint.Length == 64 && (e.Side == "imap" || e.Side == "smtp"), "cert error parsed");
                fields[e.Side + "_pin"] = e.Fingerprint;
            }
        }
        Check(id != null && id.Length == 12, "account created");
        var accts = await c.GetAccountsAsync();
        Check(accts.Any(a => a.Id == id && a.HasPassword && a.Title == "Mono Test"), "accounts");
        var folders = await c.GetFoldersAsync(id);
        Check(folders.Online && folders.Folders[0].Role == "inbox" && folders.Folders.Any(f => f.Role == "sent"), "folders");
        Check(folders.Folders.Any(f => f.Label.Trim() == "entwürfe alt"), "folder label");
        var sr = await c.SyncAsync(id, "INBOX");
        Check(sr.Added > 0 && sr.Unseen > 0, "sync result " + sr.Added + " / " + sr.Unseen);
        var list = await c.ListAsync(id, "INBOX", 0, 10);
        Check(list.Items.Count == 10 && list.Items[0].Uid > list.Items[9].Uid, "list newest first");
        var m = list.Items.First(x => x.HasAttachment || true);
        var view = await c.GetMessageAsync(id, "INBOX", m.Uid, false);
        Check(!string.IsNullOrEmpty(view.Html) && view.Date != DateTime.MinValue && !string.IsNullOrEmpty(view.From), "message view");
        await c.SetFlagAsync(id, "INBOX", m.Uid, 'F', true);
        list = await c.ListAsync(id, "INBOX", 0, 1);
        Check(list.Items[0].IsFlagged && list.Items[0].Glyphs.Contains(""), "flag via client");
        var warn = await c.SendAsync(id, new Draft { To = "Ann <ann@example.com>", Subject = "From Mono ä", Body = "Hi\n.\nthere" });
        Check(warn == "", "send: " + warn);
        try { await c.SendAsync(id, new Draft { Subject = "nobody" }); Check(false, "send without recipients must fail"); }
        catch (MailException e) { Check(e.Code == "ARG", "no recipient error"); }
        try { await c.GetMessageAsync(id, "INBOX", 999999, false); Check(false, "missing message must fail"); }
        catch (MailException e) { Check(e.Message.Length > 0, "missing message error"); }
        // RB_KEEP_ACCOUNT: leave it for the background check test (check_test.py)
        if (Environment.GetEnvironmentVariable("RB_KEEP_ACCOUNT") != null) return;
        await c.DeleteAccountAsync(id);
        Check(!(await c.GetAccountsAsync()).Any(a => a.Id == id), "account deleted");
    }
}
