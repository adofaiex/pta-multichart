using System;
using ADOFAI;
using ADOFAI.Editor.Actions;
using HarmonyLib;
using PtaMultichart.Session;
using UnityEngine;

namespace PtaMultichart.Editor
{
    internal static class PatchState
    {
        public static bool RestoredFromPlay;
        public static bool SaveSwapped;

        /// <summary>
        /// 按当前 levelData.decorations 重建场景里的装饰物。
        /// 试玩前 levelData 里已经是副轨的 Floor / Planet；回到编辑后再调一次，把它们清掉。
        /// </summary>
        public static void RefreshDecorations(scnEditor editor)
        {
            if (editor == null) return;
            editor.UpdateDecorationObjects();
            var mgr = scrDecorationManager.instance;
            if (mgr != null)
            {
                mgr.FinishUpdateDecorations();
                AlignFloorMeshes(editor, mgr);
            }
        }

        /// <summary>
        /// 装饰砖和真砖用同一层、同一套材质，球才能画在砖上面，上半截也不会发灰。
        /// 旋转烤进网格的两条边，竖着的砖才不会横着浮空。
        /// </summary>
        private static void AlignFloorMeshes(scnEditor editor, scrDecorationManager mgr)
        {
            FloorMeshRenderer sample = null;
            if (editor.floors != null)
            {
                foreach (var floor in editor.floors)
                {
                    var mesh = floor != null ? floor.floorRenderer as FloorMeshRenderer : null;
                    if (mesh == null || mesh.floorMesh == null) continue;
                    if (mesh.floorMesh._length <= 0.01f || mesh.floorMesh._width <= 0.01f) continue;
                    sample = mesh;
                    break;
                }
            }

            foreach (var dec in mgr.allDecorations)
            {
                var obj = dec as scrObjectDecoration;
                if (obj == null) continue;
                var ev = obj.sourceLevelEvent;
                if (ev == null) continue;
                string tag = ev["tag"] as string;
                if (string.IsNullOrEmpty(tag) || tag.IndexOf("pta-mc", StringComparison.Ordinal) < 0) continue;

                if (obj.objectType == ObjectDecorationType.Planet)
                {
                    PutDecorationInPlace(obj);
                    continue;
                }
                if (obj.objectType != ObjectDecorationType.Floor) continue;
                if (string.Equals(Convert.ToString(ev["trackType"]), "Midspin", StringComparison.Ordinal))
                {
                    ApplyDecorationIcon(obj);
                    PutDecorationInPlace(obj);
                    continue;
                }

                float trackAngle = ev.GetFloat("trackAngle");
                float rotation = ev.GetFloat("rotation");
                var meshRenderer = obj.floor != null ? obj.floor.floorRenderer as FloorMeshRenderer : null;
                if (meshRenderer == null || meshRenderer.floorMesh == null) continue;

                try { obj.SetFloorStyle(TrackStyle.Standard); } catch { }
                ApplyDecorationIcon(obj);
                obj.startRot = 0f;
                obj.SetRotation(0f);
                meshRenderer.floorMesh._angle0 = -180f + rotation;
                meshRenderer.floorMesh._angle1 = 180f - trackAngle + rotation;
                if (sample != null)
                {
                    meshRenderer.floorMesh._length = sample.floorMesh._length;
                    meshRenderer.floorMesh._width = sample.floorMesh._width;
                }
                PutDecorationInPlace(obj);
            }
        }

