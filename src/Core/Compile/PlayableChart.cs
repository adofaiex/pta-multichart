using System;
using System.Collections.Generic;
using PtaMultichart.Core.Model;

namespace PtaMultichart.Core.Compile
{
    /// <summary>逻辑砖号 ⇄ 播放谱砖号。被切开的砖映射到它第一段；插入的接头砖映射回它前面那块逻辑砖。</summary>
    public sealed class FloorMap
    {
        private readonly int[] _toPlayable;
        private readonly int[] _toLogical;
        private readonly bool[] _inserted;

        public FloorMap(int[] toPlayable, int[] toLogical, bool[] inserted)
        {
            _toPlayable = toPlayable ?? throw new ArgumentNullException(nameof(toPlayable));
            _toLogical = toLogical ?? throw new ArgumentNullException(nameof(toLogical));
            _inserted = inserted ?? throw new ArgumentNullException(nameof(inserted));
        }

        public int LogicalCount => _toPlayable.Length;
        public int Count => _toLogical.Length;

        public int ToPlayable(int logicalSeq)
        {
            if (logicalSeq < 0) return logicalSeq;
            if (logicalSeq >= _toPlayable.Length) return _toPlayable.Length == 0 ? logicalSeq : _toPlayable[_toPlayable.Length - 1];
            return _toPlayable[logicalSeq];
        }

        public int ToLogical(int playableSeq)
        {
            if (playableSeq < 0) return playableSeq;
            if (playableSeq >= _toLogical.Length) return _toLogical.Length == 0 ? playableSeq : _toLogical[_toLogical.Length - 1];
            return _toLogical[playableSeq];
        }

        public bool IsInserted(int playableSeq)
        {
            return playableSeq >= 0 && playableSeq < _inserted.Length && _inserted[playableSeq];
        }

        public int InsertedCount
        {
            get
            {
                int n = 0;
                foreach (var b in _inserted) if (b) n++;
                return n;
            }
        }
    }

    /// <summary>编译时需要的一处副轨点击记录（测试用）。</summary>
    public sealed class InsertedClick
    {
        public int SubTrackId;
        /// <summary>副轨上的逻辑砖号。</summary>
        public int SubSeq;
        /// <summary>播放谱里承载这一拍的砖号。</summary>
        public int PlayableSeq;
        /// <summary>这一拍的歌曲时间（秒，相对主轨第 1 块砖）。</summary>
        public double SongTime;
    }

    /// <summary>编译结果：一张纯数据的播放谱。</summary>
    public sealed class PlayableChart
    {
        public List<float> AngleData = new List<float>();
        public List<ChartEvent> Actions = new List<ChartEvent>();
        public List<ChartEvent> Decorations = new List<ChartEvent>();
        public FloorMap Map;
        public List<InsertedClick> InsertedClicks = new List<InsertedClick>();
        /// <summary>编译预检（方案 v2 §8 的 6 条）。不是失败原因，全是信息/警告。</summary>
        public PreflightReport Preflight;

        public int FloorCount => AngleData.Count + 1;
    }

    /// <summary>方案里判定「不能编译」的情况：用 Code 指明类别，Floors 指出涉及的砖。</summary>
    public sealed class CompileError : Exception
    {
        public string Code { get; }
        public int[] Floors { get; }

        public CompileError(string code, string message, params int[] floors) : base(message)
        {
            Code = code;
            Floors = floors ?? Array.Empty<int>();
        }
    }
}
