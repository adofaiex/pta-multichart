using System;
using System.Collections.Generic;
using System.Text;

namespace PtaMultichart.Core.Compile
{
    /// <summary>预检条目的稳定 id（与方案 v2 §8 的 6 条一一对应，顺序即输出顺序）。</summary>
    public static class PreflightCode
    {
        /// <summary>1. 插入点击砖 N 块，主轨增加 N 拍。</summary>
        public const string InsertedClicks = "InsertedClicks";
        /// <summary>2. 位置由 2 条 PositionTrack 补偿，拍数不变。</summary>
        public const string PositionCompensation = "PositionCompensation";
        /// <summary>3. 装饰为副轨点击区间上的线性折线。</summary>
        public const string DecorationPolyline = "DecorationPolyline";
        /// <summary>4. 副轨 seq1 不插入；1 号砖对齐锚点；0 号砖为幽灵起点。</summary>
        public const string SubTrackOrigin = "SubTrackOrigin";
        /// <summary>5. 条件事件 tag 与模组标记的交集（警告）。</summary>
        public const string ConditionalTagConflict = "ConditionalTagConflict";
        /// <summary>6. 最后一块 entryBeat 为 0。</summary>
        public const string LastEntryBeat = "LastEntryBeat";
    }

    /// <summary>一条预检输出。Kind 只有 "Info" / "Warning"，两者都不是编译失败。</summary>
    public sealed class PreflightItem
    {
        public string Code;
        public string Kind = PreflightKind.Info;
        /// <summary>方案 v2 §8 的口径原话（可带本次的 N 等具体数字）。</summary>
        public string Message;
        /// <summary>本次编译的附注（条数、砖号等），可为空。</summary>
        public string Detail;

        public bool IsWarning
        {
            get { return Kind == PreflightKind.Warning; }
        }

        public override string ToString()
        {
            return (IsWarning ? "[WARN] " : "[INFO] ") + Message +
                   (string.IsNullOrEmpty(Detail) ? "" : "  — " + Detail);
        }
    }

    /// <summary>预检的 Kind 取值。</summary>
    public static class PreflightKind
    {
        public const string Info = "Info";
        public const string Warning = "Warning";
    }

    /// <summary>一次编译的预检结果。挂在 <see cref="PlayableChart.Preflight"/> 上，不影响任何计算。</summary>
    public sealed class PreflightReport
    {
        public readonly List<PreflightItem> Items = new List<PreflightItem>();
        /// <summary>本次被退让掉的模组标记字符串（条件事件 tag 交集）。</summary>
        public readonly List<string> TagConflicts = new List<string>();

        /// <summary>插入的点击砖块数 = 主轨增加的拍数（方案 v2 §1）。</summary>
        public int InsertedClicks;
        /// <summary>发出的位置补偿事件数（插入砖 + 后继砖）。</summary>
        public int PositionTrackEvents;
        /// <summary>装饰折线段数（每段红蓝各一条 MoveDecorations）。</summary>
        public int PolylineSegments;
        /// <summary>参与的副轨条数。</summary>
        public int SubTrackCount;

        public int Count
        {
            get { return Items.Count; }
        }

        public int WarningCount
        {
            get
            {
                int n = 0;
                foreach (var item in Items) if (item.IsWarning) n++;
                return n;
            }
        }

        public bool HasWarnings
        {
            get { return WarningCount > 0; }
        }

        public PreflightItem Find(string code)
        {
            foreach (var item in Items)
                if (item != null && item.Code == code) return item;
            return null;
        }

        /// <summary>一条一条贴出来（面板/日志用）。</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            foreach (var item in Items)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(item.ToString());
            }
            return sb.ToString();
        }
    }
}
