using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PtaMultichart.Core.Compile;
using PtaMultichart.Core.Model;
using PtaMultichart.Core.Timing;
using static PtaMultichart.Tests.TestHarness;

namespace PtaMultichart.Tests
{
    internal static class PreflightAndRegressionTests
    {
        public static void RunAll()
        {
            TestPreflight();
            TestTagRetreat();
            TestLevelRegression();
            TestSubPositionTrack();
            TestUserClickSequence();
        }

        private static void TestPreflight()
        {
            Console.WriteLine("[预检：方案 v2 §8 的 6 条]");
            var main = MainTrack(180, 180);
            var sub = SubTrack(1, 1, 0, 90);
            PlayableChart pc = ChartCompiler.Compile(Chart(main, sub), Settings());

            PreflightReport rep = pc.Preflight;
            Check(rep != null, "编译结果带预检（PlayableChart.Preflight）");
            if (rep == null) return;

            Check(rep.Count == 6, "预检 6 条", "count=" + rep.Count);
            string[] order =
            {
                PreflightCode.InsertedClicks, PreflightCode.PositionCompensation,
                PreflightCode.DecorationPolyline, PreflightCode.SubTrackOrigin,
                PreflightCode.ConditionalTagConflict, PreflightCode.LastEntryBeat
            };
            bool orderOk = rep.Count == order.Length;
            for (int i = 0; orderOk && i < order.Length; i++)
                if (rep.Items[i].Code != order[i]) orderOk = false;
            Check(orderOk, "条目 id 与顺序 = 方案 §8 的 1..6");

            PreflightItem it1 = rep.Find(PreflightCode.InsertedClicks);
            Check(it1 != null && !it1.IsWarning, "① 是信息、不是警告");
            Check(it1 != null && it1.Message.Contains("插入点击砖 1 块") && it1.Message.Contains("透明中旋"),
                "① 文案：插入 1 块 / 透明中旋", it1 == null ? "null" : it1.Message);

            PreflightItem it2 = rep.Find(PreflightCode.PositionCompensation);
            Check(it2 != null && it2.Message.Contains("轨道形状不变"),
                "② 文案：拉回编辑器位置，形状不变", it2 == null ? "null" : it2.Message);

            PreflightItem it3 = rep.Find(PreflightCode.DecorationPolyline);
            Check(it3 != null && it3.Message.Contains("线性折线"), "③ 装饰为线性折线",
                it3 == null ? "null" : it3.Message);

            PreflightItem it4 = rep.Find(PreflightCode.SubTrackOrigin);
            Check(it4 != null && it4.Message.Contains("seq1 不插入") && it4.Message.Contains("幽灵"),
                "④ 文案：seq1 不插入 / 0 号砖幽灵", it4 == null ? "null" : it4.Message);

            PreflightItem it5 = rep.Find(PreflightCode.ConditionalTagConflict);
            Check(it5 != null && !it5.IsWarning, "⑤ 无交集时不报警告",
                it5 == null ? "null" : it5.Kind);
            Check(it5 != null && it5.Message.Contains("无交集"), "⑤ 无交集文案",
                it5 == null ? "null" : it5.Message);

            PreflightItem it6 = rep.Find(PreflightCode.LastEntryBeat);
            Check(it6 != null && it6.Message.Contains("最后一块 entryBeat 为 0"), "⑥ 最后一块 entryBeat 为 0",
                it6 == null ? "null" : it6.Message);

            Check(rep.WarningCount == 0 && !rep.HasWarnings, "无交集时 0 条警告", "warns=" + rep.WarningCount);
            Check(rep.InsertedClicks == 1 && rep.PositionTrackEvents >= 1 && rep.PolylineSegments == 2,
                "统计字段：1 块插入，并且有 PositionTrack",
                rep.InsertedClicks + "/" + rep.PositionTrackEvents + "/" + rep.PolylineSegments);
            Check(rep.ToText().Split('\n').Length == 6, "ToText() 输出 6 行");
            Console.WriteLine("       [sample] " + it1.Message);
            Console.WriteLine("       [sample] " + it2.Message + "  -- " + it2.Detail);
            Console.WriteLine("       [sample] " + it5.Message);
        }

