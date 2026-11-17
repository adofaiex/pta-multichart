using System;
using System.Collections.Generic;
using System.Globalization;
using PtaMultichart.Core.Model;

namespace PtaMultichart.Core.Timing
{
    public struct Vec2
    {
        public double X;
        public double Y;

        public Vec2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, double k) => new Vec2(a.X * k, a.Y * k);

        public double Length => Math.Sqrt(X * X + Y * Y);

        public override string ToString() =>
            X.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + "," +
            Y.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 一块砖的节拍/时间信息。语义与 r150 的 scrFloor + scrLevelMaker.CalculateFloorEntryTimes 对齐：
    /// EntryBeat[1] == 0，EntryTime[1] == 0（去掉了倒计时那段，跨轨比较只看相对时间）。
    /// </summary>
    public sealed class FloorTime
    {
        /// <summary>砖号（0 = 起点砖）。</summary>
        public int SeqId;
        /// <summary>angleData 里存的这个砖的出射角（度）。最后一块砖没有 angleData，为 NaN。</summary>
        public double Angle = double.NaN;
        public double EntryAngle;
        public double ExitAngle;
        public bool MidSpin;
        public bool IsCCW;
        /// <summary>这一块砖上的 speed（相对关卡/轨道基础 BPM 的倍率）。</summary>
        public double Speed = 1;
        /// <summary>从这块砖走到下一块转过的角度（弧度，带方向的意义上已归一化到 [0, 2π)）。</summary>
        public double AngleLength;
        public bool Turnaround;
        /// <summary>Pause 等带来的额外拍。</summary>
        public double ExtraBeats;
        /// <summary>Hold 的时长（拍），-1 = 没有 hold。</summary>
        public int HoldBeats = -1;
        /// <summary>游戏语义的 entryBeat（不含 speed、不含 hold，含 extraBeats）。</summary>
        public double EntryBeat;
        /// <summary>相对第一块可点击砖的歌曲时间（秒）。</summary>
        public double EntryTime;
        /// <summary>走过这块砖需要的时间（秒），含 hold / pause。</summary>
        public double TravelTime;
        /// <summary>走过这块砖的拍数（= AngleLength/π + ExtraBeats + Hold*2，用于比较与切分）。</summary>
        public double TravelBeats;
        /// <summary>这是一次真正要按的点击（seqID ≥ 1 且上一块不是中旋）。</summary>
        public bool IsClick;

        public override string ToString() =>
            "#" + SeqId + " a=" + Angle.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
            " beat=" + EntryBeat.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
            " t=" + EntryTime.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
            (MidSpin ? " midspin" : "") + (IsClick ? " click" : "");
    }

    public sealed class TrackTimeline
    {
        public double BaseBpm = 100;
        public double Crotchet = 0.6;
        public List<FloorTime> Floors = new List<FloorTime>();
        public List<int> ClickFloors = new List<int>();
        /// <summary>世界坐标（单位 = 砖长），Positions[0] = (0,0)。</summary>
        public List<Vec2> Positions = new List<Vec2>();

        public int Count => Floors.Count;

        public FloorTime this[int seq] => Floors[seq];

        public bool IsValidSeq(int seq) => seq >= 0 && seq < Floors.Count;

        public double TimeOf(int seq) => Floors[seq].EntryTime;

        public double BeatOf(int seq) => Floors[seq].EntryBeat;
    }

    /// <summary>把 angleData + 轨道事件换算成时间线。公式对齐 r150 scrLevelMaker / scnGame.ApplyCoreEventsToFloors。</summary>
    public static class TimelineBuilder
    {
        public const double TwoPi = 6.2831854820251465;
        public const double PiF = 3.1415927410125732;
        public const float MidSpinAngle = 999f;

        public static double Mod(double a, double m)
        {
            double r = a % m;
            return r < 0 ? r + m : r;
        }

        /// <summary>scrMisc.GetAngleMoved：isCW 为 true 时正转。</summary>
        public static double AngleMoved(double entryAngle, double exitAngle, bool isCW)
        {
            double sign = isCW ? 1.0 : -1.0;
            return Mod((exitAngle - entryAngle) * sign, TwoPi);
        }

        public static double ExitRadFromAngleDeg(double angleDeg)
        {
            return (0.0 - angleDeg + 90.0) * (Math.PI / 180.0);
        }

        /// <summary>反算：给定出口角（弧度），degree = 90 - rad*180/π。</summary>
        public static double AngleDegFromExitRad(double exitRad)
        {
            return 90.0 - exitRad * 180.0 / Math.PI;
        }

        public static double NormalizeDeg(double deg)
        {
            double d = Mod(deg, 360.0);
            return d < 0 ? d + 360.0 : d;
        }

        public static Vec2 VectorFromAngle(double angleRad)
        {
            return new Vec2(Math.Sin(angleRad), Math.Cos(angleRad));
        }

        private sealed class FloorEvents
        {
            public List<ChartEvent> Items = new List<ChartEvent>();
        }

