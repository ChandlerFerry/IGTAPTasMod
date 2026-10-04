using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using FullTasTracker = IGTAPTasMod.FullTasTracker;
using Vector3 = UnityEngine.Vector3;

namespace IGTAP.EngineSim.Cli
{
    // The loop builder (docs/fulltas.md, "Loop builder"): a run's buy:, start: and finish: goals are put together from pieces, short
    // input stretches from one state to one goal, kept in a library shared by every run and reused wherever the player is in the same
    // spot with the same unlocks. Every use is checked by playing it in the run. Only a goal no piece reaches is searched, once; a
    // buy the run can't pay for yet gets an extra lap of its course first, never a wait at the box. Every run's best files all its
    // goals there too (HarvestRuns), and each search window is seeded with the pieces that play its goals (PieceChain).
    sealed partial class FullTas
    {
        const int MaxExtraLaps = 64, MaxPieceTries = 12, PieceVariants = 4;
        const float PieceReach = 48f;
        const double BuildStall = 0.25;
        const int Settle = 20;

        /// <summary>Inputs from a state (the player at X, Y with these unlocks) to the goals in Target (the labels, " + " between).</summary>
        sealed class Piece
        {
            public string Target;
            public int Unlocks;
            public float X, Y;
            public List<InputTick> Inputs;
        }

        /// <summary>What a piece heads for: the run's goal Goal, or an extra lap's goals (Extra) added before it.</summary>
        sealed class Target
        {
            public int Goal;
            public FullTasGoal[] Extra;
            public string Key;
        }

        /// <summary>
        /// A way from a state to a target, checked in the run: its inputs cut where it has reached the target, the physics tick it's
        /// reached on, and the state after the cut (Next). A piece searched through the goal after it (<see cref="SolvePiece"/>'s
        /// then) carries the way on to that goal in Then.
        /// </summary>
        sealed class Step
        {
            public List<InputTick> Inputs;
            public long Physics;
            public SimSnapshot Next;
            public Step Then;
        }

        /// <summary>A piece the built route plays: from its first Length inputs (state At), toward Target.</summary>
        sealed class Use
        {
            public int Length;
            public SimSnapshot At;
            public Target Target;
            public Step Step;
        }

        sealed class Built
        {
            public List<InputTick> Inputs;
            public int Reached;
            public string Stuck;
            public readonly List<Use> Uses = new List<Use>();
        }

        FullTasTracker _runGoals;
        readonly List<Piece> _pieces = new List<Piece>();
        string _piecesFile;
        /// <summary>Set while a harvest files many pieces: they're written once at its end, not once each.</summary>
        bool _holdPieces;
        /// <summary>Saves the build so far (the frontier build's; null otherwise): called before each piece search, which can take minutes.</summary>
        Action _beforeSearch;
        /// <summary>
        /// The buy table's start for a piece search (null otherwise): a worker's replayed state at the prefix's end made the
        /// state the search starts from, as the evaluator has it (the entry's goal list and topped-up Cash, which no replay reaches).
        /// </summary>
        Func<SimSnapshot, SimSnapshot> _pieceState;

        static bool IsLoop(FullTasGoal g) { return g.kind == FullTasGoal.Buy || g.kind == FullTasGoal.Start || g.kind == FullTasGoal.Finish; }

        bool IsLoop(int goal) { return IsLoop(_runGoals.Goals[goal]); }

        static int Unlocks(Movement p)
        {
            return (p.wallJumpUnlocked ? 1 : 0) | (p.dashUnlocked ? 2 : 0) | (p.doubleJumpUnlocked ? 4 : 0) | (p.blockSwapUnlocked ? 8 : 0)
                | (p.omniDashUnlocked ? 16 : 0) | Math.Clamp(p.maxAirDashes, 0, 15) << 5 | Math.Clamp(p.maxAirJumps, 0, 15) << 9;
        }

        void LoadPieces(string traceDir)
        {
            _piecesFile = Path.Combine(traceDir, "archive", "fulltas-pieces.json");
            if (_fresh || !File.Exists(_piecesFile)) return;
            _pieces.AddRange(ReadPieces(_piecesFile));
            Log($"loop pieces: {_pieces.Count} in {Path.GetFileName(_piecesFile)}");
        }

        static IEnumerable<Piece> ReadPieces(string file)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.GetProperty("pieces").EnumerateArray().Select(p => new Piece { Target = p.GetProperty("target").GetString(),
                Unlocks = p.GetProperty("unlocks").GetInt32(), X = p.GetProperty("x").GetSingle(), Y = p.GetProperty("y").GetSingle(),
                Inputs = Solve.DecodeTicks(p.GetProperty("inputs").GetString()) }).ToList();
        }

