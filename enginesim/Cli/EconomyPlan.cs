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
using PlanStep = IGTAP.EngineSim.Cli.Step;

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
        /// <summary>Per step, the tick it ends on, the Cash held then, every currency held then, and the levels it bought (an
        /// `all:` step's count; else 1).</summary>
        public long[] Ends = Array.Empty<long>();
        public double[] Cash = Array.Empty<double>();
        public double[][] Held = Array.Empty<double[]>();
        public int[] Levels = Array.Empty<int>();
        /// <summary>Per step, whether it stood at its box for its Cash (PlanOption.Stand): realize waits for it too.</summary>
        public bool[] Stands = Array.Empty<bool>();
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
        /// the pre-check didn't queue, estimates made with no income toward the target (ranked last), and the most memory seen
        /// in use (GC.GetTotalMemory, sampled).</summary>
        public int Width, Nodes, Pruned, Prechecked, NoIncome;
        /// <summary>A --compare step didn't play (or isn't offered): the search never ran.</summary>
        public bool PrefixFailed;
        /// <summary>The search ran out of states to play before any cap: no schedule under the search's rules reaches the target, and
        /// more time wouldn't find one.</summary>
        public bool Exhausted;
        public long PeakBytes;
    }

    /// <summary>The economy the search plays steps on (a seam: <see cref="SimEconomy"/> is the game's, plan-selftest's toy a
    /// test's). Idle: ticks standing at a place; Buy: one tick touching a box (false: it didn't buy); Finish: one tick finishing a
    /// course (its argument the economy's own: `N@ticks` for the game); Holds: every currency; Key: what besides the place two
    /// states must share for the richer earlier one to stand for the other (unlocks, box levels, records). Earned: every
    /// currency gained so far, spending left out; Price: what a box costs now (its next `levels` levels), per currency as Holds
    /// counts them (null: it can't be bought now). The beam's estimate and buy pre-check read those two.</summary>
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
        double[] Price(string box, int levels = 1);
        /// <summary>The most ticks the economy can go paying nothing while it still earns (a clone's lap: its payouts come once a
        /// lap), 0 where income comes every tick or not at all.</summary>
        int Quiet();
    }

    /// <summary>A step out of a place as the search plays it: Ticks less its events standing at End, then each Buys box a tick
    /// (every one must buy: a strict buy has its Cash on arrival), then Finish a tick. Target: the plan's last buy. Stand: the
    /// target's last resort (no lap from the place to earn on), and every buy from a place no lap is offered from (Area 2's): its
    /// buy retried a tick at a time until the Cash is there. Patient: the stand isn't cut at the beam's BeamStand (no lap earns
    /// meanwhile, so a later state's stand isn't shorter), but stops when nothing the box costs has risen over StandStall ticks
    /// more than the economy's longest quiet stretch (IEconomy.Quiet: the clones' payouts come once a lap).
    /// Levels (`atoms:N`): before the touch it stands until the next Levels levels' price is held (a buy-max touch then buys
    /// all it can pay for). All (`all:box`): after the buy it stays in the box, touching it a tick at a time while its next level
    /// is paid for now (upgradeBox.OnTriggerStay2D: a buy each 0.2 s cooldown), a goal per level bought.</summary>
    sealed record PlanOption(Step Step, string End, string[] Buys, string Finish, bool Target, string[] Goals, string[] Estimated = null, bool Stand = false,
        bool Patient = false, int Levels = 1, bool All = false);

    /// <summary>fulltas-plan's search: its targets (the last the plan's end, the ones before bought first), the bonus reward boxes
    /// a plan may take, and --mode, --beam, --max-states, --minutes (as seconds), --dominance levels, --only and --stand.</summary>
    sealed record PlanSettings(string[] Targets, string[] Bonuses, string Mode = "auto", int Width = EconomyPlan.DefaultWidth, int MaxStates = 200000,
        double Seconds = 1800, bool Levels = false, ISet<string> Only = null, string Stand = null);

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
        /// <summary>A patient stand (and atoms:N's saving) gives up when no currency the box still lacks rose over this many ticks
        /// more than IEconomy.Quiet (the NP cap reached; no clones to pay GP).</summary>
        const int StandStall = 500;
        /// <summary>all:box stops after this many touches in a row that don't buy (twice the box's 0.2 s cooldown).</summary>
        const int AllMisses = 20;
        /// <summary>Area 2's atom box (AtomUpgrades/AtomUpgrade, spawnNewAtom, buy-max): its steps are atoms:N, N a power of two up to
        /// MaxAtoms.</summary>
        internal const string AtomBox = "buy:buyatom";

        /// <summary>Where a step ends and what it buys, as a movement table To: atoms:N's the atom box, all:box's its box.</summary>
        internal static string PlaceOf(Step s) => s.Kind == "atoms" ? AtomBox : s.Kind == "all" ? "buy:" + s.Arg.Substring(4) : s.Arg;
        const int MaxAtoms = 512;

        /// <summary>Held covers the price (null: the box can't be bought).</summary>
        static bool Covered(double[] price, double[] held)
        {
            if (price == null) return false;
            for (int c = 0; c < price.Length; c++)
                if (price[c] > held[c]) return false;
            return true;
        }

        /// <summary>No currency the box (its next <paramref name="levels"/> levels) still lacks has risen since <paramref name="was"/>
        /// (null: the stand's first look, never stalled); a box that can't be bought stalls when it couldn't at the last look either
        /// (one growing on a tree segment comes on within a look).</summary>
        static bool Stalled<TSnap>(IEconomy<TSnap> economy, string box, int levels, ref double[] was)
        {
            double[] price = economy.Price(box, levels), held = economy.Holds();
            if (price == null)
            {
                bool still = was != null && was.Length == 0;
                was = Array.Empty<double>();
                return still;
            }
            bool rose = was == null || was.Length == 0;
            for (int c = 0; c < price.Length && !rose; c++)
                if (price[c] > held[c] && held[c] > was[c]) rose = true;
            was = held;
            return !rose;
        }

        public static int Run(string[] args) => FullTas.EconomyPlanCommand(args);

        /// <summary>Beam: a node's income is what it gained over its last steps back to the first that covers this many ticks
        /// (Earned's change over the ticks: laps' rewards and the clones' payouts, spending left out).</summary>
        const int IncomeWindow = 300;
        /// <summary>Beam: a buy (or the target's stand) is played only if its Cash can be there in time at this many times the
        /// node's income (the buy pre-check).</summary>
        const double PrecheckMargin = 2;
        /// <summary>Beam: h's wait for a state with no income of a currency the targets still need (ranked last, kept).</summary>
        const double NoIncome = 1e6;

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
            /// <summary>The levels its `all:` step bought after its first (a goal each).</summary>
            public int Extra;
            /// <summary>Its steps still in the queue: at 0 its snapshot is let go (the search's memory is the queue's front).</summary>
            public int Pending;
            /// <summary>Beam: its tick plus the estimate of the ticks to the target.</summary>
            public double F;
        }

        /// <summary>Plays one option from the economy's state at <paramref name="tick"/>; the tick it ends on, null when a buy
        /// didn't buy (or a stand would end on <paramref name="limit"/> or later).</summary>
        internal static long? Play<TSnap>(IEconomy<TSnap> economy, long tick, PlanOption o, long limit = long.MaxValue) => Play(economy, tick, o, out _, limit);

        /// <summary><see cref="Play{TSnap}(IEconomy{TSnap}, long, PlanOption, long)"/>; <paramref name="extra"/>: the levels an
        /// `all:` step bought after its first.</summary>
        internal static long? Play<TSnap>(IEconomy<TSnap> economy, long tick, PlanOption o, out int extra, long limit = long.MaxValue)
        {
            extra = 0;
            int events = o.Buys.Length + (o.Finish != null ? 1 : 0);
            int walk = Math.Max(0, o.Step.Ticks - events);
            if (walk > 0) economy.Idle(walk, o.End);
            tick += walk;
            for (int i = 0; i < o.Buys.Length; i++)
            {
                string box = o.Buys[i];
                bool last = i == o.Buys.Length - 1;
                double[] was = null;
                int look = StandStall + economy.Quiet();
                // atoms:N: the next N levels' price saved before the touch.
                for (int w = 0; last && o.Levels > 1 && !Covered(economy.Price(box, o.Levels), economy.Holds()); w++)
                {
                    if (economy.Price(box, o.Levels) == null || w == StandCap || tick >= limit || w % look == 0 && Stalled(economy, box, o.Levels, ref was)) return null;
                    economy.Idle(1, o.End);
                    tick++;
                }
                tick++;
                if (economy.Buy(box)) continue;
                if (!o.Stand || !last) return null;
                was = null;
                for (int w = 0; ; w++)
                {
                    if (w == StandCap || tick >= limit || o.Patient && w % look == 0 && Stalled(economy, box, 1, ref was)) return null;
                    tick++;
                    if (economy.Buy(box)) break;
                }
            }
            // all:box: in the box while its next level is paid for now, a touch a tick (a buy once its cooldown is over).
            for (int miss = 0; o.All && miss < AllMisses && tick < limit && Covered(economy.Price(o.Buys[^1]), economy.Holds());)
            {
                tick++;
                if (economy.Buy(o.Buys[^1])) { extra++; miss = 0; }
                else miss++;
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

            Node<TSnap> Made(Node<TSnap> parent, PlanOption via, long tick, string place, int extra = 0)
            {
                string key = place + "|" + economy.Key();
                if (!keys.TryGetValue(key, out int id)) keys[key] = id = keys.Count;
                return new Node<TSnap>
                {
                    Tick = tick, Place = place, Snap = economy.Capture(), Parent = parent, Holds = economy.Holds(), Earned = width > 0 ? economy.Earned() : null,
                    Key = id, Via = via, Done = via?.Target == true, Extra = extra,
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
            long? Played(Node<TSnap> from, PlanOption o, out string why, out int extra, long limit = long.MaxValue)
            {
                why = null;
                extra = 0;
                double t0 = clock.Elapsed.TotalSeconds;
                long? end = null;
                economy.Restore(from.Snap);
                result.States++;
                try { end = Play(economy, from.Tick, o, out extra, limit); }
                catch (InvalidOperationException e) { why = e.Message; }
                Count(o.Step.Kind, 1, end == null ? 1 : 0, 0, clock.Elapsed.TotalSeconds - t0);
                return end;
            }
            // Same key, no later, at least as much of every currency: the kept one stands for it. Nodes come off the queue in tick
            // order, so every kept one is no later; one on the same tick holding no more is dropped in turn.
            bool Dominated(Node<TSnap> n)
            {
                if (Beaten(n)) return true;
                Keep(n);
                return false;
            }
            bool Beaten(Node<TSnap> n) => kept.TryGetValue(n.Key, out List<Node<TSnap>> list) && list.Any(k => !k.Dead && k.Tick <= n.Tick && AtLeast(k.Holds, n.Holds));
            void Keep(Node<TSnap> n)
            {
                if (!kept.TryGetValue(n.Key, out List<Node<TSnap>> list)) kept[n.Key] = list = new List<Node<TSnap>>();
                foreach (Node<TSnap> k in list)
                    if (k.Tick == n.Tick && AtLeast(n.Holds, k.Holds)) k.Dead = true;
                list.RemoveAll(k => k.Dead);
                list.Add(n);
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
                result.Goals = path.SelectMany(n => n.Extra == 0 ? n.Via.Goals : n.Via.Goals.Concat(Enumerable.Repeat(n.Via.Goals[^1], n.Extra))).ToArray();
                result.Estimated = path.SelectMany(n => n.Via.Estimated ?? Array.Empty<string>()).Distinct().ToArray();
                result.Ends = path.Select(n => n.Tick).ToArray();
                result.Cash = path.Select(n => n.Holds[0]).ToArray();
                result.Held = path.Select(n => n.Holds).ToArray();
                result.Levels = path.Select(n => 1 + n.Extra).ToArray();
                result.Stands = path.Select(n => n.Via.Stand).ToArray();
                result.Why = why;
                result.Seconds = clock.Elapsed.TotalSeconds;
                result.Keys = keys.Count;
                return result;
            }

            PlanResult Failed(Node<TSnap> at, string why)
            {
                result.PrefixFailed = true;
                return Finish(at, false, why);
            }

            var root = Made(null, null, startTick, startPlace);
            Node<TSnap> at = root;
            for (int i = 0; prefix != null && i < prefix.Count; i++)
            {
                PlanOption o = options(at.Place).FirstOrDefault(x => x.Step.Arg == prefix[i]);
                if (o == null) return Failed(at, $"step {i + 1} {prefix[i]} isn't offered at {at.Place} on tick {at.Tick} (unknown move, or the box is capped or locked)");
                long? end = Played(at, o, out string why, out int extra);
                if (end == null) return Failed(at, $"step {i + 1} {prefix[i]} doesn't play from {at.Place} on tick {at.Tick}: " + (why ?? "not enough Cash on arrival"));
                at = Made(at, o, end.Value, o.End, extra);
                // The order alone reaches the target: its tick, nothing searched (not proven).
                if (at.Done) return Finish(at, false, "the --compare order reaches the target: its own tick, nothing searched");
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
                    long? end = Played(e.parent, e.via, out _, out int extra);
                    if (last) e.parent.Snap = default;
                    if (end == null) continue;
                    n = Made(e.parent, e.via, end.Value, e.via.End, extra);
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
            result.Exhausted = capped == null;
            capped ??= "the queue ran out: no schedule reaches the target";
            if (stand == null) return Finish(at, false, capped);
            // Capped: the richest kept states, each finished by standing at the target until it can pay (not proven).
            Node<TSnap> best = null;
            foreach (Node<TSnap> k in kept.Values.SelectMany(l => l).Where(k => !k.Dead && !k.Done && k.Pending > 0).OrderByDescending(k => k.Holds[0]).Take(Completions))
            {
                economy.Restore(k.Snap);
                PlanOption o = stand(k.Place);
                int extra = 0;
                long? end = o == null ? null : Played(k, o, out _, out extra);
                if (end == null) continue;
                Node<TSnap> done = Made(k, o, end.Value, o.End, extra);
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
            // ticks to the next target. No window yet: the ticks alone. An income of 0 or less over the window (a walk, or a lap
            // whose finish hasn't paid yet; Earned falls at an atom buy's or the breaker's Cash reset) falls back on the income
            // since the search's start, and with none there either on NoIncome ticks: ranked last, never dropped.
            double Estimate(Node<TSnap> n)
            {
                var (price, ticks) = remaining(n.Place);
                Node<TSnap> a = Window(n);
                double wait = 0;
                for (int c = 0; c < price.Length; c++)
                {
                    double missing = price[c] - n.Holds[c];
                    if (missing <= 0 || n.Tick == a.Tick) continue;
                    double rate = Rate(n, a, c);
                    if (rate <= 0) rate = Rate(n, root, c);
                    if (rate <= 0) { result.NoIncome++; wait = Math.Max(wait, NoIncome); continue; }
                    wait = Math.Max(wait, missing / rate);
                }
                return wait + ticks;
            }
            // The buy pre-check, with the economy at n: the option's boxes cost more than n holds plus PrecheckMargin times n's
            // income over the step's ticks (a stand: and BeamStand more, a patient one StandCap; atoms:N: its N levels). A buy right after a buy is always played
            // (what it bought, a clone, may pay before the window shows it); a stand always checked; no income over the window
            // (Estimate's fallbacks) never pre-checks.
            bool Short(Node<TSnap> n, PlanOption o)
            {
                if (o.Buys.Length == 0) return false;
                Node<TSnap> a = Window(n);
                if (n.Tick == a.Tick || !o.Stand && n.Via?.Buys.Length > 0) return false;
                double[] need = null;
                foreach (string b in o.Buys)
                {
                    double[] p = economy.Price(b, o.Levels);
                    if (p == null) return false;
                    need ??= new double[p.Length];
                    for (int c = 0; c < p.Length; c++) need[c] += p[c];
                }
                double ticks = o.Step.Ticks + (o.Patient ? StandCap : o.Stand ? BeamStand : 0);
                for (int c = 0; c < need.Length; c++)
                {
                    double missing = need[c] - n.Holds[c];
                    double rate = Rate(n, a, c);
                    if (missing > 0 && rate > 0 && missing > PrecheckMargin * rate * ticks) return true;
                    // A patient stand for a currency nothing has paid since the search's start (GP with no clones) never ends.
                    if (missing > 0 && o.Patient && rate <= 0 && Rate(n, root, c) <= 0) return true;
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
                        long? end = Played(e.parent, e.via, out _, out int extra, e.via.Stand && !e.via.Patient ? e.parent.Tick + e.via.Step.Ticks + BeamStand : long.MaxValue);
                        if (last) e.parent.Snap = default;
                        if (end == null) continue;
                        n = Made(e.parent, e.via, end.Value, e.via.End, extra);
                        if (n.Tick > priority.tick) { queue.Enqueue((null, null, n), (n.Tick, seq++)); continue; }
                    }
                    else if (!n.Done) economy.Restore(n.Snap);
                    if (Beaten(n)) { result.Dropped++; Count(n.Via.Step.Kind, 0, 0, 1, 0); n.Snap = default; continue; }
                    if (n.Done) return Finish(n, false, $"beam {width}: not proven optimal");
                    // Let in first, then kept: one turned away never drops a same-tick rival through dominance.
                    n.F = n.Tick + Estimate(n);
                    if (!buckets.TryGetValue(n.Tick / BeamBucket, out List<Node<TSnap>> bucket)) buckets[n.Tick / BeamBucket] = bucket = new List<Node<TSnap>>();
                    bucket.RemoveAll(k => k.Dead);
                    if (bucket.Count >= width)
                    {
                        Node<TSnap> worst = bucket[0];
                        foreach (Node<TSnap> k in bucket)
                            if (k.F > worst.F) worst = k;
                        result.Pruned++;
                        if (worst.F <= n.F) { n.Snap = default; continue; }
                        worst.Dead = true;
                        if (worst.Pending == 0) worst.Snap = default;
                        bucket.Remove(worst);
                    }
                    Keep(n);
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
                result.Exhausted = why == null;
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
            economy.Restore(start);
            long startTick = sim.Engine.PhysicsTicks;
            targets = TreeTargets(sim, targets, goalOf);
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
                // A box offered: an economy box (or any --only names), or a target the last needs first.
                bool Offered(string b) => b.StartsWith("buy:", StringComparison.Ordinal) && ((only == null ? FullTasGoal.IsEconomy(b) : only.Contains(b)) || before.Contains(b));
                IEnumerable<string> tos = moves.Targets(place, caps).Concat(moves.Restarts(place, caps));
                // Its own box: all:box from standing in it (the table has no move from a place to itself).
                if (moves.Places.TryGetValue(place, out Place self) && self.Kind == "box") tos = tos.Append(place);
                foreach (string to in tos.Distinct().ToList())
                {
                    Move m = to == place ? new Move(place, place, 1, "here", false) : moves.Best(place, to, caps);
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
                    if (to == place)
                    {
                        if (Offered(to) && to != AtomBox) list.Add((new PlanOption(new Step("all", "all:" + to.Substring(4), m.Ticks), to, boxes, null, false, boxes, All: true), boxes));
                    }
                    else if (boxes.Length == 1 && to == target)
                        list.Add((new PlanOption(new Step("target", to, m.Ticks), to, boxes, null, true, boxes, estimated.ToArray()), boxes));
                    else if (boxes.Length == 1 && rewards.Contains(to))
                        list.Add((new PlanOption(new Step("reward", to, m.Ticks), to, boxes, null, false, boxes, estimated.ToArray()), boxes));
                    else if (boxes.All(Offered) && to != AtomBox)
                    {
                        list.Add((new PlanOption(new Step("buy", to, m.Ticks), boxes[boxes.Length - 1], boxes, null, false, boxes, estimated.ToArray()), boxes));
                        if (boxes.Length == 1)
                            list.Add((new PlanOption(new Step("all", "all:" + to.Substring(4), m.Ticks), to, boxes, null, false, boxes, estimated.ToArray(), All: true), boxes));
                    }
                    // atoms:N (Area 2's spawnNewAtom box): the next N atoms' price saved, then a touch (buy-max: all it can pay for).
                    if (to == AtomBox && (only == null || only.Contains(AtomBox)))
                        for (int n = 1; n <= MaxAtoms; n *= 2)
                            list.Add((new PlanOption(new Step("atoms", "atoms:" + n, m.Ticks), to, boxes, null, false, boxes, estimated.ToArray(), Stand: true, Levels: n), boxes));
                }
                // The target stands at its box until the Cash is there: everywhere with --stand, else only as the last resort where
                // there's nowhere to lap from. Where no lap is offered (Area 2), every buy stands for its Cash (nothing earns by
                // moving on), as long as what it costs comes in (Patient).
                for (int i = 0; i < list.Count; i++)
                {
                    PlanOption o = list[i].option;
                    if (!laps && o.Buys.Length > 0 && o.Step.Kind != "reward") list[i] = (o with { Stand = true, Patient = true }, list[i].boxes);
                    else if (standAtTarget && o.Target) list[i] = (o with { Stand = true }, list[i].boxes);
                }
                return built[(place, caps)] = list;
            }
            List<PlanOption> Options(string place, bool standOnly)
            {
                var list = new List<PlanOption>();
                foreach (var (o, boxes) in Built(place, PlayerCaps.Of(sim).Id))
                {
                    // A patient stand waits for a box still growing on its tree segment too.
                    if (standOnly && !o.Target || boxes != null && !economy.Active(boxes) && !(o.Patient && economy.Growing(boxes))) continue;
                    if (o.Target && before.Any(t => economy.Level(t) == bought[t])) continue;
                    // An earlier target (a tree box on the way) is a step until it's bought; again only if it's a box the search offers
                    // anyway (an economy box, or one --only names).
                    if (!o.Target && boxes != null && boxes.Any(b => before.Contains(b) && economy.Level(b) != bought[b] && !(only?.Contains(b) ?? FullTasGoal.IsEconomy(b)))) continue;
                    // A buy-max box's touch buys all it can pay for already; atoms:N past what is held now (else atoms:1's touch).
                    if (o.All && economy.BuyMax(boxes[0])) continue;
                    if (o.Levels > 1 && !(economy.Price(boxes[0], o.Levels) is double[] p && !Covered(p, economy.Holds()))) continue;
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

        /// <summary>The targets with the boxes whose buys grow a tree box target's tree down to it (Shop.TreeWay) put first, the ones
        /// the state at <paramref name="sim"/> hasn't bought, root first: a tree box is bought only once its segment has grown.</summary>
        internal static string[] TreeTargets(Simulation sim, string[] targets, Func<string, FullTasGoal> goalOf)
        {
            var tokens = FullTasGoal.Boxes.ToDictionary(b => FullTasGoal.BoxId(b.path, b.sibling), b => b.token);
            return targets.SelectMany(t => (goalOf(t)?.boxes is [string box] ? sim.Shop.TreeWay(box) : new List<string>())
                .Where(id => sim.Shop.Box(id).TimesUsed == 0 && tokens.ContainsKey(id)).Select(id => "buy:" + tokens[id]).Append(t)).Distinct().ToArray();
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
                    + $"{r.NoIncome} estimates without income, peak {r.PeakBytes / 1e9:0.00} GB");
            // GP and NP (Area 2's currencies) beside the Cash, once any step holds them.
            bool area2 = r.Held.Any(h => h.Length > 2 && (h[1] > 0 || h[2] > 0));
            for (int i = 0; i < r.Steps.Length; i++)
                Console.WriteLine($"  {r.Ends[i],6} +{r.Steps[i].Ticks,-4} {r.Cash[i],12:0.##} Cash  " + (area2 ? $"{r.Held[i][1],10:G4} GP {r.Held[i][2],10:G4} NP  " : "")
                    + $"{r.Steps[i].Kind,-7} {r.Steps[i].Arg}" + (r.Levels[i] > 1 ? $" x{r.Levels[i]}" : ""));
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

        /// <summary>The snapshot last restored while no drive tick has played since: Simulation.Capture counts what is due from the
        /// tick after Scheduler.Tick, the drive's last tick, so a capture right after a restore (Tick 0, nothing played) would count
        /// every pending Invoke, routine and the atom grid a tick early (and lose a grid due on the first tick).</summary>
        SimSnapshot _restored;

        public SimSnapshot Capture() => _restored ?? _sim.Capture((int)_sim.Engine.RunTicks);
        public void Restore(SimSnapshot s) { _sim.RestoreForDrive(s); _restored = s; }
        public void Idle(int ticks, string place) { _restored = null; _sim.EconomyIdle(ticks, At(place)); }

        public bool Buy(string label)
        {
            _restored = null;
            string[] ids = Ids(label);
            if (ids.Length != 1) throw new InvalidOperationException($"{label}: {ids.Length} boxes, the planner buys one a tick");
            return _sim.EconomyBuy(ids[0], At(label));
        }

        public void Finish(string course)
        {
            _restored = null;
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

        /// <summary>A label's boxes' price now (their next <paramref name="levels"/> levels), per currency (Shop.Need); null when one
        /// can't be bought.</summary>
        public double[] Price(string label, int levels = 1) => _sim.Shop.Need(Ids(label), levels);

        /// <summary>Twice the longest clone path of a course with clones (a clone pays at its path's end, the next one spawns an
        /// interval later, at most a path), in ticks.</summary>
        public int Quiet()
        {
            double longest = 0;
            foreach (clonesScript c in _sim.World.All<clonesScript>())
                if (_sim.Shop.Economy?.ClonesOf(c) is var (count, path) && count > 0) longest = Math.Max(longest, path);
            return (int)Math.Ceiling(2 * longest / 0.02);
        }

        /// <summary>The label's box buys all it can pay for on a touch (upgradeBox.buyMax, not neverBuyMax: Shop.Stay's routine).</summary>
        public bool BuyMax(string label) => Ids(label).Select(_sim.Shop.Box).Any(b => b != null && b.buyMax && !b.neverBuyMax);

        /// <summary>Every box of these labels can be bought once Cash allows (Shop.Affordable: not capped, unlocked, shown).</summary>
        public bool Active(string[] labels) => labels.All(l => Ids(l).Length > 0 && _sim.Shop.Affordable(Ids(l)) != null);

        /// <summary>A label's box is off while its tree segment grows (TreeSegmentActivationComponent.IE_Grow turns it on at the end):
        /// the segment on, the box off, levels left under its cap.</summary>
        public bool Growing(string[] labels) => labels.All(l => Ids(l).Select(_sim.Shop.Box).All(b => b != null && !b.gameObject.activeInHierarchy
            && b.transform.parent != null && b.transform.parent.gameObject.activeInHierarchy && b.TimesUsed < b.Cap));

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
            // The trace's goals, not the tracker's (realize tracks the goals it played), each label resolved once: NewGoal restores
            // the start to find a reward box, which mid-search would leave the economy at the start.
            FullTasGoal[] traceGoals = _runGoals.Goals;
            var resolved = new Dictionary<string, FullTasGoal>();
            FullTasGoal GoalOf(string label) => resolved.TryGetValue(label, out FullTasGoal g) ? g
                : resolved[label] = traceGoals.FirstOrDefault(x => x.label == label) ?? NewGoal(label)?.goal;
            foreach (string b in bonuses.Concat(targets))
                if (GoalOf(b) == null) throw new ArgumentException($"{b}: no such goal (a box token of FullTasGoal.Boxes, or a bonus of courses.json)");

            MoveTable moves = MoveTable.Load(this, File.Exists(buyTable) ? buyTable : null, RoutesFile, _best, movesFile);
            SimSnapshot at = GoalState(kept).at;
            string place = _labels[kept - 1];
            if (!moves.Places.ContainsKey(place)) throw new InvalidOperationException($"goal {kept} {place} is no place of the movement table");
            string[] tree = EconomyPlan.TreeTargets(_eval, targets, GoalOf);
            if (tree.Length > targets.Length) Log($"plan: the tree's way to {targets[^1]}: " + string.Join(", ", tree.Take(tree.Length - 1)));
            // A box the plan may buy that neither the best nor the buy table stood at (Area 2's): a place at the box, its walks estimated.
            foreach (string label in tree.Concat(only ?? (IEnumerable<string>)Array.Empty<string>()).Append(EconomyPlan.AtomBox).Distinct())
                if (label.StartsWith("buy:", StringComparison.Ordinal) && !moves.Places.ContainsKey(label) && GoalOf(label)?.boxes is [string id] && _eval.Shop.Box(id) is upgradeBox box)
                {
                    // Where the player touches it (just under its trigger), else the box itself.
                    Vector2 spot = _eval.TouchSpot(box) ?? new Vector2(box.transform.position.x, box.transform.position.y);
                    WorldPos w = _eval.ToWorld(new ScenePos(spot.x, spot.y));
                    moves.AddBoxPlace(label, w.x, w.y);
                }
            int targetGoal = Array.IndexOf(_labels, targets[targets.Length - 1], kept) + 1;
            string bestTick = targetGoal > 0 && targetGoal <= _best.Progress ? _best.GoalPhysics[targetGoal - 1].ToString(CultureInfo.InvariantCulture) : "-";
            Log($"plan from goal {kept} {place} (tick {_best.GoalPhysics[kept - 1]}) to {string.Join(", then ", targets)} (the best's: tick {bestTick}); bonuses {string.Join(" ", bonuses)}; "
                + $"{moves.Count} moves");

            // --mode exact: Dijkstra (proven under the state key); beam: the beam of --beam W; auto: exact, then the beam where
            // the exact search is capped. --stand on|off: the target stands at its box for its last Cash (default: the beam's on).
            string mode = Solve.Option(args, "--mode", "auto");
            if (mode != "exact" && mode != "beam" && mode != "auto") throw new ArgumentException("--mode wants exact, beam or auto");
            var settings = new PlanSettings(targets, bonuses, mode, Solve.Option(args, "--beam", EconomyPlan.DefaultWidth), maxStates,
                Solve.Option(args, "--minutes", 30.0) * 60, levels, only, Solve.Option<string>(args, "--stand", null));
            EconomyPlanResult Plan(IReadOnlyList<string> prefix) => PlanFrom(at, place, prefix, moves, settings, GoalOf);
            // --join FILE: no search; a route --realize wrote (its goals and inputs through the target) joined to the best's way on again.
            string joinFile = Solve.Option<string>(args, "--join", null);
            if (joinFile != null) return JoinAgain(args, joinFile, kept, targetGoal - 1, outFile);
            if (args.Contains("--replan")) return ReplanCheck(args, kept, targets, targetGoal - 1);
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
                    beam = r.Width == 0 ? null : new { width = r.Width, letIn = r.Nodes, keys = r.Keys, cut = r.Pruned, prechecked = r.Prechecked, noIncome = r.NoIncome, peakBytes = r.PeakBytes },
                    steps = r.Steps.Select((s, i) => new { kind = s.Kind, arg = s.Arg, ticks = s.Ticks, end = r.Ends[i], cash = r.Cash[i], held = r.Held[i].Take(7), levels = r.Levels[i] }),
                    goals = r.Goals, estimated = r.Estimated,
                };
                File.WriteAllText(outFile, JsonSerializer.Serialize(new
                {
                    run = _name, from = kept, fromLabel = place, startTick = _best.GoalPhysics[kept - 1], targets, bonuses, best = bestTick,
                    plans = rows.Select(r => Json(r.name, r.plan)),
                }, new JsonSerializerOptions { WriteIndented = true }));
                Log("plan -> " + outFile);
            }
            if (!args.Contains("--realize")) return rows.All(r => r.plan.Done) ? 0 : 3;
            return RealizeRows(args, rows, kept, targetGoal - 1, place, moves, movesFile, settings, GoalOf, outFile);
        }

        /// <summary>
        /// fulltas-plan --realize: each plan printed above played with the loop builder from the best's real state at its start
        /// (<see cref="Realize"/>; a --compare order's steps kept first through every re-plan), the best's way on after the target
        /// joined (<see cref="Join"/>); the moves it measured saved (fulltas/&lt;run&gt;.moves.json or --moves), a table of what
        /// each plan said and what its build did, the fastest whole route's goal list in --out (`goals`, with every realization's
        /// steps under `realized`) and its route in --route (as the solver saves a run's best, fulltas/&lt;run&gt;.json).
        /// </summary>
        int RealizeRows(string[] args, List<(string name, EconomyPlanResult plan)> rows, int kept, int targetIndex, string place, MoveTable moves, string movesFile,
            PlanSettings settings, Func<string, FullTasGoal> goalOf, string outFile)
        {
            if (targetIndex < 0) throw new ArgumentException("the target is no goal of the run after --from: realize joins the best's way on after it");
            foreach (string path in _seedFiles) _seeds.Add(ReadSeed(path));
            _planBase = _runGoals;
            FullTasGoal[] all = _runGoals.Goals;
            var (start, route) = BuildFrom(all, kept, targetIndex);
            long startTick = _best.GoalPhysics[kept - 1];
            var realized = new List<(string name, Realization z, Route run, string[] goals)>();
            foreach (var (name, plan) in rows)
            {
                IReadOnlyList<string> prefix = name == "free search" ? null : EconomyPlan.StepIds(name);
                Log($"realize {name}: from goal {kept} {place} on tick {startTick}");
                Realization z = Realize(plan, start, route, startTick, place, prefix, moves, (s, p, pre) => PlanFrom(s, p, pre, moves, settings, goalOf), goalOf);
                moves.Save(movesFile);
                var (run, goals, _) = z.Done ? Join(z.Planned, z.Inputs, all, kept, targetIndex) : (null, null, null);
                realized.Add((name, z, run, goals ?? all.Take(kept).Concat(z.Planned).Select(g => g.label).ToArray()));
            }
            string best = targetIndex < _best.Progress ? _best.GoalPhysics[targetIndex].ToString(CultureInfo.InvariantCulture) : "-";
            Console.WriteLine();
            Console.WriteLine($"{"order (then searched on)",-48} {"plan per round",-28} {"built",6} {"whole route",12}");
            Console.WriteLine($"{"the best's own",-48} {"",-28} {best,6} {_best.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s",12}");
            foreach (var (name, z, run, _) in realized)
                Console.WriteLine($"{name,-48} {string.Join(" > ", z.PlanTicks),-28} {(z.Done ? z.Tick.ToString(CultureInfo.InvariantCulture) : "-"),6} "
                    + $"{(run != null && run.Progress == run.GoalInput.Length ? run.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s" : run == null ? "-" : $"goal {run.Progress}"),12}"
                    + (z.Why == null ? "" : "  (" + z.Why + ")"));
            var whole = realized.Where(r => r.run != null && r.run.Progress == r.run.GoalInput.Length).OrderBy(r => r.run.Live).FirstOrDefault();
            if (outFile != null)
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(outFile)).AsObject();
                json["goals"] = whole.z == null ? null : System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(whole.goals));
                json["realized"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(realized.Select(r => new
                {
                    r.name, done = r.z.Done, why = r.z.Why, rounds = r.z.Rounds, planTicks = r.z.PlanTicks, tick = r.z.Done ? r.z.Tick : -1,
                    live = r.run != null && r.run.Progress == r.run.GoalInput.Length ? Math.Round(r.run.LiveSeconds, 2) : (double?)null,
                    r.goals, steps = r.z.Log,
                })));
                File.WriteAllText(outFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Log("realize -> " + outFile);
            }
            // With no whole route, the one that gets furthest (its goals so far, total -1): the next stretch is planned or solved from it.
            string routeFile = Solve.Option<string>(args, "--route", null);
            var written = whole.run != null ? whole : realized.Where(r => r.run != null).OrderByDescending(r => r.run.Progress).ThenBy(r => r.z.Tick).FirstOrDefault();
            if (routeFile != null && written.run != null)
            {
                Route r = written.run;
                string[] goals = written.goals;
                File.WriteAllText(routeFile, JsonSerializer.Serialize(new { name = _name, goals, inputs = r.Inputs.Select(t => t.ToJson()),
                    goalInputTicks = r.GoalInput, goalPhysicsTicks = r.GoalPhysics, total = r.Progress == goals.Length ? r.GoalPhysics[goals.Length - 1] : -1 }));
                Log($"realize: {written.name}'s route " + (r.Progress == goals.Length ? $"({r.LiveSeconds:0.00} s live)" : $"(to goal {r.Progress} of {goals.Length})") + " -> " + routeFile);
            }
            return realized.All(r => r.z.Done) ? 0 : 3;
        }

        /// <summary>
        /// fulltas-plan --replan [--replan-seconds S] [--replan-cap T]: the goals part's call (FullTas.Replan, as a goals pull makes it: the
        /// run's goals, joined, the best's tick at the target as the cap unless --replan-cap) once, on the run's best. Checks that it
        /// returns within its budget (plus a piece search's --piece-minutes) and that its goal map is the labels it names: each goal
        /// it maps to the run's goal of that index has that label. Exits 1 on a FAIL.
        /// </summary>
        int ReplanCheck(string[] args, int kept, string[] targets, int targetIndex)
        {
            foreach (string path in _seedFiles) _seeds.Add(ReadSeed(path));
            double seconds = Solve.Option(args, "--replan-seconds", 300.0);
            long cap = Solve.Option(args, "--replan-cap", targetIndex >= 0 && targetIndex < _best.Progress ? _best.GoalPhysics[targetIndex] : _best.Last);
            FullTasGoal[] all = _runGoals.Goals;
            double t0 = _clock.Elapsed.TotalSeconds;
            PlanResult p = Replan(_best, all, kept, targets, true, cap, seconds);
            double took = _clock.Elapsed.TotalSeconds - t0;
            int fails = 0;
            void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
            Console.WriteLine($"replan: done {p.Done}, target {p.Target} on tick {p.Tick} (the best's {p.Old}, cap {cap}), {p.Goals.Length} goals"
                + (p.Run != null ? $", route reaches {p.Run.Progress}" + (p.Run.Progress == p.Goals.Length ? $" in {p.Run.LiveSeconds:0.00} s" : "") : "") + (p.Why == null ? "" : "; " + p.Why));
            Check(took <= seconds + _pieceMinutes * 60 * 4, $"returned in {took:0} s, budget {seconds:0} s (+ a piece search's {_pieceMinutes * 4:0.#} min at most)");
            int mapped = Enumerable.Range(0, p.Goals.Length).Count(j => p.OldOf[j] >= 0);
            Check(p.OldOf.Length == p.Goals.Length && Enumerable.Range(0, p.Goals.Length).All(j => p.OldOf[j] < 0 || p.OldOf[j] < all.Length && all[p.OldOf[j]].label == p.Goals[j]),
                $"goal map: {p.OldOf.Length} entries for {p.Goals.Length} goals, {mapped} mapped to the run's goal of the same label, {p.Goals.Length - mapped} new");
            Check(p.Done || p.Why != null, "not done says why");
            Console.WriteLine(fails == 0 ? "replan: PASS" : $"replan: {fails} FAIL");
            return fails == 0 ? 0 : 1;
        }

        /// <summary>
        /// fulltas-plan --join FILE: the realized stretch of a route file --realize wrote (its goals after --from through the target,
        /// and its inputs to the target) joined to the best's way on after the target again (<see cref="Join"/>), written as --route
        /// writes; for a join that changed (bridges, stray fits) without realizing the plan again.
        /// </summary>
        int JoinAgain(string[] args, string file, int kept, int targetIndex, string outFile)
        {
            if (targetIndex < 0) throw new ArgumentException("the target is no goal of the run after --from");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            string[] labels = doc.RootElement.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToArray();
            long[] goalInput = doc.RootElement.GetProperty("goalInputTicks").EnumerateArray().Select(t => t.GetInt64()).ToArray();
            List<InputTick> inputs = Solve.ReadInputs(doc.RootElement);
            string target = _labels[targetIndex];
            int end = Array.IndexOf(labels, target, kept);
            if (end < 0 || end >= goalInput.Length || goalInput[end] < 0) throw new ArgumentException($"{file} doesn't reach {target} after goal {kept}");
            foreach (string path in _seedFiles) _seeds.Add(ReadSeed(path));
            _planBase = _runGoals;
            FullTasGoal[] all = _runGoals.Goals;
            BuildFrom(all, kept, targetIndex);
            FullTasGoal GoalOf(string label) => all.FirstOrDefault(g => g.label == label) ?? NewGoal(label)?.goal ?? throw new ArgumentException($"{file}: no goal {label}");
            List<FullTasGoal> planned = labels.Skip(kept).Take(end + 1 - kept).Select(GoalOf).ToList();
            var (run, goals, _) = Join(planned, inputs.Take((int)Math.Min(inputs.Count, goalInput[end] + 2)).ToList(), all, kept, targetIndex);
            Console.WriteLine($"joined: {run.Progress} of {goals.Length} goals" + (run.Progress == goals.Length ? $", {run.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s live ({run.Last} ticks)" : $": {run.EndReason}"));
            string routeFile = Solve.Option<string>(args, "--route", null);
            if (routeFile != null)
                File.WriteAllText(routeFile, JsonSerializer.Serialize(new { name = _name, goals, inputs = run.Inputs.Select(t => t.ToJson()),
                    goalInputTicks = run.GoalInput, goalPhysicsTicks = run.GoalPhysics, total = run.Progress == goals.Length ? run.GoalPhysics[goals.Length - 1] : -1 }));
            if (outFile != null) File.WriteAllText(outFile, JsonSerializer.Serialize(new { run = _name, goals, live = run.Progress == goals.Length ? Math.Round(run.LiveSeconds, 2) : (double?)null },
                new JsonSerializerOptions { WriteIndented = true }));
            return run.Progress == goals.Length ? 0 : 3;
        }

        /// <summary>The search from a state (<paramref name="start"/>, at <paramref name="place"/>), per the settings' mode: exact
        /// (Dijkstra), beam, or auto (exact, then the beam when it is capped, the sooner plan kept).</summary>
        EconomyPlanResult PlanFrom(SimSnapshot start, string place, IReadOnlyList<string> prefix, MoveTable moves, PlanSettings ps, Func<string, FullTasGoal> goalOf)
        {
            EconomyPlanResult Search(int width, bool stand) => EconomyPlan.Search(_eval, start, moves, place, ps.Targets, ps.Bonuses, ps.MaxStates, goalOf, ps.Seconds,
                prefix, Log, ps.Levels, ps.Only, width, stand);
            if (ps.Mode == "beam") return Search(ps.Width, ps.Stand != "off");
            var clock = Stopwatch.StartNew();
            EconomyPlanResult exact = Search(0, ps.Stand == "on");
            // A --compare step that doesn't play stops the beam too, as does an order that reaches the target alone.
            if (ps.Mode == "exact" || exact.Proven || exact.PrefixFailed || exact.Done && prefix != null && exact.Steps.Length == prefix.Count) return exact;
            string exactWhy = exact.Why;
            Log($"plan: the exact search isn't proven ({exactWhy}); the beam of {ps.Width}");
            // The beam gets what the exact search left of the settings' seconds (at least one).
            ps = ps with { Seconds = Math.Max(1, ps.Seconds - clock.Elapsed.TotalSeconds) };
            EconomyPlanResult then = Search(ps.Width, ps.Stand != "off");
            // The better of the two: the capped search's completion stands when the beam is no sooner.
            if (exact.Done && (!then.Done || exact.Tick <= then.Tick))
            {
                exact.Why += $"; the beam of {ps.Width}: " + (then.Done ? $"tick {then.Tick}" : then.Why);
                return exact;
            }
            then.Why = "exact " + exactWhy + "; " + then.Why;
            return then;
        }

        /// <summary>What <see cref="Realize"/> built: the goals played after the plan's start, the route and state after them and the
        /// physics tick there (the target's when done), else where and why it stopped; the re-plans made and each round's plan's
        /// target tick; per step played, what the plan said and what the build did.</summary>
        sealed class Realization
        {
            public bool Done;
            public string Why;
            public readonly List<FullTasGoal> Planned = new List<FullTasGoal>();
            public List<InputTick> Inputs;
            public SimSnapshot State;
            public long Tick;
            public int Rounds;
            public readonly List<long> PlanTicks = new List<long>();
            public readonly List<object> Log = new List<object>();
        }

        /// <summary>Realize plans again at most this many times; a step built more than OffTicks from its plan's tick is planned again from.</summary>
        const int MaxRounds = 3, OffTicks = 10;
        /// <summary>The target's wait for its last Cash (the plan's stand) is at most this many ticks.</summary>
        const int TargetWait = 3000;
        /// <summary>A step the plan stood for where no lap earns (Area 2's NP waits) waits at most this many ticks.</summary>
        const int StandWait = 10000;
        /// <summary>The idle ticks the last <see cref="Waited"/> that played stood (0: none).</summary>
        int _waited;

        /// <summary>
        /// Plays an economy plan with the loop builder from a real state (<paramref name="at"/>, after <paramref name="route"/>, on
        /// physics tick <paramref name="tick"/> at <paramref name="place"/>), never a drive's. Each step is played the builder's way:
        /// a lap with Lap, a record lap with RecordLap (a lap on the record's path once it is set; Lap where none plays), a buy, a
        /// reward and the target with Play (the library's pieces, the best's own ways, a piece search when the Cash is there). A
        /// quick restart step is played with the step after it: where a restart lands is the checkpoint's, and the builder's pieces
        /// take their own restarts (after a pad, onto a lap, before a record lap). Every step played is measured into
        /// <paramref name="moves"/> with the unlocks it started with: its place before (before any restart it took in) to its end, a
        /// lap's or record lap's walk to the gate, a lap's course run, a record lap's new best time. When a step ends more than
        /// OffTicks from the plan's tick for it, or doesn't play (a strict buy strays, the Cash isn't there on arrival, no way), the
        /// rest is planned again from the build's real state on the measured moves (<paramref name="replan"/>: its place, and the
        /// <paramref name="prefix"/> steps not played yet), at most MaxRounds times; after that, or when a re-plan finds none, the plan plays on as it is, and a step
        /// that doesn't play stops it. The target short of Cash waits where the step before left the player (the plan's stand),
        /// then takes the builder's way to its box.
        /// </summary>
        Realization Realize(EconomyPlanResult plan, SimSnapshot at, List<InputTick> route, long tick, string place, IReadOnlyList<string> prefix, MoveTable moves,
            Func<SimSnapshot, string, IReadOnlyList<string>, EconomyPlanResult> replan, Func<string, FullTasGoal> goalOf, long tickCap = long.MaxValue)
        {
            var z = new Realization { Inputs = route.ToList(), State = at, Tick = tick };
            var pending = (prefix ?? Array.Empty<string>()).ToList();
            var restarts = new List<PlanStep>();
            string from = place;
            int i = 0;
            z.PlanTicks.Add(plan.Done ? plan.Tick : -1);
            if (!plan.Done) { z.Why = "the plan isn't done: " + plan.Why; return z; }
            if (plan.Tick > tickCap) { z.Why = $"the plan's target on tick {plan.Tick} comes after the cap, tick {tickCap}"; return z; }
            Log($"realize: round 0, the plan's target on tick {plan.Tick}");
            bool Again(string why)
            {
                if (z.Rounds == MaxRounds) return false;
                z.Rounds++;
                var list = _kept.Concat(z.Planned).ToList();
                Track(list);
                EconomyPlanResult next = replan(z.State.WithGoal(Fresh(list.Count, list.Count)), from, pending);
                z.PlanTicks.Add(next.Done ? next.Tick : -1);
                Log($"realize: round {z.Rounds}, planned again from {from} on tick {z.Tick} ({why}): " + (next.Done ? $"the target on tick {next.Tick}" : "no plan (" + next.Why + ")"));
                z.Log.Add(new { round = z.Rounds, from, tick = z.Tick, why, plan = next.Done ? next.Tick : -1, estimated = next.Estimated });
                // No plan (a search out of time): the plan plays on as it is, as after MaxRounds; a step that doesn't play stops it.
                if (!next.Done) return false;
                if (next.Tick > tickCap) { z.Why = $"round {z.Rounds}: the plan's target on tick {next.Tick} comes after the cap, tick {tickCap}"; return false; }
                plan = next;
                i = 0;
                restarts.Clear();
                return true;
            }
            while (z.Why == null)
            {
                if (PlanOver) { z.Why = $"out of time on tick {z.Tick} at {from}"; break; }
                if (i >= plan.Steps.Length) { z.Why = "the plan's steps ran out before its target"; break; }
                PlanStep s = plan.Steps[i];
                if (s.Kind == "restart")
                {
                    // A quick restart lands at the checkpoint the run last touched: one that lands at the plan's landing is played
                    // (the steps after it measured from there), else the builder takes its own with the step after it.
                    PlanStep then = plan.Steps.Skip(i + 1).FirstOrDefault(x => x.Kind != "restart");
                    string first = then == null ? null : then.Kind == "lap" || then.Kind == "record" ? "start:" + then.Arg.Substring(then.Arg.IndexOf(':') + 1)
                        : then.Arg.Split(MoveTable.SetJoin)[0];
                    Read(z.State);
                    string restartCaps = PlayerCaps.Of(_eval).Id;
                    if (first != null && Restarted(z, s.Arg, goalOf(first), moves) is { } r)
                    {
                        int pad = r.pre.Count - 1;
                        moves.Measured(new Move(from, s.Arg, pad, "measured", false), restartCaps);
                        Log($"realize: restart {s.Arg} from {from}: planned {s.Ticks} ticks to {plan.Ends[i]}, lands there after {pad} idle ticks, on tick {z.Tick + pad}");
                        z.Log.Add(new { round = z.Rounds, kind = s.Kind, step = s.Arg, from, planTicks = s.Ticks, plan = plan.Ends[i], ticks = pad, built = z.Tick + pad });
                        z.Inputs = z.Inputs.Concat(r.pre).ToList();
                        z.State = r.state;
                        z.Tick += pad;
                        from = s.Arg;
                        restarts.Clear();
                    }
                    else restarts.Add(s);
                    if (pending.Count > 0 && pending[0] == s.Arg) pending.RemoveAt(0);
                    i++;
                    continue;
                }
                Read(z.State);
                string caps = PlayerCaps.Of(_eval).Id;
                int course = s.Kind == "lap" || s.Kind == "record" ? int.Parse(s.Arg.Substring(s.Arg.IndexOf(':') + 1), CultureInfo.InvariantCulture) : 0;
                float record = course > 0 ? BestPath(z.State, course) : 0f;
                _playedTicks = null;
                string to = EconomyPlan.PlaceOf(s);
                var played = PlayStep(s, z, goalOf, out FullTasGoal[] goals, out long walked, i < plan.Stands.Length && plan.Stands[i], i < plan.Levels.Length ? plan.Levels[i] : 1);
                long planEnd = plan.Ends[i];
                int planTicks = s.Ticks + restarts.Sum(r => r.Ticks);
                string[] froms = restarts.Select(r => r.Arg).Prepend(from).ToArray();
                string[] estimated = plan.Estimated.Where(e => froms.Any(f => e.StartsWith(f + " -> " + to + " [", StringComparison.Ordinal))
                    || e.StartsWith(s.Arg + "'s course timer", StringComparison.Ordinal)).ToArray();
                if (played == null)
                {
                    string why = $"{s.Kind} {s.Arg} from {from} on tick {z.Tick} doesn't play ({_whyNot})";
                    Log("realize: " + why);
                    z.Log.Add(new { round = z.Rounds, kind = s.Kind, step = s.Arg, from, planTicks, plan = planEnd, built = -1L, why, estimated });
                    if (!Again(why)) z.Why ??= why;
                    continue;
                }
                var (inputs, state, end) = played.Value;
                int ticks = (int)(end - z.Tick);
                // A wait for Cash (or a growing box) and an all: step's later levels are the economy's, not the walk's.
                moves.Measured(new Move(from, to, (int)walked, "measured", false), caps);
                if (course > 0 && _playedTicks?.Length > 0 && _playedTicks[0] > z.Tick)
                {
                    moves.Measured(new Move(from, "start:" + course, (int)(_playedTicks[0] - z.Tick), "measured", false), caps);
                    if (s.Kind == "lap" && _playedTicks.Length == 2) moves.Measured(new Move("start:" + course, "finish:" + course, (int)(_playedTicks[1] - _playedTicks[0]), "measured", false), caps);
                }
                float after = course > 0 ? BestPath(state, course) : 0f;
                if (s.Kind == "record" && after < record) moves.Measured(new Move("start:" + course, "record:" + course, (int)Math.Round(after / _dt), "measured", false), caps);
                z.Inputs = inputs;
                z.State = state;
                z.Planned.AddRange(goals);
                double cash = Cash(state);
                long off = end - planEnd;
                Log($"realize: {s.Kind} {s.Arg} from {from}: planned {planTicks} ticks to {planEnd} ({plan.Cash[i]:0} Cash), built {ticks} to {end} ({cash:0} Cash), {off:+0;-0}"
                    + (s.Kind == "record" ? $"; best time {record:0.00}s -> {after:0.00}s" : "") + (estimated.Length > 0 ? "; estimated: " + string.Join("; ", estimated) : ""));
                z.Log.Add(new { round = z.Rounds, kind = s.Kind, step = s.Arg, from, planTicks, plan = planEnd, planCash = plan.Cash[i], ticks, built = end, cash, off, estimated });
                if (pending.Count > 0 && pending[0] == s.Arg) pending.RemoveAt(0);
                z.Tick = end;
                from = course > 0 ? "finish:" + course : to.Split(MoveTable.SetJoin)[^1];
                restarts.Clear();
                i++;
                if (s.Kind == "target") { z.Done = true; break; }
                if (Math.Abs(off) > OffTicks) Again($"built {off:+0;-0} ticks off the plan");
            }
            return z;
        }

        /// <summary>A quick restart from the realization's state (the shortest pad the builder allows, Restarts; <paramref name="next"/>
        /// the goal the step after it heads for) when it lands at <paramref name="landing"/> (a movement table place, within its
        /// reach); null when it lands elsewhere.</summary>
        (List<InputTick> pre, SimSnapshot state)? Restarted(Realization z, string landing, FullTasGoal next, MoveTable moves)
        {
            if (next == null || !moves.Places.TryGetValue(landing, out Place p)) return null;
            var list = _kept.Concat(z.Planned).Append(next).ToList();
            Track(list);
            foreach (var (pre, state) in Restarts(z.State.WithGoal(Fresh(list.Count, list.Count - 1)), z.Inputs, list.Count - 1).Take(1))
            {
                var (at, _) = Where(state);
                return Math.Abs(at.x - p.X) <= MoveTable.PlaceReach && Math.Abs(at.y - p.Y) <= MoveTable.PlaceReach ? (pre, state) : null;
            }
            return null;
        }

        /// <summary>One plan step played with the builder from the realization's state (<see cref="Realize"/>); null when no way
        /// plays (_whyNot says where). <paramref name="walked"/>: its ticks less any wait for its Cash (-1: not known, the way
        /// waited inside it).</summary>
        (List<InputTick> inputs, SimSnapshot state, long tick)? PlayStep(PlanStep s, Realization z, Func<string, FullTasGoal> goalOf, out FullTasGoal[] goals, out long walked,
            bool stood = false, int levels = 1)
        {
            _whyNot = null;
            _waited = 0;
            walked = 0;
            if (s.Kind == "lap" || s.Kind == "record")
            {
                int n = int.Parse(s.Arg.Substring(s.Arg.IndexOf(':') + 1), CultureInfo.InvariantCulture);
                goals = new[] { goalOf("start:" + n), goalOf("finish:" + n) };
                // A record lap's way may skip the gate's own piece (a restart onto the course): no walk to the gate to measure.
                if (s.Kind == "record" && RecordLap(z.State, z.Inputs, z.Planned, goals, n, improve: false) is var (ri, rs, rt)) { _playedTicks = null; walked = rt - z.Tick; return (ri, rs, rt); }
                if (s.Kind == "record") Log($"realize: {s.Arg}: no record lap plays from here (the saved course routes, --seed); a lap instead");
                var lap = Lap(z.State, z.Inputs, z.Planned, goals);
                walked = lap == null ? 0 : lap.Value.tick - z.Tick;
                return lap;
            }
            // atoms:N touches the atom box (its buy-max buys all the GP pays for); all:box buys its box as many times as the plan's did,
            // while the next level is paid for.
            goals = EconomyPlan.PlaceOf(s).Split(MoveTable.SetJoin).Select(goalOf).ToArray();
            string[] boxes = goals.SelectMany(g => g.boxes ?? Array.Empty<string>()).ToArray();
            bool? affordable = boxes.Length == 0 ? true : Affordable(z.State, boxes);
            var played = Play(z.State, z.Inputs, z.Planned, goals, affordable == true);
            // A step the plan stood for (Area 2's buys: its Cash, or its box still growing) waits as the target's stand does.
            // Standing still where the step before left the player can buy that box again (a strict stray): then a piece search,
            // whose way waits for the Cash inside it, so its ticks aren't the walk's (walked -1: not measured).
            bool searched = false;
            if (played == null && stood && affordable != true)
            {
                played = Waited(z, goals, s.Ticks, StandWait);
                if (played == null) { played = Play(z.State, z.Inputs, z.Planned, goals, true); searched = true; }
            }
            else if (played == null && affordable == false && s.Kind == "target") played = Waited(z, goals, s.Ticks);
            else if (played == null && affordable == false) _whyNot += ", short of Cash";
            if (played != null) walked = searched ? -1 : played.Value.tick - z.Tick - _waited;
            if (played == null || s.Kind != "all") return played;
            var all = goals.ToList();
            var (inputs, state, tick) = played.Value;
            while (all.Count < levels && Affordable(state, boxes) == true && Play(state, inputs, z.Planned.Concat(all).ToList(), goals, true) is var (i2, s2, t2))
            {
                (inputs, state, tick) = (i2, s2, t2);
                all.AddRange(goals);
            }
            goals = all.ToArray();
            return (inputs, state, tick);
        }

        /// <summary>The target short of Cash on arrival (the plan's stand): the fewest idle ticks where the step before left the
        /// player (5 at a time from the first standing pays for it less the walk, <paramref name="walk"/>), after which the builder's
        /// way buys it.</summary>
        (List<InputTick> inputs, SimSnapshot state, long tick)? Waited(Realization z, FullTasGoal[] goals, int walk, int most = TargetWait)
        {
            var list = _kept.Concat(z.Planned).ToList();
            Track(list.Concat(goals).ToList());
            SimSnapshot here = z.State.WithGoal(Fresh(_goals, list.Count));
            string[] boxes = goals.SelectMany(g => g.boxes ?? Array.Empty<string>()).ToArray();
            List<InputTick> Idle(int n) => Enumerable.Repeat(new InputTick(), n).ToList();
            int paid = -1;
            for (int w = 25; w <= most && paid < 0; w += 25)
            {
                Track(list.Concat(goals).ToList());
                SimSnapshot s = AdvanceFrom(here, z.Inputs.Count, Idle(w));
                if (s == null) break;
                if (Affordable(s, boxes) == true) paid = w;
            }
            if (paid < 0) { _whyNot = $"{goals[^1].label}: short of Cash after {most} ticks standing"; return null; }
            for (int w = Math.Max(0, paid - walk); w <= paid; w += 5)
            {
                Track(list.Concat(goals).ToList());
                SimSnapshot s = w == 0 ? here : AdvanceFrom(here, z.Inputs.Count, Idle(w));
                if (s == null) continue;
                var played = Play(s, z.Inputs.Concat(Idle(w)).ToList(), z.Planned, goals, w == paid);
                if (played == null) continue;
                Log($"realize: {goals[^1].label} waited {w} ticks for its Cash");
                _waited = w;
                return played;
            }
            _whyNot = $"{goals[^1].label}: no way buys it after waiting {Math.Max(0, paid - walk)}-{paid} ticks";
            return null;
        }
    }
}
