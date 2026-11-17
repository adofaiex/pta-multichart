using System;
using System.Collections.Generic;
using System.Globalization;
using PtaMultichart.Core.Model;
using PtaMultichart.Core.Timing;

namespace PtaMultichart.Core.Compile
{
    /// <summary>
    /// 逻辑谱 → 播放谱。不引用 Unity，可被控制台测试直接编译。
    /// </summary>
    public static class ChartCompiler
    {
        private const double TimeEps = 1e-9;
        /// <summary>切出来的段至少占整块砖的这么多，再小游戏就会把角度当 0 处理（变成 2π 回头弯）。</summary>
        private const double MinSegmentFraction = 1e-6;
        /// <summary>伪双押只扫 1°，避免 180° 发卡弯。</summary>
        private const double PseudoSweepRad = Math.PI / 180.0;

        /// <summary>把逻辑谱编译成播放谱。失败一律抛 CompileError，不静默丢东西。</summary>
        public static PlayableChart Compile(LogicalChart chart, CompileSettings settings)
        {
            if (chart == null) throw new ArgumentNullException(nameof(chart));
            if (settings == null) settings = new CompileSettings();

            Track main = chart.Main;
            if (main == null)
                throw new CompileError("NoMainTrack", "逻辑谱里没有主轨（Track 0）");
            if (main.AngleData == null || main.AngleData.Count == 0)
                throw new CompileError("EmptyMainTrack", "主轨 angleData 为空，没有砖可以切");

            double mainBpm = settings.Bpm > 0 ? settings.Bpm : (main.BaseBpm > 0 ? main.BaseBpm : 100.0);
            TrackTimeline mainTl = TimelineBuilder.Build(main.AngleData, main.Events, mainBpm);
            int logicalFloors = mainTl.Count;

            List<SubPlan> plans = CollectSubPlans(chart, mainTl, mainBpm);
            MarkPseudoDoubles(plans, mainTl, settings);
            Dictionary<int, List<SubClick>> groups = GroupClicks(plans, mainTl, logicalFloors);
            Dictionary<int, List<SubClick>> pseudos = GroupPseudos(plans);

            var playable = new PlayableChart();
            List<float> angles = playable.AngleData;
            List<ChartEvent> actions = playable.Actions;
            List<ChartEvent> decorations = playable.Decorations;
            List<InsertedClick> insertedClicks = playable.InsertedClicks;

            int[] toPlayable = new int[logicalFloors];
            var toLogical = new List<int>(main.AngleData.Count + 1);
            var insertedFlags = new List<bool>(main.AngleData.Count + 1);
            var idealPos = new List<Vec2>(main.AngleData.Count + 1);
            var hideHosts = new List<int>();
            var masked = new List<int>();

            int p = 0;
            for (int i = 0; i < main.AngleData.Count; i++)
            {
                toPlayable[i] = p;
                FloorTime f = mainTl[i];

                groups.TryGetValue(i, out List<SubClick> clicks);
                pseudos.TryGetValue(i, out List<SubClick> chord);
                bool hasMid = clicks != null && clicks.Count > 0;
                bool hasChord = chord != null && chord.Count > 0;

                if (!hasMid && !hasChord)
                {
                    angles.Add(main.AngleData[i]);
                    toLogical.Add(i);
                    insertedFlags.Add(false);
                    idealPos.Add(mainTl.Positions[i]);
                    p++;
                    continue;
                }

                if (hasChord && hasMid)
                {
                    if (f.MidSpin)
                        throw new CompileError("CannotSplit",
                            "要切的 " + i + " 号砖是中旋砖（999），没有时长可以切", i);
                    hideHosts.Add(p);
                    masked.Add(i);
                    p = EmitPseudoAndMidspins(i, f, chord, clicks, main.AngleData[i], mainTl, angles, toLogical,
                        insertedFlags, idealPos, insertedClicks, p, mainTl.Positions[i]);
                    continue;
                }

                if (hasChord)
                {
                    hideHosts.Add(p);
                    masked.Add(i);
                    p = EmitPseudoDouble(i, f, chord, main.AngleData[i], angles, toLogical, insertedFlags,
                        idealPos, insertedClicks, p, mainTl.Positions[i]);
                    continue;
                }

                if (f.MidSpin)
                    throw new CompileError("CannotSplit",
                        "要切的 " + i + " 号砖是中旋砖（999），没有时长可以切", i);

                hideHosts.Add(p);
                masked.Add(i);
                p = EmitTransparentMidspins(i, f, clicks, main.AngleData[i], mainTl, angles, toLogical,
                    insertedFlags, idealPos, insertedClicks, p);
            }

            toPlayable[logicalFloors - 1] = p;
            toLogical.Add(logicalFloors - 1);
            insertedFlags.Add(false);
            idealPos.Add(mainTl.Positions[logicalFloors - 1]);

            playable.Map = new FloorMap(toPlayable, toLogical.ToArray(), insertedFlags.ToArray());

            int ptInsertedCount = EmitHiddenInserts(actions, insertedFlags, hideHosts);

            foreach (ChartEvent ev in main.Events)
            {
                if (EventMapper.IsSelfMarker(ev)) continue;
                actions.Add(EventMapper.MapEvent(ev, playable.Map, logicalFloors));
            }
            foreach (ChartEvent dec in main.Decorations)
                decorations.Add(EventMapper.MapEvent(dec, playable.Map, logicalFloors));

            HashSet<string> conditionalTags = RepeatEventProcessor.CollectConditionalTags(actions);
            var tagConflicts = new List<string>();
            RepeatEventProcessor.Process(actions, playable.Map, logicalFloors, conditionalTags, tagConflicts);

            foreach (int k in masked)
            {
                FloorTime host = mainTl[k];
                decorations.Add(DecorationBuilder.Mask(settings, mainTl.Positions[k], host.EntryAngle, host.ExitAngle));
            }

            foreach (SubPlan plan in plans)
            {
                Vec2[] world = SubWorldPositions(plan);
                DecorationBuilder.AddTrackVisuals(decorations, plan.Track, plan.Timeline, world, settings);
                DecorationBuilder.BakeSubDecorations(decorations, plan.Track, world);
                AddPlanetMoves(actions, plan, playable.Map, world, mainBpm);
            }

            var marker = new ChartEvent("EditorComment", 0) { Locked = true };
            marker.Data["comment"] = chart.ToComment();
            actions.Add(marker);

            int pseudoCount = CountPseudos(plans);
            playable.Preflight = PreflightBuilder.Build(playable, plans.Count, pseudoCount, ptInsertedCount, 0, tagConflicts);

            return playable;
        }

