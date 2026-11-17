using System;
using System.Collections.Generic;
using System.Globalization;
using PtaMultichart.Core.Model;
using PtaMultichart.Core.Timing;

namespace PtaMultichart.Core.Compile
{
    /// <summary>
    /// 播放谱里的装饰物 / 装饰动画。字段名以 r150 的 scrDecoration.Setup（AddObject）和
    /// ffxMoveDecorationsPlus.Decode（MoveDecorations）为准。
    /// 所有生成物 tag 以 pta-mc 开头；副轨再加 pta-mc-tN；主轨遮盖用 pta-mc-mask。
    /// </summary>
    public static class DecorationBuilder
    {
        public const string TagRoot = "pta-mc";
        public const string TagMask = "pta-mc-mask";
        public const string TrackTagPrefix = "pta-mc-t";
        public const string PlanetRedSuffix = "-red";
        public const string PlanetBlueSuffix = "-blue";

        // r150 的枚举名，写错游戏会 Enum.Parse 失败整条事件丢掉。
        private static readonly string[] FloorColorTypes = { "Single", "Glow", "Blink", "Switch", "Rainbow", "Volume" };
        private static readonly string[] TrackStyles = { "Standard", "Neon", "NeonLight", "Basic", "Gems", "Minimal" };
        // CustomFloorIcon 的名字。写了别的，AddObject 解码时 Enum.Parse 会把整块装饰丢掉。
        private static readonly string[] FloorIcons =
        {
            "None", "Snail", "DoubleSnail", "Rabbit", "DoubleRabbit", "Swirl", "Checkpoint",
            "HoldArrowShort", "HoldArrowLong", "HoldReleaseShort", "HoldReleaseLong",
            "MultiPlanetTwo", "MultiPlanetThreeMore", "Portal"
        };