        public static TrackTimeline Build(IList<float> angleData, IEnumerable<ChartEvent> events, double baseBpm)
        {
            if (angleData == null) throw new ArgumentNullException(nameof(angleData));
            if (baseBpm <= 0) throw new ArgumentOutOfRangeException(nameof(baseBpm));

            var timeline = new TrackTimeline
            {
                BaseBpm = baseBpm,
                Crotchet = 60.0 / baseBpm
            };

            int n = angleData.Count + 1;
            var floors = new FloorTime[n];
            for (int i = 0; i < n; i++)
            {
                floors[i] = new FloorTime { SeqId = i };
                if (i < angleData.Count) floors[i].Angle = angleData[i];
            }

            // 与 scrLevelMaker.InstantiateFloatFloors 一致：地板 0 的入口角固定 3π/2。
            double exit = 0;
            bool ccw = false;
            double speed = 1;
            var perFloor = new FloorEvents[n];
            for (int i = 0; i < n; i++) perFloor[i] = new FloorEvents();

            if (events != null)
            {
                foreach (var ev in events)
                {
                    if (ev == null || ev.Floor < 0 || ev.Floor >= n) continue;
                    if (!ev.Active) continue;
                    perFloor[ev.Floor].Items.Add(ev);
                }
            }

            // 先按顺序处理每块砖上的事件（Twirl 持续翻转，SetSpeed 持续生效）。
            for (int i = 0; i < n; i++)
            {
                var f = floors[i];
                if (i == 0)
                {
                    f.EntryAngle = 4.71238899230957;
                }

                foreach (var ev in perFloor[i].Items)
                {
                    switch (ev.Type)
                    {
                        case "Twirl":
                            ccw = !ccw;
                            break;
                        case "SetSpeed":
                        {
                            string type = ev.GetString("speedType", "Bpm");
                            if (string.Equals(type, "Bpm", StringComparison.OrdinalIgnoreCase))
                            {
                                double bpm = ev.GetDouble("beatsPerMinute", baseBpm);
                                speed = bpm / baseBpm;
                            }
                            else
                            {
                                double mult = ev.GetDouble("bpmMultiplier", 100);
                                speed = speed * (mult / 100.0);
                            }
                            break;
                        }
                        case "Pause":
                            if (i < n - 1 && !f.MidSpin)
                                f.ExtraBeats += ev.GetDouble("duration", 0);
                            break;
                        case "Hold":
                            if (i < n - 1 && ev.GetInt("duration", -1) >= 0)
                                f.HoldBeats = ev.GetInt("duration", -1);
                            break;
                    }
                }

                f.IsCCW = ccw;
                f.Speed = speed;

                if (i < n - 1)
                {
                    float a = angleData[i];
                    if (a == MidSpinAngle)
                    {
                        f.MidSpin = true;
                        exit = f.EntryAngle;
                    }
                    else
                    {
                        exit = ExitRadFromAngleDeg(a);
                    }
                    f.ExitAngle = exit;

                    var next = floors[i + 1];
                    next.EntryAngle = (exit + PiF) % TwoPi;
                }
                else
                {
                    // 最后一块砖：游戏在 InstantiateFloatFloors 末尾把它设成 entry + π。
                    exit = (f.EntryAngle + PiF) % TwoPi;
                    f.ExitAngle = exit;
                }
            }

            // 转角、时间、entryBeat。
            double beat = 0;
            double time = 0;
            for (int i = 0; i < n; i++)
            {
                var f = floors[i];
                double sweep = AngleMoved(f.EntryAngle, f.ExitAngle, !f.IsCCW);
                bool degenerate = sweep <= 1e-06 || sweep >= 6.283184482025146;
                if (degenerate)
                {
                    if (f.MidSpin)
                    {
                        sweep = 0;
                    }
                    else
                    {
                        sweep = TwoPi;
                        f.Turnaround = true;
                    }
                }
                f.AngleLength = sweep;

                double beatLen = sweep / Math.PI; // 0..2 拍
                f.TravelBeats = beatLen + f.ExtraBeats + (f.HoldBeats > 0 ? f.HoldBeats * 2 : 0);
                f.TravelTime = (beatLen + f.ExtraBeats + (f.HoldBeats > 0 ? f.HoldBeats * 2 : 0)) *
                               (timeline.Crotchet / Math.Max(1e-9, f.Speed));

                if (i == 0)
                {
                    f.EntryBeat = -1;
                    f.EntryTime = 0;
                }
                else if (i == 1)
                {
                    f.EntryBeat = 0;
                    f.EntryTime = 0;
                    beat = 0;
                    time = 0;
                }

                if (i >= 1)
                {
                    // 这块砖的入口就是之前累积的值
                    beat += beatLen + f.ExtraBeats;
                    time += f.TravelTime;
                    if (i + 1 < n)
                    {
                        floors[i + 1].EntryBeat = beat;
                        floors[i + 1].EntryTime = time;
                    }
                }
            }

            // 点击判定 + 世界坐标。
            timeline.Positions.Add(new Vec2(0, 0));
            for (int i = 0; i < n; i++)
            {
                var f = floors[i];
                f.IsClick = i >= 1 && !floors[i - 1].MidSpin;
                if (f.IsClick) timeline.ClickFloors.Add(i);
                if (i < n - 1)
                    timeline.Positions.Add(timeline.Positions[i] + VectorFromAngle(f.ExitAngle));
                else
                    timeline.Positions.Add(timeline.Positions[i] + VectorFromAngle(f.ExitAngle));
            }

            timeline.Floors.AddRange(floors);
            return timeline;
        }