        public static TrackTimeline RebuildTimeline(PlayableChart chart, double bpm)
        {
            if (chart == null) throw new ArgumentNullException(nameof(chart));
            return TimelineBuilder.Build(chart.AngleData, chart.Actions, bpm > 0 ? bpm : 100.0);
        }

        public static List<Vec2> NaturalPositions(IList<float> angleData, double bpm)
        {
            return TimelineBuilder.Build(angleData, null, bpm > 0 ? bpm : 100.0).Positions;
        }

        private static int EmitHiddenInserts(List<ChartEvent> actions, List<bool> insertedFlags, List<int> hideHosts)
        {
            var hide = new bool[insertedFlags.Count];
            for (int i = 0; i < insertedFlags.Count; i++) hide[i] = insertedFlags[i];
            foreach (int h in hideHosts)
                if (h >= 0 && h < hide.Length) hide[h] = true;

            int n = 0;
            for (int i = 0; i < hide.Length; i++)
            {
                if (!hide[i]) continue;
                var ev = new ChartEvent("PositionTrack", i);
                ev.Data["opacity"] = 0.0;
                ev.Data["justThisTile"] = true;
                ev.Data["positionOffset"] = ChartEvent.Pair(0.0, 0.0);
                actions.Add(ev);
                n++;
            }
            return n;
        }

        public static Vec2 IdealArcPoint(Vec2 start, Vec2 end, double sweep, bool isCCW, double f)
        {
            Vec2 chord = end - start;
            double sinHalf = Math.Sin(sweep * 0.5);
            if (sinHalf < 1e-6) return start + chord * f;

            double radius = 0.5 / sinHalf;
            Vec2 mid = (start + end) * 0.5;
            Vec2 leftPerp = new Vec2(-chord.Y, chord.X);
            double s = isCCW ? 1.0 : -1.0;
            Vec2 center = mid + leftPerp * (s * radius * Math.Cos(sweep * 0.5));
            Vec2 r0 = start - center;
            return center + Rotate(r0, s * sweep * f);
        }

