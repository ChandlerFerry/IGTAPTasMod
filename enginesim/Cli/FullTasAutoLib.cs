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
    sealed class StateLibrary
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
        readonly Dictionary<(int goal, ulong key), List<int>> _byPlayer = new Dictionary<(int, ulong), List<int>>();
        readonly HashSet<ulong> _runHashes = new HashSet<ulong>();
        /// <summary>The live (unpruned) runs by their inputs' hash.</summary>
        readonly Dictionary<ulong, LibRun> _byHash = new Dictionary<ulong, LibRun>();
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
        /// <summary>The live run of these inputs (their hash); null when never traced or pruned.</summary>
        public LibRun RunOf(ulong hash) => _byHash.TryGetValue(hash, out LibRun run) ? run : null;

        /// <summary>A traced run (FullTas.TraceRun fills it); returns its id, -1 when already known.</summary>
        public int AddTraced(LibRun run, List<LibState> states)
        {
            if (!_runHashes.Add(run.Hash)) return -1;
            run.Id = _runs.Count;
            _runs.Add(run);
            _byHash[run.Hash] = run;
            foreach (LibState s in states)
            {
                s.Id = _states.Count;
                s.Run = run.Id;
                _states.Add(s);
                run.States.Add(s.Id);
                if (!_byPlayer.TryGetValue((s.Goal, s.PlayerKey), out List<int> same)) _byPlayer[(s.Goal, s.PlayerKey)] = same = new List<int>();
                same.Add(s.Id);
                var k = (s.Goal, s.Key);
                if (!_byKey.TryGetValue(k, out List<int> list)) _byKey[k] = list = new List<int>();
                list.Add(s.Id);
            }
            _version++;
            if (Runs > MaxRuns) Prune();
            return run.Id;
        }

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
                _byHash.Remove(r.Hash);
                foreach (int id in r.States)
                {
                    _byKey[(_states[id].Goal, _states[id].Key)].Remove(id);
                    _byPlayer[(_states[id].Goal, _states[id].PlayerKey)].Remove(id);
                }
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

        public IReadOnlyList<LibSegment> Continuations(int stateId)
        {
            LibState s0 = _states[stateId];
            var list = new List<LibSegment>();
            foreach (int id in _byKey[(s0.Goal, s0.Key)])
            {
                LibRun r = _runs[_states[id].Run];
                if (r == null) continue;
                List<int> rel = Relevant(r);
                int at = rel.IndexOf(id);
                if (at < 0 || at + 1 >= rel.Count) continue;
                LibState a = _states[id], b = _states[rel[at + 1]];
                list.Add(new LibSegment { From = a.Id, To = b.Id, Run = r.Id, Ticks = b.Tick - a.Tick, Inputs = Slice(r.Inputs, a.Input, b.Input) });
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
        public (IReadOnlyList<LibSegment> path, long end) ShortestPath()
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
            if (end < 0) return (Array.Empty<LibSegment>(), long.MaxValue);
            // Walk back: consecutive states on one run make a segment; a switch starts the next.
            var chain = new List<int> { end };
            for (int v = end; prev.TryGetValue(v, out int p); v = p) chain.Add(p);
            chain.Reverse();
            var segments = new List<LibSegment>();
            int from = chain[0];
            for (int i = 1; i < chain.Count; i++)
            {
                LibState a = _states[chain[i - 1]], b = _states[chain[i]];
                if (a.Run == b.Run) continue;
                // A switch at a: the segment on a's run ends there.
                LibState f = _states[from];
                segments.Add(new LibSegment { From = f.Id, To = a.Id, Run = a.Run, Ticks = a.Tick - f.Tick, Inputs = Slice(_runs[a.Run].Inputs, f.Input, a.Input) });
                from = b.Id;
            }
            LibState last = _states[from], stop = _states[end];
            LibRun tail = _runs[stop.Run];
            // The last segment runs to the end of its run's inputs (the strict finish's ticks after the last goal).
            segments.Add(new LibSegment { From = last.Id, To = stop.Id, Run = stop.Run, Ticks = stop.Tick - last.Tick, Inputs = Slice(tail.Inputs, last.Input, tail.Inputs.Count) });
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

        /// <summary>Live states of other runs where the player is exactly as in this one (the player key: movement, engine,
        /// tracker; the world may differ), soonest first.</summary>
        public IEnumerable<LibState> SamePlayer(LibState s)
        {
            if (!_byPlayer.TryGetValue((s.Goal, s.PlayerKey), out List<int> same)) yield break;
            foreach (int id in same.OrderBy(id => _states[id].Tick))
                if (_states[id].Run != s.Run && _runs[_states[id].Run] != null) yield return _states[id];
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
        /// <summary>--parts LIST: which of --auto's parts run (docs/fulltas.md, "Parts"); "none" is AutoLoop's loop as it was.
        /// lib: the goal-state library, exact stitching, the wait fix and route-sync; tail: tail windows after a broken splice
        /// (needs lib); combine: the known routes' faster goal runs spliced into each new best (needs lib); sched: the pull
        /// scheduler; ctg: the cost-to-go (needs lib); macros: macro seeds (needs lib); stray: a run that dies on a stray buy is
        /// re-timed as a whole (Retime) and its tail windows avoid the stray box (StrayAvoid) (needs lib); goals: the economy
        /// goals edited (laps dropped or added, record laps) and adopted when the whole run is faster (FullTasAutoGoals.cs); idle:
        /// each buy's waits for Cash moved under its box (FullTasAutoIdle.cs).</summary>
        /// <summary>Every part, all on by default.</summary>
        static readonly string[] AllParts = { "lib", "tail", "combine", "sched", "ctg", "macros", "stray", "goals", "idle", "wallreset", "waste", "grafts" };
        HashSet<string> _parts = new HashSet<string>(AllParts);
        /// <summary>--parts stray's two halves (the stray study switches them one at a time).</summary>
        bool _strayRetime, _strayAvoid;
        bool Part(string p) => _parts.Contains(p);

        static HashSet<string> ParseParts(string list)
        {
            var set = new HashSet<string>();
            if (list == "none") return set;
            foreach (string p in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!AllParts.Contains(p)) throw new ArgumentException($"--parts: unknown part '{p}' (parts: {string.Join(",", AllParts)}, or none)");
                set.Add(p);
            }
            if (!set.Contains("lib") && (set.Contains("tail") || set.Contains("combine") || set.Contains("ctg") || set.Contains("macros") || set.Contains("stray")))
                throw new ArgumentException("--parts: tail, combine, ctg, macros and stray need lib");
            return set;
        }

        static readonly FieldInfo[] KeyMoveFields = IGTAPTasMod.FieldState.For(typeof(Movement));
        static readonly int KeyDashCooldown = Array.FindIndex(KeyMoveFields, f => f.Name == "dashCooldown");
        static readonly int KeyWallCoyote = Array.FindIndex(KeyMoveFields, f => f.Name == "wallCoyoteFrames");
        static readonly int KeySinceDash = Array.FindIndex(KeyMoveFields, f => f.Name == "framesSinceLastDash");
        static readonly int KeyBoostMax = Array.FindIndex(KeyMoveFields, f => f.Name == "dashBoostMax");
        static readonly FieldInfo StepFromField = typeof(Simulation).GetField("_stepFrom", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo StepToField = typeof(Simulation).GetField("_stepTo", BindingFlags.Instance | BindingFlags.NonPublic);
        /// <summary>
        /// Route-sync against every library run, for a spliced run whose old tail breaks (it dies or misses a goal after the
        /// window): (1) wherever after the change the player is exactly as on another run (the player key), that run's inputs from
        /// there; (2) at each of the first goal boundaries after the change, the tails other complete runs play from that goal,
        /// fastest known first. Every try is replayed from the start; the first that reaches every goal sooner than the best is
        /// returned. At most 16 replays.
        /// </summary>
        Route RouteSync(Route c, int changedEnd, string how)
        {
            StateLibrary.LibRun run = _lib.RunOf(SnapshotIndex.Hash(c.Inputs, c.Inputs.Count));
            if (run == null) return null;
            var tried = new HashSet<ulong>();
            int plays = 0;
            Route Try(List<InputTick> inputs, string what)
            {
                ulong hash = SnapshotIndex.Hash(inputs, inputs.Count);
                if (!tried.Add(hash) || _lib.Knows(hash)) return null;
                plays++;
                Route r = TraceRun(inputs, $"{how}, {what}");
                if (r.Progress == _goals && r.Live < _best.Live)
                {
                    Log($"auto sync: {how}: {what} -> {Describe(r)}");
                    return r;
                }
                return null;
            }
            foreach (int id in run.States)
            {
                LibState s = _lib.State(id);
                if (s.Input <= changedEnd || s.Kind == LibPoint.Start) continue;
                foreach (LibState t in _lib.SamePlayer(s).Take(2))
                {
                    StateLibrary.LibRun other = _lib.Run(t.Run);
                    Route r = Try(c.Inputs.Take(s.Input).Concat(other.Inputs.Skip(t.Input)).ToList(), $"re-synced at goal {s.Goal} ({s.Kind}) onto run {t.Run}");
                    if (r != null) return r;
                    if (plays >= 8) goto tails;
                }
            }
        tails:
            int first = Enumerable.Range(1, c.Progress).FirstOrDefault(g => c.GoalInput[g - 1] > changedEnd);
            if (first == 0) return null;
            for (int g = first; g <= Math.Min(c.Progress, first + 3); g++)
            {
                int cut = (int)Math.Min(c.GoalInput[g - 1], c.Inputs.Count);
                var tails = _lib.AllRuns.Where(r => r.Progress == _goals && r.Id != run.Id)
                    .Select(r => (r, s: r.States.Select(_lib.State).FirstOrDefault(x => x.Goal == g && (x.Kind & LibPoint.Goal) != 0)))
                    .Where(x => x.s != null).OrderBy(x => x.r.End - x.s.Tick).Take(4);
                foreach (var (r, s) in tails)
                {
                    Route got = Try(c.Inputs.Take(cut).Concat(r.Inputs.Skip(s.Input)).ToList(), $"run {r.Id}'s way on from goal {g}");
                    if (got != null) return got;
                    if (plays >= 16) return null;
                }
            }
            return null;
        }

        string SpeedsFile() => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_trace)), "fulltas", _name + ".auto-speeds.json");

        /// <summary>The speed probes' top path speeds, kept for the trace's start state and goals (StartKey + labels).</summary>
        string SpeedsKey() => StartKey(AutoHeader()).ToString("x16") + ":" + ValueHash(string.Join("|", _labels)).ToString("x16");

        bool LoadSpeeds()
        {
            string file = SpeedsFile();
            if (!System.IO.File.Exists(file)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file));
            if (doc.RootElement.GetProperty("key").GetString() != SpeedsKey()) return false;
            foreach (var v in doc.RootElement.GetProperty("speeds").EnumerateArray())
                _vtop[v.GetProperty("caps").GetString()] = (v.GetProperty("speed").GetSingle(), v.GetProperty("how").GetString() + " (cached)");
            Log($"auto: top path speeds read from {System.IO.Path.GetFileName(file)} (speed probes skipped): "
                + string.Join("; ", _vtop.OrderBy(v => v.Key).Select(v => $"{v.Key} {v.Value.speed:0.0}")));
            return true;
        }

        void SaveSpeeds()
        {
            string file = SpeedsFile();
            System.IO.File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
            {
                key = SpeedsKey(),
                speeds = _vtop.Select(v => new { caps = v.Key, speed = v.Value.speed, how = v.Value.how }),
            }));
        }

        /// <summary>A stage part's call; a part that throws is logged and treated as having nothing to say.</summary>
        T SafePart<T>(Func<T> call, string what)
        {
            try { return call(); }
            catch (Exception ex) { Log($"auto: {what} failed: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}"); return default; }
        }

        /// <summary>A part's call on a thread of its own (64 MB stack, as VerifyFresh): a part that replays routes on simulations of
        /// its own leaves the loop's simulation as it was (the engine keeps per-thread state: FullTasAutoMacros.Mine on the loop's
        /// thread left the loop's simulation replaying the best to no goal at all).</summary>
        T Isolated<T>(Func<T> call, string what) { return Solve.OnThread(() => SafePart(call, what)); }

        /// <summary>After the library changes (the start, every new best): the cost-to-go refits and the macros are mined again.</summary>
        void RefitParts()
        {
            if (_lib == null) return;
            double c0 = CpuSeconds();
            if (_ctg != null) SafePart(() => { _ctg.Fit(_lib); return 0; }, "cost-to-go Fit");
            if (_macros != null) Isolated(() => { _macros.Mine(_lib); return 0; }, "macro Mine");
            if (_ctg != null || _macros != null) Log($"auto: parts refit on {_lib.Describe()} in {CpuSeconds() - c0:0.0} CPU s");
        }

        /// <summary>The best's own library state at goal k's boundary (the window's start), null when not traced.</summary>
        LibState BestStateAt(int goal)
        {
            if (_lib == null) return null;
            StateLibrary.LibRun run = _lib.RunOf(SnapshotIndex.Hash(_best.Inputs, _best.Inputs.Count));
            if (run == null) return null;
            return run.States.Select(_lib.State).FirstOrDefault(s => goal == 0 ? s.Kind == LibPoint.Start : s.Goal == goal && (s.Kind & LibPoint.Goal) != 0);
        }

        /// <summary>
        /// Stage 4: seeds for a window from goal k-1's state: each macro playable there (its anchor kind and unlocks), played first,
        /// then the best's own inputs after as many ticks. At most 5.
        /// </summary>
        List<List<InputTick>> MacroSeeds(Route best, int k, int prefixLength)
        {
            var seeds = new List<List<InputTick>>();
            if (_macros == null) return seeds;
            LibState at = BestStateAt(k - 1);
            if (at == null) return seeds;
            string caps = PlayerCaps.Of(_eval.Player).Id;
            IReadOnlyList<Macro> list = SafePart(() => _macros.For(at, caps), "macro For");
            if (list == null) return seeds;
            foreach (Macro m in list.Where(m => m.Inputs != null && m.Inputs.Count > 0).Take(5))
                seeds.Add(m.Inputs.Concat(best.Inputs.Skip(prefixLength + m.Inputs.Count)).ToList());
            return seeds;
        }

        /// <summary>
        /// Stage 2's repair bound: a partial repaired run at goal p (tick t) whose cost-to-go from its arrival state puts its end
        /// more than 10 ticks after the best's can't win: dropped. Null: search on.
        /// </summary>
        string CostToGoBound(Route run)
        {
            if (_ctg == null || _lib == null || run.Progress == 0 || run.Progress >= _goals) return null;
            int p = run.Progress;
            TraceRun(run.Inputs, "a repair's partial run");
            StateLibrary.LibRun traced = _lib.RunOf(SnapshotIndex.Hash(run.Inputs, run.Inputs.Count));
            LibState s = traced?.States.Select(_lib.State).FirstOrDefault(x => x.Goal == p && (x.Kind & LibPoint.Goal) != 0);
            if (s == null) return null;
            LibFeatures f = s.Features;
            double ctg = SafePart(() => _ctg.Ticks(p, f), "cost-to-go Ticks");
            if (double.IsNaN(ctg) || ctg <= 0) return null;
            double end = run.Clock(p) + ctg;
            return end > _best.Last + 10 ? $"at goal {p} (tick {run.GoalPhysics[p - 1]}) the cost-to-go ({ctg:0} ticks) puts its end at {end:0}, after the best's {_best.Last}" : null;
        }

        FullTasAutoCostToGo _ctg;
        PullScheduler _sched;
        FullTasAutoMacros _macros;

        /// <summary>The parts --parts turns on (the scheduler's regions cover the --focus goals only; the loop adds the focus's
        /// first goal back to its pulls).</summary>
        void MakeParts()
        {
            if (Part("ctg")) _ctg = new FullTasAutoCostToGo(_goals);
            if (Part("sched")) _sched = new PullScheduler(_labels[(_focusFrom - 1).._focusTo], _minutes * 60 * _threads, _model?.Where(r => InFocus(r.K)).Select(r => r.Expected).ToArray());
            if (Part("macros")) _macros = new FullTasAutoMacros(log: line => Log(line));
        }

        int _stitchTried, _stitchExact;
        // The input engine's pending press state (a dash's jump frame, a lock or turn armed, a pause); its counters are left out.
        static readonly FieldInfo[] KeyEngineFields = new[] { "_wallJumpTriggered", "_rawJumpHeld", "_dashJumpFrame", "_dashJumpAxis", "_springDashFrame",
                "_springTicks", "_lockArmed", "_lockX", "_lockY", "_pendingTurnX", "_pendingTurnY", "_turnArmed", "_turnX", "_turnY", "_pauseTicksLeft",
                "_pauseFrames", "_pauseFrame", "_tickLocked", "_tickStick", "_quickRestartStep" }
            .Select(n => typeof(IGTAPTasMod.MacroEngine).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)).Where(f => f != null).ToArray();
        object[] _keyValues = Array.Empty<object>();

        static ulong Mix(ulong h, ulong v) { h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2); return h * 1099511628211UL; }

        static readonly FieldInfo[] NormalizedTimers = new[] { KeyDashCooldown, KeyWallCoyote, KeySinceDash }.Where(i => i >= 0).Select(i => KeyMoveFields[i]).ToArray();

        /// <summary>
        /// The whole game state but the clock, as one digest: Simulation.Fingerprint (the player; every course's fields; the economy:
        /// Cash, box levels, clones and their timers, buy-max routines; the RNG; the goal tracker; pending invokes and coroutines;
        /// every touched script, toggled object and layer; the rebases), taken with the three threshold timers at the values the
        /// game can't tell apart (StateKey's normalisation), then put back.
        /// </summary>
        int WorldDigest(Simulation sim)
        {
            Movement m = sim.Player;
            var saved = new object[NormalizedTimers.Length];
            float boost = KeyBoostMax >= 0 && KeyMoveFields[KeyBoostMax].GetValue(m) is float bm ? bm : 0f;
            float dashBound = Math.Max(boost * 1.4f, 4f);
            for (int i = 0; i < NormalizedTimers.Length; i++)
            {
                FieldInfo f = NormalizedTimers[i];
                saved[i] = f.GetValue(m);
                if (saved[i] is float t)
                {
                    if (f.Name == "framesSinceLastDash") { if (t >= dashBound) f.SetValue(m, dashBound); }
                    else if (t < 0f) f.SetValue(m, -1f);
                }
            }
            try { return sim.Fingerprint(); }
            finally { for (int i = 0; i < NormalizedTimers.Length; i++) NormalizedTimers[i].SetValue(m, saved[i]); }
        }

        LibState Filed(Simulation sim, int goal, LibPoint kind, long tick, int input)
        {
            ulong player = StateKey(sim);
            ulong key = Mix(player, (ulong)(uint)WorldDigest(sim));
            return new LibState
            {
                Goal = goal, Kind = kind, Key = key, PlayerKey = player,
                Tick = tick, Input = input, Features = FeaturesNow(sim),
            };
        }

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
                case string s: return FullTasAutoMacros.StableHash(s);
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
                BuysHash = sim.Goals.Hash(), Cash = sim.Shop.CashNow, Income = CloneIncome(sim),
            };
        }

        /// <summary>
        /// Replays inputs from the start on the loop's simulation, filing every goal touch and chain point, and adds the run to the
        /// library (once per input list). Returns the replay as a Route. Without the library (`--parts` without `lib`): the plain replay.
        /// </summary>
        Route TraceRun(List<InputTick> inputs, string how)
        {
            if (_lib == null) return Evaluate(inputs);
            Simulation sim = _eval;
            sim.SetStart(_origin, 0);
            sim.Goals.Limit = _goals;
            ulong hash = SnapshotIndex.Hash(inputs, inputs.Count);
            // A run the library has is replayed for its route alone: its states aren't filed again.
            bool file = !_lib.Knows(hash);
            var states = new List<LibState>();
            sim.Restore(_origin);
            if (file) states.Add(Filed(sim, 0, LibPoint.Start, 0, 0));
            LibPoint pending = LibPoint.None;
            bool ground = sim.Player.onGround, wall = sim.Player.OnWall, dash = sim.Player.dashActive;
            int progress = sim.Progress;
            RunResult rr = sim.Run(inputs, null, tick =>
            {
                if (tick >= 1 && tick <= inputs.Count && inputs[tick - 1].reset) pending |= LibPoint.Reset;
                if (pending != LibPoint.None && file)
                    states.Add(Filed(sim, sim.Progress, pending, sim.Engine.PhysicsTicks, tick));
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
            var route = new Route { Inputs = inputs, Progress = rr.Progress, GoalInput = rr.GoalInputTicks, GoalPhysics = rr.GoalPhysicsTicks, GoalHeld = rr.GoalHeldFrames, EndReason = rr.EndReason };
            // A goal touched inside the tick before an input boundary is filed at that boundary with its goal count; the last goal's
            // touch also ends the run 12 ticks later, so its state is filed at its own tick when the boundary never came.
            if (route.Progress == _goals && !states.Any(s => s.Goal == _goals))
                states.Add(new LibState { Goal = _goals, Kind = LibPoint.Goal, Key = 0, Tick = route.End, Input = (int)Math.Min(route.GoalInput[_goals - 1], inputs.Count), Features = default });
            // The end state's tick is the last goal's (the run's time), not the boundary after it.
            foreach (LibState s in states)
                if (s.Goal == _goals && (s.Kind & LibPoint.Goal) != 0) s.Tick = route.End;
            var run = new StateLibrary.LibRun
            {
                How = how, Inputs = InputRuns.From(inputs), Progress = route.Progress, End = route.Progress == _goals ? route.End : long.MaxValue,
                Hash = hash,
            };
            if (file) _lib.AddTraced(run, states);
            return route;
        }

        /// <summary>The chaining check: goal touches the route crosses at speed (|Velocity + momentum| at least 600 u/s, about a
        /// third of the top path speed), and the median speed at a goal touch.</summary>
        (int atSpeed, float median) Chaining(List<InputTick> inputs)
        {
            float[] v = GoalSpeeds(inputs);
            var speeds = v.Where(x => !float.IsNaN(x)).OrderBy(x => x).ToList();
            return (speeds.Count(x => x >= ChainSpeed), speeds.Count == 0 ? 0f : speeds[speeds.Count / 2]);
        }

        const float ChainSpeed = 600f;

        /// <summary>|Velocity + momentum| on each goal's touch tick (NaN: not reached), from a replay on the loop's simulation.</summary>
        float[] GoalSpeeds(List<InputTick> inputs)
        {
            var speeds = Enumerable.Repeat(float.NaN, _goals).ToArray();
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            int progress = 0;
            _eval.Run(inputs, null, null, 1000000, _ =>
            {
                int p = _eval.Progress;
                if (p == progress) return;
                float s = (_eval.Player.Velocity + _eval.Player.momentum).magnitude;
                for (int g = progress; g < p && g < _goals; g++) speeds[g] = s;
                progress = p;
            });
            return speeds;
        }

        /// <summary>The goals a route crosses at speed (the chaining check) that another doesn't, both ways.</summary>
        string ChainingDiff(List<InputTick> before, List<InputTick> after)
        {
            float[] a = GoalSpeeds(before), b = GoalSpeeds(after);
            var lost = Enumerable.Range(0, _goals).Where(g => a[g] >= ChainSpeed && !(b[g] >= ChainSpeed)).Select(g => $"{g + 1} {_labels[g]} {a[g]:0}->{b[g]:0}");
            var won = Enumerable.Range(0, _goals).Where(g => !(a[g] >= ChainSpeed) && b[g] >= ChainSpeed).Select(g => $"{g + 1} {_labels[g]} {a[g]:0}->{b[g]:0}");
            return $"lost at speed: {string.Join("; ", lost)}; gained: {string.Join("; ", won)}";
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
                (IReadOnlyList<LibSegment> path, long end) = _lib.ShortestPath();
                if (path.Count == 0 || end >= _best.End) return false;
                if (path.Count == 1 && _lib.Run(path[0].Run).End == end && _lib.Run(path[0].Run).Hash == SnapshotIndex.Hash(_best.Inputs, _best.Inputs.Count)) return false;
                var inputs = new List<InputTick>();
                foreach (LibSegment s in path) inputs.AddRange(s.Inputs);
                string how = $"stitch of {path.Count} segments ({why})";
                Route r = TraceRun(inputs, how);
                bool exact = r.Progress == _goals && r.End == end;
                _stitchTried++;
                if (exact) _stitchExact++;
                Log($"auto stitch: library {_lib.Describe()}; "
                    + $"shortest path {path.Count} segments over runs {string.Join(",", path.Select(s => s.Run).Distinct())}, predicted {Seconds(end):0.00}s, replayed {Describe(r)}"
                    + (exact ? " (exactly as predicted)" : " (NOT as predicted)") + $"; {_stitchExact} of {_stitchTried} stitches replayed exactly so far");
                if (r.Progress == _goals && r.Live < _best.Live)
                {
                    Commit(r, how);
                    return _best == r || _best.Live <= r.Live;
                }
                // Where the replay left the prediction: the first goal it missed or reached at another tick.
                long offset = 0;
                int goalAt = r.Progress + 1;
                var predicted = new Dictionary<int, long>();
                foreach (LibSegment s in path)
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
                LibSegment culprit = path.Skip(1).LastOrDefault(s => _lib.State(s.From).Goal < goalAt) ?? path.Skip(1).FirstOrDefault();
                if (culprit == null) return false;
                LibState join = _lib.State(culprit.From);
                _lib.Ban(join.Goal, join.Key, culprit.Run);
                Log($"auto stitch: the join at goal {join.Goal} ({join.Kind}, tick {join.Tick} of run {join.Run}) onto run {culprit.Run} doesn't replay as predicted "
                    + $"(first differs at goal {goalAt}: {r.EndReason}); banned");
            }
            return false;
        }

        /// <summary>The wait fix's linear scan (idle ticks) when the binary search's widest wait doesn't pass.</summary>
        const int StrayScan = 40;

        /// <summary>
        /// A spliced run that is ahead and then misses goal g (a buy it can't pay for yet: the Cash comes on the old route's ticks)
        /// waits there: w idle ticks before the quick restart ahead of goal g, the fewest that get it past goal g (binary search from
        /// a snapshot at the restart; when the widest wait fails, a scan of 1-StrayScan: a stray buy isn't monotone in the wait).
        /// The restart puts the player back exactly as the old route had it, so the join is exact; only
        /// the ticks and the Cash differ. A stray buy no wait there cures (or with no restart after the change) is re-timed as a
        /// whole (--parts stray, Retime). Repeats for the next goal it misses (at most 6 fixes). Returns the run, or null.
        /// <paramref name="changedStart"/>: the first input the change touched (-1: changedEnd).
        /// </summary>
        Route WaitFix(Route c, Route old, int changedEnd, string how, int changedStart = -1)
        {
            Route r = WaitFixCore(c, old, changedEnd, how, changedStart);
            if ((r == null || r.Progress < _goals) && StrayBox((r ?? c).EndReason) != null) StrayRescue(r ?? c, old, changedEnd, how);
            return r;
        }

        /// <summary>A stray-fitted run that beats the best (<see cref="StrayRescue"/>), with the goals it was fitted from; the loop adopts
        /// it as a goals edit before its next pull when those goals are still the run's.</summary>
        (GoalEdit e, Route route, string[] from) _strayWin;
        /// <summary>Stray rescues tried on the current best (at most StrayRescues a best: each replays the run for every fit).</summary>
        int _strayRescues;
        ulong _strayRescueBest;
        const int StrayRescues = 6;

        /// <summary>
        /// Any lever's candidate that the wait fix can't carry past a strict stray buy (window finds and merges, combine, revive,
        /// idle moves, tail windows, the repairs through them) and that is ahead of <paramref name="old"/> where it stops: its goals
        /// fitted to what it buys (<see cref="Played"/>: StrayFit, then the wait fix), the inputs after the change being old's
        /// from <paramref name="changedEnd"/> on. A complete fitted run faster than the best is kept in <see cref="_strayWin"/>.
        /// </summary>
        void StrayRescue(Route c, Route old, int changedEnd, string how)
        {
            int p = c.Progress;
            if (p < 1 || old.Progress < p || c.GoalPhysics[p - 1] >= old.GoalPhysics[p - 1] || TimeUp) return;
            ulong key = BestKey();
            if (key != _strayRescueBest) (_strayRescueBest, _strayRescues) = (key, 0);
            if (_strayRescues++ >= StrayRescues) return;
            int shift = c.Inputs.Count - old.Inputs.Count;
            var e = new GoalEdit { Kind = "strayfit", Text = $"{how}, its goals fitted to its strays", Key = $"strayfit:{SnapshotIndex.Hash(c.Inputs, c.Inputs.Count):x16}",
                Labels = _labels, OldOf = Enumerable.Range(0, _goals).ToArray(), Inputs = c.Inputs, NewAt = changedEnd, OldAt = changedEnd - shift };
            (Route route, _, string why) = Played(_eval, _origin, e, old);
            _eval.UseGoals(_runGoals);
            Log($"auto stray rescue: {how}: " + (route == null ? why : $"{Describe(route)} on {e.Labels.Length} goals ({route.Last - _best.Last:+0;-0} ticks against the best), {why}"));
            if (route != null && route.Live < (_strayWin.route ?? _best).Live) _strayWin = (e, route, _labels);
        }

        Route WaitFixCore(Route c, Route old, int changedEnd, string how, int changedStart = -1)
        {
            Simulation sim = _eval;
            if (changedStart < 0) changedStart = changedEnd;
            for (int fix = 0; fix < 6 && c != null && c.Progress < _goals; fix++)
            {
                int g = c.Progress + 1;
                if (g < 2 || old.Progress < g) return null;
                // The candidate's tail is the old route's inputs moved by this many.
                int shift = c.Inputs.Count - old.Inputs.Count;
                int oldGoal = (int)Math.Min(old.GoalInput[g - 1], old.Inputs.Count);
                bool stray = _strayRetime && StrayBox(c.EndReason) != null;
                int r = -1;
                for (int i = oldGoal - 1; i + shift > changedEnd && i > 0; i--)
                    if (old.Inputs[i].reset && i + shift < c.Inputs.Count && c.Inputs[i + shift].reset) { r = i + shift; break; }
                // --parts stray: the whole stretch re-timed (Retime); false when no re-timing passes goal g.
                bool Retimed(string why)
                {
                    Route re = stray ? Retime(c, changedStart, Math.Min(oldGoal + shift, c.Inputs.Count), g, how) : null;
                    if (re == null || re.Progress <= c.Progress)
                    {
                        Log($"auto wait: {how} misses goal {g} ({c.EndReason}); {why}{(stray ? "; no re-timing of the stretch passes it" : "")}");
                        return false;
                    }
                    var (at0, moved, after) = _retimed;
                    changedEnd = at0 >= changedEnd ? after : changedEnd + moved;
                    if (at0 < changedStart) changedStart += moved;
                    c = re;
                    return true;
                }
                if (r < 0)
                {
                    if (!Retimed("no quick restart after the change to wait at")) return null;
                    continue;
                }
                long lead = old.GoalPhysics[g - 2] - c.GoalPhysics[g - 2];
                int hi = (int)Math.Clamp(lead + 30, 8, 900);
                List<InputTick> head = c.Inputs.GetRange(0, r);
                sim.SetStart(_origin, 0);
                sim.Goals.Limit = _goals;
                SimSnapshot at = sim.Advance(head);
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
                    sim.SetStart(at, r);
                    sim.Goals.Limit = _goals;
                    return sim.Run(Waited(w), null, null, 1000000).Progress;
                }
                if (Passes(hi) >= g)
                {
                    int lo = 0;
                    while (hi - lo > 1)
                    {
                        int mid = (lo + hi) / 2;
                        if (Passes(mid) >= g) hi = mid; else lo = mid;
                    }
                }
                else
                {
                    // Not monotone in the wait: a re-timed walk that stands in a neighbouring box when its 0.2 s cooldown ends buys
                    // it (a strict run's stray buy) at some waits and not others. The fewest idle ticks up to StrayScan that pass.
                    int w = Enumerable.Range(1, Math.Max(hi, StrayScan)).FirstOrDefault(x => Passes(x) >= g);
                    if (w == 0)
                    {
                        sim.SetStart(_origin, 0);
                        if (!Retimed($"every wait of 1-{Math.Max(hi, StrayScan)} idle ticks at input {r} fails")) return null;
                        continue;
                    }
                    hi = w;
                }
                sim.SetStart(_origin, 0);
                var inputs = head.Concat(Waited(hi)).ToList();
                Route next = TraceRun(inputs, $"{how}, waited {hi} ticks at input {r}");
                Log($"auto wait: {how} missed goal {g} ({_labels[g - 1]}: {c.EndReason}); {hi} idle ticks before the quick restart at input {r} get it past: {Describe(next)}");
                if (next.Progress <= c.Progress) return null;
                c = next;
                changedEnd = r + hi;
            }
            return c != null && c.Progress == _goals ? c : null;
        }

        /// <summary>At most this many quick restarts a re-timing tries: the ones nearest the stray, and the last before the change.</summary>
        const int RetimeRestarts = 6;
        /// <summary>The last Retime's (restart's idle run start, ticks moved, input after the new wait), for WaitFix's bookkeeping.</summary>
        (int at, int moved, int after) _retimed;

        /// <summary>
        /// --parts stray: a run that dies on a stray buy (box B bought before goal g: the player crosses B while it is buyable, its
        /// 0.2 s cooldown over and the Cash there) re-timed as a whole, so it crosses B while B can't be bought. At one quick restart,
        /// from the last one before the change (the whole changed stretch moves) through the last before the stray, the idle ticks
        /// before it are cut (as many as it has) or StrayScan more added; a restart puts the player back where the route had it, so
        /// only the clock and the Cash move. Scored from a snapshot at each restart to goal g; the fewest ticks that pass win (a cut
        /// first). Returns the re-timed run, traced, or null.
        /// </summary>
        Route Retime(Route c, int changedStart, int strayBefore, int g, string how)
        {
            Simulation sim = _eval;
            var restarts = new List<int>();
            for (int i = Math.Min(strayBefore, c.Inputs.Count) - 1; i > 0; i--)
            {
                if (!c.Inputs[i].reset) continue;
                if (i >= changedStart) { if (restarts.Count < RetimeRestarts - 1) restarts.Add(i); }
                else { restarts.Add(i); break; }
            }
            (int r, int r0, int idle, int w)? pick = null;
            bool held = false;
            foreach (int r in restarts)
            {
                int idle = 0;
                while (r - idle - 1 >= 0 && idle < StrayScan && c.Inputs[r - idle - 1].Equals(new InputTick { held = c.Inputs[r - idle - 1].held })) idle++;
                int r0 = r - idle;
                // Two quick restarts in a row aren't played: one idle tick stays after an earlier restart.
                int keep = r0 > 0 && c.Inputs[r0 - 1].reset ? 1 : 0;
                sim.SetStart(_origin, 0);
                sim.Goals.Limit = _goals;
                SimSnapshot at = r0 == 0 ? _origin : sim.Advance(c.Inputs.GetRange(0, r0));
                if (at == null) continue;
                bool h = c.Inputs[r - 1].held;
                for (int w = keep - idle; w <= StrayScan; w++)
                {
                    if (w == 0) continue;
                    if (pick != null && w >= pick.Value.w) break;
                    var list = new List<InputTick>(c.Inputs.Count - r + idle + w);
                    for (int i = 0; i < idle + w; i++) list.Add(new InputTick { held = h });
                    list.AddRange(c.Inputs.Skip(r));
                    sim.SetStart(at, r0);
                    sim.Goals.Limit = g;
                    if (sim.Run(list, null, null, 1000000).Progress >= g) { pick = (r, r0, idle, w); held = h; break; }
                }
            }
            sim.SetStart(_origin, 0);
            sim.Goals.Limit = _goals;
            if (pick == null) return null;
            var (pr, p0, pidle, pw) = pick.Value;
            var inputs = new List<InputTick>(c.Inputs.Count + pw);
            inputs.AddRange(c.Inputs.Take(p0));
            for (int i = 0; i < pidle + pw; i++) inputs.Add(new InputTick { held = held });
            inputs.AddRange(c.Inputs.Skip(pr));
            _retimed = (p0, pw, p0 + pidle + pw);
            Route next = TraceRun(inputs, $"{how}, re-timed {pw:+0;-0} ticks at input {pr}");
            Log($"auto stray: {how} bought {StrayBox(c.EndReason)} before goal {g}; {pw:+0;-0} idle ticks at the quick restart at input {pr} "
                + $"({(pr < changedStart ? "before the change: the whole stretch moves" : "after the change")}, {restarts.Count} restarts tried) get it past: {Describe(next)}");
            return next;
        }

        /// <summary>The box a strict run's stray buy took (its EndReason "Bought &lt;path&gt;, which isn't the next goal's box"), or null.</summary>
        static string StrayBox(string endReason)
        {
            const string Head = "Bought ", Tail = ", which isn't the next goal's box";
            if (endReason == null) return null;
            int h = endReason.IndexOf(Head, StringComparison.Ordinal), t = endReason.IndexOf(Tail, StringComparison.Ordinal);
            return h < 0 || t < h + Head.Length ? null : endReason.Substring(h + Head.Length, t - h - Head.Length);
        }

        /// <summary>
        /// --parts stray: the stray box's trigger as a temporary avoid box on the player's position (the box's collider grown by
        /// the player's colliders, world frame), for a tail window over goals first..limit; null (with why) when one of those goals
        /// buys the box or the box has no BoxCollider2D.
        /// </summary>
        OfflineSolver.Box? StrayAvoid(string path, int first, int limit, out string why)
        {
            why = null;
            Simulation sim = _eval;
            foreach (upgradeBox b in sim.World.All<upgradeBox>())
            {
                if (TransformPath(b.transform) != path) continue;
                string id = IGTAPTasMod.FullTasGoal.BoxId(path, sim.World.SiblingIndex[b.transform]);
                for (int k = first; k <= limit; k++)
                {
                    string[] boxes = _runGoals.Goals[k - 1].boxes;
                    if (boxes != null && (Array.IndexOf(boxes, path) >= 0 || Array.IndexOf(boxes, id) >= 0)) { why = $"goal {k} buys it"; return null; }
                }
                BoxCollider2D box = b.GetComponent<BoxCollider2D>();
                if (box == null) { why = "it has no box collider"; return null; }
                Vector3 s = b.transform.lossyScale, pos = b.transform.position;
                float w = Math.Abs(box.size.x * s.x), h = Math.Abs(box.size.y * s.y);
                WorldPos c = sim.ToWorld(new ScenePos(pos.x + box.offset.x * s.x, pos.y + box.offset.y * s.y));
                // The player's colliders around its position: the trigger fires when one overlaps the box.
                float dx0 = 0f, dx1 = 0f, dy0 = 0f, dy1 = 0f;
                bool any = false;
                foreach (BoxCollider2D pc in sim.Player.GetComponents<BoxCollider2D>())
                {
                    if (!pc.enabled) continue;
                    Vector3 ps = pc.transform.lossyScale;
                    float ox = pc.offset.x * ps.x, oy = pc.offset.y * ps.y, hx = Math.Abs(pc.size.x * ps.x) / 2f, hy = Math.Abs(pc.size.y * ps.y) / 2f;
                    if (!any) { dx0 = ox - hx; dx1 = ox + hx; dy0 = oy - hy; dy1 = oy + hy; any = true; }
                    else { dx0 = Math.Min(dx0, ox - hx); dx1 = Math.Max(dx1, ox + hx); dy0 = Math.Min(dy0, oy - hy); dy1 = Math.Max(dy1, oy + hy); }
                }
                return new OfflineSolver.Box(new WorldPos(c.x - w / 2f - dx1, c.y - h / 2f - dy1), new WorldPos(c.x + w / 2f - dx0, c.y + h / 2f - dy0));
            }
            why = "no such box";
            return null;
        }

        static string TransformPath(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        /// <summary>The last MergeAuto dropped every find it had by the cost-to-go (RunWindows then credits the window no lead).</summary>
        bool _findsHopeless;
        Route _mergedOn;
        readonly HashSet<ulong> _merged = new HashSet<ulong>();

        /// <summary>
        /// Stage 1's merge of a window's finds: each (the last five) spliced with the old tail and traced into the library; a whole
        /// run that is faster is kept; one that misses a later goal waits there (WaitFix); then the library's shortest path; and only
        /// then the solve's repair (goal by goal from the new state) as before.
        /// </summary>
        void MergeAuto(string where, Route best, int prefixLength, int limit, List<List<InputTick>> found)
        {
            if (_lib == null) { Merge(where, best, prefixLength, limit, found); return; }
            // A resumed window hands back the finds it had: one already merged onto this best repairs the same way again (a course
            // 4 find at goal 6 cost two 55 s repairs), so only new ones go on. Keyed by the state and tick each arrives in at goal
            // limit, not its inputs: a window re-found the same arrival with other inputs past it (FalseEnding-Any%'s goal 116 at
            // 8723, repaired three times for 4,600 CPU s).
            if (_mergedOn != best) { _mergedOn = best; _merged.Clear(); }
            int given = found.Count, short_ = 0;
            found = Enumerable.Reverse(found).Take(5).Reverse().Where(f =>
            {
                Route at = Evaluate(best.Inputs.Take(prefixLength).Concat(f).ToList(), limit);
                if (at.Progress < limit) { short_++; return false; }
                return _merged.Add(Mix(Mix(StateKey(_eval), (ulong)(uint)WorldDigest(_eval)), (ulong)at.GoalPhysics[limit - 1]) ^ (ulong)limit);
            }).ToList();
            if (found.Count == 0)
            {
                Log($"auto: {where}: " + (short_ == 0 ? $"its {given} finds were merged onto this best already"
                    : $"{short_} of its last {Math.Min(5, given)} finds replay from the run's start short of goal {limit}"));
                return;
            }
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
                if (c.Progress == _goals && c.Live < _best.Live) { Commit(c, where); return; }
                if (c.Progress >= limit && c.Progress < _goals) broken.Add(c);
                if (c.Progress == _goals) broken.Add(c);
            }
            // A broken find whose arrival state's cost-to-go ends it after the best can't win (a state the later goals can't use):
            // dropped before the wait fix, the tail windows and the repair spend on it. None left: the window's gain is gone.
            var hopeless = broken.Where(c => c.Progress < _goals).Select(c => (c, why: CostToGoBound(c))).Where(x => x.why != null).ToList();
            if (hopeless.Count > 0)
            {
                Log($"auto: cost-to-go dropped {hopeless.Count} of {broken.Count(c => c.Progress < _goals)} broken finds of {where} before their repair ({hopeless[0].why})");
                foreach (var (c, _) in hopeless) broken.Remove(c);
                if (broken.Count == 0) { _findsHopeless = true; return; }
            }
            foreach (Route c in broken.Where(c => c.Progress < _goals).OrderByDescending(c => c.Progress))
            {
                Route fixedRun = WaitFix(c, best, (int)Math.Min(c.GoalInput[limit - 1], c.Inputs.Count), where, prefixLength);
                if (fixedRun != null && fixedRun.Live < _best.Live) { Commit(fixedRun, where + ", waiting for the Cash"); return; }
            }
            foreach (Route c in broken.Where(c => c.Progress < _goals || c.Live >= _best.Live).OrderByDescending(c => c.Progress).Take(3))
            {
                Route synced = RouteSync(c, (int)Math.Min(c.GoalInput[limit - 1], c.Inputs.Count), where);
                if (synced != null) { Commit(synced, where + ", route-synced"); return; }
            }
            // The cheap repair (--parts tail): the goals after the break re-searched as one window from the broken run's state.
            Route ahead = Part("tail") ? broken.Where(c => c.Progress > 0 && c.Progress < _goals && c.Clock(c.Progress) < best.Clock(c.Progress))
                .OrderByDescending(c => c.Progress).FirstOrDefault() : null;
            if (ahead != null)
            {
                Route tailed = TailWindows(ahead, best, where);
                if (tailed != null) { Commit(tailed, where + ", tail re-found"); return; }
            }
            if (TryStitch(where)) return;
            // As before: re-find the goals after the window from the new state.
            Merge(where, best, prefixLength, limit, found);
        }

        const int TailGoals = 3;
        double _tailMinutes = 0.5;

        /// <summary>
        /// The cheap repair after a find whose spliced tail breaks at goal p+1 (a death in the ending, a stray buy with no restart
        /// to wait at): goals p+1..p+goals (TailGoals) re-searched as one window from the broken run's state at goal p, on every
        /// thread for minutes (--tail-minutes), scored at the window's last goal against the best (seeds: the best's, the broken
        /// run's and <paramref name="follow"/>'s inputs from there). Each find is spliced with follow's tail after the window (the
        /// best's by default; combine's repair follows the known route it splices in) and replayed (then the wait fix, then
        /// route-sync); up to rounds windows while the broken run plus follow's rest still beats the best. A run that died on a stray
        /// buy gets the stray box as a temporary avoid box in its window (--parts stray, StrayAvoid). Returns a whole run faster
        /// than the best, or null.
        /// </summary>
        Route TailWindows(Route c, Route best, string where, Route follow = null, int goals = TailGoals, double minutes = -1, int rounds = 3)
        {
            follow ??= best;
            if (minutes < 0) minutes = _tailMinutes;
            // Follow's ticks from its goal k to its end: a run at goal k ends at best as fast as its tick there plus these.
            long Rest(int k) => follow.End - follow.GoalPhysics[k - 1];
            for (int round = 0; round < rounds && c != null && c.Progress < _goals && !TimeUp; round++)
            {
                int p = c.Progress;
                if (p == 0 || c.Clock(p) + Rest(p) >= best.Last) return null;
                int first = p + 1, limit = Math.Min(_goals, p + goals);
                int prefixLength = PrefixAt(c, first);
                List<InputTick> prefix = c.Inputs.GetRange(0, prefixLength);
                long baseline = best.Last + c.Origin - Rest(limit);
                // The window's start, played once here first: a prefix the engine can't play (a Movement.FixedUpdate
                // NullReferenceException in Advance, seen on a course 4 find) would throw on every solver thread and end the process.
                try { Solve.OnThread(() => { Simulation s = _create(); s.Goals.Limit = limit; return s.Advance(prefix) != null; }); }
                catch (InvalidOperationException ex)
                {
                    string dump = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_autoLog), $"tail-start-failed-{SnapshotIndex.Hash(prefix, prefix.Count):x16}.json");
                    System.IO.File.WriteAllText(dump, System.Text.Json.JsonSerializer.Serialize(new { inputs = prefix.Select(t => t.ToJson()) }));
                    Log($"auto tail: {where}: the engine can't play the broken run's state at goal {p} ({ex.Message}); skipped, prefix -> {System.IO.Path.GetFileName(dump)}");
                    return null;
                }
                Func<Simulation> create = () =>
                {
                    Simulation s = _create();
                    s.Goals.Limit = limit;
                    SimSnapshot at = s.Advance(prefix) ?? throw new InvalidOperationException("the prefix ended before its last input");
                    s.SetStart(at, prefix.Count);
                    return s;
                };
                var seeds = new List<List<InputTick>> { best.Inputs.Skip(PrefixAt(best, first)).ToList(), c.Inputs.Skip(prefixLength).ToList() };
                if (!ReferenceEquals(follow, best)) seeds.Add(follow.Inputs.Skip(PrefixAt(follow, first)).ToList());
                OfflineSolver.ForcedPath path = Solve.FullTasPaths(_name, _labels.Skip(first - 1).Take(limit - first + 1).ToList(), _labels);
                string stray = _strayAvoid ? StrayBox(c.EndReason) : null, avoided = "";
                if (stray != null)
                {
                    OfflineSolver.Box? box = StrayAvoid(stray, first, limit, out string why);
                    if (box != null) { path.Avoid.Add(box.Value); avoided = $", avoiding {stray} ({box.Value})"; }
                    else avoided = $", not avoiding {stray}: {why}";
                }
                var solver = new OfflineSolver(create, WindowCaps(_runGoals.Goals.Skip(first - 1).Take(limit - first + 1)), seeds, null, path, VisitCell,
                    WindowGrafts(first, limit));
                TuneGrafts(solver, 0, 0, 0);
                var found = new List<List<InputTick>>();
                long bestAt = long.MaxValue;
                solver.Improved += (time, runTicks, inputs, how) =>
                {
                    long atGoal = runTicks - Simulation.StrictFinishTicks;
                    lock (found)
                    {
                        bestAt = Math.Min(bestAt, atGoal);
                        if (atGoal < baseline) { found.Add(inputs); GraftFind(how); }
                    }
                };
                System.Threading.Thread[] threads = solver.Start(_threads);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!TimeUp && clock.Elapsed.TotalMinutes < minutes) System.Threading.Thread.Sleep(250);
                solver.Stop();
                foreach (System.Threading.Thread t in threads) t.Join(60000);
                GraftTally(solver);
                Log($"auto tail: {where}: goals {first}-{limit} from the broken run's state at goal {p} ({best.Last + c.Origin - c.GoalPhysics[p - 1] - Rest(p)} ticks ahead, {c.EndReason}{avoided}): "
                    + (found.Count > 0 ? $"{found.Count} reach goal {limit} before tick {baseline} (soonest {bestAt})"
                        : bestAt == long.MaxValue ? "no way through" : $"goal {limit} at tick {bestAt} against tick {baseline}"));
                if (found.Count == 0) return null;
                Route next = null;
                int nextChanged = 0;
                foreach (List<InputTick> f in Enumerable.Reverse(found).Take(5))
                {
                    List<InputTick> inputs = prefix.Concat(f).ToList();
                    Route through = Evaluate(inputs, limit);
                    if (through.Progress < limit) continue;
                    int end = (int)through.GoalInput[limit - 1];
                    if (end > inputs.Count) Pad(inputs, end);
                    else inputs.RemoveRange(end, inputs.Count - end);
                    int changedEnd = inputs.Count;
                    inputs.AddRange(follow.Inputs.Skip((int)Math.Min(follow.GoalInput[limit - 1], follow.Inputs.Count)));
                    Route r = TraceRun(inputs, where + ", tail re-found");
                    if (r.Progress == _goals && r.Live < _best.Live) return r;
                    if (r.Progress < _goals)
                    {
                        Route waited = WaitFix(r, follow, changedEnd, where + ", tail re-found", prefixLength);
                        if (waited != null && waited.Live < _best.Live) return waited;
                    }
                    if (next == null || r.Progress > next.Progress) { next = r; nextChanged = changedEnd; }
                }
                if (next == null || next.Progress <= p) return null;
                Route synced = next.Progress < _goals ? RouteSync(next, nextChanged, where + ", tail re-found") : null;
                if (synced != null) return synced;
                c = next;
            }
            return null;
        }
    }
}
