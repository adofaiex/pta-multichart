using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using PtaMultichart.Core.Compile;
using PtaMultichart.Core.Model;
using PtaMultichart.Core.Timing;
using PtaMultichart.Game;
using UnityEngine;

namespace PtaMultichart.Session
{
    /// <summary>
    /// 编辑器会话：内存里长期放逻辑谱，只在打开/保存/试玩三个时刻换成播放谱。
    /// </summary>
    public static class MultitrackSession
    {
        public static LogicalChart Chart { get; private set; }
        public static int CurrentTrackId { get; private set; }
        public static bool PlayableActive { get; private set; }
        public static PlayableChart ActivePlayable { get; private set; }
        public static CompileSettings Settings { get; private set; } = new CompileSettings();
        public static string LastError { get; private set; }
        public static string LastInfo { get; private set; }
        public static event Action Changed;

        public static bool HasSession => Chart != null && Chart.Tracks.Count > 0;
        public static Track Current => HasSession ? Chart.Find(CurrentTrackId) : null;
        public static Track MainTrack => HasSession ? Chart.Main : null;

        public static FloorMap ActiveMap => ActivePlayable?.Map;

        private static void Notify()
        {
            try { Changed?.Invoke(); } catch (Exception ex) { Main.Log("Changed handler failed: " + ex.Message); }
        }

        public static void SetInfo(string msg)
        {
            LastInfo = msg;
            Main.Log(msg);
        }

        public static void SetError(string msg)
        {
            LastError = msg;
            Main.Log("ERROR: " + msg);
            try { scnEditor.instance?.ShowNotification(msg); } catch { }
            Notify();
        }

        // ---------------------------------------------------------------- 打开

        public static void OnLevelLoaded(LevelData data)
        {
            Chart = null;
            CurrentTrackId = 0;
            PlayableActive = false;
            ActivePlayable = null;
            LastError = null;

            try
            {
                var marker = LevelAdapter.FindMarker(data);
                if (marker != null)
                {
                    string json = LogicalChart.StripMarker(marker.GetString("comment"));
                    var parsed = LogicalChart.FromJson(json);
                    data.levelEvents.Remove(marker);
                    Chart = parsed;
                    CurrentTrackId = 0;
                    LevelAdapter.WriteTrack(data, Chart.Main);
                    SetInfo("已从 EditorComment 还原逻辑谱： " + parsed.Tracks.Count + " 条轨道");
                }
            }
            catch (Exception ex)
            {
                SetError("逻辑谱还原失败，按普通关卡处理：" + ex.Message);
                Main.Log(ex.ToString());
                Chart = null;
            }

            if (Chart == null)
            {
                Chart = LogicalChart.CreateFromMain(LevelAdapter.ReadMain(data));
                CurrentTrackId = 0;
                SetInfo("新建多轨会话（仅主轨）");
            }

            Settings = LevelAdapter.BuildSettings(data);
            Notify();
        }

        public static void OnNewLevel(LevelData data)
        {
            Chart = null;
            CurrentTrackId = 0;
            PlayableActive = false;
            ActivePlayable = null;
            Chart = LogicalChart.CreateFromMain(LevelAdapter.ReadMain(data));
            Settings = LevelAdapter.BuildSettings(data);
            SetInfo("新关卡：会话已清空");
            Notify();
        }

        // ---------------------------------------------------------------- 轨道

        /// <summary>把编辑器里当前轨道的数据写回逻辑谱。</summary>
        public static void SaveCurrentFromEditor(scnEditor editor)
        {
            if (editor == null || !HasSession || PlayableActive) return;
            var track = Chart.Find(CurrentTrackId);
            if (track == null) return;
            var data = editor.levelData;
            track.AngleData = new List<float>(data.angleData);
            track.Events = data.levelEvents.Select(EventFactory.ToModel).Where(x => x != null).ToList();
            track.Decorations = data.decorations.Select(EventFactory.ToModel).Where(x => x != null).ToList();
        }

        public static bool SwitchTo(int trackId, scnEditor editor)
        {
            if (!HasSession || editor == null || PlayableActive) return false;
            var target = Chart.Find(trackId);
            if (target == null) return false;
            if (trackId == CurrentTrackId) return true;

            SaveCurrentFromEditor(editor);
            LevelAdapter.WriteTrack(editor.levelData, target);
            CurrentTrackId = trackId;
            editor.RemakePath();
            editor.undoStates.Clear();
            editor.redoStates.Clear();
            if (editor.floors.Count > 0) editor.SelectFloor(editor.floors[0], false);
            RefreshFloorNumbers(editor);
            SetInfo("切换到 " + TrackName(trackId));
            Notify();
            return true;
        }