        private static Vec2 Rotate(Vec2 v, double angle)
        {
            double c = Math.Cos(angle);
            double sn = Math.Sin(angle);
            return new Vec2(v.X * c - v.Y * sn, v.X * sn + v.Y * c);
        }

        private sealed class SubClick
        {
            public int TrackId;
            public int SubSeq;
            public double Time;
            public int PlayableSeq = -1;
            public bool Pseudo;
            public int Host = -1;
        }

        private sealed class SubPlan
        {
            public Track Track;
            public TrackTimeline Timeline;
            public double Bpm;
            public int Anchor;
            public double AnchorTime;
            public Vec2 AnchorPos;
            public readonly List<SubClick> Clicks = new List<SubClick>();
        }

        private static int CountPseudos(List<SubPlan> plans)
        {
            int pseudo = 0;
            foreach (SubPlan plan in plans)
                foreach (SubClick click in plan.Clicks)
                    if (click.Pseudo) pseudo++;
            return pseudo;
        }

        private static Vec2[] SubWorldPositions(SubPlan plan)
        {
            int n = plan.Timeline.Count;
            var world = new Vec2[n];
            List<Vec2> local = TimelineBuilder.ApplyPositionTracks(plan.Timeline, plan.Track.Events, false);
            Vec2 shift = plan.AnchorPos - plan.Timeline.Positions[1];
            for (int i = 0; i < n && i < local.Count; i++)
                world[i] = shift + local[i];
            return world;
        }

        private static List<SubPlan> CollectSubPlans(LogicalChart chart, TrackTimeline mainTl, double mainBpm)
        {
            var plans = new List<SubPlan>();
            var seenIds = new HashSet<int>();

            for (int t = 1; t < chart.Tracks.Count; t++)
            {
                Track track = chart.Tracks[t];
                if (track == null) continue;
                if (!seenIds.Add(track.Id))
                    throw new CompileError("TrackIdConflict", "两条副轨用了同一个 Id: " + track.Id);
                if (track.AngleData == null || track.AngleData.Count == 0) continue;

                ValidateSubTrack(track);

                if (track.AnchorFloor < 0 || track.AnchorFloor >= mainTl.Count)
                    throw new CompileError("AnchorOutOfRange",
                        "副轨 " + track.Id + " 的锚点是主轨 " + track.AnchorFloor + " 号砖，主轨只有 " +
                        mainTl.Count + " 块砖", track.AnchorFloor);

                double bpm = track.BaseBpm > 0 ? track.BaseBpm : mainBpm;
                TrackTimeline tl = TimelineBuilder.Build(track.AngleData, track.Events, bpm);

                var plan = new SubPlan
                {
                    Track = track,
                    Timeline = tl,
                    Bpm = bpm,
                    Anchor = track.AnchorFloor,
                    AnchorTime = mainTl[track.AnchorFloor].EntryTime,
                    AnchorPos = mainTl.Positions[track.AnchorFloor]
                };

                double zero = tl.Count > 1 ? tl[1].EntryTime : 0.0;
                foreach (int seq in tl.ClickFloors)
                {
                    // 副轨 seq1 对齐锚点砖，主轨已有这一拍。
                    if (seq < 2) continue;
                    plan.Clicks.Add(new SubClick
                    {
                        TrackId = track.Id,
                        SubSeq = seq,
                        Time = plan.AnchorTime + (tl[seq].EntryTime - zero)
                    });
                }

                plans.Add(plan);
            }

            return plans;
        }

        private static void ValidateSubTrack(Track track)
        {
            foreach (ChartEvent ev in track.Events)
            {
                if (ev == null || !ev.Active) continue;
                if (ev.Type == "FreeRoam" || ev.Type == "FreeRoamTwirl")
                    throw new CompileError("UnsupportedSubEvent",
                        "副轨 " + track.Id + " 上有 " + ev.Type + "，这一版不支持", ev.Floor);
                if (ev.Type == "MultiPlanet" && ev.GetInt("planets", 2) != 2)
                    throw new CompileError("UnsupportedSubEvent",
                        "副轨 " + track.Id + " 上有 MultiPlanet(planets=" + ev.GetInt("planets", 2) +
                        ")，第一版只支持 2 颗行星", ev.Floor);
            }
        }