        /// <summary>
        /// 换材质之后图标纹理会丢。按装饰上已经写好的 trackIcon 再贴一次。
        /// 装饰砖当成 isFake，漩涡的颜色、朝向用我们算好的值，不会被默认入口角改掉。
        /// </summary>
        private static void ApplyDecorationIcon(scrObjectDecoration obj)
        {
            var ev = obj.sourceLevelEvent;
            var floor = obj.floor;
            if (ev == null || floor == null) return;

            string name = Convert.ToString(ev["trackIcon"]);
            if (string.IsNullOrEmpty(name)) name = "None";
            CustomFloorIcon icon;
            if (!Enum.TryParse(name, true, out icon)) icon = CustomFloorIcon.None;

            floor.isFake = true;
            var last = typeof(scrFloor).GetField("lastIconSprite",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (last != null) last.SetValue(floor, null);

            try { obj.SetFloorRedSwirl(ev.GetBool("trackRedSwirl")); } catch { }
            float bpm = 100f;
            try { bpm = ev.GetFloat("trackSetSpeedIconBpm"); } catch { }
            if (bpm <= 0f) bpm = 100f;
            bool gray = false;
            try { gray = ev.GetBool("trackGraySetSpeedIcon"); } catch { }
            obj.SetFloorIcon(icon, gray, bpm);
            try { obj.SetFloorIconAngle(ev.GetFloat("trackIconAngle")); } catch { }
            try { obj.SetFloorIconFlipped(ev.GetBool("trackIconFlipped")); } catch { }
            try { obj.SetFloorIconOutlines(ev.GetBool("trackIconOutlines")); } catch { }
        }

        /// <summary>
        /// 装饰的 depth 只能选 Bg / Default。Default 画在星球前面，砖就会盖住球，上半截也发灰。
        /// 砖锁在 Floor 层，球锁在 Planet 层，和游戏里的真砖、真球同一套顺序。
        /// </summary>
        internal static void PutDecorationInPlace(scrObjectDecoration obj)
        {
            if (obj == null) return;
            if (obj.objectType == ObjectDecorationType.Planet)
            {
                if (obj.planetRenderer == null) return;
                obj.planetRenderer.SetLayer("Planet");
                var renderers = obj.planetRenderer.appearanceRenderers;
                if (renderers == null) return;
                foreach (var rend in renderers)
                {
                    if (rend == null) continue;
                    rend.sortingLayerName = "Planet";
                    rend.sortingOrder = 50;
                }
                return;
            }
            if (obj.objectType != ObjectDecorationType.Floor || obj.floor == null) return;
            var meshRenderer = obj.floor.floorRenderer as FloorMeshRenderer;
            if (meshRenderer != null && meshRenderer.renderer != null)
                meshRenderer.renderer.sortingLayerName = "Floor";
            obj.floor.SetSortingOrder(0);
            if (obj.floor.topGlow != null) obj.floor.topGlow.gameObject.SetActive(false);
            if (meshRenderer != null && meshRenderer.material != null && meshRenderer.material.HasProperty("_ShadowColor"))
                meshRenderer.material.SetColor("_ShadowColor", new Color(0f, 0f, 0f, 0f));
        }
    }

    [HarmonyPatch(typeof(scrObjectDecoration), "SetDepth")]
    internal static class PatchDecorationDepth
    {
        [HarmonyPostfix]
        public static void Postfix(scrObjectDecoration __instance)
        {
            var ev = __instance != null ? __instance.sourceLevelEvent : null;
            if (ev == null) return;
            string tag = ev["tag"] as string;
            if (string.IsNullOrEmpty(tag) || tag.IndexOf("pta-mc", StringComparison.Ordinal) < 0) return;
            PatchState.PutDecorationInPlace(__instance);
        }
    }

    /// <summary>
    /// 没有歌名、作者、艺术家时，游戏每次读 Hash 都打一条错误。
    /// 错误框会卡住主线程，星球就停在砖上不动。
    /// </summary>
    [HarmonyPatch(typeof(LevelData), nameof(LevelData.Hash), MethodType.Getter)]
    internal static class PatchLevelHash
    {
        [HarmonyPrefix]
        public static bool Prefix(LevelData __instance, ref string __result)
        {
            if (__instance == null) return true;
            try
            {
                if (!string.IsNullOrEmpty(__instance.artist) || !string.IsNullOrEmpty(__instance.song) || !string.IsNullOrEmpty(__instance.author))
                    return true;
            }
            catch
            {
                return true;
            }
            __result = "pta-mc";
            return false;
        }
    }