        /// <summary>副轨标签：pta-mc-tN。</summary>
        public static string TrackTag(int trackId)
        {
            return TrackTagPrefix + trackId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>主轨被切开的那块砖：用原来的入口角和出口角盖一块一样的砖。</summary>
        public static ChartEvent Mask(CompileSettings settings, Vec2 position, double entryRad, double exitRad)
        {
            ChartEvent ev = FloorObject(settings, position, entryRad, exitRad, false);
            ev.Data["tag"] = TagRoot + " " + TagMask;
            return ev;
        }

        /// <summary>
        /// Floor 装饰的网格：一边钉在 -180°，开口是 trackAngle，再用 rotation 转到入口方向。
        /// 和 scrFloor.UpdateAngle 的两条边对齐，砖的形状才和真轨道一样。
        /// </summary>
        public static void FloorPose(double entryRad, double exitRad, out double trackAngle, out double rotation)
        {
            double entryDeg = entryRad * 180.0 / Math.PI;
            double exitDeg = exitRad * 180.0 / Math.PI;
            trackAngle = Norm360(exitDeg - entryDeg);
            if (trackAngle < 1e-3) trackAngle = 360.0;
            rotation = NormSigned(270.0 - entryDeg);
        }

        private static double Norm360(double deg)
        {
            deg %= 360.0;
            if (deg < 0) deg += 360.0;
            return deg;
        }

        private static double NormSigned(double deg)
        {
            deg = Norm360(deg);
            if (deg > 180.0) deg -= 360.0;
            return deg;
        }

        /// <summary>
        /// 一条副轨的装饰：从 1 号砖起每块砖一个 Floor（0 号砖是幽灵起点，不画）+ 起点两颗 Planet。
        /// world[i] 是副轨 i 号砖的世界坐标（单位砖长，1 号砖 = 锚点砖）。
        /// </summary>
        public static void AddTrackVisuals(List<ChartEvent> decorations, Track track, TrackTimeline timeline,
            Vec2[] world, CompileSettings settings)
        {
            if (decorations == null || track == null || timeline == null || world == null) return;

            string trackTag = TrackTag(track.Id);
            string floorTags = TagRoot + " " + trackTag;

            for (int i = 1; i < timeline.Count && i < world.Length; i++)
            {
                FloorTime f = timeline[i];
                ChartEvent ev = FloorObject(settings, world[i], f.EntryAngle, f.ExitAngle, f.MidSpin);
                ApplyFloorIcon(ev, track, timeline, i);
                ev.Data["tag"] = floorTags;
                decorations.Add(ev);
            }

            Vec2 start = world.Length > 1 ? world[1] : new Vec2(0, 0);
            FloorTime home = timeline.Count > 1 ? timeline[1] : null;
            Vec2 blueOff = home == null ? new Vec2(0, 0) : TimelineBuilder.VectorFromAngle(home.EntryAngle);
            decorations.Add(PlanetObject(settings, start, new Vec2(0, 0), "DefaultRed",
                floorTags + " " + trackTag + PlanetRedSuffix));
            decorations.Add(PlanetObject(settings, start, blueOff, "DefaultBlue",
                floorTags + " " + trackTag + PlanetBlueSuffix));
        }

        /// <summary>
        /// 副轨上作者自己摆的装饰（方案 §5）：烤成 relativeTo = Global 的世界坐标，floor 归 0，
        /// 不再引用副轨砖号。relativeTo 已经是 Global 的只清 floor 引用。
        /// </summary>
        public static void BakeSubDecorations(List<ChartEvent> decorations, Track track, Vec2[] world)
        {
            if (decorations == null || track == null || track.Decorations == null) return;

            foreach (ChartEvent src in track.Decorations)
            {
                if (src == null) continue;
                ChartEvent copy = src.Clone();
                copy.Floor = 0;

                string relativeTo = copy.GetString("relativeTo", "Tile");
                if (relativeTo != "Global")
                {
                    Vec2 basePos = Vec2At(world, src.Floor);
                    Vec2 local = ReadVector2(copy, "position");
                    copy.Data["position"] = ChartEvent.Pair(basePos.X + local.X, basePos.Y + local.Y);
                    copy.Data["relativeTo"] = "Global";
                }
                decorations.Add(copy);
            }
        }

        /// <summary>AddObject + objectType=Floor。position 单位是砖长，relativeTo=Global。</summary>
        public static ChartEvent FloorObject(CompileSettings settings, Vec2 position, double entryRad, double exitRad, bool midSpin)
        {
            if (settings == null) settings = new CompileSettings();
            double trackAngle, rotation;
            FloorPose(entryRad, exitRad, out trackAngle, out rotation);

            var ev = new ChartEvent("AddObject", 0);
            ev.Data["objectType"] = "Floor";
            ev.Data["relativeTo"] = "Global";
            ev.Data["position"] = ChartEvent.Pair(position.X, position.Y);
            ev.Data["trackType"] = midSpin ? "Midspin" : "Normal";
            ev.Data["trackAngle"] = trackAngle;
            ev.Data["trackColor"] = Hex(settings.TrackColor, "debb7b");
            ev.Data["secondaryTrackColor"] = Hex(settings.SecondaryTrackColor, "ffffff");
            ev.Data["trackColorType"] = Pick(settings.TrackColorType, FloorColorTypes, "Single");
            ev.Data["trackColorAnimDuration"] = settings.TrackColorAnimDuration;
            ev.Data["trackOpacity"] = 100.0;
            ev.Data["trackStyle"] = Pick(settings.TrackStyle, TrackStyles, "Standard");
            // 装饰砖不单独开辉光。关卡自己的辉光只作用在真砖上，这里再开会叠出一圈。
            ev.Data["trackGlowEnabled"] = false;
            ev.Data["trackGlowColor"] = Hex(settings.TrackColor, "debb7b");
            ev.Data["trackIcon"] = "None";
            ev.Data["trackIconAngle"] = 0.0;
            ev.Data["trackIconFlipped"] = false;
            ev.Data["trackRedSwirl"] = false;
            ev.Data["trackGraySetSpeedIcon"] = false;
            ev.Data["trackSetSpeedIconBpm"] = settings.Bpm > 0 ? settings.Bpm : 100.0;
            ev.Data["trackIconOutlines"] = settings.FloorIconOutlines;
            ev.Data["scale"] = ChartEvent.Pair(100.0, 100.0);
            ev.Data["rotation"] = rotation;
            ev.Data["depth"] = settings.DecorationDepth;
            return ev;
        }

        /// <summary>AddObject + objectType=Planet。pivot 在当前砖心，pivotOffset 是星球相对砖心的位置。</summary>
        public static ChartEvent PlanetObject(CompileSettings settings, Vec2 center, Vec2 orbitOffset, string colorType, string tag)
        {
            if (settings == null) settings = new CompileSettings();

            var ev = new ChartEvent("AddObject", 0);
            ev.Data["objectType"] = "Planet";
            ev.Data["planetColorType"] = colorType;
            ev.Data["planetColor"] = "ffffff";
            ev.Data["planetTailColor"] = "ffffff";
            ev.Data["relativeTo"] = "Global";
            ev.Data["position"] = ChartEvent.Pair(center.X, center.Y);
            ev.Data["pivotOffset"] = ChartEvent.Pair(orbitOffset.X, orbitOffset.Y);
            ev.Data["rotation"] = 0.0;
            ev.Data["scale"] = ChartEvent.Pair(100.0, 100.0);
            ev.Data["depth"] = settings.DecorationDepth;
            ev.Data["tag"] = tag;
            return ev;
        }

        /// <summary>
        /// 让装饰星球绕砖心转。positionOffset 把轴心放到这块砖，pivotOffset 是转之前的半径，
        /// rotationOffset 是转到出口时的角度（度）。duration 为 0 时立刻到位。
        /// </summary>
        public static ChartEvent OrbitMove(int floor, string tag, double durationBeats,
            double centerX, double centerY, double radiusX, double radiusY, double rotationDeg)
        {
            var ev = new ChartEvent("MoveDecorations", floor);
            ev.Data["duration"] = durationBeats;
            ev.Data["positionOffset"] = ChartEvent.Pair(centerX, centerY);
            ev.Data["pivotOffset"] = ChartEvent.Pair(radiusX, radiusY);
            ev.Data["rotationOffset"] = rotationDeg;
            ev.Data["ease"] = "Linear";
            ev.Data["tag"] = tag;
            return ev;
        }

        /// <summary>
        /// 这块砖该显示的图标名（CustomFloorIcon）。编辑器里的其它轨道和游玩时的装饰砖共用。
        /// 旋转、变速、长按、检查点、双行星。暂停时才出现的特效图标不选。
        /// </summary>
        public static string ChooseFloorIcon(Track track, TrackTimeline timeline, int index)
        {
            if (track == null || timeline == null || index < 0 || index >= timeline.Count) return "None";
            FloorTime floor = timeline[index];
            FloorTime prev = index > 0 ? timeline[index - 1] : null;
            var here = new List<ChartEvent>();
            var prevSpeed = new List<ChartEvent>();
            if (track.Events != null)
            {
                foreach (ChartEvent ev in track.Events)
                {
                    if (ev == null || !ev.Active) continue;
                    if (ev.Floor == index) here.Add(ev);
                    else if (prev != null && ev.Floor == index - 1 && ev.Type == "SetSpeed") prevSpeed.Add(ev);
                }
            }

            string icon = "None";
            ChartEvent custom = null;
            foreach (ChartEvent ev in here)
            {
                if (ev.Type == "SetFloorIcon") { custom = ev; break; }
            }

            if (custom != null)
            {
                icon = Pick(custom.GetString("icon", "None"), FloorIcons, "None");
            }
            else
            {
                int rank = 0;
                var list = new List<ChartEvent>(here);
                list.AddRange(prevSpeed);
                foreach (ChartEvent item in list)
                {
                    string type = item.Type;
                    if (type == "Checkpoint")
                    {
                        if (rank >= 1) continue;
                        rank = 1;
                        icon = "Checkpoint";
                    }
                    else if (type == "SetSpeed")
                    {
                        if (rank >= 2) continue;
                        rank = 2;
                        double before = prev == null ? 1.0 : prev.Speed;
                        if (Math.Abs(before) < 1e-9) before = 1.0;
                        double rel = (floor.Speed - before) / before;
                        double abs = Math.Abs(rel);
                        if (abs > 0.05)
                        {
                            if (rel > 0) icon = abs < 1.05 ? "Rabbit" : "DoubleRabbit";
                            else icon = (1.0 - abs > 0.45) ? "Snail" : "DoubleSnail";
                        }
                        else if (item.Floor == index)
                        {
                            icon = "None";
                            rank = 0;
                        }
                    }
                    else if (type == "Twirl")
                    {
                        if (rank >= 2) continue;
                        rank = 2;
                        icon = "Swirl";
                    }
                    else if (type == "Hold")
                    {
                        if (rank >= 2) continue;
                        icon = floor.HoldBeats != 0 ? "HoldArrowLong" : "HoldArrowShort";
                    }
                    else if (type == "MultiPlanet")
                    {
                        if (rank >= 2) continue;
                        int planets = item.GetInt("planets", 2);
                        if (planets == 2) icon = "MultiPlanetTwo";
                        else if (planets > 2) icon = "MultiPlanetThreeMore";
                    }
                    if (rank == 2) break;
                }

                if (icon == "None" && prev != null && prev.HoldBeats >= 0)
                    icon = prev.HoldBeats == 0 ? "HoldReleaseShort" : "HoldReleaseLong";
                if (icon == "None" && prev != null && floor.IsCCW != prev.IsCCW)
                    icon = "Swirl";
            }

            return icon;
        }

        /// <summary>
        /// 装饰砖不会自己根据事件画图标。这里按 scnGame 选图标的顺序写上 trackIcon。
        /// </summary>
        private static void ApplyFloorIcon(ChartEvent dec, Track track, TrackTimeline timeline, int index)
        {
            FloorTime floor = timeline[index];
            string icon = ChooseFloorIcon(track, timeline, index);
            dec.Data["trackIcon"] = icon;
            if (icon == "Swirl")
            {
                double sweep = floor.AngleLength;
                if (sweep <= 1e-6 && !floor.MidSpin) sweep = Math.PI * 2.0;
                dec.Data["trackRedSwirl"] = sweep < Math.PI;
                dec.Data["trackIconFlipped"] = floor.IsCCW;
                double sign = floor.IsCCW ? -1.0 : 1.0;
                dec.Data["trackIconAngle"] = IconAngleDeg(floor.EntryAngle + sign * sweep / 2.0);
            }
            else if (icon == "HoldArrowLong" || icon == "HoldArrowShort")
            {
                double sign = floor.IsCCW ? -1.0 : 1.0;
                double num6 = -floor.EntryAngle + Math.PI / 2.0 - floor.AngleLength * sign;
                dec.Data["trackIconAngle"] = IconAngleDeg(-num6);
            }
            else if (icon == "Rabbit" || icon == "DoubleRabbit" || icon == "Snail" || icon == "DoubleSnail")
            {
                dec.Data["trackSetSpeedIconBpm"] = track.BaseBpm * floor.Speed;
            }
        }

        /// <summary>SetFloorIconAngle 存的是度，真正转网格时会取负。这里反着写，画出来的角度才和真砖一样。</summary>
        private static double IconAngleDeg(double iconRad)
        {
            return -iconRad * 180.0 / Math.PI;
        }

        // ------------------------------------------------------------------ 小工具

        private static Vec2 Vec2At(Vec2[] world, int index)
        {
            if (world == null || world.Length == 0) return new Vec2(0, 0);
            if (index < 0) return world[0];
            if (index >= world.Length) return world[world.Length - 1];
            return world[index];
        }

        private static Vec2 ReadVector2(ChartEvent ev, string key)
        {
            List<object> pair = ev.GetList(key);
            if (pair == null || pair.Count < 2) return new Vec2(0, 0);
            double x = pair[0] == null ? 0 : Convert.ToDouble(pair[0], CultureInfo.InvariantCulture);
            double y = pair[1] == null ? 0 : Convert.ToDouble(pair[1], CultureInfo.InvariantCulture);
            return new Vec2(x, y);
        }

        /// <summary>RDUtils.HexToColor 只吃 6/8 位十六进制（不带 #），格式不对会让整条装饰失效。</summary>
        private static string Hex(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            string s = value.Trim();
            if (s.Length > 0 && s[0] == '#') s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return fallback;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return fallback;
            }
            return s;
        }

        /// <summary>枚举名必须落在游戏认识的那几个值里，否则 LevelEvent.Decode 会 Enum.Parse 抛异常。</summary>
        private static string Pick(string value, string[] allowed, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            foreach (string a in allowed)
                if (string.Equals(a, value, StringComparison.OrdinalIgnoreCase)) return a;
            return fallback;
        }
    }
}
