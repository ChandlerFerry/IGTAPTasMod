using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using UnityEngine;
using Caps = IGTAPTasMod.Caps;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using EconomyPlanResult = IGTAP.EngineSim.Cli.PlanResult;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>One step of an economy plan: a lap (`lap:N`), a record lap (`record:N`, on the saved record's path), a buy of
    /// one box or of a set on one walk (`buy:a + buy:b`), a reward box (`reward:bonusN`), a quick restart (`restart@x,y`) or the
    /// target's buy. Arg: the step's id, as `--compare` names it; Ticks: physics ticks it takes.</summary>
    sealed record Step(string Kind, string Arg, int Ticks);

    /// <summary>What <see cref="EconomyPlan.Search(Simulation, SimSnapshot, MoveTable, string, string[], string[], int, Func{string, FullTasGoal}, double, IReadOnlyList{string}, Action{string})"/>
    /// planned. Tick: the target's physics tick; Goals: the Full-TAS goals the steps make (a lap's gate and finish, each buy and
    /// reward); Estimated: each move the plan takes that nothing measured. Proven: the queue reached the target before a cap
    /// (else the schedule, if any, is the best a capped search completed by standing at the target).</summary>
    sealed class PlanResult
    {
        public bool Done, Proven;
        public long Tick;
        public Step[] Steps = Array.Empty<Step>();
        public string[] Goals = Array.Empty<string>(), Estimated = Array.Empty<string>();
        /// <summary>Per step, the tick it ends on and the Cash held then.</summary>
        public long[] Ends = Array.Empty<long>();
        public double[] Cash = Array.Empty<double>();
        public int States, Dropped;
        /// <summary>Per step kind: steps played, those that didn't play (a buy short of Cash), dominated, and seconds playing them.</summary>
        public SortedDictionary<string, (int played, int failed, int dominated, double seconds)> Profile = new SortedDictionary<string, (int, int, int, double)>();
        public double Seconds;
        /// <summary>Why it isn't done or proven (null: it is).</summary>
        public string Why;
    }

    /// <summary>The economy the search plays steps on (a seam: <see cref="SimEconomy"/> is the game's, plan-selftest's toy a
    /// test's). Idle: ticks standing at a place; Buy: one tick touching a box (false: it didn't buy); Finish: one tick finishing a
    /// course (its argument the economy's own: `N@ticks` for the game); Holds: every currency; Key: what besides the place two
    /// states must share for the richer earlier one to stand for the other (unlocks, box levels, records).</summary>
    interface IEconomy<TSnap>
    {
        TSnap Capture();
        void Restore(TSnap s);
        void Idle(int ticks, string place);
        bool Buy(string box);
        void Finish(string course);
        double[] Holds();
        string Key();
    }

    /// <summary>A step out of a place as the search plays it: Ticks less its events standing at End, then each Buys box a tick
    /// (every one must buy: a strict buy has its Cash on arrival), then Finish a tick. Target: the plan's last buy. Stand: the
    /// target's last resort (no lap from the place to earn on): its buy retried a tick at a time until the Cash is there.</summary>
    sealed record PlanOption(Step Step, string End, string[] Buys, string Finish, bool Target, string[] Goals, string[] Estimated = null, bool Stand = false);

    /// <summary>
    /// fulltas-plan (docs/fulltas.md, "Economy planner"): the fastest schedule of laps, record laps, buys, bonuses and quick
    /// restarts from a goal of a Full-TAS run's best to a target buy. Dijkstra over whole schedules: a state is the tick, the
    /// place and the economy (the engine's own, driven without the player: Simulation.EconomyIdle/Buy/Finish), a step's ticks
    /// the movement table's (MoveTable). Steps are pushed unplayed at their end tick (known from the table) and played when
    /// they come off the queue, so nothing past the target's tick is ever simulated. A state is dropped when one kept at the
    /// same place, unlocks, box levels and course records is no later and holds at least as much of every currency. No lower
    /// bound (plain Dijkstra): an income bound that is admissible needs the most any state can earn, which isn't known.
    /// </summary>
    static class EconomyPlan
    {
        /// <summary>Most ticks the target's last resort stands at its box.</summary>
        const int StandCap = 50000;
        /// <summary>A capped search completes this many of its richest states by standing at the target.</summary>
        const int Completions = 16;

        public static int Run(string[] args) => FullTas.EconomyPlanCommand(args);

        sealed class Node<TSnap>
        {
            public long Tick;
            public string Place, Key;
            public TSnap Snap;
            public Node<TSnap> Parent;
            public PlanOption Via;
            public double[] Holds;
            public bool Done, Dead;
            /// <summary>Its steps still in the queue: at 0 its snapshot is let go (the search's memory is the queue's front).</summary>
            public int Pending;
        }

        /// <summary>Plays one option from the economy's state at <paramref name="tick"/>; the tick it ends on, null when a buy
        /// didn't buy.</summary>
        internal static long? Play<TSnap>(IEconomy<TSnap> economy, long tick, PlanOption o)
        {
            int events = o.Buys.Length + (o.Finish != null ? 1 : 0);
            int walk = Math.Max(0, o.Step.Ticks - events);
            if (walk > 0) economy.Idle(walk, o.End);
            tick += walk;
            for (int i = 0; i < o.Buys.Length; i++)
            {
                tick++;
                if (economy.Buy(o.Buys[i])) continue;
                if (!o.Stand || i < o.Buys.Length - 1) return null;
                for (int w = 0; ; w++)
                {
                    if (w == StandCap) return null;
                    tick++;
                    if (economy.Buy(o.Buys[i])) break;
                }
            }
            if (o.Finish != null)
            {
                economy.Finish(o.Finish);
                tick++;
            }
            return tick;
        }

        /// <summary>
        /// The search over any economy from its current state (the start, at <paramref name="startTick"/> and
        /// <paramref name="startPlace"/>). <paramref name="options"/>: the steps out of a place, asked with the economy at that
        /// state. <paramref name="prefix"/>: step ids played first, exactly (`--compare`), the search finishing from there.
        /// <paramref name="stand"/>: the target's stand-until-affordable option from a place, for completing a capped search.
        /// </summary>
        internal static PlanResult Search<TSnap>(IEconomy<TSnap> economy, long startTick, string startPlace, Func<string, IEnumerable<PlanOption>> options,
            int maxStates, double maxSeconds, IReadOnlyList<string> prefix = null, Action<string> log = null, Func<string, PlanOption> stand = null)
        {
            var clock = Stopwatch.StartNew();
            var result = new PlanResult();
            var queue = new PriorityQueue<(Node<TSnap> parent, PlanOption via, Node<TSnap> node), (long tick, long seq)>();
            var kept = new Dictionary<string, List<Node<TSnap>>>();
            long seq = 0;

            Node<TSnap> Made(Node<TSnap> parent, PlanOption via, long tick, string place) => new Node<TSnap>
            {
                Tick = tick, Place = place, Snap = economy.Capture(), Parent = parent, Holds = economy.Holds(), Key = place + "|" + economy.Key(),
                Via = via == null ? null : via with { Step = via.Step with { Ticks = (int)(tick - parent.Tick) } }, Done = via?.Target == true,
            };
            void Expand(Node<TSnap> n)
            {
                foreach (PlanOption o in options(n.Place).ToList())
                {
                    n.Pending++;
                    queue.Enqueue((n, o, null), (n.Tick + o.Step.Ticks, seq++));
                }
            }
            void Count(string kind, int played, int failed, int dominated, double seconds)
            {
                result.Profile.TryGetValue(kind, out var p);
                result.Profile[kind] = (p.played + played, p.failed + failed, p.dominated + dominated, p.seconds + seconds);
            }
            long? Played(Node<TSnap> from, PlanOption o, out string why)
            {
                why = null;
                double t0 = clock.Elapsed.TotalSeconds;
                long? end = null;
                economy.Restore(from.Snap);
                result.States++;
                try { end = Play(economy, from.Tick, o); }
                catch (InvalidOperationException e) { why = e.Message; }
                Count(o.Step.Kind, 1, end == null ? 1 : 0, 0, clock.Elapsed.TotalSeconds - t0);
                return end;
            }
            // Same key, no later, at least as much of every currency: the kept one stands for it. Nodes come off the queue in tick
            // order, so every kept one is no later; one on the same tick holding no more is dropped in turn.
            bool Dominated(Node<TSnap> n)
            {
                if (!kept.TryGetValue(n.Key, out List<Node<TSnap>> list)) kept[n.Key] = list = new List<Node<TSnap>>();
                foreach (Node<TSnap> k in list)
                    if (k.Tick <= n.Tick && AtLeast(k.Holds, n.Holds)) return true;
                foreach (Node<TSnap> k in list)
                    if (k.Tick == n.Tick && AtLeast(n.Holds, k.Holds)) k.Dead = true;
                list.RemoveAll(k => k.Dead);
                list.Add(n);
                return false;
            }
            PlanResult Finish(Node<TSnap> done, bool proven, string why)
            {
                var path = new List<Node<TSnap>>();
                for (Node<TSnap> n = done; n?.Via != null; n = n.Parent) path.Add(n);
                path.Reverse();
                result.Done = done?.Done == true;
                result.Tick = result.Done ? done.Tick : -1;
                result.Proven = proven && result.Done;
                result.Steps = path.Select(n => n.Via.Step).ToArray();
                result.Goals = path.SelectMany(n => n.Via.Goals).ToArray();
                result.Estimated = path.SelectMany(n => n.Via.Estimated ?? Array.Empty<string>()).Distinct().ToArray();
                result.Ends = path.Select(n => n.Tick).ToArray();
                result.Cash = path.Select(n => n.Holds[0]).ToArray();
                result.Why = why;
                result.Seconds = clock.Elapsed.TotalSeconds;
                return result;
            }

            var root = Made(null, null, startTick, startPlace);
            Node<TSnap> at = root;
            for (int i = 0; prefix != null && i < prefix.Count; i++)
            {
                PlanOption o = options(at.Place).FirstOrDefault(x => x.Step.Arg == prefix[i]);
                if (o == null) return Finish(at, false, $"step {i + 1} {prefix[i]} isn't offered at {at.Place} on tick {at.Tick} (unknown move, or the box is capped or locked)");
                long? end = Played(at, o, out string why);
                if (end == null) return Finish(at, false, $"step {i + 1} {prefix[i]} doesn't play from {at.Place} on tick {at.Tick}: " + (why ?? "not enough Cash on arrival"));
                at = Made(at, o, end.Value, o.End);
                if (at.Done) return Finish(at, true, null);
            }
            Dominated(at);
            Expand(at);
            double lastLog = 0;
            long frontier = at.Tick;
            string capped = null;
            while (queue.TryDequeue(out var e, out var priority))
            {
                if (result.States >= maxStates || clock.Elapsed.TotalSeconds >= maxSeconds)
                {
                    capped = $"capped after {result.States} states and {clock.Elapsed.TotalSeconds:0} s at tick {frontier}";
                    break;
                }
                Node<TSnap> n = e.node;
                if (n == null)
                {
                    if (e.parent.Dead) continue;
                    long? end = Played(e.parent, e.via, out _);
                    if (--e.parent.Pending == 0 && e.parent.Parent != null) e.parent.Snap = default;
                    if (end == null) continue;
                    n = Made(e.parent, e.via, end.Value, e.via.End);
                    // A stand ends later than its walk: back in the queue at its real tick.
                    if (n.Tick > priority.tick) { queue.Enqueue((null, null, n), (n.Tick, seq++)); continue; }
                }
                else if (!n.Done) economy.Restore(n.Snap);
                frontier = n.Tick;
                if (Dominated(n)) { result.Dropped++; Count(n.Via.Step.Kind, 0, 0, 1, 0); continue; }
                if (n.Done) return Finish(n, true, null);
                Expand(n);
                if (log != null && clock.Elapsed.TotalSeconds - lastLog >= 15)
                {
                    lastLog = clock.Elapsed.TotalSeconds;
                    log($"plan: tick {frontier}, {result.States} states played, {result.Dropped} dominated, {kept.Count} keys, {queue.Count} queued, {GC.GetTotalMemory(false) / 1e9:0.0} GB; "
                        + string.Join(", ", result.Profile.Select(p => $"{p.Key} {p.Value.played}/{p.Value.failed}/{p.Value.dominated} {p.Value.seconds:0}s")));
                }
            }
            capped ??= "the queue ran out: no schedule reaches the target";
            if (stand == null) return Finish(at, false, capped);
            // Capped: the richest kept states, each finished by standing at the target until it can pay (not proven).
            Node<TSnap> best = null;
            foreach (Node<TSnap> k in kept.Values.SelectMany(l => l).Where(k => !k.Dead && !k.Done && k.Pending > 0).OrderByDescending(k => k.Holds[0]).Take(Completions))
            {
                PlanOption o = stand(k.Place);
                long? end = o == null ? null : Played(k, o, out _);
                if (end == null) continue;
                Node<TSnap> done = Made(k, o, end.Value, o.End);
                if (best == null || done.Tick < best.Tick) best = done;
            }
            return Finish(best ?? at, false, capped + (best == null ? "; no state completes by standing at the target" : $"; completed by standing at the target from the {Completions} richest states"));
        }

        static bool AtLeast(double[] a, double[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i] < b[i]) return false;
            return true;
        }

        /// <summary>
        /// The search on the engine's economy from <paramref name="start"/> (a Full-TAS state at <paramref name="startPlace"/>,
        /// a MoveTable place) until it buys the last of <paramref name="targets"/> (the ones before it bought first, in any
        /// order). Steps out of a place, with the unlocks held there (PlayerCaps): every lap and record lap the table has; a buy of
        /// each economy box (FullTasGoal.IsEconomy) or earlier target, and each set of them the table stores, that is active
        /// (a capped or locked box is never offered); each <paramref name="bonuses"/> reward box not yet bought; each quick
        /// restart landing the table saw from there; the target. A move nothing measured is an estimate, kept only toward a
        /// place the run first stood at with no more unlocks than these, and listed in the plan. <paramref name="goalOf"/>:
        /// a label's goal (its boxes).
        /// </summary>
        internal static PlanResult Search(Simulation sim, SimSnapshot start, MoveTable moves, string startPlace, string[] targets, string[] bonuses, int maxStates,
            Func<string, FullTasGoal> goalOf, double maxSeconds = double.PositiveInfinity, IReadOnlyList<string> prefix = null, Action<string> log = null, bool levels = false, ISet<string> only = null)
        {
            var economy = new SimEconomy(sim, moves, label => goalOf(label)?.boxes, levels);
            sim.RestoreForDrive(start);
            long startTick = sim.Engine.PhysicsTicks;
            string target = targets[targets.Length - 1];
            var before = targets.Take(targets.Length - 1).ToHashSet();
            var bought = targets.ToDictionary(t => t, t => economy.Level(t));
            var rewards = bonuses.ToHashSet();

            string Estimate(string from, Move m, string caps) => $"{from} -> {m.To} [{caps}] {m.Ticks} ticks";
            List<PlanOption> Options(string place, bool standOnly)
            {
                string caps = PlayerCaps.Of(sim).Id;
                Caps.TryParse(caps, out Caps have);
                var list = new List<PlanOption>();
                bool laps = false;
                bool Reachable(Move m, string to)
                {
                    if (!m.Estimated) return true;
                    string dest = to.StartsWith("lap:", StringComparison.Ordinal) || to.StartsWith("record:", StringComparison.Ordinal) ? "start:" + to.Substring(to.IndexOf(':') + 1) : to;
                    // A place's caps are the run's after its goal: an unlock box's own unlock counts.
                    return dest.Split(MoveTable.SetJoin).All(p => moves.Places.TryGetValue(p, out Place q) && (q.Caps == null
                        || Caps.TryParse(q.Caps, out Caps need) && need.IsSubsetOf(sim.Shop.AfterBuys(have, goalOf(p)?.boxes ?? Array.Empty<string>()))));
                }
                foreach (string to in moves.Targets(place, caps).Concat(moves.Restarts(place, caps)).Distinct().ToList())
                {
                    Move m = moves.Best(place, to, caps);
                    if (m == null || !Reachable(m, to)) continue;
                    var estimated = new List<string>();
                    if (m.Estimated) estimated.Add(Estimate(place, m, caps));
                    bool lap = to.StartsWith("lap:", StringComparison.Ordinal), record = to.StartsWith("record:", StringComparison.Ordinal);
                    if ((lap || record) && only != null && !only.Contains(to)) continue;
                    if (lap || record)
                    {
                        string n = to.Substring(to.IndexOf(':') + 1);
                        int course = lap ? LapCourseTicks(moves, place, n, m, caps, estimated) : RecordTicks(moves, n, caps, estimated);
                        if (course <= 0) continue;
                        laps = true;
                        if (!standOnly)
                            list.Add(new PlanOption(new Step(lap ? "lap" : "record", to, m.Ticks), "finish:" + n, Array.Empty<string>(), n + "@" + course, false,
                                new[] { "start:" + n, "finish:" + n }, estimated.ToArray()));
                        continue;
                    }
                    if (to.StartsWith("restart@", StringComparison.Ordinal))
                    {
                        if (!standOnly) list.Add(new PlanOption(new Step("restart", to, m.Ticks), to, Array.Empty<string>(), null, false, Array.Empty<string>(), estimated.ToArray()));
                        continue;
                    }
                    string[] boxes = to.Split(MoveTable.SetJoin);
                    // A goal buying several boxes at once (`buy:a+b`) is a place of the best's; the planner buys them as a set.
                    if (boxes.Any(b => b.Contains('+')) || !economy.Active(boxes)) continue;
                    bool isTarget = boxes.Length == 1 && to == target;
                    if (isTarget)
                    {
                        if (before.Any(t => economy.Level(t) == bought[t])) continue;
                        list.Add(new PlanOption(new Step("target", to, m.Ticks), to, boxes, null, true, boxes, estimated.ToArray()));
                    }
                    else if (boxes.Length == 1 && rewards.Contains(to))
                    {
                        if (!standOnly) list.Add(new PlanOption(new Step("reward", to, m.Ticks), to, boxes, null, false, boxes, estimated.ToArray()));
                    }
                    else if (!standOnly && boxes.All(b => b.StartsWith("buy:", StringComparison.Ordinal) && (FullTasGoal.IsEconomy(b) && (only == null || only.Contains(b)) || before.Contains(b))))
                        list.Add(new PlanOption(new Step("buy", to, m.Ticks), boxes[boxes.Length - 1], boxes, null, false, boxes, estimated.ToArray()));
                }
                // The target's last resort: nowhere to lap from here, so stand at its box until the Cash is there.
                if (!laps || standOnly)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].Target) list[i] = list[i] with { Stand = true };
                return standOnly ? list.Where(o => o.Target).ToList() : list;
            }
            PlanResult r = Search(economy, startTick, startPlace, p => Options(p, false), maxStates, maxSeconds, prefix, log, p => Options(p, true).FirstOrDefault());
            sim.RestoreForDrive(start);
            return r;
        }

        /// <summary>A lap's course timer, in ticks: the lap less its walk to the gate when both are measured (never shorter than
        /// the table's run from the gate to the finish), else that run; else the whole lap (listed as estimated).</summary>
        static int LapCourseTicks(MoveTable moves, string from, string n, Move lap, string caps, List<string> estimated)
        {
            string gate = "start:" + n;
            Move run = moves.Best(gate, "finish:" + n, caps);
            Move walk = from == gate ? new Move(from, gate, 0, "here", false) : moves.Best(from, gate, caps);
            int least = run != null && !run.Estimated ? run.Ticks : 1;
            if (!lap.Estimated && walk != null && !walk.Estimated) return Math.Max(least, lap.Ticks - walk.Ticks);
            if (run != null && !run.Estimated) return run.Ticks;
            estimated.Add($"lap:{n}'s course timer from {from} [{caps}]: the whole lap's {lap.Ticks} ticks");
            return lap.Ticks;
        }

        /// <summary>A record lap's course timer: the saved record's (MoveTable's `record:N` from the gate).</summary>
        static int RecordTicks(MoveTable moves, string n, string caps, List<string> estimated)
        {
            Move record = moves.Best("start:" + n, "record:" + n, caps);
            if (record == null) return -1;
            if (record.Estimated) estimated.Add($"record:{n}'s course timer [{caps}]: {record.Ticks} ticks, from the farm route");
            return record.Ticks;
        }

        /// <summary>A `--compare` order's step ids: split at commas, a piece that starts no step (a restart landing's or area
        /// goal's own commas) joined back to the one before.</summary>
        internal static List<string> StepIds(string order)
        {
            var ids = new List<string>();
            foreach (string piece in order.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                if (ids.Count > 0 && !char.IsLetter(piece[0])) ids[ids.Count - 1] += "," + piece;
                else ids.Add(piece);
            }
            return ids;
        }

        /// <summary>The course timer after <paramref name="ticks"/> ticks: courseScript.FixedUpdate's 0.02f added once a tick
        /// (docs/course-timer.md), the float sum a finish passes on.</summary>
        internal static float CourseTime(int ticks)
        {
            float t = 0f;
            for (int i = 0; i < ticks; i++) t += 0.02f;
            return t;
        }

        /// <summary>Writes a plan as text: its tick, then each step's end tick, ticks and Cash.</summary>
        internal static void Print(string title, PlanResult r)
        {
            Console.WriteLine($"{title}: " + (r.Done ? $"target on tick {r.Tick}" + (r.Proven ? "" : " (not proven)") : "not done")
                + $"; {r.States} states played, {r.Dropped} dominated, {r.Seconds:0} s" + (r.Why == null ? "" : "; " + r.Why));
            for (int i = 0; i < r.Steps.Length; i++)
                Console.WriteLine($"  {r.Ends[i],6} +{r.Steps[i].Ticks,-4} {r.Cash[i],12:0.##} Cash  {r.Steps[i].Kind,-7} {r.Steps[i].Arg}");
            if (r.Estimated.Length > 0) Console.WriteLine("  estimated: " + string.Join("; ", r.Estimated));
            Console.WriteLine("  profile (played/didn't play/dominated, seconds): " + string.Join(", ", r.Profile.Select(p => $"{p.Key} {p.Value.played}/{p.Value.failed}/{p.Value.dominated} {p.Value.seconds:0.0}s")));
        }
    }

    /// <summary>The engine's economy (Simulation's drive, Task 8) as the search's: a place is a MoveTable place (its x, y the
    /// player's stand: which courses' clones are on screen), a box a goal label (its goal's one box, at its place).</summary>
    sealed class SimEconomy : IEconomy<SimSnapshot>
    {
        static readonly FieldInfo BestPathTime = typeof(courseScript).GetField("bestPathTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        readonly Simulation _sim;
        readonly MoveTable _moves;
        readonly Func<string, string[]> _boxes;
        readonly upgradeBox[] _all;
        readonly Dictionary<string, string[]> _ids = new Dictionary<string, string[]>();

        /// <summary>Levels dominance (fulltas-plan --dominance levels): the key is the unlocks alone, and a state's box levels and
        /// course records (each best path time, negated: faster is more) are compared as its holdings are, so a state with at
        /// least every level, no slower a record and at least every currency stands for one with fewer.</summary>
        readonly bool _levels;

        public SimEconomy(Simulation sim, MoveTable moves, Func<string, string[]> boxes, bool levels = false)
        {
            _sim = sim;
            _moves = moves;
            _boxes = boxes;
            _levels = levels;
            _all = sim.World.All<upgradeBox>();
        }

        public SimSnapshot Capture() => _sim.Capture((int)_sim.Engine.RunTicks);
        public void Restore(SimSnapshot s) => _sim.RestoreForDrive(s);
        public void Idle(int ticks, string place) => _sim.EconomyIdle(ticks, At(place));

        public bool Buy(string label)
        {
            string[] ids = Ids(label);
            if (ids.Length != 1) throw new InvalidOperationException($"{label}: {ids.Length} boxes, the planner buys one a tick");
            return _sim.EconomyBuy(ids[0], At(label));
        }

        public void Finish(string course)
        {
            int at = course.IndexOf('@');
            _sim.EconomyFinish(int.Parse(course.Substring(0, at), CultureInfo.InvariantCulture), EconomyPlan.CourseTime(int.Parse(course.Substring(at + 1), CultureInfo.InvariantCulture)));
        }

        public double[] Holds()
        {
            var held = new double[7 + (_levels ? _all.Length + _sim.Courses.Length : 0)];
            for (int c = 0; c < 7; c++) held[c] = _sim.Shop.Holds((globalStats.Currencies)c);
            if (!_levels) return held;
            for (int b = 0; b < _all.Length; b++) held[7 + b] = _all[b].TimesUsed;
            for (int c = 0; c < _sim.Courses.Length; c++) held[7 + _all.Length + c] = -(float)BestPathTime.GetValue(_sim.Courses[c]);
            return held;
        }

        public string Key()
        {
            if (_levels) return PlayerCaps.Of(_sim).Id;
            return PlayerCaps.Of(_sim).Id + "|" + string.Join(",", _all.Select(b => b.TimesUsed))
                + "|" + string.Join(",", _sim.Courses.Select(c => ((float)BestPathTime.GetValue(c)).ToString("R", CultureInfo.InvariantCulture)));
        }

        /// <summary>Every box of these labels can be bought once Cash allows (Shop.Affordable: not capped, unlocked, shown).</summary>
        public bool Active(string[] labels) => labels.All(l => Ids(l).Length > 0 && _sim.Shop.Affordable(Ids(l)) != null);

        /// <summary>How many times a label's boxes were bought, all told.</summary>
        public int Level(string label) => _all.Where(b => _sim.Shop.Named(Ids(label), b)).Sum(b => b.TimesUsed);

        string[] Ids(string label)
        {
            if (!_ids.TryGetValue(label, out string[] ids)) _ids[label] = ids = _boxes(label) ?? Array.Empty<string>();
            return ids;
        }

        Vector2 At(string place)
        {
            if (!_moves.Places.TryGetValue(place, out Place p)) throw new InvalidOperationException("no place " + place + " in the movement table");
            ScenePos s = _sim.ToScene(new WorldPos((float)p.X, (float)p.Y));
            return new Vector2(s.x, s.y);
        }
    }

    sealed partial class FullTas
    {
        internal static int EconomyPlanCommand(string[] args) => new FullTas(args).PlanEconomy(args);

        /// <summary>
        /// fulltas-plan &lt;trace&gt; --from &lt;goal&gt; --target &lt;buy:box&gt;[,...] [--bonuses bonus1,...] [--buytable FILE]
        /// [--moves FILE] [--max-states N] [--minutes M] [--out FILE] [--compare "step,step,..." ...]: the economy planner from the
        /// best's state at goal --from (its place that goal's) to the target buy; prints the schedule and, per --compare prefix
        /// (played exactly, then searched on), its target tick beside the free search's.
        /// </summary>
        int PlanEconomy(string[] args)
        {
            Open();
            int kept = GoalNumber(Solve.Option(args, "--from", ""), 1);
            if (kept < 1 || kept > _goals || kept > _best.Progress) throw new ArgumentException($"--from wants a goal the best reaches (1..{_best.Progress})");
            string[] targets = Solve.Option(args, "--target", "buy:buyds").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string[] bonuses = (Solve.Option(args, "--bonuses", "") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(b => b.StartsWith("reward:", StringComparison.Ordinal) ? b : "reward:" + b).ToArray();
            string buyTable = Solve.Option(args, "--buytable", BuyTableFile), movesFile = Solve.Option(args, "--moves", MovesFile);
            int maxStates = Solve.Option(args, "--max-states", 200000);
            bool levels = Solve.Option(args, "--dominance", "key") == "levels";
            // --only: the laps, record laps and economy boxes offered (default: every one the table has).
            string onlyArg = Solve.Option<string>(args, "--only", null);
            HashSet<string> only = onlyArg?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            string outFile = Solve.Option<string>(args, "--out", null);
            var compares = new List<string>();
            int c = Array.IndexOf(args, "--compare");
            for (int i = c + 1; c >= 0 && i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal); i++) compares.Add(args[i]);
            // --skip-free: only the --compare orders (one process per order runs them side by side).
            bool skipFree = args.Contains("--skip-free");
            FullTasGoal GoalOf(string label) => _runGoals.Goals.FirstOrDefault(g => g.label == label) ?? NewGoal(label)?.goal;
            foreach (string b in bonuses.Concat(targets))
                if (GoalOf(b) == null) throw new ArgumentException($"{b}: no such goal (a box token of FullTasGoal.Boxes, or a bonus of courses.json)");

            MoveTable moves = MoveTable.Load(this, File.Exists(buyTable) ? buyTable : null, RoutesFile, _best, movesFile);
            int length = (int)Math.Min(_best.GoalInput[kept - 1], _best.Inputs.Count);
            var route = _best.Inputs.Take(length).ToList();
            if (route.Count < length) Pad(route, length);
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            SimSnapshot at = _eval.Advance(route);
            if (at == null || at.Goal.Next != kept) throw new InvalidOperationException($"the best doesn't replay to goal {kept}");
            string place = _labels[kept - 1];
            if (!moves.Places.ContainsKey(place)) throw new InvalidOperationException($"goal {kept} {place} is no place of the movement table");
            int targetGoal = Array.IndexOf(_labels, targets[targets.Length - 1], kept) + 1;
            string bestTick = targetGoal > 0 && targetGoal <= _best.Progress ? _best.GoalPhysics[targetGoal - 1].ToString(CultureInfo.InvariantCulture) : "-";
            Log($"plan from goal {kept} {place} (tick {_best.GoalPhysics[kept - 1]}) to {string.Join(", then ", targets)} (the best's: tick {bestTick}); bonuses {string.Join(" ", bonuses)}; "
                + $"{moves.Count} moves");

            double seconds = Solve.Option(args, "--minutes", 30.0) * 60;
            EconomyPlanResult Plan(IReadOnlyList<string> prefix) =>
                EconomyPlan.Search(_eval, at, moves, place, targets, bonuses, maxStates, GoalOf, seconds, prefix, Log, levels, only);
            EconomyPlanResult free = skipFree ? new EconomyPlanResult { Why = "--skip-free" } : Plan(null);
            if (!skipFree) EconomyPlan.Print("free search", free);
            var rows = new List<(string name, EconomyPlanResult plan)>();
            if (!skipFree) rows.Add(("free search", free));
            foreach (string prefix in compares)
            {
                EconomyPlanResult r = Plan(EconomyPlan.StepIds(prefix));
                EconomyPlan.Print(prefix, r);
                rows.Add((prefix, r));
            }
            Console.WriteLine();
            Console.WriteLine($"{"order (then searched on)",-48} {"target tick",11} {"vs free",8}  proven");
            Console.WriteLine($"{"the best's own",-48} {bestTick,11}");
            foreach (var (name, r) in rows)
                Console.WriteLine($"{name,-48} {(r.Done ? r.Tick.ToString(CultureInfo.InvariantCulture) : "-"),11} {(r.Done && free.Done ? (r.Tick - free.Tick).ToString("+0;-0;0", CultureInfo.InvariantCulture) : ""),8}  {(r.Proven ? "yes" : "no")}"
                    + (r.Why == null ? "" : "  (" + r.Why + ")"));
            if (outFile != null)
            {
                object Json(string name, EconomyPlanResult r) => new
                {
                    name, done = r.Done, proven = r.Proven, tick = r.Tick, why = r.Why, states = r.States, dropped = r.Dropped, seconds = r.Seconds,
                    steps = r.Steps.Select((s, i) => new { kind = s.Kind, arg = s.Arg, ticks = s.Ticks, end = r.Ends[i], cash = r.Cash[i] }), goals = r.Goals, estimated = r.Estimated,
                };
                File.WriteAllText(outFile, JsonSerializer.Serialize(new
                {
                    run = _name, from = kept, fromLabel = place, startTick = _best.GoalPhysics[kept - 1], targets, bonuses, best = bestTick,
                    plans = rows.Select(r => Json(r.name, r.plan)),
                }, new JsonSerializerOptions { WriteIndented = true }));
                Log("plan -> " + outFile);
            }
            return rows.All(r => r.plan.Done) ? 0 : 3;
        }
    }
}