    [HarmonyPatch(typeof(scnGame), nameof(scnGame.LoadLevel))]
    internal static class PatchLoadLevel
    {
        [HarmonyPostfix]
        public static void Postfix(scnGame __instance, bool __result)
        {
            if (!__result || __instance == null) return;
            if (!ADOBase.isLevelEditor) return;
            try
            {
                if (PatchLoadGuard.ShouldSkip(__instance.levelData)) return;
                MultitrackSession.OnLevelLoaded(__instance.levelData);
            }
            catch (Exception ex)
            {
                Main.Log("LoadLevel postfix failed: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SaveLevel))]
    internal static class PatchSaveLevel
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            try
            {
                var editor = scnEditor.instance;
                if (editor == null) return;
                bool swapped = MultitrackSession.BeginPlayable(editor);
                PatchState.SaveSwapped = swapped && MultitrackSession.PlayableActive;
            }
            catch (Exception ex)
            {
                Main.Log("SaveLevel prefix failed: " + ex);
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer()
        {
            try
            {
                if (!PatchState.SaveSwapped) return;
                PatchState.SaveSwapped = false;
                var editor = scnEditor.instance;
                if (editor != null) MultitrackSession.EndPlayable(editor);
            }
            catch (Exception ex)
            {
                Main.Log("SaveLevel finalizer failed: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.Play))]
    internal static class PatchPlay
    {
        [HarmonyPrefix]
        public static bool Prefix(scnEditor __instance)
        {
            try
            {
                if (!MultitrackSession.HasSession) return true;
                if (MultitrackSession.PlayableActive) return true;
                if (!MultitrackSession.BeginPlayable(__instance))
                {
                    string msg = MultitrackSession.LastError ?? "编译失败";
                    try { __instance.ShowNotification(msg); } catch { }
                    Main.Log("Play cancelled: " + msg);
                    return false;
                }
                // RemakePath 只重铺砖，不会根据新写进 levelData 的 AddObject 生成装饰。
                // 必须在原方法的 ApplyEventsToFloors 之前建好，MoveDecorations 才能按 tag 找到副轨。
                PatchState.RefreshDecorations(__instance);
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Play prefix failed: " + ex);
                return true;
            }
        }

        /// <summary>试玩时把砖号文字刷成逻辑砖号（RemakePath 复用旧砖时 OnBecameVisible 不会再触发）。</summary>
        [HarmonyPostfix]
        public static void Postfix(scnEditor __instance)
        {
            try
            {
                if (!MultitrackSession.ShouldTranslateFloorNumbers) return;
                if (__instance == null || __instance.floors == null) return;
                MultitrackSession.RefreshFloorNumbers(__instance);
            }
            catch (Exception ex)
            {
                Main.Log("Play postfix failed: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SwitchToEditMode))]
    internal static class PatchSwitchToEditMode
    {
        /// <summary>
        /// 注意：**不能**在 Prefix 里把 levelData 换回逻辑谱。SwitchToEditMode 的方法体会用「播放谱的 floors」
        /// 去索引 levelData（DrawHolds / DrawMultiPlanet 等），提前换数据会让索引越界。
        /// 所以这里只在 Prefix 记状态，Postfix 里换数据 + RemakePath。
        /// </summary>
        [HarmonyPrefix]
        public static void Prefix()
        {
            PatchState.RestoredFromPlay = MultitrackSession.PlayableActive;
        }

        [HarmonyPostfix]
        public static void Postfix(scnEditor __instance)
        {
            if (!PatchState.RestoredFromPlay) return;
            PatchState.RestoredFromPlay = false;
            try
            {
                MultitrackSession.EndPlayable(__instance);
                __instance.RemakePath();
                __instance.undoStates.Clear();
                __instance.redoStates.Clear();
                if (__instance.floors.Count > 0) __instance.SelectFloor(__instance.floors[0], false);
                MultitrackSession.RefreshFloorNumbers(__instance);
                PatchState.RefreshDecorations(__instance);
                MultitrackSession.SetInfo("已回到逻辑谱（编辑态）");
            }
            catch (Exception ex)
            {
                Main.Log("SwitchToEditMode postfix failed: " + ex);
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(Exception __exception)
        {
            if (__exception != null)
                Main.Log("SwitchToEditMode threw: " + __exception);
        }
    }

    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.NewLevel))]
    internal static class PatchNewLevel
    {
        [HarmonyPostfix]
        public static void Postfix(scnEditor __instance)
        {
            try
            {
                MultitrackSession.OnNewLevel(__instance.levelData);
            }
            catch (Exception ex)
            {
                Main.Log("NewLevel postfix failed: " + ex);
            }
        }
    }

    /// <summary>试玩开始时的检查点砖号要翻译成播放谱砖号（方案 §7.3 的砖号映射）。</summary>
    [HarmonyPatch(typeof(scnGame), nameof(scnGame.Play))]
    internal static class PatchGamePlay
    {
        [HarmonyPrefix]
        public static void Prefix(ref int seqID)
        {
            try
            {
                if (!MultitrackSession.PlayableActive) return;
                var map = MultitrackSession.ActiveMap;
                if (map == null) return;
                int translated = map.ToPlayable(seqID);
                var lm = ADOBase.lm;
                int max = lm != null && lm.listFloors != null ? lm.listFloors.Count - 1 : translated;
                seqID = Mathf.Clamp(translated, 0, Mathf.Max(0, max));
                GCS.checkpointNum = seqID;
            }
            catch (Exception ex)
            {
                Main.Log("scnGame.Play prefix failed: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(scrFloor), "OnBecameVisible")]
    internal static class PatchFloorNumberText
    {
        [HarmonyPostfix]
        public static void Postfix(scrFloor __instance)
        {
            try
            {
                if (__instance == null || __instance.editorNumText == null) return;
                if (!MultitrackSession.ShouldTranslateFloorNumbers) return;
                __instance.editorNumText.letterText.text =
                    MultitrackSession.TranslatePlayableToLogical(__instance.seqID).ToString();
            }
            catch (Exception ex)
            {
                Main.Log("floor number patch failed: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(ShowCurrentFloorNumberEditorAction), nameof(ShowCurrentFloorNumberEditorAction.Execute))]
    internal static class PatchShowCurrentFloorNumber
    {
        [HarmonyPrefix]
        public static bool Prefix(scnEditor editor)
        {
            try
            {
                if (!MultitrackSession.ShouldTranslateFloorNumbers) return true;
                if (!editor.SelectionIsSingle()) return true;
                int logical = MultitrackSession.TranslatePlayableToLogical(editor.selectedFloors[0].seqID);
                editor.ShowNotification("Currently selected floor is " + logical + "!");
                Debug.Log("THIS IS FLOOR " + logical + " (logical) - entry time " +
                          editor.selectedFloors[0].entryTime + " - entry beat " + editor.selectedFloors[0].entryBeat);
                return false;
            }
            catch (Exception ex)
            {
                Main.Log("floor number action patch failed: " + ex);
                return true;
            }
        }
    }

    /// <summary>点到其它轨道的砖时，把编辑器切到那条轨并选中同一块砖。砖留在原来的位置上。</summary>
    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SelectFloor))]
    internal static class PatchSelectFloor
    {
        private static bool _redirecting;

        [HarmonyPrefix]
        public static bool Prefix(scnEditor __instance, scrFloor floorToSelect, bool cameraJump)
        {
            if (_redirecting || floorToSelect == null) return true;
            GhostTile ghost;
            try { ghost = floorToSelect.GetComponent<GhostTile>(); }
            catch { return true; }
            if (ghost == null) return true;

            int trackId = ghost.TrackId;
            int seq = ghost.SeqId;
            _redirecting = true;
            try
            {
                if (trackId != MultitrackSession.CurrentTrackId)
                    MultitrackSession.SwitchTo(trackId, __instance);
                if (__instance.floors == null || __instance.floors.Count == 0) return false;
                int index = Mathf.Clamp(seq, 0, __instance.floors.Count - 1);
                __instance.SelectFloor(__instance.floors[index], cameraJump);
            }
            catch (Exception ex)
            {
                Main.Log("select ghost failed: " + ex);
            }
            finally
            {
                _redirecting = false;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.RemakePath))]
    internal static class PatchRemakePath
    {
        [HarmonyPostfix]
        public static void Postfix(scnEditor __instance)
        {
            try
            {
                TrackOverlay.Refresh(__instance, true);
                FixOffsetLines(__instance);
            }
            catch (Exception ex)
            {
                Main.Log("RemakePath overlay failed: " + ex);
            }
        }

        /// <summary>
        /// 游戏把偏移线画在上一块砖的侧边上，和这块砖的真实位移对不齐。
        /// 改成从「没偏移时的位置」连到砖现在的位置。
        /// </summary>
        private static void FixOffsetLines(scnEditor editor)
        {
            var root = GameObject.Find("Floor Connector Lines");
            if (root != null)
            {
                for (int i = root.transform.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(root.transform.GetChild(i).gameObject);
            }
            if (editor == null || editor.floors == null || editor.events == null) return;

            Material mat = _offsetLineMaterial;
            if (mat == null)
            {
                var field = typeof(scnEditor).GetField("lineMaterial",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                mat = field != null ? field.GetValue(editor) as Material : null;
                if (mat == null)
                {
                    Shader shader = Shader.Find("Sprites/Default");
                    if (shader == null) return;
                    mat = new Material(shader);
                }
                _offsetLineMaterial = mat;
            }

            foreach (var ev in editor.events)
            {
                if (ev == null || ev.eventType != LevelEventType.PositionTrack) continue;
                if (ev.disabled != null && ev.disabled.TryGetValue("positionOffset", out var disabled) && disabled) continue;
                int floor = ev.floor;
                if (floor < 0 || floor >= editor.floors.Count) continue;
                var tile = editor.floors[floor];
                if (tile == null) continue;
                Vector3 from = tile.startPos;
                Vector3 to = tile.transform.position;
                if ((from - to).sqrMagnitude < 0.0025f) continue;

                var go = new GameObject("pta-offset-line");
                if (root != null) go.transform.SetParent(root.transform, true);
                var line = go.AddComponent<LineRenderer>();
                line.positionCount = 2;
                line.material = mat;
                line.textureMode = LineTextureMode.Tile;
                line.startColor = new Color(0.75f, 0.5f, 1f, 1f);
                line.endColor = new Color(0.35f, 0.55f, 1f, 1f);
                line.startWidth = line.endWidth = 0.1f;
                line.useWorldSpace = true;
                line.sortingOrder = 30;
                line.SetPosition(0, from);
                line.SetPosition(1, to);
            }
        }

        private static Material _offsetLineMaterial;
    }

    [HarmonyPatch(typeof(scnEditor), "DragCamera")]
    internal static class PatchDragCamera
    {
        [HarmonyPrefix]
        public static bool Prefix() => !MultitrackPanel.BlocksEditorPointer();
    }

    [HarmonyPatch(typeof(scnEditor), "DragTiles")]
    internal static class PatchDragTiles
    {
        [HarmonyPrefix]
        public static bool Prefix() => !MultitrackPanel.BlocksEditorPointer();
    }

    [HarmonyPatch(typeof(scnEditor), "DragDecorations")]
    internal static class PatchDragDecorations
    {
        [HarmonyPrefix]
        public static bool Prefix() => !MultitrackPanel.BlocksEditorPointer();
    }

    /// <summary>
    /// 没连上下一块砖时，游戏会把这块砖画成终点传送门。
    /// 其它轨道只有最后一块没有 nextfloor，那里仍是终点。
    /// 其余砖走游戏自己的图标：旋转、变速、长按。
    /// </summary>
    [HarmonyPatch(typeof(scrFloor), nameof(scrFloor.UpdateIconSprite))]
    internal static class PatchGhostIcon
    {
        [HarmonyPrefix]
        public static bool Prefix(scrFloor __instance)
        {
            GhostTile ghost;
            try { ghost = __instance.GetComponent<GhostTile>(); }
            catch { return true; }
            if (ghost == null || __instance.nextfloor != null) return true;

            try
            {
                if (ADOBase.gc != null) __instance.SetIconSprite(ADOBase.gc.sprPortal);
            }
            catch (Exception ex)
            {
                Main.Log("ghost icon failed: " + ex.Message);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(scnEditor), "Start")]
    internal static class PatchEditorStart
    {
        [HarmonyPostfix]
        public static void Postfix(scnEditor __instance)
        {
            try
            {
                MultitrackPanel.Ensure(__instance);
            }
            catch (Exception ex)
            {
                Main.Log("panel creation failed: " + ex);
            }
        }
    }
}



