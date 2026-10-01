using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using FullTasTracker = IGTAPTasMod.FullTasTracker;

namespace IGTAP.EngineSim.Cli
{
    // The engine farm planner (docs/fulltas.md, "Engine farm planner"): plans a run's buys and laps from one goal to a target buy
    // by playing each choice in the simulation (the game's own economy, the loop builder's pieces) instead of a model of it.
    sealed partial class FullTas
    {
        public static int Plan(string[] args) { return new FullTas(args).PlanFarm(args); }

        FullTasGoal[] _kept;
        FullTasTracker _planBase;
        string _whyNot;
        // The run's goal labels, and the best's stretch the plan replaces (goals from..until): its ways are borrowed (Borrowed).
        (string[] labels, int from, int until) _borrow;
        bool _recordTried;
        // Record laps: each saved course route's idle ticks after the quick restart (-1: none plays it).
        readonly Dictionary<string, int> _recordPads = new Dictionary<string, int>();
        const int MaxRecordPad = 40;
        // Record laps searched from the restart once per course, and the ways found (tried with the saved routes after).
        readonly HashSet<int> _recordSearched = new HashSet<int>();
        readonly List<(int course, List<InputTick> inputs)> _recordWays = new List<(int, List<InputTick>)>();
        // The last record lap's way from the quick restart (its idle pad and inputs) and the best time before it (RecordLap).
        List<InputTick> _recordWay;
        float _recordBefore;
        /// <summary>--ladder-max K: the most record laps in a ladder (1: none).</summary>
        int _ladderMax;
        /// <summary>The Cash the plan's ladders earn that their stand-in laps don't (Ladders): added to every state the plan plays on.</summary>
        double _credit;

        /// <summary>What <see cref="Replan"/> planned.</summary>
        sealed class PlanResult
        {
            public bool Done;
            public string Target;
            /// <summary>The target's physics tick in the plan, and in the best (-1: the best doesn't reach it).</summary>
            public long Tick, Old, AfterBonus1;
            /// <summary>The plan's goals, and the whole list: the goals before the stretch, the plan, the goals after the target.</summary>
            public string[] Planned, Goals;
            public List<object> Log;
            /// <summary>With join: the plan's route under <see cref="Goals"/>, the best's way on after the target joined when it plays
            /// on, re-timed from the start (Retune from goal 0) when it reaches every goal. Null when the plan isn't done.</summary>
            public Route Run;
        }

        int PlanFarm(string[] args)
        {
            Open();
            int kept = GoalNumber(Solve.Option(args, "--from", ""), 0);
            // --target a,b: buy a first, then b (a big box that pays back, the statue, has to be saved for: step by step, the plan
            // toward b alone keeps buying the smaller boxes that pay back sooner and reaches it late).
            string[] targets = Solve.Option(args, "--target", "buy:buydj").Split(',');
            _ladderMax = Math.Max(1, Solve.Option(args, "--ladder-max", 12));
            foreach (string path in _seedFiles) _seeds.Add(ReadSeed(path));
            string outFile = Solve.Option<string>(args, "--out", null), routeFile = Solve.Option<string>(args, "--route", null);
            PlanResult p = Replan(_best, _runGoals.Goals, kept, targets, routeFile != null);
            Console.WriteLine(JsonSerializer.Serialize(p.Planned));
            if (outFile != null)
                File.WriteAllText(outFile, JsonSerializer.Serialize(new { done = p.Done, target = p.Target, tick = p.Tick, afterBonus1 = p.AfterBonus1, best = p.Old,
                    goals = p.Goals, planned = p.Planned, log = p.Log }, new JsonSerializerOptions { WriteIndented = true }));
            // The route follows the planned goals, not the run's current ones: fulltas.json takes the --out goals with it.
            if (routeFile != null && p.Run != null)
                File.WriteAllText(routeFile, JsonSerializer.Serialize(new { name = _name, goals = p.Goals, inputs = p.Run.Inputs.Select(t => t.ToJson()),
                    goalInputTicks = p.Run.GoalInput, goalPhysicsTicks = p.Run.GoalPhysics, total = p.Run.Progress == p.Goals.Length ? p.Run.GoalPhysics[p.Goals.Length - 1] : -1 }));
            return p.Done ? 0 : 3;
        }

        /// <summary>
        /// The engine farm planner's plan (docs/fulltas.md, "Engine farm planner") of <paramref name="all"/> (a goal list <paramref name="best"/>
        /// plays, not necessarily the run's) from goal <paramref name="kept"/> (the goals before it stay) until it buys the last of
        /// <paramref name="targets"/>; with <paramref name="join"/> the route too (<see cref="PlanResult.Run"/>). Callable from the goals
        /// part (a reroute after an edit changed the economy): the solve's best, goals and simulation factory are put back after it.
        /// </summary>
        PlanResult Replan(Route best, FullTasGoal[] all, int kept, string[] targets, bool join)
        {
            var saved = (_best, _runGoals, _labels, _goals, _create, _kept, _planBase, _borrow, _credit, _pieceState, _recordWay);
            _best = best;
            _credit = 0;
            _pieceState = null;
            _recordWay = null;
            _planBase = _runGoals;
            if (_ladderMax == 0) _ladderMax = 12;
            try
            {
                Track(all.ToList());
                return ReplanOn(all, kept, targets, join);
            }
            finally
            {
                (_best, _runGoals, _labels, _goals, _create, _kept, _planBase, _borrow, _credit, _pieceState, _recordWay) = saved;
                _eval.UseGoals(_runGoals);
            }
        }

