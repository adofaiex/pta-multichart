using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using PtaMultichart.Core.Compile;
using PtaMultichart.Core.Model;
using PtaMultichart.Game;
using PtaMultichart.Session;
using UnityEngine;

namespace PtaMultichart
{
    /// <summary>
    /// 给测试桥（pta-bridge 的 invoke）用的静态入口：桥的 invoke 只支持静态成员，
    /// 这里把会话操作包一层，方便无 UI 的端到端测试。也方便手动排错。
    /// </summary>
    public static class Api
    {
        public static string State()
        {
            try
            {
                var chart = MultitrackSession.Chart;
                var tracks = new List<object>();
                if (chart != null)
                {
                    foreach (var t in chart.Tracks)
                    {
                        tracks.Add(new Dictionary<string, object>
                        {
                            ["id"] = t.Id,
                            ["anchor"] = t.AnchorFloor,
                            ["bpm"] = t.BaseBpm,
                            ["ccw"] = t.IsCCW,
                            ["exitAngle"] = t.ExitAngle,
                            ["angles"] = t.AngleData.Count,
                            ["actions"] = t.Events.Count,
                            ["decorations"] = t.Decorations.Count
                        });
                    }
                }

                return MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["hasSession"] = MultitrackSession.HasSession,
                    ["currentTrack"] = MultitrackSession.CurrentTrackId,
                    ["playableActive"] = MultitrackSession.PlayableActive,
                    ["tracks"] = tracks,
                    ["lastInfo"] = MultitrackSession.LastInfo,
                    ["lastError"] = MultitrackSession.LastError
                });
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + Escape(ex.Message) + "\"}";
            }
        }

        public static string SwitchTrack(int id)
        {
            var editor = scnEditor.instance;
            if (editor == null) return "{\"ok\":false,\"error\":\"not in editor\"}";
            bool ok = MultitrackSession.SwitchTo(id, editor);
            return "{\"ok\":" + (ok ? "true" : "false") + ",\"current\":" + MultitrackSession.CurrentTrackId + "}";
        }

        public static string CreateSub(int anchor)
        {
            var editor = scnEditor.instance;
            if (editor == null) return "{\"ok\":false,\"error\":\"not in editor\"}";
            var track = MultitrackSession.CreateSubTrack(anchor, editor);
            if (track == null) return "{\"ok\":false,\"error\":\"" + Escape(MultitrackSession.LastError ?? "unknown") + "\"}";
            return "{\"ok\":true,\"id\":" + track.Id + ",\"current\":" + MultitrackSession.CurrentTrackId +
                   ",\"bpm\":" + track.BaseBpm +
                   ",\"ccw\":" + (track.IsCCW ? "true" : "false") +
                   ",\"exitAngle\":" + track.ExitAngle +
                   ",\"ghosts\":" + PtaMultichart.Editor.TrackOverlay.GhostCount + "}";
        }

        public static string DeleteTrack(int id)
        {
            var editor = scnEditor.instance;
            if (editor == null) return "{\"ok\":false,\"error\":\"not in editor\"}";
            if (id != MultitrackSession.CurrentTrackId && !MultitrackSession.SwitchTo(id, editor))
                return "{\"ok\":false,\"error\":\"switch failed\"}";
            bool ok = MultitrackSession.DeleteCurrentTrack(editor);
            return "{\"ok\":" + (ok ? "true" : "false") + ",\"current\":" + MultitrackSession.CurrentTrackId + "}";
        }

        /// <summary>只编译不动数据，返回规模与插入点（排错/验收用）。</summary>
        public static string CompileProbe()
        {
            try
            {
                var editor = scnEditor.instance;
                if (editor == null) return "{\"ok\":false,\"error\":\"not in editor\"}";
                MultitrackSession.SaveCurrentFromEditor(editor);
                var settings = LevelAdapter.BuildSettings(editor.levelData);
                var chart = ChartCompiler.Compile(MultitrackSession.Chart, settings);
                var clicks = new List<object>();
                foreach (var c in chart.InsertedClicks)
                {
                    clicks.Add(new Dictionary<string, object>
                    {
                        ["subTrack"] = c.SubTrackId,
                        ["subSeq"] = c.SubSeq,
                        ["playableSeq"] = c.PlayableSeq,
                        ["time"] = c.SongTime
                    });
                }
                return MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["angles"] = chart.AngleData.Count,
                    ["floors"] = chart.FloorCount,
                    ["actions"] = chart.Actions.Count,
                    ["decorations"] = chart.Decorations.Count,
                    ["inserted"] = chart.Map.InsertedCount,
                    ["insertedClicks"] = clicks
                });
            }
            catch (CompileError ex)
            {
                return "{\"ok\":false,\"code\":\"" + Escape(ex.Code) + "\",\"error\":\"" + Escape(ex.Message) + "\"}";
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"" + Escape(ex.Message) + "\"}";
            }
        }

        /// <summary>当前会话里每条轨的砖号/事件摘要（JSON 字符串）。</summary>
        public static string Tracks()
        {
            try
            {
                var list = new List<object>();
                foreach (var t in MultitrackSession.Chart.Tracks)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = t.Id,
                        ["anchor"] = t.AnchorFloor,
                        ["angles"] = t.AngleData.Select(a => (double)a).ToList(),
                        ["actions"] = t.Events.Select(e => e.Type).ToList()
                    });
                }
                return MiniJson.Serialize(list);
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + Escape(ex.Message) + "\"}";
            }
        }

        /// <summary>偏移线的两个端点，以及被挪开的砖的起点和现在的位置。镜头挪到这段线的中点。</summary>
        public static string OffsetLineReport()
        {
            try
            {
                var editor = scnEditor.instance;
                if (editor == null) return "{\"ok\":false,\"error\":\"not in editor\"}";

                var lines = new List<object>();
                foreach (var lr in UnityEngine.Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None))
                {
                    if (lr == null) continue;
                    string name = lr.gameObject.name;
                    if (name != "pta-offset-line" && name != "Floor connector") continue;
                    Vector3 a = lr.GetPosition(0);
                    Vector3 b = lr.GetPosition(1);
                    lines.Add(new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["from"] = new List<object> { Math.Round(a.x, 3), Math.Round(a.y, 3) },
                        ["to"] = new List<object> { Math.Round(b.x, 3), Math.Round(b.y, 3) }
                    });
                }

                var shifted = new List<object>();
                scrFloor framed = null;
                if (editor.floors != null)
                {
                    foreach (var tile in editor.floors)
                    {
                        if (tile == null) continue;
                        Vector3 d = tile.transform.position - tile.startPos;
                        if (d.sqrMagnitude < 0.01f) continue;
                        shifted.Add(new Dictionary<string, object>
                        {
                            ["seq"] = tile.seqID,
                            ["start"] = new List<object> { Math.Round(tile.startPos.x, 3), Math.Round(tile.startPos.y, 3) },
                            ["pos"] = new List<object> { Math.Round(tile.transform.position.x, 3), Math.Round(tile.transform.position.y, 3) }
                        });
                        if (framed == null) framed = tile;
                    }
                }

                if (framed != null && editor.camera != null)
                {
                    Vector3 mid = (framed.startPos + framed.transform.position) * 0.5f;
                    editor.camera.transform.position = new Vector3(mid.x, mid.y, -10f);
                }

                return MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["lines"] = lines,
                    ["shifted"] = shifted
                });
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"" + Escape(ex.Message) + "\"}";
            }
        }

        /// <summary>当前场景里已创建的装饰对象摘要（验证 AddObject 是否真的生成）。</summary>
        public static string Decorations()
        {
            try
            {
                var list = new List<object>();
                var mgr = scrDecorationManager.instance;
                var decs = mgr != null ? mgr.allDecorations : null;
                if (decs != null)
                {
                    for (int i = 0; i < decs.Count; i++)
                    {
                        var d = decs[i];
                        if (d == null) continue;
                        list.Add(new Dictionary<string, object>
                        {
                            ["index"] = i,
                            ["name"] = d.gameObject != null ? d.gameObject.name : null,
                            ["tag"] = SafeTag(d),
                            ["type"] = d.GetType().Name,
                            ["pos"] = d.transform != null ? new List<object> { d.transform.position.x, d.transform.position.y } : null
                        });
                    }
                }
                return MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["count"] = list.Count,
                    ["items"] = list
                });
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + Escape(ex.Message) + "\"}";
            }
        }

        private static string SafeTag(object dec)
        {
            try
            {
                var f = dec.GetType().GetField("decorationTag", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null) return Convert.ToString(f.GetValue(dec));
                var p = dec.GetType().GetProperty("decorationTag", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p != null) return Convert.ToString(p.GetValue(dec));
            }
            catch { }
            return null;
        }

        /// <summary>触发一次编译并把结果直接装进编辑器（不动逻辑谱，试玩/保存之外用）。</summary>
        public static string ApplyPreview()
        {
            var editor = scnEditor.instance;
            if (editor == null) return "{\"ok\":false,\"error\":\"not in editor\"}";
            bool ok = MultitrackSession.BeginPlayable(editor);
            return "{\"ok\":" + (ok ? "true" : "false") + ",\"error\":\"" + Escape(MultitrackSession.LastError ?? "") + "\"}";
        }

        /// <summary>其它轨道的可见砖，以及正在编辑的那条轨的世界坐标。</summary>
        public static string Overlay()
        {
            try
            {
                var ghosts = new List<object>();
                var root = GameObject.Find("PtaTrackGhosts");
                if (root != null)
                {
                    var tiles = root.GetComponentsInChildren<PtaMultichart.Editor.GhostTile>(true);
                    foreach (var tile in tiles)
                    {
                        var p = tile.transform.position;
                        ghosts.Add(new Dictionary<string, object>
                        {
                            ["track"] = tile.TrackId,
                            ["seq"] = tile.SeqId,
                            ["x"] = Math.Round(p.x, 3),
                            ["y"] = Math.Round(p.y, 3)
                        });
                    }
                }

                var live = new List<object>();
                var editor = scnEditor.instance;
                if (editor != null && editor.floors != null)
                {
                    foreach (var floor in editor.floors)
                    {
                        if (floor == null) continue;
                        var p = floor.transform.position;
                        live.Add(new Dictionary<string, object>
                        {
                            ["seq"] = floor.seqID,
                            ["x"] = Math.Round(p.x, 3),
                            ["y"] = Math.Round(p.y, 3)
                        });
                    }
                }

                return MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["currentTrack"] = MultitrackSession.CurrentTrackId,
                    ["ghosts"] = PtaMultichart.Editor.TrackOverlay.GhostCount,
                    ["status"] = PtaMultichart.Editor.TrackOverlay.LastStatus,
                    ["ghostTiles"] = ghosts,
                    ["liveTiles"] = live
                });
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + Escape(ex.Message) + "\"}";
            }
        }

        /// <summary>在当前选中的砖上插入一块新砖，和编辑器方向键走的是同一个 InsertFloatFloor。</summary>
        public static string InsertAngle(float deg)
        {
            var editor = scnEditor.instance;
            if (editor == null) return "{\"ok\":false,\"error\":\"not in editor\"}";
            if (editor.selectedFloors == null || editor.selectedFloors.Count != 1 || editor.selectedFloors[0] == null)
                return "{\"ok\":false,\"error\":\"need one selected floor\"}";
            int seq = editor.selectedFloors[0].seqID;
            int before = editor.levelData.angleData.Count;
            editor.InsertFloatFloor(seq, deg);
            int select = Mathf.Clamp(seq + 1, 0, editor.floors.Count - 1);
            editor.SelectFloor(editor.floors[select], false);
            return "{\"ok\":true,\"current\":" + MultitrackSession.CurrentTrackId +
                   ",\"anglesBefore\":" + before +
                   ",\"angles\":" + editor.levelData.angleData.Count +
                   ",\"floors\":" + editor.floors.Count +
                   ",\"selected\":" + select +
                   ",\"ghosts\":" + PtaMultichart.Editor.TrackOverlay.GhostCount + "}";
        }

        /// <summary>
        /// 在世界坐标上做一次和编辑器一样的地板射线，用来确认其它轨道的碰撞体真的能被点到。
        /// </summary>
        public static string ClickWorld(float x, float y)
        {
            var editor = scnEditor.instance;
            if (editor == null) return "{\"ok\":false,\"error\":\"not in editor\"}";
            var touched = new List<Collider2D>();
            try
            {
                if (editor.floors != null)
                {
                    foreach (var floor in editor.floors)
                    {
                        if (floor == null) continue;
                        var mesh = floor.GetComponent<FloorMesh>();
                        if (mesh == null || mesh.polygonCollider == null) continue;
                        if (!mesh.polygonCollider.enabled)
                        {
                            try { mesh.GenerateCollider(); } catch { }
                            mesh.polygonCollider.enabled = true;
                            touched.Add(mesh.polygonCollider);
                        }
                    }
                }

                var hits = Physics2D.RaycastAll(new Vector2(x, y), Vector2.zero, 0f);
                scrFloor chosen = null;
                int bestOrder = int.MaxValue;
                int floorHits = 0;
                for (int i = 0; i < hits.Length; i++)
                {
                    var col = hits[i].collider;
                    if (col == null) continue;
                    var floor = col.GetComponent<scrFloor>();
                    if (floor == null) floor = col.GetComponentInParent<scrFloor>();
                    if (floor == null) continue;
                    floorHits++;
                    int order = 0;
                    try { if (floor.floorRenderer != null) order = floor.floorRenderer.sortingOrder; } catch { }
                    if (chosen == null || order < bestOrder)
                    {
                        chosen = floor;
                        bestOrder = order;
                    }
                }

                if (chosen == null)
                    return "{\"ok\":false,\"hits\":" + hits.Length + ",\"floorHits\":0}";

                string hitName = chosen.gameObject.name;
                bool wasGhost = chosen.GetComponent<PtaMultichart.Editor.GhostTile>() != null;
                editor.SelectFloor(chosen, false);
                int selected = -1;
                if (editor.selectedFloors != null && editor.selectedFloors.Count > 0 && editor.selectedFloors[0] != null)
                    selected = editor.selectedFloors[0].seqID;
                return "{\"ok\":true,\"current\":" + MultitrackSession.CurrentTrackId +
                       ",\"selected\":" + selected +
                       ",\"hit\":\"" + Escape(hitName) + "\"" +
                       ",\"wasGhost\":" + (wasGhost ? "true" : "false") +
                       ",\"floorHits\":" + floorHits +
                       ",\"ghosts\":" + PtaMultichart.Editor.TrackOverlay.GhostCount + "}";
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"" + Escape(ex.Message) + "\"}";
            }
            finally
            {
                foreach (var col in touched)
                {
                    if (col != null) col.enabled = false;
                }
            }
        }

        /// <summary>模拟点中一条非当前轨上的砖，编辑器应切到那条轨并选中同一序号。</summary>
        public static string SelectGhost(int trackId, int seq)
        {
            var editor = scnEditor.instance;
            if (editor == null) return "{\"ok\":false,\"error\":\"not in editor\"}";
            var go = GameObject.Find("pta-ghost-t" + trackId + "-" + seq);
            if (go == null) return "{\"ok\":false,\"error\":\"ghost not found\"}";
            var floor = go.GetComponent<scrFloor>();
            if (floor == null) return "{\"ok\":false,\"error\":\"no floor\"}";
            editor.SelectFloor(floor, false);
            int selected = -1;
            if (editor.selectedFloors != null && editor.selectedFloors.Count > 0 && editor.selectedFloors[0] != null)
                selected = editor.selectedFloors[0].seqID;
            float x = 0, y = 0;
            if (selected >= 0 && selected < editor.floors.Count && editor.floors[selected] != null)
            {
                x = editor.floors[selected].transform.position.x;
                y = editor.floors[selected].transform.position.y;
            }
            return "{\"ok\":true,\"current\":" + MultitrackSession.CurrentTrackId +
                   ",\"selected\":" + selected +
                   ",\"x\":" + x.ToString("0.###") + ",\"y\":" + y.ToString("0.###") +
                   ",\"ghosts\":" + PtaMultichart.Editor.TrackOverlay.GhostCount + "}";
        }

        /// <summary>关掉编辑器里的通知弹窗（测试截图用）。</summary>
        public static string ClosePopup()
        {
            try
            {
                var ed = scnEditor.instance;
                if (ed == null) return "{\"ok\":false,\"error\":\"not in editor\"}";
                if (ed.notificationPopupContainer != null) ed.notificationPopupContainer.SetActive(false);
                return "{\"ok\":true}";
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"" + Escape(ex.Message) + "\"}";
            }
        }
        private static string Escape(string s)
        {
            return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}