        /// <summary>
        /// Writes the library. Runs in parallel (planned stretches, solves) share it: under a lock, the pieces another process filed
        /// since are merged in first, so no run's pieces are lost and each learns the others'.
        /// </summary>
        void SavePieces()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_piecesFile));
            using FileStream held = Solve.Lock(_piecesFile);
            if (!_fresh && File.Exists(_piecesFile))
                foreach (Piece p in ReadPieces(_piecesFile))
                    if (!_pieces.Any(q => q.Target == p.Target && q.Unlocks == p.Unlocks && Distance(q, new Vector3(p.X, p.Y, 0f)) <= 0.01f && q.Inputs.SequenceEqual(p.Inputs)))
                        _pieces.Add(p);
            Solve.WriteShared(_piecesFile, JsonSerializer.Serialize(new
            {
                pieces = _pieces.Select(p => new { target = p.Target, unlocks = p.Unlocks, x = Math.Round(p.X, 2), y = Math.Round(p.Y, 2), inputs = Solve.EncodeTicks(p.Inputs) }),
            }));
        }

        static float Distance(Piece p, Vector3 at) { return MathF.Sqrt((p.X - at.x) * (p.X - at.x) + (p.Y - at.y) * (p.Y - at.y)); }

        IEnumerable<Piece> Near(string target, int unlocks, Vector3 at)
        {
            return _pieces.Where(p => p.Target == target && p.Unlocks == unlocks && Distance(p, at) <= PieceReach)
                .OrderBy(p => Distance(p, at)).ThenBy(p => p.Inputs.Count).Take(MaxPieceTries);
        }

        /// <summary>Files a piece; per spot (1 unit), target and unlocks it keeps the PieceVariants shortest.</summary>
        void AddPiece(string target, int unlocks, Vector3 at, List<InputTick> inputs)
        {
            if (_pieces.Any(p => p.Target == target && p.Unlocks == unlocks && Distance(p, at) <= 1f && p.Inputs.SequenceEqual(inputs))) return;
            _pieces.Add(new Piece { Target = target, Unlocks = unlocks, X = at.x, Y = at.y, Inputs = inputs });
            foreach (Piece p in _pieces.Where(p => p.Target == target && p.Unlocks == unlocks && Distance(p, at) <= 1f)
                .OrderBy(p => p.Inputs.Count).Skip(PieceVariants).ToList())
                _pieces.Remove(p);
            if (!_holdPieces) SavePieces();
        }

        (Vector3 at, int unlocks) Where(SimSnapshot s)
        {
            _eval.Restore(s);
            WorldPos start = _eval.PlayerWorld;
            return (new UnityEngine.Vector3(start.x, start.y, 0f), Unlocks(_eval.Player));
        }

        SimSnapshot AdvanceFrom(SimSnapshot at, int length, List<InputTick> inputs)
        {
            _eval.SetStart(at, length);
            _eval.Goals.Limit = _goals;
            return inputs.Count == 0 ? at : _eval.Advance(inputs);
        }

        /// <summary>
        /// Plays inputs from at (after the route's first length inputs) toward target and cuts them where it's reached; null when they
        /// don't reach it, or when the cut, replayed in the run, doesn't (the cut can need a tick or two past the goal's input tick).
        /// The inputs are followed by <see cref="Settle"/> released ticks, pressed as inputs, so a cut never relies on how the engine
        /// plays inputs that ran out. An extra lap is tracked by its own goals, the run's goals set aside, and its cut must leave the
        /// run's goals as they were.
        /// </summary>
        Step Reaches(SimSnapshot at, int length, List<InputTick> inputs, Target target)
        {
            inputs = inputs.Concat(Enumerable.Repeat(new InputTick(), Settle)).ToList();
            int reached = at.Goal.Next;
            RunResult r;
            if (target.Extra == null)
            {
                _eval.SetStart(at, length);
                _eval.Goals.Limit = target.Goal + 1;
                r = _eval.Run(inputs);
                _eval.Goals.Limit = _goals;
                if (r.Progress <= target.Goal || !r.Finished) return null;
                return Cut(at, length, inputs, (int)(r.GoalInputTicks[target.Goal] - length), r.GoalPhysicsTicks[target.Goal], target.Goal + 1, null);
            }
            FullTasTracker lap = _runGoals.For(target.Extra);
            _eval.UseGoals(lap);
            try
            {
                _eval.SetStart(at.WithGoal(lap.Capture()), length);
                r = _eval.Run(inputs);
            }
            finally { _eval.UseGoals(_runGoals); }
            int last = target.Extra.Length - 1;
            if (r.Progress <= last || !r.Finished) return null;
            return Cut(at, length, inputs, (int)(r.GoalInputTicks[last] - length), r.GoalPhysicsTicks[last], reached, target);
        }

        /// <summary>What a route toward target does when replayed from at, for the log: the goals it reaches and how it ends.</summary>
        string Replayed(SimSnapshot at, int length, List<InputTick> inputs, Target target)
        {
            inputs = inputs.Concat(Enumerable.Repeat(new InputTick(), Settle)).ToList();
            RunResult r;
            if (target.Extra == null)
            {
                _eval.SetStart(at, length);
                _eval.Goals.Limit = target.Goal + 1;
                r = _eval.Run(inputs);
                _eval.Goals.Limit = _goals;
                return $"reaches goal {r.Progress} of {target.Goal + 1}, finished {r.Finished}: {r.EndReason}";
            }
            FullTasTracker lap = _runGoals.For(target.Extra);
            _eval.UseGoals(lap);
            try
            {
                _eval.SetStart(at.WithGoal(lap.Capture()), length);
                r = _eval.Run(inputs);
            }
            finally { _eval.UseGoals(_runGoals); }
            return $"reaches {r.Progress} of the lap's {target.Extra.Length} goals, finished {r.Finished}: {r.EndReason}";
        }

        /// <summary>
        /// The shortest cut of inputs, from the goal's input tick on, whose replay in the run has reached it (want goals reached; an
        /// extra lap's goals reached too). The goal's input tick counts engine run ticks, which can fall behind the inputs pressed
        /// (a quick restart's tick, for one), so the cut is found by replaying rather than trusted.
        /// </summary>
        Step Cut(SimSnapshot at, int length, List<InputTick> inputs, int used, long physics, int want, Target extra)
        {
            for (int n = Math.Max(1, used); n <= inputs.Count; n++)
            {
                List<InputTick> part = inputs.GetRange(0, n);
                SimSnapshot next = AdvanceFrom(at, length, part);
                if (next == null || next.Goal.Next > want)
                {
                    _eval.SetStart(at, length);
                    _eval.Goals.Limit = _goals;
                    RunResult r = _eval.Run(part);
                    Log($"build: a way to goal {want} reached it at input {used}, but cut at {n} it " + (next == null ? "ends the run" : "reaches goal " + next.Goal.Next) + $": {r.EndReason}");
                    return null;
                }
                if (next.Goal.Next < want) continue;
                if (extra != null)
                {
                    // Reaching the lap's last goal ends the run there (no state to capture): judge the cut by the run's progress, reached
                    // on or before the cut's last input. Not by capping the run at the cut's length: a dash plays physics ticks with no
                    // input of their own, so a lap with dashes needs more physics ticks than inputs.
                    FullTasTracker lap = _runGoals.For(extra.Extra);
                    _eval.UseGoals(lap);
                    try
                    {
                        _eval.SetStart(at.WithGoal(lap.Capture()), length);
                        RunResult r = _eval.Run(part);
                        if (r.Progress != extra.Extra.Length || r.GoalInputTicks[extra.Extra.Length - 1] - length > n) continue;
                    }
                    finally { _eval.UseGoals(_runGoals); }
                }
                return new Step { Inputs = part, Physics = physics, Next = next };
            }
            Log($"build: a way to goal {want} reached it at input {used}, but no cut up to {inputs.Count} inputs reproduces it in the run");
            return null;
        }

        /// <summary>
        /// Idle pads before a quick restart, 0–24 ticks. A restart keeps some of the player's state (framesSinceLastDash: a dash
        /// input right after it fires a tick late while the last dash cools down), so a piece filed after one restart can need
        /// another pad, exactly, before another.
        /// </summary>
        static readonly int[] RestartPads = Enumerable.Range(0, 25).ToArray();

        /// <summary>Each idle pad and quick restart from at that keeps the goals reached, shortest first, with the state after it.</summary>
        IEnumerable<(List<InputTick> inputs, SimSnapshot state)> Restarts(SimSnapshot at, List<InputTick> route, int reached)
        {
            if (reached > 0 && _labels[reached - 1].StartsWith("at:", StringComparison.Ordinal)) yield break;
            foreach (int pad in RestartPads)
            {
                if (pad == 0 && route.Count > 0 && route[route.Count - 1].reset) continue;
                var pre = Enumerable.Repeat(new InputTick(), pad).Append(new InputTick { reset = true }).ToList();
                SimSnapshot s = AdvanceFrom(at, route.Count, pre);
                if (s != null && s.Goal.Next == reached) yield return (pre, s);
            }
        }

        /// <summary>
        /// The fastest library piece that reaches target from at: as it is, or after the shortest restart pad any piece filed at the
        /// restart's spot works from. Null for none.
        /// </summary>
        Step BestKnown(SimSnapshot at, List<InputTick> route, int reached, Target target)
        {
            Step best = null;
            foreach (Step step in KnownSteps(at, route, reached, target))
                if (best == null || step.Physics < best.Physics) best = step;
            return best;
        }

        /// <summary>
        /// A search window's library seed: from at (after prefix), goal first on, the fastest library piece for each goal played in the
        /// run one after another, until one has none or the window's goals are done. Pieces come from every run's best (<see
        /// cref="HarvestRuns"/>), so a goal another run reaches from the same spot with the same unlocks is played, not searched.
        /// Returns the inputs and how many goals they reach (0: none).
        /// </summary>
        (List<InputTick> inputs, int reached) PieceChain(SimSnapshot at, List<InputTick> prefix, int first, int limit)
        {
            var route = prefix.ToList();
            int k = first;
            for (; k < limit && at != null; k++)
            {
                Step step = BestKnown(at, route, k, new Target { Goal = k, Key = _labels[k] });
                if (step == null) break;
                route.AddRange(step.Inputs);
                at = step.Next;
            }
            return (route.GetRange(prefix.Count, route.Count - prefix.Count), k - first);
        }

        /// <summary>
        /// Every library piece that reaches target from at, as <see cref="BestKnown"/> tries them: as they are, then after each restart
        /// pad up to the first whose pieces beat the fastest so far (or the first with none filed at its spot).
        /// </summary>
        List<Step> KnownSteps(SimSnapshot at, List<InputTick> route, int reached, Target target)
        {
            var steps = new List<Step>();
            long fastest = long.MaxValue;
            void Try(List<InputTick> inputs)
            {
                if (route.Count > 0 && route[route.Count - 1].reset && inputs.Count > 0 && inputs[0].reset) return;
                Step step = Reaches(at, route.Count, inputs, target);
                if (step == null) return;
                steps.Add(step);
                fastest = Math.Min(fastest, step.Physics);
            }
            (Vector3 here, int unlocks) = Where(at);
            foreach (Piece p in Near(target.Key, unlocks, here).ToList()) Try(p.Inputs);
            foreach (var (pre, state) in Restarts(at, route, reached))
            {
                (Vector3 there, int after) = Where(state);
                List<Piece> filed = Near(target.Key, after, there).ToList();
                if (filed.Count == 0) break;
                long direct = fastest;
                foreach (Piece p in filed) Try(pre.Concat(p.Inputs).ToList());
                if (fastest < direct) break;
            }
            return steps;
        }

        FullTasGoal RunGoal(string label, string kind, int course)
        {
            return _runGoals.Goals.FirstOrDefault(g => g.label == label) ?? new FullTasGoal { label = label, kind = kind, course = course };
        }

        /// <summary>
        /// Searches a piece from at (after route) to target, seeded with the library's pieces for it, the saved course routes and
        /// <paramref name="extraSeeds"/>; once more with three times the budget if the first search finds nothing. Null: no way there.
        /// With <paramref name="then"/> (the goal right after target), the search runs through that goal too and is scored there, so a
        /// way that reaches target later but the next goal sooner wins; the step returned carries the rest in Then. <paramref name="beat"/>
        /// is the physics tick the old way reaches the scored goal on (the studio's "To beat").
        /// </summary>
        Step SolvePiece(SimSnapshot at, List<InputTick> route, int reached, Target target, double budgetMinutes, double stallMinutes, List<List<InputTick>> extraSeeds,
            Target then = null, long? beat = null)
        {
            _beforeSearch?.Invoke();
            List<InputTick> prefix = route.ToList();
            // Seeds after each restart pad for the nearest few pieces (the pad their dash timing needs isn't known).
            var restarts = Restarts(at, prefix, reached).ToList();
            (Vector3 here, int _) = Where(at);
            var seeds = new List<List<InputTick>>();
            void Seed(List<InputTick> s)
            {
                if (s.Count > 0 && !(prefix.Count > 0 && prefix[prefix.Count - 1].reset && s[0].reset) && !seeds.Any(k => k.SequenceEqual(s))) seeds.Add(s);
            }
            foreach (var (p, rank) in _pieces.Where(p => p.Target == target.Key).OrderBy(p => Distance(p, here)).Take(MaxPieceTries).Select((p, i) => (p, i)))
            {
                Seed(p.Inputs);
                if (rank < 3) foreach (var (pre, _) in restarts) Seed(pre.Concat(p.Inputs).ToList());
            }
            // The search's goals: every goal from reached through the scored one (a buy table set is several).
            int last = (then ?? target).Goal, first = Math.Min(reached, target.Goal);
            FullTasGoal[] goals = target.Extra ?? _runGoals.Goals.Skip(first).Take(last + 1 - first).ToArray();
            _eval.Restore(at);
            string caps = WindowCaps(goals);
            foreach (var s in SeedStarts(at, prefix, new HashSet<string>(goals.Select(g => g.label)), restarts.Count > 0, caps)) Seed(s.inputs);
            foreach (List<InputTick> s in extraSeeds) Seed(s);
            string span = target.Extra != null ? target.Key + " (an extra lap for Cash before " + _labels[target.Goal] + ")"
                : then != null ? _labels[target.Goal] + " + " + _labels[then.Goal] : _labels[target.Goal];
            string title = "piece for goal " + (target.Goal + 1) + (then != null ? "-" + (then.Goal + 1) : "") + "/" + _goals;
            Func<Simulation> create = () =>
            {
                (Simulation sim, SimSnapshot state) = Prefixed(prefix, title);
                if (target.Extra != null)
                {
                    FullTasTracker lap = sim.Goals.For(target.Extra);
                    sim.UseGoals(lap);
                    sim.SetStart(state.WithGoal(lap.Capture()), prefix.Count);
                }
                else
                {
                    sim.SetStart(_pieceState == null ? state : _pieceState(state), prefix.Count);
                    sim.Goals.Limit = (then ?? target).Goal + 1;
                }
                return sim;
            };
            string archiveFile = Path.Combine(_archiveDir, "piece-" + (target.Goal + 1) + "-" + ArchiveHash(prefix, goals.Select(g => g.label)).ToString("x16") + ".json");
            for (int attempt = 1; attempt <= 2 && !TimeUp; attempt++)
            {
                double budget = attempt == 1 ? budgetMinutes : budgetMinutes * 3;
                int id = ++_windowId;
                Emit(new { type = "window", id, kind = "piece", first = reached, limit = (then ?? target).Goal + 1, attempt, budget = Math.Round(budget * 60, 1),
                    start = reached == 0 ? 0.0 : Seconds(at.Goal.PhysicsTicks[reached - 1]), target = beat.HasValue ? Seconds(beat.Value) : (double?)null, savedStarts = seeds.Count, piece = span });
                // A start create alters (an extra lap's own goals, _pieceState's goal list or Cash) isn't the run's: a stored goal state
                // on a cell's prefix would bring back the run's goal list (progress past the window's goals) and its Cash.
                // Every route the search improved on, fastest first: one that doesn't reproduce in the run (a replay from a
                // cached state can differ from one from the start) leaves the others.
                List<List<InputTick>> routes = Explore(id, title, span, create, caps, seeds, archiveFile, long.MaxValue, budget, stallMinutes, null,
                    goals.Select(g => g.label).ToList(), out List<InputTick> best, resume: target.Extra == null && _pieceState == null);
                if (best != null) routes.Add(best);
                Step step = null;
                foreach (List<InputTick> found in Enumerable.Reverse(routes).Distinct(SequenceComparer.Instance).Take(40))
                {
                    Step s = Reaches(at, prefix.Count, found, target);
                    if (s != null && then != null) s = Continued(s, prefix.Count, found, then);
                    if (s != null && (step == null || Scored(s) < Scored(step))) step = s;
                }
                if (step != null) return step;
                if (routes.Count > 0) Log($"{title}: none of its {routes.Count} routes reproduced when replayed in the run; the fastest " + Replayed(at, prefix.Count, routes[routes.Count - 1], target));
            }
            return null;
        }

        /// <summary>The step with the rest of found (after its cut) checked on to then as its Then; null when the rest doesn't reach it.</summary>
        Step Continued(Step s, int length, List<InputTick> found, Target then)
        {
            List<InputTick> rest = found.Skip(s.Inputs.Count).ToList();
            if (rest.Count == 0 || s.Inputs[s.Inputs.Count - 1].reset && rest[0].reset) return null;
            Step t = Reaches(s.Next, length + s.Inputs.Count, rest, then);
            if (t == null) return null;
            s.Then = t;
            return s;
        }

        /// <summary>The physics tick a step is judged by: its own goal's, or the next goal's when it was searched through that.</summary>
        static long Scored(Step s) { return s.Then?.Physics ?? s.Physics; }

        /// <summary>
        /// The old route's own way from at to goal (its inputs from where the build stands, when the build so far is the old route's
        /// start, else from its previous goal), checked in the run; null when it doesn't reach the goal from here.
        /// </summary>
        Step OldStep(SimSnapshot at, List<InputTick> route, Route old, int goal, Target target)
        {
            if (old == null || old.Progress <= goal) return null;
            int from = goal == 0 ? 0 : (int)Math.Min(old.GoalInput[goal - 1], old.Inputs.Count);
            if (route.Count <= old.Inputs.Count && route.Count >= from && Same(route, old.Inputs)) from = route.Count;
            int to = (int)Math.Min(old.GoalInput[goal] + 2, old.Inputs.Count);
            if (to <= from) return null;
            List<InputTick> inputs = old.Inputs.GetRange(from, to - from);
            if (route.Count > 0 && route[route.Count - 1].reset && inputs[0].reset) return null;
            return Reaches(at, route.Count, inputs, target);
        }

        /// <summary>Whether route is the start of inputs.</summary>
        static bool Same(List<InputTick> route, List<InputTick> inputs)
        {
            for (int i = route.Count - 1; i >= 0; i--)
                if (!route[i].Equals(inputs[i])) return false;
            return true;
        }

        /// <summary>
        /// The physics tick the goal after goal is reached on once step has reached goal: by the fastest library piece or the old route's
        /// own way from there. long.MaxValue when neither does, or the step's own tick when goal is the run's last.
        /// </summary>
        long Ahead(Step step, List<InputTick> route, Route old, int goal)
        {
            int next = goal + 1;
            if (next >= _goals) return step.Physics;
            List<InputTick> after = route.Concat(step.Inputs).ToList();
            var target = new Target { Goal = next, Key = _runGoals.Goals[next].label };
            long best = long.MaxValue;
            Step known = IsLoop(next) ? BestKnown(step.Next, after, next, target) : null;
            if (known != null) best = known.Physics;
            Step mine = OldStep(step.Next, after, old, next, target);
            if (mine != null && mine.Physics < best) best = mine.Physics;
            return best;
        }

        /// <summary>
        /// Of the library's step and the old route's own to goal, the one after which the next goal comes sooner (a piece that gets
        /// to its goal first can leave the player where the next goal takes far longer); on a tie the one that reaches goal sooner,
        /// the old one when both do on the same tick.
        /// </summary>
        Step Pick(Step known, Step mine, List<InputTick> route, Route old, int goal)
        {
            if (known.Inputs.SequenceEqual(mine.Inputs)) return mine;
            long a = Ahead(known, route, old, goal), b = Ahead(mine, route, old, goal);
            if (a != b) return a < b ? known : mine;
            return known.Physics < mine.Physics ? known : mine;
        }

        sealed class SequenceComparer : IEqualityComparer<List<InputTick>>
        {
            public static readonly SequenceComparer Instance = new SequenceComparer();
            public bool Equals(List<InputTick> a, List<InputTick> b) { return a.SequenceEqual(b); }
            public int GetHashCode(List<InputTick> a)
            {
                var h = new HashCode();
                h.Add(a.Count);
                foreach (InputTick t in a.Take(16)) h.Add(t);
                return h.ToHashCode();
            }
        }

        /// <summary>
        /// Files a searched step in the library. A step that quick restarts within its first few ticks is filed from the restart on,
        /// so every restart to that spot reuses it (a later use gets its own pad before the restart, checked like the rest).
        /// </summary>
        void Keep(Step step, SimSnapshot at, List<InputTick> route, Target target)
        {
            SimSnapshot from = at;
            int skip = 0, reset = step.Inputs.FindIndex(k => k.reset);
            if (reset >= 0 && reset <= RestartPads[RestartPads.Length - 1])
            {
                SimSnapshot s = AdvanceFrom(at, route.Count, step.Inputs.Take(reset + 1).ToList());
                if (s != null) { from = s; skip = reset + 1; }
            }
            if (step.Inputs.Count <= skip) return;
            (Vector3 here, int unlocks) = Where(from);
            AddPiece(target.Key, unlocks, here, step.Inputs.Skip(skip).ToList());
        }

        /// <summary>Appends a checked step (<see cref="Reaches"/>) to the build; false when it would press quick restart twice in a row.</summary>
        static bool Append(Built built, ref SimSnapshot at, Step step, Target target)
        {
            int length = built.Inputs.Count;
            if (length > 0 && built.Inputs[length - 1].reset && step.Inputs[0].reset) return false;
            built.Uses.Add(new Use { Length = length, At = at, Target = target, Step = step });
            built.Inputs.AddRange(step.Inputs);
            at = step.Next;
            return true;
        }

        /// <summary>The course a loop goal belongs to: the last start/finish goal before it (else the next one); 0 for none.</summary>
        int LoopCourse(int goal)
        {
            for (int g = goal - 1; g >= 0; g--)
                if (_runGoals.Goals[g].kind == FullTasGoal.Start || _runGoals.Goals[g].kind == FullTasGoal.Finish) return _runGoals.Goals[g].course;
            for (int g = goal; g < _goals; g++)
                if (_runGoals.Goals[g].kind == FullTasGoal.Start || _runGoals.Goals[g].kind == FullTasGoal.Finish) return _runGoals.Goals[g].course;
            return 0;
        }

        /// <summary>
        /// Puts one step toward target on the build: a library piece (unless <paramref name="searchOnly"/>: the caller found none), else a
        /// searched one, filed for reuse. Null, or why not.
        /// </summary>
        string BuildStep(Built built, ref SimSnapshot at, Target target, List<List<InputTick>> extraSeeds, bool searchOnly)
        {
            Step step = searchOnly ? null : BestKnown(at, built.Inputs, built.Reached, target);
            if (step == null)
            {
                // The build takes the first way found (and a quarter minute's betterment); polish searches the pieces it plays.
                step = SolvePiece(at, built.Inputs, built.Reached, target, _pieceMinutes, BuildStall, extraSeeds);
                if (step == null) return target.Extra != null ? $"no way found to {target.Key} (an extra lap for Cash) before goal {target.Goal + 1} ({_labels[target.Goal]})"
                    : $"no way found to goal {target.Goal + 1} ({_labels[target.Goal]}) from goal {built.Reached}";
                Keep(step, at, built.Inputs, target);
            }
            return Append(built, ref at, step, target) ? null : $"the way to goal {target.Goal + 1} ({_labels[target.Goal]}) would quick restart twice in a row";
        }

        /// <summary>
        /// The run's next goal onto the build, after extra laps while it's a buy the run can't pay for. Null, or why not. With an old
        /// route, its own way here is a candidate next to the library's (<see cref="Pick"/>); <paramref name="followOld"/> takes only that.
        /// </summary>
        string BuildGoal(Built built, ref SimSnapshot at, Route old, bool followOld)
        {
            int goal = built.Reached;
            FullTasGoal g = _runGoals.Goals[goal];
            var target = new Target { Goal = goal, Key = g.label };
            Step mine = OldStep(at, built.Inputs, old, goal, target);
            if (followOld)
                return mine == null ? $"the route's own way to goal {goal + 1} ({g.label}) doesn't replay here"
                    : Append(built, ref at, mine, target) ? Advanced(built) : $"the way to goal {goal + 1} ({g.label}) would quick restart twice in a row";
            List<List<InputTick>> oldSeeds = old != null && old.Progress > goal && goal > 0
                ? new List<List<InputTick>> { old.Inputs.Skip((int)old.GoalInput[goal - 1]).Take((int)(old.GoalInput[goal] - old.GoalInput[goal - 1]) + 2).ToList() }
                : new List<List<InputTick>>();
            for (int laps = 0; ; laps++)
            {
                Step known = BestKnown(at, built.Inputs, goal, target);
                if (laps == 0 && mine != null) known = known == null ? mine : Pick(known, mine, built.Inputs, old, goal);
                if (known != null) return Append(built, ref at, known, target) ? Advanced(built) : $"the way to goal {goal + 1} ({g.label}) would quick restart twice in a row";
                _eval.Restore(at);
                if (g.kind != FullTasGoal.Buy || _eval.Shop.Affordable(g.boxes) != false) break;
                // Clone income that pays for the box within a few seconds beats a lap (FalseEnding2-Any%'s boosted Course 2 clones
                // after the statue; its lap before buy:buydj took 14.6 s from start:3). Standing still first (the player already at
                // the box: an area goal on it before the buy), then a search that may walk there and stand until it can pay; either
                // is taken only when it buys the box, else the lap follows.
                if (laps == 0 && AffordableWithin(at, built.Inputs, g, ShortWaitTicks) && !LapSooner(built, at, goal, target))
                {
                    Step stand = Stand(at, built.Inputs.Count, target);
                    if (stand != null && Append(built, ref at, stand, target))
                    {
                        Log($"build: goal {goal + 1} ({g.label}) bought {stand.Inputs.Count} ticks on, walking under its box and hopping for the clones' income, no lap");
                        return Advanced(built);
                    }
                    Log($"build: goal {goal + 1} ({g.label}) is short of Cash, but clone income pays for it within {ShortWaitTicks} ticks: searching it before a lap");
                    if (BuildStep(built, ref at, target, oldSeeds, true) == null) return Advanced(built);
                }
                int course = LoopCourse(goal);
                if (course == 0) return $"goal {goal + 1} ({g.label}): short of Cash, and no course to lap for it";
                if (laps == MaxExtraLaps) return $"goal {goal + 1} ({g.label}): still short of Cash after {MaxExtraLaps} extra laps";
                // The lap's gate and finish are pieces of their own, filed under the same labels as the run's own laps: each
                // reuses the other's.
                foreach (FullTasGoal part in new[] { RunGoal("start:" + course, FullTasGoal.Start, course), RunGoal("finish:" + course, FullTasGoal.Finish, course) })
                {
                    string stuck = BuildStep(built, ref at, new Target { Goal = goal, Extra = new[] { part }, Key = part.label }, new List<List<InputTick>>(), false);
                    if (stuck != null) return stuck;
                }
                Log($"build: extra lap of Course {course} before goal {goal + 1} ({g.label}): short of Cash");
            }
            if (g.boxes != null)
            {
                _eval.Restore(at);
                string state = _eval.Shop.Describe(g.boxes);
                // Inactive: capped, or not unlocked by the goals so far. Pieces buy nothing else, so no search changes that.
                if (g.kind == FullTasGoal.Buy && _eval.Shop.Affordable(g.boxes) == null) return $"goal {goal + 1} ({g.label}) can't be bought: {state}";
                Log($"build: searching goal {goal + 1} ({g.label}): {state}");
            }
            string why = BuildStep(built, ref at, target, oldSeeds, true);
            return why ?? Advanced(built);
        }

        static string Advanced(Built built) { built.Reached++; return null; }

        /// <summary>
        /// --reach-polish: whether an extra lap, then the buy, buys target sooner than standing under its box for the clones' income
        /// (<see cref="Stand"/>). FalseEnding%-280's goal 97 (buy:buyclone2) stood 16 s where a Course 2 lap and the walk back take 4.
        /// </summary>
        bool LapSooner(Built built, SimSnapshot at, int goal, Target target)
        {
            int course = LoopCourse(goal);
            if (_reachPolish == 0 || course == 0) return false;
            Step stand = Stand(at, built.Inputs.Count, target);
            // No hop under the box buys it: the lap, not a search for a wait (minutes, at buy:buystatue).
            if (stand == null) return true;
            var trial = new Built { Inputs = built.Inputs.ToList(), Reached = built.Reached };
            foreach (FullTasGoal part in new[] { RunGoal("start:" + course, FullTasGoal.Start, course), RunGoal("finish:" + course, FullTasGoal.Finish, course) })
                if (BuildStep(trial, ref at, new Target { Goal = goal, Extra = new[] { part }, Key = part.label }, new List<List<InputTick>>(), false) != null) return false;
            Step buy = BestKnown(at, trial.Inputs, goal, target);
            if (buy == null || stand.Physics <= buy.Physics) return false;
            Log($"build: goal {goal + 1} ({_labels[goal]}): a lap of Course {course} first buys it at tick {buy.Physics}, standing at {stand.Physics}");
            return true;
        }

        /// <summary>Ticks of clone income a buy may wait for instead of an extra lap (<see cref="BuildGoal"/>): about a lap and its walk.</summary>
        const int ShortWaitTicks = 900;

        /// <summary>
        /// A walk of 0 to 60 ticks left or right, then hops in place for up to ShortWaitTicks, that buys target's box: the player
        /// stands under it (an area goal there before the buy), each hop touches the box's trigger (a strip along its underside:
        /// buydj's is 6 units tall, 140 above the floor, so standing never buys) and the clones' income pays for it meanwhile. The
        /// one that buys it first; null for none.
        /// </summary>
        Step Stand(SimSnapshot at, int length, Target target)
        {
            Step best = null;
            foreach (List<InputTick> inputs in HopWays())
            {
                Step s = Reaches(at, length, inputs, target);
                if (s != null && (best == null || s.Physics < best.Physics)) best = s;
            }
            return best;
        }

        /// <summary>Stand's ways: a walk of 0 to 60 ticks left or right, then hops in place (or standing) for ShortWaitTicks.</summary>
        static IEnumerable<List<InputTick>> HopWays()
        {
            foreach (int period in new[] { 0, 30, 40, 50 })
                foreach (int hold in period == 0 ? new[] { 0 } : new[] { 6, 14 })
                    for (int walk = 0; walk <= 60; walk += 4)
                        foreach (float x in walk == 0 ? new[] { 0f } : new[] { -1f, 1f })
                        {
                            var inputs = Enumerable.Repeat(new InputTick { x = x }, walk).ToList();
                            for (int t = 0; t < ShortWaitTicks; t++)
                            {
                                int k = period == 0 ? -1 : t % period;
                                inputs.Add(new InputTick { press = k == 0, held = k >= 0 && k < hold, release = k == hold });
                            }
                            yield return inputs;
                        }
        }

        /// <summary>Whether goal's box is affordable after standing still for ticks from at (the clones' income meanwhile).</summary>
        bool AffordableWithin(SimSnapshot at, List<InputTick> route, FullTasGoal goal, int ticks)
        {
            SimSnapshot later = AdvanceFrom(at, route.Count, Enumerable.Repeat(new InputTick(), ticks).ToList());
            if (later == null) return false;
            _eval.Restore(later);
            return _eval.Shop.Affordable(goal.boxes) == true;
        }

        /// <summary>
        /// Builds the run's goals from..until-1 onto the route's first <paramref name="from"/> goals, as far as it gets;
        /// <paramref name="followOld"/>: with old's own ways only (the uses of a route as it is).
        /// </summary>
        Built BuildLoops(Route route, int from, int until, Route old, Action<Built> checkpoint, bool followOld = false)
        {
            // A goal's input tick can fall a tick or two before the replay of the cut counts it (as Append allows for).
            int length = from == 0 ? 0 : (int)route.GoalInput[from - 1];
            var built = new Built { Reached = from };
            SimSnapshot at = null;
            _eval.UseGoals(_runGoals);
            for (int extra = 0; extra <= 2 && at?.Goal.Next != from; extra++)
            {
                if (from == 0 && extra > 0) break;
                built.Inputs = route.Inputs.Take(length + extra).ToList();
                if (built.Inputs.Count < length + extra) Pad(built.Inputs, length + extra);
                _eval.SetStart(_origin, 0);
                _eval.Goals.Limit = _goals;
                at = _eval.Advance(built.Inputs);
            }
            if (at == null || at.Goal.Next != from)
            {
                built.Stuck = $"the route doesn't replay to goal {from}";
                return built;
            }
            TimeSpan saved = _clock.Elapsed;
            _beforeSearch = checkpoint == null ? null : () => { saved = _clock.Elapsed; checkpoint(built); };
            try
            {
                while (built.Reached < until && !TimeUp)
                {
                    string stuck = BuildGoal(built, ref at, old, followOld);
                    if (stuck != null) { built.Stuck = stuck; break; }
                    if (checkpoint != null && (_clock.Elapsed - saved).TotalSeconds >= 60) { saved = _clock.Elapsed; checkpoint(built); }
                }
            }
            finally { _beforeSearch = null; }
            return built;
        }

        /// <summary>Builds the loop goals from the best's frontier on. Null when it got as far as the goals or the time allow, else why it's stuck.</summary>
        string BuildFrontier()
        {
            Phase("build");
            int reached = _best.Progress, until = reached;
            while (until < _goals && IsLoop(until) && (_reachPolish == 0 || until == reached)) until++;
            Log($"build goals {reached + 1}-{until}/{_goals} from loop pieces ({_pieces.Count} in the library)");
            void Save(Built b)
            {
                if (b.Reached <= _best.Progress) return;
                Route r = Evaluate(b.Inputs.ToList());
                if (Better(r, _best)) Commit(r, $"built goals {reached + 1}-{r.Progress} from loop pieces");
            }
            Built built = BuildLoops(_best, reached, until, null, Save);
            Save(built);
            return TimeUp ? null : built.Stuck;
        }

        /// <summary>
        /// --reach-polish: the window of --window goals ending at the best's frontier, searched for up to --reach-polish seconds (a
        /// third of that without a faster way ends it). A faster way is kept (nothing after the frontier to replay) and its goals
        /// filed as library pieces, so the next laps build from it.
        /// </summary>
        void PolishFrontier()
        {
            Route best = _best;
            int limit = best.Progress, i = Math.Max(0, limit - _window);
            int prefixLength = i == 0 ? 0 : (int)Math.Min(best.GoalInput[i - 1], best.Inputs.Count);
            var seeds = new List<List<InputTick>> { best.Inputs.Skip(prefixLength).ToList() };
            double minutes = _reachPolish / 60;
            List<List<InputTick>> found = SearchWindow("polish", best, prefixLength, limit, seeds, best.GoalPhysics[limit - 1], minutes, 0, minutes / 3);
            if (found.Count > 0) Merge("faster through goals " + (i + 1) + "-" + limit, best, prefixLength, limit, found);
            if (_best == best) return;
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            SimSnapshot at = prefixLength == 0 ? _origin : _eval.Advance(_best.Inputs.GetRange(0, prefixLength));
            if (at != null && at.Goal.Next == i) Harvest(at, _best.Inputs.GetRange(0, prefixLength), i, _best.Progress);
        }

        /// <summary>
        /// Polish for loop goals: rebuilds each stretch of them from the library and the best's own ways (kept when the whole run gets
        /// faster), then searches the pieces the best now plays there for faster ones; when any is found, the stretch is rebuilt
        /// right away with it.
        /// </summary>
        void PolishLoops()
        {
            for (int a = 0; a < _best.Progress && !TimeUp;)
            {
                if (!IsLoop(a)) { a++; continue; }
                int b = a;
                while (b < _best.Progress && IsLoop(b)) b++;
                Route best = _best;
                Log($"rebuild goals {a + 1}-{b}/{_goals} from loop pieces");
                Built built = BuildLoops(best, a, b, best, null);
                if (built.Reached == b)
                {
                    Route run = Splice(new Route { Inputs = built.Inputs }, built.Inputs.Count, new List<InputTick>(), b, best);
                    if (run != null && Better(run, _best)) Commit(run, $"goals {a + 1}-{b} rebuilt from loop pieces");
                    else if (run != null && run.Progress >= b && run.GoalPhysics[b - 1] < best.GoalPhysics[b - 1] && _repairedRebuilds.Add(Solve.EncodeTicks(built.Inputs)))
                        Repair($"goals {a + 1}-{b} rebuilt from loop pieces", best, run);
                    else Log($"rebuilt goals {a + 1}-{b}: the whole run is no faster (" + (run == null ? "its splice didn't replay" : run.Progress < b ? $"replayed, it reaches goal {run.Progress}"
                        : $"goal {b} at tick {run.GoalPhysics[b - 1]} against the best's {best.GoalPhysics[b - 1]}, the run's end at {(run.Progress == _goals ? run.Last.ToString() : "goal " + run.Progress)}") + ")");
                }
                else if (built.Stuck != null) Log($"rebuild of goals {a + 1}-{b} stopped: {built.Stuck}");
                if (TimeUp) return;
                // The pieces are searched where the best plays them: a rejected rebuild's states are a route nobody keeps.
                Built played = _best.Progress >= b ? BuildLoops(_best, a, b, _best, null, true) : built;
                if (played.Stuck != null && played != built) Log($"replaying goals {a + 1}-{b} of the best stopped: {played.Stuck}");
                if (!ImprovePieces(played)) a = b;
            }
        }

        /// <summary>Rebuilt stretches (their inputs) already repaired this solve: a repair re-finds every goal after them, so once.</summary>
        readonly HashSet<string> _repairedRebuilds = new HashSet<string>();

        /// <summary>Pieces ImprovePieces has searched this solve (target, spot and the ways searched from), not searched again.</summary>
        readonly HashSet<string> _searchedPieces = new HashSet<string>();

        /// <summary>
        /// Searches each distinct piece a build played (most played first) for a faster one, and files what it finds; true when it
        /// found any. A piece whose next use is the run's next goal is searched through that goal and scored there, so a way that
        /// gets to its own goal later but leaves the next one sooner is found, and one that gets there sooner but costs the next
        /// goal more isn't taken.
        /// </summary>
        bool ImprovePieces(Built built)
        {
            bool improved = false;
            var groups = built.Uses.Select((u, i) => (u, i)).GroupBy(p =>
            {
                (Vector3 at, int unlocks) = Where(p.u.At);
                return (p.u.Target.Key, unlocks, (int)MathF.Round(at.x / 8f), (int)MathF.Round(at.y / 8f));
            }).OrderByDescending(g => g.Count()).ToList();
            foreach (var group in groups)
            {
                if (TimeUp) break;
                (Use u, int index) = group.First();
                Use next = index + 1 < built.Uses.Count ? built.Uses[index + 1] : null;
                bool pair = u.Target.Extra == null && next != null && next.Target.Extra == null && next.Target.Goal == u.Target.Goal + 1;
                string key = $"{group.Key}|{Solve.EncodeTicks(u.Step.Inputs)}|{(pair ? Solve.EncodeTicks(next.Step.Inputs) : "")}";
                if (!_searchedPieces.Add(key)) continue;
                List<InputTick> prefix = built.Inputs.GetRange(0, u.Length);
                long beat = pair ? next.Step.Physics : u.Step.Physics;
                List<InputTick> seed = pair ? u.Step.Inputs.Concat(next.Step.Inputs).ToList() : u.Step.Inputs;
                Step faster = SolvePiece(u.At, prefix, u.At.Goal.Next, u.Target, _windowMinutes, _windowMinutes / 3,
                    new List<List<InputTick>> { seed }, pair ? next.Target : null, beat);
                if (faster == null || Scored(faster) >= beat) continue;
                Log($"faster piece for {u.Target.Key}" + (pair ? " + " + next.Target.Key : "") + $" ({group.Count()} uses): {beat - Scored(faster)} ticks each");
                Keep(faster, u.At, prefix, u.Target);
                if (pair) Keep(faster.Then, faster.Next, prefix.Concat(faster.Inputs).ToList(), next.Target);
                improved = true;
            }
            return improved;
        }
    }
}
