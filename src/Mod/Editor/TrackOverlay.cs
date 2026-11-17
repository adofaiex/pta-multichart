using System;
using System.Collections.Generic;
using PtaMultichart.Core.Compile;
using PtaMultichart.Core.Model;
using PtaMultichart.Core.Timing;
using PtaMultichart.Session;
using UnityEngine;

namespace PtaMultichart.Editor
{
    /// <summary>挂在其它轨道的可见砖上。点它等于选中那条轨道上的对应砖。</summary>
    public sealed class GhostTile : MonoBehaviour
    {
        public int TrackId;
        public int SeqId;
    }

    /// <summary>
    /// 把当前没在编辑的轨道画在同一画面里。
    /// 正在编辑的那条轨仍是编辑器自己的地板。副轨整条平移，让 1 号砖落在锚点上。
    /// </summary>
    public static class TrackOverlay
    {
        public static int GhostCount { get; private set; }
        public static string LastStatus { get; private set; } = "";

        private static GameObject _root;
        private static bool _applying;

        public static void Clear()
        {
            GhostCount = 0;
            if (_root != null)
            {
                UnityEngine.Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        public static void Refresh(scnEditor editor, bool offsetLive)
        {
            if (_applying) return;
            if (editor == null || !MultitrackSession.HasSession || MultitrackSession.PlayableActive || editor.playMode)
            {
                Clear();
                LastStatus = "hidden";
                return;
            }

            _applying = true;
            try
            {
                MultitrackSession.SaveCurrentFromEditor(editor);
                if (offsetLive) ApplyLiveOffset(editor);
                RebuildGhosts(editor);
            }
            catch (Exception ex)
            {
                LastStatus = "error: " + ex.Message;
                Main.Log("TrackOverlay.Refresh failed: " + ex);
            }
            finally
            {
                _applying = false;
            }
        }

        private static void ApplyLiveOffset(scnEditor editor)
        {
            if (editor.floors == null || editor.floors.Count == 0) return;
            var track = MultitrackSession.Current;
            if (track == null) return;
            Vector3 origin = World(MultitrackSession.OriginOf(track));
            if (origin.sqrMagnitude < 1e-8f) return;
            foreach (var floor in editor.floors)
            {
                if (floor == null) continue;
                floor.transform.position += origin;
                floor.startPos += origin;
            }
        }

        private static void RebuildGhosts(scnEditor editor)
        {
            Clear();
            var chart = MultitrackSession.Chart;
            if (chart == null || chart.Tracks.Count < 2) 
            {
                LastStatus = "no other tracks";
                return;
            }

            var lm = ADOBase.lm;
            if (lm == null || lm.meshFloor == null || editor.floors == null || editor.floors.Count == 0)
            {
                LastStatus = "no mesh prefab";
                return;
            }

            scrFloor sample = editor.floors[0];
            _root = new GameObject("PtaTrackGhosts");
            int count = 0;
            foreach (var track in chart.Tracks)
            {
                if (track.Id == MultitrackSession.CurrentTrackId) continue;
                count += BuildTrack(editor, lm, sample, track);
            }
            GhostCount = count;
            LastStatus = "ghosts=" + count + " live=" + MultitrackSession.CurrentTrackId;
        }

        private static int BuildTrack(scnEditor editor, scrLevelMaker lm, scrFloor sample, Track track)
        {
            TrackTimeline timeline;
            try
            {
                timeline = TimelineBuilder.Build(track.AngleData, track.Events, MultitrackSession.Settings.Bpm);
            }
            catch (Exception ex)
            {
                Main.Log("ghost timeline failed for track " + track.Id + ": " + ex.Message);
                return 0;
            }

            Vector3 origin = World(MultitrackSession.OriginOf(track));
            List<Vec2> placed = TimelineBuilder.ApplyPositionTracks(timeline, track.Events, true);
            int built = 0;
            int n = Math.Min(timeline.Count, placed.Count);
            var made = new List<scrFloor>();
            for (int i = 0; i < n; i++)
            {
                // 副轨 0 号砖是锚点前的幽灵起点，不画。1 号砖才叠在锚点上。
                if (track.Id != 0 && i == 0) continue;
                var info = timeline[i];
                Vector3 local = World(placed[i]);
                var go = UnityEngine.Object.Instantiate(lm.meshFloor, origin + local, Quaternion.identity, _root.transform);
                go.name = "pta-ghost-t" + track.Id + "-" + i;
                var floor = go.GetComponent<scrFloor>();
                if (floor == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    continue;
                }

                floor.seqID = i;
                floor.entryangle = info.EntryAngle;
                floor.exitangle = info.ExitAngle;
                floor.midSpin = info.MidSpin;
                floor.floatDirection = float.IsNaN((float)info.Angle) ? sample.floatDirection : (float)info.Angle;
                floor.isCCW = info.IsCCW;
                floor.startPos = floor.transform.position;
                try { floor.SetTrackStyle(sample.initialTrackStyle, true); } catch { }
                try { floor.SetColor(sample.floorRenderer.color); } catch { }
                try { floor.UpdateAngle(); } catch { }
                // 叠在锚点上时，点击优先命中正在编辑的那条轨（排序值更小的排在射线结果前面）。
                try { floor.SetSortingOrder(sample.floorRenderer.sortingOrder + 40); } catch { }

                var mesh = floor.GetComponent<FloorMesh>();
                if (mesh != null)
                {
                    try
                    {
                        mesh.GenerateCollider();
                        if (mesh.polygonCollider != null)
                        {
                            mesh.polygonCollider.enabled = true;
                            mesh.polygonCollider.isTrigger = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Main.Log("ghost collider failed: " + ex.Message);
                    }
                }
                go.layer = sample.gameObject.layer;

                var tag = go.AddComponent<GhostTile>();
                tag.TrackId = track.Id;
                tag.SeqId = i;

                if (floor.editorNumText != null)
                {
                    floor.editorNumText.gameObject.SetActive(editor.showFloorNums && !editor.playMode);
                    if (floor.editorNumText.letterText != null)
                        floor.editorNumText.letterText.text = i.ToString();
                }
                made.Add(floor);
                built++;
            }

            for (int k = 0; k < made.Count; k++)
            {
                scrFloor tile = made[k];
                tile.prevfloor = k > 0 ? made[k - 1] : null;
                tile.nextfloor = k + 1 < made.Count ? made[k + 1] : null;
                int seq = tile.seqID;
                string iconName = DecorationBuilder.ChooseFloorIcon(track, timeline, seq);
                FloorIcon icon;
                if (!Enum.TryParse(iconName, true, out icon)) icon = FloorIcon.None;
                tile.floorIcon = icon;
                if (seq >= 0 && seq < timeline.Count)
                {
                    tile.speed = (float)timeline[seq].Speed;
                    if (timeline[seq].HoldBeats >= 0) tile.holdLength = timeline[seq].HoldBeats;
                }
                // 没有图标时不要拿关卡砖链表去猜旋转，那条链表是正在编辑的轨道。
                tile.usedCustomFloorIcon = tile.nextfloor != null && icon == FloorIcon.None;
                if (icon == FloorIcon.Checkpoint && tile.GetComponent<ffxCheckpoint>() == null)
                    tile.gameObject.AddComponent<ffxCheckpoint>();
                try { tile.UpdateIconSprite(); } catch { }
            }
            return built;
        }

        private static Vector3 World(Vec2 tileUnits)
        {
            float size = 1f;
            try
            {
                if (scrController.instance != null && scrController.instance.tileSize > 0.01f)
                    size = scrController.instance.tileSize;
            }
            catch { }
            return new Vector3((float)tileUnits.X * size, (float)tileUnits.Y * size, 0f);
        }
    }
}
