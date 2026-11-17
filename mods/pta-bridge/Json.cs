using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PtaBridge
{
    /// <summary>
    /// 极简 JSON 读写（net481 里没有 System.Text.Json，也不依赖 Newtonsoft）。
    /// 解析结果类型：Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / long / double / bool / null。
    /// </summary>
    internal static class Json
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------- 构造辅助 ----------

        /// <summary>Json.Obj("a", 1, "b", "x") → 有序字典（.NET 的 Dictionary 保持插入顺序）。</summary>
        public static Dictionary<string, object> Obj(params object[] keyValues)
        {
            var d = new Dictionary<string, object>();
            if (keyValues == null) return d;
            for (int i = 0; i + 1 < keyValues.Length; i += 2)
            {
                string k = keyValues[i] as string;
                if (k == null) continue;
                d[k] = keyValues[i + 1];
            }
            return d;
        }

        // ---------- 取值辅助（args） ----------

        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null || key == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(Dictionary<string, object> d, string key, string fallback = null)
        {
            object v = Get(d, key);
            if (v == null) return fallback;
            string s = v as string;
            if (s != null) return s;
            if (v is bool) return ((bool)v) ? "true" : "false";
            return Convert.ToString(v, Inv);
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v = Get(d, key);
            if (v == null) return fallback;
            if (v is bool) return (bool)v;
            string s = v as string;
            if (s != null)
            {
                bool b;
                if (bool.TryParse(s, out b)) return b;
                int i;
                if (int.TryParse(s, NumberStyles.Integer, Inv, out i)) return i != 0;
                return fallback;
            }
            try { return Convert.ToBoolean(v, Inv); } catch { return fallback; }
        }

        public static double Double(Dictionary<string, object> d, string key, double fallback)
        {
            object v = Get(d, key);
            if (v == null) return fallback;
            if (v is double) return (double)v;
            if (v is long) return (long)v;
            if (v is bool) return ((bool)v) ? 1 : 0;
            try { return Convert.ToDouble(v, Inv); } catch { return fallback; }
        }

        public static int Int(Dictionary<string, object> d, string key, int fallback)
        {
            object v = Get(d, key);
            if (v == null) return fallback;
            if (v is long) return (int)(long)v;
            if (v is double) return (int)Math.Round((double)v);
            if (v is bool) return ((bool)v) ? 1 : 0;
            try { return Convert.ToInt32(v, Inv); } catch { return fallback; }
        }

        public static List<object> Array(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            var list = v as List<object>;
            if (list != null) return list;
            if (v is IList && !(v is string))
            {
                var res = new List<object>();
                foreach (object o in (IList)v) res.Add(o);
                return res;
            }
            return null;
        }

        // ---------- 解析 ----------

        public static object Parse(string text)
        {
            var p = new JsonParser(text ?? string.Empty);
            p.SkipWhitespace();
            object v = p.ParseValue();
            p.SkipWhitespace();
            if (!p.End) throw new FormatException("trailing characters at offset " + p.Position);
            return v;
        }

        // ---------- 序列化 ----------

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(512);
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }

            string s = v as string;
            if (s != null) { WriteString(sb, s); return; }

            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }

            if (v is double) { sb.Append(Number((double)v)); return; }
            if (v is float) { sb.Append(Number((float)v)); return; }
            if (v is decimal) { sb.Append(((decimal)v).ToString(Inv)); return; }
            if (v is long) { sb.Append(((long)v).ToString(Inv)); return; }
            if (v is int) { sb.Append(((int)v).ToString(Inv)); return; }
            if (v is short) { sb.Append(((short)v).ToString(Inv)); return; }
            if (v is sbyte) { sb.Append(((sbyte)v).ToString(Inv)); return; }
            if (v is byte) { sb.Append(((byte)v).ToString(Inv)); return; }
            if (v is ushort) { sb.Append(((ushort)v).ToString(Inv)); return; }
            if (v is uint) { sb.Append(((uint)v).ToString(Inv)); return; }
            if (v is ulong) { sb.Append(((ulong)v).ToString(Inv)); return; }
            if (v is char) { WriteString(sb, v.ToString()); return; }

            var map = v as IDictionary;
            if (map != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, Inv) ?? string.Empty);
                    sb.Append(':');
                    Write(sb, e.Value);
                }
                sb.Append('}');
                return;
            }

            var seq = v as IEnumerable;
            if (seq != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object o in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, o);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, v.ToString());
        }

        private static string Number(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            if (d == Math.Floor(d) && Math.Abs(d) < 9.0e18) return ((long)d).ToString(Inv);
            return d.ToString("R", Inv);
        }

        private static string Number(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return "null";
            if (f == Math.Floor(f) && Math.Abs(f) < 9.0e15f) return ((long)f).ToString(Inv);
            return f.ToString("R", Inv);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", Inv));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- 递归下降解析器 ----------

        private sealed class JsonParser
        {
            private readonly string _s;
            private int _i;

            public JsonParser(string s) { _s = s; _i = 0; }

            public int Position { get { return _i; } }
            public bool End { get { return _i >= _s.Length; } }

            public void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                    else break;
                }
            }

            public object ParseValue()
            {
                if (End) throw new FormatException("unexpected end of JSON");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't':
                        Expect("true"); return true;
                    case 'f':
                        Expect("false"); return false;
                    case 'n':
                        Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw new FormatException("unexpected character '" + c + "' at offset " + _i);
                }
            }

            private void Expect(string literal)
            {
                if (_i + literal.Length > _s.Length ||
                    string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                {
                    throw new FormatException("expected '" + literal + "' at offset " + _i);
                }
                _i += literal.Length;
            }

            private Dictionary<string, object> ParseObject()
            {
                var d = new Dictionary<string, object>();
                _i++; // {
                SkipWhitespace();
                if (!End && _s[_i] == '}') { _i++; return d; }
                while (true)
                {
                    SkipWhitespace();
                    if (End || _s[_i] != '"') throw new FormatException("expected object key at offset " + _i);
                    string key = ParseString();
                    SkipWhitespace();
                    if (End || _s[_i] != ':') throw new FormatException("expected ':' at offset " + _i);
                    _i++;
                    SkipWhitespace();
                    d[key] = ParseValue();
                    SkipWhitespace();
                    if (End) throw new FormatException("unterminated object");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return d; }
                    throw new FormatException("expected ',' or '}' at offset " + _i);
                }
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                _i++; // [
                SkipWhitespace();
                if (!End && _s[_i] == ']') { _i++; return list; }
                while (true)
                {
                    SkipWhitespace();
                    list.Add(ParseValue());
                    SkipWhitespace();
                    if (End) throw new FormatException("unterminated array");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return list; }
                    throw new FormatException("expected ',' or ']' at offset " + _i);
                }
            }

            private string ParseString()
            {
                _i++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw new FormatException("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (End) throw new FormatException("unterminated escape");
                    char e = _s[_i++];
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
                            if (_i + 4 > _s.Length) throw new FormatException("bad \\u escape");
                            sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16));
                            _i += 4;
                            break;
                        default:
                            throw new FormatException("bad escape '\\" + e + "' at offset " + (_i - 1));
                    }
                }
            }

            private object ParseNumber()
            {
                int start = _i;
                if (!End && _s[_i] == '-') _i++;
                while (!End && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                bool isFloat = false;
                if (!End && _s[_i] == '.')
                {
                    isFloat = true;
                    _i++;
                    while (!End && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                }
                if (!End && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    isFloat = true;
                    _i++;
                    if (!End && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    while (!End && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                }
                string text = _s.Substring(start, _i - start);
                if (!isFloat)
                {
                    long l;
                    if (long.TryParse(text, NumberStyles.Integer, Inv, out l)) return l;
                }
                double d;
                if (double.TryParse(text, NumberStyles.Float, Inv, out d)) return d;
                throw new FormatException("bad number '" + text + "'");
            }
        }
    }
}
