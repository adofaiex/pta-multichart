using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ADOFAI;
using PtaMultichart.Core.Compile;
using PtaMultichart.Core.Model;

namespace PtaMultichart.Game
{
    /// <summary>LevelData（Unity 侧）⇄ 逻辑谱 Track / 播放谱 的适配。</summary>
    public static class LevelAdapter
    {
        public static Track ReadMain(LevelData data)
        {
            var track = new Track
            {
                Id = 0,
                AnchorFloor = 0,
                ExitAngle = data.angleData.Count > 0 ? data.angleData[0] : 0,
                IsCCW = false,
                BaseBpm = data.bpm
            };
            track.AngleData.AddRange(data.angleData);
            foreach (var ev in data.levelEvents) track.Events.Add(EventFactory.ToModel(ev));
            foreach (var ev in data.decorations) track.Decorations.Add(EventFactory.ToModel(ev));
            return track;
        }

        /// <summary>把一条轨道装进 LevelData（不动全局 settings）。</summary>
        public static void WriteTrack(LevelData data, Track track)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));
            data.pathData = "";
            data.isOldLevel = false;
            data.angleData.Clear();
            data.angleData.AddRange(track.AngleData);

            data.levelEvents.Clear();
            foreach (var ev in track.Events)
            {
                var ge = EventFactory.ToGame(ev);
                if (ge != null) data.levelEvents.Add(ge);
            }

            data.decorations.Clear();
            foreach (var ev in track.Decorations)
            {
                var ge = EventFactory.ToGame(ev);
                if (ge != null) data.decorations.Add(ge);
            }
        }

        /// <summary>把编译好的播放谱装进 LevelData。</summary>
        public static void ApplyPlayable(LevelData data, PlayableChart chart)
        {
            data.pathData = "";
            data.isOldLevel = false;
            data.angleData.Clear();
            data.angleData.AddRange(chart.AngleData);

            data.levelEvents.Clear();
            foreach (var ev in chart.Actions)
            {
                var ge = EventFactory.ToGame(ev);
                if (ge != null) data.levelEvents.Add(ge);
            }

            data.decorations.Clear();
            foreach (var ev in chart.Decorations)
            {
                var ge = EventFactory.ToGame(ev);
                if (ge != null) data.decorations.Add(ge);
            }
        }

        /// <summary>在动作列表里找逻辑谱记号（EditorComment）。</summary>
        public static LevelEvent FindMarker(LevelData data)
        {
            foreach (var ev in data.levelEvents)
            {
                if (ev != null && ev.eventType == LevelEventType.EditorComment)
                {
                    string comment = ev.GetString("comment");
                    if (LogicalChart.IsMarkerComment(comment)) return ev;
                }
            }
            return null;
        }

        public static CompileSettings BuildSettings(LevelData data)
        {
            return new CompileSettings
            {
                Bpm = data.bpm,
                TrackStyle = Str(data.trackSettings, "trackStyle", "Standard"),
                TrackColor = Str(data.trackSettings, "trackColor", "debb7b"),
                SecondaryTrackColor = Str(data.trackSettings, "secondaryTrackColor", "ffffff"),
                TrackColorType = Str(data.trackSettings, "trackColorType", "Single"),
                TrackGlowIntensity = Num(data.trackSettings, "trackGlowIntensity", 100),
                TrackColorAnimDuration = Num(data.trackSettings, "trackColorAnimDuration", 2),
                TrackColorPulse = Str(data.trackSettings, "trackColorPulse", "None"),
                TrackPulseLength = (int)Num(data.trackSettings, "trackPulseLength", 10),
                TileShape = Str(data.trackSettings, "tileShape", "Long"),
                FloorIconOutlines = data.floorIconOutlines
            };
        }

        private static string Str(LevelEvent ev, string key, string fallback)
        {
            try
            {
                var v = ev != null ? ev[key] : null;
                if (v == null) return fallback;
                string s = Convert.ToString(v, CultureInfo.InvariantCulture);
                return string.IsNullOrEmpty(s) ? fallback : s;
            }
            catch
            {
                return fallback;
            }
        }

        private static double Num(LevelEvent ev, string key, double fallback)
        {
            try
            {
                var v = ev != null ? ev[key] : null;
                return v == null ? fallback : Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>把颜色转成游戏谱面里用的 hex 字符串。</summary>
        public static string ColorToHex(UnityEngine.Color c)
        {
            return UnityEngine.ColorUtility.ToHtmlStringRGBA(c);
        }
    }
}