        PlanResult ReplanOn(FullTasGoal[] all, int kept, string[] targets, bool join)
        {
            string targetLabel = targets[targets.Length - 1];
            int targetIndex = Array.FindIndex(all, kept, g => g.label == targetLabel);
            if (targetIndex < 0) throw new ArgumentException($"no goal {targetLabel} after goal {kept}");
            if (_best.Progress < kept) throw new InvalidOperationException($"the best reaches goal {_best.Progress}, not the planned stretch's start (goal {kept})");
            int course = all.Skip(kept).Take(targetIndex - kept).FirstOrDefault(g => g.kind == FullTasGoal.Start || g.kind == FullTasGoal.Finish)?.course ?? 0;
            if (course == 0) throw new ArgumentException("the stretch has no start:/finish: goal to lap");
            var byLabel = new Dictionary<string, FullTasGoal>();
            foreach (FullTasGoal g in all) byLabel.TryAdd(g.label, g);
            foreach (string t in targets) if (!byLabel.ContainsKey(t)) throw new ArgumentException("no goal " + t);
            FullTasGoal[] lap = { byLabel["start:" + course], byLabel["finish:" + course] };
            string[] buys = all.Skip(kept).Take(targetIndex - kept).Where(g => g.kind == FullTasGoal.Buy && g.label != targetLabel).Select(g => g.label).Distinct().ToArray();

            // Replay the best to the stretch's start, as the builder does (a goal's input tick can fall a tick or two early).
            int length = kept == 0 ? 0 : (int)_best.GoalInput[kept - 1];
            List<InputTick> route = null;
            SimSnapshot at = null;
            for (int extra = 0; extra <= 2 && at?.Goal.Next != kept; extra++)
            {
                route = _best.Inputs.Take(length + extra).ToList();
                if (route.Count < length + extra) Pad(route, length + extra);
                _eval.SetStart(_origin, 0);
                _eval.Goals.Limit = _goals;
                at = _eval.Advance(route);
            }
            if (at == null || at.Goal.Next != kept) throw new InvalidOperationException($"the best doesn't replay to goal {kept}");
            Harvest(at, route, kept, Math.Min(targetIndex + 1, _best.Progress));
            SeedLaps(at, route, kept, lap);
            _kept = all.Take(kept).ToArray();
            _borrow = (all.Select(g => g.label).ToArray(), kept, Math.Min(targetIndex, _best.Progress - 1));
            // Piece searches replay the plan so far on fresh simulations: they track the plan's goals, as the evaluator does.
            Func<Simulation> make = _create;
            _create = () => { Simulation sim = make(); sim.UseGoals(_runGoals.For(_runGoals.Goals)); return sim; };

            long bonus1 = Array.FindIndex(all, g => g.label == "reward:bonus1") is int b && b >= 0 && b < _best.Progress ? _best.GoalPhysics[b] : 0;
            long old = targetIndex < _best.Progress ? _best.GoalPhysics[targetIndex] : -1;
            // A lap as the best runs them back to back (finish to finish), for the lap reward's share of the income.
            List<long> laps = Enumerable.Range(kept + 2, Math.Max(0, Math.Min(targetIndex, _best.Progress) - kept - 2))
                .Where(k => all[k].label == "finish:" + course && all[k - 2].label == "finish:" + course)
                .Select(k => _best.GoalPhysics[k] - _best.GoalPhysics[k - 2]).DefaultIfEmpty(200).OrderBy(t => t).ToList();
            long lapTicks = laps[laps.Count / 2];
            Log($"plan goals {kept + 1}.. to {targetLabel} (goal {targetIndex + 1} of the run, the best buys it at tick {old}); Course {course}, boxes {string.Join(" ", buys)}; lap {lapTicks} ticks");
            var planned = new List<FullTasGoal>();
            var log = new List<object>();
            long now = kept == 0 ? 0 : _best.GoalPhysics[kept - 1];
            bool done = false;
            int phase = 0;
            // A box the piece search found no way to (a far global box) is dropped from the plan: searched again every step, its
            // two budgets were most of the plan's time and it never played.
            var unreachable = new HashSet<string>();
            int ladderClones = -1;
            while (!done && !TimeUp)
            {
                string aim = targets[phase];
                FullTasGoal target = byLabel[aim];
                int walk = _pieces.Where(p => p.Target == aim).Select(p => p.Inputs.Count).DefaultIfEmpty(0).Min();
                double cost = Cost(at, target);
                (string what, FullTasGoal[] goals, List<InputTick> inputs, SimSnapshot state, long tick, double eta, double credit, double formula)? pick = null;
                var options = new List<(string, FullTasGoal[])> { (aim, new[] { target }), ("lap", lap), ("record lap", lap) };
                // Hold-back ladders (HoldBackLadders), each time the course's clone count changed: a ladder is worth more with more clones.
                Read(at);
                var (cn, reserve) = Clones(_eval, course);
                if (_ladderMax > 1 && cn > 0 && reserve && cn != ladderClones)
                {
                    ladderClones = cn;
                    options.Add(("hold-back ladder", lap));
                }
                string[] open = buys.Where(l => l != aim && !unreachable.Contains(l)).ToArray();
                options.AddRange(open.Select(l => (l, new[] { byLabel[l] })));
                // --group-buys: two boxes on one walk (x, then y from where x left the player) against each alone: the step
                // from x to y is often far shorter than a lap and a second walk, so "x and y in Z ticks" can beat "x in B".
                if (_groupBuys)
                    foreach (string x in open)
                        foreach (string y in open)
                            if (x != y && !unreachable.Contains(x + " then " + y)) options.Add((x + " then " + y, new[] { byLabel[x], byLabel[y] }));
                bool aimPayable = false, aimPlayed = false;
                foreach (var (what, goals) in options)
                {
                    string[] boxes = goals.Where(g => g.kind == FullTasGoal.Buy).SelectMany(g => g.boxes).ToArray();
                    bool? affordable = boxes.Length > 0 ? Affordable(at, boxes) : true;
                    // The target box can be off only because its course is unloaded here (Course 3's buydj seen from Course 2): the
                    // Cash decides. Skipping it laped Course 2 1724 times with a hundred times buydj's price held.
                    if (affordable == null && what == aim) affordable = Cash(at) >= cost;
                    if (affordable == null) continue;
                    // A buy-max touch still buying goes on without another touch; a search for a way to touch it again found none.
                    if (what != aim && goals.Any(g => g.kind == FullTasGoal.Buy && Buying(at, g))) continue;
                    if (what == "hold-back ladder")
                    {
                        var held = HoldBackLadders(route, planned, lap, course);
                        double? rate = null;
                        double f = double.NaN;
                        foreach (var h in held)
                        {
                            // One rate for them all: the same clones on the same path after each (the ladder's fastest lap).
                            rate ??= Rate(h.state, h.inputs, course, lapTicks, out f);
                            if (rate == null || rate <= 0) break;
                            double e = h.tick + Math.Max(0.0, cost - Cash(h.state)) / rate.Value + walk;
                            if (pick == null || e < pick.Value.eta) pick = (h.what, h.goals, h.inputs, h.state, h.tick, e, 0.0, f);
                        }
                        if (held.Count > 0)
                            Log($"hold-back ladders at tick {now}: " + string.Join(", ", held.Select(h => $"{h.what} to tick {h.tick}")));
                        continue;
                    }
                    var played = what == "lap" ? Lap(at, route, planned, lap) : what == "record lap" ? RecordLap(at, route, planned, lap, course)
                        : Play(at, route, planned, goals, affordable == true);
                    if (what == aim) aimPlayed = played != null;
                    if (played == null)
                    {
                        if (affordable == true && what != "lap" && what != "record lap" && what != aim && unreachable.Add(what)) Log($"{what}: no way found from tick {now}; dropped from the plan");
                        continue;
                    }
                    var (inputs, state, tick) = played.Value;
                    double eta = tick, formula = double.NaN;
                    if (what != aim)
                    {
                        // Laps fill the rest (a plan never stands at a box): the income is the lap's reward plus the clones'. One lap's
                        // clone payouts are too few to average out their rolls, and the best of several noisy guesses bought
                        // every box, so the clones' income is measured standing still after a quick restart, over IdleTicks.
                        double? rate = Rate(state, inputs, course, lapTicks, out formula);
                        if (rate == null || rate <= 0) continue;
                        eta = tick + Math.Max(0.0, cost - Cash(state)) / rate.Value + walk;
                        // A record lap's break can go on as a ladder: K - 1 more breaks, each lap a tick faster, the same income after.
                        if (what == "record lap")
                        {
                            var ladders = Ladders(inputs, state, tick, planned, lap, course);
                            var etas = ladders.Select(l => l.tick + Math.Max(0.0, cost - Cash(l.state) - l.credit) / rate.Value + walk).ToList();
                            for (int k = 0; k < ladders.Count; k++)
                            {
                                var l = ladders[k];
                                if (pick == null || etas[k] < pick.Value.eta) pick = (l.what, l.goals, l.inputs, l.state, l.tick, etas[k], l.credit, formula);
                            }
                            if (ladders.Count > 0)
                                Log($"ladders at tick {now}: record lap eta {eta:F0}; " + string.Join(", ", ladders.Select((l, k) =>
                                    $"{l.what} +{l.tick - tick} ticks +{l.credit:G3} Cash eta {etas[k]:F0}")));
                        }
                    }
                    if (pick == null || eta < pick.Value.eta) pick = (what, goals, inputs, state, tick, eta, 0.0, formula);
                }
                // More laps only add Cash: once the target is paid for and still no way reaches it, the plan is stuck here.
                if (aimPayable && !aimPlayed) { Log($"stuck at tick {now}: {aim} is paid for but no way reaches it from {Where(at.WithGoal(Fresh(_goals, 0))).at} ({_whyNot})"); break; }
                if (pick == null) { Log($"stuck at tick {now}: no option plays from here"); break; }
                var p = pick.Value;
                planned.AddRange(p.goals);
                route = p.inputs;
                at = p.state;
                now = p.tick;
                if (p.credit > 0)
                {
                    // The ladder's breaks after its first are the model's (its laps here are the fastest one again): their Cash goes
                    // into the plan's state, and into the states piece searches replay to (as the buy table's price does).
                    _credit += p.credit;
                    at = Credited(at, p.credit);
                    _pieceState = s => Credited(s, _credit);
                }
                if (p.what == aim) done = ++phase == targets.Length;
                double cash = Cash(at);
                string levels = string.Join(" ", buys.Append(targetLabel).Select(l => l.Substring(4) + " " + Level(at, byLabel[l])));
                Log($"tick {now,6} ({now - bonus1} after reward:bonus1)  {p.what,-22} cash {cash:G4}  eta {p.eta:F0}  {levels}"
                    + (double.IsNaN(p.formula) ? "" : $"  (income by formula {p.formula:G4} a tick)"));
                log.Add(new { tick = now, step = p.what, cash, eta = Math.Round(p.eta), levels, credit = p.credit });
            }
            string[] tokens = planned.Select(g => g.label).ToArray();
            Log(done ? $"{targetLabel} at tick {now}, {now - bonus1} after reward:bonus1 (the best: {old}); {tokens.Length} goals replace {targetIndex + 1 - kept}"
                : $"stopped before {targetLabel} ({tokens.Length} goals planned)");
            var result = new PlanResult { Done = done, Target = targetLabel, Tick = now, Old = old, AfterBonus1 = now - bonus1, Planned = tokens, Log = log,
                Goals = all.Take(kept).Select(g => g.label).Concat(tokens).Concat(done ? all.Skip(targetIndex + 1).Select(g => g.label) : Array.Empty<string>()).ToArray() };
            // The plan's own route, as the solver saves a run's best (fulltas/<run>.json): the goals alone replay it only as
            // closely as other pieces allow, and a buy planned just affordable on arrival can then fall short.
            if (join && done && _credit > 0)
                Log($"route: not made, the plan's ladders ({_credit:G4} Cash of breaks) are played as the fastest lap; the goals part's ladder edit slows them");
            else if (join && done)
            {
                var full = _kept.Concat(planned).Concat(all.Skip(targetIndex + 1)).ToList();
                Track(full);
                SimSnapshot origin = _origin.WithGoal(Fresh(full.Count, 0));
                RunResult Play(List<InputTick> inputs)
                {
                    _eval.SetStart(origin, 0);
                    _eval.Goals.Limit = full.Count;
                    return _eval.Run(inputs, null, null, 1000000);
                }
                RunResult r = Play(route);
                List<InputTick> written = route;
                // The old route after the target, from its first Quick Restart on: kept when it plays on from the plan's end, as a
                // reset segment's old route is (the same Quick Restart after the plan's target, after the old inputs between the
                // two or 0-24 idle ticks). A plan whose end leaves a course running Quick Restarts to that course's reset point
                // instead of the Checkpoint the old route went to, and doesn't.
                int oldCut = (int)Math.Min(_best.GoalInput[targetIndex], _best.Inputs.Count);
                int oldRestart = targetIndex + 1 < all.Length ? _best.Inputs.FindIndex(oldCut, k => k.reset) : -1;
                int reached = kept + planned.Count;
                if (oldRestart >= 0 && r.Progress >= reached)
                {
                    int cut = (int)r.GoalInputTicks[reached - 1];
                    List<InputTick> head = route.Take(cut).ToList();
                    Pad(head, cut);
                    List<InputTick> oldRunout = _best.Inputs.GetRange(oldCut, oldRestart - oldCut);
                    var runouts = new List<List<InputTick>> { oldRunout };
                    for (int wait = 0; wait <= 24; wait++) runouts.Add(Enumerable.Repeat(new InputTick { held = head.Count > 0 && head[head.Count - 1].held }, wait).ToList());
                    List<InputTick> tail = _best.Inputs.Skip(oldRestart).ToList();
                    (List<InputTick> inputs, RunResult result, int wait) furthest = (null, null, 0);
                    foreach (List<InputTick> runout in runouts)
                    {
                        if (runout.Count == 0 && head.Count > 0 && head[head.Count - 1].reset) continue;
                        List<InputTick> joined = head.Concat(runout).Concat(tail).ToList();
                        RunResult j = Play(joined);
                        if (furthest.result == null || j.Progress > furthest.result.Progress) furthest = (joined, j, runout.Count);
                        if (j.Progress < full.Count) continue;
                        written = joined;
                        r = j;
                        Log($"route: the old route after {targetLabel} plays on from the plan's end ({runout.Count} ticks before its Quick Restart)");
                        break;
                    }
                    if (written == route)
                    {
                        _eval.SetStart(origin, 0);
                        _eval.Goals.Limit = full.Count;
                        SimSnapshot end = _eval.Advance(head);
                        courseScript running = null;
                        if (end != null) { _eval.Restore(end); running = _eval.TimingCourse(); }
                        string stops = "";
                        // The furthest join is kept when it gets past the plan's end: a later stretch that breaks (a buy walk that
                        // now takes a box it can pay for) is re-planned from there, on top of this one.
                        if (furthest.result != null && furthest.result.Progress > reached)
                        {
                            written = furthest.inputs;
                            r = furthest.result;
                            stops = $"; joined after {furthest.wait} ticks it stops at goal {r.Progress + 1} ({(r.Progress < full.Count ? full[r.Progress].label : "")}: {r.EndReason})";
                        }
                        Log($"route: the old route after {targetLabel} doesn't play on to the end from the plan's end"
                            + (running != null ? $" (Course {running.courseNumber} is still running there, so its Quick Restart goes to the course's reset point, not the old route's Checkpoint)" : "")
                            + stops + "; re-plan the stretch it stops in, or solve the goals after it (the studio's Reach missing goals)");
                    }
                }
                var run = new Route { Inputs = written, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks, GoalHeld = r.GoalHeldFrames, EndReason = r.EndReason };
                // Its waits re-timed from the start: the plan's walks and the old way on were timed to another economy.
                if (run.Progress == full.Count && Retune(_eval, origin, run, written, 0, full.Count) is Route t && t.Progress == full.Count && t.Live < run.Live)
                {
                    Log($"route: re-timed from the start, {run.LiveSeconds:0.00}s -> {t.LiveSeconds:0.00}s");
                    run = t;
                }
                result.Run = run;
                Log($"route: reaches {run.Progress} of {full.Count} goals" + (run.Progress == full.Count ? $" in {run.LiveSeconds:0.00}s live ({run.Last} ticks)" : ""));
            }
            return result;
        }

