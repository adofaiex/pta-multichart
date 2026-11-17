using System;
using System.Collections.Generic;
using System.Globalization;
using ADOFAI;
using PtaMultichart.Core.Model;
using UnityEngine;

namespace PtaMultichart.Game
{
    /// <summary>游戏 LevelEvent ⇄ 逻辑谱 ChartEvent 的转换。</summary>
    public static class EventFactory
    {
        private static readonly HashSet<string> Reserved = new HashSet<string>
        {
            "floor", "eventType", "active", "visible", "locked"
        };

        public static ChartEvent ToModel(LevelEvent ev)
        {
            if (ev == null) return null;
            var model = new ChartEvent(ev.eventType.ToString(), ev.floor)
            {
                Active = ev.active,
                Visible = ev.visible,
                Locked = ev.locked
            };

            // 不用 Encode()。它只要有一个字段的运行时类型对不上（Specified cast is not valid），
            // 整条事件的数据都会丢掉，PositionTrack 的偏移回到编辑器就变成关。
            var props = ev.info != null ? ev.info.propertiesInfo : null;
            if (props == null) return model;
            foreach (var kv in props)
            {
                string key = kv.Key;
                if (Reserved.Contains(key) || kv.Value == null || !kv.Value.encode) continue;
                bool disabled = false;
                if (ev.disabled != null && ev.disabled.TryGetValue(key, out var flag)) disabled = flag;
                if (disabled && kv.Value.canBeDisabled) continue;
                object raw;
                try { raw = ev[key]; }
                catch { continue; }
                if (raw == null) continue;
                try
                {
                    model.Data[key] = Normalize(kv.Value, raw);
                }
                catch (Exception ex)
                {
                    Main.Log("EventFactory.ToModel: field '" + key + "' of " + ev.eventType +
                             " (" + raw.GetType().FullName + ") failed: " + ex.Message);
                }
            }
            return model;
        }

        private static object Normalize(PropertyInfo pi, object raw)
        {
            switch (pi.type)
            {
                case PropertyType.Vector2:
                    Vector2 v = ToVector2(raw);
                    return new List<object> { (double)v.x, (double)v.y };
                case PropertyType.Enum:
                    return raw.ToString();
                case PropertyType.Tile:
                    return NormalizeTile(raw);
                case PropertyType.Float:
                    return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                case PropertyType.Int:
                case PropertyType.Rating:
                    return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                case PropertyType.Bool:
                    return raw is bool b ? b : string.Equals(Convert.ToString(raw), "true", StringComparison.OrdinalIgnoreCase);
                case PropertyType.Color:
                    return raw is Color c ? ColorUtility.ToHtmlStringRGBA(c) : Convert.ToString(raw, CultureInfo.InvariantCulture);
                case PropertyType.FloatPair:
                    return NormalizeFloatPair(raw);
                default:
                    if (raw is Array arr) return BoxArray(arr);
                    return raw;
            }
        }

        private static List<object> NormalizeTile(object raw)
        {
            if (raw is Tuple<int, TileRelativeTo> tile)
                return new List<object> { tile.Item1, tile.Item2.ToString() };
            var list = AsList(raw);
            if (list != null && list.Count >= 2)
                return new List<object> { Convert.ToInt32(list[0], CultureInfo.InvariantCulture), Convert.ToString(list[1], CultureInfo.InvariantCulture) };
            return new List<object> { 0, "ThisTile" };
        }

        private static List<object> NormalizeFloatPair(object raw)
        {
            if (raw is Tuple<float, float> pair)
                return new List<object> { (double)pair.Item1, (double)pair.Item2 };
            var list = AsList(raw);
            if (list != null && list.Count >= 2)
                return new List<object> { ToDouble(list[0]), ToDouble(list[1]) };
            return new List<object> { 0d, 0d };
        }

        private static List<object> AsList(object raw)
        {
            if (raw is List<object> list) return list;
            if (raw is Array arr) return BoxArray(arr);
            return null;
        }

        private static List<object> BoxArray(Array arr)
        {
            var copy = new List<object>(arr.Length);
            foreach (var item in arr) copy.Add(item);
            return copy;
        }