        private static PlayableChart CompileRepeatChartWithBucket(string bucketTag, out LogicalChart chart)
        {
            var main = MainTrack(180, 180, 180, 180);
            var repeat = new ChartEvent("RepeatEvents", 1);
            repeat.Data["repeatType"] = "Floor";
            repeat.Data["gapLength"] = 1;
            repeat.Data["floorCount"] = 3;
            repeat.Data["tag"] = "rep";
            main.Events.Add(repeat);

            var hitsound = new ChartEvent("SetHitsound", 1);
            hitsound.Data["eventTag"] = "rep keep";
            hitsound.Data["hitsound"] = "Kick";
            main.Events.Add(hitsound);

            if (bucketTag != null)
            {
                var cond = new ChartEvent("SetConditionalEvents", 1);
                cond.Data["perfectTag"] = bucketTag;
                cond.Data["missTag"] = "nope";
                main.Events.Add(cond);
            }

            var sub = SubTrack(1, 1, 0, 90);
            chart = Chart(main, sub);
            return ChartCompiler.Compile(chart, Settings());
        }

        private static void TestTagRetreat()
        {
            Console.WriteLine("[tag 退让：条件事件桶 tag 与 pta-mc-unrolled 交集]");

            LogicalChart chartConflict;
            PlayableChart conflict = CompileRepeatChartWithBucket(ChartEvent.UnrolledTag, out chartConflict);
            Check(conflict != null, "有交集时编译仍然成功");
            if (conflict == null) return;

            Check(All(conflict.Actions, "RepeatEvents").Count == 0, "走了展开路径（RepeatEvents 已删）");
            var hits = new List<ChartEvent>();
            foreach (var e in conflict.Actions)
                if (e.Type == "SetHitsound") hits.Add(e);
            Check(hits.Count == 4, "原事件 + 3 个副本", "count=" + hits.Count);

            var copies = new List<ChartEvent>();
            var original = new List<ChartEvent>();
            foreach (var h in hits)
            {
                if (h.Floor == 1) original.Add(h); else copies.Add(h);
            }
            Check(original.Count == 1 && original[0].GetString("eventTag") == "rep keep",
                "宿主砖上的原事件 tag 未被改",
                original.Count == 0 ? "none" : original[0].GetString("eventTag"));

            int withUnrolled = 0;
            int keptAuthorTag = 0;
            foreach (var c in copies)
            {
                string tag = c.GetString("eventTag");
                if (tag == null) continue;
                if (tag.Contains("pta-mc-unrolled")) withUnrolled++;
                if (tag == "keep") keptAuthorTag++;
            }
            Check(withUnrolled == 0, "交集字符串 pta-mc-unrolled 没有被写入副本", "hit=" + withUnrolled);
            Check(keptAuthorTag == copies.Count, "作者 tag keep 原样保留在副本上",
                "ok=" + keptAuthorTag + "/" + copies.Count);

            var cond = Find(conflict.Actions, "SetConditionalEvents");
            Check(cond != null && cond.GetString("perfectTag") == ChartEvent.UnrolledTag,
                "作者写的条件桶 tag 一个字没动",
                cond == null ? "missing" : cond.GetString("perfectTag"));

            Check(conflict.Preflight != null && conflict.Preflight.TagConflicts.Count == 1 &&
                  conflict.Preflight.TagConflicts[0] == ChartEvent.UnrolledTag,
                "预检记录 1 个冲突字符串",
                conflict.Preflight == null ? "null" : string.Join(",", conflict.Preflight.TagConflicts.ToArray()));

            PreflightItem warn = conflict.Preflight == null ? null : conflict.Preflight.Find(PreflightCode.ConditionalTagConflict);
            Check(warn != null && warn.IsWarning, "⑤ 变成警告", warn == null ? "null" : warn.Kind);
            Check(warn != null && warn.Message.Contains("pta-mc-unrolled") && warn.Message.Contains("已保留作者 tag"),
                "⑤ 文案列出交集并说明已保留作者 tag", warn == null ? "null" : warn.Message);
            Check(conflict.Preflight != null && conflict.Preflight.WarningCount == 1,
                "警告数 = 1", conflict.Preflight == null ? "null" : conflict.Preflight.WarningCount.ToString());
            Console.WriteLine("       [sample] " + warn.Message);

            LogicalChart chartPlain;
            PlayableChart plain = CompileRepeatChartWithBucket(null, out chartPlain);
            bool sameAngles = plain.AngleData.Count == conflict.AngleData.Count;
            for (int i = 0; sameAngles && i < plain.AngleData.Count; i++)
                if (plain.AngleData[i] != conflict.AngleData[i]) sameAngles = false;
            Check(sameAngles, "有交集/无交集的播放谱角度逐元素相同（退让不动几何）");

            var plainCopies = new List<ChartEvent>();
            foreach (var e in plain.Actions)
                if (e.Type == "SetHitsound" && e.Floor != 1) plainCopies.Add(e);
            int exact = 0;
            foreach (var c in plainCopies)
                if (c.GetString("eventTag") == "keep pta-mc-unrolled") exact++;
            Check(plainCopies.Count == 3 && exact == 3,
                "无交集时副本输出与第一轮一致：keep pta-mc-unrolled",
                "ok=" + exact + "/" + plainCopies.Count);
            Check(plain.Preflight != null && plain.Preflight.WarningCount == 0 &&
                  plain.Preflight.TagConflicts.Count == 0,
                "无交集时 0 警告、0 冲突");
        }