        /// <summary>
        /// Files the saved course routes (--seed files that run this course from standing at its start) as lap pieces at the
        /// spot a quick restart from here lands: the gate's part as the lap's start piece, the rest as its finish piece, each
        /// checked by playing it there. A lap the route polished in the run is often slower than the course's own record
        /// (Course 1's farm route finishes 12 ticks sooner after the same quick restart), and the planner laps from the library.
        /// </summary>
        void SeedLaps(SimSnapshot at, List<InputTick> route, int reached, FullTasGoal[] lap)
        {
            var runs = _seeds.Where(s => s.Standing && s.Goals.Contains(lap[0].label) && s.Goals.Contains(lap[1].label)).ToList();
            if (runs.Count == 0) return;
            var restart = Restarts(at, route, reached).FirstOrDefault();
            if (restart.state == null) { Log("lap seeds: no quick restart from the stretch's start"); return; }
            List<InputTick> before = route.Concat(restart.inputs).ToList();
            int filed = 0;
            foreach (Seed s in runs)
            {
                var start = new Target { Goal = reached, Extra = new[] { lap[0] }, Key = lap[0].label };
                Step gate = Reaches(restart.state, before.Count, s.Inputs, start);
                if (gate == null || gate.Next == null) { Log($"lap seeds: {s.Name} doesn't reach {lap[0].label} from the quick restart's spot at {Where(restart.state.WithGoal(Fresh(_goals, 0))).at} ({_whyNot})"); continue; }
                var finish = new Target { Goal = reached, Extra = new[] { lap[1] }, Key = lap[1].label };
                List<InputTick> afterGate = before.Concat(gate.Inputs).ToList();
                Step end = Reaches(gate.Next, afterGate.Count, s.Inputs.Skip(gate.Inputs.Count).ToList(), finish);
                if (end == null) { Log($"lap seeds: {s.Name} crosses {lap[0].label} but doesn't reach {lap[1].label} after it ({_whyNot})"); continue; }
                Keep(gate, restart.state, before, start);
                Keep(end, gate.Next, afterGate, finish);
                filed++;
                Log($"lap seeds: {s.Name} laps from the quick restart's spot to its finish in {gate.Inputs.Count + end.Inputs.Count} inputs (finish at physics tick {end.Physics})");
            }
            Log($"lap seeds: {filed} of {runs.Count} saved course routes filed as lap pieces");
        }

