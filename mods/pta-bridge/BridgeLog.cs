using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityModManagerNet;

namespace PtaBridge
{
    /// <summary>
    /// 内存 ring buffer + bridge.log 文件日志。所有方法都不会抛异常。
    /// </summary>
    internal static class BridgeLog
    {
        private const int RingSize = 400;
        private const long RotateBytes = 2 * 1024 * 1024;

        private static readonly object Gate = new object();
        private static readonly List<string> Ring = new List<string>(RingSize + 1);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private static UnityModManager.ModEntry _mod;
        private static long _written;

        /// <summary>模组目录（bridge.log 所在目录），可能为 null。</summary>
        public static string ModDir { get; private set; }

        public static string FilePath
        {
            get { return ModDir == null ? null : Path.Combine(ModDir, "bridge.log"); }
        }

        public static void Init(UnityModManager.ModEntry mod, string directory)
        {
            _mod = mod;
            ModDir = directory;
            try
            {
                if (ModDir != null)
                {
                    System.IO.Directory.CreateDirectory(ModDir);
                    var fi = new FileInfo(FilePath);
                    _written = fi.Exists ? fi.Length : 0;
                }
            }
            catch
            {
                ModDir = null;
            }
        }

        public static void Info(string message) { Add("INFO", message); }
        public static void Warn(string message) { Add("WARN", message); }
        public static void Error(string message) { Add("ERROR", message); }

        public static void Error(string message, Exception ex)
        {
            Add("ERROR", message + " :: " + Describe(ex));
        }

        public static void Warn(string message, Exception ex)
        {
            Add("WARN", message + " :: " + Describe(ex));
        }

        private static string Describe(Exception ex)
        {
            if (ex == null) return "(null)";
            try
            {
                return ex.GetType().Name + ": " + ex.Message +
                       (string.IsNullOrEmpty(ex.StackTrace) ? string.Empty : " @ " + ex.StackTrace);
            }
            catch
            {
                return "(unprintable exception)";
            }
        }

        private static void Add(string level, string message)
        {
            string line;
            try
            {
                line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                       " [" + level + "] [pta-bridge] " + (message ?? string.Empty);
            }
            catch
            {
                return;
            }

            lock (Gate)
            {
                try
                {
                    Ring.Add(line);
                    if (Ring.Count > RingSize) Ring.RemoveRange(0, Ring.Count - RingSize);
                }
                catch
                {
                    // 内存日志失败也无所谓
                }

                try
                {
                    if (_mod != null) _mod.Logger.Log(message ?? string.Empty);
                }
                catch
                {
                    // UMM Logger 不可用时忽略
                }

                try
                {
                    string path = FilePath;
                    if (path != null)
                    {
                        if (_written > RotateBytes)
                        {
                            try
                            {
                                string old = path + ".1";
                                if (File.Exists(old)) File.Delete(old);
                                File.Move(path, old);
                            }
                            catch
                            {
                                // 轮转失败就继续往同一个文件追加
                            }
                            _written = 0;
                        }

                        File.AppendAllText(path, line + Environment.NewLine, Utf8);
                        _written += line.Length + Environment.NewLine.Length;
                    }
                }
                catch
                {
                    // 磁盘写失败不能影响游戏
                }
            }
        }

        /// <summary>最近 n 行（旧 → 新）。</summary>
        public static string[] Tail(int n)
        {
            lock (Gate)
            {
                if (n <= 0) return new string[0];
                int start = Math.Max(0, Ring.Count - n);
                var result = new string[Ring.Count - start];
                Ring.CopyTo(start, result, 0, result.Length);
                return result;
            }
        }
    }
}