        public static string TrackName(int id)
        {
            if (id == 0) return "主轨";
            var t = Chart?.Find(id);
            return t == null ? ("副轨 " + id) : ("副轨 " + id + "（锚点 " + t.AnchorFloor + " 号砖）");
        }

        /// <summary>在指定的主轨砖上创建副轨（方案 §4）。</summary>
        public static Track CreateSubTrack(int anchorSeq, scnEditor editor)
        {
            if (!HasSession || editor == null || PlayableActive) return null;
            if (CurrentTrackId != 0)
            {
                SetError("只能在主轨上创建副轨");
                return null;
            }

            SaveCurrentFromEditor(editor);
            var main = Chart.Main;
            TrackTimeline timeline;
            try
            {
                timeline = TimelineBuilder.Build(main.AngleData, main.Events, Settings.Bpm);
            }
            catch (Exception ex)
            {
                SetError("节拍计算失败：" + ex.Message);
                return null;
            }

            if (anchorSeq < 0 || anchorSeq >= timeline.Count)
            {
                SetError("锚点砖号越界：" + anchorSeq);
                return null;
            }

            var floor = timeline[anchorSeq];
            double exitDeg = ResolveExitAngleDeg(main.AngleData, anchorSeq);
            if (double.IsNaN(exitDeg))
            {
                SetError("锚点砖没有可抄的出射角：" + anchorSeq);
                return null;
            }

            double bpm = floor.Speed * Settings.Bpm;
            var track = new Track
            {
                Id = Chart.NextTrackId(),
                AnchorFloor = anchorSeq,
                ExitAngle = exitDeg,
                IsCCW = floor.IsCCW,
                BaseBpm = bpm
            };
            track.AngleData.Add((float)exitDeg);

            if (floor.IsCCW)
            {
                track.Events.Add(new ChartEvent("Twirl", 0));
            }

            if (Math.Abs(bpm - Settings.Bpm) > 1e-6)
            {
                var setSpeed = new ChartEvent("SetSpeed", 0);
                setSpeed.Set("speedType", "Bpm");
                setSpeed.Set("beatsPerMinute", bpm);
                track.Events.Add(setSpeed);
            }

            Chart.Tracks.Add(track);
            SetInfo("在 " + anchorSeq + " 号砖创建 " + TrackName(track.Id) +
                    "（出射角 " + exitDeg.ToString("0.###") + "，BPM " + bpm.ToString("0.###") + "）。它已经出现在锚点旁边，点它的砖即可编辑。");
            try { PtaMultichart.Editor.TrackOverlay.Refresh(editor, false); }
            catch (Exception ex) { Main.Log("overlay after create: " + ex.Message); }
            Notify();
            return track;
        }

        /// <summary>
        /// 把这条轨的本地坐标平移到世界上的量（单位：砖长）。
        /// 主轨是 0。副轨让 1 号砖落在锚点上，0 号砖留在锚点前一块（和播放谱同一套）。
        /// </summary>
        public static Core.Timing.Vec2 OriginOf(Track track)
        {
            if (track == null || track.Id == 0 || Chart == null || Chart.Main == null)
                return new Core.Timing.Vec2(0, 0);
            try
            {
                var main = TimelineBuilder.Build(Chart.Main.AngleData, Chart.Main.Events, Settings.Bpm);
                int anchor = track.AnchorFloor;
                if (anchor < 0 || anchor >= main.Count || anchor >= main.Positions.Count)
                    return new Core.Timing.Vec2(0, 0);
                double bpm = track.BaseBpm > 0 ? track.BaseBpm : Settings.Bpm;
                var local = TimelineBuilder.Build(track.AngleData, track.Events, bpm);
                if (local.Positions.Count < 2)
                    return new Core.Timing.Vec2(0, 0);
                return main.Positions[anchor] - local.Positions[1];
            }
            catch
            {
                return new Core.Timing.Vec2(0, 0);
            }
        }

