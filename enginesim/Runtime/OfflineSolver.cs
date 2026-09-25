using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace IGTAP.EngineSim
{
    // The in-game solver (Solver.cs) running offline: explore (Go-Explore over 48 px map cells x course timer
    // running) and optimize (mutate the best finish), judged by the game's own timer, then (at the same time) by the
    // whole run's physics ticks: a flying start's setup before its last start-gate crossing is off the timer but still
    // worth shortening. Each worker thread owns a world and archive; workers share only the best finish, which each
    // adopts by replaying it. A Full-TAS window (the simulation has goals, FullTas.cs in the CLI) is ranked in physics ticks
    // from the run start, with cells per goal reached and quick restarts among the inputs a route may add.
    public sealed class OfflineSolver
    {
        public sealed class Best
        {
            public float Time = float.PositiveInfinity;
            public long RunTicks = long.MaxValue;
            public List<InputTick> Inputs;
            public int Version;
        }

        // Search state carried from one solve to the next (Solve.cs keeps it in archive/<label>.json): every explore
        // cell's input prefix with how often it was picked, and the plateau, routes exactly as fast as the best that the
        // workers drifted to. A solve replays the prefixes to rebuild the cells (snapshots are not saved) instead of
        // re-finding them, and each worker resumes its drift from a plateau route instead of the saved one.
        public sealed class Memory
        {
            public long Runs;
            public List<Source> Sources = new List<Source>();
            public List<List<InputTick>> Plateau = new List<List<InputTick>>();

            // One run's inputs up to its last cell, and the cells it reached first.
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

        public readonly string Capabilities;
        readonly Func<Simulation> _createSimulation;
        readonly List<List<InputTick>> _seeds;
        readonly Memory _memory;
        public readonly Best Shared = new Best();
        public long Runs, Ticks, Finishes, Drifts, BranchChecks, BranchMatches;
        public event Action<float, long, List<InputTick>, string> Improved;
        volatile bool _stop;
        volatile int _exportGeneration;
        Worker[] _workers;
        List<Memory.Source>[] _partitions;

        public OfflineSolver(Func<Simulation> createSimulation, string capabilities, List<List<InputTick>> seeds, Memory memory = null)
        {
            _createSimulation = createSimulation;
            Capabilities = capabilities;
            _seeds = seeds;
            _memory = memory;
        }

        public void Stop() { _stop = true; }

        public Thread[] Start(int workers)
        {
            var threads = new Thread[workers];
            _workers = new Worker[workers];
            // The saved prefixes are split between the workers, so each replays only its share.
            _partitions = Enumerable.Range(0, workers).Select(i => _memory == null ? new List<Memory.Source>()
                : _memory.Sources.Where((s, k) => k % workers == i).ToList()).ToArray();
            for (int i = 0; i < workers; i++)
            {
                int index = i;
                int seed = Environment.TickCount * 31 + i * 7919;
                // Worker 0 evaluates the seed routes first; the others start exploring right away.
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

        // The search state to carry to the next solve: each worker hands over its archive between runs (a worker that
        // doesn't within `timeoutMs` counts with its last hand-over, or its unreplayed share of the saved state).
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
            for (int i = 0; i < _workers.Length; i++)
            {
                Worker.Handover h = _workers[i]?.LastHandover;
                foreach (Memory.Source s in h?.Pending ?? _partitions[i])
                    foreach (Memory.SavedCell c in s.Cells) Add(c.Key, s.Inputs, c.Length, c.Picks, c.Rank);
                if (h == null) continue;
                foreach (var c in h.Cells) Add(c.key, c.source, c.length, c.picks, c.rank);
                if (h.Plateau != null && !memory.Plateau.Any(p => p.SequenceEqual(h.Plateau))) memory.Plateau.Add(h.Plateau);
            }
            var sources = new Dictionary<List<InputTick>, Memory.Source>(ReferenceEqualityComparer.Instance);
            foreach (var pair in cells)
            {
                if (!sources.TryGetValue(pair.Value.source, out Memory.Source s)) sources[pair.Value.source] = s = new Memory.Source { Inputs = pair.Value.source };
                s.Cells.Add(new Memory.SavedCell { Key = pair.Key, Length = pair.Value.length, Picks = pair.Value.picks, Rank = pair.Value.rank });
            }
            // A cell's prefix is resumed after its tick's input, so the replay needs that one tick more.
            foreach (Memory.Source s in sources.Values)
                s.Inputs = s.Inputs.GetRange(0, Math.Min(s.Inputs.Count, s.Cells.Max(c => c.Length) + 1));
            memory.Sources = sources.Values.ToList();
            return memory;
        }

        // Faster on the timer, or as fast (same 0.0001 s tolerance as the game's judge) in fewer physics ticks overall.
        public static bool Beats(float time, long runTicks, float bestTime, long bestRunTicks)
        {
            return time < bestTime - .0001f || (time <= bestTime + .0001f && runTicks < bestRunTicks);
        }

        bool Offer(float time, long runTicks, List<InputTick> inputs, string how)
        {
            // Reported under the lock: tie-breaks come in quick bursts from many workers, and a later report of an older
            // best would overwrite the newer one's file (same time, same name).
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
            public int Length, Picks;
            public float Rank;
            public SimSnapshot State;
        }

        sealed class Worker
        {
            const float CellSize = 48f;
            const float SegmentSlack = .2f;
            readonly OfflineSolver _owner;
            readonly Simulation _sim;
            readonly System.Random _rng;
            readonly Queue<List<InputTick>> _seeds;
            readonly Dictionary<long, Cell> _archive = new Dictionary<long, Cell>();
            readonly bool _dash;
            float _best = float.PositiveInfinity;
            long _bestRunTicks = long.MaxValue;
            List<InputTick> _bestInputs;
            SimSnapshot[] _bestStates;
            Dictionary<long, float> _bestArrival = new Dictionary<long, float>();
            int _sharedVersion;
            float _maxTimedRank;
            // Full-TAS (the simulation has goals): cells are per goal, ranks count from the run start (a window starts late
            // in it), and routes may quick-restart.
            readonly bool _goalMode;
            float _minTimedRank = float.PositiveInfinity;
            readonly Queue<Memory.Source> _restore;
            List<InputTick> _plateau;

            // What the worker hands the saver (Export): its cells, the saved prefixes it has yet to replay, and its best
            // when that is as fast as the shared one.
            public sealed class Handover
            {
                public List<(long key, List<InputTick> source, int length, int picks, float rank)> Cells;
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
                _dash = owner.Capabilities == "wjdash" || owner.Capabilities == "wjdashaj";
                _goalMode = sim.Goals != null;
            }

            public void Loop()
            {
                while (!_owner._stop)
                {
                    AdoptShared();
                    int generation = _owner._exportGeneration;
                    if (Exported != generation) HandOver(generation);
                    if (_seeds.Count > 0) { Try(_seeds.Dequeue(), "seed"); continue; }
                    if (_restore.Count > 0) { Restore(_restore.Dequeue()); continue; }
                    if (_plateau != null) { Try(_plateau, "plateau"); _plateau = null; continue; }
                    if (_bestInputs == null || _rng.NextDouble() < .5) Explore();
                    else Optimize();
                }
            }

            void HandOver(int generation)
            {
                float sharedTime;
                long sharedTicks;
                lock (_owner.Shared) { sharedTime = _owner.Shared.Time; sharedTicks = _owner.Shared.RunTicks; }
                LastHandover = new Handover
                {
                    Cells = _archive.Select(p => (p.Key, p.Value.Source, p.Value.Length, p.Value.Picks, p.Value.Rank)).ToList(),
                    Pending = _restore.ToList(),
                    Plateau = _bestInputs != null && !Beats(sharedTime, sharedTicks, _best, _bestRunTicks) ? _bestInputs : null,
                };
                Exported = generation;
            }

            // Rebuilds a saved prefix's cells: the replay files every cell it reaches (as fast as it reached them before,
            // the inputs being the same), then the saved picks carry over so well-explored cells stay less likely.
            void Restore(Memory.Source source)
            {
                _restoreKeys = new long[source.Inputs.Count + 1];
                // Ticks the replay never reaches (cut behind the best) carry no picks.
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
                _bestBody = _replayBody;
                _bestFingerprint = _replayFingerprint;
            }

            void Explore()
            {
                if (_archive.Count == 0) { Try(RandomBurst(new List<InputTick>()), "explore"); return; }
                double total = 0;
                foreach (KeyValuePair<long, Cell> pair in _archive) total += Weight(pair.Key, pair.Value);
                double pick = _rng.NextDouble() * total;
                Cell chosen = null;
                foreach (KeyValuePair<long, Cell> pair in _archive)
                {
                    chosen = pair.Value;
                    if ((pick -= Weight(pair.Key, pair.Value)) <= 0) break;
                }
                chosen.Picks++;
                Try(RandomBurst(chosen.Source.Take(chosen.Length).ToList()), "explore", chosen.State);
            }

            // Before the first finish, timed cells reached later on the course timer (further along the course) weigh
            // more, so exploration pushes the frontier of long courses instead of re-rolling the well-covered start.
            double Weight(long key, Cell cell)
            {
                double w = key < 0 ? 3.0 : 1.0;
                if (key < 0 && _bestInputs == null && _maxTimedRank > 0)
                {
                    double reach = _goalMode ? (_maxTimedRank > _minTimedRank ? (cell.Rank - _minTimedRank) / (double)(_maxTimedRank - _minTimedRank) : 0) : cell.Rank / _maxTimedRank;
                    w *= 1 + 10 * Math.Pow(reach, 4);
                }
                return w / Math.Sqrt(cell.Picks + 1);
            }

            // Jump holds, in ticks. A normal jump or air jump reaches full height only when held until it rises slower
            // than 150 (~27 ticks; a release earlier cuts it to 50 up to 13 ticks later), so holds reach past that.
            static readonly int[] Holds = { 1, 3, 6, 10, 16, 24, 30, 40 };
            // How much longer a resumed prefix that ends mid-hold keeps holding (0 = release at once).
            static readonly int[] ResumeHolds = { 0, 3, 8, 16, 30, 60 };

            List<InputTick> RandomBurst(List<InputTick> inputs)
            {
                float x = inputs.Count > 0 ? inputs[inputs.Count - 1].x : 1f;
                // Releasing at every resume would cut every jump that crosses a cell boundary.
                int holdEnd = inputs.Count > 0 && inputs[inputs.Count - 1].held ? inputs.Count + ResumeHolds[_rng.Next(ResumeHolds.Length)] : 0;
                for (int chunk = _rng.Next(1, 4); chunk > 0; chunk--)
                {
                    if (_rng.NextDouble() < .35) x = new[] { -1f, 0f, 1f }[_rng.Next(3)];
                    int length = _rng.Next(3, 21);
                    int start = inputs.Count;
                    for (int i = 0; i < length; i++) inputs.Add(new InputTick { x = x, held = start + i < holdEnd });
                    // Full-TAS: now and then a chunk starts with a quick restart (never right after another one, nor on the
                    // window's first input: the prefix before it may end with one).
                    if (_goalMode && start > 0 && !inputs[start - 1].reset && _rng.NextDouble() < .05)
                    {
                        InputTick r = inputs[start]; r.reset = true; inputs[start] = r;
                    }
                    if (_rng.NextDouble() < .4) holdEnd = Hop(inputs, start, Holds[_rng.Next(Holds.Length)]);
                    if (_dash && _rng.NextDouble() < .25)
                    {
                        int at = start + _rng.Next(Math.Min(length, 4));
                        InputTick d = inputs[at];
                        d.dash = true;
                        if (_rng.NextDouble() < .4) d.dashJump = _rng.Next(1, 11);
                        inputs[at] = d;
                        // Superbounce (Solver.cs SuperBounce): jump buffered in the first dash frames against a wall.
                        if (d.dashJump == 0 && _rng.NextDouble() < .5)
                            holdEnd = SuperBounce(inputs, at, d.x == 0f ? (_rng.NextDouble() < .5 ? -1f : 1f) : Math.Sign(d.x));
                    }
                }
                return inputs;
            }

            // Press at `at`, held for `hold` ticks (clipped to the list), released after; returns the release tick.
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

            // Solver.cs SuperBounce: dash into a wall with jump pressed 0-5 ticks later, optionally steer away after.
            // Returns the jump's release tick.
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

            // Where the hold that starts with the press at `q` ends (the first tick not held, or a later press).
            static int HoldEnd(List<InputTick> t, int q)
            {
                int e = q + 1;
                while (e < t.Count && t[e].held && !t[e].press) e++;
                return e;
            }

            // Sets the hold of the press at `q` to end at `end`: held up to it, released there, not held after until the
            // old end.
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

            // A quick restart at `p`: not on the window's first input, never on two consecutive ticks (a real button is
            // released in between).
            static bool CanReset(List<InputTick> t, int p)
            {
                return p > 0 && p < t.Count && !t[p].reset && !t[p - 1].reset && (p + 1 >= t.Count || !t[p + 1].reset);
            }

            void Optimize()
            {
                List<InputTick> t = _bestInputs.ToList();
                int n = t.Count, p = _rng.Next(n);
                string[] kinds = _dash
                    ? new[] { "cut", "cut", "hop", "nojump", "stick", "insert", "dash", "dash", "dash", "dashjump", "dashjump", "latejump", "latejump",
                        "superbounce", "superbounce", "superbounce", "hold", "hold", "retime", "retime" }
                    : new[] { "cut", "cut", "hop", "nojump", "stick", "insert", "hold", "retime" };
                if (_goalMode) kinds = kinds.Concat(new[] { "reset", "noreset", "movereset" }).ToArray();
                switch (kinds[_rng.Next(kinds.Length)])
                {
                    case "cut": Cut(t, p, new[] { 1, 1, 2, 3, 5, 8 }[_rng.Next(6)]); break;
                    case "insert":
                        float x = new[] { -1f, 0f, 1f }[_rng.Next(3)];
                        int count = new[] { 1, 2, 4, 8, 15 }[_rng.Next(5)];
                        for (int i = 0; i < count; i++) t.Insert(p, new InputTick { x = x, held = t[p].held });
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
                    // Air jump height: a jump's hold length decides whether (and when) it is cut.
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
                    // Air jump timing (a stacked air jump climbs higher pressed later) and press/release order: moves a
                    // jump press or a dash a few ticks, keeping where the jump's hold ends.
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
                            dest.dash = true; dest.dashJump = from.dashJump;
                            from.dash = false; from.dashJump = 0;
                            t[q] = from; t[to] = dest;
                            break;
                        }
                        int end = HoldEnd(t, q);
                        bool heldBefore = q > 0 && t[q - 1].held;
                        from.press = false; t[q] = from;
                        dest.press = true; dest.held = true; dest.release = false; t[to] = dest;
                        // Earlier: hold from the new press. Later: not held before it (unless the jump was already held).
                        for (int i = Math.Min(q, to) + 1; i < Math.Max(q, to); i++) { InputTick k = t[i]; k.held = to < q || heldBefore; k.release = false; t[i] = k; }
                        if (to > q && !heldBefore) { InputTick k = t[q]; k.held = false; k.release = false; t[q] = k; }
                        if (end > to) SetHold(t, to, end);
                        break;
                    }
                    case "stick":
                    {
                        // Full left, rest, full right, or a stick short of full near the current one: a stick gives
                        // 0.3..0.8 there (MacroEngine.ControllerAxis), so full steps down to 0.8 and rest up to 0.3.
                        float was = Mathf.Abs(t[p].x), sign = t[p].x != 0f ? Mathf.Sign(t[p].x) : _rng.NextDouble() < .5 ? -1f : 1f;
                        float partial = sign * (was >= 1f ? .8f : was == 0f ? .3f : Mathf.Clamp(was + (_rng.NextDouble() < .5 ? -.1f : .1f), .3f, .8f));
                        float aim = new[] { -1f, 0f, 1f, partial }[_rng.Next(4)];
                        for (int i = p; i < Math.Min(n, p + _rng.Next(1, 15)); i++) { InputTick k = t[i]; k.x = aim; t[i] = k; }
                        break;
                    }
                    // Full-TAS: add, drop or move a quick restart.
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
                if (_goalMode)
                {
                    // A cut can bring two quick restarts together, or one to the window's first input.
                    if (t.Count > 0 && t[0].reset && !_bestInputs[0].reset) { InputTick k = t[0]; k.reset = false; t[0] = k; }
                    for (int i = 1; i < t.Count; i++)
                        if (t[i].reset && t[i - 1].reset) { InputTick k = t[i]; k.reset = false; t[i] = k; }
                }
                int s = 0;
                while (s < t.Count && s < _bestInputs.Count && t[s].Equals(_bestInputs[s])) s++;
                // From `converge` on the inputs equal the best's at the same ticks (only when the mutation kept the length:
                // a shift moves the time-driven world against the player). +12: a dash-jump pressed before it resolves.
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
                    // Fidelity check: the branch must match a from-start replay. Both run uncut: the optimize cut also
                    // fires inside the replayed prefix (a loop route revisiting a cell), which the branch never sees.
                    bool branch = Evaluate(inputs, from, null, null, false, out float branchTime, out _, out _);
                    Vector3 end = _sim.Player.transform.m_Position;
                    // The branch never judged the prefix (its source's), so the replay judges the same ticks only.
                    bool again = Evaluate(inputs, null, null, null, false, out float replay, out _, out _, from.Tick);
                    Interlocked.Increment(ref _owner.BranchChecks);
                    if (again == branch && Math.Abs(replay - branchTime) < .0001f && (end - _sim.Player.transform.m_Position).sqrMagnitude < .01f)
                        Interlocked.Increment(ref _owner.BranchMatches);
                }
                if (finished) Interlocked.Increment(ref _owner.Finishes);
                // Neutral drift: a mutation that finishes as fast as the best (or rejoins its state, so finishes exactly
                // like it) replaces it. Whole-tick timing makes the best sit on wide plateaus, and a faster route is often
                // several mutations away, each alone no faster (Course 1 NO_WJ 2.14 -> 2.06 moves a press a tick, turns
                // the stick, cuts the jump at one tick, then drops an earlier press; only the last one gains).
                // A saved plateau route (Memory) is the drift a worker had reached, so it replaces a best as fast too.
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
                _bestBody = _replayBody;
                _bestFingerprint = _replayFingerprint;
                if (improved) _owner.Offer(replayed, replayedTicks, _bestInputs, mode);
                else Interlocked.Increment(ref _owner.Drifts);
            }

            // TasPlugin.Evaluate: files map cells as the run reaches them, cuts runs already behind the best (judged
            // only after input tick `judgeAfter`). A replay that records `states` also records each tick's fingerprint;
            // a run whose inputs equal the best's from tick `converge` on stops once it is back in the best's state
            // (it would finish exactly like the best).
            bool Evaluate(List<InputTick> inputs, SimSnapshot from, SimSnapshot[] states, Dictionary<long, float> arrivals, bool cut,
                out float time, out int finishTick, out long runTicks, int judgeAfter = -1, int converge = int.MaxValue)
            {
                _converged = false;
                if (states != null)
                {
                    _replayBody = new (float, float, float, float, float, float)[inputs.Count + 1];
                    _replayFingerprint = new int?[inputs.Count + 1];
                }
                // A Full-TAS window can dash more often than a course: room for a physics tick without input per input.
                RunResult result = _sim.Run(inputs, from, tick =>
                {
                    if (states != null && tick < _replayBody.Length)
                    {
                        _replayBody[tick] = _sim.Physics.PlayerBody();
                        _replayFingerprint[tick] = _sim.Fingerprint();
                    }
                    else if (tick >= converge && _bestFingerprint != null && tick < _bestFingerprint.Length && _bestFingerprint[tick] is int best
                        && _sim.Physics.PlayerBody() == _bestBody[tick] && _sim.Fingerprint() == best)
                    {
                        _converged = true;
                        return false;
                    }
                    courseScript active = _sim.TimingCourse();
                    // Bonus and transition runs have no course timer; they are timed in physics ticks from the run start,
                    // as their finishes are.
                    bool timing = _sim.TimedFromRunStart || active != null;
                    long key = _goalMode ? GoalCellKey(_sim.Player.transform.m_Position, _sim.Progress) : CellKey(_sim.Player.transform.m_Position, timing);
                    bool losing = false;
                    float elapsed = _sim.Elapsed(active);
                    if (elapsed >= 0f)
                    {
                        if (tick > judgeAfter && elapsed > _best) losing = true;
                        if (cut && tick > judgeAfter && _bestArrival.TryGetValue(key, out float bestAt) && elapsed > bestAt + SegmentSlack) losing = true;
                        // The best route's last time in each cell: a loop route revisits cells, and cutting at the first
                        // visit would cut the best route itself once a mutation resumes past it.
                        if (arrivals != null) arrivals[key] = elapsed;
                    }
                    int filed = Math.Min(tick, inputs.Count);
                    if (_restoreKeys != null && filed < _restoreKeys.Length) _restoreKeys[filed] = key;
                    FileCell(inputs, filed, key, elapsed >= 0f ? elapsed : tick * .02f, states);
                    return !losing;
                }, _goalMode ? 400 + inputs.Count : 400);
                Interlocked.Increment(ref _owner.Runs);
                Interlocked.Add(ref _owner.Ticks, result.Stepped);
                time = result.Time;
                finishTick = result.FinishTick;
                runTicks = result.PhysicsTicks;
                return result.Finished;
            }
            (float, float, float, float, float, float)[] _replayBody, _bestBody;
            int?[] _replayFingerprint, _bestFingerprint;
            // The last Evaluate stopped because the run was back in the best's state (it would finish exactly like it).
            bool _converged;

            static long CellKey(Vector3 position, bool timing)
            {
                long key = ((long)Mathf.FloorToInt(position.x / CellSize) << 24) ^ (Mathf.FloorToInt(position.y / CellSize) & 0xFFFFFF);
                return timing ? -1 - Math.Abs(key) : key;
            }

            // Full-TAS: always timed (from the run start), and a map per goal reached, so a place visited again later in
            // the run is a new cell.
            static long GoalCellKey(Vector3 position, int progress)
            {
                long x = Mathf.FloorToInt(position.x / CellSize) & 0xFFFFFF, y = Mathf.FloorToInt(position.y / CellSize) & 0xFFFFFF;
                return -1 - (((long)progress << 48) | (x << 24) | y);
            }

            // A cell keeps its earliest arrival: by course time once the timer runs (dash ticks consume no input but still
            // cost 0.02 s each), by input tick before it. Only arrivals a run can resume from are filed: one reached after
            // the inputs ran out has no snapshot, and exploring from it would replay its whole prefix from the start.
            void FileCell(List<InputTick> inputs, int tick, long key, float rank, SimSnapshot[] states)
            {
                bool better = !_archive.TryGetValue(key, out Cell cell) || cell.Rank > rank || (cell.Rank == rank && cell.Length > tick);
                bool resumable = tick > 0 && tick < inputs.Count && !_sim.Engine.InputsExhausted;
                SimSnapshot state = resumable && (better || states != null) ? _sim.Capture(tick) : null;
                if (states != null && state != null) states[tick] = state;
                if (!better || state == null) return;
                _archive[key] = new Cell { Source = inputs, Length = tick, State = state, Rank = rank };
                if (key < 0 && rank > _maxTimedRank) _maxTimedRank = rank;
                if (_goalMode && rank < _minTimedRank) _minTimedRank = rank;
            }
        }
    }
}