        private static void MarkPseudoDoubles(List<SubPlan> plans, TrackTimeline mainTl, CompileSettings settings)
        {
            double gap = settings.MinClickGapSeconds;
            if (gap <= 0) return;

            var all = new List<SubClick>();
            foreach (SubPlan plan in plans)
            {
                foreach (SubClick click in plan.Clicks)
                {
                    all.Add(click);
                    int host = -1;
                    double best = gap;
                    foreach (int mc in mainTl.ClickFloors)
                    {
                        double d = Math.Abs(click.Time - mainTl[mc].EntryTime);
                        if (d >= best) continue;
                        best = d;
                        host = mc;
                    }
                    if (host < 0) continue;
                    if (host >= mainTl.Count - 1)
                        throw new CompileError("OverlappingClick",
                            "副轨 " + click.TrackId + " 的 " + click.SubSeq + " 号砖和主轨终点砖重合，后面没有行程可以把这 1° 收回来",
                            host, click.SubSeq);
                    click.Pseudo = true;
                    click.Host = host;
                }
            }

            for (int i = 0; i < all.Count; i++)
            {
                for (int j = i + 1; j < all.Count; j++)
                {
                    if (Math.Abs(all[i].Time - all[j].Time) >= gap) continue;
                    if (all[i].Pseudo && all[j].Pseudo) continue;
                    throw new CompileError("OverlappingClick",
                        "副轨 " + all[i].TrackId + " 的 " + all[i].SubSeq + " 号砖与副轨 " +
                        all[j].TrackId + " 的 " + all[j].SubSeq + " 号砖点击相差 " +
                        Fmt(Math.Abs(all[i].Time - all[j].Time)) +
                        "s < 半个判定窗 " + Fmt(gap) + "s，又没有贴在同一次主轨点击上",
                        all[i].SubSeq, all[j].SubSeq);
                }
            }
        }

        private static Dictionary<int, List<SubClick>> GroupPseudos(List<SubPlan> plans)
        {
            var groups = new Dictionary<int, List<SubClick>>();
            foreach (SubPlan plan in plans)
            {
                foreach (SubClick click in plan.Clicks)
                {
                    if (!click.Pseudo) continue;
                    if (!groups.TryGetValue(click.Host, out List<SubClick> list))
                    {
                        list = new List<SubClick>();
                        groups[click.Host] = list;
                    }
                    list.Add(click);
                }
            }
            foreach (KeyValuePair<int, List<SubClick>> kv in groups)
                kv.Value.Sort(CompareByTime);
            return groups;
        }

        private static double EmitSweep(FloorTime floor, double sweep, List<float> angles, List<int> toLogical,
            List<bool> insertedFlags, int logical, bool inserted, ref double entry)
        {
            toLogical.Add(logical);
            insertedFlags.Add(inserted);
            if (floor.MidSpin || sweep <= 1e-12)
            {
                angles.Add(TimelineBuilder.MidSpinAngle);
                entry = WrapTau(entry + Math.PI);
                return entry;
            }

            double sign = floor.IsCCW ? -1.0 : 1.0;
            double exit = entry + sign * sweep;
            angles.Add((float)TimelineBuilder.NormalizeDeg(TimelineBuilder.AngleDegFromExitRad(exit)));
            entry = WrapTau(exit + Math.PI);
            return entry;
        }

        private static double WrapTau(double rad)
        {
            double t = rad % (Math.PI * 2.0);
            if (t < 0) t += Math.PI * 2.0;
            return t;
        }