        private static double ResolveExitAngleDeg(List<float> angleData, int seq)
        {
            if (seq >= 0 && seq < angleData.Count && angleData[seq] != 999f)
                return angleData[seq];
            for (int k = Math.Min(seq, angleData.Count - 1); k >= 0; k--)
            {
                if (angleData[k] != 999f) return angleData[k];
            }
            return double.NaN;
        }

        public static bool DeleteCurrentTrack(scnEditor editor)
        {
            if (!HasSession || editor == null || PlayableActive) return false;
            if (CurrentTrackId == 0)
            {
                SetError("主轨不能删除");
                return false;
            }

            SaveCurrentFromEditor(editor);
            int deleting = CurrentTrackId;
            Chart.Tracks.RemoveAll(t => t.Id == deleting);
            CurrentTrackId = 0;
            LevelAdapter.WriteTrack(editor.levelData, Chart.Main);
            editor.RemakePath();
            editor.undoStates.Clear();
            editor.redoStates.Clear();
            if (editor.floors.Count > 0) editor.SelectFloor(editor.floors[0], false);
            RefreshFloorNumbers(editor);
            SetInfo("删除副轨 " + deleting);
            Notify();
            return true;
        }

        // ---------------------------------------------------------------- 保存 / 试玩

        /// <summary>临时把播放谱装进 levelData（保存与试玩共用）。</summary>
        public static bool BeginPlayable(scnEditor editor)
        {
            if (!HasSession || editor == null || PlayableActive) return false;
            try
            {
                SaveCurrentFromEditor(editor);
                Settings = LevelAdapter.BuildSettings(editor.levelData);
                ActivePlayable = ChartCompiler.Compile(Chart, Settings);
                LevelAdapter.ApplyPlayable(editor.levelData, ActivePlayable);
                PlayableActive = true;
                LastError = null;
                SetInfo("已编译播放谱：砖数 " + ActivePlayable.AngleData.Count + "（逻辑 " +
                        Chart.Main.AngleData.Count + "），动作 " + ActivePlayable.Actions.Count +
                        "，装饰 " + ActivePlayable.Decorations.Count);
                Notify();
                return true;
            }
            catch (CompileError ex)
            {
                ActivePlayable = null;
                PlayableActive = false;
                SetError("编译失败（" + ex.Code + "）：" + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                ActivePlayable = null;
                PlayableActive = false;
                SetError("编译失败：" + ex.Message);
                Main.Log(ex.ToString());
                return false;
            }
        }

        /// <summary>把逻辑谱换回 levelData（保存后 / 退出试玩时）。</summary>
        public static void EndPlayable(scnEditor editor)
        {
            if (!PlayableActive || editor == null) return;
            try
            {
                var track = Chart?.Find(CurrentTrackId) ?? Chart?.Main;
                if (track != null) LevelAdapter.WriteTrack(editor.levelData, track);
            }
            catch (Exception ex)
            {
                Main.Log("EndPlayable failed: " + ex);
            }
            finally
            {
                PlayableActive = false;
                ActivePlayable = null;
            }
        }

        /// <summary>试玩时把播放谱砖号翻译回逻辑砖号（接头砖显示前一块逻辑砖）。</summary>
        public static int TranslatePlayableToLogical(int playableSeq)
        {
            var map = ActiveMap;
            if (map == null) return playableSeq;
            try { return map.ToLogical(playableSeq); }
            catch { return playableSeq; }
        }

        public static bool ShouldTranslateFloorNumbers => PlayableActive && ActiveMap != null;

        /// <summary>
        /// 把编辑器里每块砖的序号文字刷成「当前该显示的号」。
        /// r150 只在 OnBecameVisible 里写这个文字，RemakePath 复用旧砖时不会重写，
        /// 所以切轨/退出试玩后必须手动刷一遍，否则 Ctrl+F 会看到上一套（甚至播放谱的）号码。
        /// </summary>
        public static void RefreshFloorNumbers(scnEditor editor)
        {
            if (editor == null || editor.floors == null) return;
            foreach (var f in editor.floors)
            {
                try
                {
                    if (f == null || f.editorNumText == null || f.editorNumText.letterText == null) continue;
                    int shown = PlayableActive ? TranslatePlayableToLogical(f.seqID) : f.seqID;
                    f.editorNumText.letterText.text = shown.ToString();
                }
                catch { }
            }
        }

    }
}

