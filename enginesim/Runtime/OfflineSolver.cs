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
            public Box(float x0, float y0, float x1, float y1) { X0 = Math.Min(x0, x1); Y0 = Math.Min(y0, y1); X1 = Math.Max(x0, x1); Y1 = Math.Max(y0, y1); }
            public bool Contains(Vector3 p) { return p.x >= X0 && p.x <= X1 && p.y >= Y0 && p.y <= Y1; }
            public override string ToString() { return $"{X0:0},{Y0:0}..{X1:0},{Y1:0}"; }
        }

        public readonly string Capabilities;
        readonly Func<Simulation> _createSimulation;
        readonly List<List<InputTick>> _seeds;
        readonly Memory _memory;
        readonly ForcedPath _path;
        public readonly Best Shared = new Best();
        public long Runs, Ticks, Finishes, Drifts, Splices, Grafts, BranchChecks, BranchMatches, StateHits, StateReplays, ReplayTicks;
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
        const int SnapshotBytes = 32 << 10;
        public int StatesPerWorker { get; private set; }
        public int ArchiveCells => _cells;
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
            StatesPerWorker = (int)Math.Max(512, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2 / SnapshotBytes / workers);
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

        public Dictionary<long, float> Visits()
        {
            var all = new Dictionary<long, float>();
            foreach (Worker w in _workers ?? Array.Empty<Worker>())
                if (w?.Visits != null)
                    foreach (var v in w.Visits)
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

        sealed class ClosestCell
        {
            public readonly Cell Cell;
            public readonly int Progress;
            public ClosestCell(Cell cell, int progress) { Cell = cell; Progress = progress; }
        }
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

        sealed class Worker
        {
            const float CellSize = 48f;
            const float SegmentSlack = .2f;
            const int StateEvery = 16;
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
            SimSnapshot[] _bestStates;
            Dictionary<long, float> _bestArrival = new Dictionary<long, float>();
            Dictionary<long, (float time, int tick)> _bestFirst = new Dictionary<long, (float, int)>();
            int _sharedVersion;
            readonly bool _goalMode;
            readonly Queue<Memory.Source> _restore;
            List<InputTick> _plateau;

            public sealed class Handover
            {
                public List<Memory.Source> Pending;
                public List<InputTick> Plateau;
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
                if (owner.VisitCell > 0f) _visits = new Dictionary<long, float>();
            }

            readonly Dictionary<long, float> _visits;
            public Dictionary<long, float> Visits { get { return _visits; } }

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
                foreach (KeyValuePair<long, (float time, int tick)> first in _bestFirst)
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
                };
                Exported = generation;
            }

            void Restore(Memory.Source source)
            {
                _restoreKeys = new long[source.Inputs.Count + 1];
                Array.Fill(_restoreKeys, long.MinValue);
                Evaluate(source.Inputs, null, null, null, false, out _, out _, out _);
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
                var states = new SimSnapshot[inputs.Count];
                var arrivals = new Dictionary<long, float>();
                if (!Evaluate(inputs, null, states, arrivals, false, out float replayed, out int finishTick, out long replayedTicks)) return;
                _best = replayed;
                _bestRunTicks = replayedTicks;
                _bestInputs = inputs.Take(finishTick + 1).ToList();
                _bestStates = states;
                _bestArrival = arrivals;
                _bestFirst = _replayFirst;
                _spliceScan = true;
                _bestBody = _replayBody;
                _bestFingerprint = _replayFingerprint;
                _bestWaypoints = _replayWaypoints;
            }

            void Explore()
            {
                if (_poolPicks <= 0 || _owner._cells > _pool.Length + _pool.Length / 8 + 16) Pool();
                if (_pool.Length == 0) { Try(RandomBurst(new List<InputTick>()), "explore"); return; }
                int i = Array.BinarySearch(_poolWeights, _rng.NextDouble() * _poolWeights[_pool.Length - 1]);
                Cell chosen = _pool[Math.Min(i < 0 ? ~i : i, _pool.Length - 1)];
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
            enum Contact : byte { Died, LeftGround, Landed, TouchedWall, LeftWall }
            readonly List<(int tick, Contact kind)> _contacts = new List<(int, Contact)>();
            bool _recordContacts, _contactsPrimed, _wasGround, _wasWall;
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
            };

            void RecordContact(int tick)
            {
                Movement player = _sim.Player;
                bool ground = player.onGround, wall = player.OnWall;
                if (_contactsPrimed)
                {
                    if (ground != _wasGround) _contacts.Add((tick, ground ? Contact.Landed : Contact.LeftGround));
                    if (wall != _wasWall) _contacts.Add((tick, wall ? Contact.TouchedWall : Contact.LeftWall));
                }
                _contactsPrimed = true;
                _wasGround = ground;
                _wasWall = wall;
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
                string kind = !_dash ? "jump" : _omni && _rng.NextDouble() < .4 ? omni[_rng.Next(omni.Length)] : plain[_rng.Next(plain.Length)];
                switch (kind)
                {
                    case "jump":
                        Hop(t, at, Holds[_rng.Next(Holds.Length)]);
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
                        Hop(t, at, dash - at + DashHolds[_rng.Next(DashHolds.Length)]);
                        InputTick d = t[dash]; d.dash = true; t[dash] = d;
                        break;
                    }
                    case "superbounce":
                        SuperBounce(t, at, side);
                        break;
                    case "backdash":
                        BackDash(t, at, lo);
                        break;
                    default:
                        Omni(t, at, lo, kind);
                        break;
                }
                Interlocked.Increment(ref _owner.Grafts);
                Try(t, "graft", from);
            }

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
                _poolPicks = PoolPicks;
            }
            const int PoolPicks = 16;

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
                Evaluate(cell.Source, found?.Value.state, null, null, false, out _, out _, out _, judgeAfter: cell.Length);
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
                SimSnapshot state = _sim.Capture(tick);
                state.Waypoints = _waypoints;
                while (_captureNext < _capturePath.Count && _capturePath[_captureNext].Length == tick) Keep(_capturePath[_captureNext++], state);
                if (_captureNext < _capturePath.Count) return false;
                _captured = state;
                return true;
            }

            void Keep(Cell cell, SimSnapshot state)
            {
                if (_states.TryGetValue(cell, out LinkedListNode<(Cell cell, SimSnapshot state)> node)) _recent.Remove(node);
                _states[cell] = _recent.AddFirst((cell, state));
                while (_recent.Count > _owner.StatesPerWorker)
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

            float GoalDistance()
            {
                FullTasGoal goal = _sim.Goals.Current;
                if (goal == null || goal.kind != FullTasGoal.At) return float.NaN;
                Vector3 p = _sim.PlayerAtCapture;
                float dx = Math.Max(0f, Math.Abs(p.x - goal.x) - goal.w / 2f), dy = Math.Max(0f, Math.Abs(p.y - goal.y) - goal.h / 2f);
                return MathF.Sqrt(dx * dx + dy * dy);
            }

            static readonly int[] Holds = { 1, 3, 6, 10, 16, 24, 30, 40 };
            static readonly int[] ResumeHolds = { 0, 3, 8, 16, 30, 60 };

            List<InputTick> RandomBurst(List<InputTick> inputs)
            {
                float x = inputs.Count > 0 ? inputs[inputs.Count - 1].x : 1f, y = inputs.Count > 0 ? inputs[inputs.Count - 1].y : 0f;
                int holdEnd = inputs.Count > 0 && inputs[inputs.Count - 1].held ? inputs.Count + ResumeHolds[_rng.Next(ResumeHolds.Length)] : 0;
                for (int chunk = _rng.Next(1, 4); chunk > 0; chunk--)
                {
                    if (_rng.NextDouble() < .35) x = new[] { -1f, 0f, 1f }[_rng.Next(3)];
                    if (_rng.NextDouble() < .3) y = Vertical();
                    int length = _rng.Next(3, 21);
                    int start = inputs.Count;
                    for (int i = 0; i < length; i++) inputs.Add(new InputTick { x = x, y = y, held = start + i < holdEnd });
                    if (_goalMode && start > 0 && !inputs[start - 1].reset && _rng.NextDouble() < .05)
                    {
                        InputTick r = inputs[start]; r.reset = true; inputs[start] = r;
                    }
                    if (_rng.NextDouble() < .4) holdEnd = Hop(inputs, start, Holds[_rng.Next(Holds.Length)]);
                    if (_dash && _rng.NextDouble() < .25)
                    {
                        int at = start + _rng.Next(Math.Min(length, 4));
                        if (_omni && _rng.NextDouble() < .5) holdEnd = Omni(inputs, at, start, OmniKinds[_rng.Next(OmniKinds.Length)]);
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
            float Vertical() { return Verticals[_rng.Next(Verticals.Length)]; }

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
                int release = Hop(t, Math.Min(t.Count - 1, at + _rng.Next(0, 6)), new[] { 3, 6, 10, 16 }[_rng.Next(4)]);
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
                return Hop(t, at, new[] { 3, 6, 10, 16, 24 }[_rng.Next(5)]);
            }

            static readonly (float x, float y)[] Aims = { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f), (1f, 1f), (-1f, 1f), (1f, -1f), (-1f, -1f) };
            static readonly string[] OmniKinds = { "aim", "aim", "wavedash", "wavedash", "ultra", "dashbounce", "dashbounce", "wavebounce", "wavebounce", "wavejump",
                "superground" };
            static readonly int[] DashHolds = { 3, 6, 10, 16, 24 };

            int Omni(List<InputTick> t, int at, int lo, string kind)
            {
                if (at < lo || at >= t.Count) return at;
                float side = t[at].x != 0f ? Math.Sign(t[at].x) : _rng.NextDouble() < .5 ? -1f : 1f;
                switch (kind)
                {
                    case "aim":
                    {
                        var (x, y) = Aims[_rng.Next(Aims.Length)];
                        return OmniDash(t, at, x, y, _rng.NextDouble() < .4 ? _rng.Next(1, 11) : 0, x != 0f ? x : side);
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
                int hold = jump > 0 ? DashHolds[_rng.Next(DashHolds.Length)] : 0;
                Pad(t, at + 1 + Math.Max(8, hold + 1));
                InputTick d = t[at];
                d.dash = true; d.x = x; d.y = y; d.dashJump = jump;
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

            void Optimize()
            {
                List<InputTick> t = _bestInputs.ToList();
                int n = t.Count, p = _rng.Next(n);
                string[] kinds = _dash
                    ? new[] { "cut", "cut", "hop", "nojump", "stick", "insert", "dash", "dash", "dash", "dashjump", "dashjump", "latejump", "latejump",
                        "superbounce", "superbounce", "superbounce", "backdash", "backdash", "hold", "hold", "retime", "retime", "vertical", "vertical",
                        "springdash", "lockstick", "lockstick", "dashturn" }
                    : new[] { "cut", "cut", "hop", "nojump", "stick", "insert", "hold", "retime", "vertical" };
                if (_goalMode) kinds = kinds.Concat(new[] { "reset", "noreset", "movereset" }).ToArray();
                if (_omni) kinds = kinds.Concat(OmniKinds).ToArray();
                string kind = kinds[_rng.Next(kinds.Length)];
                switch (kind)
                {
                    case "aim":
                    {
                        List<int> dashes = Enumerable.Range(0, n).Where(i => t[i].dash).ToList();
                        Omni(t, dashes.Count > 0 && _rng.NextDouble() < .7 ? dashes[_rng.Next(dashes.Count)] : p, 0, kind);
                        break;
                    }
                    case "wavedash": case "ultra": case "dashbounce": case "wavebounce": case "wavejump": case "superground":
                        Cut(t, Omni(t, p, 0, kind) + _rng.Next(1, 25), _rng.Next(0, 20));
                        break;
                    case "cut": Cut(t, p, new[] { 1, 1, 2, 3, 5, 8 }[_rng.Next(6)]); break;
                    case "insert":
                        float x = new[] { -1f, 0f, 1f }[_rng.Next(3)];
                        int count = new[] { 1, 2, 4, 8, 15 }[_rng.Next(5)];
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
                        Hop(t, j, new[] { 2, 5, 10, 16 }[_rng.Next(4)]);
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
                        for (int i = p + 1; i < Math.Min(n, p + 1 + new[] { 0, 2, 5, 10 }[_rng.Next(4)]); i++) { InputTick h = t[i]; h.held = true; t[i] = h; }
                        Cut(t, p + _rng.Next(1, 25), _rng.Next(0, 20));
                        break;
                    }
                    case "hop":
                        Hop(t, p, Holds[_rng.Next(Holds.Length)]);
                        Cut(t, p + _rng.Next(1, 30), _rng.Next(0, 10));
                        break;
                    case "nojump":
                    {
                        List<int> presses = Enumerable.Range(0, n).Where(i => t[i].press || t[i].dash).ToList();
                        if (presses.Count == 0) return;
                        int q = presses[_rng.Next(presses.Count)];
                        InputTick k = t[q]; k.press = k.dash = false; k.dashJump = 0; t[q] = k;
                        break;
                    }
                    case "hold":
                    {
                        List<int> presses = Enumerable.Range(0, n).Where(i => t[i].press).ToList();
                        if (presses.Count == 0) return;
                        int q = presses[_rng.Next(presses.Count)];
                        int end = _rng.NextDouble() < .5 ? q + Holds[_rng.Next(Holds.Length)]
                            : HoldEnd(t, q) + (_rng.NextDouble() < .5 ? -1 : 1) * new[] { 1, 2, 3, 5, 8 }[_rng.Next(5)];
                        SetHold(t, q, end);
                        break;
                    }
                    case "retime":
                    {
                        List<int> presses = Enumerable.Range(0, n).Where(i => t[i].press || t[i].dash).ToList();
                        if (presses.Count == 0) return;
                        int q = presses[_rng.Next(presses.Count)];
                        int to = q + (_rng.NextDouble() < .5 ? -1 : 1) * new[] { 1, 1, 2, 3, 5, 8 }[_rng.Next(6)];
                        if (to < 0 || to >= n || t[to].press || t[to].dash) return;
                        InputTick from = t[q], dest = t[to];
                        if (from.dash)
                        {
                            dest.dash = true; dest.dashJump = from.dashJump; dest.turnX = from.turnX;
                            from.dash = false; from.dashJump = 0; from.turnX = null;
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
                        float aim = new[] { -1f, 0f, 1f, partial }[_rng.Next(4)];
                        for (int i = p; i < Math.Min(n, p + _rng.Next(1, 15)); i++) { InputTick k = t[i]; k.x = aim; t[i] = k; }
                        break;
                    }
                    case "vertical":
                    {
                        float aim = Vertical();
                        for (int i = p; i < Math.Min(n, p + new[] { 1, 3, 6, 12, 25, 50 }[_rng.Next(6)]); i++) { InputTick k = t[i]; k.y = aim; t[i] = k; }
                        break;
                    }
                    case "springdash":
                    {
                        InputTick k = t[p]; k.springDash = _rng.NextDouble() < .2 ? 0 : _rng.Next(1, 26); t[p] = k;
                        break;
                    }
                    case "lockstick":
                    {
                        List<int> dashes = Enumerable.Range(0, n).Where(i => t[i].dash).ToList();
                        int q = dashes.Count > 0 && _rng.NextDouble() < .7 ? dashes[_rng.Next(dashes.Count)] : p;
                        InputTick k = t[q];
                        k.lockX = LockValues[_rng.Next(LockValues.Length)];
                        k.lockY = LockValues[_rng.Next(LockValues.Length)];
                        if (k.dash && k.dashJump == 0 && _rng.NextDouble() < .5) k.dashJump = _rng.Next(1, 11);
                        t[q] = k;
                        break;
                    }
                    case "dashturn":
                    {
                        // A dash with no sideways stick, turned by the stick held in its freeze (MacroEngine.StickFor). With the stick
                        // straight down from a wall cling, turned toward the wall, it is the Inwall wallbounce's climbing dash.
                        List<int> dashes = Enumerable.Range(0, n).Where(i => t[i].dash).ToList();
                        if (dashes.Count == 0) return;
                        int q = dashes[_rng.Next(dashes.Count)];
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
                        List<int> resets = Enumerable.Range(0, n).Where(i => t[i].reset).ToList();
                        if (resets.Count == 0) return;
                        int q = resets[_rng.Next(resets.Count)];
                        InputTick k = t[q]; k.reset = false; t[q] = k;
                        break;
                    }
                    case "movereset":
                    {
                        List<int> resets = Enumerable.Range(0, n).Where(i => t[i].reset).ToList();
                        if (resets.Count == 0) return;
                        int q = resets[_rng.Next(resets.Count)];
                        InputTick k = t[q]; k.reset = false; t[q] = k;
                        int to = q + (_rng.NextDouble() < .5 ? -1 : 1) * new[] { 1, 1, 2, 3, 5, 8 }[_rng.Next(6)];
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
                s = Math.Min(Math.Min(s, t.Count - 1), _bestStates.Length - 1);
                while (s > 0 && _bestStates[s] == null) s--;
                Try(t, "optimize", s > 0 ? _bestStates[s] : null, converge);
            }

            void Try(List<InputTick> inputs, string mode, SimSnapshot from = null, int converge = int.MaxValue)
            {
                if (inputs.Count == 0) inputs.Add(new InputTick());
                if (from != null && from.Tick >= inputs.Count) from = null;
                bool finished = Evaluate(inputs, from, null, null, mode == "optimize", out float time, out _, out long runTicks, converge: converge);
                bool converged = _converged;
                if (from != null && _rng.Next(100) == 0)
                {
                    bool branch = Evaluate(inputs, from, null, null, false, out float branchTime, out _, out _);
                    Vector3 end = _sim.Player.transform.m_Position;
                    bool again = Evaluate(inputs, null, null, null, false, out float replay, out _, out _, from.Tick);
                    Interlocked.Increment(ref _owner.BranchChecks);
                    if (again == branch && Math.Abs(replay - branchTime) < .0001f && (end - _sim.Player.transform.m_Position).sqrMagnitude < .01f)
                        Interlocked.Increment(ref _owner.BranchMatches);
                }
                if (finished) Interlocked.Increment(ref _owner.Finishes);
                bool drift = (mode == "optimize" && converged) || ((mode == "optimize" || mode == "plateau") && finished && !Beats(_best, _bestRunTicks, time, runTicks));
                if (!drift && (!finished || !Beats(time, runTicks, _best, _bestRunTicks))) return;
                var states = new SimSnapshot[inputs.Count];
                var arrivals = new Dictionary<long, float>();
                if (!Evaluate(inputs, null, states, arrivals, false, out float replayed, out int finishTick, out long replayedTicks)
                    || Beats(_best, _bestRunTicks, replayed, replayedTicks)) return;
                bool improved = Beats(replayed, replayedTicks, _best, _bestRunTicks);
                _best = replayed;
                _bestRunTicks = replayedTicks;
                _bestInputs = inputs.Take(finishTick + 1).ToList();
                _bestStates = states;
                _bestArrival = arrivals;
                _bestFirst = _replayFirst;
                _bestBody = _replayBody;
                _bestFingerprint = _replayFingerprint;
                _bestWaypoints = _replayWaypoints;
                _spliceScan = true;
                if (improved) _owner.Offer(replayed, replayedTicks, _bestInputs, mode);
                else Interlocked.Increment(ref _owner.Drifts);
            }

            bool Evaluate(List<InputTick> inputs, SimSnapshot from, SimSnapshot[] states, Dictionary<long, float> arrivals, bool cut,
                out float time, out int finishTick, out long runTicks, int judgeAfter = -1, int converge = int.MaxValue)
            {
                _converged = false;
                if (states != null)
                {
                    _replayBody = new (float, float, float, float, float, float)[inputs.Count + 1];
                    _replayFingerprint = new int?[inputs.Count + 1];
                    _replayWaypoints = new int[inputs.Count + 1];
                    _replayFirst = new Dictionary<long, (float, int)>();
                }
                ForcedPath path = _owner._path;
                _waypoints = from != null && from.Tick < inputs.Count ? from.Waypoints : 0;
                _avoided = false;
                if (_recordContacts) { _contacts.Clear(); _contactsPrimed = false; }
                Dictionary<long, float> visits = _visits;
                Action<int> onStep = path == null && visits == null ? null : _ =>
                {
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
                    foreach (Box box in path.Avoid) if (box.Contains(p)) _avoided = true;
                };
                RunResult result = _sim.Run(inputs, from, tick =>
                {
                    _endTick = tick;
                    if (_recordContacts) RecordContact(tick);
                    if (_avoided) return false;
                    if (states != null && tick < _replayBody.Length)
                    {
                        _replayBody[tick] = _sim.Physics.PlayerBody();
                        if (tick % StateEvery == 0) _replayFingerprint[tick] = _sim.Fingerprint();
                        _replayWaypoints[tick] = _waypoints;
                    }
                    else if (tick >= converge && _bestFingerprint != null && tick < _bestFingerprint.Length && _bestFingerprint[tick] is int best
                        && _sim.Physics.PlayerBody() == _bestBody[tick] && _sim.Fingerprint() == best && _waypoints == _bestWaypoints[tick])
                    {
                        _converged = true;
                        return false;
                    }
                    courseScript active = _sim.TimingCourse();
                    bool timing = _sim.TimedFromRunStart || active != null;
                    long key = _goalMode ? GoalCellKey(Place(_sim, _swapper, true), _sim.Progress) : CellKey(Place(_sim, _swapper, false), timing, _waypoints);
                    bool losing = false;
                    float elapsed = _sim.Elapsed(active);
                    int filed = Math.Min(tick, inputs.Count);
                    if (elapsed >= 0f)
                    {
                        if (tick > judgeAfter && elapsed > _best) losing = true;
                        if (cut && tick > judgeAfter && _bestArrival.TryGetValue(key, out float bestAt) && elapsed > bestAt + SegmentSlack) losing = true;
                        if (arrivals != null) arrivals[key] = elapsed;
                        if (states != null && filed < inputs.Count) _replayFirst.TryAdd(key, (elapsed, filed));
                        if (_splicing && _spliceAt < 0 && from != null && tick > from.Tick && tick < inputs.Count && !_sim.Engine.InputsExhausted
                            && _bestFirst.TryGetValue(key, out (float time, int tick) first) && elapsed < first.time - SpliceGain)
                        {
                            _spliceAt = tick;
                            _spliceBestTick = first.tick;
                        }
                    }
                    if (_restoreKeys != null && filed < _restoreKeys.Length) _restoreKeys[filed] = key;
                    if (_capturePath != null && CaptureOnPath(tick)) return false;
                    FileCell(inputs, filed, key, elapsed >= 0f ? elapsed : tick * .02f, states);
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
            (float, float, float, float, float, float)[] _replayBody, _bestBody;
            Dictionary<long, (float time, int tick)> _replayFirst;
            bool _splicing;
            int _spliceAt, _spliceBestTick;
            const float SpliceGain = .02f;
            int?[] _replayFingerprint, _bestFingerprint;
            int[] _replayWaypoints, _bestWaypoints;
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
            // moss 44, momentum x 45-47 and y 48-50, waypoints or goal progress 51-61, blue blocks 62.
            const int ProgressShift = 51;
            const float MomentumStep = 250f;

            static long Place(Simulation sim, colouredBlockSwapper swapper, bool resources)
            {
                Movement player = sim.Player;
                Vector3 position = sim.PlayerAtCapture;
                long x = Mathf.FloorToInt(position.x / CellSize) & 0xFFFF, y = Mathf.FloorToInt(position.y / CellSize) & 0xFFFF;
                long key = (x << 16) | y;
                if ((object)swapper != null && swapper.isBlueActive) key |= 1L << 62;
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

            static long CellKey(long place, bool timing, int waypoints)
            {
                long key = ((long)waypoints << ProgressShift) | place;
                return timing ? -1 - key : key;
            }

            static long GoalCellKey(long place, int progress)
            {
                return -1 - (((long)progress << ProgressShift) | place);
            }

            static int GoalProgress(long goalKey) { return (int)(((-1 - goalKey) >> ProgressShift) & 0x7FF); }

            void FileCell(List<InputTick> inputs, int tick, long key, float rank, SimSnapshot[] states)
            {
                bool better = !_archive.TryGetValue(key, out Cell cell) || Earlier(rank, tick, cell);
                bool resumable = tick > 0 && tick < inputs.Count && !_sim.Engine.InputsExhausted;
                SimSnapshot state = resumable && (better || states != null && tick % StateEvery == 0) ? _sim.Capture(tick) : null;
                if (state != null) state.Waypoints = _waypoints;
                if (states != null && state != null) states[tick] = state;
                if (!better || state == null) return;
                float distance = _goalMode ? GoalDistance() : float.NaN;
                var filed = new Cell { Source = inputs, Length = tick, Rank = rank, Distance = distance, Waypoints = _waypoints, Parent = _fromCell };
                if (!_owner.File(key, filed)) return;
                Keep(filed, state);
                _owner.Extend(key, filed, _goalMode ? GoalProgress(key) : -1);
            }
        }
    }
}
