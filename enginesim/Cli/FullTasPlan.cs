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
            _kept = all.Take(kept).ToArray();
            _planBase = _runGoals;
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
                foreach (var (what, goals) in options)
                {
                    bool? affordable = goals[0].kind == FullTasGoal.Buy ? Affordable(at, goals[0]) : true;
                    if (affordable == null) continue;
                    var played = what == "lap" ? Lap(at, route, planned, lap) : Play(at, route, planned, goals, affordable == true);
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
                _eval.SetStart(_origin.WithGoal(Fresh(full.Count, 0)), 0);
                _eval.Goals.Limit = full.Count;
                RunResult r = _eval.Run(route, null, null, 1000000);
                // The route follows the planned goals, not the run's current ones: fulltas.json takes the --out goals with it.
                File.WriteAllText(routeFile, JsonSerializer.Serialize(new { name = _name, goals = full.Select(g => g.label), inputs = route.Select(t => t.ToJson()),
                    goalInputTicks = r.GoalInputTicks, goalPhysicsTicks = r.GoalPhysicsTicks, total = -1 }));
                Log($"route: reaches {r.Progress} of {full.Count} goals -> {routeFile}");
            }
            return done ? 0 : 3;
        }

        /// <summary>
        /// Files the best's own steps from goal from on (each goal's inputs from where the goal before left the player) as library
        /// pieces: the builder files only the pieces it builds, and a polished route passes spots none starts from (after the
        /// statue a quick restart lands at its checkpoint, and the lap from there crosses Course 3's gate where no finish piece began).
        /// </summary>
        void Harvest(SimSnapshot at, List<InputTick> route, int from, int until)
        {
            int filed = _pieces.Count, length = route.Count;
            for (int k = from; k < until && IsLoop(k); k++)
            {
                int end = (int)Math.Min(_best.GoalInput[k], _best.Inputs.Count);
                if (end > length)
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
        /// search when <paramref name="search"/> and none plays). Null when one can't be reached.
        /// </summary>
        (List<InputTick> inputs, SimSnapshot state, long tick)? Play(SimSnapshot at, List<InputTick> route, List<FullTasGoal> planned, FullTasGoal[] goals, bool search)
        {
            var inputs = route.ToList();
            var list = _kept.Concat(planned).ToList();
            long tick = -1;
            foreach (FullTasGoal g in goals)
            {
                list.Add(g);
                Track(list);
                int i = list.Count - 1;
                at = at.WithGoal(Fresh(list.Count, i));
                var target = new Target { Goal = i, Key = g.label };
                Step step = BestKnown(at, inputs, i, target);
                if (step == null && search)
                {
                    step = SolvePiece(at, inputs, i, target, _pieceMinutes, BuildStall, new List<List<InputTick>>());
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
