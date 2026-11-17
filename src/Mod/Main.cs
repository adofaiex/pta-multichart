using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;

namespace PtaMultichart
{
    public static class Main
    {
        public static UnityModManager.ModEntry Mod { get; private set; }
        public static Harmony Harmony { get; private set; }
        private static bool _patched;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;
            Mod.Logger.Log("pta-multichart loading (v0.1.0)");
            Harmony = new Harmony(modEntry.Info.Id);
            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;
            ApplyPatches();
            _patched = true;
            Mod.Logger.Log("pta-multichart active");
            return true;
        }

        /// <summary>逐个 patch class 应用，单个失败不影响其它（r150 里方法签名有变动时很常见）。</summary>
        public static void ApplyPatches()
        {
            var harmony = Harmony;
            if (harmony == null) return;
            int ok = 0, failed = 0;
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Mod.Logger.Log("patch class failed: " + type.FullName + " :: " + ex.Message);
                }
            }
            Mod.Logger.Log("patches applied: " + ok + " ok, " + failed + " failed");
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (value)
            {
                if (_patched) return true;
                Harmony = Harmony ?? new Harmony(modEntry.Info.Id);
                ApplyPatches();
                _patched = true;
            }
            else
            {
                Harmony?.UnpatchAll(modEntry.Info.Id);
                _patched = false;
            }
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            UnityEngine.GUILayout.Label("pta-multichart v0.1.0");
        }

        public static void Log(string msg)
        {
            Mod?.Logger.Log(msg);
        }
    }
}



