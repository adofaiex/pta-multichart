using System;
using System.Collections.Generic;
using System.Globalization;

namespace PtaMultichart.Core.Model
{
    /// <summary>
    /// 一条 track 上的一个游戏事件（actions 或 decorations）。Data 用游戏 JSON 语义：键 = LevelEvent 的字段名，
    /// 值 = GDMiniJSON 风格（long/double/bool/string/List&lt;object&gt;/Dictionary&lt;string,object&gt;）。
    /// </summary>
    public sealed class ChartEvent
    {
        public const string UnrolledTag = "pta-mc-unrolled";

        public string Type;
        public int Floor;
        public bool Active = true;
        public bool Visible = true;
        public bool Locked;
        public Dictionary<string, object> Data = new Dictionary<string, object>();

        public ChartEvent() { }

        public ChartEvent(string type, int floor)
        {
            Type = type;
            Floor = floor;
        }

        public ChartEvent Clone()
        {
            var copy = new ChartEvent(Type, Floor)
            {
                Active = Active,
                Visible = Visible,
                Locked = Locked,
                Data = new Dictionary<string, object>(Data.Count)
            };
            foreach (var kv in Data) copy.Data[kv.Key] = CloneValue(kv.Value);
            return copy;
        }

        public static object CloneValue(object v)
        {
            if (v is Dictionary<string, object> dict)
            {
                var d = new Dictionary<string, object>(dict.Count);
                foreach (var kv in dict) d[kv.Key] = CloneValue(kv.Value);
                return d;
            }
            if (v is List<object> list)
            {
                var l = new List<object>(list.Count);
                foreach (var item in list) l.Add(CloneValue(item));
                return l;
            }
            return v;
        }

        public bool Has(string key) => Data.ContainsKey(key);

        public void Set(string key, object value) => Data[key] = value;

        public object GetObject(string key) => Data.TryGetValue(key, out var v) ? v : null;