        /// <summary>
        /// 把一段圆弧按比例切成若干段，返回每一段自己的出射角（度）。
        /// 保持入口角固定、方向不变、最后一段的出口角严格等于原出口角。
        /// </summary>
        public static List<double> SplitExitAnglesDegrees(FloorTime floor, IList<double> fractions)
        {
            if (fractions == null || fractions.Count == 0)
                throw new ArgumentException("fractions 不能为空");
            double sum = 0;
            foreach (var f in fractions)
            {
                if (f <= 0) throw new ArgumentException("fractions 必须为正");
                sum += f;
            }

            bool isCW = !floor.IsCCW;
            double sign = isCW ? 1.0 : -1.0;
            double cumulative = 0;
            var result = new List<double>(fractions.Count);
            for (int i = 0; i < fractions.Count; i++)
            {
                cumulative += fractions[i] / sum;
                if (i == fractions.Count - 1)
                {
                    result.Add(i == 0 ? AngleDegFromExitRad(floor.ExitAngle) : AngleDegFromExitRad(floor.ExitAngle));
                    result[i] = NormalizeDeg(AngleDegFromExitRad(floor.ExitAngle));
                }
                else
                {
                    double exitRad = floor.EntryAngle + sign * floor.AngleLength * cumulative;
                    result.Add(NormalizeDeg(AngleDegFromExitRad(exitRad)));
                }
            }
            return result;
        }

        /// <summary>
        /// 把 PositionTrack 叠到链式位置上，单位是砖长。
        /// 和 scnGame 一样：偏移累加，justThisTile 不传给后面的砖；relativeTo 指向已经摆好的砖时，改成相对那块砖。
        /// editorOnly 的事件只在 includeEditorOnly 时生效（编辑器里的幽灵砖要，播放用的装饰不要）。
        /// </summary>
        public static List<Vec2> ApplyPositionTracks(TrackTimeline timeline, IEnumerable<ChartEvent> events, bool includeEditorOnly)
        {
            var result = new List<Vec2>();
            if (timeline == null) return result;
            int n = timeline.Positions.Count;

            var perFloor = new List<ChartEvent>[n];
            if (events != null)
            {
                foreach (ChartEvent ev in events)
                {
                    if (ev == null || !ev.Active || ev.Type != "PositionTrack") continue;
                    if (ev.Floor < 0 || ev.Floor >= n) continue;
                    if (!includeEditorOnly && ev.GetBool("editorOnly", false)) continue;
                    if (perFloor[ev.Floor] == null) perFloor[ev.Floor] = new List<ChartEvent>();
                    perFloor[ev.Floor].Add(ev);
                }
            }

            Vec2 carry = new Vec2(0, 0);
            for (int i = 0; i < n; i++)
            {
                Vec2 published = carry;
                Vec2 offset = carry;
                if (perFloor[i] != null)
                {
                    foreach (ChartEvent ev in perFloor[i])
                    {
                        offset += ReadOffset(ev);
                        int target = ResolveTile(ev, i, n);
                        if (target != i && target >= 0 && target < result.Count)
                            offset += result[target] - (timeline.Positions[i] + published);
                        if (!ev.GetBool("justThisTile", false))
                            published = offset;
                    }
                }
                result.Add(timeline.Positions[i] + offset);
                carry = published;
            }
            return result;
        }

        private static Vec2 ReadOffset(ChartEvent ev)
        {
            List<object> pair = ev.GetList("positionOffset");
            if (pair == null || pair.Count < 2) return new Vec2(0, 0);
            return new Vec2(ToDouble(pair[0]), ToDouble(pair[1]));
        }

        private static int ResolveTile(ChartEvent ev, int seq, int count)
        {
            List<object> pair = ev.GetList("relativeTo");
            if (pair == null || pair.Count < 2) return seq;
            int off = pair[0] == null ? 0 : Convert.ToInt32(pair[0], CultureInfo.InvariantCulture);
            string kind = Convert.ToString(pair[1], CultureInfo.InvariantCulture);
            int raw;
            if (kind == "Start") raw = off;
            else if (kind == "End") raw = count - 1 + off;
            else raw = seq + off;
            if (raw < 0) return 0;
            if (raw >= count) return count - 1;
            return raw;
        }

        private static double ToDouble(object value)
        {
            if (value == null) return 0;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }
}
