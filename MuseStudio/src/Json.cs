using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MuseStudio
{
    // A small JSON reader / writer (the bridge's packages, state and events; MuseStudio's data files). Objects read as
    // Dictionary<string, object>, arrays as List<object>, numbers as double, plus string / bool / null.
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Reader(text);
            p.Space();
            var v = p.Value();
            p.Space();
            if (p.i != text.Length) throw new FormatException("JSON: trailing text at " + p.i);
            return v;
        }

        public static string Write(object v)
        {
            var sb = new StringBuilder();
            WriteValue(sb, v);
            return sb.ToString();
        }

        class Reader
        {
            readonly string s;
            public int i;
            public Reader(string text) { s = text; }

            public void Space() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

            public object Value()
            {
                if (i >= s.Length) throw new FormatException("JSON: unexpected end");
                char c = s[i];
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == 't' && Word("true")) return true;
                if (c == 'f' && Word("false")) return false;
                if (c == 'n' && Word("null")) return null;
                return Num();
            }

            bool Word(string w)
            {
                if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) throw new FormatException("JSON: bad literal at " + i);
                i += w.Length;
                return true;
            }

            Dictionary<string, object> Obj()
            {
                var d = new Dictionary<string, object>();
                i++;
                Space();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Space();
                    if (i >= s.Length || s[i] != '"') throw new FormatException("JSON: key expected at " + i);
                    string k = Str();
                    Space();
                    if (i >= s.Length || s[i] != ':') throw new FormatException("JSON: ':' expected at " + i);
                    i++;
                    Space();
                    d[k] = Value();
                    Space();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("JSON: ',' or '}' expected at " + i);
                }
            }

            List<object> Arr()
            {
                var a = new List<object>();
                i++;
                Space();
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    Space();
                    a.Add(Value());
                    Space();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return a; }
                    throw new FormatException("JSON: ',' or ']' expected at " + i);
                }
            }

            string Str()
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("JSON: bad \\u escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                            i += 4;
                            break;
                        default: throw new FormatException("JSON: bad escape at " + i);
                    }
                }
                throw new FormatException("JSON: unterminated string");
            }

            object Num()
            {
                int st = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (st == i) throw new FormatException("JSON: unexpected '" + s[st] + "' at " + st);
                return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double || v is float)
            {
                double d = Convert.ToDouble(v);
                sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is int || v is long || v is short || v is byte || v is uint) { sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return; }
            var dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, e.Value);
                }
                sb.Append('}');
                return;
            }
            var list = v as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var x in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, x);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, v.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // Typed reads of parsed values (missing or wrong type: the fallback).
        public static Dictionary<string, object> Obj(object v, string key)
        {
            var d = v as Dictionary<string, object>;
            object x;
            return d != null && d.TryGetValue(key, out x) ? x as Dictionary<string, object> : null;
        }

        public static List<object> Arr(object v, string key)
        {
            var d = v as Dictionary<string, object>;
            object x;
            return d != null && d.TryGetValue(key, out x) ? x as List<object> : null;
        }

        public static string Str(object v, string key, string fallback = null)
        {
            var d = v as Dictionary<string, object>;
            object x;
            return d != null && d.TryGetValue(key, out x) && x is string ? (string)x : fallback;
        }

        public static double Num(object v, string key, double fallback = 0)
        {
            var d = v as Dictionary<string, object>;
            object x;
            return d != null && d.TryGetValue(key, out x) && x is double ? (double)x : fallback;
        }

        public static bool Has(object v, string key)
        {
            var d = v as Dictionary<string, object>;
            object x;
            return d != null && d.TryGetValue(key, out x) && x != null;
        }
    }
}