        public double GetDouble(string key, double fallback = 0)
        {
            if (!Data.TryGetValue(key, out var v) || v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public int GetInt(string key, int fallback = 0)
        {
            if (!Data.TryGetValue(key, out var v) || v == null) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public string GetString(string key, string fallback = null)
        {
            if (!Data.TryGetValue(key, out var v) || v == null) return fallback;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public bool GetBool(string key, bool fallback = false)
        {
            if (!Data.TryGetValue(key, out var v) || v == null) return fallback;
            if (v is bool b) return b;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "Enabled";
        }

        public List<object> GetList(string key)
        {
            if (!Data.TryGetValue(key, out var v) || v == null) return null;
            if (v is List<object> list) return list;
            // 游戏 EncodeVector2 / EncodeTile 写出来的是 object[]，不是 List。
            if (v is Array arr)
            {
                var copy = new List<object>(arr.Length);
                foreach (var item in arr) copy.Add(item);
                return copy;
            }
            return null;
        }

        public static List<object> Pair(double x, double y) => new List<object> { x, y };

        /// <summary>PropertyType.Tile 的 JSON 形态：[floor, "ThisTile"|"Start"|"End"]。</summary>
        public static List<object> Pair(int floor, string relativeTo) => new List<object> { floor, relativeTo };


        public override string ToString() => Type + "@" + Floor;

        // ---- JSON ----

        public Dictionary<string, object> ToJsonObject()
        {
            var d = new Dictionary<string, object>
            {
                ["type"] = Type,
                ["floor"] = Floor
            };
            if (!Active) d["active"] = false;
            if (!Visible) d["visible"] = false;
            if (Locked) d["locked"] = true;
            if (Data.Count > 0) d["data"] = Data;
            return d;
        }

        public static ChartEvent FromJsonObject(Dictionary<string, object> d)
        {
            var ev = new ChartEvent
            {
                Type = d.TryGetValue("type", out var t) ? Convert.ToString(t, CultureInfo.InvariantCulture) : null,
                Floor = d.TryGetValue("floor", out var f) ? Convert.ToInt32(f, CultureInfo.InvariantCulture) : 0,
                Active = !d.TryGetValue("active", out var a) || Convert.ToBoolean(a, CultureInfo.InvariantCulture),
                Visible = !d.TryGetValue("visible", out var v) || Convert.ToBoolean(v, CultureInfo.InvariantCulture),
                Locked = d.TryGetValue("locked", out var l) && Convert.ToBoolean(l, CultureInfo.InvariantCulture)
            };
            if (d.TryGetValue("data", out var data) && data is Dictionary<string, object> dd)
                ev.Data = dd;
            return ev;
        }
    }

    /// <summary>一条轨道（逻辑谱）。轨道 0 是主轨；1 起是副轨。</summary>
    public sealed class Track
    {
        public int Id;
        /// <summary>副轨锚点：主轨砖号（seqID）。主轨为 0。</summary>
        public int AnchorFloor;
        /// <summary>创建时从锚点抄下来的出射角（度）。</summary>
        public double ExitAngle;
        /// <summary>创建时抄下来的旋转方向（true = 逆时针）。</summary>
        public bool IsCCW;
        /// <summary>创建时抄下来的有效 BPM（绝对值）。</summary>
        public double BaseBpm = 100;

        public List<float> AngleData = new List<float>();
        public List<ChartEvent> Events = new List<ChartEvent>();
        public List<ChartEvent> Decorations = new List<ChartEvent>();

        public bool IsMain => Id == 0;

        public Track Clone()
        {
            var t = new Track
            {
                Id = Id,
                AnchorFloor = AnchorFloor,
                ExitAngle = ExitAngle,
                IsCCW = IsCCW,
                BaseBpm = BaseBpm,
                AngleData = new List<float>(AngleData)
            };
            foreach (var e in Events) t.Events.Add(e.Clone());
            foreach (var e in Decorations) t.Decorations.Add(e.Clone());
            return t;
        }

        public Dictionary<string, object> ToJsonObject()
        {
            var actions = new List<object>(Events.Count);
            foreach (var e in Events) actions.Add(e.ToJsonObject());
            var decos = new List<object>(Decorations.Count);
            foreach (var e in Decorations) decos.Add(e.ToJsonObject());

            var angles = new List<object>(AngleData.Count);
            foreach (var a in AngleData) angles.Add(a);

            return new Dictionary<string, object>
            {
                ["id"] = Id,
                ["anchor"] = AnchorFloor,
                ["exitAngle"] = ExitAngle,
                ["ccw"] = IsCCW,
                ["bpm"] = BaseBpm,
                ["angleData"] = angles,
                ["actions"] = actions,
                ["decorations"] = decos
            };
        }

        public static Track FromJsonObject(Dictionary<string, object> d)
        {
            var t = new Track
            {
                Id = d.TryGetValue("id", out var id) ? Convert.ToInt32(id, CultureInfo.InvariantCulture) : 0,
                AnchorFloor = d.TryGetValue("anchor", out var a) ? Convert.ToInt32(a, CultureInfo.InvariantCulture) : 0,
                ExitAngle = d.TryGetValue("exitAngle", out var ea) ? Convert.ToDouble(ea, CultureInfo.InvariantCulture) : 0,
                IsCCW = d.TryGetValue("ccw", out var ccw) && Convert.ToBoolean(ccw, CultureInfo.InvariantCulture),
                BaseBpm = d.TryGetValue("bpm", out var bpm) ? Convert.ToDouble(bpm, CultureInfo.InvariantCulture) : 100
            };
            if (d.TryGetValue("angleData", out var angles) && angles is List<object> al)
            {
                foreach (var v in al) t.AngleData.Add(Convert.ToSingle(v, CultureInfo.InvariantCulture));
            }
            if (d.TryGetValue("actions", out var acts) && acts is List<object> aal)
            {
                foreach (var v in aal)
                    if (v is Dictionary<string, object> od) t.Events.Add(ChartEvent.FromJsonObject(od));
            }
            if (d.TryGetValue("decorations", out var decos) && decos is List<object> dal)
            {
                foreach (var v in dal)
                    if (v is Dictionary<string, object> od) t.Decorations.Add(ChartEvent.FromJsonObject(od));
            }
            return t;
        }
    }

    /// <summary>模组的逻辑文档：全局 settings 仍在 LevelData 里，这里只放每轨的角度/事件/锚点信息。</summary>
    public sealed class LogicalChart
    {
        public const string MarkerPrefix = "[[PTA-MC:v1]]";
        public const string MarkerPrefixV0 = "[[PTA-MC]]";

        public int Version = 1;
        public List<Track> Tracks = new List<Track>();

        public Track Main => Tracks.Count > 0 ? Tracks[0] : null;

        public static LogicalChart CreateFromMain(Track main)
        {
            var chart = new LogicalChart();
            main.Id = 0;
            chart.Tracks.Add(main);
            return chart;
        }

        public Track Find(int id)
        {
            foreach (var t in Tracks) if (t.Id == id) return t;
            return null;
        }

        public int NextTrackId()
        {
            int max = 0;
            foreach (var t in Tracks) max = Math.Max(max, t.Id);
            return max + 1;
        }

        public string ToJson()
        {
            var tracks = new List<object>(Tracks.Count);
            foreach (var t in Tracks) tracks.Add(t.ToJsonObject());
            var root = new Dictionary<string, object>
            {
                ["version"] = Version,
                ["tracks"] = tracks
            };
            return MiniJson.Serialize(root);
        }

        public string ToComment()
        {
            return MarkerPrefix + "\n" + ToJson();
        }

        public static bool IsMarkerComment(string comment)
        {
            return comment != null &&
                   (comment.StartsWith(MarkerPrefix, StringComparison.Ordinal) ||
                    comment.StartsWith(MarkerPrefixV0, StringComparison.Ordinal));
        }

        public static string StripMarker(string comment)
        {
            if (comment == null) return null;
            if (comment.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                return comment.Substring(MarkerPrefix.Length).TrimStart('\r', '\n');
            if (comment.StartsWith(MarkerPrefixV0, StringComparison.Ordinal))
                return comment.Substring(MarkerPrefixV0.Length).TrimStart('\r', '\n');
            return comment;
        }

        public static LogicalChart FromJson(string json)
        {
            if (!(MiniJson.Deserialize(json) is Dictionary<string, object> root))
                throw new FormatException("逻辑谱 JSON 不是对象");
            var chart = new LogicalChart();
            if (root.TryGetValue("version", out var v)) chart.Version = Convert.ToInt32(v, CultureInfo.InvariantCulture);
            if (root.TryGetValue("tracks", out var ts) && ts is List<object> list)
            {
                foreach (var item in list)
                    if (item is Dictionary<string, object> od) chart.Tracks.Add(Track.FromJsonObject(od));
            }
            if (chart.Tracks.Count == 0) throw new FormatException("逻辑谱没有轨道");
            return chart;
        }

        public LogicalChart Clone()
        {
            var c = new LogicalChart { Version = Version };
            foreach (var t in Tracks) c.Tracks.Add(t.Clone());
            return c;
        }
    }

    /// <summary>编译时用到的全局轨道样式（来自 LevelData 的 TrackSettings，不由逻辑谱保存）。</summary>
    public sealed class CompileSettings
    {
        public double Bpm = 100;
        public string TrackStyle = "Standard";
        public string TrackColor = "debb7b";
        public string SecondaryTrackColor = "ffffff";
        public string TrackColorType = "Single";
        public double TrackGlowIntensity = 100;
        public double TrackColorAnimDuration = 2;
        public string TrackColorPulse = "None";
        public int TrackPulseLength = 10;
        public string TileShape = "Long";
        public bool FloorIconOutlines;
        /// <summary>副轨/遮盖装饰的 depth。-1 = 默认层（和地板同层）。</summary>
        public int DecorationDepth = -1;
        /// <summary>主轨与副轨点击时间差小于这个秒数就算重叠，拒绝编译（方案 §12 的「半个判定窗」）。</summary>
        public double MinClickGapSeconds = 0.05;
    }
}