        private static void TestLevelRegression()
        {
            Console.WriteLine("[5 张 testlevels 播放谱 angleData 回归（不放回 LevelGen）]");
            string dir = FindTestLevelDir();
            Check(dir != null, "找到 testlevels 目录", dir == null ? "not found" : dir);
            if (dir == null) return;

            string[] names = { "t1_insert", "t2_repeat_const", "t3_repeat_var", "t4_midspin", "t6_two_subs" };
            foreach (string name in names)
            {
                string path = Path.Combine(dir, name + ".adofai");
                if (!File.Exists(path))
                {
                    Check(false, name + ": 基线文件存在", path);
                    continue;
                }

                var level = MiniJson.Deserialize(File.ReadAllText(path)) as Dictionary<string, object>;
                var angles = level != null && level.ContainsKey("angleData") ? level["angleData"] as List<object> : null;
                Check(angles != null, name + ": 基线里有 angleData");
                if (angles == null) continue;

                LogicalChart chart = RestoreChart(level);
                Check(chart != null, name + ": 从 EditorComment 记号还原出逻辑谱");
                if (chart == null) continue;

                PlayableChart pc = ChartCompiler.Compile(chart, new CompileSettings { Bpm = 100 });
                Check(pc.AngleData.Count >= chart.Main.AngleData.Count,
                    name + ": 播放谱段数不少于逻辑谱",
                    "recompiled=" + pc.AngleData.Count);

                int inserted = pc.InsertedClicks.Count;
                Check(pc.Preflight != null && pc.Preflight.InsertedClicks == inserted,
                    name + ": 预检的插入点击数与记录一致（" + inserted + "）",
                    "map=" + pc.Map.InsertedCount);

                var wantTags = new List<string>();
                if (level["actions"] is List<object> baseActions)
                {
                    foreach (var a in baseActions)
                    {
                        var ev = a as Dictionary<string, object>;
                        if (ev == null) continue;
                        string type = Convert.ToString(ev.ContainsKey("eventType") ? ev["eventType"] : null, CultureInfo.InvariantCulture);
                        if (type == "EditorComment" || !ev.ContainsKey("eventTag")) continue;
                        string bt = Convert.ToString(ev["eventTag"], CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(bt)) wantTags.Add(bt);
                    }
                }
                var gotTags = new List<string>();
                foreach (var e in pc.Actions)
                {
                    if (e.Type == "EditorComment" || !e.Has("eventTag")) continue;
                    string gt = e.GetString("eventTag", "");
                    if (!string.IsNullOrEmpty(gt)) gotTags.Add(gt);
                }
                wantTags.Sort();
                gotTags.Sort();
                bool tagsSame = wantTags.Count == gotTags.Count;
                for (int i = 0; tagsSame && i < wantTags.Count; i++)
                    if (wantTags[i] != gotTags[i]) tagsSame = false;
                Check(tagsSame, name + ": 非空 eventTag 输出与第一轮一致（" + wantTags.Count + " 个）",
                    tagsSame ? "" : "want=[" + string.Join(",", wantTags.ToArray()) + "] got=[" +
                                     string.Join(",", gotTags.ToArray()) + "]");

                TrackTimeline logicalTl = TimelineBuilder.Build(chart.Main.AngleData, chart.Main.Events, 100);
                TrackTimeline playTl = ChartCompiler.RebuildTimeline(pc, 100);
                bool timeOk = true;
                int badFloor = -1;
                for (int L = 1; L < logicalTl.Count; L++)
                {
                    double want = logicalTl[L].EntryTime;
                    double got = playTl[pc.Map.ToPlayable(L)].EntryTime;
                    if (Math.Abs(want - got) > 0.002) { timeOk = false; badFloor = L; break; }
                }
                Check(timeOk, name + ": 主轨每次点击的时间与逻辑谱一致",
                    timeOk ? "" : "floor " + badFloor);
            }
        }

