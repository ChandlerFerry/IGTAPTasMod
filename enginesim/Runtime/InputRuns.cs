using System;
using System.Collections;
using System.Collections.Generic;

namespace IGTAP.EngineSim
{
    /// <summary>
    /// An immutable input sequence stored as runs of equal ticks: what the explore archive's cells and a saved archive's
    /// sources hold. A <see cref="List{InputTick}"/> is 68 bytes a tick; a route runs ~25 equal ticks at a time, so this is
    /// ~3 bytes a tick (Area 2's routes are 1,000-2,000 ticks, and every explore run copies its cell's prefix).
    /// Adjacent runs never hold equal ticks. <see cref="Take"/> shares the arrays.
    /// </summary>
    public sealed class InputRuns : IReadOnlyList<InputTick>
    {
        public static readonly InputRuns Empty = new InputRuns(Array.Empty<InputTick>(), Array.Empty<int>(), 0, 0);

        readonly InputTick[] _ticks;
        // Each run's end (exclusive): cumulative tick counts.
        readonly int[] _ends;
        readonly int _runs;

        public int Count { get; }

        /// <summary>The explore archive's cells on these inputs (OfflineSolver counts them into its archive's bytes while
        /// one is there). The one mutable field: the sequence itself never changes.</summary>
        internal int Cells;

        /// <summary>What it holds in memory: this object and its two arrays, headers included.</summary>
        public long Bytes => 48 + 2 * 24 + _ticks.Length * (long)System.Runtime.CompilerServices.Unsafe.SizeOf<InputTick>() + _ends.Length * 4L;

        InputRuns(InputTick[] ticks, int[] ends, int runs, int count)
        {
            _ticks = ticks;
            _ends = ends;
            _runs = runs;
            Count = count;
        }

        /// <summary>The list's first <paramref name="count"/> ticks (all of them by default).</summary>
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

        /// <summary>The first <paramref name="count"/> ticks, sharing this one's arrays.</summary>
        public InputRuns Take(int count)
        {
            if (count >= Count) return this;
            if (count <= 0) return Empty;
            int runs = RunAt(count - 1) + 1;
            return new InputRuns(_ticks, _ends, runs, count);
        }

        /// <summary>The first <paramref name="count"/> ticks (all of them by default) as a list of exactly that capacity.</summary>
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

        /// <summary>The runs from tick <paramref name="from"/> on (the first cut to start there).</summary>
        public IEnumerable<(InputTick tick, int count)> Runs(int from = 0)
        {
            if (from >= Count) yield break;
            int r = RunAt(Math.Max(0, from));
            yield return (_ticks[r], Math.Min(_ends[r], Count) - Math.Max(0, from));
            for (r++; r < _runs; r++) yield return (_ticks[r], Math.Min(_ends[r], Count) - _ends[r - 1]);
        }

        /// <summary>How many leading ticks the two share.</summary>
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
            // An exact hit is a run's end: the tick starts the next run.
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

        /// <summary>Builds one run by run, merging a run into the last when their ticks are equal.</summary>
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
