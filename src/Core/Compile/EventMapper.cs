using System;
using System.Collections.Generic;
using PtaMultichart.Core.Model;

namespace PtaMultichart.Core.Compile
{
    internal static class EventMapper
    {
        public static bool IsSelfMarker(ChartEvent ev)
        {
            if (ev == null) return false;
            if (ev.Type != "EditorComment") return false;
            return LogicalChart.IsMarkerComment(ev.GetString("comment", null));
        }

        public static ChartEvent MapEvent(ChartEvent src, FloorMap map, int logicalFloors)
        {
            ChartEvent copy = src.Clone();
            int hostLogical = src.Floor;
            copy.Floor = map.ToPlayable(hostLogical);
            RemapTilePairs(copy, map, logicalFloors, hostLogical);
            return copy;
        }

        /// <summary>
        /// Tile 型字段（JSON 里是 [int, "ThisTile"|"Start"|"End"]）按 scnGame.IDFromTile 的语义改。
        /// 用结构识别而不是按 key 名，因为不同事件的同名 key 类型不同
        /// （MoveDecorations 的 relativeTo 是 DecPlacementType 字符串，PositionTrack 的才是 Tile）。
        /// </summary>
        private static void RemapTilePairs(ChartEvent ev, FloorMap map, int logicalFloors, int hostLogical)
        {
            List<string> keys = null;
            foreach (KeyValuePair<string, object> kv in ev.Data)
            {
                if (!IsTileValue(kv.Value)) continue;
                if (keys == null) keys = new List<string>();
                keys.Add(kv.Key);
            }
            if (keys == null) return;

            int hostMapped = map.ToPlayable(hostLogical);
            foreach (string key in keys)
            {
                var pair = (List<object>)ev.Data[key];
                string kind = (string)pair[1];
                int off = Convert.ToInt32(pair[0], System.Globalization.CultureInfo.InvariantCulture);

                if (kind == "ThisTile")
                {
                    int logicalTarget = Clamp(hostLogical + off, 0, logicalFloors - 1);
                    ev.Data[key] = ChartEvent.Pair(map.ToPlayable(logicalTarget) - hostMapped, kind);
                }
                else if (kind == "Start")
                {
                    int logicalTarget = Clamp(off, 0, logicalFloors - 1);
                    ev.Data[key] = ChartEvent.Pair(map.ToPlayable(logicalTarget), "Start");
                }
                else // End：先换成逻辑谱上的绝对序号，再映射，最后写成 Start，避免末尾因为插入而错位。
                {
                    int logicalTarget = Clamp(logicalFloors - 1 + off, 0, logicalFloors - 1);
                    ev.Data[key] = ChartEvent.Pair(map.ToPlayable(logicalTarget), "Start");
                }
            }
        }

        private static bool IsTileValue(object v)
        {
            var list = v as List<object>;
            if (list == null || list.Count != 2) return false;
            var kind = list[1] as string;
            if (kind == null) return false;
            if (kind != "ThisTile" && kind != "Start" && kind != "End") return false;
            return list[0] is int || list[0] is long || list[0] is double || list[0] is float;
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
