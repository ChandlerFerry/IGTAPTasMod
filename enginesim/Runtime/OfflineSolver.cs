using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace IGTAP.EngineSim
{
    // The in-game solver (Solver.cs) running offline: explore (Go-Explore over 48 px map cells x course timer
    // running) and optimize (mutate the best finish), judged by the game's own timer. Each worker thread owns a
    // world and archive; workers share only the best finish, which each adopts by replaying it.
    public sealed class OfflineSolver
    {
        public sealed class Best
        {
            public float Time = float.PositiveInfinity;
            public List<InputTick> Inputs;
            public int Version;
        }

        public readonly string Capabilities;
        readonly Func<Simulation> _createSimulation;
        readonly List<List<InputTick>> _seeds;
        public readonly Best Shared = new Best();
        public long Runs, Ticks, Finishes, BranchChecks, BranchMatches;
        public event Action<float, List<InputTick>, string> Improved;
        volatile bool _stop;

        public OfflineSolver(Func<Simulation> createSimulation, string capabilities, List<List<InputTick>> seeds)
        {
            _createSimulation = createSimulation;
            Capabilities = capabilities;
            _seeds = seeds;
        }

        public void Stop() { _stop = true; }

        public Thread[] Start(int workers)
        {
            var threads = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                int seed = Environment.TickCount * 31 + i * 7919;
                // Worker 0 evaluates the seed routes first; the others start exploring right away.
                var seeds = i == 0 ? _seeds : new List<List<InputTick>>();
                threads[i] = new Thread(() => new Worker(this, _createSimulation(), seed, seeds).Loop()) { IsBackground = true, Name = "solver-" + i };
                threads[i].Start();
            }
            return threads;
        }

        bool Offer(float time, List<InputTick> inputs, string how)
        {
            lock (Shared)
            {
                if (time >= Shared.Time - .0001f) return false;
                Shared.Time = time;
                Shared.Inputs = inputs;
                Shared.Version++;
            }
            Improved?.Invoke(time, inputs, how);
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
            List<InputTick> _bestInputs;
            SimSnapshot[] _bestStates;
            Dictionary<long, float> _bestArrival = new Dictionary<long, float>();
            int _sharedVersion;
            float _maxTimedRank;
            // Ground/wall contact per tick of the last from-start evaluation, and the ticks where the best route's contact
            // changes (lands, leaves a ledge, touches or leaves a wall): where coyote, buffered and wall jumps live.
            byte[] _flags;
            List<int> _bestEdges = new List<int>();
            static readonly System.Reflection.FieldInfo OnGroundField = typeof(Movement).GetField("onGround", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            static readonly System.Reflection.FieldInfo OnWallField = typeof(Movement).GetField("OnWall", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            static readonly bool Moves = Environment.GetEnvironmentVariable("IGTAP_MOVES") != "0";

            public Worker(OfflineSolver owner, Simulation sim, int seed, List<List<InputTick>> seeds)
            {
                _owner = owner;
                _sim = sim;
                _rng = new System.Random(seed);
                _seeds = new Queue<List<InputTick>>(seeds.Select(s => s.ToList()));
                _seeds.Enqueue(new List<InputTick>());
                _dash = owner.Capabilities == "wjdash";
            }

            public void Loop()
            {
                while (!_owner._stop)
                {
                    AdoptShared();
                    if (_seeds.Count > 0) { Try(_seeds.Dequeue(), "seed"); continue; }
                    if (_bestInputs == null || _rng.NextDouble() < .5) Explore();
                    else Optimize();
                }
            }

            void AdoptShared()
            {
                List<InputTick> inputs;
                float time;
                lock (_owner.Shared)
                {
                    if (_owner.Shared.Version == _sharedVersion) return;
                    _sharedVersion = _owner.Shared.Version;
                    inputs = _owner.Shared.Inputs;
                    time = _owner.Shared.Time;
                }
                if (inputs == null || time >= _best - .0001f) return;
                var states = new SimSnapshot[inputs.Count];
                var arrivals = new Dictionary<long, float>();
                if (!Evaluate(inputs, null, states, arrivals, false, out float replayed, out int finishTick)) return;
                _best = replayed;
                _bestInputs = inputs.Take(finishTick + 1).ToList();
                _bestStates = states;
                _bestArrival = arrivals;
                _bestEdges = Edges(_flags);
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
                if (key < 0 && _bestInputs == null && _maxTimedRank > 0) w *= 1 + 10 * Math.Pow(cell.Rank / _maxTimedRank, 4);
                return w / Math.Sqrt(cell.Picks + 1);
            }

            List<InputTick> RandomBurst(List<InputTick> inputs)
            {
                float x = inputs.Count > 0 ? inputs[inputs.Count - 1].x : 1f;
                for (int chunk = _rng.Next(1, 4); chunk > 0; chunk--)
                {
                    if (_rng.NextDouble() < .35) x = new[] { -1f, 0f, 1f }[_rng.Next(3)];
                    int length = _rng.Next(3, 21);
                    int start = inputs.Count;
                    for (int i = 0; i < length; i++) inputs.Add(new InputTick { x = x });
                    if (_rng.NextDouble() < .4) Hop(inputs, start, new[] { 1, 3, 6, 10, 16 }[_rng.Next(5)]);
                    if (_dash && _rng.NextDouble() < .25)
                    {
                        int at = start + _rng.Next(Math.Min(length, 4));
                        InputTick d = inputs[at];
                        d.dash = true;
                        if (_rng.NextDouble() < .4) d.dashJump = _rng.Next(1, 11);
                        inputs[at] = d;
                        // Superbounce (Solver.cs SuperBounce): jump buffered in the first dash frames against a wall.
                        if (d.dashJump == 0 && _rng.NextDouble() < .5) SuperBounce(inputs, at, d.x == 0f ? (_rng.NextDouble() < .5 ? -1f : 1f) : Math.Sign(d.x));
                    }
                }
                return inputs;
            }

            static void Hop(List<InputTick> t, int at, int hold)
            {
                if (at >= t.Count) return;
                InputTick first = t[at];
                first.press = true; first.held = true; first.release = false;
                t[at] = first;
                for (int i = at + 1; i < Math.Min(t.Count, at + hold); i++) { InputTick k = t[i]; k.held = true; t[i] = k; }
                if (at + hold < t.Count) { InputTick r = t[at + hold]; r.held = false; r.release = true; t[at + hold] = r; }
            }

            // Solver.cs SuperBounce: dash into a wall with jump pressed 0-5 ticks later, optionally steer away after.
            void SuperBounce(List<InputTick> t, int at, float toward)
            {
                if (at < 0 || at >= t.Count) return;
                for (int i = at; i < Math.Min(t.Count, at + 8); i++) { InputTick k = t[i]; k.x = toward; t[i] = k; }
                InputTick d = t[at]; d.dash = true; d.dashJump = 0; t[at] = d;
                Hop(t, Math.Min(t.Count - 1, at + _rng.Next(0, 6)), new[] { 3, 6, 10, 16 }[_rng.Next(4)]);
                if (_rng.NextDouble() < .5)
                    for (int i = at + 8; i < Math.Min(t.Count, at + 8 + _rng.Next(1, 20)); i++) { InputTick k = t[i]; k.x = -toward; t[i] = k; }
            }

            static void Cut(List<InputTick> t, int at, int count)
            {
                if (at < t.Count) t.RemoveRange(at, Math.Min(count, t.Count - at));
            }

            void Optimize()
            {
                List<InputTick> t = _bestInputs.ToList();
                int n = t.Count, p = _rng.Next(n);
                string[] kinds = _dash
                    ? new[] { "cut", "cut", "hop", "nojump", "stick", "insert", "dash", "dash", "dash", "dashjump", "dashjump", "latejump", "latejump",
                        "superbounce", "superbounce", "superbounce" }
                    : new[] { "cut", "cut", "hop", "nojump", "stick", "insert" };
                if (Moves) kinds = kinds.Concat(_dash ? new[] { "edgejump", "edgejump", "retime", "shift", "edgedash", "dashshift" } : new[] { "edgejump", "edgejump", "retime", "shift" }).ToArray();
                List<int> presses = Enumerable.Range(0, n).Where(i => t[i].press).ToList();
                switch (kinds[_rng.Next(kinds.Length)])
                {
                    // Jump a few ticks around a contact change: the last coyote tick off a ledge (maxCoyoteFrames), a wall
                    // jump in the wall-coyote window (maxWallCoyoteFrames), or a press buffered before landing / touching a wall.
                    case "edgejump":
                    {
                        if (_bestEdges.Count == 0) return;
                        int at = Math.Max(0, Math.Min(n - 1, _bestEdges[_rng.Next(_bestEdges.Count)] + _rng.Next(-8, 3)));
                        Hop(t, at, new[] { 1, 2, 3, 5, 8, 12, 16 }[_rng.Next(7)]);
                        if (_rng.NextDouble() < .5) Cut(t, at + _rng.Next(1, 25), _rng.Next(1, 10));
                        break;
                    }
                    // Move a release by a tick or three: the jump cut (Velocity.y 150-580 -> 50) and the wall-jump lock
                    // cancel it gives depend on the exact release tick.
                    case "retime":
                    {
                        if (presses.Count == 0) return;
                        int q = presses[_rng.Next(presses.Count)];
                        int hold = ReleaseOf(t, q) - q + new[] { -3, -2, -1, 1, 2, 3 }[_rng.Next(6)];
                        int next = presses.FirstOrDefault(i => i > q);
                        if (next > q) hold = Math.Min(hold, next - q - 1);
                        if (hold < 1) return;
                        ClearHop(t, q);
                        Hop(t, q, hold);
                        break;
                    }
                    // Move a whole press (and its hold) by a tick or three.
                    case "shift":
                    {
                        if (presses.Count == 0) return;
                        int q = presses[_rng.Next(presses.Count)];
                        int hold = ReleaseOf(t, q) - q;
                        int to = q + new[] { -3, -2, -1, 1, 2, 3 }[_rng.Next(6)];
                        if (to < 0 || to >= n) return;
                        ClearHop(t, q);
                        Hop(t, to, hold);
                        break;
                    }
                    // Dash on a contact change: off a ledge, or on the wall-touch tick, where the dash reverses away from
                    // the wall (getPlayerControlledInput: OnWall && wallCoyoteFrames == maxWallCoyoteFrames flips facing).
                    case "edgedash":
                    {
                        if (_bestEdges.Count == 0) return;
                        int at = Math.Max(0, Math.Min(n - 1, _bestEdges[_rng.Next(_bestEdges.Count)] + _rng.Next(-3, 3)));
                        InputTick k = t[at]; k.dash = true; k.x = _rng.NextDouble() < .5 ? -1f : 1f; k.dashJump = _rng.NextDouble() < .3 ? _rng.Next(1, 7) : 0; t[at] = k;
                        if (_rng.NextDouble() < .5) Cut(t, at + _rng.Next(1, 25), _rng.Next(1, 15));
                        break;
                    }
                    case "dashshift":
                    {
                        List<int> dashes = Enumerable.Range(0, n).Where(i => t[i].dash).ToList();
                        if (dashes.Count == 0) return;
                        int q = dashes[_rng.Next(dashes.Count)], to = q + new[] { -2, -1, 1, 2 }[_rng.Next(4)];
                        if (to < 0 || to >= n) return;
                        InputTick src = t[q], k = t[to];
                        k.dash = true; k.dashJump = src.dashJump; t[to] = k;
                        src.dash = false; src.dashJump = 0; t[q] = src;
                        break;
                    }
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
                        Hop(t, p, new[] { 1, 2, 4, 7, 11, 16 }[_rng.Next(6)]);
                        Cut(t, p + _rng.Next(1, 30), _rng.Next(0, 10));
                        break;
                    case "nojump":
                    {
                        List<int> actions = Enumerable.Range(0, n).Where(i => t[i].press || t[i].dash).ToList();
                        if (actions.Count == 0) return;
                        int q = actions[_rng.Next(actions.Count)];
                        InputTick k = t[q]; k.press = k.dash = false; k.dashJump = 0; t[q] = k;
                        break;
                    }
                    case "stick":
                    {
                        float aim = new[] { -1f, 0f, 1f, Mathf.Clamp(t[p].x + (_rng.NextDouble() < .5 ? -.1f : .1f), -1f, 1f) }[_rng.Next(4)];
                        for (int i = p; i < Math.Min(n, p + _rng.Next(1, 15)); i++) { InputTick k = t[i]; k.x = aim; t[i] = k; }
                        break;
                    }
                }
                int s = 0;
                while (s < t.Count && s < _bestInputs.Count && t[s].Equals(_bestInputs[s])) s++;
                s = Math.Min(Math.Min(s, t.Count - 1), _bestStates.Length - 1);
                while (s > 0 && _bestStates[s] == null) s--;
                Try(t, "optimize", s > 0 ? _bestStates[s] : null);
            }

            void Try(List<InputTick> inputs, string mode, SimSnapshot from = null)
            {
                if (inputs.Count == 0) inputs.Add(new InputTick());
                if (from != null && from.Tick >= inputs.Count) from = null;
                bool finished = Evaluate(inputs, from, null, null, mode == "optimize", out float time, out _);
                if (from != null && _rng.Next(100) == 0)
                {
                    // Fidelity check: the branch must match a from-start replay. Both run uncut: the optimize cut also
                    // fires inside the replayed prefix (a loop route revisiting a cell), which the branch never sees.
                    bool branch = Evaluate(inputs, from, null, null, false, out float branchTime, out _);
                    Vector3 end = _sim.Player.transform.m_Position;
                    bool again = Evaluate(inputs, null, null, null, false, out float replay, out _);
                    Interlocked.Increment(ref _owner.BranchChecks);
                    if (again == branch && Math.Abs(replay - branchTime) < .0001f && (end - _sim.Player.transform.m_Position).sqrMagnitude < .01f)
                        Interlocked.Increment(ref _owner.BranchMatches);
                }
                if (!finished) return;
                Interlocked.Increment(ref _owner.Finishes);
                if (time >= _best - .0001f) return;
                var states = new SimSnapshot[inputs.Count];
                var arrivals = new Dictionary<long, float>();
                if (!Evaluate(inputs, null, states, arrivals, false, out float replayed, out int finishTick) || replayed >= _best - .0001f) return;
                _best = replayed;
                _bestInputs = inputs.Take(finishTick + 1).ToList();
                _bestStates = states;
                _bestArrival = arrivals;
                _bestEdges = Edges(_flags);
                _owner.Offer(replayed, _bestInputs, mode);
            }

            // TasPlugin.Evaluate: files map cells as the run reaches them, cuts runs already behind the best.
            bool Evaluate(List<InputTick> inputs, SimSnapshot from, SimSnapshot[] states, Dictionary<long, float> arrivals, bool cut,
                out float time, out int finishTick)
            {
                if (states != null) _flags = new byte[states.Length];
                RunResult result = _sim.Run(inputs, from, tick =>
                {
                    if (states != null && tick < _flags.Length)
                        _flags[tick] = (byte)(((bool)OnGroundField.GetValue(_sim.Player) ? 1 : 0) | ((bool)OnWallField.GetValue(_sim.Player) ? 2 : 0));
                    courseScript active = _sim.TimingCourse();
                    bool timing = active != null;
                    long key = CellKey(_sim.Player.transform.m_Position, timing);
                    bool losing = false;
                    if (timing)
                    {
                        float elapsed = Simulation.CourseTime(active);
                        if (elapsed > _best) losing = true;
                        if (cut && _bestArrival.TryGetValue(key, out float bestAt) && elapsed > bestAt + SegmentSlack) losing = true;
                        // The best route's last time in each cell: a loop route revisits cells, and cutting at the first
                        // visit would cut the best route itself once a mutation resumes past it.
                        if (arrivals != null) arrivals[key] = elapsed;
                    }
                    FileCell(inputs, Math.Min(tick, inputs.Count), key, timing ? Simulation.CourseTime(active) : tick * .02f, states);
                    return !losing;
                });
                Interlocked.Increment(ref _owner.Runs);
                Interlocked.Add(ref _owner.Ticks, result.Stepped);
                time = result.Time;
                finishTick = result.FinishTick;
                return result.Finished;
            }

            static List<int> Edges(byte[] flags)
            {
                var edges = new List<int>();
                for (int i = 2; i < flags.Length; i++) if (flags[i] != flags[i - 1]) edges.Add(i);
                return edges;
            }

            // The press/hold run starting at q: returns the tick of its release (or the list end).
            static int ReleaseOf(List<InputTick> t, int q)
            {
                int r = q + 1;
                while (r < t.Count && t[r].held && !t[r].press && !t[r].release) r++;
                return r;
            }

            static void ClearHop(List<InputTick> t, int q)
            {
                int r = ReleaseOf(t, q);
                for (int i = q; i <= Math.Min(r, t.Count - 1); i++)
                {
                    InputTick k = t[i];
                    if (i > q && k.press) break;
                    k.held = false; k.release = false; if (i == q) k.press = false;
                    t[i] = k;
                }
            }

            static long CellKey(Vector3 position, bool timing)
            {
                long key = ((long)Mathf.FloorToInt(position.x / CellSize) << 24) ^ (Mathf.FloorToInt(position.y / CellSize) & 0xFFFFFF);
                return timing ? -1 - Math.Abs(key) : key;
            }

            // A cell keeps its earliest arrival: by course time once the timer runs (dash ticks consume no input but still
            // cost 0.02 s each), by input tick before it.
            void FileCell(List<InputTick> inputs, int tick, long key, float rank, SimSnapshot[] states)
            {
                bool better = !_archive.TryGetValue(key, out Cell cell) || cell.Rank > rank || (cell.Rank == rank && cell.Length > tick);
                bool resumable = tick > 0 && tick < inputs.Count && !_sim.Engine.InputsExhausted;
                SimSnapshot state = resumable && (better || states != null) ? _sim.Capture(tick) : null;
                if (states != null && state != null) states[tick] = state;
                if (!better) return;
                _archive[key] = new Cell { Source = inputs, Length = tick, State = state, Rank = rank };
                if (key < 0 && rank > _maxTimedRank) _maxTimedRank = rank;
            }
        }
    }
}
