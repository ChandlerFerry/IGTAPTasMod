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
using EconomyPlanResult = IGTAP.EngineSim.Cli.PlanResult;

namespace IGTAP.EngineSim.Cli
{
    // How the economy planner's plans are played (docs/fulltas.md, "Economy planner": realize): each step with the loop builder's
    // pieces (Play, Lap, RecordLap), the best's own steps and the saved course routes filed first (Harvest, SeedLaps), and the old
    // route joined after the plan's target (Join). FullTas.Realize (EconomyPlan.cs) drives them.
    sealed partial class FullTas
    {
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

        /// <summary>What <see cref="Replan"/> planned.</summary>
        sealed class PlanResult
        {
            public bool Done;
            public string Target;
            /// <summary>The target's physics tick in the plan, and in the best (-1: the best doesn't reach it).</summary>
            public long Tick, Old, AfterBonus1;
            /// <summary>Each round's plan's target tick (-1: no plan), the first the search's from the start.</summary>
            public long[] PlanTicks;
            /// <summary>The plan's goals, and the whole list: the goals before the stretch, the plan, the goals after the target.</summary>
            public string[] Planned, Goals;
            /// <summary>Per goal of <see cref="Goals"/>, its index in the planned list (Replan's <c>all</c>); -1 for a goal the plan
            /// or a stray fit put in.</summary>
            public int[] OldOf;
            public List<object> Log;
            /// <summary>Why it isn't done (null: it is).</summary>
            public string Why;
            /// <summary>With join: the plan's route under <see cref="Goals"/>, the best's way on after the target joined when it plays
            /// on, re-timed from the start (Retune from goal 0) when it reaches every goal. Null when the plan isn't done.</summary>
            public Route Run;
        }

        /// <summary>
        /// The economy planner's plan (docs/fulltas.md, "Economy planner") of <paramref name="all"/> (a goal list <paramref name="best"/>
        /// plays, not necessarily the run's) from goal <paramref name="kept"/> (the goals before it stay) until it buys the last of
        /// <paramref name="targets"/>: searched (EconomyPlan.Search, --mode auto) and realized (Realize), with <paramref name="join"/>
        /// the route too (<see cref="PlanResult.Run"/>). The goals part's pull calls it on each economy stretch (FullTasAutoGoals.cs, GoalsPull):
        /// the solve's best, goals and simulation factory are put back after it. <paramref name="tickCap"/>: a plan whose target
        /// comes after this tick is not realized (it can't beat the best); <paramref name="seconds"/>: the whole call's budget
        /// (searches, realize, join; a piece search running at the end can overrun it by --piece-minutes); <paramref name="bonuses"/>:
        /// the reward goals the plan may take (the stretch's own: it takes or skips each).
        /// </summary>
        PlanResult Replan(Route best, FullTasGoal[] all, int kept, string[] targets, bool join, long tickCap = long.MaxValue, double seconds = ReplanMinutes * 60,
            string[] bonuses = null)
        {
            var saved = (_best, _runGoals, _labels, _goals, _create, _kept, _planBase, _borrow, _pieceState);
            _best = best;
            _pieceState = null;
            _planBase = _runGoals;
            try
            {
                Track(all.ToList());
                _planDeadline = _clock.Elapsed.TotalSeconds + seconds;
                return PlanAndRealize(all, kept, targets, join, tickCap, bonuses ?? Array.Empty<string>());
            }
            finally
            {
                _planDeadline = double.PositiveInfinity;
                (_best, _runGoals, _labels, _goals, _create, _kept, _planBase, _borrow, _pieceState) = saved;
                _eval.UseGoals(_runGoals);
            }
        }

        /// <summary>The goals part's plans: at most this long, search, realize and join together.</summary>
        const double ReplanMinutes = 10;

        /// <summary>When a Replan's budget runs out (_clock seconds; infinite outside one): its searches get what is left, and
        /// realize and the join stop where they are.</summary>
        double _planDeadline = double.PositiveInfinity;

