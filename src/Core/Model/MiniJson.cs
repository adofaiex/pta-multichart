using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PtaMultichart.Core.Model
{
    /// <summary>
    /// 极小的 JSON 读写器。写：支持 null/bool/整数/浮点/string/IDictionary&lt;string,object&gt;/IEnumerable。
    /// 读：整数给 long，小数给 double，和游戏的 GDMiniJSON 一致，便于 LevelEvent.Decode 直接吃。
    /// 不依赖 Unity，也不依赖 System.Text.Json（模组里两边都能用）。
    /// </summary>
    public static class MiniJson
    {
        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null)
            {
                sb.Append("null");
                return;
            }

            if (v is string s)
            {
                WriteString(sb, s);
                return;
            }

            if (v is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }

            if (v is char c)
            {
                WriteString(sb, c.ToString());
                return;
            }

            if (v is IDictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            if (v is IDictionary d2)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in d2)
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

            switch (v)
            {
                case float f:
                    if (float.IsNaN(f) || float.IsInfinity(f)) sb.Append("null");
                    else sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case double dd:
                    if (double.IsNaN(dd) || double.IsInfinity(dd)) sb.Append("null");
                    else sb.Append(dd.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return;
                case sbyte _:
                case byte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
            }

            if (v is IEnumerable list)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
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
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
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

        public static object Deserialize(string json)
        {
            if (json == null) return null;
            var p = new Parser(json);
            object v = p.ParseValue();
            p.SkipWhite();
            return v;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s)
            {
                _s = s;
                _i = 0;
            }

            public void SkipWhite()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private void Expect(char c)
            {
                SkipWhite();
                if (_i >= _s.Length || _s[_i] != c)
                    throw new FormatException("JSON: expected '" + c + "' at " + _i);
                _i++;
            }

            public object ParseValue()
            {
                SkipWhite();
                if (_i >= _s.Length) throw new FormatException("JSON: unexpected end");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't':
                        ExpectWord("true");
                        return true;
                    case 'f':
                        ExpectWord("false");
                        return false;
                    case 'n':
                        ExpectWord("null");
                        return null;
                    default:
                        return ParseNumber();
                }
            }

            private void ExpectWord(string w)
            {
                if (_i + w.Length > _s.Length || string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0)
                    throw new FormatException("JSON: expected '" + w + "' at " + _i);
                _i += w.Length;
            }

            private Dictionary<string, object> ParseObject()
            {
                Expect('{');
                var dict = new Dictionary<string, object>();
                SkipWhite();
                if (_i < _s.Length && _s[_i] == '}')
                {
                    _i++;
                    return dict;
                }

                while (true)
                {
                    SkipWhite();
                    string key = ParseString();
                    Expect(':');
                    dict[key] = ParseValue();
                    SkipWhite();
                    if (_i < _s.Length && _s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }
                    Expect('}');
                    return dict;
                }
            }

            private List<object> ParseArray()
            {
                Expect('[');
                var list = new List<object>();
                SkipWhite();
                if (_i < _s.Length && _s[_i] == ']')
                {
                    _i++;
                    return list;
                }

                while (true)
                {
                    list.Add(ParseValue());
                    SkipWhite();
                    if (_i < _s.Length && _s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }
                    Expect(']');
                    return list;
                }
            }

            private string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (_i >= _s.Length) break;
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
                            if (_i + 4 <= _s.Length)
                            {
                                sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16));
                                _i += 4;
                            }
                            break;
                        default:
                            sb.Append(e);
                            break;
                    }
                }
                throw new FormatException("JSON: unterminated string");
            }

            private object ParseNumber()
            {
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                bool isFloat = false;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c >= '0' && c <= '9')
                    {
                        _i++;
                    }
                    else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                    {
                        isFloat = isFloat || c == '.' || c == 'e' || c == 'E';
                        _i++;
                    }
                    else
                    {
                        break;
                    }
                }

                string text = _s.Substring(start, _i - start);
                if (!isFloat && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    return l;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return d;
                throw new FormatException("JSON: bad number '" + text + "'");
            }
        }
    }
}