        private static double ToDouble(object value)
        {
            if (value == null) return 0;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        public static LevelEvent ToGame(ChartEvent model)
        {
            if (model == null) return null;
            if (!Enum.TryParse(model.Type, out LevelEventType type))
                throw new ArgumentException("未知事件类型: " + model.Type);

            var ev = new LevelEvent(model.Floor, type)
            {
                active = model.Active,
                visible = model.Visible,
                locked = model.Locked
            };

            foreach (var kv in model.Data)
            {
                var props = ev.info != null ? ev.info.propertiesInfo : null;
                if (props != null && props.TryGetValue(kv.Key, out var pi))
                {
                    try
                    {
                        ev[kv.Key] = ConvertValue(pi, kv.Value);
                        ev.disabled[kv.Key] = false;
                    }
                    catch (Exception ex)
                    {
                        Main.Log("EventFactory.ToGame: field '" + kv.Key + "' of " + model.Type + " failed: " + ex.Message);
                    }
                }
                else
                {
                    ev[kv.Key] = kv.Value;
                }
            }
            return ev;
        }

        private static object ConvertValue(PropertyInfo pi, object value)
        {
            if (value == null) return null;
            switch (pi.type)
            {
                case PropertyType.Enum:
                    if (value is string s)
                        return Enum.Parse(pi.enumType, s);
                    return Enum.ToObject(pi.enumType, Convert.ToInt32(value));

                case PropertyType.Vector2:
                    return ToVector2(value);

                case PropertyType.Vector2Range:
                {
                    var list = value as List<object>;
                    if (list != null && list.Count == 2)
                        return new Tuple<Vector2, Vector2>(ToVector2(list[0]), ToVector2(list[1]));
                    return value;
                }

                case PropertyType.Tile:
                {
                    var list = AsList(value);
                    if (list != null && list.Count >= 2)
                    {
                        int floor = Convert.ToInt32(list[0], CultureInfo.InvariantCulture);
                        var rel = (TileRelativeTo)Enum.Parse(typeof(TileRelativeTo), Convert.ToString(list[1], CultureInfo.InvariantCulture));
                        return new Tuple<int, TileRelativeTo>(floor, rel);
                    }
                    return new Tuple<int, TileRelativeTo>(0, TileRelativeTo.ThisTile);
                }

                case PropertyType.FloatPair:
                {
                    var list = value as List<object>;
                    if (list != null && list.Count >= 2)
                    {
                        float a = list[0] == null ? float.NaN : Convert.ToSingle(list[0]);
                        float b = list[1] == null ? float.NaN : Convert.ToSingle(list[1]);
                        return new Tuple<float, float>(a, b);
                    }
                    return value;
                }

                case PropertyType.Bool:
                    return value is bool boolValue ? boolValue : string.Equals(Convert.ToString(value), "true", StringComparison.OrdinalIgnoreCase);

                case PropertyType.Int:
                case PropertyType.Rating:
                    return Convert.ToInt32(value);

                case PropertyType.Float:
                    return Convert.ToSingle(value);

                case PropertyType.Color:
                case PropertyType.String:
                case PropertyType.LongString:
                case PropertyType.File:
                    return Convert.ToString(value);

                default:
                    return value;
            }
        }

        public static Vector2 ToVector2(object value)
        {
            if (value is Vector2 v) return v;
            if (value is Vector3 v3) return new Vector2(v3.x, v3.y);
            if (value is Array arr && arr.Length >= 2)
                return new Vector2(ToFloat(arr.GetValue(0)), ToFloat(arr.GetValue(1)));
            if (value is List<object> list && list.Count >= 2)
                return new Vector2(ToFloat(list[0]), ToFloat(list[1]));
            if (value != null)
            {
                var type = value.GetType();
                var fx = type.GetField("x") ?? type.GetField("X");
                var fy = type.GetField("y") ?? type.GetField("Y");
                if (fx != null && fy != null)
                    return new Vector2(ToFloat(fx.GetValue(value)), ToFloat(fy.GetValue(value)));
            }
            return Vector2.zero;
        }

        private static float ToFloat(object value)
        {
            if (value == null) return float.NaN;
            return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

