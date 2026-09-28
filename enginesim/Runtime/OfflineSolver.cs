using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using IGTAPTasMod;
using UnityEngine;

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
                public List<InputTick> Inputs;
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
            public bool IsEmpty => Via.Count == 0 && Avoid.Count == 0;
        }

        public readonly struct Box
        {
            public readonly float X0, Y0, X1, Y1;
            /// <summary>Goal mode: the box cuts a run only once it has reached this many goals (0: always).</summary>
            public readonly int After;
            public Box(float x0, float y0, float x1, float y1, int after = 0) { X0 = Math.Min(x0, x1); Y0 = Math.Min(y0, y1); X1 = Math.Max(x0, x1); Y1 = Math.Max(y0, y1); After = after; }
            public bool Contains(Vector3 p) { return p.x >= X0 && p.x <= X1 && p.y >= Y0 && p.y <= Y1; }
            public override string ToString() { return $"{X0:0},{Y0:0}..{X1:0},{Y1:0}" + (After > 0 ? " after goal " + After : ""); }
        }

        public readonly string Capabilities;
        readonly Func<Simulation> _createSimulation;
        readonly List<List<InputTick>> _seeds;
        readonly Memory _memory;
        readonly ForcedPath _path;
        public readonly Best Shared = new Best();
        public long Runs, Ticks, Finishes, Drifts, Splices, Grafts, CornerGrafts, ClipGrafts, ClipsLanded, SetupGrafts, SetupsLanded, GateTaps, BranchChecks, BranchMatches, StateHits, StateReplays, ReplayTicks;
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
        // Each worker's share of half the memory for its states, divided by what its captures allocate (measured: a state
        // after a FloatingOrigin rebase or in a big economy is several times a plain course's).
        long _stateBudget;
        public int StatesPerWorker { get; private set; }
        int _cells;

        public string ArchiveStatus()
        {
            long hits = Interlocked.Read(ref StateHits), replays = Interlocked.Read(ref StateReplays);
            return $"archive {_cells} cells, states {(hits + replays == 0 ? 100 : 100 * hits / (hits + replays))}% cached ({StatesPerWorker} per worker), "
                + $"{Interlocked.Read(ref ReplayTicks)} replay ticks";
        }

        public OfflineSolver(Func<Simulation> createSimulation, string capabilities, List<List<InputTick>> seeds, Memory memory = null, ForcedPath path = null,
            float visitCell = 0f)
        {
            _createSimulation = createSimulation;
            Capabilities = capabilities;
            _seeds = seeds;
            _memory = memory;
            _path = path != null && !path.IsEmpty ? path : null;
            VisitCell = visitCell;
        }

        public void Stop() { _stop = true; }

        public Thread[] Start(int workers)
        {
            var threads = new Thread[workers];
            _workers = new Worker[workers];
            _stateBudget = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2 / workers;
            StatesPerWorker = (int)Math.Max(512, _stateBudget / Worker.StartStateBytes);
            _partitions = Enumerable.Range(0, workers).Select(i => _memory == null ? new List<Memory.Source>()
                : _memory.Sources.Where((s, k) => k % workers == i).ToList()).ToArray();
            for (int i = 0; i < workers; i++)
            {
                int index = i;
                int seed = Environment.TickCount * 31 + i * 7919;
                var seeds = i == 0 ? _seeds : new List<List<InputTick>>();
                List<InputTick> plateau = _memory != null && _memory.Plateau.Count > 0 ? _memory.Plateau[i % _memory.Plateau.Count] : null;
                threads[i] = new Thread(() =>
                {
                    var worker = new Worker(this, _createSimulation(), seed, seeds, _partitions[index], plateau);
                    _workers[index] = worker;
                    worker.Loop();
                }) { IsBackground = true, Name = "solver-" + i };
                threads[i].Start();
            }
            return threads;
        }

        public Memory Export(int timeoutMs)
        {
            int generation = ++_exportGeneration;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs && _workers.Any(w => w == null || w.Exported < generation)) Thread.Sleep(10);
            var cells = new Dictionary<long, (List<InputTick> source, int length, int picks, float rank)>();
            void Add(long key, List<InputTick> source, int length, int picks, float rank)
            {
                if (!cells.TryGetValue(key, out var c)) { cells[key] = (source, length, picks, rank); return; }
                bool better = rank < c.rank || (rank == c.rank && length < c.length);
                cells[key] = better ? (source, length, c.picks + picks, rank) : (c.source, c.length, c.picks + picks, c.rank);
            }
            var memory = new Memory { Runs = (_memory?.Runs ?? 0) + Interlocked.Read(ref Runs) };
            foreach (var pair in _archive) Add(pair.Key, pair.Value.Source, pair.Value.Length, pair.Value.Picks, pair.Value.Rank);
            for (int i = 0; i < _workers.Length; i++)
            {
                Worker.Handover h = _workers[i]?.LastHandover;
                foreach (Memory.Source s in h?.Pending ?? _partitions[i])
                    foreach (Memory.SavedCell c in s.Cells) Add(c.Key, s.Inputs, c.Length, c.Picks, c.Rank);
                if (h?.Plateau != null && !memory.Plateau.Any(p => p.SequenceEqual(h.Plateau))) memory.Plateau.Add(h.Plateau);
            }
            var sources = new Dictionary<List<InputTick>, Memory.Source>(ReferenceEqualityComparer.Instance);
            foreach (var pair in cells)
            {
                if (!sources.TryGetValue(pair.Value.source, out Memory.Source s)) sources[pair.Value.source] = s = new Memory.Source { Inputs = pair.Value.source };
                s.Cells.Add(new Memory.SavedCell { Key = pair.Key, Length = pair.Value.length, Picks = pair.Value.picks, Rank = pair.Value.rank });
            }
            foreach (Memory.Source s in sources.Values)
                s.Inputs = s.Inputs.GetRange(0, Math.Min(s.Inputs.Count, s.Cells.Max(c => c.Length) + 1));
            memory.Sources = sources.Values.ToList();
            return memory;
        }

        public readonly float VisitCell;

        public static long VisitKey(Vector3 world, float cell)
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

        public static bool Beats(float time, long runTicks, float bestTime, long bestRunTicks)
        {
            return time < bestTime - .0001f || (time <= bestTime + .0001f && runTicks < bestRunTicks);
        }

        bool Offer(float time, long runTicks, List<InputTick> inputs, string how)
        {
            lock (Shared)
            {
                if (!Beats(time, runTicks, Shared.Time, Shared.RunTicks)) return false;
                Shared.Time = time;
                Shared.RunTicks = runTicks;
                Shared.Inputs = inputs;
                Shared.Version++;
                Improved?.Invoke(time, runTicks, inputs, how);
            }
            return true;
        }

        sealed class Cell
        {
            public List<InputTick> Source;
            public int Length, Picks, Waypoints;
            public Cell Parent;
            public float Rank, Distance;
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
                    return true;
                }
                if (!Earlier(cell.Rank, cell.Length, old)) return false;
                if (_archive.TryUpdate(key, cell, old)) return true;
            }
        }

        void Extend(long key, Cell cell, int progress)
        {
            float rank = cell.Rank, distance = cell.Distance;
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

        /// <summary>Goal mode: from the player to the next at: goal's rect, in the capture frame; NaN for any other next goal.</summary>
        public static float GoalDistance(Simulation sim)
        {
            FullTasGoal goal = sim.Goals.Current;
            if (goal == null || goal.kind != FullTasGoal.At) return float.NaN;
            Vector3 p = sim.PlayerAtCapture;
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
                Inputs = c.Cell.Source.GetRange(0, Math.Min(c.Cell.Length, c.Cell.Source.Count)) };
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
                return n.cell.Source.GetRange(0, Math.Min(n.cell.Length, n.cell.Source.Count));
            }).ToList();
        }

        sealed class Worker
        {
            internal const float CellSize = 48f;
            const float SegmentSlack = .2f;
            const int StateEvery = 16;
            // Each worker keeps only the states it made (a snapshot holds its own world's objects), and a cell it lacks one
            // for is replayed. An explore draws up to this many cells and takes the first it has a state for.
            // ponytail: favours each worker's own cells; draw 1 if the search narrows.
            const int CachedDraws = 3;
            const int BranchCheckEvery = 10000;
            readonly OfflineSolver _owner;
            readonly Simulation _sim;
            readonly colouredBlockSwapper _swapper;
            readonly System.Random _rng;
            readonly Queue<List<InputTick>> _seeds;
            readonly System.Collections.Concurrent.ConcurrentDictionary<long, Cell> _archive;
            readonly Dictionary<Cell, LinkedListNode<(Cell cell, SimSnapshot state)>> _states = new Dictionary<Cell, LinkedListNode<(Cell, SimSnapshot)>>();
            readonly LinkedList<(Cell cell, SimSnapshot state)> _recent = new LinkedList<(Cell, SimSnapshot)>();
            Cell[] _pool = Array.Empty<Cell>();
            double[] _poolWeights = Array.Empty<double>();
            int _poolPicks;
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
                _bestInputs = inputs.Take(finishTick + 1).ToList();
                _bestRun = replay;
                _spliceScan = true;
            }
            readonly bool _goalMode;
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

            public Worker(OfflineSolver owner, Simulation sim, int seed, List<List<InputTick>> seeds, List<Memory.Source> restore, List<InputTick> plateau)
            {
                _owner = owner;
                _sim = sim;
                _rng = new System.Random(seed);
                _seeds = new Queue<List<InputTick>>(seeds.Select(s => s.ToList()));
                _seeds.Enqueue(new List<InputTick>());
                _restore = new Queue<Memory.Source>(restore);
                _plateau = plateau?.ToList();
                _omni = owner.Capabilities == "omni" || owner.Capabilities == "wjdsdjbsom";
                _dash = _omni || owner.Capabilities == "wjds" || owner.Capabilities == "wjdsdj" || owner.Capabilities == "wjdsdjbs";
                _goalMode = sim.Goals != null;
                if (_goalMode) Interlocked.CompareExchange(ref owner._near, Enumerable.Repeat(float.PositiveInfinity, sim.Goals.Goals.Length + 1).ToArray(), null);
                _archive = owner._archive;
                _swapper = sim.World.All<colouredBlockSwapper>().FirstOrDefault();
                if (owner.VisitCell > 0f) Visits = new Dictionary<long, float>();
                _kinds = _dash
                    ? new[] { "cut", "cut", "hop", "nojump", "stick", "insert", "dash", "dash", "dash", "dashjump", "dashjump", "latejump", "latejump",
                        "superbounce", "superbounce", "superbounce", "backdash", "backdash", "hold", "hold", "retime", "retime", "vertical", "vertical",
                        "springdash", "lockstick", "lockstick", "dashturn" }
                    : new[] { "cut", "cut", "hop", "nojump", "stick", "insert", "hold", "retime", "vertical" };
                if (_goalMode) _kinds = _kinds.Concat(new[] { "reset", "noreset", "movereset" }).ToArray();
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
                    if (_restore.Count > 0) { Restore(_restore.Dequeue()); _spliceScan |= _restore.Count == 0; continue; }
                    if (_plateau != null) { Try(_plateau, "plateau"); _plateau = null; continue; }
                    if (_spliceScan && _bestInputs != null) { QueueSplices(); _spliceScan = false; }
                    if (_spliceQueue.Count > 0)
                    {
                        var (cell, bestTick) = _spliceQueue.Dequeue();
                        SimSnapshot state = StateOf(cell);
                        _fromCell = cell;
                        Splice(cell.Source, cell.Length, bestTick, state);
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
                    Plateau = _bestInputs != null && !Beats(sharedTime, sharedTicks, _best, _bestRunTicks) ? _bestInputs : null,
                    Visits = Visits == null ? null : new Dictionary<long, float>(Visits),
                };
                Exported = generation;
            }

            void Restore(Memory.Source source)
            {
                _restoreKeys = new long[source.Inputs.Count + 1];
                Array.Fill(_restoreKeys, long.MinValue);
                Evaluate(source.Inputs, null, null, false, out _, out _, out _);
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
                lock (_owner.Shared)
                {
                    if (_owner.Shared.Version == _sharedVersion) return;
                    _sharedVersion = _owner.Shared.Version;
                    inputs = _owner.Shared.Inputs;
                    time = _owner.Shared.Time;
                    runTicks = _owner.Shared.RunTicks;
                }
                if (inputs == null || !Beats(time, runTicks, _best, _bestRunTicks)) return;
                var replay = new Replay(inputs.Count);
                if (Evaluate(inputs, null, replay, false, out float replayed, out int finishTick, out long replayedTicks))
                    Adopt(inputs, replayed, finishTick, replayedTicks, replay);
            }

            void Explore()
            {
                if (_poolPicks <= 0 || _owner._cells > _pool.Length + _pool.Length / 8 + 16) Pool();
                if (_pool.Length == 0) { Try(RandomBurst(new List<InputTick>()), "explore"); return; }
                Cell chosen = Draw();
                for (int draw = 1; draw < CachedDraws && !_states.ContainsKey(chosen); draw++) chosen = Draw();
                _poolPicks--;
                Interlocked.Increment(ref chosen.Picks);
                SimSnapshot from = StateOf(chosen);
                if (from == null) return;
                List<InputTick> inputs = RandomBurst(chosen.Source.Take(chosen.Length).ToList());
                _splicing = _bestInputs != null;
                _spliceAt = -1;
                _fromCell = chosen;
                List<InputTick> spliceInto = _bestInputs;
                _recordContacts = true;
                Try(inputs, "explore", from);
                _recordContacts = false;
                if (_died) _contacts.Add((_endTick, Contact.Died));
                int lo = Math.Max(chosen.Length, from.Tick + 1);
                _contacts.RemoveAll(c => c.tick < lo);
                (int tick, Contact kind)? graft = _contacts.Count > 0 && _rng.NextDouble() < GraftRate ? _contacts[_rng.Next(_contacts.Count)] : null;
                _splicing = false;
                // _spliceBestTick is a tick of the best this run was judged against; a new best from Try makes it stale.
                if (_spliceAt > chosen.Length && ReferenceEquals(spliceInto, _bestInputs)) Splice(inputs, _spliceAt, _spliceBestTick, from);
                if (graft is var (tick, kind)) Graft(inputs, tick, kind, lo, from);
                _fromCell = null;
            }

            // Tech lives on the ticks where the player's contact changes, and a random burst puts its jump and dash anywhere in
            // its chunks, so it seldom lands them there. An explore run's contacts are recorded (it leaves the ground at a
            // ledge, lands, touches or leaves a wall, dies), and a graft replaces the run from one of them with an input made
            // for it, a few ticks around it, then GraftCoast ticks of stick: a ledge gets the coyote jump, a super off its edge,
            // the backwards dash-jump, a hyper, ultra or super ground bounce; a landing the buffered jump, the buffered dash's
            // superjump, a jump held into a dash, ground and wave bounces; a wall the wall jump, superbounce, wall dash; a wall
            // left the wall coyote jump; a death (mostly spikes past their grace) all of the plain ones, 1 to 7 ticks early.
            // A corner hyper: a down-diagonal dash that landed (canHyper, flat) and slid off the floor's edge with onGround still
            // up on coyote frames. Its contact is the tick that dash was pressed, and the graft presses it again a few ticks
            // either way with a later jump, so the hyper lands on the corner and jumps in coyote time past the edge, where a
            // landing past the corner can sit below lastHyperY - 4 (the game's ultra).
            // A wall behind: in the air, not dashing, Velocity.y under 580 (a jump cut brings it to 50), a wall within reach of
            // the back edge at the ground rays' height (WallBehindLeft: the wall is at -x). The graft is the wall clip: a light
            // flick toward the wall (release on it, cutting a held jump), the jump pressed with a light flick back the next
            // tick, then a hold, so a second clip off the same wall can follow. A wall ahead (in the air, not dashing, a wall
            // within 12 u of the front edge) sets one up: the stick turns away from the wall for a few ticks and then rests,
            // which leaves the wall behind a player drifting off it. A start gate in reach while the timer is off is the late
            // gate touch: a tap toward the gate and back, as late as the route can leave it, so the timer starts only then.
            enum Contact : byte
            {
                Died, LeftGround, Landed, TouchedWall, LeftWall, CornerHyper, WallBehindLeft, WallBehindRight, WallAheadLeft, WallAheadRight,
                NearGateLeft, NearGateRight, WallAboveLeft, WallAboveRight
            }
            readonly List<(int tick, Contact kind)> _contacts = new List<(int, Contact)>();
            bool _recordContacts, _contactsPrimed, _wasGround, _wasWall, _wasDash, _wasSlide;
            float _wasBehind, _wasAbove, _wasAhead, _wasNearGate;
            int _dashStart;
            const double GraftRate = .5;
            const int GraftCoast = 40;

            // Per contact: the graft's first tick relative to it (lo..hi), the plain inputs and the omnidash ones (null: all).
            static readonly Dictionary<Contact, (int lo, int hi, string[] plain, string[] omni)> GraftsAt = new Dictionary<Contact, (int, int, string[], string[])>
            {
                [Contact.Died] = (-7, -1, new[] { "jump", "jump", "dash", "dashjump", "dashjump", "primed", "primed", "superbounce" }, null),
                [Contact.LeftGround] = (-4, 3, new[] { "jump", "jump", "dashjump", "dashjump", "backdash", "primed" },
                    new[] { "wavedash", "ultra", "superground", "wavejump", "aim" }),
                [Contact.Landed] = (-4, 1, new[] { "jump", "dashjump", "dashjump", "primed", "primed", "dash" },
                    new[] { "dashbounce", "wavebounce", "superground", "wavedash" }),
                [Contact.TouchedWall] = (-3, 3, new[] { "jump", "jump", "superbounce", "superbounce", "dash", "primed" }, new[] { "aim", "dashbounce" }),
                [Contact.LeftWall] = (0, 4, new[] { "jump", "jump", "dashjump" }, new[] { "aim", "wavejump" }),
                [Contact.CornerHyper] = (-3, 3, new[] { "dashjump" }, new[] { "cornerhyper", "cornerhyper", "cornerultra" }),
                [Contact.WallBehindLeft] = (0, 24, new[] { "wallclip" }, new[] { "wallclip" }),
                [Contact.WallBehindRight] = (0, 24, new[] { "wallclip" }, new[] { "wallclip" }),
                [Contact.WallAheadLeft] = (-2, 10, new[] { "turnaway" }, new[] { "turnaway" }),
                [Contact.WallAheadRight] = (-2, 10, new[] { "turnaway" }, new[] { "turnaway" }),
                [Contact.NearGateLeft] = (0, 0, new[] { "gatetap" }, new[] { "gatetap" }),
                [Contact.NearGateRight] = (0, 0, new[] { "gatetap" }, new[] { "gatetap" }),
                [Contact.WallAboveLeft] = (0, 0, new[] { "rest" }, new[] { "rest" }),
                [Contact.WallAboveRight] = (0, 0, new[] { "rest" }, new[] { "rest" }),
            };

            void RecordContact(int tick)
            {
                Movement player = _sim.Player;
                bool ground = player.onGround, wall = player.OnWall;
                float behind = WallBehind(player), above = behind == 0f ? WallBehindAbove(player) : 0f, ahead = WallAhead(player), gate = NearGate();
                if (_contactsPrimed)
                {
                    if (ground != _wasGround) _contacts.Add((tick, ground ? Contact.Landed : Contact.LeftGround));
                    if (wall != _wasWall) _contacts.Add((tick, wall ? Contact.TouchedWall : Contact.LeftWall));
                    if (behind != 0f && behind != _wasBehind) _contacts.Add((tick, behind < 0f ? Contact.WallBehindLeft : Contact.WallBehindRight));
                    if (above != 0f && above != _wasAbove) _contacts.Add((tick, above < 0f ? Contact.WallAboveLeft : Contact.WallAboveRight));
                    if (ahead != 0f && ahead != _wasAhead) _contacts.Add((tick, ahead < 0f ? Contact.WallAheadLeft : Contact.WallAheadRight));
                    if (gate != 0f && gate != _wasNearGate) _contacts.Add((tick, gate < 0f ? Contact.NearGateLeft : Contact.NearGateRight));
                }
                else
                {
                    // An explore from a clip-ready cell (or one by the gate, or under a wall at its back) starts in it: that
                    // is the contact too.
                    if (behind != 0f) _contacts.Add((tick, behind < 0f ? Contact.WallBehindLeft : Contact.WallBehindRight));
                    if (above != 0f) _contacts.Add((tick, above < 0f ? Contact.WallAboveLeft : Contact.WallAboveRight));
                    if (gate != 0f) _contacts.Add((tick, gate < 0f ? Contact.NearGateLeft : Contact.NearGateRight));
                }
                _contactsPrimed = true;
                _wasGround = ground;
                _wasWall = wall;
                _wasBehind = behind;
                _wasAbove = above;
                _wasAhead = ahead;
                _wasNearGate = gate;
            }

            // The side of a wall at the player's back that isn't clip-ready yet because its bottom is still above the ground
            // rays (the back ray at y + 19 or y + 45 hits it within 12 u, the one at y - 18 doesn't), 0 for none: rising up
            // the wall's side with the stick at rest makes it clip-ready.
            static float WallBehindAbove(Movement player)
            {
                if (player.onGround || player.dashActive) return 0f;
                float back = player.facingRight ? -1f : 1f;
                Vector3 p = player.transform.position;
                var dir = new Vector2(back, 0f);
                int layer = GroundLayer(player);
                return Physics2D.Raycast(new Vector2(p.x + back * 15f, p.y + 19f), dir, 12f, layer)
                    || Physics2D.Raycast(new Vector2(p.x + back * 15f, p.y + 45f), dir, 12f, layer) ? back : 0f;
            }

            // The side of the target course's start gate (-1: at -x) when the timer is off and the gate is within 16 u of the
            // hitbox (x + 5 × facing ± 20.5, y ± 36) at its height, 0 otherwise.
            float NearGate()
            {
                if (_sim.TimingCourse() != null) return 0f;
                Movement player = _sim.Player;
                Vector3 p = player.transform.position;
                float cx = p.x + (player.facingRight ? 5f : -5f);
                foreach (Rect box in Gates())
                {
                    if (p.y + 36f < box.yMin || p.y - 36f > box.yMax) continue;
                    if (cx + 20.5f < box.xMin && box.xMin - (cx + 20.5f) <= 16f) return 1f;
                    if (cx - 20.5f > box.xMax && (cx - 20.5f) - box.xMax <= 16f) return -1f;
                }
                return 0f;
            }

            // The target course's start gate boxes in the scene frame, cached until FloatingOrigin moves the world.
            List<Rect> Gates()
            {
                Vector3 origin = _sim.CurrentOrigin;
                if (_gates != null && origin == _gatesOrigin) return _gates;
                _gates = new List<Rect>();
                _gatesOrigin = origin;
                foreach (startGate gate in _sim.World.All<startGate>())
                {
                    if (!gate.gameObject.activeInHierarchy) continue;
                    courseScript course = gate.transform.parent != null ? gate.transform.parent.GetComponentInParent<courseScript>() : null;
                    if (course == null || _sim.TargetCourse != 0 && course.courseNumber != _sim.TargetCourse) continue;
                    BoxCollider2D box = gate.GetComponent<BoxCollider2D>();
                    if (box == null) continue;
                    Vector3 s = gate.transform.lossyScale, at = gate.transform.position;
                    float w = Math.Abs(box.size.x * s.x), h = Math.Abs(box.size.y * s.y);
                    _gates.Add(new Rect(at.x + box.offset.x * s.x - w / 2f, at.y + box.offset.y * s.y - h / 2f, w, h));
                }
                return _gates;
            }
            List<Rect> _gates;
            Vector3 _gatesOrigin;

            // The side of a wall behind the player (-1: at -x, 1: at +x) that a wall clip can use now, 0 for none. The turn
            // swings the hitbox's back edge (x ∓ 15.5, offset 5, width 41) 10 u into the wall, and the front ground ray (x ∓ 25
            // after the turn) grounds the player from inside it while Velocity.y is under 200; a clip needs the back edge
            // within ~8 u, so the ray looks 12 u out from x ∓ 15 at the ground rays' height (y - 18).
            static float WallBehind(Movement player)
            {
                if (player.onGround || player.dashActive || player.Velocity.y >= 580f) return 0f;
                float back = player.facingRight ? -1f : 1f;
                Vector3 p = player.transform.position;
                return Physics2D.Raycast(new Vector2(p.x + back * 15f, p.y - 18f), new Vector2(back, 0f), 12f, GroundLayer(player)) ? back : 0f;
            }

            // The side of a wall in front of the player within 12 u of the hitbox's front edge (x ± 25.5), at the wall rays'
            // heights (y + 19, y - 18) or just over the head (y + 45: a wall whose bottom the player is rising up to), 0 for none.
            static float WallAhead(Movement player)
            {
                if (player.onGround || player.dashActive || player.OnWall) return 0f;
                float front = player.facingRight ? 1f : -1f;
                Vector3 p = player.transform.position;
                var dir = new Vector2(front, 0f);
                int layer = GroundLayer(player);
                foreach (float dy in AheadHeights)
                    if (Physics2D.Raycast(new Vector2(p.x + front * 25f, p.y + dy), dir, 12f, layer)) return front;
                return 0f;
            }
            static readonly float[] AheadHeights = { -18f, 19f, 45f };

            static readonly Func<Movement, LayerMask> GroundLayer = FieldGetter<LayerMask>("groundRaycastLayer");

            // Every physics step of an explore run (a dash runs ticks with no input tick, which RecordContact never sees): the
            // input tick a dash was pressed on, and a CornerHyper contact there when its flat hyper dash leaves the floor.
            void RecordSlide()
            {
                Movement player = _sim.Player;
                bool dash = player.cutsceneMode == Movement.cutsceneModes.dash;
                if (dash && !_wasDash) _dashStart = (int)_sim.Engine.RunTicks - 1;
                bool slide = dash && player.onGround && CanHyper(player) && CoyoteFrames(player) < player.maxCoyoteFrames;
                if (slide && !_wasSlide) _contacts.Add((_dashStart, Contact.CornerHyper));
                _wasDash = dash;
                _wasSlide = slide;
            }

            void Graft(List<InputTick> run, int tick, Contact contact, int lo, SimSnapshot from)
            {
                var (before, after, plain, omni) = GraftsAt[contact];
                omni ??= OmniKinds;
                int at = Math.Min(tick, run.Count) + _rng.Next(before, after + 1);
                if (at < lo || at > run.Count) return;
                List<InputTick> t = run.GetRange(0, at);
                InputTick last = t[at - 1];
                float side = last.x != 0f ? Math.Sign(last.x) : _rng.NextDouble() < .5 ? -1f : 1f;
                if (_rng.NextDouble() < .3) side = -side;
                var coast = new InputTick { x = side, y = last.y };
                for (int i = 0; i < GraftCoast; i++) t.Add(coast);
                if (last.held) { InputTick r = t[at]; r.release = true; t[at] = r; }
                // A corner hyper's contact is a dash press: only the corner re-timings fit it. A wall behind takes only the clip,
                // a wall ahead only the turn away that sets one up, a wall above the back only the rest that rises to it, a start
                // gate in reach only the tap that touches it.
                bool clip = contact == Contact.WallBehindLeft || contact == Contact.WallBehindRight;
                bool ahead = contact == Contact.WallAheadLeft || contact == Contact.WallAheadRight
                    || contact == Contact.WallAboveLeft || contact == Contact.WallAboveRight;
                bool gate = contact == Contact.NearGateLeft || contact == Contact.NearGateRight;
                string kind = clip ? "wallclip" : ahead ? OneOf(plain) : gate ? "gatetap"
                    : !_dash ? "jump" : _omni && (contact == Contact.CornerHyper || _rng.NextDouble() < .4) ? OneOf(omni) : OneOf(plain);
                switch (kind)
                {
                    case "jump":
                        Hop(t, at, OneOf(Holds));
                        break;
                    case "dash":
                    case "dashjump":
                    {
                        InputTick d = t[at];
                        d.dash = true;
                        if (kind == "dashjump") d.dashJump = _rng.Next(1, 5);
                        t[at] = d;
                        break;
                    }
                    case "primed":
                    {
                        // Jump pressed first and held: the jump buffer is still up when the dash fires, so a dash that
                        // touches ground (a corner's gap, a spike wall's ground rays) jumps out of it at once.
                        int dash = at + _rng.Next(1, 7);
                        Hop(t, at, dash - at + OneOf(DashHolds));
                        InputTick d = t[dash]; d.dash = true; t[dash] = d;
                        break;
                    }
                    case "superbounce":
                        SuperBounce(t, at, side);
                        break;
                    case "backdash":
                        BackDash(t, at, lo);
                        break;
                    case "wallclip":
                        WallClip(t, at, contact == Contact.WallBehindLeft ? -1f : 1f);
                        break;
                    case "turnaway":
                        TurnAway(t, at, contact == Contact.WallAheadLeft ? -1f : 1f, last.held, _rng.Next(1, 5));
                        break;
                    case "rest":
                        TurnAway(t, at, contact == Contact.WallAboveLeft ? -1f : 1f, last.held, 0);
                        break;
                    case "gatetap":
                        // The run rests from the contact to the tap (explore's burst would carry it off the spot), and one
                        // contact tries several taps: only a 1-2 tick brush at the right height starts the timer.
                        for (int variant = 1; variant < GateVariants; variant++)
                        {
                            List<InputTick> other = t.ToList();
                            GateTap(other, at, contact == Contact.NearGateLeft ? -1f : 1f, last.held);
                            Interlocked.Increment(ref _owner.GateTaps);
                            ClipChain(other, -1, at, from, 0);
                        }
                        GateTap(t, at, contact == Contact.NearGateLeft ? -1f : 1f, last.held);
                        break;
                    default:
                        Omni(t, at, lo, kind);
                        break;
                }
                Interlocked.Increment(ref _owner.Grafts);
                if (contact == Contact.CornerHyper) Interlocked.Increment(ref _owner.CornerGrafts);
                if (gate) Interlocked.Increment(ref _owner.GateTaps);
                if (!clip && !ahead && !gate) { Try(t, contact == Contact.CornerHyper ? "corner hyper" : "graft", from); return; }
                if (ahead) Interlocked.Increment(ref _owner.SetupGrafts);
                ClipChain(t, clip ? at : -1, clip ? -1 : at, from, 0);
            }

            // A wall clip or its set-up, then more clips in the same run: its run records its contacts, and a clip goes on the
            // first wall behind after the last input made (the next one needs Velocity.y back under 580, or a set-up that drifted
            // to a stop with the wall at its back). An explore cell keeps only the earliest arrival in its square, so the slow
            // set-up next to a wall and the states between clips seldom survive to be explored from later; chaining finds a
            // climb of several clips (Course 1 NONE's pillar and shelf: four) in one explore step.
            void ClipChain(List<InputTick> t, int clipAt, int setupAt, SimSnapshot from, int depth)
            {
                if (clipAt >= 0) Interlocked.Increment(ref _owner.ClipGrafts);
                _clipWatch = clipAt;
                _setupWatch = setupAt;
                _clipLanded = _setupLanded = false;
                bool record = _recordContacts;
                _recordContacts = depth < MaxClipChain;
                Try(t, clipAt >= 0 ? "wall clip" : "wall clip setup", from);
                _recordContacts = record;
                bool landed = _clipLanded;
                if (landed) Interlocked.Increment(ref _owner.ClipsLanded);
                if (_setupLanded) Interlocked.Increment(ref _owner.SetupsLanded);
                _clipWatch = _setupWatch = -1;
                if (depth >= MaxClipChain || clipAt >= 0 && !landed) return;
                int after = Math.Max(clipAt, setupAt) + 2;
                foreach (var (tick, kind) in _contacts)
                {
                    if (tick < after || kind != Contact.WallBehindLeft && kind != Contact.WallBehindRight) continue;
                    int at = Math.Min(tick, t.Count) + _rng.Next(0, 9);
                    if (at >= t.Count || _rng.NextDouble() < .15) return;
                    List<InputTick> next = t.GetRange(0, at);
                    InputTick last = next[at - 1];
                    // After the clip's hold the run drifts: on along the wall, off it, or back the way it came (Course 1 NONE
                    // leaves the pillar's left face for the shelf's edge, 90 u away, in the second clip's rise).
                    var coast = new InputTick { x = OneOf(Sticks), y = last.y };
                    for (int i = 0; i < GraftCoast; i++) next.Add(coast);
                    WallClip(next, at, kind == Contact.WallBehindLeft ? -1f : 1f);
                    ClipChain(next, at, -1, from, depth + 1);
                    return;
                }
            }
            const int MaxClipChain = 4;
            int _clipWatch = -1, _setupWatch = -1;
            bool _clipLanded, _setupLanded, _watchAir;
            static readonly bool ClipLog = Environment.GetEnvironmentVariable("IGTAP_CLIPLOG") == "1";

            void Pool()
            {
                int n = _owner._cells + 64;
                var cells = new List<Cell>(n);
                var weights = new List<double>(n);
                double total = 0;
                foreach (KeyValuePair<long, Cell> pair in _archive)
                {
                    total += Weight(pair.Key, pair.Value);
                    cells.Add(pair.Value);
                    weights.Add(total);
                }
                _pool = cells.ToArray();
                _poolWeights = weights.ToArray();
                _poolPicks = Math.Max(PoolPicks, _pool.Length / 256);
            }
            const int PoolPicks = 16;

            Cell Draw()
            {
                int i = Array.BinarySearch(_poolWeights, _rng.NextDouble() * _poolWeights[_pool.Length - 1]);
                return _pool[Math.Min(i < 0 ? ~i : i, _pool.Length - 1)];
            }

            SimSnapshot StateOf(Cell cell)
            {
                if (_states.TryGetValue(cell, out LinkedListNode<(Cell cell, SimSnapshot state)> node))
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    Interlocked.Increment(ref _owner.StateHits);
                    return node.Value.state;
                }
                var path = new List<Cell> { cell };
                LinkedListNode<(Cell cell, SimSnapshot state)> found = null;
                for (Cell ancestor = cell.Parent; ancestor != null && !_states.TryGetValue(ancestor, out found); ancestor = ancestor.Parent) path.Add(ancestor);
                path.Reverse();
                _capturePath = path;
                _captureNext = 0;
                _captured = null;
                Evaluate(cell.Source, found?.Value.state, null, false, out _, out _, out _, judgeAfter: cell.Length);
                _capturePath = null;
                Interlocked.Increment(ref _owner.StateReplays);
                return _captured;
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
                if (_states.TryGetValue(cell, out LinkedListNode<(Cell cell, SimSnapshot state)> node)) _recent.Remove(node);
                _states[cell] = _recent.AddFirst((cell, state));
                int cap = (int)Math.Max(512, _owner._stateBudget / Math.Max(1024.0, _stateBytes));
                _owner.StatesPerWorker = cap;
                while (_recent.Count > cap)
                {
                    _states.Remove(_recent.Last.Value.cell);
                    _recent.RemoveLast();
                }
            }

            void Splice(List<InputTick> run, int at, int bestTick, SimSnapshot from)
            {
                if (from == null) return;
                var t = new List<InputTick>(at + _bestInputs.Count - bestTick);
                t.AddRange(run.Take(at));
                t.AddRange(_bestInputs.Skip(bestTick));
                if (t.Count <= at) return;
                InputTick k = t[at], before = t[at - 1];
                if (k.held && !before.held) k.press = true;
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

            double Weight(long key, Cell cell)
            {
                double w = key < 0 ? 3.0 : 1.0;
                if (key < 0 && _bestInputs == null)
                {
                    double reach = !_goalMode ? (_owner._maxTimedRank > 0 ? cell.Rank / _owner._maxTimedRank : 0) : GoalReach(GoalProgress(key), cell.Distance);
                    w *= 1 + 10 * Math.Pow(reach, 4);
                }
                if (_owner._path != null && _bestInputs == null) w *= 1 + 10 * cell.Waypoints;
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
                    if (_goalMode && start > 0 && !inputs[start - 1].reset && _rng.NextDouble() < .05)
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
                            if (d.dashJump == 0 && _rng.NextDouble() < .5)
                                holdEnd = start > 0 && inputs[start - 1].x != 0f && _rng.NextDouble() < .3
                                    ? BackDash(inputs, at, start)
                                    : SuperBounce(inputs, at, d.x == 0f ? (_rng.NextDouble() < .5 ? -1f : 1f) : Math.Sign(d.x));
                        }
                    }
                }
                if (_owner._path != null && inputs.Count > 0)
                {
                    InputTick last = inputs[inputs.Count - 1];
                    var coast = new InputTick { x = last.x, y = last.y, held = last.held && !last.release };
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

            // The wall clip (techs.json gc-wall-clip): a light flick toward the wall behind on `at` with the jump released (a
            // held jump is cut to 50 up), the jump pressed with a light flick back on at + 1, where the turned hitbox's front
            // ground ray starts inside the wall and grounds the player, then a hold with the stick at rest. The flick back
            // leaves the back edge ~8 u from the wall, close enough for a second clip off it after the hold.
            int WallClip(List<InputTick> t, int at, float toward)
            {
                int hold = OneOf(ClipHolds);
                Pad(t, at + 3 + hold);
                InputTick flick = t[at];
                flick.x = toward * OneOf(ClipFlicks); flick.y = 0f; flick.press = false; flick.dash = false;
                flick.release = at > 0 && t[at - 1].held; flick.held = false;
                t[at] = flick;
                InputTick jump = t[at + 1];
                jump.x = -toward * OneOf(ClipBacks); jump.y = 0f; jump.press = true; jump.held = true; jump.release = false; jump.dash = false;
                t[at + 1] = jump;
                int end = at + 2 + hold;
                for (int i = at + 2; i < end; i++) { InputTick k = t[i]; k.x = 0f; k.y = 0f; k.press = false; k.held = true; k.release = false; k.dash = false; t[i] = k; }
                for (int i = end; i < t.Count && t[i].held && !t[i].press; i++) { InputTick k = t[i]; k.held = false; k.release = false; t[i] = k; }
                InputTick r = t[end]; r.release = true; t[end] = r;
                return end;
            }
            static readonly float[] ClipFlicks = { .3f, .35f, .4f, .45f, .5f, .55f }, ClipBacks = { .3f, .4f, .5f, .65f, 1f };
            static readonly int[] ClipHolds = { 0, 3, 6, 9, 12, 16, 20, 26 };

            // Sets up a wall clip: with a wall ahead, the stick turns away from it for 1-4 ticks, then rests for 3-30, so the
            // player drifts to a stop facing away with the wall at its back; the jump stays as it was (held on, not cut).
            // ClipChain then clips off the wall behind that the run records.
            int TurnAway(List<InputTick> t, int at, float wall, bool held, int turn)
            {
                int rest = _rng.Next(3, 31);
                Pad(t, at + turn + rest + 1);
                float away = -wall * OneOf(TurnAways);
                int end = at + turn + rest;
                for (int i = at; i < end; i++)
                {
                    InputTick k = t[i];
                    k.x = i < at + turn ? away : 0f; k.y = 0f; k.press = false; k.dash = false; k.held = held; k.release = false;
                    t[i] = k;
                }
                if (held) { InputTick r = t[end]; r.release = true; t[end] = r; }
                return end;
            }
            static readonly float[] TurnAways = { .3f, .5f, .7f, 1f };

            // The late gate touch (techs.json late-gate-touch): with the start gate in reach and the timer off, the stick rests
            // (a jump pressed at the contact half the time: standing by the gate, the brush comes in the jump's rise), then a
            // tap toward the gate (1-2 ticks, 0.3-1) and back (1-2 ticks), then rest again, the jump held as it was, so the
            // hitbox brushes the gate for a tick; ClipChain then clips off the wall behind it.
            int GateTap(List<InputTick> t, int at, float gate, bool held)
            {
                int delay = _rng.Next(0, 21), tap = _rng.Next(1, 3), back = _rng.Next(1, 3), rest = _rng.Next(5, 31);
                int from = at + delay, end = from + tap + back + rest;
                Pad(t, end + 1);
                bool jump = !held && _rng.NextDouble() < .5;
                float toward = gate * OneOf(TurnAways), away = -gate * OneOf(TurnAways);
                for (int i = at; i < end; i++)
                {
                    InputTick k = t[i];
                    k.x = i < from ? 0f : i < from + tap ? toward : i < from + tap + back ? away : 0f;
                    k.y = 0f; k.press = jump && i == at; k.dash = false; k.held = held || jump; k.release = false;
                    t[i] = k;
                }
                if (held || jump) { InputTick r = t[end]; r.release = true; t[end] = r; }
                return end;
            }
            const int GateVariants = 8;

            static readonly (float x, float y)[] Aims = { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f), (1f, 1f), (-1f, 1f), (1f, -1f), (-1f, -1f) };
            static readonly string[] OmniKinds = { "aim", "aim", "steep", "wavedash", "wavedash", "ultra", "dashbounce", "dashbounce", "wavebounce", "wavebounce",
                "wavejump", "superground" };
            static readonly int[] DashHolds = { 3, 6, 10, 16, 24 };
            static readonly int[] BounceHolds = { 3, 6, 10, 16 }, DashJumpHolds = { 2, 5, 10, 16 }, LateHolds = { 0, 2, 5, 10 };
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
                        // Freeze aim: the dash pressed with the stick at rest, then the stick 0.35 sideways and near full
                        // up (or down) in its freeze. Unsnapped, it goes 19° off vertical, which no dash pressed on a tick can
                        // (MacroEngine.StickFor); (0.35, 0.94) is a stick a round gate allows.
                        int end = OmniDash(t, at, 0f, 0f, _rng.NextDouble() < .4 ? _rng.Next(1, 11) : 0, side);
                        InputTick d = t[at];
                        d.turnX = side * 0.35f;
                        d.turnY = _rng.NextDouble() < .75 ? 0.94f : -0.94f;
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
                    case "wavejump":
                        return OmniDash(t, at, side, 1f, _rng.NextDouble() < .6 ? _rng.Next(1, 11) : 0, side);
                    case "cornerhyper":
                        return CornerHyper(t, at, side);
                    case "cornerultra":
                    {
                        // The chain's first hyper a few ticks before, so the corner hyper's pre-dash momentum is a hyper's.
                        int first = at - _rng.Next(8, 17);
                        if (first >= lo) WaveDash(t, first, side);
                        return CornerHyper(t, at, side);
                    }
                }
                return at;
            }

            int WaveDash(List<InputTick> t, int at, float side) { return OmniDash(t, at, side, -1f, _rng.NextDouble() < .85 ? _rng.Next(4, 11) : 0, side); }

            // A wavedash whose jump comes late (dash frame 4-12): after the landing's 4 dash frames, into the coyote frames
            // once the flat dash has slid off the corner.
            int CornerHyper(List<InputTick> t, int at, float side) { return OmniDash(t, at, side, -1f, _rng.Next(4, 13), side); }

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
                if (jump == 0) return at;
                int end = at + 1 + hold;
                for (int i = at + 1; i < end; i++) { InputTick k = t[i]; k.held = true; k.press = false; k.release = false; t[i] = k; }
                for (int i = end; i < t.Count && t[i].held && !t[i].press; i++) { InputTick k = t[i]; k.held = false; k.release = false; t[i] = k; }
                InputTick r = t[end]; r.release = true; t[end] = r;
                return end;
            }

            static void Pad(List<InputTick> t, int count)
            {
                if (t.Count == 0) return;
                InputTick last = t[t.Count - 1];
                var coast = new InputTick { x = last.x, y = last.y, held = last.held && !last.release };
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
                InputTick first = t[q]; first.held = true; t[q] = first;
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
            static readonly Func<InputTick, bool> IsDash = k => k.dash, IsPress = k => k.press, IsPressOrDash = k => k.press || k.dash, IsReset = k => k.reset;

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
                        for (int i = p + 1; i < Math.Min(n, p + 1 + OneOf(LateHolds)); i++) { InputTick h = t[i]; h.held = true; t[i] = h; }
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
                        int q = Pick(t, IsPress);
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
                            from.dash = false; from.dashJump = 0; from.turnX = from.turnY = null;
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
                        if (k.dash && k.dashJump == 0 && _rng.NextDouble() < .5) k.dashJump = _rng.Next(1, 11);
                        t[q] = k;
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
                bool finished = Evaluate(inputs, from, null, mode == "optimize", out float time, out _, out long runTicks, converge: converge);
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
                bool drift = (mode == "optimize" && converged) || ((mode == "optimize" || mode == "plateau") && finished && !Beats(_best, _bestRunTicks, time, runTicks));
                if (!drift && (!finished || !Beats(time, runTicks, _best, _bestRunTicks))) return;
                // An optimize run starts from a state of the best with the best's inputs before it, so its replay starts there too.
                SimSnapshot resume = mode == "optimize" ? from : null;
                var record = new Replay(inputs.Count);
                if (resume != null) record.CopyPrefix(_bestRun, resume.Tick);
                if (!Evaluate(inputs, resume, record, false, out float replayed, out int finishTick, out long replayedTicks)
                    || Beats(_best, _bestRunTicks, replayed, replayedTicks)) return;
                bool improved = Beats(replayed, replayedTicks, _best, _bestRunTicks);
                Adopt(inputs, replayed, finishTick, replayedTicks, record);
                if (improved) _owner.Offer(replayed, replayedTicks, _bestInputs, mode);
                else Interlocked.Increment(ref _owner.Drifts);
            }

            bool Evaluate(List<InputTick> inputs, SimSnapshot from, Replay replay, bool cut,
                out float time, out int finishTick, out long runTicks, int judgeAfter = -1, int converge = int.MaxValue)
            {
                _converged = false;
                ForcedPath path = _owner._path;
                _waypoints = from != null && from.Tick < inputs.Count ? from.Waypoints : 0;
                _avoided = false;
                if (_recordContacts) { _contacts.Clear(); _contactsPrimed = false; _wasDash = _wasSlide = false; }
                Dictionary<long, float> visits = Visits;
                bool corners = _recordContacts && _omni;
                Action<int> onStep = path == null && visits == null && !corners ? null : _ =>
                {
                    if (corners) RecordSlide();
                    if (path == null && visits == null) return;
                    Vector3 p = _sim.PlayerWorld;
                    if (visits != null)
                    {
                        long cell = VisitKey(p, _owner.VisitCell);
                        float t = _sim.Elapsed(null);
                        if (!visits.TryGetValue(cell, out float seen) || t < seen) visits[cell] = t;
                    }
                    if (path == null) return;
                    if (_waypoints < path.Via.Count && path.Via[_waypoints].Contains(p) && ++_waypoints > _owner.MostWaypoints)
                        lock (_owner.Shared) _owner.MostWaypoints = Math.Max(_owner.MostWaypoints, _waypoints);
                    foreach (Box box in path.Avoid) if (box.Contains(p) && (box.After == 0 || _sim.Progress >= box.After)) _avoided = true;
                };
                RunResult result = _sim.Run(inputs, from, tick =>
                {
                    _endTick = tick;
                    if (_recordContacts) RecordContact(tick);
                    if (_clipWatch >= 0 && tick > _clipWatch && tick <= _clipWatch + 3 && !_clipLanded && _watchAir && _sim.Player.Velocity.y >= 780f)
                    {
                        _clipLanded = true;
                        if (ClipLog) Console.WriteLine($"CLIPLOG {_sim.PlayerWorld.x:0} {_sim.PlayerWorld.y:0} {(_sim.TimingCourse() != null ? 1 : 0)}");
                    }
                    _watchAir = !_sim.Player.onGround;
                    if (_setupWatch >= 0 && tick > _setupWatch && !_setupLanded && WallBehind(_sim.Player) != 0f)
                    {
                        _setupLanded = true;
                        if (ClipLog) Console.WriteLine($"SETUPLOG {_sim.PlayerWorld.x:0} {_sim.PlayerWorld.y:0} {(_sim.TimingCourse() != null ? 1 : 0)}");
                    }
                    if (_avoided) return false;
                    bool recorded = replay != null && tick < replay.Body.Length;
                    if (recorded)
                    {
                        replay.Body[tick] = _sim.Physics.PlayerBody();
                        if (tick % StateEvery == 0) replay.Fingerprint[tick] = _sim.Fingerprint();
                        replay.Waypoints[tick] = _waypoints;
                    }
                    else if (tick >= converge && _bestRun != null && tick < _bestRun.Fingerprint.Length && _bestRun.Fingerprint[tick] is int best
                        && _sim.Physics.PlayerBody() == _bestRun.Body[tick] && _sim.Fingerprint() == best && _waypoints == _bestRun.Waypoints[tick])
                    {
                        _converged = true;
                        return false;
                    }
                    courseScript active = _sim.TimingCourse();
                    bool timing = _sim.TimedFromRunStart || active != null;
                    long key = _goalMode ? GoalCellKey(Place(_sim, _swapper, true), _sim.Progress)
                        : CellKey(Place(_sim, _swapper, false) | (timing ? 0L : Motion(_sim.Player)), timing, _waypoints);
                    bool losing = false;
                    float elapsed = _sim.Elapsed(active);
                    int filed = Math.Min(tick, inputs.Count);
                    if (recorded) { replay.Keys[tick] = key; replay.Elapsed[tick] = elapsed; }
                    replay?.Record(filed, key, elapsed);
                    if (elapsed >= 0f)
                    {
                        if (tick > judgeAfter && elapsed > _best) losing = true;
                        if (cut && tick > judgeAfter && _bestRun.Arrival.TryGetValue(key, out float bestAt) && elapsed > bestAt + SegmentSlack) losing = true;
                        if (_splicing && _spliceAt < 0 && from != null && tick > from.Tick && tick < inputs.Count && !_sim.Engine.InputsExhausted
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
                }, _goalMode ? 400 + inputs.Count : 400, onStep);
                if (_capturePath != null) Interlocked.Add(ref _owner.ReplayTicks, result.Stepped);
                else
                {
                    Interlocked.Increment(ref _owner.Runs);
                    Interlocked.Add(ref _owner.Ticks, result.Stepped);
                }
                _died = result.EndReason != null && result.EndReason.StartsWith("Player death", StringComparison.Ordinal);
                time = result.Time;
                finishTick = result.FinishTick;
                runTicks = result.PhysicsTicks;
                return result.Finished && (path == null || _waypoints == path.Via.Count && !_avoided);
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

            static readonly Func<Movement, bool> CanHyper = FieldGetter<bool>("canHyper");
            static readonly Func<Movement, float> CoyoteFrames = FieldGetter<float>("coyoteFrames");

            static Func<Movement, T> FieldGetter<T>(string name)
            {
                FieldInfo field = typeof(Movement).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(nameof(Movement), name);
                ParameterExpression player = Expression.Parameter(typeof(Movement));
                return Expression.Lambda<Func<Movement, T>>(Expression.Field(player, field), player).Compile();
            }

            // Cell key bits: y 0-15 and x 16-31 (48-unit cells, wrapped), air dashes 32-35, air jumps 36-39, wall jumps 40-43,
            // moss 44, momentum x 45-47 and y 48-50 (a course: a wall clip's wall behind 45-46, and before the timer the Motion
            // bits 47-49), waypoints or goal progress 51-61, blue blocks 62.
            internal const int ProgressShift = 51;
            const float MomentumStep = 250f;

            static long Place(Simulation sim, colouredBlockSwapper swapper, bool resources)
            {
                Movement player = sim.Player;
                Vector3 position = sim.PlayerAtCapture;
                long x = Mathf.FloorToInt(position.x / CellSize) & 0xFFFF, y = Mathf.FloorToInt(position.y / CellSize) & 0xFFFF;
                long key = (x << 16) | y;
                if ((object)swapper != null && swapper.isBlueActive) key |= 1L << 62;
                // A clip-ready state (a wall at the back, in reach of a wall clip) is its own cell: the earliest arrival in a
                // square is seldom the slow one that stopped next to the wall, and only that one clips.
                if (!resources) key |= WallBehind(player) switch { < 0f => 1L << 45, > 0f => 1L << 46, _ => 0L };
                bool moss = GroundType(player) != 0;
                if (!moss && !resources) return key;
                long wall = Math.Clamp(player.wallJumpsLeft + 1, 0, 15), air = Math.Clamp(player.airJumpsLeft, 0, 15), dash = Math.Clamp(player.airDashesLeft, 0, 15);
                key |= (moss ? 1L << 44 : 0L) | (wall << 40) | (air << 36) | (dash << 32);
                if (!resources) return key;
                // A superjump or spike super carries its momentum across the next hundred ticks; an arrival that has it and
                // one that doesn't are different states, so neither replaces the other.
                return key | (MomentumBucket(player.momentum.x) << 45) | (MomentumBucket(player.momentum.y) << 48);
            }

            static long MomentumBucket(float m)
            {
                return 3 + Math.Sign(m) * Math.Min(3, (int)(Math.Abs(m) / MomentumStep));
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
                float distance = _goalMode ? GoalDistance(_sim) : float.NaN;
                var filed = new Cell { Source = inputs, Length = tick, Rank = rank, Distance = distance, Waypoints = _waypoints, Parent = _fromCell };
                if (!_owner.File(key, filed)) return;
                if (ClipLog && cell == null) Console.WriteLine($"NEWCELL {_sim.PlayerWorld.x:0} {_sim.PlayerWorld.y:0} {(key < 0 ? 1 : 0)} {(_clipWatch >= 0 || _setupWatch >= 0 ? 1 : 0)}");
                Keep(filed, state);
                _owner.Extend(key, filed, _goalMode ? GoalProgress(key) : -1);
            }
        }
    }
}
