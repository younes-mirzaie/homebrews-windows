// Minimal strict JSON reader: objects -> Dictionary<string, object>, arrays -> List<object>,
// numbers -> long or double, plus string, bool, null. Enough for the catalog index.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AppLoader.Catalog
{
    public static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i, 0);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException("JSON: extra text at " + i);
            return v;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object Value(string s, ref int i, int depth)
        {
            if (depth > 32) throw new FormatException("JSON: nested too deep");
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unexpected end");
            char c = s[i];
            if (c == '{')
            {
                var o = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return o; }
                for (;;)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("JSON: expected a key at " + i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("JSON: expected ':' at " + i);
                    i++;
                    if (o.ContainsKey(k)) throw new FormatException("JSON: duplicate key " + k);
                    o[k] = Value(s, ref i, depth + 1);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return o; }
                    throw new FormatException("JSON: expected ',' or '}' at " + i);
                }
            }
            if (c == '[')
            {
                var a = new List<object>();
                i++; Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return a; }
                for (;;)
                {
                    a.Add(Value(s, ref i, depth + 1));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return a; }
                    throw new FormatException("JSON: expected ',' or ']' at " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            string num = s.Substring(start, i - start);
            long l;
            if (long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
            double d;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("JSON: bad value at " + start);
        }

        static bool Lit(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static string Str(string s, ref int i)
        {
            var b = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return b.ToString();
                if (c < 0x20) throw new FormatException("JSON: control character in string");
                if (c != '\\') { b.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': b.Append('"'); break;
                    case '\\': b.Append('\\'); break;
                    case '/': b.Append('/'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON: bad \\u escape");
                        b.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("JSON: bad escape");
                }
            }
            throw new FormatException("JSON: unterminated string");
        }
    }
}
