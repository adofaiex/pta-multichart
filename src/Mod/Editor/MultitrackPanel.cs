using System;
using ADOFAI;
using PtaMultichart.Session;
using UnityEngine;
using UnityEngine.UI;

namespace PtaMultichart.Editor
{
    /// <summary>编辑器里的 IMGUI 面板：当前轨、轨道列表、创建/切换/删除副轨。</summary>
    public sealed class MultitrackPanel : MonoBehaviour
    {
        private static MultitrackPanel _instance;
        private Rect _window = new Rect(300f, 80f, 380f, 430f);
        private bool _visible = true;
        private Vector2 _scroll;
        private Vector2 _preflightScroll;
        private Image _blocker;

        /// <summary>鼠标在面板上时，编辑器不要跟着拖镜头或拖砖。</summary>
        public static bool BlocksEditorPointer()
        {
            if (_instance == null || !_instance._visible) return false;
            Vector2 gui = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return _instance._window.Contains(gui);
        }
        // 编译预检（方案 v2 §8）；null = 还没编译过。只用同一份列表，不新做控件族。
        private static PtaMultichart.Core.Compile.PreflightReport _preflight;

        public static MultitrackPanel Ensure(scnEditor editor)
        {
            if (_instance != null) return _instance;
            var go = new GameObject("PtaMultichartPanel");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<MultitrackPanel>();
            _instance.CreateBlocker(go);
            return _instance;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                _visible = !_visible;
            }
        }

        private void OnGUI()
        {
            if (!_visible) return;
            if (!ADOBase.isLevelEditor || scnEditor.instance == null) return;

            _window = GUI.Window(0x7A4D, _window, DrawWindow, "PTA Multichart v0.1");
            PlaceBlocker();
        }

        private void CreateBlocker(GameObject go)
        {
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            go.AddComponent<GraphicRaycaster>();
            var child = new GameObject("PtaPanelBlocker");
            child.transform.SetParent(go.transform, false);
            _blocker = child.AddComponent<Image>();
            _blocker.color = new Color(0f, 0f, 0f, 0f);
            _blocker.raycastTarget = true;
        }

        private void PlaceBlocker()
        {
            if (_blocker == null) return;
            _blocker.enabled = _visible;
            var rt = _blocker.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.sizeDelta = new Vector2(_window.width, _window.height);
            rt.anchoredPosition = new Vector2(_window.x, Screen.height - _window.yMax);
        }

        private void DrawWindow(int id)
        {
            var editor = scnEditor.instance;
            if (editor == null) return;

            GUILayout.BeginVertical();

            if (!MultitrackSession.HasSession)
            {
                GUILayout.Label("No session (open a level in the editor).");
                GUILayout.EndVertical();
                GUI.DragWindow(new Rect(0, 0, 10000, 20));
                return;
            }

            var current = MultitrackSession.Current;
            string mode = MultitrackSession.PlayableActive ? " [PLAYABLE DATA]" : "";
            GUILayout.Label("正在编辑: " + MultitrackSession.TrackName(MultitrackSession.CurrentTrackId) + mode);
            GUILayout.Label("所有轨道同时显示。点任意一条上的砖，就会改那一条。");
            if (current != null && !MultitrackSession.PlayableActive)
            {
                GUILayout.Label("  angleData: " + current.AngleData.Count + "   actions: " +
                                current.Events.Count + "   decorations: " + current.Decorations.Count);
            }

            GUILayout.Space(4);
            GUILayout.Label("Tracks:");
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(120));
            foreach (var t in MultitrackSession.Chart.Tracks)
            {
                bool isCurrent = t.Id == MultitrackSession.CurrentTrackId;
                string label = (isCurrent ? "> " : "  ") + MultitrackSession.TrackName(t.Id) +
                               "  [" + t.AngleData.Count + " tiles]";
                if (GUILayout.Button(label))
                {
                    if (!isCurrent) MultitrackSession.SwitchTo(t.Id, editor);
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Create sub track @ selected tile"))
            {
                CreateAtSelection(editor);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Selected: " + SelectedText(editor));
            if (GUILayout.Button("Delete current sub"))
            {
                MultitrackSession.DeleteCurrentTrack(editor);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            if (GUILayout.Button("Recompile preview (log)"))
            {
                PreviewCompile(editor);
            }

            if (!string.IsNullOrEmpty(MultitrackSession.LastError))
                GUILayout.Label("ERROR: " + MultitrackSession.LastError);
            if (!string.IsNullOrEmpty(MultitrackSession.LastInfo))
                GUILayout.Label(MultitrackSession.LastInfo);

            if (_preflight != null)
            {
                GUILayout.Space(4);
                GUILayout.Label("Preflight: " + _preflight.Count + " items, warnings: " + _preflight.WarningCount);
                _preflightScroll = GUILayout.BeginScrollView(_preflightScroll, GUILayout.Height(120));
                foreach (var item in _preflight.Items)
                {
                    GUILayout.Label((item.IsWarning ? "[!] " : "[i] ") + item.Message +
                                    (string.IsNullOrEmpty(item.Detail) ? "" : "  - " + item.Detail));
                }
                GUILayout.EndScrollView();
            }

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private static string SelectedText(scnEditor editor)
        {
            if (editor.selectedFloors == null || editor.selectedFloors.Count == 0) return "none";
            if (editor.selectedFloors.Count > 1) return editor.selectedFloors.Count + " tiles";
            return editor.selectedFloors[0].seqID.ToString();
        }

        private static void CreateAtSelection(scnEditor editor)
        {
            if (editor.selectedFloors == null || editor.selectedFloors.Count == 0)
            {
                MultitrackSession.SetError("请先在主轨上选中一块砖");
                return;
            }
            int anchor = editor.selectedFloors[0].seqID;
            MultitrackSession.CreateSubTrack(anchor, editor);
        }

        private static void PreviewCompile(scnEditor editor)
        {
            if (!MultitrackSession.HasSession) return;
            try
            {
                MultitrackSession.SaveCurrentFromEditor(editor);
                var settings = Game.LevelAdapter.BuildSettings(editor.levelData);
                var chart = PtaMultichart.Core.Compile.ChartCompiler.Compile(MultitrackSession.Chart, settings);
                _preflight = chart.Preflight;
                MultitrackSession.SetInfo("preview: tiles=" + chart.AngleData.Count +
                                          " actions=" + chart.Actions.Count +
                                          " decorations=" + chart.Decorations.Count +
                                          " | preflight: " + (_preflight == null ? "none" :
                                              _preflight.Count + " items, warnings: " + _preflight.WarningCount));
            }
            catch (Exception ex)
            {
                _preflight = null;
                MultitrackSession.SetError("preview failed: " + ex.Message);
            }
        }
    }
}