        bool PlanOver => TimeUp || _clock.Elapsed.TotalSeconds >= _planDeadline;

        /// <summary>A search's settings with the budget left (at least a second; the settings' own when no Replan budget runs).</summary>
        PlanSettings Budgeted(PlanSettings ps) => double.IsPositiveInfinity(_planDeadline) ? ps
            : ps with { Seconds = Math.Max(1, Math.Min(ps.Seconds, _planDeadline - _clock.Elapsed.TotalSeconds)) };

        PlanResult PlanAndRealize(FullTasGoal[] all, int kept, string[] targets, bool join, long tickCap, string[] bonuses)
        {
            string targetLabel = targets[targets.Length - 1];
            int targetIndex = Array.FindIndex(all, kept, g => g.label == targetLabel);
            if (targetIndex < 0) throw new ArgumentException($"no goal {targetLabel} after goal {kept}");
            if (kept < 1) throw new ArgumentException("the plan starts after a goal (its place)");
            var (at, route) = BuildFrom(all, kept, targetIndex);
            string place = all[kept - 1].label;
            MoveTable moves = MoveTable.Load(this, File.Exists(BuyTableFile) ? BuyTableFile : null, RoutesFile, _best, MovesFile);
            if (!moves.Places.ContainsKey(place)) throw new InvalidOperationException($"goal {kept} {place} is no place of the movement table");
            // Each label resolved once: NewGoal restores the start to find a reward box, which mid-search would leave the economy there.
            var resolved = new Dictionary<string, FullTasGoal>();
            FullTasGoal GoalOf(string label) => resolved.TryGetValue(label, out FullTasGoal g) ? g
                : resolved[label] = all.FirstOrDefault(x => x.label == label) ?? NewGoal(label)?.goal;
            // A search may take half the call's budget (at least ReplanMinutes): FalseEnding%-280's buy:buywj to buy:buyds, the
            // beam's every step kind offered (all:box among them), was at tick 9759 of about 10000 when 10 minutes ran out.
            var settings = new PlanSettings(targets, bonuses, Seconds: Math.Max(ReplanMinutes * 60, (_planDeadline - _clock.Elapsed.TotalSeconds) / 2));
            long start = _best.GoalPhysics[kept - 1];
            EconomyPlanResult plan = PlanFrom(at, place, null, moves, Budgeted(settings), GoalOf);
            Realization z = Realize(plan, at, route, start, place, null, moves, (s, p, prefix) => PlanFrom(s, p, prefix, moves, Budgeted(settings), GoalOf), GoalOf, tickCap);
            moves.Save(MovesFile);
            long bonus1 = Array.FindIndex(all, g => g.label == "reward:bonus1") is int b && b >= 0 && b < _best.Progress ? _best.GoalPhysics[b] : 0;
            string[] tokens = z.Planned.Select(g => g.label).ToArray();
            var result = new PlanResult
            {
                Done = z.Done, Target = targetLabel, Tick = z.Tick, Old = targetIndex < _best.Progress ? _best.GoalPhysics[targetIndex] : -1, AfterBonus1 = z.Tick - bonus1,
                Planned = tokens, Log = z.Log, Why = z.Why, PlanTicks = z.PlanTicks.ToArray(),
                Goals = all.Take(kept).Select(g => g.label).Concat(tokens).Concat(z.Done ? all.Skip(targetIndex + 1).Select(g => g.label) : Array.Empty<string>()).ToArray(),
                OldOf = Enumerable.Range(0, kept).Concat(Enumerable.Repeat(-1, tokens.Length))
                    .Concat(z.Done ? Enumerable.Range(targetIndex + 1, all.Length - targetIndex - 1) : Enumerable.Empty<int>()).ToArray(),
            };
            if (join && z.Done) (result.Run, result.Goals, result.OldOf) = Join(z.Planned, z.Inputs, all, kept, targetIndex);
            return result;
        }

