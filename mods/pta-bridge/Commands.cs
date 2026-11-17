using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PtaBridge
{
    /// <summary>可安全回给客户端的错误（消息会原样返回）。</summary>
    internal sealed class BridgeError : Exception
    {
        public BridgeError(string message) : base(message) { }
    }

    /// <summary>
    /// 所有 cmd 的实现。**只在 Unity 主线程执行**（由 Bridge.Pump 调用）。
    /// 任何异常都会被上层捕获并转成 {"ok":false,"error":...}。
    /// </summary>
    internal static class Commands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static object Execute(string cmd, Dictionary<string, object> args)
        {
            if (args == null) args = new Dictionary<string, object>();
            string c = (cmd ?? string.Empty).Trim().ToLowerInvariant();

            switch (c)
            {
                case "": throw new BridgeError("missing cmd");
                case "ping": return CmdPing();
                case "state": return CmdState();
                case "floors": return CmdFloors(args);
                case "openlevel": return CmdOpenLevel(args);
                case "openeditor": return CmdOpenEditor(args);
                case "save": return CmdSave();
                case "play": return CmdPlay();
                case "exitedit": return CmdExitEdit();
                case "setauto": return CmdSetAuto(args);
                case "hits": return CmdHits();
                case "resethits": return CmdResetHits();
                case "setfloornums": return CmdSetFloorNums(args);
                case "selectfloor": return CmdSelectFloor(args);
                case "invoke": return CmdInvoke(args);
                case "screenshot": return CmdScreenshot(args);
                case "log": return CmdLog();
                case "quit": return CmdQuit();
                case "gotolevelselect": return CmdGoToLevelSelect();
                case "help": return CmdHelp();
                default: throw new BridgeError("unknown cmd '" + cmd + "' (try cmd 'help')");
            }
        }

        // ==================== 基础辅助 ====================

        private static T Safe<T>(Func<T> func, T fallback)
        {
            try { return func(); }
            catch { return fallback; }
        }

        private static void TryLog(Action action, string what)
        {
            try { action(); }
            catch (Exception ex) { BridgeLog.Warn(what + " failed", ex); }
        }

        private static string SceneName()
        {
            string s = Safe(() => ADOBase.sceneName, null);
            if (!string.IsNullOrEmpty(s)) return s;
            return Safe(() => SceneManager.GetActiveScene().name, null);
        }

        private static scnEditor EditorOrThrow()
        {
            var ed = Safe(() => scnEditor.instance, null);
            if (ed == null)
            {
                throw new BridgeError("not in level editor (scnEditor.instance == null); call cmd 'openeditor' first");
            }
            return ed;
        }

        private static List<scrFloor> FloorList()
        {
            var ed = Safe(() => scnEditor.instance, null);
            if (ed != null)
            {
                var list = Safe(() => ed.floors, null);
                if (list != null) return list;
            }

            var lm = Safe(() => scrLevelMaker.instance, null);
            if (lm != null)
            {
                var list = Safe(() => lm.listFloors, null);
                if (list != null) return list;
            }

            throw new BridgeError("no floor list available (not in editor / no level loaded)");
        }

        /// <summary>是否真的在“演奏中”（菜单里 scrController 也可能存在且未暂停，所以还要看场景）。</summary>
        private static bool ComputeIsPlaying()
        {
            var ctrl = Safe(() => scrController.instance, null);
            if (ctrl == null) return false;
            if (Safe(() => ctrl.paused, true)) return false;

            var ed = Safe(() => scnEditor.instance, null);
            if (ed != null) return Safe(() => ed.playMode, false);

            return Safe(() => ADOBase.isScnGame, false);
        }

        private static string ResolveLevelPath(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                throw new BridgeError("missing 'path'");
            }

            string p = raw;
            try { p = Environment.ExpandEnvironmentVariables(p); } catch { }

            if (!Path.IsPathRooted(p))
            {
                p = Path.GetFullPath(p);
            }

            if (!File.Exists(p))
            {
                throw new BridgeError("level file not found: " + p);
            }

            return p;
        }

        // ==================== ping ====================

        private static object CmdPing()
        {
            var ctrl = Safe(() => scrController.instance, null);

            var d = new Dictionary<string, object>();
            d["mod"] = Main.ModId;
            d["version"] = Main.Version;
            d["unityVersion"] = Safe(() => Application.unityVersion, null);
            d["gameVersion"] = Safe(() => Application.version, null);
            d["scene"] = SceneName();
            d["isLevelEditor"] = Safe(() => ADOBase.isLevelEditor, false);
            d["hasController"] = ctrl != null;
            d["isPlaying"] = ComputeIsPlaying();
            d["editorPlayMode"] = Safe(() => ADOBase.editor != null && ADOBase.editor.playMode, false);
            d["levelPath"] = Safe(() => ADOBase.levelPath, null);
            d["acceptedAgreement"] = Safe(() => Persistence.acceptedAgreement, false);
            d["port"] = Bridge.Port;
            d["server"] = Bridge.ServerKind;
            d["queued"] = Bridge.QueueDepth;
            d["pid"] = Safe(() => System.Diagnostics.Process.GetCurrentProcess().Id, 0);
            d["frameCount"] = Safe(() => Time.frameCount, 0);
            d["screenWidth"] = Safe(() => Screen.width, 0);
            d["screenHeight"] = Safe(() => Screen.height, 0);
            d["uptimeSec"] = Math.Round((DateTime.UtcNow - Bridge.StartedUtc).TotalSeconds, 1);
            d["logFile"] = BridgeLog.FilePath;
            return d;
        }

        // ==================== state ====================

        private static object CmdState()
        {
            var ctrl = Safe(() => scrController.instance, null);
            var ed = Safe(() => scnEditor.instance, null);

            int floorCount = -1;
            var list = Safe(() => FloorList(), null);
            if (list != null) floorCount = list.Count;

            var d = new Dictionary<string, object>();
            d["scene"] = SceneName();
            d["isLevelEditor"] = ed != null;
            d["mode"] = ed != null ? "editor" : (ctrl != null ? "play" : "none");
            d["levelPath"] = Safe(() => ADOBase.levelPath, null);
            d["floorCount"] = floorCount;
            d["floors"] = floorCount;              // 同 floorCount（老脚本用 floors.Count 口径）
            d["currentSeqID"] = ctrl != null ? Safe(() => ctrl.currentSeqID, -1) : -1;
            d["paused"] = ctrl != null && Safe(() => ctrl.paused, false);
            d["isPlaying"] = ComputeIsPlaying();
            d["percentComplete"] = ctrl != null
                ? Math.Round(Safe(() => (double)ctrl.percentComplete, 0.0), 6)
                : 0.0;
            d["songPosition"] = Safe(() => scrConductor.instance == null ? 0.0 : scrConductor.instance.songposition_minusi, 0.0);
            d["songPositionV"] = Safe(() => scrConductor.instance == null ? 0.0 : scrConductor.instance.songposition_minusv, 0.0);
            d["gameworld"] = ctrl != null && Safe(() => ctrl.gameworld, false);
            d["showFloorNums"] = ed != null ? Safe(() => ed.showFloorNums, false) : false;
            d["playMode"] = ed != null ? Safe(() => ed.playMode, false) : false;
            d["pausedInPlayMode"] = ed != null ? Safe(() => ed.pausedInPlayMode, false) : false;
            d["isLoading"] = ed != null ? Safe(() => ed.isLoading, false) : false;
            d["selectedFloors"] = ed != null ? SelectedFloorIds(ed) : new List<object>();
            d["selectedFloorCached"] = ed != null ? Safe(() => ed.selectedFloorCached, -1) : -1;

            if (ed != null)
            {
                d["angleCount"] = Safe(() => (ed.levelData != null && ed.levelData.angleData != null) ? ed.levelData.angleData.Count : -1, -1);
                d["song"] = Safe(() => ed.levelData == null ? null : ed.levelData.song, null);
                d["bpm"] = Safe(() => ed.levelData == null ? 0.0 : (double)ed.levelData.bpm, 0.0);
                d["levelVersion"] = Safe(() => ed.levelData == null ? -1 : ed.levelData.version, -1);
            }
            else
            {
                d["angleCount"] = -1;
            }

            return d;
        }

        private static List<object> SelectedFloorIds(scnEditor ed)
        {
            var ids = new List<object>();
            try
            {
                var sel = ed.selectedFloors;
                if (sel != null)
                {
                    foreach (var f in sel)
                    {
                        if (f != null) ids.Add(f.seqID);
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("reading selectedFloors failed", ex);
            }
            return ids;
        }

        // ==================== floors ====================

        private static object CmdFloors(Dictionary<string, object> args)
        {
            var list = FloorList();
            int from = Json.Int(args, "from", 0);
            int count = Json.Int(args, "count", int.MaxValue);

            if (from < 0) from = 0;
            if (count < 0) count = 0;

            long rawEnd = (long)from + (long)count;
            int end = (int)Math.Min((long)list.Count, rawEnd);
            if (from > list.Count) from = list.Count;

            var result = new List<object>(Math.Max(0, end - from));
            for (int i = from; i < end; i++)
            {
                result.Add(DescribeFloor(list[i]));
            }
            return result;
        }

        private static object DescribeFloor(scrFloor f)
        {
            var d = new Dictionary<string, object>();
            if (f == null)
            {
                d["seqID"] = -1;
                return d;
            }

            d["seqID"] = Safe(() => f.seqID, -1);
            d["entryBeat"] = Math.Round(Safe(() => f.entryBeat, 0.0), 6);
            d["entryTime"] = Math.Round(Safe(() => f.entryTime, 0.0), 6);
            d["speed"] = Math.Round(Safe(() => (double)f.speed, 1.0), 6);
            d["isCCW"] = Safe(() => f.isCCW, false);
            d["midSpin"] = Safe(() => f.midSpin, false);
            d["angleLength"] = Math.Round(Safe(() => f.angleLength, 0.0), 6);
            d["opacityVal"] = Math.Round(Safe(() => (double)f.opacityVal, 1.0), 6);
            d["holdLength"] = Safe(() => f.holdLength, -1);
            d["isFake"] = Safe(() => f.isFake, false);

            Vector3 pos = Safe(() => f.transform.position, Vector3.zero);
            d["position"] = Json.Obj("x", Math.Round((double)pos.x, 6), "y", Math.Round((double)pos.y, 6));

            d["editorNumText"] = FloorNumText(f);
            d["editorNumVisible"] = Safe(() => f.editorNumText != null
                                            && f.editorNumText.gameObject != null
                                            && f.editorNumText.gameObject.activeSelf, false);
            return d;
        }

        private static string FloorNumText(scrFloor f)
        {
            return Safe<string>(() =>
            {
                if (f.editorNumText == null) return null;
                var text = f.editorNumText.letterText;
                return text == null ? null : text.text;
            }, null);
        }

        // ==================== openlevel / openeditor ====================

        private static object CmdOpenLevel(Dictionary<string, object> args)
        {
            var ed = EditorOrThrow();
            string path = ResolveLevelPath(Json.Str(args, "path", null));

            int before = Safe(() => ed.floors == null ? -1 : ed.floors.Count, -1);
            ed.OpenLevel(path);

            return Json.Obj("submitted", true, "path", path, "floorsBefore", before);
        }

        /// <summary>
        /// 进入编辑器（可选同时指定要打开的谱面）。
        /// 已在编辑器：有 path 就 OpenLevel，否则什么都不做。
        /// 不在编辑器：levelToOpenOnLoad + 转场（先在 PortalTravelAction 里走 GCS.sceneToLoad 流程）。
        /// </summary>
        private static object CmdOpenEditor(Dictionary<string, object> args)
        {
            string raw = Json.Str(args, "path", null);
            string path = string.IsNullOrEmpty(raw) ? null : ResolveLevelPath(raw);

            var ed = Safe(() => scnEditor.instance, null);
            if (ed != null)
            {
                if (path != null)
                {
                    ed.OpenLevel(path);
                    return Json.Obj("submitted", true, "way", "editor.OpenLevel", "path", path, "scene", SceneName());
                }
                return Json.Obj("submitted", false, "way", "already-in-editor", "path", null, "scene", SceneName());
            }

            if (path != null)
            {
                scnEditor.levelToOpenOnLoad = path;
            }

            var ctrl = Safe(() => scrController.instance, null);
            if (ctrl != null)
            {
                ctrl.PortalTravelAction(Portal.EditorScene);
                return Json.Obj("submitted", true, "way", "PortalTravelAction", "path", path,
                                "scene", SceneName(), "levelToOpenOnLoad", Safe(() => scnEditor.levelToOpenOnLoad, null));
            }

            var loader = Safe(() => ADOBase.loader, null);
            if (loader != null)
            {
                loader.LoadScene("scnEditor");
                return Json.Obj("submitted", true, "way", "loader.LoadScene", "path", path,
                                "scene", SceneName(), "levelToOpenOnLoad", Safe(() => scnEditor.levelToOpenOnLoad, null));
            }

            // 最后兜底：和暂停菜单里的 LevelEditor 按钮完全一致
            SceneManager.LoadScene("scnEditor", LoadSceneMode.Additive);
            return Json.Obj("submitted", true, "way", "SceneManager.LoadScene(additive)", "path", path,
                            "scene", SceneName(), "levelToOpenOnLoad", Safe(() => scnEditor.levelToOpenOnLoad, null));
        }

        // ==================== save ====================

        private static object CmdSave()
        {
            var ed = EditorOrThrow();
            string path = Safe(() => ADOBase.levelPath, null);
            ed.SaveLevel();

            long size = -1;
            string mtime = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                size = Safe(() => new FileInfo(path).Length, -1L);
                mtime = Safe(() => File.GetLastWriteTimeUtc(path).ToString("o", Inv), null);
            }

            return Json.Obj("saved", true, "path", path, "size", size, "mtimeUtc", mtime);
        }

        // ==================== play / exitedit ====================

        private static object CmdPlay()
        {
            var ed = EditorOrThrow();
            int before = Safe(() => ed.floors == null ? -1 : ed.floors.Count, -1);
            ed.Play();
            return Json.Obj("submitted", true, "floors", before, "playMode", Safe(() => ed.playMode, false));
        }

        private static object CmdExitEdit()
        {
            var ed = Safe(() => scnEditor.instance, null);
            if (ed == null)
            {
                throw new BridgeError("not in level editor (scnEditor.instance == null)");
            }

            try
            {
                ed.SwitchToEditMode(false);
            }
            catch (Exception ex)
            {
                throw new BridgeError("SwitchToEditMode failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            var ctrl = Safe(() => scrController.instance, null);
            return Json.Obj("submitted", true,
                            "playMode", Safe(() => ed.playMode, false),
                            "currentSeqID", ctrl != null ? Safe(() => ctrl.currentSeqID, -1) : -1);
        }

        // ==================== setauto ====================

        private static object CmdSetAuto(Dictionary<string, object> args)
        {
            bool on = Json.Bool(args, "on", true);

            // RDC.auto 就是 RDConstants.data.auto，游戏里所有自动播放判断都读它
            RDC.auto = on;

            var ed = Safe(() => scnEditor.instance, null);
            if (ed != null)
            {
                TryLog(() => ed.autoFailed = false, "reset autoFailed");
            }

            return Json.Obj("auto", Safe(() => RDC.auto, on),
                            "editorAutoFailed", ed != null ? Safe(() => ed.autoFailed, false) : (object)null);
        }

        // ==================== hits ====================

        private static object CmdHits()
        {
            var trackers = Safe(() => scrMistakesManager.marginTrackers, null);
            if (trackers == null || trackers.Length == 0)
            {
                throw new BridgeError("scrMistakesManager.marginTrackers unavailable");
            }

            var t = trackers[0];
            if (t == null)
            {
                throw new BridgeError("marginTrackers[0] is null");
            }

            var names = new List<object>();
            try
            {
                foreach (var hm in t.hitMargins)
                {
                    names.Add(hm.ToString());
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("reading hitMargins failed", ex);
            }

            var counts = new List<object>();
            var byName = new Dictionary<string, object>();
            try
            {
                int n = t.hitMarginsCount == null ? 0 : t.hitMarginsCount.Length;
                for (int i = 0; i < n; i++)
                {
                    int v = t.hitMarginsCount[i];
                    counts.Add(v);
                    byName[((HitMargin)i).ToString()] = v;
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("reading hitMarginsCount failed", ex);
            }

            var ctrl = Safe(() => scrController.instance, null);

            var d = new Dictionary<string, object>();
            d["hitMargins"] = names;
            d["hitMarginsLength"] = names.Count;
            d["hitMarginsCount"] = counts;
            d["hitMarginsCountByName"] = byName;
            d["playerHitMarginCount"] = Safe(() => t.playerHitMarginCount, -1);
            d["deaths"] = Safe(() => t.deaths, -1);
            d["xScore"] = Safe(() => t.xScore, -1);
            d["hardestDifficulty"] = Safe(() => t.hardestDifficulty.ToString(), null);
            d["percentComplete"] = ctrl != null
                ? Math.Round(Safe(() => (double)ctrl.percentComplete, 0.0), 6)
                : Math.Round(Safe(() => (double)t.percentComplete, 0.0), 6);
            d["percentAcc"] = Math.Round(Safe(() => (double)t.percentAcc, 0.0), 6);
            d["currentSeqID"] = ctrl != null ? Safe(() => ctrl.currentSeqID, -1) : -1;
            d["trackers"] = trackers.Length;
            d["gameworld"] = ctrl != null && Safe(() => ctrl.gameworld, false);
            return d;
        }

        private static object CmdResetHits()
        {
            var pm = Safe(() => ADOBase.playerManager, null);
            if (pm != null && Safe(() => pm.mistakesManager != null, false))
            {
                pm.mistakesManager.Reset();
            }
            else
            {
                var trackers = Safe(() => scrMistakesManager.marginTrackers, null);
                if (trackers == null) throw new BridgeError("scrMistakesManager.marginTrackers unavailable");
                foreach (var t in trackers)
                {
                    if (t != null) t.Reset();
                }
            }

            return CmdHits();
        }

        // ==================== setfloornums / selectfloor ====================

        private static object CmdSetFloorNums(Dictionary<string, object> args)
        {
            var ed = EditorOrThrow();
            bool on = Json.Bool(args, "on", true);

            ed.showFloorNums = on;

            try
            {
                ed.RemakePath();
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("RemakePath failed", ex);
            }

            var list = Safe(() => FloorList(), null);
            var arr = new List<object>();
            if (list != null)
            {
                foreach (var f in list)
                {
                    arr.Add(Json.Obj("seqID", Safe(() => f.seqID, -1),
                                     "text", FloorNumText(f),
                                     "visible", Safe(() => f.editorNumText != null
                                                           && f.editorNumText.gameObject != null
                                                           && f.editorNumText.gameObject.activeSelf, false)));
                }
            }

            return Json.Obj("showFloorNums", Safe(() => ed.showFloorNums, on),
                            "count", arr.Count,
                            "floors", arr);
        }

        private static object CmdSelectFloor(Dictionary<string, object> args)
        {
            var ed = EditorOrThrow();
            var list = FloorList();

            int id = Json.Int(args, "id", -1);
            if (id < 0 || id >= list.Count)
            {
                throw new BridgeError("floor id out of range: " + id + " (floors=" + list.Count + ")");
            }

            var floor = list[id];
            if (floor == null) throw new BridgeError("floor " + id + " is null");

            // scnEditor.SelectFloor(scrFloor floorToSelect, bool cameraJump = true)
            ed.SelectFloor(floor, false);

            return Json.Obj("selected", id,
                            "seqID", Safe(() => floor.seqID, -1),
                            "selectedFloors", SelectedFloorIds(ed));
        }

        // ==================== invoke（反射调试工具箱） ====================

        private static object CmdInvoke(Dictionary<string, object> args)
        {
            return ReflectInvoke.Execute(
                Json.Str(args, "type", null),
                Json.Str(args, "member", null),
                Json.Array(args, "args"));
        }

        // ==================== screenshot ====================

        private static object CmdScreenshot(Dictionary<string, object> args)
        {
            string raw = Json.Str(args, "path", null);
            string path;
            if (string.IsNullOrEmpty(raw))
            {
                string dir = BridgeLog.ModDir ?? Safe(() => Path.GetTempPath(), ".");
                path = Path.Combine(dir, "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", Inv) + ".png");
            }
            else
            {
                path = raw;
                try { path = Environment.ExpandEnvironmentVariables(path); } catch { }
                if (!Path.IsPathRooted(path)) path = Path.GetFullPath(path);
            }

            string dirName = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dirName)) System.IO.Directory.CreateDirectory(dirName);

            int w = Json.Int(args, "width", 0);
            int h = Json.Int(args, "height", 0);
            if (w <= 0) w = Safe(() => Screen.width, 1280);
            if (h <= 0) h = Safe(() => Screen.height, 720);
            w = Math.Max(16, Math.Min(8192, w));
            h = Math.Max(16, Math.Min(8192, h));

            Camera cam = null;
            var scrCam = Safe(() => scrCamera.instance, null);
            if (scrCam != null) cam = Safe(() => scrCam.camobj, null);
            if (cam == null) cam = Safe(() => Camera.main, null);
            if (cam == null) throw new BridgeError("no camera available (scrCamera.instance.camobj and Camera.main are both null)");

            RenderTexture rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture prevTarget = null;
            try { prevTarget = cam.targetTexture; } catch { }
            Texture2D tex = null;
            int bytes;

            try
            {
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                RenderTexture.active = prevActive;

                byte[] png = ImageConversion.EncodeToPNG(tex);
                if (png == null || png.Length == 0) throw new BridgeError("EncodeToPNG returned no data");

                File.WriteAllBytes(path, png);
                bytes = png.Length;
            }
            finally
            {
                try { cam.targetTexture = prevTarget; } catch { }
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                if (tex != null)
                {
                    try { UnityEngine.Object.Destroy(tex); } catch { }
                }
            }

            return Json.Obj("path", path, "width", w, "height", h, "bytes", bytes);
        }

        // ==================== log / quit / 其他 ====================

        private static object CmdLog()
        {
            string[] lines = BridgeLog.Tail(200);
            return Json.Obj("file", BridgeLog.FilePath, "count", lines.Length, "lines", lines);
        }

        private static object CmdQuit()
        {
            BridgeLog.Info("quit requested via bridge");
            Main.RequestQuit();
            return Json.Obj("quitting", true,
                            "pid", Safe(() => System.Diagnostics.Process.GetCurrentProcess().Id, 0));
        }

        private static object CmdGoToLevelSelect()
        {
            BridgeLog.Info("goto level select");
            ADOBase.GoToLevelSelect();
            return Json.Obj("submitted", true, "way", "ADOBase.GoToLevelSelect", "scene", SceneName());
        }

        private static object CmdHelp()
        {
            var cmds = new Dictionary<string, object>();
            cmds["ping"] = "版本 / unityVersion / scene / isLevelEditor / isPlaying ...";
            cmds["state"] = "编辑器与播放状态（levelPath、angleCount、floors、showFloorNums、playMode、pausedInPlayMode、selectedFloors、currentSeqID、percentComplete、songPosition）";
            cmds["floors"] = "{from?,count?} -> [{seqID,entryBeat,entryTime,speed,isCCW,midSpin,angleLength,opacityVal,holdLength,position{x,y},editorNumText,editorNumVisible,isFake}]";
            cmds["openlevel"] = "{path} -> scnEditor.OpenLevel(path)（异步，之后轮询 state）";
            cmds["openeditor"] = "{path?} -> 进编辑器（必要时 levelToOpenOnLoad + 转场），已在编辑器则 OpenLevel";
            cmds["save"] = "scnEditor.SaveLevel()";
            cmds["play"] = "scnEditor.Play()";
            cmds["exitedit"] = "scnEditor.SwitchToEditMode(false)";
            cmds["setauto"] = "{on} -> RDC.auto";
            cmds["hits"] = "marginTrackers[0] 的 hitMargins / hitMarginsCount / deaths / percentComplete ...";
            cmds["resethits"] = "重置 mistakesManager，返回重置后的 hits";
            cmds["setfloornums"] = "{on} -> showFloorNums + RemakePath()，返回每块砖的 editorNumText";
            cmds["selectfloor"] = "{id} -> scnEditor.SelectFloor(floors[id], false)";
            cmds["invoke"] = "{type,member,args?} 反射读写静态成员（临时调试用）";
            cmds["screenshot"] = "{path?,width?,height?} -> 渲染到 RenderTexture 并写 PNG";
            cmds["log"] = "最近 200 行桥日志";
            cmds["quit"] = "Application.Quit()（先 flush 日志/响应）";
            cmds["gotolevelselect"] = "ADOBase.GoToLevelSelect()";
            return Json.Obj("cmds", cmds, "endpoint", "POST http://127.0.0.1:" + Bridge.Port + "/cmd");
        }
    }
}
