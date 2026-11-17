using System;
using System.Collections.Generic;
using System.Globalization;
using PtaMultichart.Core.Model;
using PtaMultichart.Core.Timing;

namespace PtaMultichart.Tests
{
    internal static class Program
    {
        private static int _failures;
        private static int _checks;
        private const double Eps = 1e-6;

        private static void Check(bool cond, string name, string detail = null)
        {
            _checks++;
            if (cond) Console.WriteLine("  ok   " + name);
            else { _failures++; Console.WriteLine("  FAIL " + name + (detail == null ? "" : "  [" + detail + "]")); }
        }

        private static void Near(double actual, double expected, double eps, string name)
        {
            Check(Math.Abs(actual - expected) <= eps, name,
                "actual=" + actual.ToString("0.######", CultureInfo.InvariantCulture) +
                " expected=" + expected.ToString("0.######", CultureInfo.InvariantCulture));
        }

        private static void Main()
        {
            TestStraight180();
            TestTurns();
            TestTurnaround();
            TestMidSpin();
            TestTwirl();
            TestSetSpeed();
            TestHoldPause();
            TestClicks();
            TestSplit();
            TestMiniJson();
            TestChartRoundTrip();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "ALL TESTS PASSED (" + _checks + " checks)"
                : _failures + " FAILURES out of " + _checks + " checks");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }

        private static TrackTimeline Build(params float[] angles) =>
            TimelineBuilder.Build(angles, Array.Empty<ChartEvent>(), 100);

        private static void TestStraight180()
        {
            Console.WriteLine("[straight 180 = left]");
            var t = Build(180, 180, 180);
            Check(t.Floors.Count == 4, "floor count = angleData+1");
            Near(t[1].EntryBeat, 0, Eps, "floor1 entryBeat = 0");
            Near(t[2].EntryBeat, 1, Eps, "floor2 entryBeat = 1");
            Near(t[3].EntryBeat, 2, Eps, "floor3 entryBeat = 2");
            Near(t[1].TravelTime, 0.6, Eps, "180 deg at bpm100 = 1 beat = 0.6s");
            Near(t[3].EntryTime, 1.2, Eps, "floor3 entryTime = 1.2s");
            Near(t[1].AngleLength, Math.PI, Eps, "angleLength = pi");
            Near(t.Positions[1].X, -1.0, Eps, "180 deg is -x");
            Near(t.Positions[1].Y, 0.0, Eps, "pos1 y = 0");
            Near(t.Positions[2].X, -2.0, Eps, "pos2 x = -2");
        }

        private static void TestTurns()
        {
            Console.WriteLine("[turns]");
            var t1 = Build(0, 90);   // right -> up : 0.5 beat
            Near(t1[2].EntryBeat, 0.5, Eps, "0 -> 90 = 0.5 beat");

            var t2 = Build(180, 90); // left -> up : 1.5 beat
            Near(t2[2].EntryBeat, 1.5, Eps, "180 -> 90 = 1.5 beat");

            var t3 = Build(0, 270);  // right -> down : 1.5 beat
            Near(t3[2].EntryBeat, 1.5, Eps, "0 -> 270 = 1.5 beat");
        }

        private static void TestTurnaround()
        {
            Console.WriteLine("[180 deg reversal]");
            var t = Build(180, 0, 180);
            Check(t[1].Turnaround, "floor1 reversed = turnaround");
            Near(t[2].EntryBeat, 2.0, Eps, "turnaround costs 2 beats");
        }

        private static void TestMidSpin()
        {
            Console.WriteLine("[midspin 999]");
            var t = Build(180, 999, 180);
            Check(t[1].MidSpin, "floor1 is midspin");
            Near(t[1].EntryBeat, 0, Eps, "floor1 entryBeat 0");
            Near(t[1].TravelTime, 0.0, Eps, "midspin travel time = 0");
            Near(t[2].EntryBeat, 0.0, Eps, "midspin adds no beats to the next floor");
            Check(!t[2].IsClick, "floor after midspin is not a click");
            Check(t[1].IsClick, "midspin floor itself is a click");
            Check(t[3].IsClick, "floor3 is a click");
        }

        private static void TestTwirl()
        {
            Console.WriteLine("[twirl]");
            var events = new List<ChartEvent> { new ChartEvent("Twirl", 1) };
            var t = TimelineBuilder.Build(new float[] { 180, 90 }, events, 100);
            Check(t[1].IsCCW, "twirl flips isCCW on floor1");
            Near(t[2].EntryBeat, 0.5, Eps, "CCW: 180 -> 90 = 0.5 beat");
        }

