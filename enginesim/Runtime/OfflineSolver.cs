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
                /// <summary>Run-length stored: a saved archive's 36 M ticks were 3 GB as lists (Course 9 WJ_DS_DJ_BS).</summary>
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
            /// <summary>Boxes the search is steered away from without cutting a run: an explore cell filed inside one is picked
            /// <see cref="DiscouragedWeight"/> as often (the known routes' corridors, CourseAuto's OtherWays).</summary>
            public readonly List<Box> Discourage = new List<Box>();
            /// <summary>A standing rule's "guide": true turns the guide on for every solve it covers, as --guide does.</summary>
            public bool Guide;
            public bool IsEmpty => Via.Count == 0 && Avoid.Count == 0;

            /// <summary>A run's step from <paramref name="prev"/> (<see cref="Simulation.PlayerStepFrom"/>) to <paramref name="p"/>: past
            /// the next via box it counts it; true when an avoid box cuts it (one cutting only after a goal once
            /// <paramref name="progress"/> has reached it).</summary>
            public bool Step(WorldPos prev, WorldPos p, int progress, ref int waypoints)
            {
                if (waypoints < Via.Count && Via[waypoints].Contains(p)) waypoints++;
                foreach (Box box in Avoid) if (box.Cuts(prev, p) && (box.After == 0 || progress >= box.After)) return true;
                return false;
            }
        }

        public readonly struct Box
        {
            public readonly float X0, Y0, X1, Y1;
            /// <summary>Goal mode: the box cuts a run only once it has reached this many goals (0: always).</summary>
            public readonly int After;
            /// <summary>One-way (forced-paths.json's block "from", <see cref="FullTasGoal.SideNames"/>): the box cuts only a move across
            /// that edge into it, so a direction gate cuts leaving the start its way and not a farm lap's drop back through it (0: any tick inside).</summary>
            public readonly int From;
            public Box(WorldPos a, WorldPos b, int after = 0, int from = 0) { X0 = Math.Min(a.x, b.x); Y0 = Math.Min(a.y, b.y); X1 = Math.Max(a.x, b.x); Y1 = Math.Max(a.y, b.y); After = after; From = from; }
            public bool Contains(WorldPos p) { return p.x >= X0 && p.x <= X1 && p.y >= Y0 && p.y <= Y1; }

            /// <summary>The step from <paramref name="prev"/> to <paramref name="p"/> is cut: it ends inside, or for a one-way box it
            /// crosses the entry edge inwards (<see cref="FullTasGoal.CrossesInto"/>).</summary>
            public bool Cuts(WorldPos prev, WorldPos p)
            {
                return From == 0 ? Contains(p) : FullTasGoal.CrossesInto(From, X0, Y0, X1, Y1, prev.x, prev.y, p.x, p.y);
            }

            public override string ToString()
            {
                int from = From;
                return $"{X0:0},{Y0:0}..{X1:0},{Y1:0}" + (After > 0 ? " after goal " + After : "")
                    + (from != 0 ? " from " + string.Join("+", FullTasGoal.SideNames.Where((s, i) => (from & 1 << i) != 0)) : "");
            }
        }

        public readonly string Capabilities;
        readonly Func<Simulation> _createSimulation;
        readonly List<List<InputTick>> _seeds;
        // No route yet: a plain solve's cells also key the air dashes, air jumps and wall jumps left, so an arrival that kept
        // them (a super off a ledge, its dash and air jump still to come) isn't replaced by an earlier one that spent them.
        readonly bool _fresh;
        // The loaded archive (Memory) is not kept: its runs count and plateau routes are, and its sources only until each
        // worker has restored its share (a resident 150k-cell archive held 3 GB of inputs after its cells were replaced).
        readonly long _priorRuns;
        readonly List<List<InputTick>> _plateaus;
        List<Memory.Source> _loaded;
        readonly ForcedPath _path;
        public readonly Best Shared = new Best();
        public long Runs, Ticks, Finishes, Drifts, Splices, BranchChecks, BranchMatches, StateHits, StateReplays, ReplayTicks;
        /// <summary>Grafts made from mined tech demos (0 with --no-tech-grafts), and those at a per-step tech contact
        /// (ContactLog.Step's kinds: a spring hit, an air jump, a dash jump, a wall boost, a pause, a step-up, a spike, a slant).</summary>
        public long TechGrafted, TechGraftedAtStep;
        /// <summary>Mined grafts made by a chain (PatternChain), and the chain grafts whose run was checked for / showed the
        /// contact the demo showed after it (a wall clip's landing).</summary>
        public long ChainGrafts, ExpectChecks, ExpectsMet;
        /// <summary>The tech demos mined into graft patterns (null: --no-tech-grafts), offered at the same contact kinds.</summary>
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
        // The memory the solver keeps is half of AvailableMemory: each worker's share of half of that for its states, divided
        // by what its captures allocate (measured: a state after a FloatingOrigin rebase or in a big economy is several times a
        // plain course's), and a quarter for the archive. The rest of the heap is the worlds, the replays and the GC's room.
        long _stateBudget;
        public int StatesPerWorker { get; private set; }
        int _cells;
        // The archive's bytes, counted as cells are filed, replaced and evicted (Count), its budget, what a cell costs beyond
        // its inputs, and the eviction passes made (a worker rebuilds its draw pools after one).
        long _archiveBytes, _archiveBudget, _cellBytes;
        int _evictions;
        public long EvictedCells;
        readonly object _evicting = new object();
        /// <summary>A Full-TAS run's stored goal-boundary states, resumed instead of replaying a cell's prefix over them (null: none).</summary>
        public SnapshotIndex Resume;
        /// <summary>No quick restarts: no mutation adds or moves one (a Full-TAS reset study: the way on that keeps the momentum).
        /// The seeds must carry none either. Set before the search starts.</summary>
        public bool NoResets;
        /// <summary>Cell replays that started from a stored goal state rather than a worker's own ancestor.</summary>
        public long GoalResumes;
        /// <summary>Mined patterns drawn per grafted contact.</summary>
        public int MinedDraws = 1;
        /// <summary>The share of explore runs with a contact that get a mined graft (a Full-TAS window's per-window cap).</summary>
        public double GraftRate = .5;
        /// <summary>Run input ticks (from the search's start) whose contacts a graft prefers, 3 picks in 4 (a waste audit's span); null: any.</summary>
        public (int lo, int hi)? GraftFocus;
        /// <summary>
        /// A classed Full-TAS window's price of a finished run (FullTasClasses.cs; null: runs rank by their time alone): made once per
        /// worker for its simulation (on its thread), given a run played whole from the window's start and its result, the seconds
        /// to add (+infinity: the run never counts). Such a run ranks by its time plus these, its runTicks by them in ticks (so a
        /// window's baseline compares scores); a run resumed from a state inside the window ranks by its time alone, a lower bound,
        /// and is played whole and priced before it is kept. Set before the search starts.
        /// </summary>
        public Func<Simulation, Func<List<InputTick>, RunResult, double>> Penalty;
        readonly System.Collections.Concurrent.ConcurrentDictionary<List<InputTick>, long> _penaltyTicks
            = new System.Collections.Concurrent.ConcurrentDictionary<List<InputTick>, long>(ReferenceEqualityComparer.Instance);

        /// <summary>The ticks of <see cref="Penalty"/> in a reported best's runTicks (0 without one).</summary>
        public long PenaltyTicks(List<InputTick> inputs) => _penaltyTicks.TryGetValue(inputs, out long ticks) ? ticks : 0;

        public string ArchiveStatus()
        {
            long hits = Interlocked.Read(ref StateHits), replays = Interlocked.Read(ref StateReplays), goals = Interlocked.Read(ref GoalResumes);
            return $"archive {_cells} cells ({Interlocked.Read(ref _archiveBytes) >> 20} MB" + (_evictions == 0 ? "" : $", {Interlocked.Read(ref EvictedCells)} evicted") + "), "
                + $"states {(hits + replays == 0 ? 100 : 100 * hits / (hits + replays))}% cached ({StatesPerWorker} per worker), "
                + $"{Interlocked.Read(ref ReplayTicks)} replay ticks" + (goals == 0 ? "" : $", {goals} from stored goal states");
        }

        /// <summary>How much less often a cell in a <see cref="ForcedPath.Discourage"/> box is picked.</summary>
        public const double DiscouragedWeight = .02;

        /// <summary>How hard the guide pulls explore cell picks toward the target (0: no guidance, 1: its full share).</summary>
        public readonly float GuideWeight;
        /// <summary>The search's random seed (each worker gets this + its index); 0: the clock, as ever.</summary>
        public readonly int Rng;
        float _guideBest = float.PositiveInfinity;

        /// <summary>The progress line's guide fragment: how close the closest cell filed has walked to the target.</summary>
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

        public Thread[] Start(int workers)
        {
            var threads = new Thread[workers];
            _workers = new Worker[workers];
            long available = AvailableMemory();
            _stateBudget = available / 2 / workers;
            StatesPerWorker = (int)Math.Max(512, _stateBudget / Worker.StartStateBytes);
            _archiveBudget = available / 4;
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

        /// <summary>The memory the states may take half of: the machine's physical memory, or the GC's limit when that is lower.
        /// (The GC reports its heap hard limit alone when one is set, as the CLI's 70 GiB is, however much RAM there is.)</summary>
        static long AvailableMemory()
        {
            long limit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, ram = PhysicalMemory();
            return ram > 0 ? Math.Min(limit, ram) : limit;
        }

        /// <summary>The machine's physical memory in bytes, 0 when it can't be read.</summary>
        static long PhysicalMemory()
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status) != 0 ? (long)status.TotalPhys : 0;
            }
            // "MemTotal:       65756408 kB"
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
            // Trimmed without a copy (Take shares the runs): a copy of every source's inputs here ran the CLI out of memory.
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

        /// <summary>
        /// Every worker's visited cells: while they run, the copies each made of its own at the last <see cref="Export"/>; once
        /// stopped (and joined), their maps themselves.
        /// </summary>
        public Dictionary<long, float> Visits()
        {
            var all = new Dictionary<long, float>();
            foreach (Worker w in _workers ?? Array.Empty<Worker>())
                if ((_stop ? w?.Visits : w?.LastHandover?.Visits) is Dictionary<long, float> visits)
                    foreach (var v in visits)
                        if (!all.TryGetValue(v.Key, out float t) || v.Value < t) all[v.Key] = v.Value;
            return all;
        }

        /// <summary>
        /// The inputs without each pause the run doesn't need: <paramref name="kept"/> plays them with that pause dropped and says
        /// whether the run is no worse (then it stays dropped). A pause's frames aren't physics ticks, so a record's ranking can't
        /// see one that lifts nothing (a pauselift graft left on a tick where it does nothing), yet 1x opens the menu for them.
        /// </summary>
        public static List<InputTick> DropIdlePauses(List<InputTick> inputs, Func<List<InputTick>, bool> kept)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].pause <= 0) continue;
                var without = new List<InputTick>(inputs);
                InputTick k = without[i];
                k.pause = k.pauseTick = k.pauseFrame = 0;
                without[i] = k;
                if (kept(without)) inputs = without;
            }
            return inputs;
        }

        public static bool Beats(float time, long runTicks, float bestTime, long bestRunTicks)
        {
            return time < bestTime - .0001f || (time <= bestTime + .0001f && runTicks < bestRunTicks);
        }

        /// <summary>A farm search (a farm loop's lap, README ### Rules 30): finishes rank by live time from the search's start
        /// (runTicks is then MacroEngine.LiveUnits of the run's physics ticks and held frames), the course timer breaking a tie.
        /// Set before the search starts.</summary>
        public bool Farm;

        /// <summary>The ranking the search keeps its best by: <see cref="Beats"/>, or by runTicks first in a <see cref="Farm"/> search.</summary>
        bool Ranks(float time, long runTicks, float bestTime, long bestRunTicks)
        {
            return Farm ? runTicks < bestRunTicks || (runTicks == bestRunTicks && time < bestTime - .0001f) : Beats(time, runTicks, bestTime, bestRunTicks);
        }

        // The bests taken, in order, waiting for Improved: queued under Shared's lock as Shared takes them, raised outside it.
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
                if (Penalty != null) _penaltyTicks[inputs] = penaltyTicks;
                _reports.Enqueue((time, runTicks, inputs, how));
            }
            Report();
            return true;
        }

        // Improved's handlers write files and the console: they run outside Shared's lock (workers keep adopting and offering),
        // one at a time and in the order the bests were taken. A worker that finds another reporting leaves its best to it: the
        // reporter looks at the queue again after letting go.
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
            /// <summary>The inputs of the run that filed it (shared by every cell that run filed); the cell is their first
            /// Length ticks. Run-length stored: as lists, the cells' inputs outgrew the states on Area 2's long routes.</summary>
            public InputRuns Source;
            public int Length, Picks, Waypoints;
            /// <summary>Filed inside one of the run's <see cref="ForcedPath.Discourage"/> boxes.</summary>
            public bool Discouraged;
            public Cell Parent;
            public float Rank, Distance;
            /// <summary>Its map cell: what a state's eviction weighs (the draw pool's pick weight).</summary>
            public long Key;
            /// <summary>The worker that last kept a state for it (filed it, or replayed to it): the one whose own draws it is in.
            /// Written by any worker, read racily by the rest; a stale read only puts a draw in the other pool.</summary>
            public int Owner;
            /// <summary>Replaced or evicted from the archive: an eviction pass takes it out of its descendants' Parent chains, so
            /// it and its inputs can go.</summary>
            public volatile bool Removed;
        }

        /// <summary>What an archive cell costs beyond its inputs: the Cell, its dictionary node and bucket, and its slots in each
        /// worker's four draw pool arrays (Pool: the cells and their cumulative weights, the own ones and theirs; grown by
        /// doubling, so ~1.5 slots a cell).</summary>
        static long CellBytes(int workers) { return 80 + 56 + 8 + workers * 4 * 12; }

        /// <summary>Counts a cell into (<paramref name="sign"/> 1) or out of (-1) the archive's bytes: the cell, and its inputs
        /// while it is their first cell in the archive (a run's cells share its inputs).</summary>
        void Count(Cell cell, int sign)
        {
            long bytes = _cellBytes;
            int filed = Interlocked.Add(ref cell.Source.Cells, sign);
            if (sign > 0 ? filed == 1 : filed == 0) bytes += cell.Source.Bytes;
            Interlocked.Add(ref _archiveBytes, sign * bytes);
        }

        /// <summary>The archive is over its budget: an eviction is due (<see cref="Evict"/>).</summary>
        bool ArchiveFull => Volatile.Read(ref _archiveBytes) > _archiveBudget;

        /// <summary>
        /// Takes the archive back to 7/8 of its budget: removes its least useful cells, those with the lowest pick weight
        /// (<paramref name="weight"/>: the calling worker's, which is also what its state eviction weighs), and then takes the
        /// removed cells (these and the ones replaced since the last pass) out of every live cell's Parent chain, so nothing
        /// but a worker's state or draw pool keeps them. One worker evicts at a time; the others carry on filing.
        /// </summary>
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

        /// <summary>Goal mode: from the player to the next at: goal's rect, in the capture frame; NaN for any other next goal.
        /// With guidance on the guide's walkable distance instead of the straight line.</summary>
        public static float GoalDistance(Simulation sim)
        {
            FullTasGoal goal = sim.Goals.Current;
            if (goal == null || goal.kind != FullTasGoal.At) return float.NaN;
            CapturePos p = sim.PlayerAtCapture;
            if (DistanceField.Enabled) return DistanceField.For(sim).Distance(p);
            float dx = Math.Max(0f, Math.Abs(p.x - goal.x) - goal.w / 2f), dy = Math.Max(0f, Math.Abs(p.y - goal.y) - goal.h / 2f);
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        sealed record ClosestCell(Cell Cell, int Progress);
        volatile ClosestCell _closest;

        // Goal mode: the cell with the most goals reached, then nearest the next at: goal (no distance counts as farthest),
        // then earliest.
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

        /// <summary>
        /// Goal mode: the search's attempt furthest toward the goals so far (most goals reached, then nearest the next area
        /// goal), as the window's inputs up to that cell; null before any. Changes whenever a closer cell is filed.
        /// </summary>
        public Attempt Closest()
        {
            ClosestCell c = _closest;
            return c == null ? null : new Attempt { Progress = c.Progress, Distance = c.Cell.Distance, Rank = c.Cell.Rank,
                Inputs = c.Cell.Source.ToList(c.Cell.Length) };
        }

        /// <summary>
        /// Goal mode: the window's inputs to up to <paramref name="max"/> filed cells whose goals reached pass
        /// <paramref name="progress"/> and whose square is within <paramref name="dx"/>, <paramref name="dy"/> units of the
        /// capture-frame point's, nearest first. Cells in <paramref name="skip"/> are left out; the ones returned are added to it.
        /// </summary>
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
            // Each worker keeps only the states it made (a snapshot holds its own world's objects), and a cell it lacks one
            // for is replayed. So a worker draws mostly from the cells it owns (Cell.Owner: it kept their states last), and an
            // explore draws up to this many cells and takes the first it has a state for.
            const int CachedDraws = 3;
            const int BranchCheckEvery = 10000;
            readonly OfflineSolver _owner;
            /// <summary>This worker's index: the Cell.Owner of the cells it keeps states for.</summary>
            readonly int _index;
            readonly Simulation _sim;
            /// <summary>Decodes this world's copy of a stored goal state (SnapshotStore.StateOf).</summary>
            readonly SnapshotStore _store;
            readonly colouredBlockSwapper _swapper;
            readonly System.Random _rng;
            readonly Queue<List<InputTick>> _seeds;
            readonly System.Collections.Concurrent.ConcurrentDictionary<long, Cell> _archive;
            readonly Dictionary<Cell, Entry> _states = new Dictionary<Cell, Entry>();
            // Eviction is by the draw pool's pick weight first and recency second, so the frontier's hot cells stay through
            // a wide draw's churn (plain LRU let them go). Every touch queues a record; one whose use isn't its entry's
            // latest (touched since, or superseded) is stale and skipped, and the queue is rebuilt from the live entries once
            // it holds four records for each.
            readonly PriorityQueue<Entry, (double weight, long use)> _evict = new PriorityQueue<Entry, (double, long)>();
            long _use;
            int _cap;

            sealed class Entry
            {
                public Cell Cell;
                public SimSnapshot State;
                /// <summary>When it was last touched; 0 once a newer entry for its cell replaces it.</summary>
                public long Use;
                /// <summary>Its pick weight when last touched: its eviction priority, with Use.</summary>
                public double Weight;
            }
            // The draw pools, rebuilt by Pool: every filed cell, and the ones this worker owns, each with its cumulative pick
            // weights; an own draw is taken with probability _ownShare.
            Cell[] _pool = Array.Empty<Cell>(), _ownPool = Array.Empty<Cell>();
            double[] _poolWeights = Array.Empty<double>(), _ownWeights = Array.Empty<double>();
            int _poolCount, _ownCount, _poolPicks;
            // The archive's eviction passes as of the last Pool: an eviction since makes it rebuild them without the cells gone.
            int _poolEvictions;
            // The archive's eviction weighs cells as this worker's state eviction does (Weight, no guide closeness).
            readonly Func<long, Cell, double> _evictWeight;
            double _ownShare;
            readonly bool _dash, _omni;
            float _best = float.PositiveInfinity;
            long _bestRunTicks = long.MaxValue;
            List<InputTick> _bestInputs;
            Replay _bestRun;
            int _sharedVersion;

            // A run replayed with its record: per tick the states kept, the body, fingerprint and waypoints (convergence), the
            // cell key and time; per cell the last arrival (segment cuts) and the first (splices).
            sealed class Replay
            {
                public readonly SimSnapshot[] States;
                public readonly (float, float, float, float, float, float)[] Body;
                public readonly int?[] Fingerprint;
                public readonly int[] Waypoints;
                public readonly long[] Keys;
                public readonly float[] Elapsed;
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
                    Array.Fill(Elapsed, float.NaN);
                }

                /// <summary>Ticks 0..through as <paramref name="best"/> had them: a run resumed from its state there matches it up to it.</summary>
                public void CopyPrefix(Replay best, int through)
                {
                    int n = through + 1;
                    Array.Copy(best.States, States, n);
                    Array.Copy(best.Body, Body, n);
                    Array.Copy(best.Fingerprint, Fingerprint, n);
                    Array.Copy(best.Waypoints, Waypoints, n);
                    Array.Copy(best.Keys, Keys, n);
                    Array.Copy(best.Elapsed, Elapsed, n);
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
                // A run that may quick-restart (a Full-TAS window, resets allowed) also grafts the mined windows that restart.
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
                if (_goalMode && !owner.NoResets) _kinds = _kinds.Concat(new[] { "reset", "noreset", "movereset" }).ToArray();
                if (_omni) _kinds = _kinds.Concat(OmniKinds).ToArray();
            }

            public readonly Dictionary<long, float> Visits;
            readonly string[] _kinds;

            public void Loop()
            {
                while (!_owner._stop)
                {
                    AdoptShared();
                    int generation = _owner._exportGeneration;
                    if (Exported != generation) HandOver(generation);
                    if (_seeds.Count > 0) { Try(_seeds.Dequeue(), "seed"); continue; }
                    if (_restore.Count > 0)
                    {
                        Restore(_restore.Dequeue());
                        // Its share restored, the loaded sources can go: the cells filed from them hold what they still need.
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
                // Shared.Version only moves under the lock, after the rest: an unchanged one skips the lock on every loop.
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
                // Contacts are graft sites only for the mined windows: with --no-tech-grafts there is nothing to record.
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
                // _spliceBestTick is a tick of the best this run was judged against; a new best from Try makes it stale.
                if (_spliceAt > chosen.Length && ReferenceEquals(spliceInto, _bestInputs)) Splice(inputs, _spliceAt, _spliceBestTick, from);
                if (graft is var (tick, kind)) Graft(inputs, tick, kind, lo, from);
                _fromCell = null;
            }

            // Tech lives on the ticks where the player's contact changes, and a random burst puts its jump and dash anywhere in
            // its chunks, so it seldom lands them there. An explore run's contacts are recorded (ContactLog: it leaves the ground
            // at a ledge, lands, touches or leaves a wall, dies; a flat hyper dash slides off a corner; a wall behind, ahead or
            // above the back; the start gate in reach before the timer; and the per-step kinds), and a graft replaces the run from
            // one of them with a mined tech demo's input window at the same contact kind (TechGrafts): the Tech tab's demos are
            // the graft library, with the same detection replayed over them.
            readonly ContactLog _log = new ContactLog();
            readonly ContactProbe _gate;
            List<(int tick, Contact kind)> _contacts => _log.Contacts;
            bool _recordContacts;

            void RecordContact(int tick) { _log.Tick(tick, _sim.Player, _gate.NearGate()); }

            // Every physics step of an explore run (a dash runs ticks with no input tick, which RecordContact never sees): the
            // input tick a dash was pressed on, and a CornerHyper contact there when its flat hyper dash leaves the floor.
            void RecordSlide() { _log.Slide((int)_sim.Engine.RunTicks, _sim.Player); }

            // The per-step tech contacts (ContactLog.Step: a spring hit, an air jump, a dash jump, a wall boost, a landing jumped
            // out of at once, a pause, a step-up, a spike touched or left, an overhang or slope touched).
            void RecordStep() { _log.Step(_sim); }

            /// <summary>A contact of the run to graft at, uniformly among the kinds the mined tech windows offer at this run's
            /// unlocks; with a <see cref="GraftFocus"/>, among those in it 3 picks in 4 (any when none is).</summary>
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

            // The demo's own input window around the same contact kind, placed and sampled as its graft spec says and filtered by
            // the run's category unlocks. Its mode names the tech, so a new best says which demo's window found it.
            void Graft(List<InputTick> run, int tick, Contact contact, int lo, SimSnapshot from)
            {
                for (int draw = 0; draw < _owner.MinedDraws; draw++)
                {
                    if (_owner.Mined.Pick(contact, _rng, _resets) is not TechPattern pattern) break;
                    if (contact >= Contact.SpringHitLeft) Interlocked.Increment(ref _owner.TechGraftedAtStep);
                    MinedGraft(_owner.Mined, run, tick, lo, pattern, from, 0, null);
                }
            }

            // One mined graft at a contact: the pattern composed onto the run (its Tries times, each sampled anew) and run. A
            // pattern with a chain (its own, else the one it was chained from) records its run's contacts, and the first contact of
            // a chained kind at least 2 ticks after its placement gets the next graft, up to the chain's depth, 85% of the time;
            // with an expected contact only when the run shows it where the demo did (the graft worked: a wall clip grounded the
            // player). An explore cell keeps only the earliest arrival in its square, so the slow states between a chain's grafts
            // (a set-up drifting to a stop by a wall, the rise between two clips) seldom survive to be explored from later: chaining
            // finds a climb of several clips (Course 1 NONE's pillar and shelf: four) in one explore step.
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
                    // After an eviction pass no live cell's Parent chain holds a removed cell: their states would only wait
                    // for the state eviction (on a 40 MB archive budget they were half the cache).
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
                // The guide's closeness to the target: 1 for the pool's closest cell to it, 0 for cells a span of distance
                // further out (the span capped, so one cell that wandered off doesn't flatten the rest).
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
                // A worker's own draws come in proportion to the pick weight it owns (OwnShare of its draws when every worker
                // owns an even share), the rest from every cell, so summed over the workers each cell is drawn near its share
                // of the whole pool, as one global pool drew it, while most draws land on cells this worker has states for.
                // At most MostOwnShare, so the others' cells are drawn here too and a cell owned by a busy worker doesn't
                // starve; none when this worker owns every cell (the own pool is the whole pool).
                _ownShare = owned == 0 || owned == count ? 0 : Math.Min(MostOwnShare, OwnShare * _owner._workers.Length * own / total);
                _poolPicks = Math.Max(PoolPicks, count / 256);
            }
            const int PoolPicks = 16;
            const double OwnShare = .9, MostOwnShare = .95;
            /// <summary>The distance the guide's closeness fades out over.</summary>
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
                // The run's stored goal state may sit later on the cell's own prefix than this worker's nearest state does.
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
                long before = GC.GetAllocatedBytesForCurrentThread();
                SimSnapshot state = _sim.Capture(tick);
                _stateBytes += (GC.GetAllocatedBytesForCurrentThread() - before - _stateBytes) / 64;
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
                if (t.Count <= at) return;
                InputTick k = t[at], before = t[at - 1];
                if (k.held && !before.held && before.dashJump == 0) k.press = true;
                if (!k.held) k.release = before.held;
                if (k.press && before.held)
                {
                    if (before.press || at - 1 <= from.Tick) return;
                    before.held = false; before.release = true; t[at - 1] = before;
                }
                t[at] = k;
                Interlocked.Increment(ref _owner.Splices);
                Try(t, "splice", from);
            }

            /// <summary>An explore cell's share of the picks: timed cells over untimed, ones further toward the run's goal (its
            /// way through timed runs, its goal reach, its forced-path boxes), and by the guide ones closer to the target,
            /// then the pick-count novelty that keeps it exploring. <paramref name="closeness"/> is 1 for the pool's closest
            /// cell to the target and 0 for its far ones.</summary>
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
                            // A super's height is how long its jump is held: bonus15's gap takes 24+ inputs.
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
                        // Freeze aim: the dash pressed with the stick at rest, then the stick 0.35 sideways and full up (or
                        // down) in its freeze. The sideways part isn't snapped, so it goes 19° off vertical, which no dash
                        // pressed on a tick can (MacroEngine.StickFor).
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
                        // Super ground bounce: a ground super (flat dash, jump in it), then the dash straight down into the ground and its jump.
                        OmniDash(t, at, side, 0f, _rng.Next(1, 7), side);
                        return DashBounce(t, at + 1 + _rng.Next(4, 20), side);
                    case "bouncechain":
                    {
                        // The down-dash landing chain (the old 34.88 s VMAN route over goals 15-17): two hypers (an ultra: each
                        // adds to the momentum, 876 -> 1609 -> 2929 there), then a dash straight down onto the floor and a jump
                        // out of the landing, 2-5 times, 3-11 ticks of air between. A jump off the ground keeps the momentum and
                        // adds half again, more than the air takes (2528 to 5832 over four landings). One dash bounce at a time
                        // the search had to find each link as its own step, and never kept the chain; from 44.94's state before
                        // goal 15, 600 random chains of this shape reached 1000 units further in 110 ticks than the route did.
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

            /// <summary>Holds a dash jump's jump for <paramref name="hold"/> inputs after the dash at <paramref name="at"/>, then releases it.</summary>
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

            /// <summary>A random tick of <paramref name="t"/> that matches; -1 for none.</summary>
            int Pick(List<InputTick> t, Func<InputTick, bool> match)
            {
                int count = 0;
                foreach (InputTick k in t) if (match(k)) count++;
                if (count == 0) return -1;
                for (int i = 0, skip = _rng.Next(count); ; i++) if (match(t[i]) && skip-- == 0) return i;
            }

            void Optimize()
            {
                List<InputTick> t = _bestInputs.ToList();
                int n = t.Count, p = _rng.Next(n);
                string kind = OneOf(_kinds);
                switch (kind)
                {
                    case "aim": case "steep":
                    {
                        int dash = Pick(t, IsDash);
                        Omni(t, dash >= 0 && _rng.NextDouble() < .7 ? dash : p, 0, kind);
                        break;
                    }
                    case "wavedash": case "ultra": case "dashbounce": case "wavebounce": case "wavejump": case "superground":
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
                        // A dash with no sideways stick, turned by the stick held in its freeze (MacroEngine.StickFor). With the stick
                        // straight down from a wall cling, turned toward the wall, it is the Inwall wallbounce's climbing dash.
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
                if (from != null && from.Tick >= inputs.Count) from = null;
                // A priced window keeps a run slower to a cell than the best: it may be cleaner (the segment cut compares times alone).
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
                if (finished) Interlocked.Increment(ref _owner.Finishes);
                bool drift = (mode == "optimize" && converged) || ((mode == "optimize" || mode == "plateau") && finished && !_owner.Ranks(_best, _bestRunTicks, time, runTicks));
                if (!drift && (!finished || !_owner.Ranks(time, runTicks, _best, _bestRunTicks))) return;
                // An optimize run starts from a state of the best with the best's inputs before it, so its replay starts there too;
                // a priced window's replay is the whole run, which is what the price is of.
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

            /// <summary>Plays a run. With a <see cref="OfflineSolver.Penalty"/>, a finished run played whole (no <paramref name="from"/>,
            /// judged throughout) has its price in its time and runTicks (<see cref="_pricedTicks"/> the ticks), or doesn't finish.</summary>
            /// <param name="encoded">The inputs already run-length stored (a restored source, a cell's replay): the cells this run
            /// files share it. Otherwise they're stored at the run's first filed cell (a run never changes its inputs).</param>
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

            // The run Evaluate is on, for its step and tick callbacks: the delegates are made once, so a run allocates no
            // closure. (Evaluate never runs inside one of its own callbacks.)
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
                if (path.Step(_sim.PlayerStepFrom, p, _sim.Progress, ref _waypoints)) _avoided = true;
                if (_waypoints > before && _waypoints > _owner.MostWaypoints)
                    lock (_owner.Shared) _owner.MostWaypoints = Math.Max(_owner.MostWaypoints, _waypoints);
            }

            bool AfterTick(int tick)
            {
                List<InputTick> inputs = _runInputs;
                Replay replay = _runReplay;
                _endTick = tick;
                // The tick's WallBehind probe (NaN: not cast yet), cast once for the contacts and the cell key.
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
                long key = _goalMode ? GoalCellKey(Place(_sim, _swapper, true, ref behind, false), _sim.Progress)
                    : CellKey(Place(_sim, _swapper, false, ref behind, _owner._fresh) | (timing ? 0L : Motion(_sim.Player)), timing, _waypoints);
                bool losing = false;
                float elapsed = _sim.Elapsed(active);
                int filed = Math.Min(tick, inputs.Count);
                if (recorded) { replay.Keys[tick] = key; replay.Elapsed[tick] = elapsed; }
                replay?.Record(filed, key, elapsed);
                if (elapsed >= 0f)
                {
                    if (tick > _runJudgeAfter && elapsed > _best) losing = true;
                    if (_runCut && tick > _runJudgeAfter && _bestRun.Arrival.TryGetValue(key, out float bestAt) && elapsed > bestAt + SegmentSlack) losing = true;
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

            // Before the timer a course run sets its start up, and no saved route's set-up strays more than 1626 u (x or y) from
            // the start gate (3_WJ_DS_DJ 2.76's flying start). Past SetupReach it is wandering the world: unseeded, Course 7
            // WJ_DS_DJ_BS filed 131k of its 158k untimed cells there, out to 8600 u, and never finished.
            const float SetupReach = 2500f;
            readonly WorldPos[] _gates;

            bool OutOfSetupReach()
            {
                if (_gates == null || _gates.Length == 0) return false;
                WorldPos p = _sim.PlayerWorld;
                foreach (WorldPos g in _gates)
                    if (Math.Max(Math.Abs(p.x - g.x), Math.Abs(p.y - g.y)) <= SetupReach) return false;
                return true;
            }

            /// <summary>The tick's <see cref="ContactRays.WallBehind"/>, cast on the first ask (<paramref name="behind"/> NaN).</summary>
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



            // Cell key bits: y 0-15 and x 16-31 (48-unit cells, wrapped), air dashes 32-35, air jumps 36-39, wall jumps 40-43,
            // moss 44, momentum x 45-47 and y 48-50 (a course: a wall clip's wall behind 45-46, and before the timer the Motion
            // bits 47-49), waypoints or goal progress 51-61, blue blocks 62.
            internal const int ProgressShift = 51;
            const float MomentumStep = 250f;

            static long Place(Simulation sim, colouredBlockSwapper swapper, bool resources, ref float behind, bool spent)
            {
                Movement player = sim.Player;
                CapturePos position = sim.PlayerAtCapture;
                long x = Mathf.FloorToInt(position.x / CellSize) & 0xFFFF, y = Mathf.FloorToInt(position.y / CellSize) & 0xFFFF;
                long key = (x << 16) | y;
                if ((object)swapper != null && swapper.isBlueActive) key |= 1L << 62;
                // A clip-ready state (a wall at the back, in reach of a wall clip graft) is its own cell: the earliest arrival in a
                // square is seldom the slow one that stopped next to the wall, and only that one clips.
                if (!resources) key |= Behind(player, ref behind) switch { < 0f => 1L << 45, > 0f => 1L << 46, _ => 0L };
                bool moss = GroundType(player) != 0;
                if (!moss && !resources && !spent) return key;
                long wall = Math.Clamp(player.wallJumpsLeft + 1, 0, 15), air = Math.Clamp(player.airJumpsLeft, 0, 15), dash = Math.Clamp(player.airDashesLeft, 0, 15);
                key |= (moss ? 1L << 44 : 0L) | (wall << 40) | (air << 36) | (dash << 32);
                if (!resources) return key;
                // A superjump or spike super carries its momentum across the next hundred ticks; an arrival that has it and
                // one that doesn't are different states, so neither replaces the other.
                bool omni = player.omniDashUnlocked;
                return key | (MomentumBucket(player.momentum.x, omni) << 45) | (MomentumBucket(player.momentum.y, omni) << 48);
            }

            // With the omnidirectional dash, momentum from chained hypers and down-dash landings reaches thousands (VMAN goals
            // 15-17: 2528 to 5832 on the old 34.88 s route, against 764-2669 for a hyper or two), and every one past 750 fell
            // in one bucket, so the slow hyper's earlier arrival replaced the chain's. There the steps are 250, 1000 and 2500.
            static readonly float[] OmniMomentum = { 250f, 1000f, 2500f };

            static long MomentumBucket(float m, bool omni)
            {
                float a = Math.Abs(m);
                int level = omni ? (a >= OmniMomentum[2] ? 3 : a >= OmniMomentum[1] ? 2 : a >= OmniMomentum[0] ? 1 : 0) : Math.Min(3, (int)(a / MomentumStep));
                return 3 + Math.Sign(m) * level;
            }

            // Before the timer (a course's untimed set-up), where the player is going is part of the cell: vertical speed in
            // four bands (falling, near the top, rising, just jumped) and the facing, bits 47-49. A set-up is slow on purpose
            // (Course 1 NONE's climb to the late gate touch), so its states would lose every square to an earlier arrival
            // going the other way.
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

            static long GoalCellKey(long place, int progress)
            {
                return -1 - (((long)progress << ProgressShift) | place);
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