        /// <summary>
        /// Files the best's own steps from goal from on (each goal's inputs from where the goal before left the player) as library
        /// pieces: the builder files only the pieces it builds, and a polished route passes spots none starts from (after the
        /// statue a quick restart lands at its checkpoint, and the lap from there crosses Course 3's gate where no finish piece began).
        /// Every goal is filed, area goals and rewards too: the search windows try them wherever another run stands where this one did.
        /// </summary>
        void Harvest(SimSnapshot at, List<InputTick> route, int from, int until)
        {
            int filed = _pieces.Count, length = route.Count;
            _holdPieces = true;
            try
            {
                for (int k = from; k < until; k++)
                {
                    int end = (int)Math.Min(_best.GoalInput[k], _best.Inputs.Count);
                    if (end > length)
                        Keep(new Step { Inputs = _best.Inputs.GetRange(length, Math.Min(end + 2, _best.Inputs.Count) - length) }, at, _best.Inputs.GetRange(0, length),
                            new Target { Goal = k, Key = _labels[k] });
                    // The goal after the last is never played to (a run's last goal ends it: no state after it).
                    if (k == until - 1) break;
                    SimSnapshot next = null;
                    int used = end;
                    for (int extra = 0; extra <= 2 && next?.Goal.Next != k + 1 && end + extra <= _best.Inputs.Count; extra++)
                    {
                        used = end + extra;
                        next = used == length ? at : AdvanceFrom(at, length, _best.Inputs.GetRange(length, used - length));
                    }
                    if (next == null || next.Goal.Next != k + 1) { Log($"harvest stops at goal {k + 1}: the best doesn't replay to it"); break; }
                    at = next;
                    length = used;
                }
            }
            finally
            {
                _holdPieces = false;
                SavePieces();
            }
            Log($"harvest: {_pieces.Count - filed} pieces from the best's goals {from + 1}-{until}");
        }

        /// <summary>
        /// --harvest FILE: a saved route (a fulltas/&lt;run&gt;.json: goals and inputs, for goals of this run's labels) played under
        /// its own goals from the run's start, its steps filed as library pieces (<see cref="Harvest"/>). A build or reach after a
        /// plan changed the goals then plays the old route's laps and box walks where the player stands as it did, instead of
        /// searching each goal again.
        /// </summary>
        void HarvestRoute(string file)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            string[] labels = doc.RootElement.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToArray();
            List<InputTick> inputs = Solve.ReadInputs(doc.RootElement);
            var byLabel = new Dictionary<string, FullTasGoal>();
            foreach (FullTasGoal g in _runGoals.Goals) byLabel.TryAdd(g.label, g);
            string unknown = labels.FirstOrDefault(l => !byLabel.ContainsKey(l));
            if (unknown != null) { Log($"harvest {Path.GetFileName(file)}: goal {unknown} isn't one of the run's; skipped"); return; }
            FullTasTracker goals = _runGoals;
            string[] runLabels = _labels;
            int count = _goals;
            Route best = _best;
            try
            {
                _runGoals = goals.For(labels.Select(l => byLabel[l]).ToArray());
                _labels = labels;
                _goals = labels.Length;
                _eval.UseGoals(_runGoals);
                SimSnapshot start = _origin.WithGoal(Fresh(_goals, 0));
                _eval.SetStart(start, 0);
                _eval.Goals.Limit = _goals;
                RunResult r = _eval.Run(inputs, null, null, 1000000);
                _best = new Route { Inputs = inputs, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks, GoalHeld = r.GoalHeldFrames };
                Log($"harvest {Path.GetFileName(file)}: plays {r.Progress} of its {labels.Length} goals");
                Harvest(start, new List<InputTick>(), 0, r.Progress);
            }
            finally
            {
                _runGoals = goals;
                _labels = runLabels;
                _goals = count;
                _best = best;
                _eval.UseGoals(_runGoals);
            }
        }

