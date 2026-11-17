using System;
using System.Collections.Generic;
using PtaMultichart.Core.Model;

namespace PtaMultichart.Core.Compile
{
    internal static class RepeatEventProcessor
    {
        private static readonly string[] ConditionalTagKeys =
        {
            "perfectTag", "earlyPerfectTag", "latePerfectTag", "veryEarlyTag", "veryLateTag",
            "tooEarlyTag", "tooLateTag", "lossTag", "onCheckpointTag"
        };

        /// <summary>收集 SetConditionalEvents 9 个桶 tag（scnGame 整串匹配用），仅用于冲突退让检测。</summary>
        public static HashSet<string> CollectConditionalTags(IEnumerable<ChartEvent> events)
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            if (events == null) return tags;
            foreach (ChartEvent ev in events)
            {
                if (ev == null || ev.Type != "SetConditionalEvents") continue;
                foreach (string key in ConditionalTagKeys)
                {
                    string value = ev.GetString(key, null);
                    if (!string.IsNullOrEmpty(value)) tags.Add(value);
                }
            }
            return tags;
        }

        /// <summary>
        /// RepeatEvents(Floor) 处理：步长在播放谱里仍是常数时只改 floor 与 gapLength；
        /// 不恒定时把带匹配 tag 的事件复制到各目标砖并删掉 RepeatEvents。
        /// </summary>
        public static void Process(List<ChartEvent> actions, FloorMap map, int logicalFloors,
            HashSet<string> conditionalTags, List<string> tagConflicts)
        {
            var repeats = new List<ChartEvent>();
            foreach (ChartEvent ev in actions)
                if (ev.Type == "RepeatEvents") repeats.Add(ev);
            if (repeats.Count == 0) return;

            foreach (ChartEvent rep in repeats)
            {
                string repeatType = rep.GetString("repeatType", "Beat");
                if (!string.Equals(repeatType, "Floor", StringComparison.OrdinalIgnoreCase))
                    continue;

                int hostLogical = map.ToLogical(rep.Floor);
                int gap = rep.GetInt("gapLength", 1);
                int count = rep.GetInt("floorCount", 0);
                if (count < 0) count = 0;
                if (count > 100000)
                    throw new CompileError("CannotSplit",
                        "RepeatEvents 的 floorCount 太大（" + count + "），拒绝展开", rep.Floor);

                var targets = new List<int>();
                for (int k = 0; k <= count; k++)
                {
                    long target = (long)hostLogical + (long)k * gap;
                    if (target < 0 || target > logicalFloors - 1) break;
                    targets.Add((int)target);
                }
                if (targets.Count <= 1) continue;

                bool constant = true;
                int delta = 0;
                for (int k = 0; k + 1 < targets.Count; k++)
                {
                    int step = map.ToPlayable(targets[k + 1]) - map.ToPlayable(targets[k]);
                    if (k == 0) delta = step;
                    else if (step != delta) { constant = false; break; }
                }

                if (constant)
                {
                    rep.Data["gapLength"] = delta;
                    continue;
                }

                string[] ruleTags = SplitTags(rep.GetString("tag", null));
                int hostPlayable = rep.Floor;
                var snapshot = new List<ChartEvent>(actions);
                for (int k = 1; k < targets.Count; k++)
                {
                    int dst = map.ToPlayable(targets[k]);
                    foreach (ChartEvent src in snapshot)
                    {
                        if (src == rep || src.Type == "RepeatEvents" || src.Floor != hostPlayable) continue;
                        string[] own = SplitTags(src.GetString("eventTag", null));
                        if (own.Length == 0) continue;
                        if (ruleTags.Length > 0 && !SharesTag(own, ruleTags)) continue;
                        ChartEvent copy = src.Clone();
                        copy.Floor = dst;
                        copy.Data["eventTag"] = StripTags(own, ruleTags, conditionalTags, tagConflicts);
                        actions.Add(copy);
                    }
                }
                actions.Remove(rep);
            }
        }

        private static string[] SplitTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            return s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool SharesTag(string[] own, string[] rule)
        {
            foreach (string a in own)
                foreach (string b in rule)
                    if (a == b) return true;
            return false;
        }

        private static string StripTags(string[] own, string[] remove, HashSet<string> conditionalTags,
            List<string> tagConflicts)
        {
            var kept = new List<string>(own.Length + 1);
            bool hasUnrolled = false;
            foreach (string tag in own)
            {
                bool drop = false;
                foreach (string r in remove)
                    if (tag == r) { drop = true; break; }
                if (drop) continue;
                if (tag == ChartEvent.UnrolledTag) hasUnrolled = true;
                kept.Add(tag);
            }

            if (!hasUnrolled)
            {
                if (conditionalTags != null && conditionalTags.Contains(ChartEvent.UnrolledTag))
                {
                    if (tagConflicts != null && !tagConflicts.Contains(ChartEvent.UnrolledTag))
                        tagConflicts.Add(ChartEvent.UnrolledTag);
                }
                else
                {
                    kept.Add(ChartEvent.UnrolledTag);
                }
            }
            return string.Join(" ", kept.ToArray());
        }
    }
}
