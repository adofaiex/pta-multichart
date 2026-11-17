using System;
using ADOFAI;

namespace PtaMultichart.Editor
{
    internal static class PatchLoadGuard
    {
        private static string _lastPath;
        private static double _lastSeconds;

        /// <summary>同一次打开过程中 LoadLevel 可能被调用两次（r150 的 OpenLevelCo 路径），第二次不要再把会话清空。</summary>
        public static bool ShouldSkip(LevelData data)
        {
            string path = null;
            try { path = ADOBase.levelPath; } catch { }
            double now = TimeNow();
            bool skip = !string.IsNullOrEmpty(path) && path == _lastPath && (now - _lastSeconds) < 2.0;
            _lastPath = path;
            _lastSeconds = now;
            return skip;
        }

        private static double TimeNow()
        {
            try { return UnityEngine.Time.realtimeSinceStartup; } catch { return 0; }
        }
    }
}
