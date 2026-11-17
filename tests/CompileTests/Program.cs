using System;
using System.Collections.Generic;
using PtaMultichart.Core.Compile;
using PtaMultichart.Core.Model;
using PtaMultichart.Core.Timing;
using static PtaMultichart.Tests.TestHarness;

namespace PtaMultichart.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            TestBasicInsert();
            TestPositionCompensation();
            TestSubDecoration();
            TestSubFloorIcons();
            TestRepeatConstant();
            TestRepeatUnroll();
            TestTileRelativeTo();
            TestOverlapError();
            TestPseudoAndMid();
            TestUnsupportedSubEvent();
            TestMidSpinSubTrack();
            TestClickInPause();
            PreflightAndRegressionTests.RunAll();

            Console.WriteLine();
            Console.WriteLine(Failures == 0
                ? "ALL TESTS PASSED (" + Checks + " checks)"
                : Failures + " FAILURES out of " + Checks + " checks");
            return Failures == 0 ? 0 : 1;
        }

        private static void TestBasicInsert()
        {
            Console.WriteLine("[basic insert: 主轨 [180,180] + 副轨 0.5 拍点击]");
            var main = MainTrack(180, 180);
            main.Decorations.Add(new ChartEvent("AddDecoration", 2));
            var sub = SubTrack(1, 1, 0, 90);

            PlayableChart pc = ChartCompiler.Compile(Chart(main, sub), Settings());

            Check(pc.AngleData.Count == 4, "播放谱 angleData = 4 段（半段 + 中旋 + 原角度）", "count=" + pc.AngleData.Count);
            Check(pc.FloorCount == 5, "播放谱砖数 = 5", "floors=" + pc.FloorCount);
            Near(pc.AngleData[1], 270, 1e-3, "切出来的第一段 angleData = 270（原 180 的一半）");
            Check(pc.AngleData[2] == 999f, "插入的点击是中旋 999");
            Check(Math.Abs(pc.AngleData[3] - 180f) < 1e-3, "最后一段写回原来的 180，后面的入口角不变");

            Check(pc.Map.LogicalCount == 3, "逻辑砖数 = 3", "=" + pc.Map.LogicalCount);
            Check(pc.Map.Count == 5, "播放谱砖数（FloorMap）= 5", "=" + pc.Map.Count);
            Check(pc.Map.ToPlayable(1) == 1, "逻辑 1 号砖 → 播放 1 号砖（第一段）", "=" + pc.Map.ToPlayable(1));
            Check(pc.Map.ToPlayable(2) == 4, "逻辑 2 号砖 → 播放 4 号砖（中旋和收尾各占一块）", "=" + pc.Map.ToPlayable(2));
            Check(pc.Map.ToLogical(2) == 1, "播放 2 号砖 → 逻辑 1 号砖（插入砖算它前面那块）", "=" + pc.Map.ToLogical(2));
            Check(pc.Map.IsInserted(2) && pc.Map.IsInserted(3), "中旋和收尾都藏起来");
            Check(!pc.Map.IsInserted(1) && !pc.Map.IsInserted(4), "宿主和终点不是插入砖");

            Check(pc.InsertedClicks.Count == 1, "InsertedClicks = 1 条", "count=" + pc.InsertedClicks.Count);
            if (pc.InsertedClicks.Count == 1)
            {
                InsertedClick ic = pc.InsertedClicks[0];
                Check(ic.SubTrackId == 1, "插入点击来自副轨 1", "=" + ic.SubTrackId);
                Check(ic.SubSeq == 2, "插入点击是副轨 2 号砖", "=" + ic.SubSeq);
                Check(ic.PlayableSeq == 2, "插入点击落在播放 2 号砖", "=" + ic.PlayableSeq);
                Near(ic.SongTime, 0.3, Eps, "插入点击的歌曲时间 = 0.3s（锚点后 0.5 拍）");
            }

            ChartEvent hide = null;
            foreach (var ev in FindAll(pc.Actions, "PositionTrack", 2))
                if (ev.GetDouble("opacity", 100) == 0) hide = ev;
            Check(hide != null && hide.GetBool("justThisTile"), "插入的中旋是透明的，并且 justThisTile");
            bool hostHidden = false;
            bool closerHidden = false;
            foreach (var ev in FindAll(pc.Actions, "PositionTrack", 1))
                if (ev.GetDouble("opacity", 100) == 0) hostHidden = true;
            foreach (var ev in FindAll(pc.Actions, "PositionTrack", 3))
                if (ev.GetDouble("opacity", 100) == 0) closerHidden = true;
            Check(hostHidden && closerHidden, "半截砖和收尾砖都藏起来");
            int masks = 0;
            foreach (var d in pc.Decorations)
                if (d.GetString("tag", "").Contains("pta-mc-mask")) masks++;
            Check(masks == 1, "用一块遮盖装饰补回原来的砖", "count=" + masks);

            var mainDeco = Find(pc.Decorations, "AddDecoration");
            Check(mainDeco != null, "主轨作者装饰还在");
            if (mainDeco != null)
                Check(mainDeco.Floor == 4, "主轨装饰 floor 2 → 播放 4（走砖号映射）", "floor=" + mainDeco.Floor);

            var marker = Find(pc.Actions, "EditorComment", 0);
            Check(marker != null, "0 号砖上有 EditorComment");
            if (marker != null)
            {
                Check(marker.Locked, "记号 locked = true");
                string comment = marker.GetString("comment", "");
                Check(comment.StartsWith("[[PTA-MC:v1]]\n", StringComparison.Ordinal), "记号以 [[PTA-MC:v1]] 换行开头");
                var back = LogicalChart.FromJson(LogicalChart.StripMarker(comment));
                Check(back.Tracks.Count == 2, "记号里的逻辑谱能还原出 2 条轨");
            }
            Check(All(pc.Actions, "EditorComment").Count == 1, "只写了一条记号");

            TrackTimeline tl = ChartCompiler.RebuildTimeline(pc, 100);
            Check(tl[2].IsClick, "插入砖在重算的时间线上确实是一次点击");
            Near(tl[2].EntryTime, 0.3, Eps, "插入砖的实际歌曲时间 = 0.3s（与副轨点击同一时刻）");
            Near(tl[1].EntryTime, 0.0, Eps, "1 号砖仍在 0s");
            Near(tl[tl.Count - 1].EntryTime, 0.6, 0.002, "主轨终点砖仍在 0.6s");

            TrackTimeline logical = TimelineBuilder.Build(main.AngleData, main.Events, 100);
            var placed = SimulatePositions(pc, 100);
            bool shape = true;
            string shapeDetail = "";
            for (int L = 0; L < logical.Count; L++)
            {
                Vec2 got = placed[pc.Map.ToPlayable(L)];
                Vec2 want = logical.Positions[L];
                if (Math.Abs(got.X - want.X) > 0.02 || Math.Abs(got.Y - want.Y) > 0.02)
                {
                    shape = false;
                    shapeDetail = "L" + L + " got=(" + got.X.ToString("0.###") + "," + got.Y.ToString("0.###") +
                        ") want=(" + want.X.ToString("0.###") + "," + want.Y.ToString("0.###") + ")";
                }
            }
            Check(shape, "每块逻辑砖拉回编辑器里的位置", shapeDetail);
        }

        private static void TestPositionCompensation()
        {
            Console.WriteLine("[播放谱：砖在星球轨道上，终点时间不后移]");
            var main = MainTrack(180, 180);
            var sub = SubTrack(1, 1, 0, 90);
            PlayableChart pc = ChartCompiler.Compile(Chart(main, sub), Settings());

            List<Vec2> sim = ChartCompiler.NaturalPositions(pc.AngleData, 100);
            bool unit = true;
            for (int i = 1; i < sim.Count; i++)
            {
                double len = (sim[i] - sim[i - 1]).Length;
                if (Math.Abs(len - 1) > 1e-4) unit = false;
            }
            Check(unit, "相邻砖相距 1 砖长（星球绕砖一圈半径正好落到下一块）");

            TrackTimeline played = ChartCompiler.RebuildTimeline(pc, 100);
            Near(played[2].EntryTime, 0.3, 0.002, "插入点击在 0.3s");
            Near(played[played.Count - 1].EntryTime, 0.6, 0.002, "终点仍在 0.6s");
        }

        private static void TestSubDecoration()
        {
            Console.WriteLine("[sub decorations: Floor / Planet / MoveDecorations]");
            var main = MainTrack(180, 180);
            var sub = SubTrack(1, 1, 0, 90);
            PlayableChart pc = ChartCompiler.Compile(Chart(main, sub), Settings());

            var floors = new List<ChartEvent>();
            var planets = new List<ChartEvent>();
            foreach (var d in pc.Decorations)
            {
                if (d.GetString("objectType") == "Floor" && !d.GetString("tag", "").Contains("pta-mc-mask")) floors.Add(d);
                if (d.GetString("objectType") == "Planet") planets.Add(d);
            }
            Check(floors.Count == 2, "副轨画 1 号砖和终点砖（幽灵 0 号不画）", "count=" + floors.Count);
            Check(planets.Count == 2, "副轨 → 红蓝 2 个 Planet 装饰", "count=" + planets.Count);

            if (floors.Count >= 1)
            {
                Check(floors[0].GetString("tag") == "pta-mc pta-mc-t1", "Floor 装饰 tag = pta-mc pta-mc-t1",
                    floors[0].GetString("tag"));
                Check(floors[0].GetString("trackType") == "Normal", "普通砖 trackType = Normal");
                Vec2 p0 = ReadVec(floors[0], "position");
                Near(p0.X, -1.0, 1e-9, "副轨 1 号砖落在锚点砖世界坐标 x = -1");
                Near(p0.Y, 0.0, 1e-9, "副轨 1 号砖落在锚点砖世界坐标 y = 0");
                Near(floors[0].GetDouble("trackAngle", -1), 90, 1e-4, "trackAngle 是 90° 的转角");
                Near(floors[0].GetDouble("trackOpacity", -1), 100, 1e-9, "trackOpacity = 100");
            }

            ChartEvent red = FindByTag(pc.Decorations, "pta-mc pta-mc-t1 pta-mc-t1-red");
            ChartEvent blue = FindByTag(pc.Decorations, "pta-mc pta-mc-t1 pta-mc-t1-blue");
            Check(red != null && red.GetString("planetColorType") == "DefaultRed", "红色星球 planetColorType = DefaultRed");
            Check(blue != null && blue.GetString("planetColorType") == "DefaultBlue", "蓝色星球 planetColorType = DefaultBlue");
            if (red != null)
            {
                Vec2 rp = ReadVec(red, "position");
                Near(rp.X, -1.0, 1e-9, "红星球起点 = 锚点砖 x");
                Near(rp.Y, 0.0, 1e-9, "红星球起点 = 锚点砖 y");
            }

            var moves = All(pc.Actions, "MoveDecorations");
            Check(moves.Count == 4, "这块砖上红蓝各先归位再旋转", "count=" + moves.Count);
            ChartEvent spin = null;
            foreach (var m in moves)
                if (m.GetString("tag") == "pta-mc-t1-blue" && m.GetDouble("duration", -1) > 0.1) spin = m;
            Check(spin != null, "蓝星球绕着 1 号砖转，红星球停在砖心");
            if (spin != null)
            {
                Check(spin.Floor == 1, "旋转挂在锚点砖（播放 1 号砖）", "floor=" + spin.Floor);
                Near(spin.GetDouble("duration", -1), 0.5, 1e-6, "duration = 0.5 拍");
                Check(spin.GetString("ease") == "Linear", "ease = Linear");
                Vec2 off = ReadVec(spin, "positionOffset");
                Near(off.X, 0.0, 1e-6, "轴心留在锚点");
                Near(off.Y, 0.0, 1e-6, "轴心留在锚点");
                Vec2 radius = ReadVec(spin, "pivotOffset");
                Near(radius.X, -1.0, 1e-4, "从入口方向（砖的左侧）出发");
                Near(spin.GetDouble("rotationOffset", 999), -90, 0.05, "顺时针转 90°");
            }
        }

        private static void TestSubFloorIcons()
        {
            Console.WriteLine("[副轨装饰砖带上旋转 / 变速 / 长按图标]");
            var sub = SubTrack(1, 1, 0, 90, 180, 180);
            sub.Events.Add(new ChartEvent("Twirl", 1));
            var speed = new ChartEvent("SetSpeed", 2);
            speed.Data["speedType"] = "Bpm";
            speed.Data["beatsPerMinute"] = 200;
            sub.Events.Add(speed);
            var hold = new ChartEvent("Hold", 3);
            hold.Data["duration"] = 2;
            sub.Events.Add(hold);

            PlayableChart pc = ChartCompiler.Compile(Chart(MainTrack(180, 180, 180, 180, 180, 180, 180), sub), Settings());
            var floors = SubFloors(pc);
            Check(floors.Count >= 4, "四块可见砖都有装饰", "count=" + floors.Count);
            if (floors.Count >= 4)
            {
                Check(floors[0].GetString("trackIcon") == "Swirl", "1 号砖的旋转是漩涡", floors[0].GetString("trackIcon"));
                Check(floors[0].GetBool("trackIconFlipped"), "逆时针旋转，漩涡翻转");
                var tl = TimelineBuilder.Build(sub.AngleData, sub.Events, 100);
                double sweep = tl[1].AngleLength;
                if (sweep <= 1e-6 && !tl[1].MidSpin) sweep = Math.PI * 2;
                Check(floors[0].GetBool("trackRedSwirl") == (sweep < Math.PI), "漩涡红蓝按转角是否小于 180°");
                Check(floors[1].GetString("trackIcon") == "Rabbit", "2 倍速是兔子", floors[1].GetString("trackIcon"));
                Near(floors[1].GetDouble("trackSetSpeedIconBpm", 0), 200, 1e-6, "兔子按 200 BPM 跳");
                Check(floors[2].GetString("trackIcon") == "HoldArrowLong", "长按是长箭头", floors[2].GetString("trackIcon"));
                Check(floors[3].GetString("trackIcon") == "HoldReleaseLong", "松开长按的下一块是松开图标",
                    floors[3].GetString("trackIcon"));
            }

            var fast = SubTrack(1, 1, 0, 180);
            var triple = new ChartEvent("SetSpeed", 1);
            triple.Data["speedType"] = "Bpm";
            triple.Data["beatsPerMinute"] = 300;
            fast.Events.Add(triple);
            var floorsFast = SubFloors(ChartCompiler.Compile(Chart(MainTrack(180, 180, 180, 180, 180, 180), fast), Settings()));
            Check(floorsFast.Count >= 1 && floorsFast[0].GetString("trackIcon") == "DoubleRabbit",
                "超过 2 倍是双兔", floorsFast.Count == 0 ? "none" : floorsFast[0].GetString("trackIcon"));

            var slow = SubTrack(1, 1, 0, 180);
            var half = new ChartEvent("SetSpeed", 1);
            half.Data["speedType"] = "Bpm";
            half.Data["beatsPerMinute"] = 50;
            slow.Events.Add(half);
            var floorsSlow = SubFloors(ChartCompiler.Compile(Chart(MainTrack(180, 180, 180, 180, 180, 180), slow), Settings()));
            Check(floorsSlow.Count >= 1 && floorsSlow[0].GetString("trackIcon") == "Snail",
                "半速是蜗牛", floorsSlow.Count == 0 ? "none" : floorsSlow[0].GetString("trackIcon"));

            var marked = SubTrack(1, 1, 0, 180);
            marked.Events.Add(new ChartEvent("Checkpoint", 1));
            var floorsMarked = SubFloors(ChartCompiler.Compile(Chart(MainTrack(180, 180, 180, 180, 180, 180), marked), Settings()));
            Check(floorsMarked.Count >= 1 && floorsMarked[0].GetString("trackIcon") == "Checkpoint",
                "检查点用检查点图标", floorsMarked.Count == 0 ? "none" : floorsMarked[0].GetString("trackIcon"));

            var chosen = SubTrack(1, 1, 0, 180);
            chosen.Events.Add(new ChartEvent("Twirl", 1));
            var setIcon = new ChartEvent("SetFloorIcon", 1);
            setIcon.Data["icon"] = "Checkpoint";
            chosen.Events.Add(setIcon);
            var floorsChosen = SubFloors(ChartCompiler.Compile(Chart(MainTrack(180, 180, 180, 180, 180, 180), chosen), Settings()));
            Check(floorsChosen.Count >= 1 && floorsChosen[0].GetString("trackIcon") == "Checkpoint",
                "指定的地板图标盖过旋转", floorsChosen.Count == 0 ? "none" : floorsChosen[0].GetString("trackIcon"));
        }

        private static void TestRepeatConstant()
        {
            Console.WriteLine("[RepeatEvents Floor / gapLength=1 / 每块之间都插 → 只改数字]");
            var main = MainTrack(180, 180, 180, 180);
            var repeat = new ChartEvent("RepeatEvents", 1);
            repeat.Data["repeatType"] = "Floor";
            repeat.Data["gapLength"] = 1;
            repeat.Data["floorCount"] = 3;
            repeat.Data["tag"] = "grp";
            main.Events.Add(repeat);

            var color = new ChartEvent("ColorTrack", 1);
            color.Data["eventTag"] = "grp";
            color.Data["trackColor"] = "ff0000";
            main.Events.Add(color);

            var sub = SubTrack(1, 1, 0, 90, 90, 90);
            PlayableChart pc = ChartCompiler.Compile(Chart(main, sub), Settings());

            Check(pc.AngleData.Count == 10, "三块砖各切一刀 → 10 段", "count=" + pc.AngleData.Count);
            Check(pc.InsertedClicks.Count == 3, "插了 3 次点击", "count=" + pc.InsertedClicks.Count);
            Check(pc.Map.ToPlayable(1) == 1 && pc.Map.ToPlayable(2) == 4 &&
                  pc.Map.ToPlayable(3) == 7 && pc.Map.ToPlayable(4) == 10,
                "步长恒为 3（1→4→7→10）");

            var reps = All(pc.Actions, "RepeatEvents");
            Check(reps.Count == 1, "播放谱里仍是一条 RepeatEvents（只改数字）", "count=" + reps.Count);
            if (reps.Count == 1)
            {
                Check(reps[0].Floor == 1, "RepeatEvents 落在映射后的 1 号砖", "floor=" + reps[0].Floor);
                Check(reps[0].GetInt("gapLength", -1) == 3, "gapLength 1 → 3", "=" + reps[0].GetInt("gapLength", -1));
                Check(reps[0].GetInt("floorCount", -1) == 3, "floorCount 不变 = 3");
                Check(reps[0].GetString("tag") == "grp", "tag 不变");
            }
            Check(All(pc.Actions, "ColorTrack").Count == 1, "没有展开出副本");
            Check(Find(pc.Actions, "ColorTrack") != null && Find(pc.Actions, "ColorTrack").Floor == 1,
                "ColorTrack 跟着落在 1 号砖");
            bool anyUnrolled = false;
            foreach (var e in pc.Actions)
                if (e.GetString("eventTag", "").Contains("pta-mc-unrolled")) anyUnrolled = true;
            Check(!anyUnrolled, "没有 pta-mc-unrolled 标记");

            TrackTimeline logical = TimelineBuilder.Build(main.AngleData, main.Events, 100);
            TrackTimeline played = ChartCompiler.RebuildTimeline(pc, 100);
            bool timesOk = true;
            for (int L = 1; L < logical.Count; L++)
            {
                double want = logical[L].EntryTime;
                double got = played[pc.Map.ToPlayable(L)].EntryTime;
                if (Math.Abs(want - got) > 0.002) timesOk = false;
            }
            Check(timesOk, "每块逻辑砖的点击时间在播放谱里保持不变");
        }

        private static void TestRepeatUnroll()
        {
            Console.WriteLine("[RepeatEvents Floor / gapLength=1 / 只有一段插 → 展开成 pta-mc-unrolled]");
            var main = MainTrack(180, 180, 180, 180);
            var repeat = new ChartEvent("RepeatEvents", 1);
            repeat.Data["repeatType"] = "Floor";
            repeat.Data["gapLength"] = 1;
            repeat.Data["floorCount"] = 3;
            repeat.Data["tag"] = "grp";
            main.Events.Add(repeat);

            var color = new ChartEvent("ColorTrack", 1);
            color.Data["eventTag"] = "grp";
            color.Data["trackColor"] = "ff0000";
            main.Events.Add(color);

            var sub = SubTrack(1, 1, 0, 90);
            PlayableChart pc = ChartCompiler.Compile(Chart(main, sub), Settings());

            Check(pc.AngleData.Count == 6, "只有一块砖被切 → 6 段", "count=" + pc.AngleData.Count);
            Check(pc.Map.ToPlayable(1) == 1 && pc.Map.ToPlayable(2) == 4 &&
                  pc.Map.ToPlayable(3) == 5 && pc.Map.ToPlayable(4) == 6,
                "步长变成 3,1,1");

            Check(All(pc.Actions, "RepeatEvents").Count == 0, "播放谱里删掉了 RepeatEvents");

            var colors = All(pc.Actions, "ColorTrack");
            Check(colors.Count == 4, "原事件 + 3 个副本", "count=" + colors.Count);

            var byFloor = new Dictionary<int, ChartEvent>();
            foreach (var c in colors) byFloor[c.Floor] = c;
            Check(byFloor.ContainsKey(1) && byFloor.ContainsKey(4) &&
                  byFloor.ContainsKey(5) && byFloor.ContainsKey(6),
                "副本落在播放 4、5、6 号砖（对应逻辑 2、3、4）",
                string.Join(",", new List<int>(byFloor.Keys).ConvertAll(x => x.ToString()).ToArray()));

            if (byFloor.ContainsKey(1))
                Check(byFloor[1].GetString("eventTag") == "grp", "原始事件 tag 保持 grp");
            if (byFloor.ContainsKey(4))
                Check(byFloor[4].GetString("eventTag") == "pta-mc-unrolled",
                    "副本去掉匹配 tag、加 pta-mc-unrolled", byFloor[4].GetString("eventTag"));
            if (byFloor.ContainsKey(4))
                Check(byFloor[4].GetString("trackColor") == "ff0000", "副本内容照抄");
        }

        private static void TestTileRelativeTo()
        {
            Console.WriteLine("[TileRelativeTo: ThisTile / Start / End]");
            var main = MainTrack(180, 180, 180);

            var thisTile = new ChartEvent("PositionTrack", 1);
            thisTile.Data["tag"] = "case-thistile";
            thisTile.Data["relativeTo"] = ChartEvent.Pair(2, "ThisTile");
            main.Events.Add(thisTile);

            var start = new ChartEvent("PositionTrack", 0);
            start.Data["tag"] = "case-start";
            start.Data["relativeTo"] = ChartEvent.Pair(2, "Start");
            main.Events.Add(start);

            var end = new ChartEvent("PositionTrack", 0);
            end.Data["tag"] = "case-end";
            end.Data["relativeTo"] = ChartEvent.Pair(0, "End");
            main.Events.Add(end);

            var sub = SubTrack(1, 1, 0, 90);
            PlayableChart pc = ChartCompiler.Compile(Chart(main, sub), Settings());

            ChartEvent a = FindByTag(pc.Actions, "case-thistile");
            Check(a != null, "ThisTile 事件还在");
            if (a != null)
            {
                List<object> pair = a.GetList("relativeTo");
                Check(pair != null && pair.Count == 2, "relativeTo 还是 pair");
                if (pair != null && pair.Count == 2)
                {
                    Check((string)pair[1] == "ThisTile", "ThisTile 类型不变");
                    Check(Convert.ToInt32(pair[0]) == 4, "ThisTile 偏移 2 → 4（两端映射差）",
                        "=" + Convert.ToInt32(pair[0]));
                }
            }

            ChartEvent b = FindByTag(pc.Actions, "case-start");
            Check(b != null, "Start 事件还在");
            if (b != null)
            {
                List<object> pair = b.GetList("relativeTo");
                Check(pair != null && (string)pair[1] == "Start", "Start 类型不变");
                Check(pair != null && Convert.ToInt32(pair[0]) == 4, "Start 绝对 2 → 播放 4（映射后）",
                    pair == null ? "null" : "=" + Convert.ToInt32(pair[0]));
            }

            ChartEvent c = FindByTag(pc.Actions, "case-end");
            Check(c != null, "End 事件还在");
            if (c != null)
            {
                List<object> pair = c.GetList("relativeTo");
                Check(pair != null && (string)pair[1] == "Start", "End 写成 Start");
                Check(pair != null && Convert.ToInt32(pair[0]) == 5, "End 0 → 逻辑 3 → 播放 5",
                    pair == null ? "null" : "=" + Convert.ToInt32(pair[0]));
            }
        }

        private static void TestOverlapError()
        {
            Console.WriteLine("[同一时刻：伪双押 1° + 999，不拒绝编译]");
            var main = MainTrack(180, 180, 180);
            var sub = SubTrack(1, 1, 0, 0);
            PlayableChart pc = ChartCompiler.Compile(Chart(main, sub), Settings());

            int mid = 0;
            foreach (float a in pc.AngleData)
                if (a == 999f) mid++;
            Check(mid == 1, "播放谱里有 1 个 999 中旋", "count=" + mid);
            Check(pc.InsertedClicks.Count == 1, "记了 1 次伪双押点击", "n=" + pc.InsertedClicks.Count);
            Check(pc.InsertedClicks[0].SubSeq == 2, "伪双押来自副轨 2 号砖",
                "sub=" + pc.InsertedClicks[0].SubSeq);

            TrackTimeline played = ChartCompiler.RebuildTimeline(pc, 100);
            int host = pc.Map.ToPlayable(2);
            int next = pc.Map.ToPlayable(3);
            Near(played[host].EntryTime, 0.6, 1e-4, "主轨 2 号砖仍在 0.6s");
            Near(played[pc.InsertedClicks[0].PlayableSeq].EntryTime, 0.6 + 0.6 / 180.0, 0.002,
                "伪双押比主轨点击晚 1/180 拍");
            Near(played[next].EntryTime, 1.2, 0.002, "后面的主轨点击仍在 1.2s（这 1° 被扣回）");

            bool pseudoHidden = false;
            foreach (var ev in FindAll(pc.Actions, "PositionTrack", pc.InsertedClicks[0].PlayableSeq))
                if (ev.GetDouble("opacity", 100) == 0) pseudoHidden = true;
            Check(pseudoHidden, "伪双押的中旋是透明的");
        }

        private static void TestPseudoAndMid()
        {
            Console.WriteLine("[同一块砖：伪双押 + 中途插入可以同时存在]");
            var main = MainTrack(180, 180, 180);
            var sub = SubTrack(1, 1, 0, 0, 90);
            PlayableChart pc = null;
            CompileError err = null;
            try { pc = ChartCompiler.Compile(Chart(main, sub), Settings()); }
            catch (CompileError e) { err = e; }
            Check(err == null && pc != null, "不再拒绝同一块砖上的伪双押和中途插入",
                err == null ? "ok" : err.Code + " " + err.Message);
            if (pc == null) return;
            int mid = 0;
            foreach (float a in pc.AngleData)
                if (a == 999f) mid++;
            Check(mid >= 2, "伪双押和中途插入各有一个透明中旋", "count=" + mid);
            Check(pc.InsertedClicks.Count == 2, "记了 2 次插入点击", "n=" + pc.InsertedClicks.Count);
        }

        private static void TestUnsupportedSubEvent()
        {
            Console.WriteLine("[失败用例：副轨上的 FreeRoam / MultiPlanet]");
            var main = MainTrack(180, 180, 180, 180);

            var freeRoam = SubTrack(1, 1, 0, 90);
            freeRoam.Events.Add(new ChartEvent("FreeRoam", 1));
            CompileError e1 = null;
            try { ChartCompiler.Compile(Chart(main, freeRoam), Settings()); }
            catch (CompileError e) { e1 = e; }
            Check(e1 != null && e1.Code == "UnsupportedSubEvent", "FreeRoam → UnsupportedSubEvent",
                e1 == null ? "no error" : e1.Code);

            var multi = SubTrack(2, 1, 0, 90);
            var mp = new ChartEvent("MultiPlanet", 1);
            mp.Data["planets"] = 3;
            multi.Events.Add(mp);
            CompileError e2 = null;
            try { ChartCompiler.Compile(Chart(MainTrack(180, 180, 180, 180), multi), Settings()); }
            catch (CompileError e) { e2 = e; }
            Check(e2 != null && e2.Code == "UnsupportedSubEvent", "MultiPlanet(3) → UnsupportedSubEvent",
                e2 == null ? "no error" : e2.Code);

            var multi2 = SubTrack(3, 1, 0, 90);
            var mp2 = new ChartEvent("MultiPlanet", 1);
            mp2.Data["planets"] = 2;
            multi2.Events.Add(mp2);
            PlayableChart ok = null;
            try { ok = ChartCompiler.Compile(Chart(MainTrack(180, 180, 180, 180), multi2), Settings()); }
            catch (CompileError e) { Console.WriteLine("       [info] " + e.Message); }
            Check(ok != null, "MultiPlanet(2) 允许编译");
        }

        private static void TestMidSpinSubTrack()
        {
            Console.WriteLine("[副轨中旋砖：装饰 trackType=Midspin，幽灵 0 号砖不画]");
            var main = MainTrack(180, 180, 180, 180);
            var sub = SubTrack(1, 1, 0, 999, 90);
            PlayableChart pc = ChartCompiler.Compile(Chart(main, sub), Settings());

            Check(pc.InsertedClicks.Count == 1, "只插入 1 次点击（中旋不额外占拍）",
                "count=" + pc.InsertedClicks.Count);
            if (pc.InsertedClicks.Count == 1)
            {
                Check(pc.InsertedClicks[0].SubSeq == 3, "插入的是副轨 3 号砖", "=" + pc.InsertedClicks[0].SubSeq);
                Near(pc.InsertedClicks[0].SongTime, 0.9, Eps, "插入时间 = 0.9s（中旋那段不加拍）");
            }

            ChartEvent mid = null;
            int normal = 0;
            int subFloorDecos = 0;
            foreach (var d in pc.Decorations)
            {
                if (d.GetString("objectType") != "Floor") continue;
                if (d.GetString("tag", "") != "pta-mc pta-mc-t1") continue;
                subFloorDecos++;
                if (d.GetString("trackType") == "Midspin") mid = d;
                if (d.GetString("trackType") == "Normal") normal++;
            }
            Check(mid != null, "副轨 1 号砖（999）的 Floor 装饰 trackType = Midspin");
            Check(normal == 2, "2 号砖和终点砖是 Normal", "count=" + normal);
            Check(subFloorDecos == 3, "画 1、2 号砖和终点砖（幽灵 0 号不画）",
                "count=" + subFloorDecos);
        }

        private static void TestClickInPause()
        {
            Console.WriteLine("[失败用例：副轨点击落在主轨 Pause 尾巴里]");
            var main = MainTrack(180, 180);
            var pause = new ChartEvent("Pause", 1);
            pause.Data["duration"] = 1.0;
            main.Events.Add(pause);

            var sub = SubTrack(1, 1, 0, 90, 90);
            CompileError err = null;
            try { ChartCompiler.Compile(Chart(main, sub), Settings()); }
            catch (CompileError e) { err = e; }

            Check(err != null, "抛出了 CompileError");
            if (err != null)
            {
                Check(err.Code == "ClickInPause", "错误码 = ClickInPause", err.Code);
                Console.WriteLine("       [info] " + err.Message);
            }
        }
    }
}