        private static int EmitTransparentMidspins(int logical, FloorTime floor, List<SubClick> clicks,
            float originalExit, TrackTimeline mainTl, List<float> angles, List<int> toLogical,
            List<bool> insertedFlags, List<Vec2> idealPos, List<InsertedClick> insertedClicks, int p)
        {
            List<double> fractions = BuildFractions(mainTl, logical, clicks);
            Vec2 arcStart = mainTl.Positions[logical];
            Vec2 arcEnd = mainTl.Positions[logical + 1];
            double entry = floor.EntryAngle;

            entry = EmitSweep(floor, floor.AngleLength * fractions[0], angles, toLogical, insertedFlags,
                logical, false, ref entry);
            idealPos.Add(arcStart);
            p++;

            double cumulative = fractions[0];
            for (int c = 0; c < clicks.Count; c++)
            {
                entry = EmitSweep(floor, 0, angles, toLogical, insertedFlags, logical, true, ref entry);
                idealPos.Add(IdealArcPoint(arcStart, arcEnd, floor.AngleLength, floor.IsCCW, cumulative));
                SubClick sub = clicks[c];
                sub.PlayableSeq = p;
                insertedClicks.Add(new InsertedClick
                {
                    SubTrackId = sub.TrackId,
                    SubSeq = sub.SubSeq,
                    PlayableSeq = p,
                    SongTime = sub.Time
                });
                p++;

                if (c < clicks.Count - 1)
                {
                    cumulative += fractions[c + 1];
                    entry = EmitSweep(floor, floor.AngleLength * fractions[c + 1], angles, toLogical, insertedFlags,
                        logical, true, ref entry);
                    idealPos.Add(IdealArcPoint(arcStart, arcEnd, floor.AngleLength, floor.IsCCW, cumulative));
                    p++;
                }
            }

            angles.Add(originalExit);
            toLogical.Add(logical);
            insertedFlags.Add(true);
            idealPos.Add(arcEnd);
            return p + 1;
        }

        private static int EmitPseudoDouble(int logical, FloorTime floor, List<SubClick> chord, float originalExit,
            List<float> angles, List<int> toLogical, List<bool> insertedFlags, List<Vec2> idealPos,
            List<InsertedClick> insertedClicks, int p, Vec2 hostPos)
        {
            if (floor.MidSpin)
                throw new CompileError("CannotSplit",
                    logical + " 号砖是中旋，没有行程可以做伪双押", logical);
            double need = chord.Count * PseudoSweepRad;
            if (floor.AngleLength <= need + 1e-3)
                throw new CompileError("CannotSplit",
                    logical + " 号砖的转角只有 " + (floor.AngleLength * 180.0 / Math.PI).ToString("0.###") +
                    "°，放不下 " + chord.Count + " 个 1° 伪双押", logical);

            double entry = floor.EntryAngle;
            entry = EmitSweep(floor, PseudoSweepRad, angles, toLogical, insertedFlags, logical, false, ref entry);
            idealPos.Add(hostPos);
            p++;

            for (int n = 0; n < chord.Count; n++)
            {
                entry = EmitSweep(floor, 0, angles, toLogical, insertedFlags, logical, true, ref entry);
                idealPos.Add(hostPos);
                SubClick sub = chord[n];
                sub.PlayableSeq = p;
                insertedClicks.Add(new InsertedClick
                {
                    SubTrackId = sub.TrackId,
                    SubSeq = sub.SubSeq,
                    PlayableSeq = p,
                    SongTime = sub.Time
                });
                p++;

                if (n == chord.Count - 1)
                {
                    angles.Add(originalExit);
                    toLogical.Add(logical);
                    insertedFlags.Add(true);
                    idealPos.Add(hostPos);
                    p++;
                }
                else
                {
                    entry = EmitSweep(floor, PseudoSweepRad, angles, toLogical, insertedFlags, logical, true, ref entry);
                    idealPos.Add(hostPos);
                    p++;
                }
            }
            return p;
        }