        private static void TestSubPositionTrack()
        {
            Console.WriteLine("[副轨 PositionTrack：移动装饰，不改点击时间]");
            var main = MainTrack(0, 0, 0, 0, 0, 0, 0, 0);
            var plain = SubTrack(1, 1, 0, 90, 0);
            PlayableChart before = ChartCompiler.Compile(Chart(main, plain), Settings());

            var moved = SubTrack(1, 1, 0, 90, 0);
            var pt = new ChartEvent("PositionTrack", 1);
            pt.Data["positionOffset"] = ChartEvent.Pair(1.0, 0.0);
            pt.Data["justThisTile"] = true;
            moved.Events.Add(pt);
            PlayableChart after = ChartCompiler.Compile(Chart(main, moved), Settings());

            List<ChartEvent> a = FloorDecos(before);
            List<ChartEvent> b = FloorDecos(after);
            Check(a.Count == b.Count && a.Count >= 2, "两边都画出副轨砖", a.Count + "/" + b.Count);
            if (a.Count >= 2 && b.Count >= 2)
            {
                Vec2 p0 = ReadVec(a[0], "position");
                Vec2 q0 = ReadVec(b[0], "position");
                Vec2 p1 = ReadVec(a[1], "position");
                Vec2 q1 = ReadVec(b[1], "position");
                Near(q0.X - p0.X, 1, 1e-6, "1 号砖按 PositionTrack 右移 1 砖");
                Near(q0.Y - p0.Y, 0, 1e-6, "1 号砖 y 不变");
                Near(q1.X - p1.X, 0, 1e-6, "justThisTile 不把 2 号砖一起带走 x");
                Near(q1.Y - p1.Y, 0, 1e-6, "justThisTile 不把 2 号砖一起带走 y");
            }

            TrackTimeline ta = ChartCompiler.RebuildTimeline(before, 100);
            TrackTimeline tb = ChartCompiler.RebuildTimeline(after, 100);
            bool same = ta.Count == tb.Count;
            for (int i = 0; same && i < ta.Count; i++)
                if (Math.Abs(ta[i].EntryTime - tb[i].EntryTime) > 1e-6) same = false;
            Check(same, "PositionTrack 不改变点击时间");

            var encoded = SubTrack(1, 1, 0, 90, 0);
            var raw = new ChartEvent("PositionTrack", 1);
            raw.Data["positionOffset"] = new object[] { 3m, 3m };
            encoded.Events.Add(raw);
            PlayableChart fromGame = ChartCompiler.Compile(Chart(main, encoded), Settings());
            List<ChartEvent> c = FloorDecos(fromGame);
            Check(c.Count >= 2, "游戏编码的偏移也能画出副轨");
            if (a.Count >= 2 && c.Count >= 2)
            {
                Vec2 base0 = ReadVec(a[0], "position");
                Vec2 got0 = ReadVec(c[0], "position");
                Vec2 base1 = ReadVec(a[1], "position");
                Vec2 got1 = ReadVec(c[1], "position");
                Near(got0.X - base0.X, 3, 1e-6, "object[] 偏移让 1 号砖 x +3");
                Near(got0.Y - base0.Y, 3, 1e-6, "object[] 偏移让 1 号砖 y +3");
                Near(got1.X - base1.X, 3, 1e-6, "偏移继续传到后面的砖 x");
                Near(got1.Y - base1.Y, 3, 1e-6, "偏移继续传到后面的砖 y");
            }
        }

