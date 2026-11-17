using System;
using System.Collections.Generic;
using PtaMultichart.Core.Model;

namespace PtaMultichart.Core.Compile
{
    internal static class PreflightBuilder
    {
        /// <summary>生成 6 条只读编译预检项（tag 冲突为警告，其余为信息）。</summary>
        public static PreflightReport Build(PlayableChart playable, int subTrackCount, int pseudoCount,
            int positionTrackInserted, int positionTrackCorrections, List<string> tagConflicts)
        {
            var report = new PreflightReport();
            int inserted = playable.InsertedClicks.Count;
            int ptEvents = positionTrackInserted + positionTrackCorrections;

            int polylineEvents = 0;
            foreach (ChartEvent ev in playable.Actions)
            {
                if (ev == null || ev.Type != "MoveDecorations") continue;
                string tag = ev.GetString("tag", null);
                if (tag != null && tag.StartsWith(DecorationBuilder.TrackTagPrefix, StringComparison.Ordinal))
                    polylineEvents++;
            }

            report.InsertedClicks = inserted;
            report.PositionTrackEvents = ptEvents;
            report.PolylineSegments = polylineEvents / 2;
            report.SubTrackCount = subTrackCount;
            if (tagConflicts != null) report.TagConflicts.AddRange(tagConflicts);

            report.Items.Add(new PreflightItem
            {
                Code = PreflightCode.InsertedClicks,
                Kind = PreflightKind.Info,
                Message = "插入点击砖 " + inserted + " 块，以透明中旋写入主轨，可见砖的角度与编辑器相同" +
                          (pseudoCount > 0 ? "，另有伪双押 " + pseudoCount + " 处（扫 1° 后接中旋）" : ""),
                Detail = "播放谱砖数 " + playable.FloorCount + "，其中插入砖 " + playable.Map.InsertedCount + " 块"
            });

            report.Items.Add(new PreflightItem
            {
                Code = PreflightCode.PositionCompensation,
                Kind = PreflightKind.Info,
                Message = "插入的中旋是透明的，砖的位置不挪，轨道形状不变",
                Detail = "本次 " + ptEvents + " 条事件：插入砖位置 " + positionTrackInserted + " 条 + 后继砖拉回 " +
                         positionTrackCorrections + " 条"
            });

            report.Items.Add(new PreflightItem
            {
                Code = PreflightCode.DecorationPolyline,
                Kind = PreflightKind.Info,
                Message = "装饰为副轨点击区间上的线性折线",
                Detail = "本次 " + report.PolylineSegments + " 段，每段红蓝各 1 条 MoveDecorations（ease=Linear）；" +
                         "不按每 1/4 拍取样"
            });

            report.Items.Add(new PreflightItem
            {
                Code = PreflightCode.SubTrackOrigin,
                Kind = PreflightKind.Info,
                Message = "副轨 seq1 不插入；1 号砖对齐锚点；0 号砖为幽灵起点，不占拍，不参与判定",
                Detail = "本次副轨 " + report.SubTrackCount + " 条"
            });

            bool conflicted = report.TagConflicts.Count > 0;
            report.Items.Add(new PreflightItem
            {
                Code = PreflightCode.ConditionalTagConflict,
                Kind = conflicted ? PreflightKind.Warning : PreflightKind.Info,
                Message = conflicted
                    ? "条件事件 tag 与模组标记有交集（" + string.Join(",", report.TagConflicts.ToArray()) +
                      "）；已保留作者 tag，这些字符串未写入"
                    : "条件事件 tag 与模组标记无交集，本轮不做 tag 退让",
                Detail = conflicted
                    ? "交集 " + report.TagConflicts.Count + " 个字符串；编译继续，砖数与拍数不受影响"
                    : "模组标记（pta-mc*）照常写入"
            });

            report.Items.Add(new PreflightItem
            {
                Code = PreflightCode.LastEntryBeat,
                Kind = PreflightKind.Info,
                Message = "最后一块 entryBeat 为 0。这是当前口径",
                Detail = "引擎侧默认值（entryBeat 的赋值循环不写最后一块，保持 scrFloor 初始 0）；本项不改任何计算"
            });

            return report;
        }
    }
}
