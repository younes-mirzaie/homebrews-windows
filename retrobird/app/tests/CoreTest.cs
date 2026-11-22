// Checks the shared core pieces that need no mail engine (replies, the body page, day groups,
// provider names), on desktop Mono. Run by helper/test/run-tests.sh.
using System;
using Retrobird.Core;

static class CoreTest
{
    static int pass, fail;
    static void Check(bool ok, string what) { if (ok) pass++; else { fail++; Console.WriteLine("FAIL: " + what); } }

    static int Main()
    {
        var v = new MessageView
        {
            From = "Ann Example <ann@x.org>", To = "me@y.org, Bob <bob@z.org>", Cc = "carol@w.org",
            Subject = "Plans", MessageId = "<1@x>", Text = "line one\n> older", Date = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc)
        };
        var r = Drafts.Reply(v, "INBOX", 7, false, "me@y.org");
        Check(r.To == "Ann Example <ann@x.org>" && r.Subject == "Re: Plans" && r.ReplyUid == 7 && r.Cc == "", "reply to sender");
        Check(r.Body.Contains("> line one") && r.Body.Contains(">> older"), "quote");
        var all = Drafts.Reply(v, "INBOX", 7, true, "ME@y.org");
        Check(all.Cc == "Bob <bob@z.org>, carol@w.org", "reply all leaves me out: " + all.Cc);
        Check(Drafts.Prefixed("Re: x", "Re:") == "Re: x" && Drafts.Prefixed("AW: x", "Re:") == "AW: x", "prefix once");
        Check(Drafts.Forward(v).Subject == "Fwd: Plans", "forward subject");
        Check(Drafts.FromMailto("mailto:a%40b.org?subject=x").To == "a@b.org", "mailto");
        Check(Drafts.Recipients(v, "me@y.org") == "to you, Bob and carol@w.org", "recipients: " + Drafts.Recipients(v, "me@y.org"));

        var plain = new MessageView { Html = "<div class=\"rb-plain\">hi</div>", Text = "hi" };
        var page = MessageHtml.Build(plain, false, new BodyStyle { Dark = true, Accent = "#a4c400" });
        Check(page.Contains("background:#000000") && page.Contains("#a4c400"), "plain mail follows the dark theme");
        var html = new MessageView { Html = "<p>café \U0001F600</p>" };
        page = MessageHtml.Build(html, false, new BodyStyle { Dark = true });
        Check(page.Contains("background:#ffffff") && page.Contains("caf&#233;") && page.Contains("&#128512;"), "html mail stays white, entities");

        Check(Format.DayGroup(DateTime.UtcNow) == "today", "today");
        Check(Format.DayGroup(DateTime.UtcNow.AddDays(-1)) == "yesterday", "yesterday");
        Check(Format.Sentence("last week") == "Last week", "sentence case");
        Check(Provider.ForEmail("a@hotmail.com").ShortName == "Outlook" && Provider.ByName("Yahoo Mail").ShortName == "Yahoo", "short names");
        Check(new AccountInfo { Email = "a@gmail.com" }.ShortName == "Gmail" && new AccountInfo { Email = "a@example.net" }.ShortName == "example.net", "account short name");

        Console.WriteLine("C# core: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
