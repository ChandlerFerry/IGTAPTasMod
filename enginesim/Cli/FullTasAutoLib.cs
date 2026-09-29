using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    // Stage 1 of --auto (docs/fulltas.md, "Auto: goal-state library"): every run the loop plays from the start files its exact
    // states at goal touches and chain points (quick restarts, landings, wall contacts, dash ends). Two states join only when they
    // are bit-identical in everything that moves the player (StateKey); a stitched route is the first run's inputs up to the join
    // and the second run's after it, so it plays as one continuous run. The best known route is the shortest path through those
    // joins, recomputed whenever a run is filed; it is kept only after a replay from the start reproduces it.
    sealed class StateLibrary : IStateLibrary
    {
        public sealed class LibRun
        {
            public int Id;
            public string How;
            public InputRuns Inputs;
            public int Progress;
            public long End = long.MaxValue;
            /// <summary>This run's states in time order (ids into the library).</summary>
            public List<int> States = new List<int>();
            public ulong Hash;
        }

        readonly List<LibState> _states = new List<LibState>();
        readonly List<LibRun> _runs = new List<LibRun>();
        readonly Dictionary<(int goal, ulong key), List<int>> _byKey = new Dictionary<(int, ulong), List<int>>();
        readonly HashSet<ulong> _runHashes = new HashSet<ulong>();
        /// <summary>Joins a replay showed are not exact: (goal, key, the run switched to).</summary>
        readonly HashSet<(int, ulong, int)> _banned = new HashSet<(int, ulong, int)>();
        readonly int _goals;
        public readonly int MaxRuns;
        int _version, _toEndVersion = -1;
        long[] _toEnd = Array.Empty<long>();

        public StateLibrary(int goals, int maxRuns) { _goals = goals; MaxRuns = maxRuns; }

        public int Runs => _runs.Count(r => r != null);
        public int Version => _version;
        public LibState State(int id) => _states[id];
        public LibRun Run(int id) => _runs[id];
        public IEnumerable<LibRun> AllRuns => _runs.Where(r => r != null);
        public bool Knows(ulong hash) => _runHashes.Contains(hash);

        /// <summary>A traced run (FullTas.TraceRun fills it); returns its id, -1 when already known.</summary>
        public int AddTraced(LibRun run, List<LibState> states)
        {
            if (!_runHashes.Add(run.Hash)) return -1;
            run.Id = _runs.Count;
            _runs.Add(run);
            foreach (LibState s in states)
            {
                s.Id = _states.Count;
                s.Run = run.Id;
                _states.Add(s);
                run.States.Add(s.Id);
                var k = (s.Goal, s.Key);
                if (!_byKey.TryGetValue(k, out List<int> list)) _byKey[k] = list = new List<int>();
                list.Add(s.Id);
            }
            _version++;
            if (Runs > MaxRuns) Prune();
            return run.Id;
        }

        int IStateLibrary.Add(IReadOnlyList<InputTick> inputs, string how) => throw new NotSupportedException("the library is filled by FullTas.TraceRun (it replays on the loop's simulation)");

        /// <summary>
        /// The capped Pareto set: a run is dropped when none of its states is among the 3 soonest at its (goal, kind) or among the
        /// 3 fastest-moving there, and it isn't the best (the newest complete run with the lowest end). Its states stay filed as
        /// dead entries (ids are stable) but no longer join.
        /// </summary>
        void Prune()
        {
            var keep = new HashSet<int>();
            LibRun best = AllRuns.Where(r => r.Progress == _goals).OrderBy(r => r.End).FirstOrDefault();
            if (best != null) keep.Add(best.Id);
            foreach (var g in AllRuns.SelectMany(r => r.States).Select(id => _states[id]).GroupBy(s => (s.Goal, s.Kind)))
            {
                foreach (LibState s in g.OrderBy(s => s.Tick).Take(3)) keep.Add(s.Run);
                foreach (LibState s in g.OrderByDescending(s => s.Features.Speed).Take(3)) keep.Add(s.Run);
            }
            // Oldest first among the rest.
            foreach (LibRun r in AllRuns.ToList())
            {
                if (Runs <= MaxRuns) break;
                if (keep.Contains(r.Id)) continue;
                _runs[r.Id] = null;
                foreach (int id in r.States) _byKey[(_states[id].Goal, _states[id].Key)].Remove(id);
            }
            _version++;
        }

        public void Ban(int goal, ulong key, int toRun) { _banned.Add((goal, key, toRun)); _version++; }

        /// <summary>States at a join: ones whose (goal, key) another live run shares.</summary>
        bool Joins(LibState s)
        {
            List<int> same = _byKey[(s.Goal, s.Key)];
            if (same.Count < 2) return false;
            foreach (int id in same) if (_states[id].Run != s.Run) return true;
            return false;
        }

        List<int> Relevant(LibRun r)
        {
            var list = new List<int>();
            for (int i = 0; i < r.States.Count; i++)
            {
                LibState s = _states[r.States[i]];
                if (i == 0 || (s.Kind & LibPoint.Goal) != 0 && s.Goal == _goals || Joins(s)) list.Add(s.Id);
            }
            return list;
        }

        public IReadOnlyList<Segment> Continuations(int stateId)
        {
            LibState s0 = _states[stateId];
            var list = new List<Segment>();
            foreach (int id in _byKey[(s0.Goal, s0.Key)])
            {
                LibRun r = _runs[_states[id].Run];
                if (r == null) continue;
                List<int> rel = Relevant(r);
                int at = rel.IndexOf(id);
                if (at < 0 || at + 1 >= rel.Count) continue;
                LibState a = _states[id], b = _states[rel[at + 1]];
                list.Add(new Segment { From = a.Id, To = b.Id, Run = r.Id, Ticks = b.Tick - a.Tick, Inputs = Slice(r.Inputs, a.Input, b.Input) });
            }
            return list;
        }

        static InputRuns Slice(InputRuns all, int from, int to)
        {
            var b = new InputRuns.Builder();
            int at = 0;
            foreach (var (tick, count) in all.Runs(from))
            {
                int n = Math.Min(count, to - from - at);
                if (n <= 0) break;
                b.Add(tick, n);
                at += n;
            }
            return b.Build();
        }

        public IReadOnlyList<LibState> StatesAt(int goal)
        {
            var list = new List<LibState>();
            foreach (LibRun r in AllRuns)
                foreach (int id in r.States)
                {
                    LibState s = _states[id];
                    if (s.Goal == goal && (s.Kind & LibPoint.Goal) != 0) list.Add(s);
                }
            return list;
        }

        /// <summary>
        /// Dijkstra over the join graph: along a run from one relevant state to its next (the ticks between them), and at a join to
        /// any other live run's state with the same (goal, key) at no cost, unless a replay banned that switch. The end is a state
        /// filed at the last goal's touch.
        /// </summary>
        public (IReadOnlyList<Segment> path, long end) ShortestPath()
        {
            var rel = new Dictionary<int, List<int>>();
            var next = new Dictionary<int, int>();
            foreach (LibRun r in AllRuns)
            {
                List<int> list = Relevant(r);
                rel[r.Id] = list;
                for (int i = 0; i + 1 < list.Count; i++) next[list[i]] = list[i + 1];
            }
            var dist = new Dictionary<int, long>();
            var prev = new Dictionary<int, int>();
            var queue = new PriorityQueue<int, long>();
            foreach (LibRun r in AllRuns)
            {
                int s = rel[r.Id][0];
                dist[s] = 0;
                queue.Enqueue(s, 0);
            }
            int end = -1;
            long endTick = long.MaxValue;
            while (queue.TryDequeue(out int u, out long du))
            {
                if (dist[u] != du) continue;
                LibState su = _states[u];
                if ((su.Kind & LibPoint.Goal) != 0 && su.Goal == _goals) { end = u; endTick = du; break; }
                void Relax(int v, long d)
                {
                    if (dist.TryGetValue(v, out long dv) && dv <= d) return;
                    dist[v] = d;
                    prev[v] = u;
                    queue.Enqueue(v, d);
                }
                if (next.TryGetValue(u, out int n)) Relax(n, du + _states[n].Tick - su.Tick);
                foreach (int v in _byKey[(su.Goal, su.Key)])
                {
                    LibState sv = _states[v];
                    if (v == u || sv.Run == su.Run || _runs[sv.Run] == null || _banned.Contains((su.Goal, su.Key, sv.Run))) continue;
                    Relax(v, du);
                }
            }
            if (end < 0) return (Array.Empty<Segment>(), long.MaxValue);
            // Walk back: consecutive states on one run make a segment; a switch starts the next.
            var chain = new List<int> { end };
            for (int v = end; prev.TryGetValue(v, out int p); v = p) chain.Add(p);
            chain.Reverse();
            var segments = new List<Segment>();
            int from = chain[0];
            for (int i = 1; i < chain.Count; i++)
            {
                LibState a = _states[chain[i - 1]], b = _states[chain[i]];
                if (a.Run == b.Run) continue;
                // A switch at a: the segment on a's run ends there.
                LibState f = _states[from];
                segments.Add(new Segment { From = f.Id, To = a.Id, Run = a.Run, Ticks = a.Tick - f.Tick, Inputs = Slice(_runs[a.Run].Inputs, f.Input, a.Input) });
                from = b.Id;
            }
            LibState last = _states[from], stop = _states[end];
            LibRun tail = _runs[stop.Run];
            // The last segment runs to the end of its run's inputs (the strict finish's ticks after the last goal).
            segments.Add(new Segment { From = last.Id, To = stop.Id, Run = stop.Run, Ticks = stop.Tick - last.Tick, Inputs = Slice(tail.Inputs, last.Input, tail.Inputs.Count) });
            return (segments, endTick);
        }

        /// <summary>Ticks from a state to the run's end on the fastest known way on (long.MaxValue: none known).</summary>
        public long ToEnd(int stateId)
        {
            if (_toEndVersion != _version) ComputeToEnd();
            return stateId < _toEnd.Length ? _toEnd[stateId] : long.MaxValue;
        }

        void ComputeToEnd()
        {
            _toEnd = Enumerable.Repeat(long.MaxValue, _states.Count).ToArray();
            // Backward over each run (every state, not only joins), then across joins, until nothing improves.
            bool changed = true;
            for (int pass = 0; changed && pass < 8; pass++)
            {
                changed = false;
                foreach (LibRun r in AllRuns)
                {
                    long best = long.MaxValue;
                    long bestTick = 0;
                    for (int i = r.States.Count - 1; i >= 0; i--)
                    {
                        LibState s = _states[r.States[i]];
                        if ((s.Kind & LibPoint.Goal) != 0 && s.Goal == _goals) { best = 0; bestTick = s.Tick; }
                        foreach (int v in _byKey[(s.Goal, s.Key)])
                            if (_runs[_states[v].Run] != null && _toEnd[v] != long.MaxValue && (best == long.MaxValue || _toEnd[v] < best - (bestTick - s.Tick)))
                            {
                                best = _toEnd[v];
                                bestTick = s.Tick;
                            }
                        long here = best == long.MaxValue ? long.MaxValue : best + (bestTick - s.Tick);
                        if (here < _toEnd[s.Id]) { _toEnd[s.Id] = here; changed = true; }
                    }
                }
            }
            _toEndVersion = _version;
        }

        public string Describe()
        {
            int joins = _byKey.Count(k => k.Value.Select(id => _states[id].Run).Distinct().Count() > 1);
            return $"{Runs} runs, {_states.Count} states, {joins} join points";
        }
    }

    sealed partial class FullTas
    {
        StateLibrary _lib;
        /// <summary>--stages N: 0 AutoLoop's loop as it was; 1 the goal-state library and exact stitching; 2 + the cost-to-go;
        /// 3 + successive halving; 4 + macro edges (the default).</summary>
        int _stages = 4;

        static readonly FieldInfo[] KeyMoveFields = IGTAPTasMod.FieldState.For(typeof(Movement));
        static readonly int KeyDashCooldown = Array.FindIndex(KeyMoveFields, f => f.Name == "dashCooldown");
        static readonly int KeyWallCoyote = Array.FindIndex(KeyMoveFields, f => f.Name == "wallCoyoteFrames");
        static readonly int KeySinceDash = Array.FindIndex(KeyMoveFields, f => f.Name == "framesSinceLastDash");
        static readonly int KeyBoostMax = Array.FindIndex(KeyMoveFields, f => f.Name == "dashBoostMax");
        static readonly FieldInfo StepFromField = typeof(Simulation).GetField("_stepFrom", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo StepToField = typeof(Simulation).GetField("_stepTo", BindingFlags.Instance | BindingFlags.NonPublic);
        // The input engine's pending press state (a dash's jump frame, a lock or turn armed, a pause); its counters are left out.
        static readonly FieldInfo[] KeyEngineFields = new[] { "_wallJumpTriggered", "_rawJumpHeld", "_dashJumpFrame", "_dashJumpAxis", "_springDashFrame",
                "_springTicks", "_lockArmed", "_lockX", "_lockY", "_pendingTurnX", "_pendingTurnY", "_turnArmed", "_turnX", "_turnY", "_pauseTicksLeft",
                "_pauseFrames", "_pauseFrame", "_tickLocked", "_tickStick", "_quickRestartStep" }
            .Select(n => typeof(MacroEngine).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)).Where(f => f != null).ToArray();
        object[] _keyValues = Array.Empty<object>();

        static ulong Mix(ulong h, ulong v) { h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2); return h * 1099511628211UL; }

        static ulong Bits(float f) => (uint)BitConverter.SingleToInt32Bits(f);

        static ulong ValueHash(object v)
        {
            switch (v)
            {
                case null: return 0x51;
                case float f: return Bits(f);
                case double d: return (ulong)BitConverter.DoubleToInt64Bits(d);
                case int i: return (ulong)(uint)i;
                case long l: return (ulong)l;
                case bool b: return b ? 3UL : 5UL;
                case Vector2 v2: return Mix(Bits(v2.x), Bits(v2.y));
                case Vector3 v3: return Mix(Mix(Bits(v3.x), Bits(v3.y)), Bits(v3.z));
                case UnityEngine.Object o: return (ulong)(uint)o.GetInstanceID() * 31;
                case string s:
                {
                    ulong h = 14695981039346656037UL;
                    foreach (char c in s) h = (h ^ c) * 1099511628211UL;
                    return h;
                }
                case System.Collections.IEnumerable items:
                {
                    ulong h = 0x77;
                    foreach (object item in items) h = Mix(h, ValueHash(item));
                    return h;
                }
                default:
                    return v.GetType().IsEnum ? (ulong)Convert.ToInt64(v) : (ulong)(uint)v.GetHashCode();
            }
        }

        /// <summary>
        /// The exact-join key of the simulation's state now: every Movement field but presentation ones (dashCooldown and
        /// wallCoyoteFrames past 0, and framesSinceLastDash past its last read, count as one value each: the game only compares them
        /// with those thresholds, docs/fulltas.md "Reset segments"), the player's world place, the world's origin, the body, the
        /// interpolation's ends, the animator state, the player's invokes and coroutines, the input engine's pending press state and
        /// the goal tracker (goals reached, kept boxes). The world's economy is not in it: a stitched route replays it.
        /// </summary>
        ulong StateKey(Simulation sim)
        {
            if (_keyValues.Length < KeyMoveFields.Length) _keyValues = new object[KeyMoveFields.Length];
            IGTAPTasMod.FieldState.Read(sim.Player, _keyValues);
            float boost = KeyBoostMax >= 0 && _keyValues[KeyBoostMax] is float bm ? bm : 0f;
            float dashBound = Math.Max(boost * 1.4f, 4f);
            ulong h = 1469598103934665603UL;
            for (int i = 0; i < KeyMoveFields.Length; i++)
            {
                if (Simulation.MovementPresentationFields.Contains(KeyMoveFields[i].Name)) continue;
                object v = _keyValues[i];
                if ((i == KeyDashCooldown || i == KeyWallCoyote) && v is float t && t < 0f) v = -1f;
                else if (i == KeySinceDash && v is float s && s >= dashBound) v = dashBound;
                h = Mix(h, ValueHash(v));
            }
            WorldPos w = sim.PlayerWorld;
            ScenePos o = sim.WorldOriginInScene;
            h = Mix(Mix(Mix(Mix(h, Bits(w.x)), Bits(w.y)), Bits(o.x)), Bits(o.y));
            var body = sim.Physics.PlayerBody();
            h = Mix(Mix(Mix(Mix(Mix(Mix(h, Bits(body.px)), Bits(body.py)), Bits(body.angle)), Bits(body.vx)), Bits(body.vy)), Bits(body.w));
            h = Mix(Mix(h, ValueHash(StepFromField?.GetValue(sim))), ValueHash(StepToField?.GetValue(sim)));
            h = Mix(h, (ulong)(uint)(sim.Player.animator?.GetInteger("Animation") ?? 0));
            foreach (Scheduler.Pending p in sim.Scheduler.Queue)
                if (ReferenceEquals(p.Target, sim.Player)) h = Mix(Mix(h, (ulong)(uint)(p.Due - sim.Scheduler.Tick)), ValueHash(p.Method));
            foreach (Scheduler.Routine r in sim.Scheduler.Routines)
                if (ReferenceEquals(r.Target, sim.Player)) h = Mix(Mix(h, ValueHash(r.Body?.GetType().Name)), (ulong)(uint)(r.Due - sim.Scheduler.Tick));
            foreach (FieldInfo f in KeyEngineFields) h = Mix(h, ValueHash(f.GetValue(sim.Engine)));
            h = Mix(Mix(h, (ulong)(uint)sim.Progress), (ulong)(uint)sim.Goals.Hash());
            return h;
        }

        LibFeatures FeaturesNow(Simulation sim)
        {
            Movement m = sim.Player;
            WorldPos w = sim.PlayerWorld;
            return new LibFeatures
            {
                X = w.x, Y = w.y, VX = m.Velocity.x, VY = m.Velocity.y, MX = m.momentum.x, MY = m.momentum.y,
                DashesLeft = m.airDashesLeft, AirJumpsLeft = m.airJumpsLeft, FacingRight = m.facingRight, OnGround = m.onGround,
                BuysHash = sim.Goals.Hash(), Cash = sim.Shop.CashNow,
            };
        }

        /// <summary>
        /// Replays inputs from the start on the loop's simulation, filing every goal touch and chain point, and adds the run to the
        /// library (once per input list). Returns the replay as a Route.
        /// </summary>
        Route TraceRun(List<InputTick> inputs, string how)
        {
            Simulation sim = _eval;
            sim.SetStart(_origin, 0);
            sim.Goals.Limit = _goals;
            var states = new List<LibState>();
            sim.Restore(_origin);
            states.Add(new LibState { Goal = 0, Kind = LibPoint.Start, Key = StateKey(sim), Tick = 0, Input = 0, Features = FeaturesNow(sim) });
            LibPoint pending = LibPoint.None;
            bool ground = sim.Player.onGround, wall = sim.Player.OnWall, dash = sim.Player.dashActive;
            int progress = sim.Progress;
            RunResult rr = sim.Run(inputs, null, tick =>
            {
                if (tick >= 1 && tick <= inputs.Count && inputs[tick - 1].reset) pending |= LibPoint.Reset;
                if (pending != LibPoint.None)
                    states.Add(new LibState { Goal = sim.Progress, Kind = pending, Key = StateKey(sim), Tick = sim.Engine.PhysicsTicks, Input = tick, Features = FeaturesNow(sim) });
                pending = LibPoint.None;
                return true;
            }, 1000000, _ =>
            {
                Movement m = sim.Player;
                if (m.onGround && !ground) pending |= LibPoint.Land;
                if (m.OnWall && !wall) pending |= LibPoint.Wall;
                if (!m.dashActive && dash) pending |= LibPoint.DashEnd;
                if (sim.Progress != progress) pending |= LibPoint.Goal;
                ground = m.onGround; wall = m.OnWall; dash = m.dashActive; progress = sim.Progress;
            });
            var route = new Route { Inputs = inputs, Progress = rr.Progress, GoalInput = rr.GoalInputTicks, GoalPhysics = rr.GoalPhysicsTicks, EndReason = rr.EndReason };
            // A goal touched inside the tick before an input boundary is filed at that boundary with its goal count; the last goal's
            // touch also ends the run 12 ticks later, so its state is filed at its own tick when the boundary never came.
            if (route.Progress == _goals && !states.Any(s => s.Goal == _goals))
                states.Add(new LibState { Goal = _goals, Kind = LibPoint.Goal, Key = 0, Tick = route.Last, Input = (int)Math.Min(route.GoalInput[_goals - 1], inputs.Count), Features = default });
            // The end state's tick is the last goal's (the run's time), not the boundary after it.
            foreach (LibState s in states)
                if (s.Goal == _goals && (s.Kind & LibPoint.Goal) != 0) s.Tick = route.Last;
            var run = new StateLibrary.LibRun
            {
                How = how, Inputs = InputRuns.From(inputs), Progress = route.Progress, End = route.Progress == _goals ? route.Last : long.MaxValue,
                Hash = SnapshotIndex.Hash(inputs, inputs.Count),
            };
            if (!_lib.Knows(run.Hash)) _lib.AddTraced(run, states);
            return route;
        }

        /// <summary>The chaining check: goal touches the route crosses at speed (|Velocity + momentum| at least 600 u/s, about a
        /// third of the top path speed), and the median speed at a goal touch.</summary>
        (int atSpeed, float median) Chaining(List<InputTick> inputs)
        {
            _lib ??= new StateLibrary(_goals, 150);
            ulong hash = SnapshotIndex.Hash(inputs, inputs.Count);
            StateLibrary.LibRun run = _lib.AllRuns.FirstOrDefault(r => r.Hash == hash);
            if (run == null) { TraceRun(inputs, "chaining check"); run = _lib.AllRuns.FirstOrDefault(r => r.Hash == hash); }
            if (run == null) return (0, 0f);
            var speeds = run.States.Select(_lib.State).Where(s => (s.Kind & LibPoint.Goal) != 0 && s.Goal > 0).Select(s => s.Features.Speed).OrderBy(v => v).ToList();
            return (speeds.Count(v => v >= 600f), speeds.Count == 0 ? 0f : speeds[speeds.Count / 2]);
        }

        /// <summary>
        /// The library's shortest path, played from the start: kept (Commit, which replays it fresh) when it beats the best. A replay
        /// that differs from the path's prediction bans the last join before the first goal it missed or reached at another tick,
        /// and the path is recomputed (at most 6 times).
        /// </summary>
        bool TryStitch(string why)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                (IReadOnlyList<Segment> path, long end) = _lib.ShortestPath();
                if (path.Count == 0 || end >= _best.Last) return false;
                if (path.Count == 1 && _lib.Run(path[0].Run).End == end && _lib.Run(path[0].Run).Hash == SnapshotIndex.Hash(_best.Inputs, _best.Inputs.Count)) return false;
                var inputs = new List<InputTick>();
                foreach (Segment s in path) inputs.AddRange(s.Inputs);
                string how = $"stitch of {path.Count} segments ({why})";
                Route r = TraceRun(inputs, how);
                Log($"auto stitch: library {_lib.Describe()}; shortest path {path.Count} segments over runs {string.Join(",", path.Select(s => s.Run).Distinct())}, predicted {Seconds(end):0.00}s, replayed {Describe(r)}");
                if (r.Progress == _goals && r.Last < _best.Last)
                {
                    Commit(r, how);
                    return _best == r || _best.Last <= r.Last;
                }
                // Where the replay left the prediction: the first goal it missed or reached at another tick.
                long offset = 0;
                int goalAt = r.Progress + 1;
                var predicted = new Dictionary<int, long>();
                foreach (Segment s in path)
                {
                    LibState a = _lib.State(s.From);
                    StateLibrary.LibRun run = _lib.Run(s.Run);
                    foreach (int id in run.States)
                    {
                        LibState x = _lib.State(id);
                        if (x.Tick < a.Tick || x.Tick > _lib.State(s.To).Tick || (x.Kind & LibPoint.Goal) == 0) continue;
                        predicted[x.Goal] = offset + x.Tick - a.Tick;
                    }
                    offset += s.Ticks;
                }
                for (int g = 1; g <= r.Progress; g++)
                    if (predicted.TryGetValue(g, out long p) && p != r.GoalPhysics[g - 1]) { goalAt = g; break; }
                Segment culprit = path.Skip(1).LastOrDefault(s => _lib.State(s.From).Goal < goalAt) ?? path.Skip(1).FirstOrDefault();
                if (culprit == null) return false;
                LibState join = _lib.State(culprit.From);
                _lib.Ban(join.Goal, join.Key, culprit.Run);
                Log($"auto stitch: the join at goal {join.Goal} ({join.Kind}, tick {join.Tick} of run {join.Run}) onto run {culprit.Run} doesn't replay as predicted "
                    + $"(first differs at goal {goalAt}: {r.EndReason}); banned");
            }
            return false;
        }

        /// <summary>
        /// A spliced run that is ahead and then misses goal g (a buy it can't pay for yet: the Cash comes on the old route's ticks)
        /// waits there: w idle ticks before the quick restart ahead of goal g, the fewest that get it past goal g (binary search from
        /// a snapshot at the restart). The restart puts the player back exactly as the old route had it, so the join is exact; only
        /// the ticks and the Cash differ. Repeats for the next goal it misses (at most 6 waits). Returns the run, or null.
        /// </summary>
        Route WaitFix(Route c, Route old, int changedEnd, string how)
        {
            for (int fix = 0; fix < 6 && c != null && c.Progress < _goals; fix++)
            {
                int g = c.Progress + 1;
                if (g < 2 || old.Progress < g) return null;
                // The candidate's tail is the old route's inputs moved by this many.
                int shift = c.Inputs.Count - old.Inputs.Count;
                int oldGoal = (int)Math.Min(old.GoalInput[g - 1], old.Inputs.Count);
                int r = -1;
                for (int i = oldGoal - 1; i + shift > changedEnd && i > 0; i--)
                    if (old.Inputs[i].reset && i + shift < c.Inputs.Count && c.Inputs[i + shift].reset) { r = i + shift; break; }
                if (r < 0) { Log($"auto wait: {how} misses goal {g} ({c.EndReason}); no quick restart after the change to wait at"); return null; }
                long lead = old.GoalPhysics[g - 2] - c.GoalPhysics[g - 2];
                int hi = (int)Math.Clamp(lead + 30, 8, 900);
                List<InputTick> head = c.Inputs.GetRange(0, r);
                _eval.SetStart(_origin, 0);
                _eval.Goals.Limit = _goals;
                SimSnapshot at = _eval.Advance(head);
                if (at == null) return null;
                bool held = r > 0 && c.Inputs[r - 1].held;
                List<InputTick> Waited(int w)
                {
                    var list = new List<InputTick>(c.Inputs.Count - r + w);
                    for (int i = 0; i < w; i++) list.Add(new InputTick { held = held });
                    list.AddRange(c.Inputs.Skip(r));
                    return list;
                }
                int Passes(int w)
                {
                    _eval.SetStart(at, r);
                    _eval.Goals.Limit = _goals;
                    return _eval.Run(Waited(w), null, null, 1000000).Progress;
                }
                if (Passes(hi) < g) { Log($"auto wait: {how} misses goal {g} ({c.EndReason}) even after {hi} idle ticks at input {r}"); _eval.SetStart(_origin, 0); return null; }
                int lo = 0;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) / 2;
                    if (Passes(mid) >= g) hi = mid; else lo = mid;
                }
                _eval.SetStart(_origin, 0);
                var inputs = head.Concat(Waited(hi)).ToList();
                Route next = TraceRun(inputs, $"{how}, waited {hi} ticks at input {r}");
                Log($"auto wait: {how} missed goal {g} ({_labels[g - 1]}: {c.EndReason}); {hi} idle ticks before the quick restart at input {r} get it past: {Describe(next)}");
                if (next.Progress <= c.Progress) return null;
                c = next;
                changedEnd = r + hi;
            }
            return c != null && c.Progress == _goals ? c : null;
        }

        /// <summary>
        /// Stage 1's merge of a window's finds: each (the last five) spliced with the old tail and traced into the library; a whole
        /// run that is faster is kept; one that misses a later goal waits there (WaitFix); then the library's shortest path; and only
        /// then the solve's repair (goal by goal from the new state) as before.
        /// </summary>
        void MergeAuto(string where, Route best, int prefixLength, int limit, List<List<InputTick>> found)
        {
            if (_stages < 1 || _lib == null) { Merge(where, best, prefixLength, limit, found); return; }
            var broken = new List<Route>();
            foreach (List<InputTick> f in Enumerable.Reverse(found).Take(5))
            {
                List<InputTick> inputs = best.Inputs.Take(prefixLength).Concat(f).ToList();
                Route through = Evaluate(inputs, limit);
                if (through.Progress < limit) continue;
                int end = (int)through.GoalInput[limit - 1];
                if (end > inputs.Count) Pad(inputs, end);
                else inputs.RemoveRange(end, inputs.Count - end);
                int changedEnd = inputs.Count;
                if (best.Progress >= limit) inputs.AddRange(best.Inputs.Skip((int)best.GoalInput[limit - 1]));
                bool twice = false;
                for (int i = 1; i < inputs.Count; i++) if (inputs[i].reset && inputs[i - 1].reset) twice = true;
                if (twice) continue;
                Route c = TraceRun(inputs, where);
                if (c.Progress == _goals && c.Last < _best.Last) { Commit(c, where); return; }
                if (c.Progress >= limit && c.Progress < _goals) broken.Add(c);
                if (c.Progress == _goals) broken.Add(c);
            }
            foreach (Route c in broken.Where(c => c.Progress < _goals).OrderByDescending(c => c.Progress))
            {
                Route fixedRun = WaitFix(c, best, (int)Math.Min(c.GoalInput[limit - 1], c.Inputs.Count), where);
                if (fixedRun != null && fixedRun.Last < _best.Last) { Commit(fixedRun, where + ", waiting for the Cash"); return; }
            }
            if (TryStitch(where)) return;
            // As before: re-find the goals after the window from the new state.
            Merge(where, best, prefixLength, limit, found);
        }
    }
}