        /// <summary>
        /// The builder set up on <paramref name="all"/> from goal <paramref name="kept"/> (the goals before it stay, the planned stretch
        /// runs to <paramref name="targetIndex"/>): the best replayed there (as the builder does: a goal's input tick can fall a tick or
        /// two early), its own steps in the stretch and the saved course routes filed as pieces, its ways borrowable, and piece
        /// searches tracking the plan's goals. The state there and the route to it.
        /// </summary>
        (SimSnapshot at, List<InputTick> route) BuildFrom(FullTasGoal[] all, int kept, int targetIndex)
        {
            if (_best.Progress < kept) throw new InvalidOperationException($"the best reaches goal {_best.Progress}, not the planned stretch's start (goal {kept})");
            int course = all.Skip(kept).Take(targetIndex - kept).FirstOrDefault(g => g.kind == FullTasGoal.Start || g.kind == FullTasGoal.Finish)?.course ?? 0;
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
            var byLabel = new Dictionary<string, FullTasGoal>();
            foreach (FullTasGoal g in all) byLabel.TryAdd(g.label, g);
            if (course > 0) SeedLaps(at, route, kept, new[] { byLabel["start:" + course], byLabel["finish:" + course] });
            _kept = all.Take(kept).ToArray();
            _borrow = (all.Select(g => g.label).ToArray(), kept, Math.Min(targetIndex, _best.Progress - 1));
            // Piece searches replay the plan so far on fresh simulations: they track the plan's goals, as the evaluator does.
            Func<Simulation> make = _create;
            _create = () => { Simulation sim = make(); sim.UseGoals(_runGoals.For(_runGoals.Goals)); return sim; };
            return (at, route);
        }

        /// <summary>A join that dies (or stops short of a goal) after the target bridges at most this many goals with the builder.</summary>
        const int JoinBridges = 16;

