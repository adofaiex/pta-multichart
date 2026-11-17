using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PtaMultichart.Core.Compile;
using PtaMultichart.Core.Model;
using PtaMultichart.Core.Timing;

namespace PtaMultichart.Tests
{
    /// <summary>
    /// 用 ChartCompiler 生成测试谱面（写进 project/pta-multichart/testlevels/）。
    /// 这些文件是「播放谱 + 记号」，模组打开时会从记号还原成逻辑谱。
    /// </summary>
    internal static class Program
    {
        private static string _root;
        private static string _outDir;

        private static void Main()
        {
            _root = FindRoot();
            _outDir = Path.Combine(_root, "testlevels");
            Directory.CreateDirectory(_outDir);

            string templatePath = Path.Combine(_outDir, "template.adofai");
            if (!File.Exists(templatePath))
                throw new FileNotFoundException("缺少模板文件 testlevels/template.adofai", templatePath);
            string template = File.ReadAllText(templatePath);

            Generate(template, "t1_insert", BuildT1());
            Generate(template, "t2_repeat_const", BuildRepeatFixture(true));
            Generate(template, "t3_repeat_var", BuildRepeatFixture(false));
            Generate(template, "t4_midspin", BuildT4());
            Generate(template, "t6_two_subs", BuildT6());

            // 失败用例：副轨点击和主轨点击同一时刻
            try
            {
                var chart = BuildOverlap();
                ChartCompiler.Compile(chart, new CompileSettings { Bpm = 100 });
                Console.WriteLine("t5_overlap: UNEXPECTED SUCCESS (expected CompileError)");
            }
            catch (CompileError ex)
            {
                Console.WriteLine("t5_overlap: correctly rejected: " + ex.Code + " - " + ex.Message);
            }
        }

        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "testlevels")) &&
                    File.Exists(Path.Combine(dir.FullName, "README.md")))
                    return dir.FullName;
                string sub = Path.Combine(dir.FullName, "project", "pta-multichart");
                if (Directory.Exists(Path.Combine(sub, "testlevels")))
                    return sub;
                dir = dir.Parent;
            }
            throw new Exception("找不到工作区根目录（含 testlevels/）");
        }

        private static void Generate(string template, string name, LogicalChart chart)
        {
            var settings = new CompileSettings { Bpm = 100 };
            PlayableChart playable;
            try
            {
                playable = ChartCompiler.Compile(chart, settings);
            }
            catch (Exception ex)
            {
                Console.WriteLine(name + ": COMPILE FAILED: " + ex.GetType().Name + " " + ex.Message);
                return;
            }

            var dict = (Dictionary<string, object>)MiniJson.Deserialize(template);
            dict["angleData"] = playable.AngleData.Select(a => (object)(double)a).ToList();
            dict["actions"] = playable.Actions.Select(e => (object)Flatten(e)).ToList();
            dict["decorations"] = playable.Decorations.Select(e => (object)Flatten(e)).ToList();
            if (dict["settings"] is Dictionary<string, object> s)
            {
                s["bpm"] = 100.0;
                s["song"] = "";
                s["songFilename"] = "";
                s["artist"] = "";
                s["author"] = "pta-test";
                s["version"] = 19L;
            }

            string json = MiniJson.Serialize(dict);
            string path = Path.Combine(_outDir, name + ".adofai");
            File.WriteAllText(path, json);

            var tl = TimelineBuilder.Build(playable.AngleData, playable.Actions, 100);
            Console.WriteLine(name + ": tiles=" + playable.AngleData.Count +
                              " actions=" + playable.Actions.Count +
                              " decorations=" + playable.Decorations.Count +
                              " clicks=" + tl.ClickFloors.Count +
                              " inserted=" + playable.Map.InsertedCount);
            foreach (var c in playable.InsertedClicks)
                Console.WriteLine("    insert sub#" + c.SubTrackId + "/" + c.SubSeq +
                                  " -> playable " + c.PlayableSeq +
                                  " t=" + c.SongTime.ToString("0.####", CultureInfo.InvariantCulture));
            Console.WriteLine("    angles: " + string.Join(",", playable.AngleData.Select(a => a.ToString("0.###", CultureInfo.InvariantCulture))));
            Console.WriteLine("    written: " + path);
        }

        private static Dictionary<string, object> Flatten(ChartEvent ev)
        {
            var d = new Dictionary<string, object> { ["floor"] = ev.Floor, ["eventType"] = ev.Type };
            foreach (var kv in ev.Data) d[kv.Key] = kv.Value;
            if (!ev.Active) d["active"] = false;
            if (!ev.Visible) d["visible"] = false;
            if (ev.Locked) d["locked"] = true;
            return d;
        }

        // ---------------------------------------------------------------- fixtures

        private static LogicalChart BuildT1()
        {
            var chart = NewChart(180, 180);
            var sub = new Track { Id = 1, AnchorFloor = 1, ExitAngle = 180, IsCCW = false, BaseBpm = 100 };
            sub.AngleData.AddRange(new float[] { 180, 270 });
            chart.Tracks.Add(sub);
            return chart;
        }

        private static LogicalChart BuildT4()
        {
            var chart = NewChart(180, 999, 180, 180);
            var sub = new Track { Id = 1, AnchorFloor = 3, ExitAngle = 180, IsCCW = false, BaseBpm = 100 };
            sub.AngleData.AddRange(new float[] { 180, 270 });
            chart.Tracks.Add(sub);
            return chart;
        }

        private static LogicalChart BuildRepeatFixture(bool insertEveryGap)
        {
            var chart = NewChart(180, 180, 180, 180);

            var repeat = new ChartEvent("RepeatEvents", 1);
            repeat.Set("repeatType", "Floor");
            repeat.Set("floorCount", 3);
            repeat.Set("gapLength", 1);
            repeat.Set("tag", "rep");
            repeat.Set("executeOnCurrentFloor", false);
            chart.Main.Events.Add(repeat);

            var hitsound = new ChartEvent("SetHitsound", 1);
            hitsound.Set("hitsound", "Kick");
            hitsound.Set("hitsoundVolume", 100.0);
            hitsound.Set("eventTag", "rep");
            chart.Main.Events.Add(hitsound);

            var sub = new Track { Id = 1, AnchorFloor = 1, ExitAngle = 180, IsCCW = false, BaseBpm = 100 };
            if (insertEveryGap)
                sub.AngleData.AddRange(new float[] { 180, 270, 270, 270 }); // clicks at 0.5, 1.5, 2.5
            else
                sub.AngleData.AddRange(new float[] { 180, 270 });           // click at 0.5 only
            chart.Tracks.Add(sub);
            return chart;
        }

        private static LogicalChart BuildT6()
        {
            var chart = NewChart(180, 180, 180);
            var sub1 = new Track { Id = 1, AnchorFloor = 1, ExitAngle = 180, IsCCW = false, BaseBpm = 100 };
            sub1.AngleData.AddRange(new float[] { 180, 270 });
            chart.Tracks.Add(sub1);
            var sub2 = new Track { Id = 2, AnchorFloor = 2, ExitAngle = 180, IsCCW = false, BaseBpm = 100 };
            sub2.AngleData.AddRange(new float[] { 180, 270 });
            chart.Tracks.Add(sub2);
            return chart;
        }

        private static LogicalChart BuildOverlap()
        {
            var chart = NewChart(180, 180);
            var sub = new Track { Id = 1, AnchorFloor = 1, ExitAngle = 180, IsCCW = false, BaseBpm = 100 };
            sub.AngleData.AddRange(new float[] { 180, 180 }); // click at 1.0 beat == main floor2
            chart.Tracks.Add(sub);
            return chart;
        }

        private static LogicalChart NewChart(params float[] angles)
        {
            var chart = new LogicalChart();
            var main = new Track { Id = 0, AnchorFloor = 0, ExitAngle = angles.Length > 0 ? angles[0] : 0, BaseBpm = 100 };
            main.AngleData.AddRange(angles);
            chart.Tracks.Add(main);
            return chart;
        }
    }
}



