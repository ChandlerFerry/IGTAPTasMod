using System;
using System.Collections;
using System.Collections.Generic;

namespace IGTAP.EngineSim
{
    public sealed class InputRuns : IReadOnlyList<InputTick>
    {
        public static readonly InputRuns Empty = new InputRuns(Array.Empty<InputTick>(), Array.Empty<int>(), 0, 0);

        readonly InputTick[] _ticks;
        readonly int[] _ends;
        readonly int _runs;

        public int Count { get; }

        internal int Cells;

        public long Bytes => 48 + 2 * 24 + _ticks.Length * (long)System.Runtime.CompilerServices.Unsafe.SizeOf<InputTick>() + _ends.Length * 4L;

        InputRuns(InputTick[] ticks, int[] ends, int runs, int count)
        {
            _ticks = ticks;
            _ends = ends;
            _runs = runs;
            Count = count;
        }

        public static InputRuns From(List<InputTick> inputs, int count = -1)
        {
            if (count < 0 || count > inputs.Count) count = inputs.Count;
            int runs = 0;
            for (int i = 0; i < count; i++) if (i == 0 || !inputs[i].Equals(inputs[i - 1])) runs++;
            var ticks = new InputTick[runs];
            var ends = new int[runs];
            int r = -1;
            for (int i = 0; i < count; i++)
            {
                if (i == 0 || !inputs[i].Equals(inputs[i - 1])) ticks[++r] = inputs[i];
                ends[r] = i + 1;
            }
            return new InputRuns(ticks, ends, runs, count);
        }

        public InputRuns Take(int count)
        {
            if (count >= Count) return this;
            if (count <= 0) return Empty;
            int runs = RunAt(count - 1) + 1;
            return new InputRuns(_ticks, _ends, runs, count);
        }

        public List<InputTick> ToList(int count = -1)
        {
            if (count < 0 || count > Count) count = Count;
            var list = new List<InputTick>(count);
            for (int r = 0, at = 0; at < count; r++)
            {
                int end = Math.Min(_ends[r], count);
                for (; at < end; at++) list.Add(_ticks[r]);
            }
            return list;
        }

        public IEnumerable<(InputTick tick, int count)> Runs(int from = 0)
        {
            if (from >= Count) yield break;
            int r = RunAt(Math.Max(0, from));
            yield return (_ticks[r], Math.Min(_ends[r], Count) - Math.Max(0, from));
            for (r++; r < _runs; r++) yield return (_ticks[r], Math.Min(_ends[r], Count) - _ends[r - 1]);
        }

        public static int SharedPrefix(InputRuns a, InputRuns b)
        {
            int ra = 0, rb = 0, at = 0, count = Math.Min(a.Count, b.Count);
            while (at < count)
            {
                if (!a._ticks[ra].Equals(b._ticks[rb])) return at;
                int end = Math.Min(Math.Min(a._ends[ra], b._ends[rb]), count);
                at = end;
                if (a._ends[ra] == end) ra++;
                if (b._ends[rb] == end) rb++;
            }
            return count;
        }

        public InputTick this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                return _ticks[RunAt(index)];
            }
        }

        int RunAt(int index)
        {
            int at = Array.BinarySearch(_ends, 0, _runs, index);
            return at >= 0 ? at + 1 : ~at;
        }

        public IEnumerator<InputTick> GetEnumerator()
        {
            for (int r = 0, at = 0; at < Count; r++)
            {
                int end = Math.Min(_ends[r], Count);
                for (; at < end; at++) yield return _ticks[r];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public sealed class Builder
        {
            readonly List<InputTick> _ticks = new List<InputTick>();
            readonly List<int> _ends = new List<int>();
            int _count;

            public Builder Add(InputTick tick, int count)
            {
                if (count <= 0) return this;
                _count += count;
                if (_ticks.Count > 0 && _ticks[_ticks.Count - 1].Equals(tick)) _ends[_ends.Count - 1] = _count;
                else { _ticks.Add(tick); _ends.Add(_count); }
                return this;
            }

            public Builder AddRange(InputRuns runs, int count)
            {
                foreach (var (tick, n) in runs.Take(count).Runs()) Add(tick, n);
                return this;
            }

            public InputRuns Build() => _count == 0 ? Empty : new InputRuns(_ticks.ToArray(), _ends.ToArray(), _ticks.Count, _count);
        }
    }
}