        /// <summary>
        /// The whole route of a realized plan (<paramref name="planned"/>'s goals after the first <paramref name="kept"/> of
        /// <paramref name="all"/>, played by <paramref name="route"/>), the goals after the target (<paramref name="targetIndex"/>)
        /// kept: the best's way on after the target joined when it plays on, re-timed from the start (Retune from goal 0) when it
        /// reaches every goal. The old route is joined from its first Quick Restart after the target, after its old inputs between
        /// the two or 0-24 idle ticks (a reset segment's way: the same Quick Restart, from the plan's end). Where every join dies
        /// or stops before a goal (the plan's end left another checkpoint, frame phase or Cash for the old way), that goal is played
        /// the builder's way (Play: the library, the best's own ways, a piece search) and the old route joined again from it, at most
        /// JoinBridges goals; one that stops on a stray buy gets its goals fitted (below). The route, its goal labels and per goal its
        /// index in <paramref name="all"/> (-1: the plan's, or one a stray fit put in). Stops bridging when the Replan budget is out.
        /// </summary>
        (Route run, string[] goals, int[] oldOf) Join(List<FullTasGoal> planned, List<InputTick> route, FullTasGoal[] all, int kept, int targetIndex)
        {
            string targetLabel = all[targetIndex].label;
            var full = _kept.Concat(planned).Concat(all.Skip(targetIndex + 1)).ToList();
            Track(full);
            SimSnapshot origin = _origin.WithGoal(Fresh(full.Count, 0));
            RunResult Replay(List<InputTick> inputs)
            {
                _eval.SetStart(origin, 0);
                _eval.Goals.Limit = full.Count;
                return _eval.Run(inputs, null, null, 1000000);
            }
            RunResult r = Replay(route);
            List<InputTick> written = route;
            int reached = kept + planned.Count;
            var saved = _borrow;
            // Each goal's goal in the old route (-1: the plan's, or one a stray fit put in), kept in step with the fits.
            var oldOf = Enumerable.Range(0, kept).Concat(Enumerable.Repeat(-1, reached - kept)).Concat(Enumerable.Range(targetIndex + 1, full.Count - reached)).ToList();
            // The bridges borrow the best's own ways to the goals after the target too.
            _borrow = (all.Select(g => g.label).ToArray(), kept, Math.Min(all.Length, _best.Progress) - 1);
            try
            {
                List<InputTick> basis = route;
                (List<InputTick> inputs, RunResult result, int wait) furthest = (null, null, 0);
                for (int at = reached, bridges = 0; r.Progress >= reached && at < full.Count && !PlanOver;)
                {
                    int old = at == reached ? targetIndex : oldOf[at - 1];
                    int oldCut = old < 0 ? -1 : (int)Math.Min(_best.GoalInput[old], _best.Inputs.Count);
                    int oldRestart = old < 0 || old + 1 >= all.Length ? -1 : _best.Inputs.FindIndex(oldCut, k => k.reset);
                    RunResult b = Replay(basis);
                    if (oldRestart < 0 || b.Progress < at) break;
                    // Cut where goal at - 1 is reached by the cut's own inputs: its input tick, or a few after (with no input after
                    // them the replay can still drift into it, where the old way's next inputs steer away).
                    List<InputTick> head = null;
                    for (int extra = 0; extra <= 6 && head == null; extra++)
                    {
                        int cut = (int)Math.Min(b.GoalInputTicks[at - 1] + extra, Math.Max(basis.Count, b.GoalInputTicks[at - 1]));
                        var h = basis.Take(cut).ToList();
                        Pad(h, cut);
                        RunResult rh = Replay(h);
                        if (rh.Progress >= at && rh.GoalInputTicks[at - 1] <= cut) head = h;
                    }
                    if (head == null) break;
                    List<InputTick> oldRunout = _best.Inputs.GetRange(oldCut, oldRestart - oldCut);
                    var runouts = new List<List<InputTick>> { oldRunout };
                    for (int wait = 0; wait <= 24; wait++) runouts.Add(Enumerable.Repeat(new InputTick { held = head.Count > 0 && head[head.Count - 1].held }, wait).ToList());
                    List<InputTick> tail = _best.Inputs.Skip(oldRestart).ToList();
                    furthest = (null, null, 0);
                    foreach (List<InputTick> runout in runouts)
                    {
                        if (runout.Count == 0 && head.Count > 0 && head[head.Count - 1].reset) continue;
                        List<InputTick> joined = head.Concat(runout).Concat(tail).ToList();
                        RunResult j = Replay(joined);
                        if (furthest.result == null || j.Progress > furthest.result.Progress) furthest = (joined, j, runout.Count);
                        if (j.Progress < full.Count) continue;
                        written = joined;
                        r = j;
                        Log($"route: the old route after {(at == reached ? targetLabel : full[at - 1].label)} plays on ({runout.Count} ticks before its Quick Restart)");
                        break;
                    }
                    if (written != route || furthest.result == null) break;
                    // A join that loses the head's goals (the old way's next inputs steer off them) bridges the next goal.
                    if (furthest.result.Progress < at) furthest = (basis, b, 0);
                    // The plan's end can hold more Cash than the best's did there, and the old way on then walks through a box it can
                    // pay for (a strict stray: README ### Rules 38). The goals are fitted as the goals part's StrayFit does: that box's
                    // next buy goal moved up to the stray, else its label put in there; the join replayed, at most once a goal.
                    RunResult fitted = furthest.result;
                    var fits = new List<string>();
                    // A box that strays again at the goal it was fitted at would be fitted there forever: once per goal and box.
                    var fitAt = new HashSet<(int, string)>();
                    while (fits.Count < full.Count && fitted.Progress < full.Count)
                    {
                        string box = StrayBox(fitted.EndReason);
                        if (box == null || !fitAt.Add((fitted.Progress, box))) break;
                        bool Stray(FullTasGoal g) => g.kind == FullTasGoal.Buy && g.boxes?.Length == 1 && g.boxes[0].Split('#')[0] == box && FullTasGoal.IsEconomy(g.label);
                        int p = fitted.Progress, later = box == null ? -1 : full.FindIndex(p, Stray);
                        FullTasGoal g = box == null ? null : later >= 0 ? full[later] : all.FirstOrDefault(Stray);
                        if (g == null) break;
                        int o = later >= 0 ? oldOf[later] : -1;
                        if (later >= 0) { full.RemoveAt(later); oldOf.RemoveAt(later); }
                        full.Insert(p, g);
                        oldOf.Insert(p, o);
                        fits.Add(later >= 0 ? $"goal {later + 1} ({g.label}) moved to {p + 1}" : $"{g.label} put in at goal {p + 1}");
                        Track(full);
                        origin = _origin.WithGoal(Fresh(full.Count, 0));
                        fitted = Replay(furthest.inputs);
                    }
                    if (fits.Count > 0)
                        Log($"route: the join's strays fitted ({fits.Count}: {string.Join(", ", fits.Take(3))}{(fits.Count > 3 ? ", ..." : "")}): it reaches goal {fitted.Progress} of {full.Count}"
                            + (fitted.Progress < full.Count ? $" ({fitted.EndReason})" : ""));
                    furthest.result = fitted;
                    if (fitted.Progress == full.Count) { written = furthest.inputs; r = fitted; break; }
                    if (bridges == JoinBridges) break;
                    // Bridge the goal it stops at: the joined way up to the goal before it, then the builder's way there.
                    int stop = fitted.Progress;
                    if (stop > at)
                    {
                        int keep = (int)fitted.GoalInputTicks[stop - 1];
                        basis = furthest.inputs.Take(keep).ToList();
                        Pad(basis, keep);
                        at = stop;
                    }
                    Track(full);
                    _eval.SetStart(origin, 0);
                    _eval.Goals.Limit = full.Count;
                    SimSnapshot there = _eval.Advance(basis);
                    // A piece search only when its box (if any) can be paid for now: a short one can't buy it whatever the walk.
                    bool search = there != null && (full[at].boxes == null || full[at].kind != FullTasGoal.Buy || Affordable(there, full[at].boxes) == true);
                    var bridged = there == null ? null : Play(there, basis, full.Skip(kept).Take(at - kept).ToList(), new[] { full[at] }, search);
                    Track(full);
                    Log($"route: the old route stops at goal {stop + 1} ({full[stop].label}: {fitted.EndReason}); "
                        + (bridged == null ? $"no way to goal {at + 1} ({full[at].label}) from there ({_whyNot})" : $"goal {at + 1} ({full[at].label}) bridged, on tick {bridged.Value.tick}"));
                    if (bridged == null) break;
                    basis = bridged.Value.inputs;
                    at++;
                    bridges++;
                    RunResult reach = Replay(basis);
                    if (reach.Progress > furthest.result.Progress) furthest = (basis, reach, 0);
                }
                if (written == route && furthest.result != null)
                {
                    _eval.SetStart(origin, 0);
                    _eval.Goals.Limit = full.Count;
                    SimSnapshot end = _eval.Advance(route);
                    courseScript running = null;
                    if (end != null) { _eval.Restore(end); running = _eval.TimingCourse(); }
                    // The furthest join is kept when it gets past the plan's end: a later stretch that breaks (a buy walk that
                    // now takes a box it can pay for) is planned again from there, on top of this one.
                    if (furthest.result.Progress > reached)
                    {
                        written = furthest.inputs;
                        r = furthest.result;
                    }
                    Log($"route: the old route after {targetLabel} doesn't play on to the end from the plan's end"
                        + (running != null ? $" (Course {running.courseNumber} is still running there, so its Quick Restart goes to the course's reset point, not the old route's Checkpoint)" : "")
                        + $"; joined after {furthest.wait} ticks it stops at goal {furthest.result.Progress + 1} ({(furthest.result.Progress < full.Count ? full[furthest.result.Progress].label : "")}: {furthest.result.EndReason})"
                        + "; plan the stretch it stops in again, or solve the goals after it (the studio's Reach missing goals)");
                }
            }
            finally { _borrow = saved; }
            Track(full);
            origin = _origin.WithGoal(Fresh(full.Count, 0));
            var run = new Route { Inputs = written, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks, GoalHeld = r.GoalHeldFrames, EndReason = r.EndReason };
            // Its waits re-timed from the start: the plan's walks and the old way on were timed to another economy.
            if (run.Progress == full.Count && Retune(_eval, origin, run, written, 0, full.Count) is Route t && t.Progress == full.Count && t.Live < run.Live)
            {
                Log($"route: re-timed from the start, {run.LiveSeconds:0.00}s -> {t.LiveSeconds:0.00}s");
                run = t;
            }
            Log($"route: reaches {run.Progress} of {full.Count} goals" + (run.Progress == full.Count ? $" in {run.LiveSeconds:0.00}s live ({run.Last} ticks)" : ""));
            return (run, full.Select(g => g.label).ToArray(), oldOf.ToArray());
        }