        /// <summary>
        /// Files every run's saved best (fulltas/&lt;run&gt;.json with its trace fulltas-&lt;run&gt;.json beside) in the library, each
        /// played from its own trace's start under the trace's goals (<see cref="Harvest"/>), so a run reuses another's way to a goal
        /// it shares (the same label: an area goal's label is its box) wherever it stands where the other did, with the same
        /// unlocks, without a "seeds" entry. A best is harvested again only when it, its trace's start or its trace's goals change
        /// (archive/fulltas-harvested.json).
        /// </summary>
        void HarvestRuns(string traceDir)
        {
            string bests = Path.Combine(traceDir, "fulltas");
            if (_fresh || !Directory.Exists(bests)) return;
            string indexFile = Path.Combine(traceDir, "archive", "fulltas-harvested.json");
            var done = File.Exists(indexFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(indexFile)) : new Dictionary<string, string>();
            bool changed = false;
            foreach (string runFile in Directory.GetFiles(bests, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                string run = Path.GetFileNameWithoutExtension(runFile);
                string trace = Path.Combine(traceDir, "fulltas-" + run + ".json");
                // Only a run's own best has a trace of that name (not its .closest, .faster or .before-* copies).
                if (!File.Exists(trace) || !File.Exists(Path.ChangeExtension(trace, null) + ".world.json")) continue;
                string text = File.ReadAllText(runFile);
                JsonElement header = Solve.ReadHeader(trace);
                if (!header.TryGetProperty("fulltas", out JsonElement fulltas)) continue;
                string[] labels = fulltas.GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray();
                string key = TextHash(text).ToString("x16") + "-" + StartKey(header).ToString("x16") + "-" + TextHash(string.Join("\n", labels)).ToString("x16");
                if (done.TryGetValue(run, out string was) && was == key) continue;
                HarvestRun(run, text, trace, header, labels);
                done[run] = key;
                changed = true;
            }
            if (!changed) return;
            Directory.CreateDirectory(Path.GetDirectoryName(indexFile));
            Solve.WriteShared(indexFile, JsonSerializer.Serialize(done));
        }

        static ulong TextHash(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text)) hash = Feed(hash, b, 1);
            return hash;
        }

        /// <summary>
        /// One run's saved best (its goals still the trace's leading ones) played in a simulation of its own trace and harvested. The
        /// game's statics are per thread and a world load sets its thread's, so the harvest loads and plays that world on a thread of
        /// its own: loaded here, it would leave the solve's own simulation running on the other world's statics.
        /// </summary>
        void HarvestRun(string run, string text, string trace, JsonElement header, string[] labels)
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            List<InputTick> inputs = Solve.ReadInputs(root);
            string[] solved = root.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToArray();
            long[] goalInput = root.TryGetProperty("goalInputTicks", out JsonElement ticks) ? ticks.EnumerateArray().Select(t => t.GetInt64()).ToArray() : new long[0];
            int keep = CurrentInputs(solved, goalInput, labels, inputs.Count);
            if (keep < inputs.Count) inputs.RemoveRange(keep, inputs.Count - keep);
            if (inputs.Count == 0) return;
            Simulation eval = _eval;
            FullTasTracker goals = _runGoals;
            string[] runLabels = _labels;
            int count = _goals;
            Route best = _best;
            try
            {
                Solve.OnThread(() =>
                {
                    Simulation sim = Solve.Creator(trace, header)();
                    sim.StrictBuys = true;
                    _eval = sim;
                    _runGoals = sim.Goals;
                    _labels = labels;
                    _goals = labels.Length;
                    SimSnapshot start = sim.Start;
                    sim.SetStart(start, 0);
                    sim.Goals.Limit = _goals;
                    RunResult r = sim.Run(inputs, null, null, 1000000);
                    _best = new Route { Inputs = inputs, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks, GoalHeld = r.GoalHeldFrames };
                    Log($"harvest run {run}: its best plays {r.Progress} of its {labels.Length} goals");
                    Harvest(start, new List<InputTick>(), 0, r.Progress);
                });
            }
            finally
            {
                _eval = eval;
                _runGoals = goals;
                _labels = runLabels;
                _goals = count;
                _best = best;
            }
        }

        /// <summary>A goal given as its 1-based number or its label (first occurrence); fallback when empty.</summary>
        int GoalNumber(string arg, int fallback)
        {
            if (arg == "") return fallback;
            if (int.TryParse(arg, out int n)) return n;
            int i = Array.IndexOf(_labels, arg);
            if (i < 0) throw new ArgumentException("no goal " + arg);
            return i + 1;
        }

        /// <summary>
        /// Plays goals one after another from at (after route and the plan so far), each with the fastest library piece (or a piece
        /// search when <paramref name="search"/> and none plays). A goal with another after it (a lap's gate) takes the piece after
        /// which that next goal comes soonest: the fastest gate piece alone can cross the gate where only a slow finish piece plays
        /// (Course 1's laps took 248 ticks from the box for the best's 107). Null when one can't be reached.
        /// </summary>
        (List<InputTick> inputs, SimSnapshot state, long tick)? Play(SimSnapshot at, List<InputTick> route, List<FullTasGoal> planned, FullTasGoal[] goals, bool search)
        {
            var inputs = route.ToList();
            var list = _kept.Concat(planned).ToList();
            long tick = -1;
            for (int k = 0; k < goals.Length; k++)
            {
                FullTasGoal g = goals[k];
                list.Add(g);
                Track(list);
                int i = list.Count - 1;
                at = at.WithGoal(Fresh(list.Count, i));
                var target = new Target { Goal = i, Key = g.label };
                Step step = k + 1 < goals.Length ? Leading(at, inputs, list, goals[k + 1], target) : BestKnown(at, inputs, i, target);
                if (step == null && search)
                {
                    var borrowed = BorrowedInputs(at, target);
                    step = Borrowed(at, inputs, target, borrowed);
                    if (step == null) step = SolvePiece(at, inputs, i, target, _pieceMinutes, BuildStall, borrowed);
                    if (step != null) Keep(step, at, inputs, target);
                }
                if (step == null || inputs.Count > 0 && inputs[inputs.Count - 1].reset && step.Inputs[0].reset)
                {
                    _whyNot = $"{g.label} from {Where(at).at}" + (step == null ? "" : ": restarts twice");
                    return null;
                }
                inputs.AddRange(step.Inputs);
                at = step.Next;
                tick = step.Physics;
            }
            return (inputs, at, tick);
        }

        const int BorrowBack = 12;

        /// <summary>
        /// Ways to target to replay before a piece search: the library's pieces for it from any distance (nearest first; the library
        /// plays only those within <see cref="PieceReach"/>, and each box of a row leaves the player elsewhere), then the best's own
        /// inputs to the same goal in the stretch the plan replaces, from each of its quick restarts and goal cuts up to
        /// <see cref="BorrowBack"/> goals before it (a quick restart lands at a checkpoint wherever it's pressed, so the best's way
        /// on from one plays from any spot: the climb from Course 2 to buydj). Also the search's seeds when none plays.
        /// </summary>
        List<List<InputTick>> BorrowedInputs(SimSnapshot at, Target target)
        {
            var (here, unlocks) = Where(at);
            var tries = _pieces.Where(p => p.Target == target.Key && p.Unlocks == unlocks).OrderBy(p => Distance(p, here))
                .Take(MaxPieceTries).Select(p => p.Inputs).ToList();
            var (labels, from, until) = _borrow;
            for (int j = from; j <= until && labels != null; j++)
            {
                if (labels[j] != target.Key) continue;
                int end = (int)Math.Min(_best.GoalInput[j] + 2, _best.Inputs.Count);
                var cuts = new SortedSet<int>();
                for (int k = Math.Max(0, j - BorrowBack); k < j; k++) cuts.Add((int)Math.Min(_best.GoalInput[k], end - 1));
                for (int c = cuts.Count > 0 ? cuts.Min : end; c < end; c++) if (_best.Inputs[c].reset) cuts.Add(c);
                foreach (int c in cuts) if (c < end) tries.Add(_best.Inputs.GetRange(c, end - c));
            }
            return tries;
        }

        /// <summary>The fastest of tries that reaches target from at (after route); null when none does.</summary>
        Step Borrowed(SimSnapshot at, List<InputTick> route, Target target, List<List<InputTick>> tries)
        {
            Step best = null;
            foreach (List<InputTick> t in tries)
            {
                if (t.Count == 0 || route.Count > 0 && route[route.Count - 1].reset && t[0].reset) continue;
                Step s = Reaches(at, route.Count, t, target);
                if (s != null && (best == null || s.Physics < best.Physics)) best = s;
            }
            return best;
        }

        /// <summary>
        /// Of the library's pieces to list's last goal from at, the one after which next is reached soonest by the library (on a tie
        /// the one that reaches its own goal first); the fastest to its own goal when next plays after none. Leaves the plan's goals
        /// tracking list.
        /// </summary>
        Step Leading(SimSnapshot at, List<InputTick> inputs, List<FullTasGoal> list, FullTasGoal next, Target target)
        {
            List<Step> steps = KnownSteps(at, inputs, target.Goal, target);
            if (steps.Count < 2) return steps.FirstOrDefault();
            var ahead = list.Append(next).ToList();
            Track(ahead);
            var then = new Target { Goal = list.Count, Key = next.label };
            Step pick = null;
            long pickAhead = long.MaxValue;
            foreach (Step s in steps)
            {
                Step after = BestKnown(s.Next.WithGoal(Fresh(ahead.Count, then.Goal)), inputs.Concat(s.Inputs).ToList(), then.Goal, then);
                long a = after?.Physics ?? long.MaxValue;
                if (pick == null || a < pickAhead || a == pickAhead && s.Physics < pick.Physics) (pick, pickAhead) = (s, a);
            }
            Track(list);
            return pick;
        }

        /// <summary>
        /// A lap from at: library pieces as they are, else after a quick restart (the fastest start piece from a box can cross the
        /// gate where no finish piece begins), and a piece search only when neither plays.
        /// </summary>
        (List<InputTick> inputs, SimSnapshot state, long tick)? Lap(SimSnapshot at, List<InputTick> route, List<FullTasGoal> planned, FullTasGoal[] lap)
        {
            var played = Play(at, route, planned, lap, false);
            if (played != null) return played;
            Log($"lap from {Where(at.WithGoal(Fresh(_goals, 0))).at}: no library pieces as it is ({_whyNot})");
            var list = _kept.Concat(planned).Append(lap[0]).ToList();
            Track(list);
            SimSnapshot here = at.WithGoal(Fresh(list.Count, list.Count - 1));
            foreach (var (pre, state) in Restarts(here, route, list.Count - 1).Take(1))
            {
                played = Play(state, route.Concat(pre).ToList(), planned, lap, false);
                if (played != null) return played;
                Log($"lap after a restart (pad {pre.Count - 1}) at {Where(state.WithGoal(Fresh(_goals, 0))).at}: none either ({_whyNot})");
            }
            return Play(at, route, planned, lap, true);
        }

        static readonly System.Reflection.FieldInfo BestPathTime = typeof(courseScript).GetField("bestPathTime",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// A lap that sets the course's best time, which is the clones' path: every clone pays once per path, so a faster best pays
        /// the whole rest of the stretch (Course 2's route crossed its laps in 2.44-2.70 s against the 2.20 s record). The gate is
        /// crossed with the library's piece, a quick restart puts the player at the course's reset point, and each saved course
        /// route (--seed) that runs this course from standing there is played to the finish; the one that leaves the lowest best
        /// time wins (then the soonest). Slower than a farm lap; the planner's estimate weighs the clones' income after it.
        /// </summary>
        (List<InputTick> inputs, SimSnapshot state, long tick)? RecordLap(SimSnapshot at, List<InputTick> route, List<FullTasGoal> planned, FullTasGoal[] lap, int course)
        {
            var runs = _seeds.Where(s => s.Standing && s.Goals.Contains(lap[0].label) && s.Goals.Contains(lap[1].label)).ToList();
            if (runs.Count == 0) return null;
            var gate = Play(at, route, planned, new[] { lap[0] }, false);
            if (gate == null) return null;
            var (inputs, state, _) = gate.Value;
            var list = _kept.Concat(planned).Concat(lap).ToList();
            Track(list);
            int i = list.Count - 1;
            SimSnapshot here = state.WithGoal(Fresh(list.Count, i));
            var target = new Target { Goal = i, Key = lap[1].label };
            float before = BestPath(here, course);
            (List<InputTick> inputs, SimSnapshot state, long tick, float best, List<InputTick> way)? pick = null;
            var tried = new List<string>();
            // A saved course route starts standing still: the game's run waits for three grounded ticks and its start alignment
            // before the first input. Idle ticks after the quick restart stand in for that wait; each route's pad is the first that
            // plays it to the finish, then reused (the restart lands the same).
            var starts = new List<(string how, List<InputTick> pre, SimSnapshot state)>();
            foreach (var (pre, restarted) in Restarts(here, inputs, i).Take(1)) starts.Add(("", pre, restarted));
            foreach (var (how, pre, restarted) in starts)
                foreach (Seed s in runs)
                {
                    Step end = null;
                    string key = s.Name + how;
                    bool cached = _recordPads.TryGetValue(key, out int found);
                    if (cached && found < 0) { tried.Add($"{key} misses {lap[1].label}"); continue; }
                    foreach (int idle in cached ? new[] { found } : Enumerable.Range(0, MaxRecordPad + 1))
                    {
                        end = Reaches(restarted, inputs.Count + pre.Count, Enumerable.Repeat(new InputTick(), idle).Concat(s.Inputs).ToList(), target);
                        if (end != null) { found = idle; break; }
                    }
                    if (!cached) _recordPads[key] = end == null ? -1 : found;
                    if (end == null)
                    {
                        Read(restarted);
                        string pose = $"restart at {_eval.PlayerWorld.x:0.00},{_eval.PlayerWorld.y:0.00} facing {(_eval.Player.facingRight ? "right" : "left")}, grounded {_eval.Player.onGround}";
                        tried.Add($"{key} misses {lap[1].label} ({Replayed(restarted, inputs.Count + pre.Count, s.Inputs, target)}; {pose})");
                        continue;
                    }
                    float best = BestPath(end.Next, course);
                    tried.Add($"{key} {best:0.00}s after {found} idle ticks");
                    if (pick == null || best < pick.Value.best || best == pick.Value.best && end.Physics < pick.Value.tick)
                        pick = (inputs.Concat(pre).Concat(end.Inputs).ToList(), end.Next, end.Physics, best, end.Inputs);
                }
            // A saved route that misses from here (Course 2's 2.20 s lap takes its spikes on their last grace tick, and a restart's
            // start differs from its recording in the last hundredths) is searched once per course from the restart: seeded with
            // those routes, the search keeps the way that finishes soonest from standing there, which is the gate crossed at
            // speed. Its way is then tried with the saved routes at every later record lap.
            if (!_recordSearched.Contains(course) && starts.Count > 0 && tried.Any(t => t.Contains(" misses ")))
            {
                _recordSearched.Add(course);
                var (how, pre, restarted) = starts[0];
                var seedsFor = runs.Select(s => s.Inputs).ToList();
                Step found = SolvePiece(restarted, inputs.Concat(pre).ToList(), i, target, _pieceMinutes * 3, BuildStall, seedsFor);
                if (found != null)
                {
                    float best = BestPath(found.Next, course);
                    Log($"record lap: searched from the restart, a lap sets the best time to {best:0.00}s (finish at tick {found.Physics})");
                    _recordWays.Add((course, found.Inputs));
                    if (pick == null || best < pick.Value.best || best == pick.Value.best && found.Physics < pick.Value.tick)
                        pick = (inputs.Concat(pre).Concat(found.Inputs).ToList(), found.Next, found.Physics, best, found.Inputs);
                }
            }
            else
                foreach (var (_, pre, restarted) in starts.Take(1))
                    foreach (var (c, way) in _recordWays)
                    {
                        if (c != course) continue;
                        Step end = Reaches(restarted, inputs.Count + pre.Count, way, target);
                        if (end == null) continue;
                        float best = BestPath(end.Next, course);
                        if (pick == null || best < pick.Value.best || best == pick.Value.best && end.Physics < pick.Value.tick)
                            pick = (inputs.Concat(pre).Concat(end.Inputs).ToList(), end.Next, end.Physics, best, end.Inputs);
                    }
            if (!_recordTried) { _recordTried = true; Log("record lap seeds: " + string.Join("; ", tried)); }
            if (pick == null || pick.Value.best >= before) return null;
            Log($"record lap: best time {before:0.00}s -> {pick.Value.best:0.00}s, finish at tick {pick.Value.tick}");
            (_recordWay, _recordBefore) = (pick.Value.way, before);
            return (pick.Value.inputs, pick.Value.state, pick.Value.tick);
        }

        /// <summary>
        /// Hold-back ladders at the plan's end (after <paramref name="route"/>): the goals part's ladder edit (<see cref="LadderEdits"/>)
        /// at this one spot, K = 2..8 laps from the quick restart, the fastest lap slowed to T+K-1, ..., T, the plan's earlier laps
        /// that set the course's best time held back to T+K (<see cref="SlowOne"/>, the waits after each re-timed) so the first
        /// breaks it. Every break is played, none credited: each is the whole route (its earlier laps changed), the plan's goals
        /// with 2K lap goals after them, where it leaves the player and its last finish.
        /// </summary>
        List<(string what, FullTasGoal[] goals, List<InputTick> inputs, SimSnapshot state, long tick)> HoldBackLadders(List<InputTick> route,
            List<FullTasGoal> planned, FullTasGoal[] lap, int course)
        {
            var found = new List<(string, FullTasGoal[], List<InputTick>, SimSnapshot, long)>();
            var list = _kept.Concat(planned).ToList();
            Track(list);
            _eval.SetStart(_origin.WithGoal(Fresh(list.Count, 0)), 0);
            _eval.Goals.Limit = list.Count;
            RunResult rr = _eval.Run(route, null, null, 1000000);
            if (rr.Progress < list.Count) return found;
            int a = (int)Math.Min(rr.GoalInputTicks[list.Count - 1], route.Count);
            var r = new Route { Inputs = route.Take(a).ToList(), Progress = rr.Progress, GoalInput = rr.GoalInputTicks, GoalPhysics = rr.GoalPhysicsTicks, GoalHeld = rr.GoalHeldFrames };
            var spot = new LadderSpot { Course = course, A = a, First = list.Count, Restart = new InputTick { reset = true } };
            Route best = _best;
            List<GoalEdit> edits;
            // LadderEdits reads the best for its log text; here the best is the plan so far.
            _best = r;
            try { edits = LadderEdits(r, Laps(r), course, 0, new List<LadderSpot> { spot }, false, true); }
            finally { _best = best; }
            if (spot.Why != null) Log($"hold-back ladders: {spot.Why}");
            foreach (GoalEdit e in edits)
            {
                int k = (e.Labels.Length - list.Count) / 2;
                var goals = list.Concat(Enumerable.Range(0, k).SelectMany(_ => lap)).ToList();
                Track(goals);
                _eval.SetStart(_origin.WithGoal(Fresh(goals.Count, 0)), 0);
                _eval.Goals.Limit = goals.Count;
                SimSnapshot end = _eval.Advance(e.Inputs);
                if (end == null || end.Goal.Next != goals.Count) continue;
                found.Add(($"hold-back ladder {k}", goals.Skip(list.Count).ToArray(), e.Inputs, end, end.Goal.PhysicsTicks[goals.Count - 1]));
            }
            Track(list);
            return found;
        }

        float BestPath(SimSnapshot s, int course) { Read(s); return (float)BestPathTime.GetValue(_eval.Courses.First(c => c.courseNumber == course)); }

        /// <summary>The plan's goals become the run's: the builder's steps track them (strict buys: only the next one's box).</summary>
        void Track(List<FullTasGoal> goals)
        {
            _runGoals = _planBase.For(goals.ToArray());
            _labels = goals.Select(g => g.label).ToArray();
            _goals = goals.Count;
            _eval.UseGoals(_runGoals);
        }

        static FullTasTracker.State Fresh(int count, int next)
        {
            var s = new FullTasTracker.State { Next = next, InputTicks = new long[count], PhysicsTicks = new long[count], HeldFrames = new long[count] };
            Array.Fill(s.InputTicks, -1L);
            Array.Fill(s.PhysicsTicks, -1L);
            Array.Fill(s.HeldFrames, -1L);
            return s;
        }

        const int IdleSettle = 300, IdleTicks = 2000;

        /// <summary>
        /// Cash per tick laps earn from s (just after inputs): the course's lap reward over a lap of lapTicks, plus the clones' income
        /// standing still after a quick restart (off any box) after IdleSettle (a clone just bought first pays about a path later),
        /// over the whole clone laps of the course that fit in IdleTicks: a pool that runs in step pays in a bunch once a lap, so a
        /// window cut mid-lap is off by up to a bunch. formula: the same from the clones' own
        /// numbers (<see cref="CloneIncome"/>), a cross-check. Null when no restart or stand plays.
        /// </summary>
        double? Rate(SimSnapshot s, List<InputTick> inputs, int course, long lapTicks, out double formula)
        {
            formula = double.NaN;
            Read(s);
            courseScript c = _eval.Courses.First(x => x.courseNumber == course);
            double reward = c.reward, path = (float)BestPathTime.GetValue(c) / _dt;
            int window = path > 0 && path < IdleTicks ? (int)Math.Round(Math.Floor(IdleTicks / (path + CloneLifeFrames / FramesPerTick)) * (path + CloneLifeFrames / FramesPerTick)) : IdleTicks;
            foreach (var (pre, restarted) in Restarts(s, inputs, _goals).Take(1))
            {
                int length = inputs.Count + pre.Count;
                SimSnapshot settled = AdvanceFrom(restarted, length, Enumerable.Repeat(new InputTick(), IdleSettle).ToList());
                SimSnapshot idle = settled == null ? null : AdvanceFrom(settled, length + IdleSettle, Enumerable.Repeat(new InputTick(), window).ToList());
                if (idle == null) continue;
                Read(settled);
                formula = CloneIncome(_eval) + reward / Math.Max(1, lapTicks);
                return (Cash(idle) - Cash(settled)) / window + reward / Math.Max(1, lapTicks);
            }
            return null;
        }

        /// <summary>
        /// Ladders on top of a record lap (state, just after its finish at tick, played after inputs): K = 2..--ladder-max back-to-back
        /// record laps from the quick restart, the fastest lap slowed by K - 1, ..., 1, 0 ticks, so each finish is a new best and a
        /// break (docs/fulltas.md, "Engine farm planner"). K is at most the ticks between the best before the record lap and the
        /// fastest. The laps after the first are played here as the record lap's way from the restart (no break: its time is the
        /// best already), then K(K-1)/2 idle ticks for the slowing; each of those K - 1 breaks is credited with the Cash the model
        /// gives it (while the course keeps Start's spawn reserve a free slot refills on the next frame, so after a break the pool
        /// runs in step, and at the next break every clone has run the restart's overhead o of its new path T': it pays the rest,
        /// C (1 - o / T') payouts). A course whose clones have spawned (on build 25640326 on screen too) or been off screen has lost
        /// the reserve for good: its pool refills one an interval, and a break's C / 2 is only an advance the emptier pool pays back
        /// over T' (none credited).
        /// </summary>
        List<(string what, FullTasGoal[] goals, List<InputTick> inputs, SimSnapshot state, long tick, double credit)> Ladders(
            List<InputTick> inputs, SimSnapshot state, long tick, List<FullTasGoal> planned, FullTasGoal[] lap, int course)
        {
            var found = new List<(string, FullTasGoal[], List<InputTick>, SimSnapshot, long, double)>();
            float fastest = BestPath(state, course);
            int fast = (int)Math.Round(fastest / _dt), kmax = Math.Min(_ladderMax, (int)Math.Round(_recordBefore / _dt) - fast);
            if (kmax < 2 || _recordWay == null) return found;
            courseScript c = _eval.Courses.First(x => x.courseNumber == course);
            var (clones, reserve) = Clones(_eval, course);
            if (!reserve) clones = 0;
            double pay = ClonePay(_eval, c);
            var list = _kept.Concat(planned).Concat(lap).ToList();
            long last = tick;
            for (int k = 2; k <= kmax; k++)
            {
                list.AddRange(lap);
                Track(list);
                int start = list.Count - 2;
                var (pre, restarted) = Restarts(state.WithGoal(Fresh(list.Count, start)), inputs, start).FirstOrDefault();
                Step end = restarted == null ? null : Reaches(restarted, inputs.Count + pre.Count, _recordWay, new Target { Goal = start + 1, Key = lap[1].label });
                if (end == null) { Log($"ladder: lap {k} doesn't play from the quick restart ({(restarted == null ? "no restart" : Replayed(restarted, inputs.Count + pre.Count, _recordWay, new Target { Goal = start + 1, Key = lap[1].label }))})"); break; }
                inputs = inputs.Concat(pre).Concat(end.Inputs).ToList();
                state = end.Next;
                long overhead = end.Physics - last - fast;
                last = end.Physics;
                // Lap k of K sets the path fast + K - k; the laps' slowing is sum 0..K-1 ticks.
                double credit = clones * pay * Enumerable.Range(0, k - 1).Sum(j => Math.Max(0.0, 1.0 - (double)overhead / (fast + j)));
                int slow = k * (k - 1) / 2;
                SimSnapshot slowed = AdvanceFrom(state, inputs.Count, Enumerable.Repeat(new InputTick(), slow).ToList());
                if (slowed == null) break;
                found.Add(($"ladder {k}", Enumerable.Range(0, k).SelectMany(_ => lap).ToArray(), inputs.Concat(Enumerable.Repeat(new InputTick(), slow)).ToList(),
                    slowed, end.Physics + slow, credit));
            }
            return found;
        }

        /// <summary>s with Cash more on its Shop (a copy: the snapshot is shared).</summary>
        static SimSnapshot Credited(SimSnapshot s, double cash)
        {
            if (cash <= 0 || s.Economy?.Awarded == null) return s;
            SimSnapshot t = s.WithGoal(s.Goal);
            var awarded = (double[])s.Economy.Awarded.Clone();
            awarded[(int)globalStats.Currencies.Cash] += cash;
            t.Economy = new Shop.State { Spent = s.Economy.Spent, Awarded = awarded, Economy = s.Economy.Economy, BuyMax = s.Economy.BuyMax };
            return t;
        }

        // The studio's 280 fps clock renders 5.6 frames a physics tick; a clone in flight runs its path and 1.5 frames more on average
        // before its slot refills (clonesScript, builds 25630570 and 25640326: income / ideal = 1 - 1.5 / (T fps), C:\tmp\igtap\clonesim.py).
        const double FramesPerTick = IGTAPTasMod.MacroEngine.PlaybackFrameRate * 0.02, CloneLifeFrames = 1.5;

        /// <summary>The course's clone count, and whether it still has Start's spawn reserve (its spawn timer far past any interval).</summary>
        static (int count, bool reserve) Clones(Simulation sim, int course)
        {
            foreach (object[] row in sim.Shop.Economy?.CloneStates() ?? Enumerable.Empty<object[]>())
                if ((int)row[0] == course) return ((int)row[1], (float)row[5] > 100f);
            return (0, false);
        }

        /// <summary>A clone's payout on course c: reward x the clone multiplier x the competition boost x (2 bigness + 1), its size
        /// rolled as SpawnClone rolls it, averaged.</summary>
        static double ClonePay(Simulation sim, courseScript c)
        {
            Economy e = sim.Shop.Economy;
            if (e == null) return 0;
            double mult = 0.1 + e.GlobalOf(globalStats.globalUpgradeSet.cloneMult) * 0.2 + e.LocalOf(c.localUpgradesScript, localUpgrades.localUpgradeSet.cloneMult) * 0.1;
            return c.reward * mult * (c.compBoostTimeLeft > 0.0 ? c.compBoostBonus : 1.0) * Rolls(e, c).size;
        }

        /// <summary>
        /// SpawnClone's speed and size rolls averaged: E[2j + 1] (a payout's size) and E[(2j + 1) (1 + 0.35 i) 0.75^j] (payouts a path
        /// time, the speed counted). Each tier i is reached while Random.Range(0, 101) stays under chance / (1 + i) and 50 + 5 cap.
        /// </summary>
        static (double size, double income) Rolls(Economy e, courseScript c)
        {
            double[] Tiers(double chance, double cap)
            {
                var p = new List<double>();
                double reach = 1.0;
                for (int i = 0; i < 1.0 + cap; i++)
                {
                    double under = Math.Min(chance / (1.0 + i), 50.0 + cap * 5.0), pass = Enumerable.Range(0, 101).Count(k => k < under) / 101.0;
                    p.Add(reach * (1 - pass));
                    reach *= pass;
                }
                p.Add(reach);
                return p.ToArray();
            }
            localUpgrades l = c.localUpgradesScript;
            double[] fast = Tiers(e.LocalOf(l, localUpgrades.localUpgradeSet.fastCloneChance) * 5.0 + e.GlobalOf(globalStats.globalUpgradeSet.fastCloneChance) * 8.0,
                e.GlobalOf(globalStats.globalUpgradeSet.maxCloneFastness));
            double[] big = Tiers(e.LocalOf(l, localUpgrades.localUpgradeSet.bigCloneChance) * 5.0 + e.GlobalOf(globalStats.globalUpgradeSet.bigCloneChance) * 8.0,
                e.GlobalOf(globalStats.globalUpgradeSet.maxCloneBigness));
            double size = 0, income = 0;
            for (int i = 0; i < fast.Length; i++)
                for (int j = 0; j < big.Length; j++)
                {
                    size += fast[i] * big[j] * (2 * j + 1);
                    income += fast[i] * big[j] * (2 * j + 1) * (1 + 0.35 * i) * Math.Pow(0.75, j);
                }
            return (size, income);
        }

        /// <summary>
        /// The clones' Cash per physics tick by formula, every course with clones and a path (build 25640326). On screen a full pool
        /// banks its spawn timer, so every slot refills the frame after its clone pays, so min(C, path frames) clones fly and each pays once a path: C pay / path, less
        /// 1.5 frames a lap. Off screen a course pays ceil(pay C / path) (the rolls' speed counted) once its timer reaches 1 s:
        /// every 50 ticks.
        /// </summary>
        internal static double CloneIncome(Simulation sim)
        {
            Economy e = sim.Shop.Economy;
            if (e == null) return 0;
            double dt = sim.World.FixedDeltaTime, w = 0;
            foreach (object[] row in e.CloneStates())
            {
                int number = (int)row[0], count = (int)row[1];
                float path = (float)row[2];
                courseScript c = sim.Courses.FirstOrDefault(x => x.courseNumber == number);
                if (c == null || path <= 0.1f) continue;
                double pay = ClonePay(sim, c) / Rolls(e, c).size * Rolls(e, c).income, ticks = path / dt;
                w += (bool)row[3] ? Math.Min(count, ticks * FramesPerTick) * pay / ticks * (1 - CloneLifeFrames / (ticks * FramesPerTick))
                    : Math.Ceiling(pay * count / path) * dt;
            }
            return w;
        }

        /// <summary>Restores s to read the economy: its goal state can be another plan's, so it gets one the tracker holds.</summary>
        void Read(SimSnapshot s) { _eval.Restore(s.WithGoal(Fresh(_goals, 0))); }
        double Cash(SimSnapshot s) { Read(s); return _eval.Shop.CashNow; }

        bool? Affordable(SimSnapshot s, string[] boxes) { Read(s); return _eval.Shop.Affordable(boxes); }

        bool Buying(SimSnapshot s, FullTasGoal g) { Read(s); return _eval.Shop.Buying(g.boxes); }

        static readonly Regex CostText = new Regex(@"costs (\S+)"), LevelText = new Regex(@": (\d+)/");

        // ponytail: reads the box's cost and level from Shop.Describe's log text (4 digits); a Shop accessor if the planner needs more.
        double Cost(SimSnapshot s, FullTasGoal g)
        {
            Read(s);
            Match m = CostText.Match(_eval.Shop.Describe(g.boxes));
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0.0;
        }

        string Level(SimSnapshot s, FullTasGoal g)
        {
            Read(s);
            Match m = LevelText.Match(_eval.Shop.Describe(g.boxes));
            return m.Success ? m.Groups[1].Value : "?";
        }
    }
}
