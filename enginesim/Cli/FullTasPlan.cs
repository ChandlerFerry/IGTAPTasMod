using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        int PlanFarm(string[] args)
        {
            Open();
            FullTasGoal[] all = _runGoals.Goals;
            int kept = GoalNumber(Solve.Option(args, "--from", ""), 0);
            // --target a,b: buy a first, then b (a big box that pays back, the statue, has to be saved for: step by step, the plan
            // toward b alone keeps buying the smaller boxes that pay back sooner and reaches it late).
            string[] targets = Solve.Option(args, "--target", "buy:buydj").Split(',');
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
            foreach (string path in _seedFiles) _seeds.Add(ReadSeed(path));
            SeedLaps(at, route, kept, lap);
            _kept = all.Take(kept).ToArray();
            _planBase = _runGoals;
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
            while (!done && !TimeUp)
            {
                string aim = targets[phase];
                FullTasGoal target = byLabel[aim];
                int walk = _pieces.Where(p => p.Target == aim).Select(p => p.Inputs.Count).DefaultIfEmpty(0).Min();
                double cost = Cost(at, target);
                (string what, FullTasGoal[] goals, List<InputTick> inputs, SimSnapshot state, long tick, double eta)? pick = null;
                var options = new List<(string, FullTasGoal[])> { (aim, new[] { target }), ("lap", lap) };
                options.AddRange(buys.Where(l => l != aim && !unreachable.Contains(l)).Select(l => (l, new[] { byLabel[l] })));
                bool aimPayable = false, aimPlayed = false;
                foreach (var (what, goals) in options)
                {
                    bool? affordable = goals[0].kind == FullTasGoal.Buy ? Affordable(at, goals[0]) : true;
                    // The target box can be off only because its course is unloaded here (Course 3's buydj seen from Course 2): the
                    // Cash decides. Skipping it laped Course 2 1724 times with a hundred times buydj's price held.
                    if (affordable == null && what == aim) affordable = Cash(at) >= cost;
                    if (affordable == null) continue;
                    // A buy-max touch still buying goes on without another touch; a search for a way to touch it again found none.
                    if (goals[0].kind == FullTasGoal.Buy && what != aim && Buying(at, goals[0])) continue;
                    if (what == aim) aimPayable = affordable == true;
                    var played = what == "lap" ? Lap(at, route, planned, lap) : Play(at, route, planned, goals, affordable == true);
                    if (what == aim) aimPlayed = played != null;
                    if (played == null)
                    {
                        if (affordable == true && what != "lap" && what != aim && unreachable.Add(what)) Log($"{what}: no way found from tick {now}; dropped from the plan");
                        continue;
                    }
                    var (inputs, state, tick) = played.Value;
                    double eta = tick;
                    if (what != aim)
                    {
                        // Laps fill the rest (a plan never stands at a box): the income is the lap's reward plus the clones'. One lap's
                        // clone payouts are too few to average out their rolls, and the best of several noisy guesses bought
                        // every box, so the clones' income is measured standing still after a quick restart, over IdleTicks.
                        double? rate = Rate(state, inputs, course, lapTicks);
                        if (rate == null || rate <= 0) continue;
                        eta = tick + Math.Max(0.0, cost - Cash(state)) / rate.Value + walk;
                    }
                    if (pick == null || eta < pick.Value.eta) pick = (what, goals, inputs, state, tick, eta);
                }
                // More laps only add Cash: once the target is paid for and still no way reaches it, the plan is stuck here.
                if (aimPayable && !aimPlayed) { Log($"stuck at tick {now}: {aim} is paid for but no way reaches it from {Where(at.WithGoal(Fresh(_goals, 0))).at} ({_whyNot})"); break; }
                if (pick == null) { Log($"stuck at tick {now}: no option plays from here"); break; }
                var p = pick.Value;
                planned.AddRange(p.goals);
                route = p.inputs;
                at = p.state;
                now = p.tick;
                if (p.what == aim) done = ++phase == targets.Length;
                double cash = Cash(at);
                string levels = string.Join(" ", buys.Append(targetLabel).Select(l => l.Substring(4) + " " + Level(at, byLabel[l])));
                Log($"tick {now,6} ({now - bonus1} after reward:bonus1)  {p.what,-22} cash {cash:G4}  eta {p.eta:F0}  {levels}");
                log.Add(new { tick = now, step = p.what, cash, eta = Math.Round(p.eta), levels });
            }
            string[] tokens = planned.Select(g => g.label).ToArray();
            Log(done ? $"{targetLabel} at tick {now}, {now - bonus1} after reward:bonus1 (the best: {old}); {tokens.Length} goals replace {targetIndex + 1 - kept}"
                : $"stopped before {targetLabel} ({tokens.Length} goals planned)");
            Console.WriteLine(JsonSerializer.Serialize(tokens));
            string outFile = Solve.Option<string>(args, "--out", null);
            if (outFile != null)
                File.WriteAllText(outFile, JsonSerializer.Serialize(new { done, target = targetLabel, tick = now, afterBonus1 = now - bonus1, best = old,
                    goals = all.Take(kept).Select(g => g.label).Concat(tokens).Concat(done ? all.Skip(targetIndex + 1).Select(g => g.label) : Array.Empty<string>()),
                    planned = tokens, log }, new JsonSerializerOptions { WriteIndented = true }));
            // The plan's own route, as the solver saves a run's best (fulltas/<run>.json): the goals alone replay it only as
            // closely as other pieces allow, and a buy planned just affordable on arrival can then fall short.
            string routeFile = Solve.Option<string>(args, "--route", null);
            if (routeFile != null && done)
            {
                var full = _kept.Concat(planned).Concat(all.Skip(targetIndex + 1)).ToList();
                Track(full);
                RunResult Play(List<InputTick> inputs)
                {
                    _eval.SetStart(_origin.WithGoal(Fresh(full.Count, 0)), 0);
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
                        _eval.SetStart(_origin.WithGoal(Fresh(full.Count, 0)), 0);
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
                            + stops + "; re-plan the stretch it stops in, or solve the goals after it (Solve unsolved)");
                    }
                }
                // The route follows the planned goals, not the run's current ones: fulltas.json takes the --out goals with it.
                File.WriteAllText(routeFile, JsonSerializer.Serialize(new { name = _name, goals = full.Select(g => g.label), inputs = written.Select(t => t.ToJson()),
                    goalInputTicks = r.GoalInputTicks, goalPhysicsTicks = r.GoalPhysicsTicks, total = r.Progress == full.Count ? r.GoalPhysicsTicks[full.Count - 1] : -1 }));
                Log($"route: reaches {r.Progress} of {full.Count} goals" + (r.Progress == full.Count ? $" in {r.GoalPhysicsTicks[full.Count - 1] * _dt:0.00}s" : "") + $" -> {routeFile}");
            }
            return done ? 0 : 3;
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
        /// Goals that aren't buys, gates or finishes are played through, not filed.
        /// </summary>
        void Harvest(SimSnapshot at, List<InputTick> route, int from, int until)
        {
            int filed = _pieces.Count, length = route.Count;
            for (int k = from; k < until; k++)
            {
                int end = (int)Math.Min(_best.GoalInput[k], _best.Inputs.Count);
                if (end > length && IsLoop(k))
                    Keep(new Step { Inputs = _best.Inputs.GetRange(length, Math.Min(end + 2, _best.Inputs.Count) - length) }, at, _best.Inputs.GetRange(0, length),
                        new Target { Goal = k, Key = _labels[k] });
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
                _best = new Route { Inputs = inputs, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks };
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
            var s = new FullTasTracker.State { Next = next, InputTicks = new long[count], PhysicsTicks = new long[count] };
            Array.Fill(s.InputTicks, -1L);
            Array.Fill(s.PhysicsTicks, -1L);
            return s;
        }

        const int IdleSettle = 300, IdleTicks = 2000;

        /// <summary>
        /// Cash per tick laps earn from s (just after inputs): the course's lap reward over a lap of lapTicks, plus the clones' income
        /// standing still after a quick restart (off any box) for IdleTicks after IdleSettle (a clone just bought first pays about a
        /// path later). Null when no restart or stand plays.
        /// </summary>
        double? Rate(SimSnapshot s, List<InputTick> inputs, int course, long lapTicks)
        {
            Read(s);
            double reward = _eval.Courses.First(c => c.courseNumber == course).reward;
            foreach (var (pre, restarted) in Restarts(s, inputs, _goals).Take(1))
            {
                int length = inputs.Count + pre.Count;
                SimSnapshot settled = AdvanceFrom(restarted, length, Enumerable.Repeat(new InputTick(), IdleSettle).ToList());
                SimSnapshot idle = settled == null ? null : AdvanceFrom(settled, length + IdleSettle, Enumerable.Repeat(new InputTick(), IdleTicks).ToList());
                if (idle != null) return (Cash(idle) - Cash(settled)) / IdleTicks + reward / Math.Max(1, lapTicks);
            }
            return null;
        }

        /// <summary>Restores s to read the economy: its goal state can be another plan's, so it gets one the tracker holds.</summary>
        void Read(SimSnapshot s) { _eval.Restore(s.WithGoal(Fresh(_goals, 0))); }
        double Cash(SimSnapshot s) { Read(s); return _eval.Shop.CashNow; }

        bool? Affordable(SimSnapshot s, FullTasGoal g) { Read(s); return _eval.Shop.Affordable(g.boxes); }

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