        /// <summary>A saved course route's inputs from the quick restart the planner makes: a standing one as it is, a restart
        /// one (a farm route's laps from its own quick restart) without its first tick, that restart.</summary>
        static List<InputTick> LapInputs(Seed s) => s.Restart ? s.Inputs.Skip(1).ToList() : s.Inputs;

        /// <summary>
        /// Files the saved course routes (--seed files that run this course from standing at its start, or from a quick restart
        /// onto it) as lap pieces at the spot a quick restart from here lands: the gate's part as the lap's start piece, the rest
        /// as its finish piece, each checked by playing it there. A lap the route polished in the run is often slower than the course's own record
        /// (Course 1's farm route finishes 12 ticks sooner after the same quick restart), and the planner laps from the library.
        /// </summary>
        void SeedLaps(SimSnapshot at, List<InputTick> route, int reached, FullTasGoal[] lap)
        {
            var runs = _seeds.Where(s => (s.Standing || s.Restart) && s.Goals.Contains(lap[0].label) && s.Goals.Contains(lap[1].label)).ToList();
            if (runs.Count == 0) return;
            var restart = Restarts(at, route, reached).FirstOrDefault();
            if (restart.state == null) { Log("lap seeds: no quick restart from the stretch's start"); return; }
            List<InputTick> before = route.Concat(restart.inputs).ToList();
            int filed = 0;
            foreach (Seed s in runs)
            {
                var start = new Target { Goal = reached, Extra = new[] { lap[0] }, Key = lap[0].label };
                Step gate = Reaches(restart.state, before.Count, LapInputs(s), start);
                if (gate == null || gate.Next == null) { Log($"lap seeds: {s.Name} doesn't reach {lap[0].label} from the quick restart's spot at {Where(restart.state.WithGoal(Fresh(_goals, 0))).at} ({_whyNot})"); continue; }
                var finish = new Target { Goal = reached, Extra = new[] { lap[1] }, Key = lap[1].label };
                List<InputTick> afterGate = before.Concat(gate.Inputs).ToList();
                Step end = Reaches(gate.Next, afterGate.Count, LapInputs(s).Skip(gate.Inputs.Count).ToList(), finish);
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

        /// <summary>The physics tick of each goal the last <see cref="Play"/> that played reached (realize measures a lap's walk to its gate).</summary>
        long[] _playedTicks;

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
            var ticks = new long[goals.Length];
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
                tick = ticks[k] = step.Physics;
            }
            _playedTicks = ticks;
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
        /// the whole rest of the stretch (Course 2's route crossed its laps in 2.44-2.70 s against the 2.20 s record). Each saved
        /// course route (--seed) that runs this course from standing at its reset point is played to the finish from two starts: the
        /// gate crossed with the library's piece, then a quick restart (the course is running: it lands at the reset point); and a
        /// quick restart right away, which lands at the checkpoint the run last touched (after this course's finish, its reset point:
        /// the route crosses the gate itself, no walk to it first), or, right after one, where it landed. The one that leaves the
        /// lowest best time wins (then the soonest).
        /// Slower than a farm lap; the planner's estimate weighs the clones' income after it. Without <paramref name="improve"/> a
        /// lap on the record's path once the record is set (the plan's later `record:N` steps): the same ways, kept when they don't
        /// lower the best time.
        /// </summary>
        (List<InputTick> inputs, SimSnapshot state, long tick)? RecordLap(SimSnapshot at, List<InputTick> route, List<FullTasGoal> planned, FullTasGoal[] lap, int course,
            bool improve = true)
        {
            var runs = _seeds.Where(s => (s.Standing || s.Restart) && s.Goals.Contains(lap[0].label) && s.Goals.Contains(lap[1].label)).ToList();
            if (runs.Count == 0) return null;
            var list = _kept.Concat(planned).Concat(lap).ToList();
            int i = list.Count - 1;
            var target = new Target { Goal = i, Key = lap[1].label };
            // A saved course route starts standing still: the game's run waits for three grounded ticks and its start alignment
            // before the first input. Idle ticks after the quick restart stand in for that wait; each route's pad is the first that
            // plays it to the finish, then tried first (the restart lands the same; another frame phase tries the rest).
            var starts = new List<(string how, List<InputTick> basis, List<InputTick> pre, SimSnapshot state)>();
            var gate = Play(at, route, planned, new[] { lap[0] }, false);
            long[] gateTick = _playedTicks;
            Track(list);
            float before = BestPath(at, course);
            if (gate is var (crossed, past, _))
                foreach (var (pre, restarted) in Restarts(past.WithGoal(Fresh(list.Count, i)), crossed, i).Take(1)) starts.Add(("", crossed, pre, restarted));
            // Right after a quick restart (the plan's restart to the course's reset point) the routes play from where it landed.
            // A route's pad is kept per start and spot (a restart elsewhere misses them all).
            if (route.Count > 0 && route[route.Count - 1].reset)
            {
                SimSnapshot landed = at.WithGoal(Fresh(list.Count, i - 1));
                var (spot, _) = Where(landed);
                starts.Add(($" from the restart before it at {spot.x:0},{spot.y:0}", route, new List<InputTick>(), landed));
            }
            else
                foreach (var (pre, restarted) in Restarts(at.WithGoal(Fresh(list.Count, i - 1)), route, i - 1).Take(1))
                {
                    var (spot, _) = Where(restarted);
                    starts.Add(($" from a restart at {spot.x:0},{spot.y:0}", route, pre, restarted));
                }
            (List<InputTick> inputs, SimSnapshot state, long tick, float best, string how)? pick = null;
            void Pick(string how, List<InputTick> basis, List<InputTick> pre, Step end)
            {
                float best = BestPath(end.Next, course);
                if (pick == null || best < pick.Value.best || best == pick.Value.best && end.Physics < pick.Value.tick)
                    pick = (basis.Concat(pre).Concat(end.Inputs).ToList(), end.Next, end.Physics, best, how == "" ? $" from a restart after the gate (crossed on tick {gateTick[0]})" : how);
            }
            var tried = new List<string>();
            foreach (var (how, basis, pre, restarted) in starts)
            {
                int played = tried.Count(t => !t.Contains(" misses "));
                foreach (Seed s in runs)
                {
                    Step end = null;
                    string key = s.Name + how;
                    bool cached = _recordPads.TryGetValue(key, out int found);
                    if (cached && found < 0) { tried.Add($"{key} misses {lap[1].label}"); continue; }
                    foreach (int idle in cached ? Enumerable.Range(0, MaxRecordPad + 1).Prepend(found).Distinct() : Enumerable.Range(0, MaxRecordPad + 1))
                    {
                        end = Reaches(restarted, basis.Count + pre.Count, Enumerable.Repeat(new InputTick(), idle).Concat(LapInputs(s)).ToList(), target);
                        if (end != null) { found = idle; break; }
                    }
                    if (end != null || !cached) _recordPads[key] = end == null ? -1 : found;
                    if (end == null)
                    {
                        Read(restarted);
                        string pose = $"restart at {_eval.PlayerWorld.x:0.00},{_eval.PlayerWorld.y:0.00} facing {(_eval.Player.facingRight ? "right" : "left")}, grounded {_eval.Player.onGround}";
                        tried.Add($"{key} misses {lap[1].label} ({Replayed(restarted, basis.Count + pre.Count, s.Inputs, target)}; {pose})");
                        continue;
                    }
                    tried.Add($"{key} {BestPath(end.Next, course):0.00}s after {found} idle ticks");
                    Pick(how, basis, pre, end);
                }
                if (tried.Count(t => !t.Contains(" misses ")) == played)
                    Log($"record lap: no saved route plays{(how == "" ? " after the gate" : how)}: " + string.Join("; ", tried.Skip(tried.Count - runs.Count).Take(2)));
            }
            // A saved route that misses from here (Course 2's 2.20 s lap takes its spikes on their last grace tick, and a restart's
            // start differs from its recording in the last hundredths) is searched once per course from the restart: seeded with
            // those routes, the search keeps the way that finishes soonest from standing there, which is the gate crossed at
            // speed. Its way is then tried with the saved routes at every later record lap.
            if (!_recordSearched.Contains(course) && starts.Count > 0 && tried.Any(t => t.Contains(" misses ")))
            {
                _recordSearched.Add(course);
                var (how, basis, pre, restarted) = starts[0];
                Step found = SolvePiece(restarted, basis.Concat(pre).ToList(), i, target, _pieceMinutes * 3, BuildStall, runs.Select(LapInputs).ToList());
                if (found != null)
                {
                    Log($"record lap: searched from the restart, a lap sets the best time to {BestPath(found.Next, course):0.00}s (finish at tick {found.Physics})");
                    _recordWays.Add((course, found.Inputs));
                    Pick(how, basis, pre, found);
                }
            }
            else
                foreach (var (how, basis, pre, restarted) in starts)
                    foreach (var (c, way) in _recordWays)
                    {
                        if (c != course) continue;
                        Step end = Reaches(restarted, basis.Count + pre.Count, way, target);
                        if (end != null) Pick(how, basis, pre, end);
                    }
            if (!_recordTried) { _recordTried = true; Log("record lap seeds: " + string.Join("; ", tried)); }
            if (pick == null || improve && pick.Value.best >= before) return null;
            Log($"record lap: best time {before:0.00}s -> {pick.Value.best:0.00}s, finish at tick {pick.Value.tick}{pick.Value.how}");
            return (pick.Value.inputs, pick.Value.state, pick.Value.tick);
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

        /// <summary>Restores s to read the economy: its goal state can be another plan's, so it gets one the tracker holds.</summary>
        void Read(SimSnapshot s) { _eval.Restore(s.WithGoal(Fresh(_goals, 0))); }
        double Cash(SimSnapshot s) { Read(s); return _eval.Shop.CashNow; }

        bool? Affordable(SimSnapshot s, string[] boxes) { Read(s); return _eval.Shop.Affordable(boxes); }

        static readonly Regex CostText = new Regex(@"costs (\S+)");

        // ponytail: reads the box's cost from Shop.Describe's log text (4 digits); a Shop accessor if the planner needs more.
        double Cost(SimSnapshot s, FullTasGoal g)
        {
            Read(s);
            Match m = CostText.Match(_eval.Shop.Describe(g.boxes));
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0.0;
        }
    }
}