        private static int EmitPseudoAndMidspins(int logical, FloorTime floor, List<SubClick> chord, List<SubClick> clicks,
            float originalExit, TrackTimeline mainTl, List<float> angles, List<int> toLogical, List<bool> insertedFlags,
            List<Vec2> idealPos, List<InsertedClick> insertedClicks, int p, Vec2 hostPos)
        {
            double need = chord.Count * PseudoSweepRad;
            if (floor.AngleLength <= need + 1e-3)
                throw new CompileError("CannotSplit",
                    logical + " 号砖的转角只有 " + (floor.AngleLength * 180.0 / Math.PI).ToString("0.###") +
                    "°，放不下 " + chord.Count + " 个 1° 伪双押", logical);

            double entry = floor.EntryAngle;
            entry = EmitSweep(floor, PseudoSweepRad, angles, toLogical, insertedFlags, logical, false, ref entry);
            idealPos.Add(hostPos);
            p++;

            for (int n = 0; n < chord.Count; n++)
            {
                entry = EmitSweep(floor, 0, angles, toLogical, insertedFlags, logical, true, ref entry);
                idealPos.Add(hostPos);
                SubClick pseudo = chord[n];
                pseudo.PlayableSeq = p;
                insertedClicks.Add(new InsertedClick
                {
                    SubTrackId = pseudo.TrackId,
                    SubSeq = pseudo.SubSeq,
                    PlayableSeq = p,
                    SongTime = pseudo.Time
                });
                p++;
                if (n < chord.Count - 1)
                {
                    entry = EmitSweep(floor, PseudoSweepRad, angles, toLogical, insertedFlags, logical, true, ref entry);
                    idealPos.Add(hostPos);
                    p++;
                }
            }

            double remain = floor.AngleLength - need;
            List<double> fractions = BuildFractions(mainTl, logical, clicks);
            Vec2 arcStart = mainTl.Positions[logical];
            Vec2 arcEnd = mainTl.Positions[logical + 1];
            entry = EmitSweep(floor, remain * fractions[0], angles, toLogical, insertedFlags, logical, true, ref entry);
            idealPos.Add(arcStart);
            p++;

            double cumulative = fractions[0];
            for (int c = 0; c < clicks.Count; c++)
            {
                entry = EmitSweep(floor, 0, angles, toLogical, insertedFlags, logical, true, ref entry);
                idealPos.Add(IdealArcPoint(arcStart, arcEnd, floor.AngleLength, floor.IsCCW, cumulative));
                SubClick sub = clicks[c];
                sub.PlayableSeq = p;
                insertedClicks.Add(new InsertedClick
                {
                    SubTrackId = sub.TrackId,
                    SubSeq = sub.SubSeq,
                    PlayableSeq = p,
                    SongTime = sub.Time
                });
                p++;
                if (c < clicks.Count - 1)
                {
                    cumulative += fractions[c + 1];
                    entry = EmitSweep(floor, remain * fractions[c + 1], angles, toLogical, insertedFlags, logical, true, ref entry);
                    idealPos.Add(IdealArcPoint(arcStart, arcEnd, floor.AngleLength, floor.IsCCW, cumulative));
                    p++;
                }
            }

            angles.Add(originalExit);
            toLogical.Add(logical);
            insertedFlags.Add(true);
            idealPos.Add(arcEnd);
            return p + 1;
        }

        private static Dictionary<int, List<SubClick>> GroupClicks(List<SubPlan> plans, TrackTimeline mainTl,
            int logicalFloors)
        {
            var groups = new Dictionary<int, List<SubClick>>();
            if (logicalFloors < 2) return groups;

            double trackEnd = mainTl[logicalFloors - 1].EntryTime;

            foreach (SubPlan plan in plans)
            {
                foreach (SubClick click in plan.Clicks)
                {
                    if (click.Pseudo) continue;
                    if (click.Time >= trackEnd - TimeEps)
                        throw new CompileError("ClickAfterTrackEnd",
                            "副轨 " + plan.Track.Id + " 的 " + click.SubSeq + " 号砖点击在 " + Fmt(click.Time) +
                            "s，主轨最后一次点击在 " + Fmt(trackEnd) + "s，后面没有砖可以切", click.SubSeq);

                    int k = FindHostFloor(mainTl, click.Time);
                    if (k < 0)
                        throw new CompileError("CannotSplit",
                            "副轨 " + plan.Track.Id + " 的 " + click.SubSeq + " 号砖点击在 " + Fmt(click.Time) +
                            "s，找不到能承载它的主轨砖（时间窗落空），拒绝静默丢弃", click.SubSeq);
                    if (!groups.TryGetValue(k, out List<SubClick> list))
                    {
                        list = new List<SubClick>();
                        groups[k] = list;
                    }
                    list.Add(click);
                }
            }

            foreach (KeyValuePair<int, List<SubClick>> kv in groups)
                kv.Value.Sort(CompareByTime);

            return groups;
        }

        private static int CompareByTime(SubClick a, SubClick b) => a.Time.CompareTo(b.Time);

        private static int FindHostFloor(TrackTimeline tl, double t)
        {
            int last = tl.Count - 1;
            for (int i = 1; i <= last - 1; i++)
            {
                FloorTime f = tl[i];
                double t0 = f.EntryTime;
                double t1 = t0 + f.TravelTime;
                if (t < t0 - TimeEps || t >= t1) continue;
                if (SplitSpan(tl, f) <= TimeEps) continue;
                if (f.ExtraBeats > 0 || f.HoldBeats > 0)
                {
                    double arc = ArcTime(tl, f);
                    if (t - t0 >= arc - TimeEps)
                        throw new CompileError("ClickInPause",
                            "点击落在 " + i + " 号砖的 Pause/Hold 尾巴里（" + Fmt(t - t0) + "s，弧线只占 " +
                            Fmt(arc) + "s），那段时间不在轨道上", i);
                }
                return i;
            }
            return -1;
        }

