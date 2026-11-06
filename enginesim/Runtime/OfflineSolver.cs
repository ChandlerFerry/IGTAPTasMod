using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using IGTAPTasMod;
using UnityEngine;
using static IGTAP.EngineSim.ContactRays;

namespace IGTAP.EngineSim
{
    public sealed class OfflineSolver
    {
        public sealed class Best
        {
            public float Time = float.PositiveInfinity;
            public long RunTicks = long.MaxValue;
            public List<InputTick> Inputs;
            public int Version;
        }

        public sealed class Memory
        {
            public long Runs;
            public List<Source> Sources = new List<Source>();
            public List<List<InputTick>> Plateau = new List<List<InputTick>>();

            public sealed class Source
            {
                public InputRuns Inputs;
                public List<SavedCell> Cells = new List<SavedCell>();
            }

            public struct SavedCell
            {
                public long Key;
                public int Length, Picks;
                public float Rank;
            }
        }

        public sealed class ForcedPath
        {
            public readonly List<Box> Via = new List<Box>(), Avoid = new List<Box>();
            public readonly List<Box> Discourage = new List<Box>();
            public bool Guide;
            public bool IsEmpty => Via.Count == 0 && Avoid.Count == 0;
            public bool OnCourseOnly;

            public bool Step(WorldPos prev, WorldPos p, int progress, ref int waypoints, bool timing = true)
            {
                if (waypoints < Via.Count && Via[waypoints].Contains(p)) waypoints++;
                if (OnCourseOnly && !timing) return false;
                foreach (Box box in Avoid) if (box.Cuts(prev, p) && box.Live(progress)) return true;
                return false;
            }
        }

        public readonly struct Box
        {
            public readonly float X0, Y0, X1, Y1;
            public readonly int After;
            public readonly int From;
            public readonly int Until;
            public Box(WorldPos a, WorldPos b, int after = 0, int from = 0, int until = 0) { X0 = Math.Min(a.x, b.x); Y0 = Math.Min(a.y, b.y); X1 = Math.Max(a.x, b.x); Y1 = Math.Max(a.y, b.y); After = after; From = from; Until = until; }
            public bool Live(int progress) { return (After == 0 || progress >= After) && (Until == 0 || progress < Until); }
            public bool Contains(WorldPos p) { return p.x >= X0 && p.x <= X1 && p.y >= Y0 && p.y <= Y1; }

            public bool Cuts(WorldPos prev, WorldPos p)
            {
                return From == 0 ? Contains(p) : FullTasGoal.CrossesInto(From, X0, Y0, X1, Y1, prev.x, prev.y, p.x, p.y);
            }

            public override string ToString()
            {
                int from = From;
                return $"{X0:0},{Y0:0}..{X1:0},{Y1:0}" + (After > 0 ? " after goal " + After : "") + (Until > 0 ? " until goal " + Until : "")
                    + (from != 0 ? " from " + string.Join("+", FullTasGoal.SideNames.Where((s, i) => (from & 1 << i) != 0)) : "");
            }
        }

        public readonly string Capabilities;
        readonly Func<Simulation> _createSimulation;
        readonly List<List<InputTick>> _seeds;
        readonly bool _fresh;
        readonly long _priorRuns;
        readonly List<List<InputTick>> _plateaus;
        List<Memory.Source> _loaded;
        readonly ForcedPath _path;
        public readonly Best Shared = new Best();
        public long Runs, Ticks, Finishes, Drifts, Splices, BranchChecks, BranchMatches, StateHits, StateReplays, ReplayTicks;
        public long TechGrafted, TechGraftedAtStep;
        public long ChainGrafts, ExpectChecks, ExpectsMet;
        public readonly TechGrafts Mined;
        public int MostWaypoints;
        public event Action<float, long, List<InputTick>, string> Improved;
        volatile bool _stop;
        volatile int _exportGeneration;
        Worker[] _workers;
        List<Memory.Source>[] _partitions;
        readonly System.Collections.Concurrent.ConcurrentDictionary<long, Cell> _archive = new System.Collections.Concurrent.ConcurrentDictionary<long, Cell>();
        readonly object _extent = new object();
        float _maxTimedRank;
        int _minProgress = int.MaxValue, _maxProgress = -1;
        float[] _near;
        long _stateBudget;
        public int StatesPerWorker { get; private set; }
        int _cells;
        long _archiveBytes, _archiveBudget, _cellBytes;
        int _evictions;
        public long EvictedCells;
        readonly object _evicting = new object();
        public SnapshotIndex Resume;
        public bool NoResets;
        public long ArchiveBudget;
        public long StateBudget;
        public long TickLimit, StallTicks;
        long _improvedAt;
        internal bool Spent => TickLimit > 0 && (Interlocked.Read(ref Ticks) >= TickLimit
            || StallTicks > 0 && Volatile.Read(ref Shared.Version) > 0 && Interlocked.Read(ref Ticks) - Interlocked.Read(ref _improvedAt) >= StallTicks);
        public long GoalResumes;
        public int MinedDraws = 1;
        public double GraftRate = .5;
        public (int lo, int hi)? GraftFocus;
        public Func<Simulation, Func<List<InputTick>, RunResult, double>> Penalty;
        public Func<List<InputTick>, bool> Accept;
        public Action<List<InputTick>, float> FinishedRun;
        public float LosingSlack;
        readonly System.Collections.Concurrent.ConcurrentDictionary<List<InputTick>, long> _penaltyTicks
            = new System.Collections.Concurrent.ConcurrentDictionary<List<InputTick>, long>(ReferenceEqualityComparer.Instance);

        public long PenaltyTicks(List<InputTick> inputs) => _penaltyTicks.TryGetValue(inputs, out long ticks) ? ticks : 0;

        readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _ends = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

        static string EndKey(string why) => why == null ? "inputs out"
            : System.Text.RegularExpressions.Regex.Replace(why.Length > 70 ? why.Substring(0, 70) : why, @"-?\d[\d.,]*", "#");

        public string ArchiveStatus()
        {
            long hits = Interlocked.Read(ref StateHits), replays = Interlocked.Read(ref StateReplays), goals = Interlocked.Read(ref GoalResumes);
            return $"archive {_cells} cells ({Interlocked.Read(ref _archiveBytes) >> 20} MB" + (_evictions == 0 ? "" : $", {Interlocked.Read(ref EvictedCells)} evicted") + "), "
                + $"states {(hits + replays == 0 ? 100 : 100 * hits / (hits + replays))}% cached ({StatesPerWorker} per worker), "
                + $"{Interlocked.Read(ref ReplayTicks)} replay ticks" + (goals == 0 ? "" : $", {goals} from stored goal states")
                + "; ends: " + string.Join("; ", _ends.OrderByDescending(e => e.Value).Take(3).Select(e => $"{e.Value} {e.Key}"));
        }

        public const double DiscouragedWeight = .02;

        public readonly float GuideWeight;
        public readonly int Rng;
        float _guideBest = float.PositiveInfinity;

        public string GuideStatus()
        {
            return !DistanceField.Enabled || GuideWeight <= 0f ? "guide off"
                : float.IsFinite(_guideBest) ? $"guide d={_guideBest:0}" : "guide d=-";
        }

        public OfflineSolver(Func<Simulation> createSimulation, string capabilities, List<List<InputTick>> seeds, Memory memory = null, ForcedPath path = null,
            float visitCell = 0f, TechGrafts mined = null, float guide = 1f, int rng = 0)
        {
            _createSimulation = createSimulation;
            Capabilities = capabilities;
            _seeds = seeds;
            _fresh = seeds.Count == 0;
            _priorRuns = memory?.Runs ?? 0;
            _plateaus = memory?.Plateau ?? new List<List<InputTick>>();
            _loaded = memory?.Sources;
            _path = path != null && (!path.IsEmpty || path.Discourage.Count > 0) ? path : null;
            VisitCell = visitCell;
            GuideWeight = guide;
            Rng = rng;
            Mined = mined;
            Mined?.Prepare(capabilities);
        }

        public void Stop() { _stop = true; }

        public void Offer(List<InputTick> inputs) { _offered.Enqueue(inputs); }
        readonly System.Collections.Concurrent.ConcurrentQueue<List<InputTick>> _offered = new System.Collections.Concurrent.ConcurrentQueue<List<InputTick>>();

        public Thread[] Start(int workers)
        {
            var threads = new Thread[workers];
            _workers = new Worker[workers];
            long available = AvailableMemory();
            _stateBudget = StateBudget > 0 ? StateBudget : available / 2 / workers;
            StatesPerWorker = (int)Math.Max(512, _stateBudget / Worker.StartStateBytes);
            _archiveBudget = ArchiveBudget > 0 ? ArchiveBudget : available / 4;
            _cellBytes = CellBytes(workers);
            _partitions = Enumerable.Range(0, workers).Select(i => _loaded == null ? new List<Memory.Source>()
                : _loaded.Where((s, k) => k % workers == i).ToList()).ToArray();
            _loaded = null;
            for (int i = 0; i < workers; i++)
            {
                int index = i;
                int seed = Rng != 0 ? Rng + i : Environment.TickCount * 31 + i * 7919;
                var seeds = i == 0 ? _seeds : new List<List<InputTick>>();
                List<InputTick> plateau = _plateaus.Count > 0 ? _plateaus[i % _plateaus.Count] : null;
                threads[i] = new Thread(() =>
                {
                    var worker = new Worker(this, index, _createSimulation(), seed, seeds, _partitions[index], plateau);
                    _workers[index] = worker;
                    worker.Loop();
                }) { IsBackground = true, Name = "solver-" + i };
                threads[i].Start();
            }
            return threads;
        }

        static long AvailableMemory()
        {
            long limit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, ram = PhysicalMemory();
            return ram > 0 ? Math.Min(limit, ram) : limit;
        }