        private static void TestUserClickSequence()
        {
            Console.WriteLine("[当前关卡：播放谱点击时间 = 两条轨按时间合并]");
            var main = MainTrack(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var sub = SubTrack(1, 3, 0, 90, 90, 90, 180, 180, 180, 180);
            PlayableChart pc = ChartCompiler.Compile(Chart(main, sub), Settings());
            TrackTimeline played = ChartCompiler.RebuildTimeline(pc, 100);

            double[] expect =
            {
                0, 0.6, 1.2, 1.5, 1.8, 2.1, 2.4, 2.7,
                3.0, 3.0 + 0.6 / 180.0,
                3.6, 3.6 + 0.6 / 180.0,
                4.2, 4.2 + 0.6 / 180.0,
                4.8, 4.8 + 0.6 / 180.0,
                5.4
            };
            var got = new List<double>();
            for (int i = 0; i < played.Count; i++)
            {
                if (!played[i].IsClick) continue;
                got.Add(played[i].EntryTime);
            }
            Check(got.Count == expect.Length, "点击次数 = 主轨 10 次 + 副轨 7 次",
                "got=" + got.Count + " want=" + expect.Length);
            bool ok = got.Count == expect.Length;
            int bad = -1;
            for (int i = 0; ok && i < expect.Length; i++)
            {
                if (Math.Abs(got[i] - expect[i]) > 0.002) { ok = false; bad = i; }
            }
            Check(ok, "每次点击的歌曲时间与合并后的序列一致",
                ok ? "" : "第 " + bad + " 次 got=" + (bad >= 0 && bad < got.Count ? Fmt(got[bad]) : "?") +
                          " want=" + (bad >= 0 ? Fmt(expect[bad]) : "?"));

            var placed = SimulatePositions(pc, 100);
            double worst = 0;
            for (int i = 1; i < placed.Count; i++)
            {
                double dx = placed[i].X - placed[i - 1].X;
                double dy = placed[i].Y - placed[i - 1].Y;
                double err = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - 1.0);
                if (err > worst) worst = err;
            }
            Check(worst < 0.05, "相邻砖仍相距 1 块砖，星球落得上", "worst=" + worst.ToString("0.###"));
        }

        private static string FindTestLevelDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "testlevels");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "README.md")))
                    return candidate;
                candidate = Path.Combine(dir.FullName, "project", "pta-multichart", "testlevels");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        private static LogicalChart RestoreChart(Dictionary<string, object> level)
        {
            if (level == null || !level.ContainsKey("actions")) return null;
            if (!(level["actions"] is List<object> actions)) return null;
            foreach (var a in actions)
            {
                if (!(a is Dictionary<string, object> ev)) continue;
                if (Convert.ToString(ev.ContainsKey("eventType") ? ev["eventType"] : null, CultureInfo.InvariantCulture) != "EditorComment")
                    continue;
                string comment = Convert.ToString(ev.ContainsKey("comment") ? ev["comment"] : null, CultureInfo.InvariantCulture);
                if (!LogicalChart.IsMarkerComment(comment)) continue;
                return LogicalChart.FromJson(LogicalChart.StripMarker(comment));
            }
            return null;
        }
    }
}
