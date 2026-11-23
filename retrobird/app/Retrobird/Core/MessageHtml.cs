// The page the message body is shown in (a web view). The engine has already removed scripts
// and blocked remote pictures; this wraps it in a page that fits the phone and the theme, with
// the message's headers on top so they scroll away with the mail.
// Shared by the Windows Phone 8.1 and Windows 10 apps.
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Retrobird.Core
{
    public sealed class BodyStyle
    {
        public bool Dark;            // the app's theme
        public string Accent = "#1ba1e2";
        public string Font = "'Segoe WP','Segoe UI',sans-serif";
        public double FontSize = 16; // CSS px; the app's body text size
        public string Padding = "0 19px 24px";
        /// <summary>Windows 10 header sizes and icons (Segoe MDL2 Assets); otherwise Metro's.</summary>
        public bool Uwp;
    }

    /// <summary>What the page shows above the mail, so it scrolls away with it.</summary>
    public sealed class MessageHeader
    {
        public string Where = "", From = "", Subject = "", When = "", Details = "";
        public List<Attachment> Attachments = new List<Attachment>();
        public bool PicturesHidden;
    }

    public static class MessageHtml
    {
        public static bool IsPlain(string html) { return (html ?? "").StartsWith("<div class=\"rb-plain\">"); }

        static string Escape(string s) { return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"); }

        /// <summary>Everything above 127 goes in as a numeric character reference, so the text
        /// survives whatever encoding the web view assumes.</summary>
        public static string ToEntities(string s)
        {
            var b = new StringBuilder(s.Length + 64);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 128) { b.Append(c); continue; }
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    cp = char.ConvertToUtf32(c, s[++i]);
                b.Append("&#").Append(cp.ToString(CultureInfo.InvariantCulture)).Append(';');
            }
            return b.ToString();
        }

        // Taps in the header: the "when" line shows every address; attachments and "show
        // pictures" go to the app (window.external.notify "att:<index>" / "images").
        const string Script =
            "<script>document.addEventListener('click',function(e){for(var t=e.target;t&&t.getAttribute;t=t.parentNode){" +
            "var a=t.getAttribute('data-rb');if(!a)continue;" +
            "if(a=='details'){var d=document.getElementById('rb-details');d.style.display=d.style.display=='block'?'none':'block'}" +
            "else{try{window.external.notify(a)}catch(x){}}e.preventDefault();return}},false);</script>";

        static string Header(MessageHeader h, BodyStyle st, string bg, string fg, string subtle)
        {
            if (h == null) return "";
            string pad = st.Uwp ? "12px" : "19px";
            string icons = st.Uwp ? "'Segoe MDL2 Assets'" : "'Segoe UI Symbol'";
            string clip = st.Uwp ? "\uE723" : "\uE16C";
            var b = new StringBuilder();
            b.Append("<style>");
            b.Append(".rb-head,.rb-head *{font-family:" + st.Font + ";background:" + bg + ";color:" + fg + ";line-height:1.25;text-align:left}");
            b.Append(".rb-head{padding:" + (st.Uwp ? "12px" : "0") + " " + pad + " 8px}");
            b.Append(".rb-head div{margin:0}.rb-head .rb-sub{color:" + subtle + "}.rb-head .rb-acc{color:" + st.Accent + "}");
            if (st.Uwp)
            {
                b.Append(".rb-where{font-size:12px}.rb-subject{font-size:24px;font-weight:300;margin-top:2px!important}");
                b.Append(".rb-from{font-size:15px;font-weight:600;margin-top:8px!important}.rb-when{font-size:12px;margin-top:2px!important}");
            }
            else
            {
                b.Append(".rb-where{font-size:18.14px;font-weight:600;text-transform:uppercase;padding-top:12px}");
                b.Append(".rb-from{font-size:34.13px;font-weight:300;letter-spacing:-0.01em;margin-top:8px!important;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}");
                b.Append(".rb-subject{font-size:25.6px;font-weight:300}.rb-when{font-size:14.93px;margin-top:6px!important}");
            }
            b.Append("#rb-details{display:none;font-size:" + (st.Uwp ? "12px" : "14.93px") + ";margin-top:6px!important;white-space:pre-wrap}");
            b.Append(".rb-att{display:table;margin-top:" + (st.Uwp ? "8px" : "12px") + "!important;cursor:pointer}");
            b.Append(".rb-att span{display:table-cell;vertical-align:middle}");
            string box = st.Uwp ? "32px" : "38.5px";
            b.Append(".rb-head .rb-icon{width:" + box + ";height:" + box + ";background:" + st.Accent + ";color:#ffffff;text-align:center;font-family:" + icons + ";font-size:" + (st.Uwp ? "16px" : "18px") + "}");
            b.Append(".rb-att .rb-name{padding-left:" + (st.Uwp ? "8px" : "9.5px") + ";font-size:" + (st.Uwp ? "15px" : "18.13px") + "}");
            b.Append(".rb-att .rb-size{display:block;font-size:" + (st.Uwp ? "12px" : "14.93px") + ";color:" + subtle + "}");
            b.Append(".rb-pics{margin-top:10px!important;padding:8px 0;border-top:1px solid " + subtle + ";border-bottom:1px solid " + subtle + ";font-size:" + (st.Uwp ? "12px" : "14.93px") + "}");
            b.Append(".rb-head a{color:" + st.Accent + ";text-decoration:none;cursor:pointer}");
            b.Append("</style><div class=\"rb-head\">");
            if (h.Where.Length > 0) b.Append("<div class=\"rb-where" + (st.Uwp ? " rb-sub" : "") + "\">" + Escape(h.Where) + "</div>");
            if (st.Uwp)
            {
                b.Append("<div class=\"rb-subject\">" + Escape(h.Subject) + "</div>");
                b.Append("<div class=\"rb-from rb-acc\">" + Escape(h.From) + "</div>");
            }
            else
            {
                b.Append("<div class=\"rb-from\">" + Escape(h.From) + "</div>");
                b.Append("<div class=\"rb-subject rb-acc\">" + Escape(h.Subject) + "</div>");
            }
            b.Append("<div class=\"rb-when rb-sub\" data-rb=\"details\">" + Escape(h.When) + "</div>");
            b.Append("<div id=\"rb-details\" class=\"rb-sub\">" + Escape(h.Details) + "</div>");
            foreach (var a in h.Attachments)
            {
                b.Append("<div class=\"rb-att\" data-rb=\"att:" + a.Index.ToString(CultureInfo.InvariantCulture) + "\">");
                b.Append("<span class=\"rb-icon\">" + clip + "</span><span class=\"rb-name\">" + Escape(a.Name));
                b.Append("<span class=\"rb-size\">" + Escape(a.SizeText) + "</span></span></div>");
            }
            if (h.PicturesHidden)
                b.Append("<div class=\"rb-pics rb-sub\">" + Escape(st.Uwp ? "Pictures from the web are hidden to protect your privacy. " : "Pictures from the web are hidden. ") +
                         "<a data-rb=\"images\">" + (st.Uwp ? "Show pictures" : "show pictures") + "</a></div>");
            b.Append("</div>");
            return b.ToString();
        }

        /// <summary>The whole page: the headers (when given), then the mail, scrolling as one.
        /// Plain text mail takes the app's theme and accent; formatted (HTML) mail keeps the white
        /// page its sender designed it for.</summary>
        public static string Build(MessageView v, bool showPlain, BodyStyle st, MessageHeader head = null)
        {
            string content = v.Html ?? "";
            bool plain = IsPlain(content);
            if (showPlain && !string.IsNullOrEmpty(v.Text))
            {
                content = "<div class=\"rb-plain\">" + Escape(v.Text) + "</div>";
                plain = true;
            }
            string themeBg = st.Dark ? "#000000" : "#ffffff", themeFg = st.Dark ? "#ffffff" : "#000000";
            string subtle = st.Dark ? "#999999" : "#666666";
            string bg = "#ffffff", fg = "#000000";
            if (plain && st.Dark) { bg = "#000000"; fg = "#ffffff"; }
            string size = st.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
            var page = "<!DOCTYPE html><html><head><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>" +
                       "html,body{margin:0;padding:0}" +
                       "html{background:" + (head != null ? themeBg : bg) + "}" +
                       "body{font-family:" + st.Font + ";font-size:" + size + "px;line-height:1.4;word-wrap:break-word;" +
                       "background:" + (head != null ? themeBg : bg) + ";color:" + fg + "}" +
                       ".rb-mail{padding:" + (plain ? st.Padding : "8px 12px") + ";background:" + bg + ";color:" + fg + "}" +
                       "img{max-width:100%;height:auto}table{max-width:100%}" +
                       ".rb-plain{white-space:pre-wrap;font-family:inherit}" +
                       ".rb-mail a{color:" + (plain ? st.Accent : "#0645ad") + "}" +
                       "blockquote{margin:0 0 0 6px;padding-left:8px;border-left:2px solid #888}" +
                       "</style>" + (head != null ? Script : "") + "</head><body>" +
                       Header(head, st, themeBg, themeFg, subtle) +
                       "<div class=\"rb-mail\">" + content + "</div></body></html>";
            return ToEntities(page);
        }
    }
}
