using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityModManagerNet;
using UnityEngine;

namespace PtaBridge
{
    /// <summary>UMM 入口。Info.json 里 EntryMethod = "PtaBridge.Main.Load"。</summary>
    public static class Main
    {
        public const string ModId = "pta-bridge";
        public const string Version = "0.1.0";

        public static UnityModManager.ModEntry Mod { get; private set; }

        private static GameObject _go;
        private static BridgeRunner _runner;

        private static bool _quitRequested;
        private static DateTime _quitAtUtc = DateTime.MinValue;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;

            string dir = ResolveModDir();
            BridgeLog.Init(modEntry, dir);
            BridgeLog.Info("loading " + ModId + " v" + Version +
                           " (unity " + SafeString(() => Application.unityVersion) +
                           ", pid " + Safe(() => System.Diagnostics.Process.GetCurrentProcess().Id, 0) + ")");
            BridgeLog.Info("mod dir: " + (dir ?? "<unknown>"));

            try
            {
                if (modEntry != null)
                {
                    modEntry.OnToggle = OnToggle;
                    modEntry.OnGUI = OnGUI;
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("registering UMM callbacks failed", ex);
            }

            EnsureRunner();

            try
            {
                int port = ResolvePort();
                Bridge.Start(port);
                BridgeLog.Info("ready: POST http://127.0.0.1:" + port + "/cmd");
            }
            catch (Exception ex)
            {
                BridgeLog.Error("starting bridge server failed", ex);
            }

            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            BridgeLog.Info("toggle -> " + value);
            if (value)
            {
                EnsureRunner();
                if (!Bridge.IsRunning)
                {
                    try { Bridge.Start(Bridge.Port); }
                    catch (Exception ex) { BridgeLog.Error("restart failed", ex); }
                }
            }
            else
            {
                Bridge.Stop();
            }
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            try
            {
                GUILayout.Label("pta-bridge v" + Version + "  [" + (Bridge.IsRunning ? "running" : "stopped") + "]");
                GUILayout.Label("POST http://127.0.0.1:" + Bridge.Port + "/cmd   (" + Bridge.ServerKind + ")");
                GUILayout.Label("queue: " + Bridge.QueueDepth);
                GUILayout.Label("log: " + (BridgeLog.FilePath ?? "-"));

                if (!Bridge.IsRunning)
                {
                    if (GUILayout.Button("Start bridge"))
                    {
                        Bridge.Start(Bridge.Port);
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("OnGUI failed", ex);
            }
        }

        // ---------- 主线程对象 ----------

        /// <summary>创建/DontDestroyOnLoad 泵对象。必须在主线程。</summary>
        private static void EnsureRunner()
        {
            try
            {
                if (_runner != null) return;

                _go = new GameObject("pta-bridge");
                UnityEngine.Object.DontDestroyOnLoad(_go);
                _runner = _go.AddComponent<BridgeRunner>();
                BridgeLog.Info("runner GameObject created (\"pta-bridge\")");
            }
            catch (Exception ex)
            {
                BridgeLog.Error("EnsureRunner failed (are we on the main thread?)", ex);
            }
        }

        // ---------- 端口 ----------

        private static int ResolvePort()
        {
            string raw = null;

            string env = SafeString(() => Environment.GetEnvironmentVariable("PTA_BRIDGE_PORT"));
            if (!string.IsNullOrEmpty(env)) raw = env;

            string[] argv = Safe(() => Environment.GetCommandLineArgs(), null);
            if (argv != null)
            {
                for (int i = 0; i < argv.Length; i++)
                {
                    string a = argv[i];
                    if (string.IsNullOrEmpty(a)) continue;

                    if (string.Equals(a, "-port", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "--port", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < argv.Length) raw = argv[i + 1];
                    }
                    else if (a.StartsWith("-port=", StringComparison.OrdinalIgnoreCase))
                    {
                        raw = a.Substring("-port=".Length);
                    }
                    else if (a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                    {
                        raw = a.Substring("--port=".Length);
                    }
                }
            }

            int port;
            if (!string.IsNullOrEmpty(raw) &&
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) &&
                port > 1023 && port < 65536)
            {
                return port;
            }

            return Bridge.DefaultPort;
        }

        // ---------- 退出 ----------

        /// <summary>请求退出（延迟一点，好让 HTTP 响应先写回客户端）。</summary>
        public static void RequestQuit()
        {
            _quitRequested = true;
            _quitAtUtc = DateTime.UtcNow.AddMilliseconds(400);
        }

        /// <summary>主线程每帧查一次：到点了就 Application.Quit()。</summary>
        public static bool QuitDue
        {
            get
            {
                if (!_quitRequested) return false;
                if (DateTime.UtcNow < _quitAtUtc) return false;
                _quitRequested = false;
                return true;
            }
        }

        // ---------- 小工具 ----------

        private static string ResolveModDir()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            }
            catch
            {
                return null;
            }
        }

        private static T Safe<T>(Func<T> func, T fallback)
        {
            try { return func(); }
            catch { return fallback; }
        }

        private static string SafeString(Func<string> func)
        {
            return Safe(func, null);
        }
    }
}
