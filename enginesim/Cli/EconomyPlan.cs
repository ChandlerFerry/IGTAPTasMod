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
    /// reward); Estimated: each move the plan takes that nothing measured. Proven: the queue reached the target before a cap,
    /// so no schedule reaches it sooner under the planner's state key (hidden economy state such as clones in flight, box
    /// cooldowns or the reward refresh's phase isn't compared); else the schedule, if any, is the best a capped search
    /// completed by standing at the target.</summary>
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
        /// <summary>A capped search's states finished by standing at the target.</summary>
        public int Completed;
        /// <summary>Per step kind: steps played, those that didn't play (a buy short of Cash), dominated, and seconds playing them.</summary>
        public SortedDictionary<string, (int played, int failed, int dominated, double seconds)> Profile = new SortedDictionary<string, (int, int, int, double)>();
        public double Seconds;
        /// <summary>Why it isn't done or proven (null: it is).</summary>
        public string Why;
        /// <summary>Distinct keys (place and economy key) the search saw.</summary>
        public int Keys;
        /// <summary>Beam (Width above 0): states let in, states cut by the width (turned away or dropped for a better one), buys
        /// the pre-check didn't queue, states dropped for no income toward the target, and the most memory seen in use
        /// (GC.GetTotalMemory, sampled).</summary>
        public int Width, Nodes, Pruned, Prechecked, Unreachable;
        public long PeakBytes;
    }

    /// <summary>The economy the search plays steps on (a seam: <see cref="SimEconomy"/> is the game's, plan-selftest's toy a
    /// test's). Idle: ticks standing at a place; Buy: one tick touching a box (false: it didn't buy); Finish: one tick finishing a
    /// course (its argument the economy's own: `N@ticks` for the game); Holds: every currency; Key: what besides the place two
    /// states must share for the richer earlier one to stand for the other (unlocks, box levels, records). Earned: every
    /// currency gained so far, spending left out; Price: what a box costs now, per currency as Holds counts them (null: it
    /// can't be bought now). The beam's estimate and buy pre-check read those two.</summary>
    interface IEconomy<TSnap>
    {
        TSnap Capture();
        void Restore(TSnap s);
        void Idle(int ticks, string place);
        bool Buy(string box);
        void Finish(string course);
        double[] Holds();
        string Key();
        double[] Earned();
        double[] Price(string box);
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
    /// same place, unlocks, box levels, course records and running buy-max routines is no later and holds at least as much of
    /// every currency; the rest of the economy (clones in flight and their path progress, spawn timers, box cooldowns, the
    /// reward refresh's phase, scheduled Invokes, the frame grid's phase) isn't compared, so a plan is optimal under that
    /// key, not over every state the game can be in (realize measures the real one). No lower
    /// bound (plain Dijkstra): an income bound that is admissible needs the most any state can earn, which isn't known. For
    /// targets that search can't finish, --mode beam keeps at most a width of states per BeamBucket ticks, ranked by tick and
    /// income (Search's Beam): a plan, not proven optimal.
    /// </summary>
    static class EconomyPlan
    {
        /// <summary>fulltas-plan --beam's default width (docs/fulltas.md, "Economy planner": chosen by measurement).</summary>
        internal const int DefaultWidth = 200;
        /// <summary>Most ticks the target's last resort stands at its box.</summary>
        const int StandCap = 50000;
        /// <summary>Beam: most ticks the target's stand waits past its walk (a longer wait is a later state's: the beam goes on
        /// lapping and buying, and a stand from there is shorter).</summary>
        const int BeamStand = 2000;
        /// <summary>Beam: the width is per this many ticks of plan (fulltas-plan --beam; docs/fulltas.md "Economy planner").</summary>
        const int BeamBucket = 10;
        /// <summary>A capped search completes this many of its richest states by standing at the target.</summary>
        const int Completions = 16;

        public static int Run(string[] args) => FullTas.EconomyPlanCommand(args);

        /// <summary>Beam: a node's income is what it gained over its last steps back to the first that covers this many ticks
        /// (Earned's change over the ticks: laps' rewards and the clones' payouts, spending left out).</summary>
        const int IncomeWindow = 300;
        /// <summary>Beam: a buy (or the target's stand) is played only if its Cash can be there in time at this many times the
        /// node's income (the buy pre-check).</summary>
        const double PrecheckMargin = 2;

        sealed class Node<TSnap>
        {
            public long Tick;
            public string Place;
            /// <summary>Its place and economy key, as the search's id for it: the key's string is kept once, not per node.</summary>
            public int Key;
            public TSnap Snap;
            public Node<TSnap> Parent;
            /// <summary>The option that made it, shared with every node it made (the step's ticks are Tick less the parent's).</summary>
            public PlanOption Via;
            /// <summary>Every currency held, and (beam) gained so far, spending left out.</summary>
            public double[] Holds, Earned;
            public bool Done, Dead;
            /// <summary>Its steps still in the queue: at 0 its snapshot is let go (the search's memory is the queue's front).</summary>
            public int Pending;
            /// <summary>Beam: its tick plus the estimate of the ticks to the target.</summary>
            public double F;
        }

        /// <summary>Plays one option from the economy's state at <paramref name="tick"/>; the tick it ends on, null when a buy
        /// didn't buy (or a stand would end on <paramref name="limit"/> or later).</summary>
        internal static long? Play<TSnap>(IEconomy<TSnap> economy, long tick, PlanOption o, long limit = long.MaxValue)
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
                    if (w == StandCap || tick >= limit) return null;
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
        /// <paramref name="width"/> above 0: the beam (fulltas-plan --mode beam) instead of Dijkstra, <paramref name="remaining"/>
        /// the targets' price still to pay and the table's ticks to the next one from a place, asked with the economy at that state.
        /// </summary>
        internal static PlanResult Search<TSnap>(IEconomy<TSnap> economy, long startTick, string startPlace, Func<string, IEnumerable<PlanOption>> options,
            int maxStates, double maxSeconds, IReadOnlyList<string> prefix = null, Action<string> log = null, Func<string, PlanOption> stand = null,
            int width = 0, Func<string, (double[] price, int ticks)> remaining = null)
        {
            var clock = Stopwatch.StartNew();
            var result = new PlanResult { Width = width };
            var queue = new PriorityQueue<(Node<TSnap> parent, PlanOption via, Node<TSnap> node), (long tick, long seq)>();
            var kept = new Dictionary<int, List<Node<TSnap>>>();
            var keys = new Dictionary<string, int>();
            long seq = 0;

            Node<TSnap> Made(Node<TSnap> parent, PlanOption via, long tick, string place)
            {
                string key = place + "|" + economy.Key();
                if (!keys.TryGetValue(key, out int id)) keys[key] = id = keys.Count;
                return new Node<TSnap>
                {
                    Tick = tick, Place = place, Snap = economy.Capture(), Parent = parent, Holds = economy.Holds(), Earned = width > 0 ? economy.Earned() : null,
                    Key = id, Via = via, Done = via?.Target == true,
                };
            }
            // beam: the pre-check's buys left out (the economy is at n's state here).
            void Expand(Node<TSnap> n, bool beam = false)
            {
                foreach (PlanOption o in options(n.Place).ToList())
                {
                    if (beam && Short(n, o)) { result.Prechecked++; continue; }
                    n.Pending++;
                    queue.Enqueue((n, o, null), (n.Tick + o.Step.Ticks, seq++));
                }
            }
            void Count(string kind, int played, int failed, int dominated, double seconds)
            {
                result.Profile.TryGetValue(kind, out var p);
                result.Profile[kind] = (p.played + played, p.failed + failed, p.dominated + dominated, p.seconds + seconds);
            }
            long? Played(Node<TSnap> from, PlanOption o, out string why, long limit = long.MaxValue)
            {
                why = null;
                double t0 = clock.Elapsed.TotalSeconds;
                long? end = null;
                economy.Restore(from.Snap);
                result.States++;
                try { end = Play(economy, from.Tick, o, limit); }
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
                    if (!k.Dead && k.Tick <= n.Tick && AtLeast(k.Holds, n.Holds)) return true;
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
                result.Steps = path.Select(n => n.Via.Step with { Ticks = (int)(n.Tick - n.Parent.Tick) }).ToArray();
                result.Goals = path.SelectMany(n => n.Via.Goals).ToArray();
                result.Estimated = path.SelectMany(n => n.Via.Estimated ?? Array.Empty<string>()).Distinct().ToArray();
                result.Ends = path.Select(n => n.Tick).ToArray();
                result.Cash = path.Select(n => n.Holds[0]).ToArray();
                result.Why = why;
                result.Seconds = clock.Elapsed.TotalSeconds;
                result.Keys = keys.Count;
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
            if (width > 0) return Beam(at);
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
                    bool last = --e.parent.Pending == 0 && e.parent.Parent != null;
                    if (e.parent.Dead)
                    {
                        if (last) e.parent.Snap = default;
                        continue;
                    }
                    long? end = Played(e.parent, e.via, out _);
                    if (last) e.parent.Snap = default;
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
                economy.Restore(k.Snap);
                PlanOption o = stand(k.Place);
                long? end = o == null ? null : Played(k, o, out _);
                if (end == null) continue;
                Node<TSnap> done = Made(k, o, end.Value, o.End);
                result.Completed++;
                if (best == null || done.Tick < best.Tick) best = done;
            }
            return Finish(best ?? at, false, capped + (best == null ? "; no state completes by standing at the target" : $"; completed by standing at the target from the {Completions} richest states"));

            // Where n's income is measured from: its last steps back to the first that covers IncomeWindow ticks (n itself at the
            // start, before any step).
            Node<TSnap> Window(Node<TSnap> n)
            {
                Node<TSnap> a = n;
                while (a.Parent != null && n.Tick - a.Tick < IncomeWindow) a = a.Parent;
                return a;
            }
            // n's income of a currency per tick over its window (0 with no window yet: only zero-tick steps since the start).
            double Rate(Node<TSnap> n, Node<TSnap> a, int c) => n.Tick == a.Tick ? 0 : (n.Earned[c] - a.Earned[c]) / (n.Tick - a.Tick);
            // The beam's h: the slowest currency's missing price (every target not bought yet) over n's income, plus the table's
            // ticks to the next target. Null when a currency still missing came in at 0 over a window of ticks (unreachable
            // without a buy). No window yet: the ticks alone.
            double? Estimate(Node<TSnap> n)
            {
                var (price, ticks) = remaining(n.Place);
                Node<TSnap> a = Window(n);
                double wait = 0;
                for (int c = 0; c < price.Length; c++)
                {
                    double missing = price[c] - n.Holds[c];
                    if (missing <= 0 || n.Tick == a.Tick) continue;
                    double rate = Rate(n, a, c);
                    if (rate <= 0) return null;
                    wait = Math.Max(wait, missing / rate);
                }
                return wait + ticks;
            }
            // The buy pre-check, with the economy at n: the option's boxes cost more than n holds plus PrecheckMargin times n's
            // income over the step's ticks (the target's stand: and BeamStand more). A buy right after a buy is always played
            // (what it bought, a clone, may pay before the window shows it); a stand always checked.
            bool Short(Node<TSnap> n, PlanOption o)
            {
                if (o.Buys.Length == 0) return false;
                Node<TSnap> a = Window(n);
                if (n.Tick == a.Tick || !o.Stand && n.Via?.Buys.Length > 0) return false;
                double[] need = null;
                foreach (string b in o.Buys)
                {
                    double[] p = economy.Price(b);
                    if (p == null) return false;
                    need ??= new double[p.Length];
                    for (int c = 0; c < p.Length; c++) need[c] += p[c];
                }
                double ticks = o.Step.Ticks + (o.Stand ? BeamStand : 0);
                for (int c = 0; c < need.Length; c++)
                {
                    double missing = need[c] - n.Holds[c];
                    if (missing > 0 && missing > PrecheckMargin * Rate(n, a, c) * ticks) return true;
                }
                return false;
            }

            // fulltas-plan --mode beam: the search above (Dijkstra's tick order, the steps played as they come off the queue, the
            // same dominance), with at most `width` states let in per BeamBucket ticks of plan: a state past that is let in only
            // if its tick + Estimate beats the bucket's worst, which is dropped (marked dead: its queued steps are skipped). The
            // states a bucket compares are at about the same tick, so h ranks them by how soon their income pays the rest. A step
            // the pre-check says can't pay isn't queued; the target stands at most BeamStand ticks for its Cash. The first plan
            // off the queue is the beam's. Not proven optimal: the width, h and the pre-check drop states Dijkstra would play.
            // Memory: a state's snapshot is held while it has steps queued, so at most the buckets the longest step spans.
            PlanResult Beam(Node<TSnap> start)
            {
                var buckets = new Dictionary<long, List<Node<TSnap>>>();
                string why = null;
                double lastBeamLog = 0;
                Dominated(start);
                Expand(start, true);
                while (queue.TryDequeue(out var e, out var priority))
                {
                    if (clock.Elapsed.TotalSeconds >= maxSeconds) { why = $"stopped after {clock.Elapsed.TotalSeconds:0} s at tick {priority.tick}"; break; }
                    Node<TSnap> n = e.node;
                    if (n == null)
                    {
                        bool last = --e.parent.Pending == 0 && e.parent.Parent != null;
                        if (e.parent.Dead)
                        {
                            if (last) e.parent.Snap = default;
                            continue;
                        }
                        long? end = Played(e.parent, e.via, out _, e.via.Stand ? e.parent.Tick + e.via.Step.Ticks + BeamStand : long.MaxValue);
                        if (last) e.parent.Snap = default;
                        if (end == null) continue;
                        n = Made(e.parent, e.via, end.Value, e.via.End);
                        if (n.Tick > priority.tick) { queue.Enqueue((null, null, n), (n.Tick, seq++)); continue; }
                    }
                    else if (!n.Done) economy.Restore(n.Snap);
                    if (Dominated(n)) { result.Dropped++; Count(n.Via.Step.Kind, 0, 0, 1, 0); n.Snap = default; continue; }
                    if (n.Done) return Finish(n, false, $"beam {width}: not proven optimal");
                    double? h = Estimate(n);
                    if (h == null) { result.Unreachable++; n.Dead = true; n.Snap = default; continue; }
                    n.F = n.Tick + h.Value;
                    if (!buckets.TryGetValue(n.Tick / BeamBucket, out List<Node<TSnap>> bucket)) buckets[n.Tick / BeamBucket] = bucket = new List<Node<TSnap>>();
                    if (bucket.Count >= width)
                    {
                        Node<TSnap> worst = bucket[0];
                        foreach (Node<TSnap> k in bucket)
                            if (k.F > worst.F) worst = k;
                        result.Pruned++;
                        if (worst.F <= n.F) { n.Dead = true; n.Snap = default; continue; }
                        worst.Dead = true;
                        if (worst.Pending == 0) worst.Snap = default;
                        bucket.Remove(worst);
                    }
                    bucket.Add(n);
                    if ((++result.Nodes & 255) == 0) result.PeakBytes = Math.Max(result.PeakBytes, GC.GetTotalMemory(false));
                    Expand(n, true);
                    if (clock.Elapsed.TotalSeconds - lastBeamLog >= 15)
                    {
                        lastBeamLog = clock.Elapsed.TotalSeconds;
                        log?.Invoke($"beam: tick {n.Tick}, Cash {n.Holds[0]:0}, {result.States} states played, {result.Nodes} let in, {result.Pruned} cut, {result.Dropped} dominated, "
                            + $"{result.Prechecked} pre-checked out, {queue.Count} queued, {GC.GetTotalMemory(false) / 1e9:0.00} GB; "
                            + string.Join(", ", result.Profile.Select(p => $"{p.Key} {p.Value.played}/{p.Value.failed}/{p.Value.dominated}")));
                    }
                }
                return Finish(start, false, $"beam {width}: " + (why ?? "the queue ran out") + "; no plan reached the target");
            }
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
        /// a label's goal (its boxes). <paramref name="width"/> above 0: the beam of that width. <paramref name="standAtTarget"/>:
        /// the target's buy stands at its box until the Cash is there (else only where no lap is offered); the one place a plan
        /// waits for Cash. The steps out of a place are built once per place and unlocks (the table's moves), then filtered by
        /// the state (active boxes, earlier targets bought), so every node shares them.
        /// </summary>
        internal static PlanResult Search(Simulation sim, SimSnapshot start, MoveTable moves, string startPlace, string[] targets, string[] bonuses, int maxStates,
            Func<string, FullTasGoal> goalOf, double maxSeconds = double.PositiveInfinity, IReadOnlyList<string> prefix = null, Action<string> log = null, bool levels = false,
            ISet<string> only = null, int width = 0, bool standAtTarget = false)
        {
            var economy = new SimEconomy(sim, moves, label => goalOf(label)?.boxes, levels);
            sim.RestoreForDrive(start);
            long startTick = sim.Engine.PhysicsTicks;
            string target = targets[targets.Length - 1];
            var before = targets.Take(targets.Length - 1).ToHashSet();
            var bought = targets.ToDictionary(t => t, t => economy.Level(t));
            var rewards = bonuses.ToHashSet();
            var built = new Dictionary<(string place, string caps), List<(PlanOption option, string[] boxes)>>();

            string Estimate(string from, Move m, string caps) => $"{from} -> {m.To} [{caps}] {m.Ticks} ticks";
            List<(PlanOption option, string[] boxes)> Built(string place, string caps)
            {
                if (built.TryGetValue((place, caps), out var list)) return list;
                Caps.TryParse(caps, out Caps have);
                list = new List<(PlanOption, string[])>();
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
                        list.Add((new PlanOption(new Step(lap ? "lap" : "record", to, m.Ticks), "finish:" + n, Array.Empty<string>(), n + "@" + course, false,
                            new[] { "start:" + n, "finish:" + n }, estimated.ToArray()), null));
                        continue;
                    }
                    if (to.StartsWith("restart@", StringComparison.Ordinal))
                    {
                        list.Add((new PlanOption(new Step("restart", to, m.Ticks), to, Array.Empty<string>(), null, false, Array.Empty<string>(), estimated.ToArray()), null));
                        continue;
                    }
                    string[] boxes = to.Split(MoveTable.SetJoin);
                    // A goal buying several boxes at once (`buy:a+b`) is a place of the best's; the planner buys them as a set.
                    if (boxes.Any(b => b.Contains('+'))) continue;
                    if (boxes.Length == 1 && to == target)
                        list.Add((new PlanOption(new Step("target", to, m.Ticks), to, boxes, null, true, boxes, estimated.ToArray()), boxes));
                    else if (boxes.Length == 1 && rewards.Contains(to))
                        list.Add((new PlanOption(new Step("reward", to, m.Ticks), to, boxes, null, false, boxes, estimated.ToArray()), boxes));
                    else if (boxes.All(b => b.StartsWith("buy:", StringComparison.Ordinal) && (FullTasGoal.IsEconomy(b) && (only == null || only.Contains(b)) || before.Contains(b))))
                        list.Add((new PlanOption(new Step("buy", to, m.Ticks), boxes[boxes.Length - 1], boxes, null, false, boxes, estimated.ToArray()), boxes));
                }
                // The target stands at its box until the Cash is there: everywhere with --stand, else only as the last resort where
                // there's nowhere to lap from.
                if (!laps || standAtTarget)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].option.Target) list[i] = (list[i].option with { Stand = true }, list[i].boxes);
                return built[(place, caps)] = list;
            }
            List<PlanOption> Options(string place, bool standOnly)
            {
                var list = new List<PlanOption>();
                foreach (var (o, boxes) in Built(place, PlayerCaps.Of(sim).Id))
                {
                    if (standOnly && !o.Target || boxes != null && !economy.Active(boxes)) continue;
                    if (o.Target && before.Any(t => economy.Level(t) == bought[t])) continue;
                    list.Add(standOnly && !o.Stand ? o with { Stand = true } : o);
                }
                return list;
            }
            // The beam's estimate: every target not bought yet, at its price now, and the table's ticks to the next one.
            (double[] price, int ticks) Remaining(string place)
            {
                var price = new double[7];
                string next = null;
                foreach (string t in targets)
                {
                    if (t != target && economy.Level(t) != bought[t]) continue;
                    double[] p = economy.Price(t);
                    for (int c = 0; p != null && c < p.Length; c++) price[c] += p[c];
                    next ??= t;
                }
                return (price, next == null ? 0 : moves.Best(place, next, PlayerCaps.Of(sim).Id)?.Ticks ?? 0);
            }
            PlanResult r = Search(economy, startTick, startPlace, p => Options(p, false), maxStates, maxSeconds, prefix, log, p => Options(p, true).FirstOrDefault(),
                width, Remaining);
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
            Console.WriteLine($"{title}: " + (r.Done ? $"target on tick {r.Tick}" + (r.Proven ? " (proven under the planner's state key)" : " (not proven)") : "not done")
                + $"; {r.States} states played, {r.Dropped} dominated, {r.Seconds:0} s" + (r.Why == null ? "" : "; " + r.Why));
            if (r.Width > 0)
                Console.WriteLine($"  beam {r.Width}: {r.Nodes} states let in, {r.Keys} keys, {r.Pruned} cut by the width, {r.Prechecked} buys pre-checked out, "
                    + $"{r.Unreachable} without income, peak {r.PeakBytes / 1e9:0.00} GB");
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
            // A running buy-max routine goes on spending and raising its box's level: which boxes have one is part of the key.
            string buying = string.Join(",", Enumerable.Range(0, _all.Length).Where(i => _sim.Shop.Buying(new[] { _sim.Shop.Id(_all[i]) })));
            if (_levels) return PlayerCaps.Of(_sim).Id + "|" + buying;
            return PlayerCaps.Of(_sim).Id + "|" + buying + "|" + string.Join(",", _all.Select(b => b.TimesUsed))
                + "|" + string.Join(",", _sim.Courses.Select(c => ((float)BestPathTime.GetValue(c)).ToString("R", CultureInfo.InvariantCulture)));
        }

        public double[] Earned()
        {
            var earned = new double[7];
            for (int c = 0; c < 7; c++) earned[c] = _sim.Shop.Gained((globalStats.Currencies)c);
            return earned;
        }

        /// <summary>A label's boxes' price now, per currency (Shop.Need); null when one can't be bought.</summary>
        public double[] Price(string label) => _sim.Shop.Need(Ids(label));

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
            // --mode exact: Dijkstra (proven under the state key); beam: the beam of --beam W; auto: exact, then the beam where
            // the exact search is capped. --stand on|off: the target stands at its box for its last Cash (default: the beam's on).
            string mode = Solve.Option(args, "--mode", "auto");
            if (mode != "exact" && mode != "beam" && mode != "auto") throw new ArgumentException("--mode wants exact, beam or auto");
            int width = Solve.Option(args, "--beam", EconomyPlan.DefaultWidth);
            string standArg = Solve.Option<string>(args, "--stand", null);
            EconomyPlanResult Plan(IReadOnlyList<string> prefix)
            {
                string exactWhy = null;
                if (mode != "beam")
                {
                    EconomyPlanResult exact = EconomyPlan.Search(_eval, at, moves, place, targets, bonuses, maxStates, GoalOf, seconds, prefix, Log, levels, only,
                        standAtTarget: standArg == "on");
                    // A --compare step that doesn't play stops the beam too.
                    if (mode == "exact" || exact.Proven || exact.Why?.StartsWith("step ", StringComparison.Ordinal) == true) return exact;
                    exactWhy = exact.Why;
                    Log($"plan: the exact search isn't proven ({exactWhy}); the beam of {width}");
                }
                EconomyPlanResult beam = EconomyPlan.Search(_eval, at, moves, place, targets, bonuses, maxStates, GoalOf, seconds, prefix, Log, levels, only,
                    width, standArg != "off");
                if (exactWhy != null) beam.Why = "exact " + exactWhy + "; " + beam.Why;
                return beam;
            }
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
            Console.WriteLine($"{"order (then searched on)",-48} {"target tick",11} {"vs free",8}  proven*");
            Console.WriteLine($"{"the best's own",-48} {bestTick,11}");
            foreach (var (name, r) in rows)
                Console.WriteLine($"{name,-48} {(r.Done ? r.Tick.ToString(CultureInfo.InvariantCulture) : "-"),11} {(r.Done && free.Done ? (r.Tick - free.Tick).ToString("+0;-0;0", CultureInfo.InvariantCulture) : ""),8}  {(r.Proven ? "yes" : "no")}"
                    + (r.Why == null ? "" : "  (" + r.Why + ")"));
            Console.WriteLine("* proven: no schedule reaches the target sooner under the planner's state key (place, unlocks, box levels, records,"
                + " running buy-max routines); hidden economy state such as clones in flight isn't compared");
            if (outFile != null)
            {
                object Json(string name, EconomyPlanResult r) => new
                {
                    name, done = r.Done, proven = r.Proven, tick = r.Tick, why = r.Why, states = r.States, dropped = r.Dropped, seconds = r.Seconds,
                    beam = r.Width == 0 ? null : new { width = r.Width, letIn = r.Nodes, keys = r.Keys, cut = r.Pruned, prechecked = r.Prechecked, unreachable = r.Unreachable, peakBytes = r.PeakBytes },
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
