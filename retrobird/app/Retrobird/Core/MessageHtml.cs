// The page the message body is shown in (a web view). The engine has already removed scripts
// and blocked remote pictures; this only wraps it in a page that fits the phone and the theme.
// Shared by the Windows Phone 8.1 and Windows 10 apps.
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
        /// <summary>Tells the app (window.external.notify "hide" / "show") when the reader scrolls
        /// down into the mail or back up, so the headers above can make room.</summary>
        public bool ScrollNotify;
    }

    public static class MessageHtml
    {
        // Hide after 32 px down (and only when the mail is long enough that it still scrolls with
        // the headers gone), show again after 32 px up or at the top.
        const string ScrollScript =
            "<script>(function(){var a=0,h=false;function y(){return window.pageYOffset||document.documentElement.scrollTop||0}" +
            "function n(v){try{window.external.notify(v)}catch(e){}}" +
            "window.onscroll=function(){var t=y(),room=document.documentElement.scrollHeight-window.innerHeight;" +
            "if(!h){if(t>a+32&&t>48&&room>320){h=true;n('hide');a=t}else if(t<a)a=t}" +
            "else{if(t<a-32||t<=0){h=false;n('show');a=t}else if(t>a)a=t}}})();</script>";

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

        /// <summary>The whole page. Plain text mail takes the app's theme and accent; formatted
        /// (HTML) mail keeps the white page its sender designed it for.</summary>
        public static string Build(MessageView v, bool showPlain, BodyStyle st)
        {
            string content = v.Html ?? "";
            bool plain = IsPlain(content);
            if (showPlain && !string.IsNullOrEmpty(v.Text))
            {
                content = "<div class=\"rb-plain\">" + Escape(v.Text) + "</div>";
                plain = true;
            }
            string bg = "#ffffff", fg = "#000000";
            if (plain && st.Dark) { bg = "#000000"; fg = "#ffffff"; }
            string size = st.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
            var page = "<!DOCTYPE html><html><head><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>" +
                       "html,body{margin:0;padding:0}" +
                       "body{font-family:" + st.Font + ";font-size:" + size + "px;line-height:1.4;word-wrap:break-word;" +
                       "padding:" + (plain ? st.Padding : "8px 12px") + ";background:" + bg + ";color:" + fg + "}" +
                       "img{max-width:100%;height:auto}table{max-width:100%}" +
                       ".rb-plain{white-space:pre-wrap;font-family:inherit}" +
                       "a{color:" + (plain ? st.Accent : "#0645ad") + "}" +
                       "blockquote{margin:0 0 0 6px;padding-left:8px;border-left:2px solid #888}" +
                       "</style>" + (st.ScrollNotify ? ScrollScript : "") + "</head><body>" + content + "</body></html>";
            return ToEntities(page);
        }
    }
}