        static long PhysicalMemory()
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status) != 0 ? (long)status.TotalPhys : 0;
            }
            if (OperatingSystem.IsLinux() && System.IO.File.Exists("/proc/meminfo"))
                foreach (string line in System.IO.File.ReadLines("/proc/meminfo"))
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts && long.TryParse(parts[1], out long kb) ? kb * 1024 : 0;
            return 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        static extern int GlobalMemoryStatusEx(ref MemoryStatusEx status);

        public Memory Export(int timeoutMs)
        {
            int generation = ++_exportGeneration;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs && _workers.Any(w => w == null || w.Exported < generation)) Thread.Sleep(10);
            var cells = new Dictionary<long, (InputRuns source, int length, int picks, float rank)>();
            void Add(long key, InputRuns source, int length, int picks, float rank)
            {
                if (!cells.TryGetValue(key, out var c)) { cells[key] = (source, length, picks, rank); return; }
                bool better = rank < c.rank || (rank == c.rank && length < c.length);
                cells[key] = better ? (source, length, c.picks + picks, rank) : (c.source, c.length, c.picks + picks, c.rank);
            }
            var memory = new Memory { Runs = _priorRuns + Interlocked.Read(ref Runs) };
            foreach (var pair in _archive) Add(pair.Key, pair.Value.Source, pair.Value.Length, pair.Value.Picks, pair.Value.Rank);
            for (int i = 0; i < _workers.Length; i++)
            {
                Worker.Handover h = _workers[i]?.LastHandover;
                foreach (Memory.Source s in h?.Pending ?? _partitions[i])
                    foreach (Memory.SavedCell c in s.Cells) Add(c.Key, s.Inputs, c.Length, c.Picks, c.Rank);
                if (h?.Plateau != null && !memory.Plateau.Any(p => p.SequenceEqual(h.Plateau))) memory.Plateau.Add(h.Plateau);
            }
            var sources = new Dictionary<InputRuns, Memory.Source>(ReferenceEqualityComparer.Instance);
            foreach (var pair in cells)
            {
                if (!sources.TryGetValue(pair.Value.source, out Memory.Source s)) sources[pair.Value.source] = s = new Memory.Source { Inputs = pair.Value.source };
                s.Cells.Add(new Memory.SavedCell { Key = pair.Key, Length = pair.Value.length, Picks = pair.Value.picks, Rank = pair.Value.rank });
            }
            foreach (Memory.Source s in sources.Values)
                s.Inputs = s.Inputs.Take(Math.Min(s.Inputs.Count, s.Cells.Max(c => c.Length) + 1));
            memory.Sources = sources.Values.ToList();
            return memory;
        }

        public readonly float VisitCell;

        public static long VisitKey(WorldPos world, float cell)
        {
            return ((long)Mathf.FloorToInt(world.x / cell) << 32) | (uint)Mathf.FloorToInt(world.y / cell);
        }

        internal static long GoalKey(Simulation sim, colouredBlockSwapper swapper)
        {
            float behind = float.NaN;
            return Worker.GoalCellKey(Worker.Place(sim, swapper, true, ref behind, false), sim.Progress, sim.Goals.Armed);
        }

        // Joins keep every press a real press (RULES.md 3): false if the seam needs the previous input to release a press.
        internal static bool Seam(List<InputTick> t, int at, int floor)
        {
            InputTick k = t[at], before = t[at - 1];
            if (k.held && !before.held && before.dashJump == 0) k.press = true;
            if (!k.held) k.release = before.held;
            if (k.press && before.held)
            {
                if (before.press || at - 1 <= floor) return false;
                before.held = false; before.release = true; t[at - 1] = before;
            }
            t[at] = k;
            return true;
        }

        public Dictionary<long, float> Visits()
        {
            var all = new Dictionary<long, float>();
            foreach (Worker w in _workers ?? Array.Empty<Worker>())
                if ((_stop ? w?.Visits : w?.LastHandover?.Visits) is Dictionary<long, float> visits)
                    foreach (var v in visits)
                        if (!all.TryGetValue(v.Key, out float t) || v.Value < t) all[v.Key] = v.Value;
            return all;
        }

        public static List<InputTick> DropIdlePauses(List<InputTick> inputs, Func<List<InputTick>, bool> kept)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].pause <= 0) continue;
                var without = new List<InputTick>(inputs);
                InputTick k = without[i];
                k.pause = k.pauseTick = k.pauseFrame = 0;
                without[i] = k;
                if (kept(without)) { inputs = without; continue; }
                for (int frames = 1; frames < inputs[i].pause; frames *= 2)
                {
                    var shorter = new List<InputTick>(inputs);
                    InputTick s = shorter[i];
                    s.pause = frames;
                    shorter[i] = s;
                    if (kept(shorter)) { inputs = shorter; break; }
                }
            }
            return inputs;
        }

        public static bool Beats(float time, long runTicks, float bestTime, long bestRunTicks)
        {
            return time < bestTime - .0001f || (time <= bestTime + .0001f && runTicks < bestRunTicks);
        }

        public bool Farm;

        bool Ranks(float time, long runTicks, float bestTime, long bestRunTicks)
        {
            return Farm ? runTicks < bestRunTicks || (runTicks == bestRunTicks && time < bestTime - .0001f) : Beats(time, runTicks, bestTime, bestRunTicks);
        }

        readonly System.Collections.Concurrent.ConcurrentQueue<(float time, long runTicks, List<InputTick> inputs, string how)> _reports
            = new System.Collections.Concurrent.ConcurrentQueue<(float, long, List<InputTick>, string)>();
        readonly object _reporting = new object();

        bool Offer(float time, long runTicks, List<InputTick> inputs, string how, long penaltyTicks)
        {
            lock (Shared)
            {
                if (!Ranks(time, runTicks, Shared.Time, Shared.RunTicks)) return false;
                Shared.Time = time;
                Shared.RunTicks = runTicks;
                Shared.Inputs = inputs;
                Shared.Version++;
                Interlocked.Exchange(ref _improvedAt, Interlocked.Read(ref Ticks));
                if (Penalty != null) _penaltyTicks[inputs] = penaltyTicks;
                _reports.Enqueue((time, runTicks, inputs, how));
            }
            Report();
            return true;
        }

        // Improved handlers run outside Shared's lock, one at a time, in the order the bests were taken.
        void Report()
        {
            while (!_reports.IsEmpty && Monitor.TryEnter(_reporting))
                try
                {
                    while (_reports.TryDequeue(out var r)) Improved?.Invoke(r.time, r.runTicks, r.inputs, r.how);
                }
                finally { Monitor.Exit(_reporting); }
        }

        sealed class Cell
        {
            public InputRuns Source;
            public int Length, Picks, Waypoints;
            public bool Discouraged;
            public Cell Parent;
            public float Rank, Distance;
            public long Key;
            // Written by any worker, read racily; a stale read only puts a draw in the other pool.
            public int Owner;
            public volatile bool Removed;
        }

        static long CellBytes(int workers) { return 80 + 56 + 8 + workers * 4 * 12; }

        void Count(Cell cell, int sign)
        {
            long bytes = _cellBytes;
            int filed = Interlocked.Add(ref cell.Source.Cells, sign);
            if (sign > 0 ? filed == 1 : filed == 0) bytes += cell.Source.Bytes;
            Interlocked.Add(ref _archiveBytes, sign * bytes);
        }

        bool ArchiveFull => Volatile.Read(ref _archiveBytes) > _archiveBudget;

        void Evict(Func<long, Cell, double> weight)
        {
            if (!Monitor.TryEnter(_evicting)) return;
            try
            {
                if (!ArchiveFull) return;
                var cells = new List<(double weight, long key, Cell cell)>(_cells + 64);
                foreach (var pair in _archive) cells.Add((weight(pair.Key, pair.Value), pair.Key, pair.Value));
                cells.Sort((a, b) => a.weight.CompareTo(b.weight));
                long target = _archiveBudget / 8 * 7;
                int removed = 0;
                foreach (var (_, key, cell) in cells)
                {
                    if (Volatile.Read(ref _archiveBytes) <= target) break;
                    if (!_archive.TryRemove(new KeyValuePair<long, Cell>(key, cell))) continue;
                    cell.Removed = true;
                    Count(cell, -1);
                    Interlocked.Decrement(ref _cells);
                    removed++;
                }
                Interlocked.Add(ref EvictedCells, removed);
                foreach (var pair in _archive)
                {
                    Cell parent = pair.Value.Parent;
                    while (parent != null && parent.Removed) parent = parent.Parent;
                    pair.Value.Parent = parent;
                }
                Interlocked.Increment(ref _evictions);
            }
            finally { Monitor.Exit(_evicting); }
        }

        static bool Earlier(float rank, int length, Cell than) { return than.Rank > rank || (than.Rank == rank && than.Length > length); }

        bool File(long key, Cell cell)
        {
            while (true)
            {
                if (!_archive.TryGetValue(key, out Cell old))
                {
                    if (!_archive.TryAdd(key, cell)) continue;
                    Interlocked.Increment(ref _cells);
                    Count(cell, 1);
                    return true;
                }
                if (!Earlier(cell.Rank, cell.Length, old)) return false;
                if (!_archive.TryUpdate(key, cell, old)) continue;
                old.Removed = true;
                Count(cell, 1);
                Count(old, -1);
                return true;
            }
        }

        void Extend(long key, Cell cell, int progress)
        {
            float rank = cell.Rank, distance = cell.Distance;
            if (distance < _guideBest) _guideBest = distance;
            if (key < 0 && rank > _maxTimedRank) lock (_extent) _maxTimedRank = Math.Max(_maxTimedRank, rank);
            if (progress < 0) return;
            if (progress < _minProgress || progress > _maxProgress || distance < _near[progress] || Closer(cell, progress, _closest))
                lock (_extent)
                {
                    _minProgress = Math.Min(_minProgress, progress);
                    _maxProgress = Math.Max(_maxProgress, progress);
                    if (distance < _near[progress]) _near[progress] = distance;
                    if (Closer(cell, progress, _closest)) _closest = new ClosestCell(cell, progress);
                }
        }

        public static float GoalDistance(Simulation sim)
        {
            FullTasGoal goal = sim.Goals.Current;
            if (goal == null) return float.NaN;
            CapturePos p = sim.PlayerAtCapture;
            if (goal.kind == FullTasGoal.Buy || goal.kind == FullTasGoal.Reward) return DistanceField.Enabled ? DistanceField.For(sim).Distance(p) : float.NaN;
            if (goal.kind != FullTasGoal.At) return float.NaN;
            if (DistanceField.Enabled) return DistanceField.For(sim).Distance(p);
            float dx = Math.Max(0f, Math.Abs(p.x - goal.x) - goal.w / 2f), dy = Math.Max(0f, Math.Abs(p.y - goal.y) - goal.h / 2f);
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        sealed record ClosestCell(Cell Cell, int Progress);
        volatile ClosestCell _closest;

        static bool Closer(Cell cell, int progress, ClosestCell than)
        {
            if (than == null || progress != than.Progress) return than == null || progress > than.Progress;
            float d = float.IsNaN(cell.Distance) ? float.PositiveInfinity : cell.Distance;
            float old = float.IsNaN(than.Cell.Distance) ? float.PositiveInfinity : than.Cell.Distance;
            return d < old || d == old && cell.Rank < than.Cell.Rank;
        }

        public sealed class Attempt
        {
            public int Progress;
            public float Distance, Rank;
            public List<InputTick> Inputs;
        }

        public Attempt Closest()
        {
            ClosestCell c = _closest;
            return c == null ? null : new Attempt { Progress = c.Progress, Distance = c.Cell.Distance, Rank = c.Cell.Rank,
                Inputs = c.Cell.Source.ToList(c.Cell.Length) };
        }

        public List<List<InputTick>> Near(Vector2 at, Func<int, bool> progress, float dx, float dy, HashSet<long> skip, int max)
        {
            int cx = Mathf.FloorToInt(at.x / Worker.CellSize), cy = Mathf.FloorToInt(at.y / Worker.CellSize);
            int sx = Mathf.CeilToInt(dx / Worker.CellSize), sy = Mathf.CeilToInt(dy / Worker.CellSize);
            var near = new List<(int d, long key, Cell cell)>();
            foreach (var pair in _archive)
            {
                if (pair.Key >= 0 || skip.Contains(pair.Key) || !progress(Worker.GoalProgress(pair.Key))) continue;
                long place = -1 - pair.Key;
                // Place keeps 16 bits of each square index: compare them wrapped.
                int x = (short)(((place >> 16) - cx) & 0xFFFF), y = (short)((place - cy) & 0xFFFF);
                if (Math.Abs(x) <= sx && Math.Abs(y) <= sy) near.Add((Math.Abs(x) + Math.Abs(y), pair.Key, pair.Value));
            }
            return near.OrderBy(n => n.d).Take(max).Select(n =>
            {
                skip.Add(n.key);
                return n.cell.Source.ToList(n.cell.Length);
            }).ToList();
        }

        sealed class Worker
        {
            internal const float CellSize = 48f;
            const float SegmentSlack = .2f;
            const int StateEvery = 16;
            const int CachedDraws = 3;
            const int BranchCheckEvery = 10000;
            readonly OfflineSolver _owner;
            readonly int _index;
            readonly Simulation _sim;
            readonly SnapshotStore _store;
            readonly colouredBlockSwapper _swapper;
            readonly System.Random _rng;
            readonly Queue<List<InputTick>> _seeds;
            readonly System.Collections.Concurrent.ConcurrentDictionary<long, Cell> _archive;
            readonly Dictionary<Cell, Entry> _states = new Dictionary<Cell, Entry>();
            readonly PriorityQueue<Entry, (double weight, long use)> _evict = new PriorityQueue<Entry, (double, long)>();
            long _use;
            int _cap;

            sealed class Entry
            {
                public Cell Cell;
                public SimSnapshot State;
                public long Use;
                public double Weight;
            }
            Cell[] _pool = Array.Empty<Cell>(), _ownPool = Array.Empty<Cell>();
            double[] _poolWeights = Array.Empty<double>(), _ownWeights = Array.Empty<double>();
            int _poolCount, _ownCount, _poolPicks;
            int _poolEvictions;
            readonly Func<long, Cell, double> _evictWeight;
            double _ownShare;
            readonly bool _dash, _omni;
            float _best = float.PositiveInfinity;
            long _bestRunTicks = long.MaxValue;
            List<InputTick> _bestInputs;
            Replay _bestRun;
            int _sharedVersion;

            sealed class Replay
            {
                public readonly SimSnapshot[] States;
                public readonly (float, float, float, float, float, float)[] Body;
                public readonly int?[] Fingerprint;
                public readonly int[] Waypoints;
                public readonly long[] Keys;
                public readonly float[] Elapsed;
                public readonly bool[] Dash;
                public readonly Dictionary<long, float> Arrival = new Dictionary<long, float>();
                public readonly Dictionary<long, (float time, int tick)> First = new Dictionary<long, (float, int)>();

                public Replay(int inputs)
                {
                    States = new SimSnapshot[inputs];
                    Body = new (float, float, float, float, float, float)[inputs + 1];
                    Fingerprint = new int?[inputs + 1];
                    Waypoints = new int[inputs + 1];
                    Keys = new long[inputs + 1];
                    Elapsed = new float[inputs + 1];
                    Dash = new bool[inputs + 1];
                    Array.Fill(Elapsed, float.NaN);
                }

                public void CopyPrefix(Replay best, int through)
                {
                    int n = through + 1;
                    Array.Copy(best.States, States, n);
                    Array.Copy(best.Body, Body, n);
                    Array.Copy(best.Fingerprint, Fingerprint, n);
                    Array.Copy(best.Waypoints, Waypoints, n);
                    Array.Copy(best.Keys, Keys, n);
                    Array.Copy(best.Elapsed, Elapsed, n);
                    Array.Copy(best.Dash, Dash, n);
                    for (int t = 0; t < n; t++) Record(t, Keys[t], Elapsed[t]);
                }

                public void Record(int tick, long key, float elapsed)
                {
                    if (!(elapsed >= 0f)) return;
                    Arrival[key] = elapsed;
                    if (tick < States.Length) First.TryAdd(key, (elapsed, tick));
                }
            }

            void Adopt(List<InputTick> inputs, float time, int finishTick, long runTicks, Replay replay)
            {
                _best = time;
                _bestRunTicks = runTicks;
                _bestInputs = inputs.GetRange(0, Math.Min(finishTick + 1, inputs.Count));
                _bestRun = replay;
                _spliceScan = true;
            }
            readonly bool _goalMode;
            readonly DistanceField _guide;
            readonly Queue<Memory.Source> _restore;
            List<InputTick> _plateau;

            public sealed class Handover
            {
                public List<Memory.Source> Pending;
                public List<InputTick> Plateau;
                public Dictionary<long, float> Visits;
            }
            public volatile Handover LastHandover;
            public volatile int Exported;

            public Worker(OfflineSolver owner, int index, Simulation sim, int seed, List<List<InputTick>> seeds, List<Memory.Source> restore, List<InputTick> plateau)
            {
                _owner = owner;
                _index = index;
                _afterTick = AfterTick;
                _evictWeight = (key, cell) => Weight(key, cell, 0f);
                _onStep = OnStep;
                _sim = sim;
                _penalty = owner.Penalty?.Invoke(sim);
                _store = new SnapshotStore(sim);
                _gate = new ContactProbe(sim);
                _rng = new System.Random(seed);
                _seeds = new Queue<List<InputTick>>(seeds.Select(s => s.ToList()));
                _seeds.Enqueue(new List<InputTick>());
                _restore = new Queue<Memory.Source>(restore);
                _plateau = plateau?.ToList();
                Caps caps = Caps.Parse(owner.Capabilities);
                _omni = caps.Omni;
                _dash = caps.Dash;
                _goalMode = sim.Goals != null;
                if (!sim.TimedFromRunStart && sim.TargetCourse > 0)
                    _gates = sim.World.All<startGate>().Where(g => g.GetComponentInParent<courseScript>() is courseScript c && c.courseNumber == sim.TargetCourse)
                        .Select(g => sim.ToWorld(ScenePos.From(g.transform.position))).ToArray();
                if (_gates != null && _gates.Length > 0)
                {
                    var from = new[] { sim.PlayerWorld, new WorldPos(sim.Player.respawnPoint.x, sim.Player.respawnPoint.y) };
                    _setupReach = Math.Max(SetupReach, from.Max(at => _gates.Min(g => Math.Max(Math.Abs(at.x - g.x), Math.Abs(at.y - g.y)))) + 500f);
                }
                _resets = _goalMode && !owner.NoResets;
                if (owner.GuideWeight > 0f && DistanceField.Enabled)
                {
                    _guide = DistanceField.For(sim);
                    if (!_goalMode && owner._path != null) _guide.SetVia(owner._path.Via);
                }
                if (_goalMode) Interlocked.CompareExchange(ref owner._near, Enumerable.Repeat(float.PositiveInfinity, sim.Goals.Goals.Length + 1).ToArray(), null);
                _archive = owner._archive;
                _swapper = sim.World.All<colouredBlockSwapper>().FirstOrDefault();
                if (owner.VisitCell > 0f) Visits = new Dictionary<long, float>();
                _kinds = _dash
                    ? new[] { "cut", "cut", "hop", "nojump", "stick", "insert", "dash", "dash", "dash", "dashjump", "dashjump", "latejump", "latejump",
                        "superbounce", "superbounce", "superbounce", "backdash", "backdash", "hold", "hold", "retime", "retime", "vertical", "vertical",
                        "springdash", "lockstick", "lockstick", "dashturn" }
                    : new[] { "cut", "cut", "hop", "nojump", "stick", "insert", "hold", "retime", "vertical" };
                if (!owner.NoResets) _kinds = _kinds.Concat(new[] { "reset", "noreset", "movereset" }).ToArray();
                if (_omni) _kinds = _kinds.Concat(OmniKinds).ToArray();
            }

            public readonly Dictionary<long, float> Visits;
            readonly string[] _kinds;

            public void Loop()
            {
                while (!_owner._stop && !_owner.Spent)
                {
                    AdoptShared();
                    int generation = _owner._exportGeneration;
                    if (Exported != generation) HandOver(generation);
                    if (_seeds.Count > 0) { Try(_seeds.Dequeue(), "seed"); continue; }
                    if (_owner._offered.TryDequeue(out List<InputTick> offered)) { Try(offered, "walk"); continue; }
                    if (_restore.Count > 0)
                    {
                        Restore(_restore.Dequeue());
                        if (_restore.Count == 0) { _spliceScan = true; _owner._partitions[_index] = new List<Memory.Source>(); }
                        continue;
                    }
                    if (_plateau != null) { Try(_plateau, "plateau"); _plateau = null; continue; }
                    if (_spliceScan && _bestInputs != null) { QueueSplices(); _spliceScan = false; }
                    if (_spliceQueue.Count > 0)
                    {
                        var (cell, bestTick) = _spliceQueue.Dequeue();
                        SimSnapshot state = StateOf(cell);
                        _fromCell = cell;
                        Splice(cell.Source.ToList(cell.Length), cell.Length, bestTick, state);
                        _fromCell = null;
                        continue;
                    }
                    if (_bestInputs == null || _rng.NextDouble() < .5) Explore();
                    else Optimize();
                }
            }

            void QueueSplices()
            {
                _spliceQueue.Clear();
                var found = new List<(Cell cell, int bestTick, float gain)>();
                foreach (KeyValuePair<long, (float time, int tick)> first in _bestRun.First)
                    if (_archive.TryGetValue(first.Key, out Cell cell) && cell.Length > 0 && cell.Rank < first.Value.time - SpliceGain)
                        found.Add((cell, first.Value.tick, first.Value.time - cell.Rank));
                foreach (var f in found.OrderByDescending(f => f.gain).Take(64)) _spliceQueue.Enqueue((f.cell, f.bestTick));
            }
            readonly Queue<(Cell cell, int bestTick)> _spliceQueue = new Queue<(Cell, int)>();
            bool _spliceScan;

            void HandOver(int generation)
            {
                float sharedTime;
                long sharedTicks;
                lock (_owner.Shared) { sharedTime = _owner.Shared.Time; sharedTicks = _owner.Shared.RunTicks; }
                LastHandover = new Handover
                {
                    Pending = _restore.ToList(),
                    Plateau = _bestInputs != null && !_owner.Ranks(sharedTime, sharedTicks, _best, _bestRunTicks) ? _bestInputs : null,
                    Visits = Visits == null ? null : new Dictionary<long, float>(Visits),
                };
                Exported = generation;
            }

            void Restore(Memory.Source source)
            {
                List<InputTick> inputs = source.Inputs.ToList();
                _restoreKeys = new long[inputs.Count + 1];
                Array.Fill(_restoreKeys, long.MinValue);
                Evaluate(inputs, null, null, false, out _, out _, out _, encoded: source.Inputs);
                long[] keys = _restoreKeys;
                _restoreKeys = null;
                foreach (Memory.SavedCell saved in source.Cells)
                    if (saved.Length < keys.Length && _archive.TryGetValue(keys[saved.Length], out Cell cell))
                        cell.Picks = Math.Max(cell.Picks, saved.Picks);
            }
            long[] _restoreKeys;

            void AdoptShared()
            {
                List<InputTick> inputs;
                float time;
                long runTicks;
                if (Volatile.Read(ref _owner.Shared.Version) == _sharedVersion) return;
                lock (_owner.Shared)
                {
                    if (_owner.Shared.Version == _sharedVersion) return;
                    _sharedVersion = _owner.Shared.Version;
                    inputs = _owner.Shared.Inputs;
                    time = _owner.Shared.Time;
                    runTicks = _owner.Shared.RunTicks;
                }
                if (inputs == null || !_owner.Ranks(time, runTicks, _best, _bestRunTicks)) return;
                var replay = new Replay(inputs.Count);
                if (Evaluate(inputs, null, replay, false, out float replayed, out int finishTick, out long replayedTicks))
                    Adopt(inputs, replayed, finishTick, replayedTicks, replay);
            }

            void Explore()
            {
                if (_poolPicks <= 0 || _owner._cells > _poolCount + _poolCount / 8 + 16 || _poolEvictions != _owner._evictions) Pool();
                if (_poolCount == 0) { Try(RandomBurst(new List<InputTick>()), "explore"); return; }
                Cell chosen = Draw();
                for (int draw = 1; draw < CachedDraws && !_states.ContainsKey(chosen); draw++) chosen = Draw();
                _poolPicks--;
                Interlocked.Increment(ref chosen.Picks);
                SimSnapshot from = StateOf(chosen);
                if (from == null) return;
                List<InputTick> inputs = RandomBurst(chosen.Source.ToList(chosen.Length));
                _splicing = _bestInputs != null;
                _spliceAt = -1;
                _fromCell = chosen;
                List<InputTick> spliceInto = _bestInputs;
                bool grafts = _owner.Mined != null;
                _recordContacts = grafts;
                Try(inputs, "explore", from);
                _recordContacts = false;
                int lo = Math.Max(chosen.Length, from.Tick + 1);
                (int tick, Contact kind)? graft = null;
                if (grafts)
                {
                    if (_died) _contacts.Add((_endTick, Contact.Died));
                    _log.DropBefore(lo);
                    if (_contacts.Count > 0 && _rng.NextDouble() < _owner.GraftRate) graft = GraftContact();
                }
                _splicing = false;
                if (_spliceAt > chosen.Length && ReferenceEquals(spliceInto, _bestInputs)) Splice(inputs, _spliceAt, _spliceBestTick, from);
                if (graft is var (tick, kind)) Graft(inputs, tick, kind, lo, from);
                _fromCell = null;
            }

            readonly ContactLog _log = new ContactLog();
            readonly ContactProbe _gate;
            List<(int tick, Contact kind)> _contacts => _log.Contacts;
            bool _recordContacts;

            void RecordContact(int tick) { _log.Tick(tick, _sim.Player, _gate.NearGate()); }

            void RecordSlide() { _log.Slide((int)_sim.Engine.RunTicks, _sim.Player); }

            void RecordStep() { _log.Step(_sim); }

            (int tick, Contact kind)? GraftContact()
            {
                (int lo, int hi) focus = _owner.GraftFocus is var (flo, fhi) && _rng.NextDouble() < .75 ? (flo, fhi) : (int.MinValue, int.MaxValue);
                bool In((int tick, Contact kind) c) => c.tick >= focus.lo && c.tick <= focus.hi && _owner.Mined.Offers(c.kind, _resets);
                int n = 0;
                for (int i = 0; i < _contacts.Count; i++) if (In(_contacts[i])) n++;
                if (n == 0 && focus.lo != int.MinValue)
                {
                    focus = (int.MinValue, int.MaxValue);
                    for (int i = 0; i < _contacts.Count; i++) if (In(_contacts[i])) n++;
                }
                if (n == 0) return null;
                int pick = _rng.Next(n);
                for (int i = 0; i < _contacts.Count; i++)
                    if (In(_contacts[i]) && pick-- == 0) return _contacts[i];
                return null;
            }

            void Graft(List<InputTick> run, int tick, Contact contact, int lo, SimSnapshot from)
            {
                for (int draw = 0; draw < _owner.MinedDraws; draw++)
                {
                    if (_owner.Mined.Pick(contact, _rng, _resets) is not TechPattern pattern) break;
                    if (contact >= Contact.SpringHitLeft) Interlocked.Increment(ref _owner.TechGraftedAtStep);
                    MinedGraft(_owner.Mined, run, tick, lo, pattern, from, 0, null);
                }
            }

            void MinedGraft(TechGrafts mined, List<InputTick> run, int tick, int lo, TechPattern pattern, SimSnapshot from, int depth, PatternChain inherited,
                (int lo, int hi)? shift = null)
            {
                PatternChain chain = pattern.Chain ?? inherited;
                bool chaining = chain != null && depth < chain.Depth;
                for (int attempt = 0; attempt < pattern.Tries; attempt++)
                {
                    if (!TechGrafts.Compose(run, tick, lo, pattern, _rng, out List<InputTick> t, out int placed, shift)) continue;
                    Interlocked.Increment(ref _owner.TechGrafted);
                    if (depth > 0) Interlocked.Increment(ref _owner.ChainGrafts);
                    bool record = _recordContacts;
                    _recordContacts = chaining;
                    Try(t, pattern.Mode, from);
                    _recordContacts = record;
                    if (!chaining) continue;
                    if (pattern.Chain?.Expect is Contact expect)
                    {
                        Interlocked.Increment(ref _owner.ExpectChecks);
                        int want = placed + pattern.Chain.ExpectAt;
                        bool met = false;
                        foreach (var c in _contacts) if (c.kind == expect && c.tick >= want - 1 && c.tick <= want + 2) { met = true; break; }
                        if (!met) continue;
                        Interlocked.Increment(ref _owner.ExpectsMet);
                    }
                    (int tick, Contact kind)? next = null;
                    foreach (var c in _contacts) if (c.tick >= placed + 2 && Array.IndexOf(chain.On, c.kind) >= 0) { next = c; break; }
                    if (next is not var (at, kind) || _rng.NextDouble() < .15) continue;
                    if (mined.PickChain(kind, chain.With, _rng, _resets) is TechPattern follow)
                        MinedGraft(mined, t, at, lo, follow, from, depth + 1, chain, (chain.ShiftLo, chain.ShiftHi));
                }
            }
            bool _resets;

            void Pool()
            {
                if (_poolEvictions != _owner._evictions)
                {
                    foreach (Entry removed in _states.Values.Where(e => e.Cell.Removed).ToList())
                    {
                        removed.Use = 0;
                        _states.Remove(removed.Cell);
                    }
                    _poolEvictions = _owner._evictions;
                }
                int count = 0;
                float near = float.PositiveInfinity, far = float.NegativeInfinity;
                foreach (KeyValuePair<long, Cell> pair in _archive)
                {
                    if (count == _pool.Length) Array.Resize(ref _pool, Math.Max(_owner._cells + 64, count * 2));
                    _pool[count++] = pair.Value;
                    float d = pair.Value.Distance;
                    if (float.IsFinite(d))
                    {
                        if (d < near) near = d;
                        if (d > far) far = d;
                    }
                }
                if (count < _poolCount) Array.Clear(_pool, count, _poolCount - count);
                if (_poolWeights.Length < count)
                {
                    _poolWeights = new double[_pool.Length];
                    _ownPool = new Cell[_pool.Length];
                    _ownWeights = new double[_pool.Length];
                }
                float span = float.IsFinite(near) ? Math.Clamp(far - near, CellSize, GuideSpan) : 0f;
                double total = 0, own = 0;
                int owned = 0;
                for (int i = 0; i < count; i++)
                {
                    Cell cell = _pool[i];
                    float d = cell.Distance;
                    float closeness = span <= 0f || !float.IsFinite(d) ? 0f : Math.Clamp((near + span - d) / span, 0f, 1f);
                    double w = Weight(cell.Key, cell, closeness);
                    total += w;
                    _poolWeights[i] = total;
                    if (cell.Owner != _index) continue;
                    own += w;
                    _ownPool[owned] = cell;
                    _ownWeights[owned++] = own;
                }
                if (owned < _ownCount) Array.Clear(_ownPool, owned, _ownCount - owned);
                _poolCount = count;
                _ownCount = owned;
                _ownShare = owned == 0 || owned == count ? 0 : Math.Min(MostOwnShare, OwnShare * _owner._workers.Length * own / total);
                _poolPicks = Math.Max(PoolPicks, count / 256);
            }
            const int PoolPicks = 16;
            const double OwnShare = .9, MostOwnShare = .95;
            const float GuideSpan = 2000f;

            Cell Draw()
            {
                return _ownShare > 0 && _rng.NextDouble() < _ownShare ? DrawFrom(_ownPool, _ownWeights, _ownCount) : DrawFrom(_pool, _poolWeights, _poolCount);
            }

            Cell DrawFrom(Cell[] pool, double[] weights, int count)
            {
                int i = Array.BinarySearch(weights, 0, count, _rng.NextDouble() * weights[count - 1]);
                return pool[Math.Min(i < 0 ? ~i : i, count - 1)];
            }

            SimSnapshot StateOf(Cell cell)
            {
                if (_states.TryGetValue(cell, out Entry entry))
                {
                    Touch(entry);
                    Interlocked.Increment(ref _owner.StateHits);
                    return entry.State;
                }
                var path = new List<Cell> { cell };
                Entry found = null;
                for (Cell ancestor = cell.Parent; ancestor != null && !_states.TryGetValue(ancestor, out found); ancestor = ancestor.Parent) path.Add(ancestor);
                path.Reverse();
                if (found != null) Touch(found);
                SimSnapshot from = found?.State;
                List<InputTick> inputs = cell.Source.ToList();
                if (_owner.Resume is SnapshotIndex resume && resume.Find(inputs, cell.Length, from?.Tick ?? -1) is SnapshotIndex.Entry goal)
                    try
                    {
                        from = _store.StateOf(goal, goal.Tick - resume.Base);
                        Interlocked.Increment(ref _owner.GoalResumes);
                    }
                    catch (NotSupportedException) { }
                _capturePath = path;
                _captureNext = 0;
                _captured = null;
                Evaluate(inputs, from, null, false, out _, out _, out _, judgeAfter: cell.Length, encoded: cell.Source);
                _capturePath = null;
                Interlocked.Increment(ref _owner.StateReplays);
                return _captured;
            }

            void Touch(Entry entry)
            {
                entry.Use = ++_use;
                entry.Weight = Weight(entry.Cell.Key, entry.Cell, 0f);
                _evict.Enqueue(entry, (entry.Weight, entry.Use));
                if (_evict.Count <= 4 * _states.Count + 1024) return;
                _evict.Clear();
                _evict.EnqueueRange(_states.Values.Select(live => (live, (live.Weight, live.Use))));
            }
            List<Cell> _capturePath;
            int _captureNext;
            SimSnapshot _captured;
            Cell _fromCell;

            bool CaptureOnPath(int tick)
            {
                while (_captureNext < _capturePath.Count && _capturePath[_captureNext].Length < tick) _captureNext++;
                if (_captureNext == _capturePath.Count || _capturePath[_captureNext].Length != tick) return false;
                SimSnapshot state = Capture(tick);
                state.Waypoints = _waypoints;
                while (_captureNext < _capturePath.Count && _capturePath[_captureNext].Length == tick) Keep(_capturePath[_captureNext++], state);
                if (_captureNext < _capturePath.Count) return false;
                _captured = state;
                return true;
            }

            public const int StartStateBytes = 32 << 10;
            double _stateBytes = StartStateBytes;

            SimSnapshot Capture(int tick)
            {
                long before = GC.GetAllocatedBytesForCurrentThread() - _sim.SharedBytes;
                SimSnapshot state = _sim.Capture(tick);
                _stateBytes += (GC.GetAllocatedBytesForCurrentThread() - _sim.SharedBytes - before - _stateBytes) / 64;
                return state;
            }

            void Keep(Cell cell, SimSnapshot state)
            {
                if (_states.TryGetValue(cell, out Entry old)) old.Use = 0;
                var entry = new Entry { Cell = cell, State = state };
                _states[cell] = entry;
                cell.Owner = _index;
                Touch(entry);
                int cap = (int)Math.Max(512, _owner._stateBudget / Math.Max(1024.0, _stateBytes));
                if (cap != _cap) _owner.StatesPerWorker = _cap = cap;
                while (_states.Count > cap && _evict.TryDequeue(out Entry victim, out var priority))
                    if (priority.use == victim.Use) _states.Remove(victim.Cell);
            }

            void Splice(List<InputTick> run, int at, int bestTick, SimSnapshot from)
            {
                if (from == null) return;
                var t = new List<InputTick>(at + _bestInputs.Count - bestTick);
                t.AddRange(CollectionsMarshal.AsSpan(run)[..Math.Min(at, run.Count)]);
                if (bestTick < _bestInputs.Count) t.AddRange(CollectionsMarshal.AsSpan(_bestInputs)[Math.Max(0, bestTick)..]);
                if (t.Count <= at || !Seam(t, at, from.Tick)) return;
                Interlocked.Increment(ref _owner.Splices);
                Try(t, "splice", from);
            }

            double Weight(long key, Cell cell, float closeness)
            {
                double w = key < 0 ? 3.0 : 1.0;
                if (key < 0 && _bestInputs == null)
                {
                    double reach = !_goalMode ? (_owner._maxTimedRank > 0 ? cell.Rank / _owner._maxTimedRank : 0) : GoalReach(GoalProgress(key), cell.Distance);
                    w *= 1 + 10 * Math.Pow(reach, 4);
                }
                if (_owner._path != null && _bestInputs == null) w *= 1 + 10 * cell.Waypoints;
                if (closeness > 0f) w *= 1 + 10 * _owner.GuideWeight * closeness * closeness;
                if (cell.Discouraged) w *= DiscouragedWeight;
                return w / Math.Sqrt(cell.Picks + 1);
            }

            double GoalReach(int progress, float distance)
            {
                int min = _owner._minProgress, max = _owner._maxProgress;
                if (max < 0) return 0;
                double span = max - min + Nearness(max, _owner._near[max]);
                return span > 0 ? (progress - min + Nearness(progress, distance)) / span : 0;
            }

            double Nearness(int progress, float distance)
            {
                float near = _owner._near[progress];
                return float.IsFinite(near) && !float.IsNaN(distance) ? (near + CellSize) / (double)(distance + CellSize) : 0;
            }

            static readonly int[] Holds = { 1, 3, 6, 10, 16, 24, 30, 40 };
            static readonly int[] ResumeHolds = { 0, 3, 8, 16, 30, 60 };

            List<InputTick> RandomBurst(List<InputTick> inputs)
            {
                float x = inputs.Count > 0 ? inputs[inputs.Count - 1].x : 1f, y = inputs.Count > 0 ? inputs[inputs.Count - 1].y : 0f;
                int holdEnd = inputs.Count > 0 && inputs[inputs.Count - 1].held ? inputs.Count + OneOf(ResumeHolds) : 0;
                for (int chunk = _rng.Next(1, 4); chunk > 0; chunk--)
                {
                    if (_rng.NextDouble() < .35) x = OneOf(Sticks);
                    if (_rng.NextDouble() < .3) y = Vertical();
                    int length = _rng.Next(3, 21);
                    int start = inputs.Count;
                    for (int i = 0; i < length; i++) inputs.Add(new InputTick { x = x, y = y, held = start + i < holdEnd });
                    if (_goalMode && !_owner.NoResets && start > 0 && !inputs[start - 1].reset && _rng.NextDouble() < .05)
                    {
                        InputTick r = inputs[start]; r.reset = true; inputs[start] = r;
                    }
                    if (_rng.NextDouble() < .4) holdEnd = Hop(inputs, start, OneOf(Holds));
                    if (_dash && _rng.NextDouble() < .25)
                    {
                        int at = start + _rng.Next(Math.Min(length, 4));
                        if (_omni && _rng.NextDouble() < .5) holdEnd = Omni(inputs, at, start, OneOf(OmniKinds));
                        else
                        {
                            InputTick d = inputs[at];
                            d.dash = true;
                            if (_rng.NextDouble() < .4) d.dashJump = _rng.Next(1, 11);
                            if (d.x == 0f && _rng.NextDouble() < .15) d.turnX = _rng.NextDouble() < .5 ? -1f : 1f;
                            inputs[at] = d;
                            if (d.dashJump > 0) holdEnd = HoldJump(inputs, at, OneOf(DashHolds));
                            if (d.dashJump == 0 && _rng.NextDouble() < .5)
                                holdEnd = start > 0 && inputs[start - 1].x != 0f && _rng.NextDouble() < .3
                                    ? BackDash(inputs, at, start)
                                    : SuperBounce(inputs, at, d.x == 0f ? (_rng.NextDouble() < .5 ? -1f : 1f) : Math.Sign(d.x));
                        }
                    }
                }
                if (_owner._path != null && !_owner._path.IsEmpty && inputs.Count > 0)
                {
                    InputTick coast = inputs[inputs.Count - 1].Coast();
                    for (int i = 0; i < 25; i++) inputs.Add(coast);
                }
                return inputs;
            }

            static readonly float[] Verticals = { -1f, -1f, 0f, 0f, 1f };
            float Vertical() { return OneOf(Verticals); }

            static int Hop(List<InputTick> t, int at, int hold)
            {
                if (at >= t.Count) return at;
                InputTick first = t[at];
                first.press = true; first.held = true; first.release = false;
                t[at] = first;
                for (int i = at + 1; i < Math.Min(t.Count, at + hold); i++) { InputTick k = t[i]; k.held = true; k.release = false; t[i] = k; }
                for (int i = at + hold; i < t.Count && t[i].held && !t[i].press; i++) { InputTick k = t[i]; k.held = false; k.release = false; t[i] = k; }
                if (at + hold < t.Count) { InputTick r = t[at + hold]; r.release = true; t[at + hold] = r; }
                return at + hold;
            }

            int SuperBounce(List<InputTick> t, int at, float toward)
            {
                if (at < 0 || at >= t.Count) return 0;
                for (int i = at; i < Math.Min(t.Count, at + 8); i++) { InputTick k = t[i]; k.x = toward; t[i] = k; }
                InputTick d = t[at]; d.dash = true; d.dashJump = 0; t[at] = d;
                int release = Hop(t, Math.Min(t.Count - 1, at + _rng.Next(0, 6)), OneOf(BounceHolds));
                if (_rng.NextDouble() < .5)
                    for (int i = at + 8; i < Math.Min(t.Count, at + 8 + _rng.Next(1, 20)); i++) { InputTick k = t[i]; k.x = -toward; t[i] = k; }
                return release;
            }

            int BackDash(List<InputTick> t, int at, int lo)
            {
                if (at < lo || at >= t.Count) return 0;
                int first = Math.Max(lo, at - _rng.Next(0, 5));
                float into = first > 0 && t[first - 1].x != 0f ? Math.Sign(t[first - 1].x) : (_rng.NextDouble() < .5 ? -1f : 1f);
                for (int i = first; i < Math.Min(t.Count, at + 8); i++) { InputTick k = t[i]; k.x = -into; t[i] = k; }
                InputTick d = t[at];
                d.dash = true; d.dashJump = 0;
                if (at - first >= 3 && _rng.NextDouble() < .5) d.x = 0f;
                t[at] = d;
                return Hop(t, at, OneOf(DashHolds));
            }

            static readonly (float x, float y)[] Aims = { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f), (1f, 1f), (-1f, 1f), (1f, -1f), (-1f, -1f) };
            static readonly string[] OmniKinds = { "aim", "aim", "steep", "wavedash", "wavedash", "ultra", "dashbounce", "dashbounce", "wavebounce", "wavebounce",
                "wavejump", "superground", "bouncechain", "bouncechain" };
            static readonly int[] DashHolds = { 3, 6, 10, 16, 24, 32, 40 };
            static readonly int[] BounceHolds = { 3, 6, 10, 16, 24, 40 }, DashJumpHolds = { 2, 5, 10, 16, 24, 40 }, LateHolds = { 0, 2, 5, 10, 16, 24, 40 };
            static readonly int[] Steps = { 1, 1, 2, 3, 5, 8 }, Nudges = { 1, 2, 3, 5, 8 }, Inserts = { 1, 2, 4, 8, 15 }, VerticalSpans = { 1, 3, 6, 12, 25, 50 };
            static readonly float[] Sticks = { -1f, 0f, 1f };

            T OneOf<T>(T[] from) { return from[_rng.Next(from.Length)]; }

            int Omni(List<InputTick> t, int at, int lo, string kind)
            {
                if (at < lo || at >= t.Count) return at;
                float side = t[at].x != 0f ? Math.Sign(t[at].x) : _rng.NextDouble() < .5 ? -1f : 1f;
                switch (kind)
                {
                    case "aim":
                    {
                        var (x, y) = OneOf(Aims);
                        return OmniDash(t, at, x, y, _rng.NextDouble() < .4 ? _rng.Next(1, 11) : 0, x != 0f ? x : side);
                    }
                    case "steep":
                    {
                        // Freeze aim: sideways 0.35 stays unsnapped (19 deg off vertical), unlike a dash pressed on a tick (StickFor).
                        int end = OmniDash(t, at, 0f, 0f, _rng.NextDouble() < .4 ? _rng.Next(1, 11) : 0, side);
                        InputTick d = t[at];
                        d.turnX = side * 0.35f;
                        d.turnY = _rng.NextDouble() < .75 ? 1f : -1f;
                        t[at] = d;
                        return end;
                    }
                    case "wavedash":
                        Airborne(t, at, lo);
                        return WaveDash(t, at, side);
                    case "ultra":
                        Airborne(t, at, lo);
                        WaveDash(t, at, side);
                        return WaveDash(t, at + 1 + _rng.Next(1, 13), side);
                    case "dashbounce":
                        Airborne(t, at, lo);
                        return DashBounce(t, at, side);
                    case "wavebounce":
                        Airborne(t, at, lo);
                        WaveDash(t, at, side);
                        return DashBounce(t, at + 1 + _rng.Next(1, 15), side);
                    case "superground":
                        OmniDash(t, at, side, 0f, _rng.Next(1, 7), side);
                        return DashBounce(t, at + 1 + _rng.Next(4, 20), side);
                    case "bouncechain":
                    {
                        int end = at;
                        double seed = _rng.NextDouble();
                        if (seed < .9) end = WaveDash(t, at, side);
                        if (seed < .7) end = WaveDash(t, end + 1 + _rng.Next(1, 13), side);
                        for (int k = _rng.Next(2, 6); k > 0; k--) end = DashBounce(t, end + 1 + _rng.Next(3, 12), side);
                        return end;
                    }
                    case "wavejump":
                        return OmniDash(t, at, side, 1f, _rng.NextDouble() < .6 ? _rng.Next(1, 11) : 0, side);
                }
                return at;
            }

            int WaveDash(List<InputTick> t, int at, float side) { return OmniDash(t, at, side, -1f, _rng.NextDouble() < .85 ? _rng.Next(4, 11) : 0, side); }

            int DashBounce(List<InputTick> t, int at, float side) { return OmniDash(t, at, 0f, -1f, _rng.Next(1, 11), side); }

            void Airborne(List<InputTick> t, int at, int lo)
            {
                int from = at - _rng.Next(1, 5);
                if (from >= lo && _rng.NextDouble() < .5) Hop(t, from, _rng.Next(1, 4));
            }

            int OmniDash(List<InputTick> t, int at, float x, float y, int jump, float side)
            {
                int hold = jump > 0 ? OneOf(DashHolds) : 0;
                Pad(t, at + 1 + Math.Max(8, hold + 1));
                InputTick d = t[at];
                d.dash = true; d.x = x; d.y = y; d.dashJump = jump; d.turnX = d.turnY = null;
                if (jump > 0) d.press = false;
                t[at] = d;
                for (int i = at + 1; i < at + 9; i++) { InputTick k = t[i]; k.x = side; k.y = 0f; t[i] = k; }
                return jump == 0 ? at : HoldJump(t, at, hold);
            }

            static int HoldJump(List<InputTick> t, int at, int hold)
            {
                int end = at + 1 + hold;
                Pad(t, end + 1);
                for (int i = at + 1; i < end; i++) { InputTick k = t[i]; k.held = true; k.press = false; k.release = false; t[i] = k; }
                for (int i = end; i < t.Count && t[i].held && !t[i].press; i++) { InputTick k = t[i]; k.held = false; k.release = false; t[i] = k; }
                InputTick r = t[end]; r.release = true; t[end] = r;
                return end;
            }

            static void Pad(List<InputTick> t, int count)
            {
                if (t.Count == 0) return;
                InputTick coast = t[t.Count - 1].Coast();
                while (t.Count < count) t.Add(coast);
            }

            static int HoldEnd(List<InputTick> t, int q)
            {
                int e = q + 1;
                while (e < t.Count && t[e].held && !t[e].press) e++;
                return e;
            }

            static void SetHold(List<InputTick> t, int q, int end)
            {
                int old = HoldEnd(t, q);
                end = Math.Max(q + 1, end);
                InputTick first = t[q]; if (first.dashJump == 0) first.held = true; t[q] = first;
                for (int i = q + 1; i < Math.Min(t.Count, Math.Max(old, end) + 1); i++)
                {
                    if (t[i].press) break;
                    InputTick k = t[i];
                    k.held = i < end;
                    k.release = i == end;
                    t[i] = k;
                }
            }

            static void Cut(List<InputTick> t, int at, int count)
            {
                if (at < t.Count) t.RemoveRange(at, Math.Min(count, t.Count - at));
            }

            static bool CanReset(List<InputTick> t, int p)
            {
                return p > 0 && p < t.Count && !t[p].reset && !t[p - 1].reset && (p + 1 >= t.Count || !t[p + 1].reset);
            }

            static readonly float?[] LockValues = { null, -1f, 0f, 1f };
            static readonly Func<InputTick, bool> IsDash = k => k.dash, IsPress = k => k.press, IsJump = k => k.press || k.dashJump > 0, IsPressOrDash = k => k.press || k.dash, IsReset = k => k.reset;

            int Pick(List<InputTick> t, Func<InputTick, bool> match)
            {
                int count = 0;
                foreach (InputTick k in t) if (match(k)) count++;
                if (count == 0) return -1;
                for (int i = 0, skip = _rng.Next(count); ; i++) if (match(t[i]) && skip-- == 0) return i;
            }

            static readonly string[] NewDashKinds = { "dash", "dashjump", "latejump", "superbounce", "backdash",
                "aim", "steep", "wavedash", "ultra", "dashbounce", "wavebounce", "wavejump", "superground", "bouncechain" };
            // Dash buffer 8 ticks with gate slack, +6 held through a wall jump lock (RULES.md 4), +press tick's own refill.
            const int DashReach = 16;
            readonly List<int> _dashTicks = new List<int>();

            int DashTick(int n)
            {
                bool[] ready = _bestRun.Dash;
                _dashTicks.Clear();
                int next = int.MaxValue;
                for (int q = Math.Min(n + DashReach, ready.Length - 1); q >= 0; q--)
                {
                    if (ready[q]) next = q;
                    if (q < n && next - q <= DashReach) _dashTicks.Add(q);
                }
                return _dashTicks.Count == 0 ? -1 : _dashTicks[_rng.Next(_dashTicks.Count)];
            }

            void Optimize()
            {
                List<InputTick> t = _bestInputs.ToList();
                int n = t.Count, p = _rng.Next(n);
                string kind = OneOf(_kinds);
                if (Array.IndexOf(NewDashKinds, kind) >= 0 && (p = DashTick(n)) < 0) return;
                switch (kind)
                {
                    case "aim": case "steep":
                    {
                        int dash = Pick(t, IsDash);
                        Omni(t, dash >= 0 && _rng.NextDouble() < .7 ? dash : p, 0, kind);
                        break;
                    }
                    case "wavedash": case "ultra": case "dashbounce": case "wavebounce": case "wavejump": case "superground": case "bouncechain":
                        Cut(t, Omni(t, p, 0, kind) + _rng.Next(1, 25), _rng.Next(0, 20));
                        break;
                    case "cut": Cut(t, p, OneOf(Steps)); break;
                    case "insert":
                        float x = OneOf(Sticks);
                        int count = OneOf(Inserts);
                        for (int i = 0; i < count; i++) t.Insert(p, new InputTick { x = x, y = t[p].y, held = t[p].held });
                        break;
                    case "dash":
                    {
                        InputTick k = t[p]; k.dash = true; t[p] = k;
                        Cut(t, p + _rng.Next(1, 25), _rng.Next(0, 20));
                        break;
                    }
                    case "dashjump":
                    {
                        InputTick k = t[p]; k.dash = true; t[p] = k;
                        int j = Math.Min(n - 1, p + _rng.Next(0, 7));
                        Hop(t, j, OneOf(DashJumpHolds));
                        Cut(t, j + _rng.Next(1, 25), _rng.Next(0, 20));
                        break;
                    }
                    case "superbounce":
                        SuperBounce(t, p, t[p].x == 0f ? (_rng.NextDouble() < .5 ? -1f : 1f) : Math.Sign(t[p].x));
                        Cut(t, p + 8 + _rng.Next(1, 25), _rng.Next(0, 20));
                        break;
                    case "backdash":
                        BackDash(t, p, 0);
                        Cut(t, p + 8 + _rng.Next(1, 25), _rng.Next(0, 20));
                        break;
                    case "latejump":
                    {
                        InputTick k = t[p]; k.dash = true; k.press = false; k.dashJump = _rng.Next(1, 11); t[p] = k;
                        HoldJump(t, p, OneOf(LateHolds));
                        Cut(t, p + _rng.Next(1, 25), _rng.Next(0, 20));
                        break;
                    }
                    case "hop":
                        Hop(t, p, OneOf(Holds));
                        Cut(t, p + _rng.Next(1, 30), _rng.Next(0, 10));
                        break;
                    case "nojump":
                    {
                        int q = Pick(t, IsPressOrDash);
                        if (q < 0) return;
                        InputTick k = t[q]; k.press = k.dash = false; k.dashJump = 0; t[q] = k;
                        break;
                    }
                    case "hold":
                    {
                        int q = Pick(t, IsJump);
                        if (q < 0) return;
                        int end = _rng.NextDouble() < .5 ? q + OneOf(Holds)
                            : HoldEnd(t, q) + (_rng.NextDouble() < .5 ? -1 : 1) * OneOf(Nudges);
                        SetHold(t, q, end);
                        break;
                    }
                    case "retime":
                    {
                        int q = Pick(t, IsPressOrDash);
                        if (q < 0) return;
                        int to = q + (_rng.NextDouble() < .5 ? -1 : 1) * OneOf(Steps);
                        if (to < 0 || to >= n || t[to].press || t[to].dash) return;
                        InputTick from = t[q], dest = t[to];
                        if (from.dash)
                        {
                            dest.dash = true; dest.dashJump = from.dashJump; dest.turnX = from.turnX; dest.turnY = from.turnY;
                            dest.pause = from.pause; dest.pauseTick = from.pauseTick; dest.pauseFrame = from.pauseFrame;
                            from.dash = false; from.dashJump = 0; from.turnX = from.turnY = null; from.pause = from.pauseTick = from.pauseFrame = 0;
                            t[q] = from; t[to] = dest;
                            break;
                        }
                        int end = HoldEnd(t, q);
                        bool heldBefore = q > 0 && t[q - 1].held;
                        from.press = false; t[q] = from;
                        dest.press = true; dest.held = true; dest.release = false; t[to] = dest;
                        for (int i = Math.Min(q, to) + 1; i < Math.Max(q, to); i++) { InputTick k = t[i]; k.held = to < q || heldBefore; k.release = false; t[i] = k; }
                        if (to > q && !heldBefore) { InputTick k = t[q]; k.held = false; k.release = false; t[q] = k; }
                        if (end > to) SetHold(t, to, end);
                        break;
                    }
                    case "stick":
                    {
                        float was = Mathf.Abs(t[p].x), sign = t[p].x != 0f ? Mathf.Sign(t[p].x) : _rng.NextDouble() < .5 ? -1f : 1f;
                        float partial = sign * (was >= 1f ? .8f : was == 0f ? .3f : Mathf.Clamp(was + (_rng.NextDouble() < .5 ? -.1f : .1f), .3f, .8f));
                        int pick = _rng.Next(Sticks.Length + 1);
                        float aim = pick < Sticks.Length ? Sticks[pick] : partial;
                        for (int i = p; i < Math.Min(n, p + _rng.Next(1, 15)); i++) { InputTick k = t[i]; k.x = aim; t[i] = k; }
                        break;
                    }
                    case "vertical":
                    {
                        float aim = Vertical();
                        for (int i = p; i < Math.Min(n, p + OneOf(VerticalSpans)); i++) { InputTick k = t[i]; k.y = aim; t[i] = k; }
                        break;
                    }
                    case "springdash":
                    {
                        InputTick k = t[p]; k.springDash = _rng.NextDouble() < .2 ? 0 : _rng.Next(1, 26); t[p] = k;
                        break;
                    }
                    case "lockstick":
                    {
                        int dash = Pick(t, IsDash);
                        int q = dash >= 0 && _rng.NextDouble() < .7 ? dash : p;
                        InputTick k = t[q];
                        k.lockX = OneOf(LockValues);
                        k.lockY = OneOf(LockValues);
                        bool jump = k.dash && k.dashJump == 0 && _rng.NextDouble() < .5;
                        if (jump) k.dashJump = _rng.Next(1, 11);
                        t[q] = k;
                        if (jump) HoldJump(t, q, OneOf(DashHolds));
                        break;
                    }
                    case "dashturn":
                    {
                        int q = Pick(t, IsDash);
                        if (q < 0) return;
                        InputTick k = t[q];
                        k.x = 0f;
                        if (_rng.NextDouble() < .5) k.y = -1f;
                        k.turnX = _rng.NextDouble() < .2 ? null : _rng.NextDouble() < .5 ? -1f : 1f;
                        t[q] = k;
                        break;
                    }
                    case "reset":
                    {
                        if (!_goalMode)
                        {
                            int timed = Array.FindIndex(_bestRun.Elapsed, e => e >= 0f);
                            p = 1 + _rng.Next(Math.Max(1, timed < 0 ? n : timed));
                        }
                        if (!CanReset(t, p)) return;
                        InputTick k = t[p]; k.reset = true; t[p] = k;
                        break;
                    }
                    case "noreset":
                    {
                        int q = Pick(t, IsReset);
                        if (q < 0) return;
                        InputTick k = t[q]; k.reset = false; t[q] = k;
                        break;
                    }
                    case "movereset":
                    {
                        int q = Pick(t, IsReset);
                        if (q < 0) return;
                        InputTick k = t[q]; k.reset = false; t[q] = k;
                        int to = q + (_rng.NextDouble() < .5 ? -1 : 1) * OneOf(Steps);
                        if (to < 0 || to >= n || !CanReset(t, to)) return;
                        k = t[to]; k.reset = true; t[to] = k;
                        break;
                    }
                }
                {
                    if (t.Count > 0 && t[0].reset && !_bestInputs[0].reset) { InputTick k = t[0]; k.reset = false; t[0] = k; }
                    for (int i = 1; i < t.Count; i++)
                        if (t[i].reset && t[i - 1].reset) { InputTick k = t[i]; k.reset = false; t[i] = k; }
                }
                int s = 0;
                while (s < t.Count && s < _bestInputs.Count && t[s].Equals(_bestInputs[s])) s++;
                int converge = int.MaxValue;
                if (t.Count == _bestInputs.Count)
                {
                    int j = t.Count - 1;
                    while (j >= 0 && t[j].Equals(_bestInputs[j])) j--;
                    converge = j + 1 + 12;
                }
                SimSnapshot[] states = _bestRun.States;
                s = Math.Min(Math.Min(s, t.Count - 1), states.Length - 1);
                while (s > 0 && states[s] == null) s--;
                Try(t, "optimize", s > 0 ? states[s] : null, converge);
            }

            void Try(List<InputTick> inputs, string mode, SimSnapshot from = null, int converge = int.MaxValue)
            {
                if (inputs.Count == 0) inputs.Add(new InputTick());
                if (_owner.Accept != null && !_owner.Accept(inputs)) return;
                if (from != null && from.Tick >= inputs.Count) from = null;
                bool finished = Evaluate(inputs, from, null, mode == "optimize" && _penalty == null, out float time, out _, out long runTicks, converge: converge);
                bool converged = _converged;
                if (from != null && _rng.Next(BranchCheckEvery) == 0)
                {
                    bool branch = Evaluate(inputs, from, null, false, out float branchTime, out _, out _);
                    Vector3 end = _sim.Player.transform.m_Position;
                    bool again = Evaluate(inputs, null, null, false, out float replay, out _, out _, from.Tick);
                    Interlocked.Increment(ref _owner.BranchChecks);
                    if (again == branch && Math.Abs(replay - branchTime) < .0001f && (end - _sim.Player.transform.m_Position).sqrMagnitude < .01f)
                        Interlocked.Increment(ref _owner.BranchMatches);
                }
                if (finished)
                {
                    Interlocked.Increment(ref _owner.Finishes);
                    _owner.FinishedRun?.Invoke(new List<InputTick>(inputs), time);
                }
                bool drift = (mode == "optimize" && converged) || ((mode == "optimize" || mode == "plateau") && finished && !_owner.Ranks(_best, _bestRunTicks, time, runTicks));
                if (!drift && (!finished || !_owner.Ranks(time, runTicks, _best, _bestRunTicks))) return;
                SimSnapshot resume = mode == "optimize" && _penalty == null ? from : null;
                var record = new Replay(inputs.Count);
                if (resume != null) record.CopyPrefix(_bestRun, resume.Tick);
                if (!Evaluate(inputs, resume, record, false, out float replayed, out int finishTick, out long replayedTicks)
                    || _owner.Ranks(_best, _bestRunTicks, replayed, replayedTicks)) return;
                long priced = _pricedTicks;
                inputs = DropIdlePauses(inputs, without =>
                {
                    var bare = new Replay(without.Count);
                    if (!Evaluate(without, null, bare, false, out float t, out int f, out long ticks) || _owner.Ranks(replayed, replayedTicks, t, ticks)) return false;
                    (record, replayed, finishTick, replayedTicks, priced) = (bare, t, f, ticks, _pricedTicks);
                    return true;
                });
                _pricedTicks = priced;
                bool improved = _owner.Ranks(replayed, replayedTicks, _best, _bestRunTicks);
                Adopt(inputs, replayed, finishTick, replayedTicks, record);
                if (improved) _owner.Offer(replayed, replayedTicks, _bestInputs, mode, _pricedTicks);
                else Interlocked.Increment(ref _owner.Drifts);
            }

            bool Evaluate(List<InputTick> inputs, SimSnapshot from, Replay replay, bool cut,
                out float time, out int finishTick, out long runTicks, int judgeAfter = -1, int converge = int.MaxValue, InputRuns encoded = null)
            {
                _converged = false;
                ForcedPath path = _owner._path;
                _waypoints = from != null && from.Tick < inputs.Count ? from.Waypoints : 0;
                _avoided = false;
                if (_recordContacts) _log.Reset();
                _runCorners = _recordContacts && _omni;
                _runInputs = inputs;
                _runEncoded = encoded;
                _runFrom = from;
                _runReplay = replay;
                _runCut = cut;
                _runJudgeAfter = judgeAfter;
                _runConverge = converge;
                RunResult result = _sim.Run(inputs, from, _afterTick, _goalMode ? 400 + inputs.Count : 400,
                    path == null && Visits == null && !_runCorners && !_recordContacts ? null : _onStep);
                if (_capturePath != null) Interlocked.Add(ref _owner.ReplayTicks, result.Stepped);
                else
                {
                    Interlocked.Increment(ref _owner.Runs);
                    Interlocked.Add(ref _owner.Ticks, result.Stepped);
                    _owner._ends.AddOrUpdate(EndKey(result.EndReason), 1, (_, n) => n + 1);
                }
                _died = result.EndReason != null && result.EndReason.StartsWith("Player death", StringComparison.Ordinal);
                time = result.Time;
                finishTick = result.FinishTick;
                runTicks = _owner.Farm ? MacroEngine.LiveUnits(result.PhysicsTicks, result.HeldFrames) : result.PhysicsTicks;
                _pricedTicks = 0;
                bool finished = result.Finished && (path == null || _waypoints == path.Via.Count && !_avoided);
                if (!finished || _penalty == null || from != null || judgeAfter >= 0) return finished;
                double price = _penalty(inputs, result);
                if (double.IsPositiveInfinity(price)) return false;
                _pricedTicks = (long)Math.Ceiling(price / _sim.World.FixedDeltaTime);
                time += (float)price;
                runTicks += _pricedTicks;
                return true;
            }

            readonly Func<List<InputTick>, RunResult, double> _penalty;
            long _pricedTicks;

            readonly Func<int, bool> _afterTick;
            readonly Action<int> _onStep;
            List<InputTick> _runInputs;
            InputRuns _runEncoded;
            SimSnapshot _runFrom;
            Replay _runReplay;
            bool _runCut, _runCorners;
            int _runJudgeAfter, _runConverge;

            void OnStep(int step)
            {
                if (_runCorners) RecordSlide();
                if (_recordContacts) RecordStep();
                ForcedPath path = _owner._path;
                Dictionary<long, float> visits = Visits;
                if (path == null && visits == null) return;
                WorldPos p = _sim.PlayerWorld;
                if (visits != null)
                {
                    long cell = VisitKey(p, _owner.VisitCell);
                    float t = _sim.Elapsed(null);
                    if (!visits.TryGetValue(cell, out float seen) || t < seen) visits[cell] = t;
                }
                if (path == null) return;
                int before = _waypoints;
                if (path.Step(_sim.PlayerStepFrom, p, _sim.Progress, ref _waypoints, !path.OnCourseOnly || _sim.TimingCourse() != null)) _avoided = true;
                if (_waypoints > before && _waypoints > _owner.MostWaypoints)
                    lock (_owner.Shared) _owner.MostWaypoints = Math.Max(_owner.MostWaypoints, _waypoints);
            }

            bool AfterTick(int tick)
            {
                List<InputTick> inputs = _runInputs;
                Replay replay = _runReplay;
                _endTick = tick;
                float behind = float.NaN;
                if (_recordContacts)
                {
                    RecordContact(tick);
                    behind = _log.Behind;
                }
                if (_avoided) return false;
                bool recorded = replay != null && tick < replay.Body.Length;
                if (recorded)
                {
                    replay.Body[tick] = _sim.Physics.PlayerBody();
                    if (tick % StateEvery == 0) replay.Fingerprint[tick] = _sim.Fingerprint();
                    replay.Waypoints[tick] = _waypoints;
                    replay.Dash[tick] = _sim.Player.dashUnlocked && _sim.Player.airDashesLeft > 0;
                }
                else if (tick >= _runConverge && _bestRun != null && tick < _bestRun.Fingerprint.Length && _bestRun.Fingerprint[tick] is int best
                    && _sim.Physics.PlayerBody() == _bestRun.Body[tick] && _sim.Fingerprint() == best && _waypoints == _bestRun.Waypoints[tick])
                {
                    _converged = true;
                    return false;
                }
                courseScript active = _sim.TimingCourse();
                bool timing = _sim.TimedFromRunStart || active != null;
                if (!timing && OutOfSetupReach()) return false;
                long key = _goalMode ? GoalCellKey(Place(_sim, _swapper, true, ref behind, false), _sim.Progress, _sim.Goals.Armed)
                    : CellKey(Place(_sim, _swapper, false, ref behind, _owner._fresh) | (timing ? 0L : Motion(_sim.Player)), timing, _waypoints);
                bool losing = false;
                float elapsed = _sim.Elapsed(active);
                int filed = Math.Min(tick, inputs.Count);
                if (recorded) { replay.Keys[tick] = key; replay.Elapsed[tick] = elapsed; }
                replay?.Record(filed, key, elapsed);
                if (elapsed >= 0f)
                {
                    if (tick > _runJudgeAfter && elapsed > _best + _owner.LosingSlack) losing = true;
                    if (_runCut && tick > _runJudgeAfter && _bestRun.Arrival.TryGetValue(key, out float bestAt) && elapsed > bestAt + SegmentSlack + _owner.LosingSlack) losing = true;
                    if (_splicing && _spliceAt < 0 && _runFrom != null && tick > _runFrom.Tick && tick < inputs.Count && !_sim.Engine.InputsExhausted
                        && _bestRun.First.TryGetValue(key, out (float time, int tick) first) && elapsed < first.time - SpliceGain)
                    {
                        _spliceAt = tick;
                        _spliceBestTick = first.tick;
                    }
                }
                if (_restoreKeys != null && filed < _restoreKeys.Length) _restoreKeys[filed] = key;
                if (_capturePath != null && CaptureOnPath(tick)) return false;
                FileCell(inputs, filed, key, elapsed >= 0f ? elapsed : tick * .02f, replay?.States);
                return !losing;
            }

            const float SetupReach = 2500f;
            readonly WorldPos[] _gates;
            readonly float _setupReach = SetupReach;

            bool OutOfSetupReach()
            {
                if (_gates == null || _gates.Length == 0) return false;
                WorldPos p = _sim.PlayerWorld;
                foreach (WorldPos g in _gates)
                    if (Math.Max(Math.Abs(p.x - g.x), Math.Abs(p.y - g.y)) <= _setupReach) return false;
                return true;
            }

            static float Behind(Movement player, ref float behind)
            {
                if (float.IsNaN(behind)) behind = WallBehind(player);
                return behind;
            }
            bool _splicing;
            int _spliceAt, _spliceBestTick;
            const float SpliceGain = .02f;
            int _waypoints;
            bool _avoided;
            bool _converged;
            bool _died;
            int _endTick;

            static readonly Func<Movement, int> GroundType = GroundTypeGetter();

            static Func<Movement, int> GroundTypeGetter()
            {
                FieldInfo field = typeof(Movement).GetField("currentGroundType", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(nameof(Movement), "currentGroundType");
                ParameterExpression player = Expression.Parameter(typeof(Movement));
                return Expression.Lambda<Func<Movement, int>>(Expression.Convert(Expression.Field(player, field), typeof(int)), player).Compile();
            }



            // Key bits: y 0-15, x 16-31, dash 32-35, jump 36-39, WJ 40-43, moss 44, momentum 45-50, progress 51-61, blue 62.
            internal const int ProgressShift = 51;
            const float MomentumStep = 250f;

            internal static long Place(Simulation sim, colouredBlockSwapper swapper, bool resources, ref float behind, bool spent)
            {
                Movement player = sim.Player;
                CapturePos position = sim.PlayerAtCapture;
                long x = Mathf.FloorToInt(position.x / CellSize) & 0xFFFF, y = Mathf.FloorToInt(position.y / CellSize) & 0xFFFF;
                long key = (x << 16) | y;
                if ((object)swapper != null && swapper.isBlueActive) key |= 1L << 62;
                if (!resources) key |= Behind(player, ref behind) switch { < 0f => 1L << 45, > 0f => 1L << 46, _ => 0L };
                bool moss = GroundType(player) != 0;
                if (!moss && !resources && !spent) return key;
                long wall = Math.Clamp(player.wallJumpsLeft + 1, 0, 15), air = Math.Clamp(player.airJumpsLeft, 0, 15), dash = Math.Clamp(player.airDashesLeft, 0, 15);
                key |= (moss ? 1L << 44 : 0L) | (wall << 40) | (air << 36) | (dash << 32);
                if (!resources) return key;
                bool omni = player.omniDashUnlocked;
                return key | (MomentumBucket(player.momentum.x, omni) << 45) | (MomentumBucket(player.momentum.y, omni) << 48);
            }

            static readonly float[] OmniMomentum = { 250f, 1000f, 2500f };

            static long MomentumBucket(float m, bool omni)
            {
                float a = Math.Abs(m);
                int level = omni ? (a >= OmniMomentum[2] ? 3 : a >= OmniMomentum[1] ? 2 : a >= OmniMomentum[0] ? 1 : 0) : Math.Min(3, (int)(a / MomentumStep));
                return 3 + Math.Sign(m) * level;
            }

            static long Motion(Movement player)
            {
                float vy = player.Velocity.y;
                long band = vy < -200f ? 0 : vy < 200f ? 1 : vy < 580f ? 2 : 3;
                return (band << 47) | (player.facingRight ? 1L << 49 : 0L);
            }

            static long CellKey(long place, bool timing, int waypoints)
            {
                long key = ((long)waypoints << ProgressShift) | place;
                return timing ? -1 - key : key;
            }

            internal static long GoalCellKey(long place, int progress, bool armed)
            {
                return -1 - (((long)progress << ProgressShift) | (armed ? place ^ 1L << 31 : place));
            }

            internal static int GoalProgress(long goalKey) { return (int)(((-1 - goalKey) >> ProgressShift) & 0x7FF); }

            void FileCell(List<InputTick> inputs, int tick, long key, float rank, SimSnapshot[] states)
            {
                bool better = !_archive.TryGetValue(key, out Cell cell) || Earlier(rank, tick, cell);
                bool resumable = tick > 0 && tick < inputs.Count && !_sim.Engine.InputsExhausted;
                SimSnapshot state = resumable && (better || states != null && tick % StateEvery == 0) ? Capture(tick) : null;
                if (state != null) state.Waypoints = _waypoints;
                if (states != null && state != null) states[tick] = state;
                if (!better || state == null) return;
                float distance = _guide != null ? _guide.Distance(_sim.PlayerAtCapture, _waypoints) : _goalMode ? GoalDistance(_sim) : float.NaN;
                var filed = new Cell { Source = _runEncoded ??= InputRuns.From(inputs), Length = tick, Rank = rank, Distance = distance, Waypoints = _waypoints, Parent = _fromCell, Key = key };
                if (_owner._path != null && _owner._path.Discourage.Count > 0)
                {
                    WorldPos at = _sim.PlayerWorld;
                    filed.Discouraged = _owner._path.Discourage.Exists(b => b.Contains(at));
                }
                if (!_owner.File(key, filed)) return;
                Keep(filed, state);
                _owner.Extend(key, filed, _goalMode ? GoalProgress(key) : -1);
                if (_owner.ArchiveFull) _owner.Evict(_evictWeight);
            }
        }
    }
}