        private static List<double> BuildFractions(TrackTimeline tl, int seq, List<SubClick> clicks)
        {
            FloorTime f = tl[seq];
            double span = SplitSpan(tl, f);
            if (span <= TimeEps)
                throw new CompileError("CannotSplit", seq + " 号砖的行程时间是 0，切不了", seq);

            var fractions = new List<double>(clicks.Count + 1);
            double prev = f.EntryTime;
            foreach (SubClick click in clicks)
            {
                double frac = (click.Time - prev) / span;
                if (frac < MinSegmentFraction)
                    throw new CompileError("CannotSplit",
                        seq + " 号砖上两次点击相隔太近（只有 " + Fmt(frac * 100) +
                        "% 的砖），切成这么小的段游戏会当回头弯", seq);
                fractions.Add(frac);
                prev = click.Time;
            }
            double tail = (f.EntryTime + span - prev) / span;
            if (tail < MinSegmentFraction)
                throw new CompileError("CannotSplit",
                    seq + " 号砖最后一次切分只剩 " + Fmt(tail * 100) + "% 的砖，游戏会当回头弯", seq);
            fractions.Add(tail);
            return fractions;
        }

        private static double SplitSpan(TrackTimeline tl, FloorTime f)
        {
            if (f.ExtraBeats > 0 || f.HoldBeats > 0) return ArcTime(tl, f);
            return f.TravelTime;
        }

        private static double ArcTime(TrackTimeline tl, FloorTime f)
        {
            double beats = f.AngleLength / Math.PI;
            return beats * tl.Crotchet / Math.Max(1e-9, f.Speed);
        }

        private static void AddPlanetMoves(List<ChartEvent> actions, SubPlan plan, FloorMap map,
            Vec2[] world, double mainBpm)
        {
            TrackTimeline tl = plan.Timeline;
            if (tl == null || tl.Count < 2 || world == null || world.Length < 2) return;

            string trackTag = DecorationBuilder.TrackTag(plan.Track.Id);
            string redTag = trackTag + DecorationBuilder.PlanetRedSuffix;
            string blueTag = trackTag + DecorationBuilder.PlanetBlueSuffix;
            Vec2 origin = world[1];

            for (int i = 1; i < tl.Count - 1 && i < world.Length; i++)
            {
                FloorTime f = tl[i];
                int trigger = i == 1 ? map.ToPlayable(plan.Anchor) : PlayableOfSubFloor(plan, i);
                if (trigger < 0) continue;

                double beats = f.TravelTime * mainBpm / 60.0;
                if (beats <= TimeEps) continue;

                Vec2 entry = TimelineBuilder.VectorFromAngle(f.EntryAngle);
                double sweepDeg = f.AngleLength * 180.0 / Math.PI;
                double rot = f.IsCCW ? sweepDeg : -sweepDeg;
                double cx = world[i].X - origin.X;
                double cy = world[i].Y - origin.Y;
                bool redSits = i % 2 == 1;

                AddOrbit(actions, trigger, redSits ? redTag : blueTag, beats, cx, cy, 0, 0, 0);
                AddOrbit(actions, trigger, redSits ? blueTag : redTag, beats, cx, cy, entry.X, entry.Y, rot);
            }
        }

        private static int PlayableOfSubFloor(SubPlan plan, int subSeq)
        {
            foreach (SubClick click in plan.Clicks)
                if (click.SubSeq == subSeq && click.PlayableSeq >= 0) return click.PlayableSeq;
            return -1;
        }

        private static void AddOrbit(List<ChartEvent> actions, int floor, string tag, double beats,
            double cx, double cy, double rx, double ry, double rot)
        {
            actions.Add(DecorationBuilder.OrbitMove(floor, tag, 0, cx, cy, rx, ry, 0));
            actions.Add(DecorationBuilder.OrbitMove(floor, tag, beats, cx, cy, rx, ry, rot));
        }

        private static string Fmt(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
