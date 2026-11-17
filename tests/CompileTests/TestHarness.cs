using System;
using System.Collections.Generic;
using System.Globalization;
using PtaMultichart.Core.Compile;
using PtaMultichart.Core.Model;
using PtaMultichart.Core.Timing;

namespace PtaMultichart.Tests
{
    internal static class TestHarness
    {
        public static int Failures { get; private set; }
        public static int Checks { get; private set; }
        public const double Eps = 1e-6;

        public static void Check(bool cond, string name, string detail = null)
        {
            Checks++;
            if (cond) Console.WriteLine("  ok   " + name);
            else
            {
                Failures++;
                Console.WriteLine("  FAIL " + name + (detail == null ? "" : "  [" + detail + "]"));
            }
        }

        public static void Near(double actual, double expected, double eps, string name)
        {
            Check(Math.Abs(actual - expected) <= eps, name,
                "actual=" + Fmt(actual) + " expected=" + Fmt(expected));
        }

        public static string Fmt(double d) => d.ToString("0.############", CultureInfo.InvariantCulture);

        public static Track MainTrack(params float[] angles)
        {
            var t = new Track { Id = 0, BaseBpm = 100 };
            t.AngleData.AddRange(angles);
            return t;
        }

        public static Track SubTrack(int id, int anchor, params float[] angles)
        {
            var t = new Track { Id = id, AnchorFloor = anchor, BaseBpm = 100 };
            t.AngleData.AddRange(angles);
            return t;
        }

        public static LogicalChart Chart(Track main, params Track[] subs)
        {
            var c = new LogicalChart();
            c.Tracks.Add(main);
            foreach (var s in subs) c.Tracks.Add(s);
            return c;
        }

        public static ChartEvent Find(List<ChartEvent> list, string type, int floor = -1)
        {
            foreach (var e in list)
            {
                if (e.Type != type) continue;
                if (floor >= 0 && e.Floor != floor) continue;
                return e;
            }
            return null;
        }

        public static List<ChartEvent> FindAll(List<ChartEvent> list, string type, int floor)
        {
            var r = new List<ChartEvent>();
            foreach (var e in list)
                if (e.Type == type && e.Floor == floor) r.Add(e);
            return r;
        }

        public static ChartEvent FindByTag(List<ChartEvent> list, string tag)
        {
            foreach (var e in list)
                if (e.GetString("tag", null) == tag) return e;
            return null;
        }

        public static List<ChartEvent> All(List<ChartEvent> list, string type)
        {
            var r = new List<ChartEvent>();
            foreach (var e in list) if (e.Type == type) r.Add(e);
            return r;
        }

        public static CompileSettings Settings() => new CompileSettings { Bpm = 100, MinClickGapSeconds = 0.05 };

        public static Vec2 ReadVec(ChartEvent ev, string key)
        {
            List<object> pair = ev.GetList(key);
            if (pair == null || pair.Count < 2) return new Vec2(double.NaN, double.NaN);
            return new Vec2(Convert.ToDouble(pair[0], CultureInfo.InvariantCulture),
                Convert.ToDouble(pair[1], CultureInfo.InvariantCulture));
        }

        public static List<Vec2> SimulatePositions(PlayableChart pc, double bpm)
        {
            List<Vec2> natural = ChartCompiler.NaturalPositions(pc.AngleData, bpm);
            var result = new List<Vec2>(pc.FloorCount);
            var carry = new Vec2(0, 0);

            for (int i = 0; i < pc.FloorCount; i++)
            {
                var own = new Vec2(0, 0);
                bool propagates = false;
                foreach (var ev in pc.Actions)
                {
                    if (ev.Type != "PositionTrack" || ev.Floor != i) continue;
                    Vec2 off = ReadVec(ev, "positionOffset");
                    own = own + off;
                    if (!ev.GetBool("justThisTile")) propagates = true;
                }
                result.Add(natural[i] + carry + own);
                if (propagates) carry = carry + own;
            }
            return result;
        }

        public static List<ChartEvent> SubFloors(PlayableChart pc)
        {
            var floors = new List<ChartEvent>();
            foreach (var d in pc.Decorations)
            {
                if (d.GetString("objectType") == "Floor" && d.GetString("tag", "") == "pta-mc pta-mc-t1")
                    floors.Add(d);
            }
            return floors;
        }

        public static List<ChartEvent> FloorDecos(PlayableChart pc)
        {
            var list = new List<ChartEvent>();
            foreach (ChartEvent d in pc.Decorations)
            {
                if (d.GetString("objectType") != "Floor") continue;
                if (d.GetString("tag", "").Contains("pta-mc-mask")) continue;
                list.Add(d);
            }
            return list;
        }
    }
}