        private static void TestSetSpeed()
        {
            Console.WriteLine("[set speed]");
            var events = new List<ChartEvent>
            {
                new ChartEvent("SetSpeed", 1) { Data = { ["speedType"] = "Bpm", ["beatsPerMinute"] = 200.0 } }
            };
            var t = TimelineBuilder.Build(new float[] { 180, 180 }, events, 100);
            Near(t[1].Speed, 2.0, Eps, "speed = 200/100");
            Near(t[1].TravelTime, 0.3, Eps, "1 beat at speed 2 = 0.3s");
        }

        private static void TestHoldPause()
        {
            Console.WriteLine("[hold + pause]");
            var events = new List<ChartEvent>
            {
                new ChartEvent("Hold", 1) { Data = { ["duration"] = 1 } },
                new ChartEvent("Pause", 2) { Data = { ["duration"] = 0.5 } }
            };
            var t = TimelineBuilder.Build(new float[] { 180, 180, 180 }, events, 100);
            Check(t[1].HoldBeats == 1, "hold beats stored");
            Near(t[1].TravelTime, 0.6 + 1.2, Eps, "hold 1 adds 2 beats of time");
            Near(t[2].TravelTime, 0.6 + 0.3, Eps, "pause 0.5 adds 0.5 beat of time");
            Near(t[3].EntryTime, 1.8 + 0.9, Eps, "entryTime accumulates hold+pause");
        }

        private static void TestClicks()
        {
            Console.WriteLine("[clicks]");
            var t = Build(180, 180, 999, 180, 180);
            // angles: floor0 exit 180, floor1 exit 180, floor2 exit 999(midspin), floor3 exit 180, floor4 = end
            var expected = new List<int> { 1, 2, 4, 5 };
            Check(t.ClickFloors.Count == expected.Count, "click count = " + expected.Count,
                string.Join(",", t.ClickFloors));
            for (int i = 0; i < Math.Min(expected.Count, t.ClickFloors.Count); i++)
                Check(t.ClickFloors[i] == expected[i], "click #" + i + " = " + expected[i],
                    string.Join(",", t.ClickFloors));
        }

        private static void TestSplit()
        {
            Console.WriteLine("[arc split]");
            var t = Build(180, 180);
            var f = t[1];
            var exits = TimelineBuilder.SplitExitAnglesDegrees(f, new List<double> { 0.25, 0.25, 0.5 });
            Check(exits.Count == 3, "three pieces");
            double total = 0;
            double prev = f.EntryAngle;
            foreach (var deg in exits)
            {
                double exit = TimelineBuilder.ExitRadFromAngleDeg(deg);
                total += TimelineBuilder.AngleMoved(prev, exit, !f.IsCCW);
                prev = exit;
            }
            Near(total, f.AngleLength, Eps, "sweep is conserved");
            Near(exits[2], 180, Eps, "last piece keeps the original angle");
        }

        private static void TestMiniJson()
        {
            Console.WriteLine("[mini json]");
            var obj = new Dictionary<string, object>
            {
                ["a"] = 1,
                ["b"] = 1.5,
                ["c"] = "x\"y\nz",
                ["d"] = new List<object> { 1, true, null, new Dictionary<string, object> { ["k"] = 2.25 } }
            };
            string json = MiniJson.Serialize(obj);
            var back = MiniJson.Deserialize(json) as Dictionary<string, object>;
            Check(back != null, "round trip object");
            Check(Convert.ToInt64(back["a"]) == 1, "int round trip");
            Near(Convert.ToDouble(back["b"]), 1.5, 1e-12, "double round trip");
            Check((string)back["c"] == "x\"y\nz", "string escaping");
            var list = back["d"] as List<object>;
            Check(list != null && list.Count == 4 && (bool)list[1], "nested list");
        }

        private static void TestChartRoundTrip()
        {
            Console.WriteLine("[logical chart round trip]");
            var main = new Track { Id = 0, BaseBpm = 100 };
            main.AngleData.AddRange(new float[] { 180, 180 });
            main.Events.Add(new ChartEvent("SetSpeed", 1) { Data = { ["speedType"] = "Bpm", ["beatsPerMinute"] = 120.0 } });
            var sub = new Track { Id = 1, AnchorFloor = 1, ExitAngle = 180, IsCCW = false, BaseBpm = 100 };
            sub.AngleData.AddRange(new float[] { 180 });
            var chart = new LogicalChart();
            chart.Tracks.Add(main);
            chart.Tracks.Add(sub);

            string comment = chart.ToComment();
            Check(LogicalChart.IsMarkerComment(comment), "marker prefix detected");
            var parsed = LogicalChart.FromJson(LogicalChart.StripMarker(comment));
            Check(parsed.Tracks.Count == 2, "two tracks restored");
            Check(parsed.Tracks[1].AnchorFloor == 1, "anchor restored");
            Check(parsed.Tracks[0].Events.Count == 1 && parsed.Tracks[0].Events[0].GetString("speedType") == "Bpm",
                "event data restored");
        }
    }
}

