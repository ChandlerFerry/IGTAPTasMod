using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        /// <summary><see cref="Price"/> of a box not shown yet too (a tree box whose segment hasn't grown): the beam's estimate.</summary>
        double[] PriceAhead(string box);
        /// <summary>The most ticks the economy can go paying nothing while it still earns (a clone's lap: its payouts come once a
        /// lap), 0 where income comes every tick or not at all.</summary>
        int Quiet();
        /// <summary>The most of each currency waiting can bring (NP: its cap while nothing can raise it), else infinity.</summary>
        double[] Ceiling();
        /// <summary>What the economy earns a tick standing still now, per currency as Holds counts them (null: not modelled): the
        /// beam ranks an investment by it before its money shows over a window.</summary>
        double[] Rates();
        /// <summary>The NP a tick (as Rates counts it), its cap, and the one-off NP of tiers first formed, once the waiting atoms and
        /// <paramref name="extra"/> more have landed and crafted up (null: not modelled): the beam prices an NP target in atoms.</summary>
        (double rate, double cap, double bonus)? NpAfterAtoms(int extra);
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
        /// <summary>The unattended stages' beam (Zip% 2026-10-08: beam 4 reached buytree7 in 250k states) and its state cap, in place of a
        /// seconds cap: the same plan on any machine.</summary>
        internal const int UnattendedWidth = 4, BeamStates = 1_000_000;
        /// <summary>Most ticks the target's last resort stands at its box.</summary>
        const int StandCap = 50000;
        /// <summary>Beam: most ticks the target's stand waits past its walk (a longer wait is a later state's: the beam goes on
        /// lapping and buying, and a stand from there is shorter).</summary>
        const int BeamStand = 2000;
        /// <summary>Beam: most ticks an atoms:N touch stands for its GP past its walk. Nearly every atoms option the beam plays fails
        /// (Zip% from goal 630: 98.6% of 35,081, 82% of the beam's time at BeamStand); a later state's stand is shorter.</summary>
        const int AtomStand = 300;
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

        /// <summary>
        /// Area 2's ways that open with a box (the planner's table doesn't see doors): Course 8 behind the Area 2 door
        /// (`buytree7`), Course 10's boxes behind CompBoostStrength (`buytree13`). Course 10 itself isn't lapped before the zip
        /// movers (its routes all take them): <see cref="Never"/>.
        /// </summary>
        internal static readonly Dictionary<string, string> Gates = new Dictionary<string, string>
        {
            ["lap:8"] = "buy:buytree7", ["record:8"] = "buy:buytree7", ["buy:buyclonecd8"] = "buy:buytree7", ["buy:buyclonegp8"] = "buy:buytree7",
            ["buy:buycd8"] = "buy:buytree7", ["buy:buycddigits8"] = "buy:buytree7",
            ["buy:buyreward10"] = "buy:buytree13", ["buy:buyfree10a"] = "buy:buytree13", ["buy:buyfree10b"] = "buy:buytree13",
            ["buy:buyclone10"] = "buy:buytree13", ["buy:buyclonemult10"] = "buy:buytree13", ["buy:buywatts10"] = "buy:buytree13",
            ["buy:buynp10"] = "buy:buytree13", ["buy:buyatomlevel10"] = "buy:buytree13", ["buy:buygreen10"] = "buy:buytree13",
        };
        static readonly HashSet<string> Never = new HashSet<string> { "lap:10", "record:10" };
        /// <summary>
        /// What the unattended planner offers for an Area 2 target (a box under zone 2/): every Area 2 economy box and tree box (Courses
        /// 6-10's, the atom box, the tree's), Courses 6-9's laps and record laps. The game's doors (Gates, Never) still apply; a box or
        /// lap realize can't build becomes a failed fact (MoveTable.Failed).
        /// </summary>
        internal static readonly HashSet<string> AreaTwoOffer = new HashSet<string>(
            FullTasGoal.Boxes.Where(b => b.path.StartsWith("zone 2/", StringComparison.Ordinal) || b.path.StartsWith("AtomUpgrades/", StringComparison.Ordinal)
                    || System.Text.RegularExpressions.Regex.IsMatch(b.path, @"^Courses/[Cc]ourse ([6-9]|10)/"))
                .Select(b => "buy:" + b.token)
                .Concat(new[] { 6, 7, 8, 9 }.SelectMany(n => new[] { "lap:" + n, "record:" + n })));
        /// <summary>globalStats' NP cap is this many ticks of its NP a tick.</summary>
        const double NpCapTicks = 6000;
        const int Np = (int)globalStats.Currencies.AtomicPower;
        const int Gp = (int)globalStats.Currencies.GreenPower;
        /// <summary>AtomColliderSystem's grid lands a waiting atom each 0.4 s.</summary>
        const int AtomGridTicks = 20;

        /// <summary>Where a step ends and what it buys, as a movement table To: atoms:N's the atom box, all:box's its box.</summary>
        internal static string PlaceOf(Step s) => s.Kind == "atoms" ? AtomBox : s.Kind == "all" ? "buy:" + s.Arg.Substring(4) : s.Arg;
        const int MaxAtoms = 512;
        internal const int MaxAtomsCount = MaxAtoms;

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
        /// (one growing on a tree segment comes on within a look). A price over what waiting can hold (IEconomy.Ceiling: NP's cap)
        /// stalls at once: the stand would fill the cap and wait out a look for nothing.</summary>
        /// <summary>Why a target's buy gave up (the first StandLogs a process): the planner's question when every target fails.</summary>
        static void StandLog<TSnap>(PlanOption o, string box, IEconomy<TSnap> economy, long tick, int w, string why)
        {
            if (!o.Target || System.Threading.Interlocked.Increment(ref _standLogs) > StandLogs) return;
            double[] price = economy.Price(box), held = economy.Holds(), ceiling = economy.Ceiling();
            string need = price == null ? "no price (not buyable)" : string.Join(", ", Enumerable.Range(0, price.Length).Where(c => price[c] > 0)
                .Select(c => $"{(globalStats.Currencies)c} {held[c]:G4} of {price[c]:G4}{(double.IsPositiveInfinity(ceiling[c]) ? "" : $" (ceiling {ceiling[c]:G4})")}"));
            Console.WriteLine($"plan: target {box} gave up on tick {tick} after {w} ticks standing ({why}): {need}");
        }
        const int StandLogs = 10;
        static int _standLogs;

        static bool Stalled<TSnap>(IEconomy<TSnap> economy, string box, int levels, ref double[] was)
        {
            double[] price = economy.Price(box, levels), held = economy.Holds();
            if (price == null)
            {
                bool still = was != null && was.Length == 0;
                was = Array.Empty<double>();
                return still;
            }
            double[] ceiling = economy.Ceiling();
            for (int c = 0; c < price.Length; c++)
                if (price[c] > ceiling[c]) return true;
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
            /// <summary>The levels its `all:` step bought after its first (a goal each); an `atoms` step's: the atoms its touch's
        /// buy-max pays for after the first (no goal: Simulation.Bought's continuing), which realize waits for.</summary>
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
                // An atom touch buys every atom the GP pays for (buy-max): realize stands for as many (PlayStep's levels).
                int could = 0;
                if (last && o.Step.Kind == "atoms")
                    while (could < MaxAtoms && Covered(economy.Price(box, could + 1), economy.Holds())) could++;
                tick++;
                if (economy.Buy(box)) { extra = Math.Max(0, could - 1); continue; }
                if (!o.Stand || !last) { StandLog(o, box, economy, tick, 0, "not a stand"); return null; }
                was = null;
                for (int w = 0; ; w++)
                {
                    if (w == StandCap || tick >= limit || o.Patient && w % look == 0 && Stalled(economy, box, 1, ref was))
                    {
                        StandLog(o, box, economy, tick, w, w == StandCap ? "stood its cap" : tick >= limit ? "past its limit" : "stalled");
                        return null;
                    }
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
            int width = 0, Func<string, (double[] price, int ticks)> remaining = null, Func<string, int> atomDetour = null)
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
                result.Goals = path.SelectMany(n => n.Extra == 0 || n.Via.Step.Kind == "atoms" ? n.Via.Goals : n.Via.Goals.Concat(Enumerable.Repeat(n.Via.Goals[^1], n.Extra))).ToArray();
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
                double[] ceiling = economy.Ceiling();
                // What the state earns a tick now (its clones, NP): an investment counts at once, not once its money shows over the
                // window (Zip%'s plans from buytree0 never levelled buytree0's green clone chance, all of Area 2's GP).
                double[] model = economy.Rates();
                double wait = 0;
                bool capped = false;
                for (int c = 0; c < price.Length; c++)
                {
                    double missing = price[c] - n.Holds[c];
                    if (missing <= 0 || n.Tick == a.Tick) continue;
                    if (c == Np && economy.NpAfterAtoms(0) != null)
                    {
                        double np = NpWait(n, a, price[c], model, atomDetour?.Invoke(n.Place) ?? 0);
                        if (double.IsPositiveInfinity(np)) { result.NoIncome++; np = NoIncome; }
                        wait = Math.Max(wait, np);
                        continue;
                    }
                    // GP at the clones' own rate (AtomRate): a state that touched the atom box mid-way to a GP price ranked as if its reset
                    // courses still paid, and the beam drifted into touches (Zip%'s buyatomlevel9 stage: every stand for its 6M GP stalled).
                    double rate = AtomRate(n, a, c, model);
                    // A price over the currency's cap (Area 2's NP, capped at NpCapTicks of its rate) is reached only by raising the
                    // rate (atoms): ranked by that rate, the cap's. The window's earnings stop at the cap, so every state read as no
                    // income and Cash broke the ties (Zip%'s plan to buytree5 laid up 3.3M Cash at NP's cap, 693, for 30 minutes).
                    if (c < ceiling.Length && !double.IsPositiveInfinity(ceiling[c]) && price[c] > ceiling[c])
                    {
                        rate = ceiling[c] / NpCapTicks;
                        capped = true;
                    }
                    if (rate <= 0) { result.NoIncome++; wait = Math.Max(wait, NoIncome); continue; }
                    wait = Math.Max(wait, missing / rate);
                }
                // Capped under the price, the rate rises only with atoms: the next atom's price still missing (GP) counts on top, so
                // a state saving for it ranks over one laying up Cash.
                if (capped && economy.Price(AtomBox) is double[] atom)
                    for (int c = 0; c < atom.Length; c++)
                    {
                        double missing = atom[c] - n.Holds[c];
                        if (missing <= 0 || n.Tick == a.Tick) continue;
                        double rate = AtomRate(n, a, c, model);
                        wait += rate > 0 ? missing / rate : NoIncome;
                    }
                return wait + ticks;
            }
            // An atom's price, paid in GP, at the state's own GP income: the clones' (Economy.CloneRates, green at its expected share), the
            // only GP there is. Not the window's or the root's: right after an atom touch the courses' clones are gone (doNewAtomPrestige)
            // and the window still holds their income, so a state that touched short of the NP cap ranked as if it still earned and the
            // beam filled with them (Zip% from goal 998: every leading state at NP's cap, 78 a tick, 0 GP). Other currencies as Rate.
            double AtomRate(Node<TSnap> n, Node<TSnap> a, int c, double[] model)
            {
                if (c == Gp && model != null && c < model.Length) return model[c];
                double rate = Math.Max(Rate(n, a, c), model != null && c < model.Length ? model[c] : 0);
                return rate > 0 ? rate : Rate(n, root, c);
            }
            // NP comes only from atoms, and an atom's worth jumps as tiers craft up (12 white atoms, 384 GP, make 17 NP a tick, 6 make
            // 0.24 and cap under buytree5's 22,500): the soonest of buying k more atoms (their GP at n's GP income, the 0.4 s grid
            // landing each) and then earning the NP at the rate they craft up to, its cap over the price. So a green box or a
            // buytree0 level, raising the GP income, ranks at once (the NP window alone saw no income below a merge).
            double NpWait(Node<TSnap> n, Node<TSnap> a, double npPrice, double[] model, int detour)
            {
                double best = double.PositiveInfinity;
                // From the fewest atoms whose NP cap holds the price, found by halves (the cap only rises with atoms; fewer can't buy it).
                int lo = 0, hi = MaxAtoms + 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (economy.NpAfterAtoms(mid) is var (_, midCap, _) && midCap >= npPrice) hi = mid;
                    else lo = mid + 1;
                }
                for (int k = lo; k <= MaxAtoms; k++)
                {
                    double gp = 0;
                    if (k > 0)
                    {
                        if (!(economy.Price(AtomBox, k) is double[] atoms)) break;
                        for (int c = 0; c < atoms.Length; c++)
                        {
                            double missing = atoms[c] - n.Holds[c];
                            if (missing <= 0) continue;
                            double rate = AtomRate(n, a, c, model);
                            gp = Math.Max(gp, rate > 0 ? missing / rate : double.PositiveInfinity);
                        }
                    }
                    // The atoms' price only rises with k: no more atoms beat the best once their GP alone takes longer.
                    if (gp >= best) break;
                    if (!(economy.NpAfterAtoms(k) is var (np, cap, bonus)) || cap < npPrice || np <= 0) continue;
                    // More atoms are a touch away: the walk to the atom box and on to the next target, past the walk there straight.
                    best = Math.Min(best, gp + (k > 0 ? detour : 0) + AtomGridTicks * k + Math.Max(0, npPrice - n.Holds[Np] - bonus) / np);
                }
                return best;
            }
            // The buy pre-check, with the economy at n: the option's boxes cost more than n holds plus PrecheckMargin times n's
            // income over the step's ticks (a stand: and BeamStand more, a patient one StandCap; atoms:N: its N levels, an atom
            // touch's stand over AtomStand, as the beam plays it). A buy right after a buy is always played
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
                double ticks = o.Step.Ticks + (o.Step.Kind == "atoms" ? AtomStand : o.Patient ? StandCap : o.Stand ? BeamStand : 0);
                for (int c = 0; c < need.Length; c++)
                {
                    double missing = need[c] - n.Holds[c];
                    double rate = Rate(n, a, c);
                    if (missing > 0 && rate > 0 && missing > PrecheckMargin * rate * ticks) return true;
                    // A patient stand for a currency nothing has paid since the search's start (GP with no clones) never ends.
                    if (missing > 0 && o.Patient && rate <= 0 && Rate(n, root, c) <= 0) return true;
                    // An atom touch short of its price with nothing coming in over the window: its walk and stand are played to fail.
                    if (missing > 0 && rate <= 0 && o.Step.Kind == "atoms") return true;
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
                    // With no seconds cap (unattended) the beam stops at maxStates played; every other caller has one, as before.
                    if (clock.Elapsed.TotalSeconds >= maxSeconds || double.IsPositiveInfinity(maxSeconds) && result.States >= maxStates)
                    {
                        why = $"stopped after {result.States} states ({clock.Elapsed.TotalSeconds:0} s) at tick {priority.tick}";
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
                        long? end = Played(e.parent, e.via, out _, out int extra, e.via.Stand && (!e.via.Patient || e.via.Step.Kind == "atoms") ? e.parent.Tick + e.via.Step.Ticks + (e.via.Step.Kind == "atoms" ? AtomStand : BeamStand) : long.MaxValue);
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
                            + string.Join(", ", result.Profile.Select(p => $"{p.Key} {p.Value.played}/{p.Value.failed}/{p.Value.dominated}"))
                            + $"; GP {n.Holds[1]:G3}, NP {n.Holds[2]:G3}" + (economy.Rates() is double[] r ? $" (a tick: Cash {r[0]:G3}, GP {r[1]:G3}, NP {r[2]:G3}; F {n.F - n.Tick:0})" : "") + $"; its steps: {Trail(n)}");
                    }
                }
                result.Exhausted = why == null;
                return Finish(start, false, $"beam {width}: " + (why ?? "the queue ran out") + "; no plan reached the target");
            }
        }

        /// <summary>A node's steps from the start, runs of one step as "step xN" (the beam's progress line).</summary>
        static string Trail<TSnap>(Node<TSnap> n)
        {
            var steps = new List<string>();
            for (; n?.Via != null; n = n.Parent) steps.Add(n.Via.Step.Arg);
            steps.Reverse();
            var runs = new List<string>();
            for (int i = 0, j; i < steps.Count; i = j)
            {
                for (j = i + 1; j < steps.Count && steps[j] == steps[i]; j++) { }
                runs.Add(j - i > 1 ? $"{steps[i]} x{j - i}" : steps[i]);
            }
            return string.Join(", ", runs);
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
            var built = new Dictionary<(string place, string caps, int tree, int orbs), List<(PlanOption option, string[] boxes)>>();
            upgradeBox[] treeBoxes = TreeBoxes(sim);

            string Estimate(string from, Move m, string caps) => $"{from} -> {m.To} [{caps}] {m.Ticks} ticks";
            List<(PlanOption option, string[] boxes)> Built(string place, string caps)
            {
                // The tree's level as FullTas.TreeLevel counts it: the tree boxes bought.
                int tree = MoveTable.TreeGroup(TreeLevel(treeBoxes)), orbs = OrbLevel(sim);
                if (built.TryGetValue((place, caps, tree, orbs), out var list)) return list;
                moves.AtTreeLevel(tree);
                moves.AtOrbs(orbs);
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
                    Move m = to == place ? (moves.HasFailed(place, place, caps) ? null : new Move(place, place, 1, "here", false)) : moves.Best(place, to, caps);
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
                        // Standing at a landing, a quick restart lands there again: no tick, no change, and the gate refuses two in a row.
                        if (place.StartsWith("restart@", StringComparison.Ordinal)) continue;
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
                    // Not from the atom box itself: the touch's buy-max routine buys the rest a step at a time, and those buys are no
                    // goal (Simulation.Bought's continuing), so a second atoms step there plans a buy the run never makes (Zip%'s
                    // realize stuck at goal 831, 2026-10-06); the player falls from the box after a touch anyway.
                    if (to == AtomBox && to != place && (only == null || only.Contains(AtomBox)))
                        for (int n = 1; n <= MaxAtoms; n *= 2)
                            list.Add((new PlanOption(new Step("atoms", "atoms:" + n, m.Ticks), to, boxes, null, false, boxes, estimated.ToArray(), Stand: true, Levels: n), boxes));
                }
                // The target stands at its box until the Cash is there: everywhere with --stand, else only as the last resort where
                // there's nowhere to lap from. Where no lap is offered, and for a box paid in GP or NP (Area 2's: no lap pays them, the
                // atoms do), every buy stands for its price (nothing earns it by moving on), as long as what it costs comes in (Patient).
                for (int i = 0; i < list.Count; i++)
                {
                    PlanOption o = list[i].option;
                    if ((!laps || economy.AtomPaid(o.Buys)) && o.Buys.Length > 0 && o.Step.Kind != "reward") list[i] = (o with { Stand = true, Patient = true }, list[i].boxes);
                    else if (standAtTarget && o.Target) list[i] = (o with { Stand = true }, list[i].boxes);
                }
                return built[(place, caps, tree, orbs)] = list;
            }
            List<PlanOption> Options(string place, bool standOnly)
            {
                var list = new List<PlanOption>();
                foreach (var (o, boxes) in Built(place, PlayerCaps.Of(sim).Id))
                {
                    // A patient stand waits for a box still growing on its tree segment too.
                    if (standOnly && !o.Target || boxes != null && !economy.Active(boxes) && !(o.Patient && economy.Growing(boxes))) continue;
                    if (Never.Contains(o.Step.Arg) || Gates.TryGetValue(o.Step.Arg, out string door) && economy.Level(door) == 0
                        || boxes != null && boxes.Any(b => Gates.TryGetValue(b, out string boxDoor) && economy.Level(boxDoor) == 0)) continue;
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
            // The ticks an atom touch adds on the way to the next target (the beam's estimate charges it to a state that needs atoms): saving
            // states that never paid it out-ranked the ones that had touched, bucket after bucket, and the plan never touched (Zip% from
            // goal 998: 265k GP held at tick 98k, the touch worth buytree7 on tick 74,696 never taken).
            int AtomDetour(string place)
            {
                string next = targets.FirstOrDefault(t => t == target || economy.Level(t) == bought[t]);
                string caps = PlayerCaps.Of(sim).Id;
                if (next == null || place == AtomBox) return 0;
                Move toAtom = moves.Best(place, AtomBox, caps), on = moves.Best(AtomBox, next, caps), direct = moves.Best(place, next, caps);
                return toAtom == null || on == null ? 0 : Math.Max(0, toAtom.Ticks + on.Ticks - (direct?.Ticks ?? 0));
            }
            (double[] price, int ticks) Remaining(string place)
            {
                var price = new double[7];
                string next = null;
                foreach (string t in targets)
                {
                    if (t != target && economy.Level(t) != bought[t]) continue;
                    // A tree box its segment hasn't grown yet costs what it will (buytree5's 22,500 NP left out, the beam saw only the
                    // tree's cheap boxes before it and laid up Cash at NP's cap).
                    double[] p = economy.Price(t) ?? economy.PriceAhead(t);
                    for (int c = 0; p != null && c < p.Length; c++) price[c] += p[c];
                    next ??= t;
                }
                return (price, next == null ? 0 : moves.Best(place, next, PlayerCaps.Of(sim).Id)?.Ticks ?? 0);
            }
            // Green clones at their expected share while planning (Economy.ExpectedGreens), never in a run that is built or judged.
            Economy model = sim.Shop.Economy;
            if (model != null) model.ExpectedGreens = true;
            PlanResult r;
            try
            {
                r = Search(economy, startTick, startPlace, p => Options(p, false), maxStates, maxSeconds, prefix, log, p => Options(p, true).FirstOrDefault(),
                    width, Remaining, AtomDetour);
            }
            finally { if (model != null) model.ExpectedGreens = false; }
            sim.RestoreForDrive(start);
            return r;
        }

        /// <summary>The targets with the boxes whose buys grow a tree box target's tree down to it (Shop.TreeWay) put first, the ones
        /// the state at <paramref name="sim"/> hasn't bought, root first: a tree box is bought only once its segment has grown.</summary>
        /// <summary>The refill orbs' level (unlockJiggleDrops + moreRefreshOrbs, the run's own: Simulation.RefillOrbState).</summary>
        internal static int OrbLevel(Simulation sim) => sim.Shop.Economy is Economy e
            ? (int)(e.GlobalOf(globalStats.globalUpgradeSet.unlockJiggleDrops) + e.GlobalOf(globalStats.globalUpgradeSet.moreRefreshOrbs)) : 0;

        /// <summary>The tree boxes of the run's world (buytree0-27), each once.</summary>
        internal static upgradeBox[] TreeBoxes(Simulation sim) => FullTasGoal.Boxes.Where(b => b.token.StartsWith("buytree", StringComparison.Ordinal))
            .Select(b => sim.Shop.Box(FullTasGoal.BoxId(b.path, b.sibling))).Where(b => b != null).Distinct().ToArray();

        /// <summary>The tree's level as FullTas.TreeLevel counts it: the tree boxes bought (the boxes' state as the world holds it now).</summary>
        internal static int TreeLevel(upgradeBox[] treeBoxes) => treeBoxes.Count(b => b.TimesUsed > 0);

        internal static string[] TreeTargets(Simulation sim, string[] targets, Func<string, FullTasGoal> goalOf)
        {
            var tokens = FullTasGoal.Boxes.ToDictionary(b => FullTasGoal.BoxId(b.path, b.sibling), b => b.token);
            return targets.SelectMany(t => (goalOf(t)?.boxes is [string box] ? sim.Shop.TreeWay(box) : new List<string>())
                .Where(id => sim.Shop.Box(id).TimesUsed == 0 && tokens.ContainsKey(id)).Select(id => "buy:" + tokens[id]).Append(t)).Distinct().ToArray();
        }

        /// <summary>A lap's course timer, in ticks: the lap less its walk to the gate when both are measured (never shorter than
        /// the table's run from the gate to the finish), else that run; else the saved record's timer, else the run estimated, else
        /// the whole lap (listed as estimated).</summary>
        static int LapCourseTicks(MoveTable moves, string from, string n, Move lap, string caps, List<string> estimated)
        {
            string gate = "start:" + n;
            Move run = moves.Best(gate, "finish:" + n, caps);
            Move walk = from == gate ? new Move(from, gate, 0, "here", false) : moves.Best(from, gate, caps);
            int least = run != null && !run.Estimated ? run.Ticks : 1;
            if (!lap.Estimated && walk != null && !walk.Estimated) return Math.Max(least, lap.Ticks - walk.Ticks);
            if (run != null && !run.Estimated) return run.Ticks;
            // A course the run never timed: the saved record's timer, else its gate to its finish estimated (not the walk there).
            Move known = moves.Best(gate, "record:" + n, caps) ?? run;
            if (known != null)
            {
                estimated.Add($"lap:{n}'s course timer [{caps}]: {known.Ticks} ticks, " + (known == run ? "its gate to its finish estimated" : "the saved record's"));
                return known.Ticks;
            }
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

        /// <summary>The farthest a box's touch spot may be from its place for a buy to touch there.</summary>
        const float NearPlace = 300f;

        public bool Buy(string label)
        {
            _restored = null;
            string[] ids = Ids(label);
            if (ids.Length != 1) throw new InvalidOperationException($"{label}: {ids.Length} boxes, the planner buys one a tick");
            // Touched from the box's touch spot when the place doesn't reach it: a place is where the player stood when a run bought
            // the box (from its side, jumping through), and Zip%'s buytree0 place stands 135 u off the box's trigger, so the planner
            // never bought a second level (each +1% green clone chance: five more made six times the GP).
            Vector2 at = At(label);
            // Only a touch spot near the place: a tree box just grown drops from 2000 u above for ~100 ticks, and its touch spot would
            // buy it in mid-air.
            if (_sim.Shop.Box(ids[0]) is upgradeBox box && !_sim.Reaches(box, at) && _sim.TouchSpot(box) is Vector2 spot
                && Vector2.Distance(spot, at) < NearPlace) at = spot;
            return _sim.EconomyBuy(ids[0], at);
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

        public double[] Rates()
        {
            double[] rates = _sim.Shop.Economy?.CloneRates(_sim.World.FixedDeltaTime);
            if (rates == null) return null;
            globalStats stats = Singleton<globalStats>.Instance;
            // globalStats.DoConstantCurrency's NP below its cap (the cap's own rate: Ceiling).
            // globalStats.DoConstantCurrency's NP below its cap (the cap's own rate: Ceiling).
            if (stats != null) rates[(int)globalStats.Currencies.AtomicPower] = stats.NPrewardPerTick * NpSpeed(stats);
            return rates;
        }

        static double NpSpeed(globalStats stats) => (1.0 + globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.NpGenerationSpeed])
            * stats.GameSpeed * (globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.compBoostDoublesNP] != 0.0 ? stats.EffectOfNpCompBoost : 1.0);

        public (double rate, double cap, double bonus)? NpAfterAtoms(int extra)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if (stats == null || !(_sim.Shop.Economy?.NpAfterAtoms(extra) is var (rate, cap, bonus))) return null;
            return (rate * NpSpeed(stats), cap, bonus);
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
        public double[] PriceAhead(string label) => _sim.Shop.Need(Ids(label), 1, shown: false);

        /// <summary>Twice the longest clone path of a course with clones (a clone pays at its path's end, the next one spawns an
        /// interval later, at most a path), in ticks.</summary>
        public double[] Ceiling()
        {
            var ceiling = new double[7];
            Array.Fill(ceiling, double.PositiveInfinity);
            if (_sim.Shop.Economy?.NpCapped == true) ceiling[(int)globalStats.Currencies.AtomicPower] = _sim.NpCapAfterPending();
            return ceiling;
        }

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

        /// <summary>One of these labels' boxes is paid in GP or NP (Area 2's currencies, which no course lap pays).</summary>
        public bool AtomPaid(string[] labels) => labels.SelectMany(Ids).Select(_sim.Shop.Box).Any(b => b != null
            && Shop.Currency(b) is globalStats.Currencies.GreenPower or globalStats.Currencies.AtomicPower);

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
        internal static int EconomyPlanCommand(string[] args)
        {
            if (Solve.Option<string>(args, "--try", null) == null)
                throw new ArgumentException("fulltas-plan keeps only --try STEPS (a diagnostic); `fulltas <trace>` runs the economy stages");
            return new FullTas(args) { _unattended = true }.PlanEconomy(args);
        }

        /// <summary>
        /// fulltas &lt;trace&gt; with no flags (docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md, section 4): from the best's
        /// furthest goal, plan to its next buy and realize, install when the build gets further, again, until the best reaches every
        /// goal, the next goal isn't an economy buy (then the reach solve, Search), or two stages in a row install nothing (the second
        /// with the beam twice as wide). Each stage opens the run afresh. Writes fulltas/&lt;run&gt;.report.json after each stage.
        /// </summary>
        internal static int Unattended(string[] args)
        {
            var report = new List<object>();
            for (int stage = 1, fails = 0; fails < 2; stage++)
            {
                var run = new FullTas(args) { _unattended = true, _stageWidth = fails == 1 ? EconomyPlan.UnattendedWidth * 2 : EconomyPlan.UnattendedWidth };
                Console.WriteLine($"stage {stage}" + (fails > 0 ? $" ({fails} without progress before it)" : ""));
                int code;
                try { code = run.PlanEconomy(args); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    run._stageReport = new { stop = "error: " + ex.Message };
                    WriteReport(run, report);
                    throw;
                }
                WriteReport(run, report);
                if (run._allReached) return 0;
                if (code == NotEconomy) return new FullTas(args).Search();
                fails = run._installed ? 0 : fails + 1;
                if (fails == 2)
                {
                    report.Add(new { stop = "two stages in a row got no further" });
                    File.WriteAllText(run.ReportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            Console.WriteLine("unattended: two stages in a row got no further; see the report");
            return 3;
        }

        static void WriteReport(FullTas run, List<object> report)
        {
            report.Add(run._stageReport);
            Directory.CreateDirectory(Path.GetDirectoryName(run.ReportFile));
            File.WriteAllText(run.ReportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>PlanEconomy's answer when the next goal is no economy buy (none, or one held already): the reach solve's.</summary>
        const int NotEconomy = 4;
        /// <summary>A target this far off in ticks of GP income for the atoms it needs is out of reach (an enabler first).</summary>
        const double EnablerHorizon = 50000;
        /// <summary>
        /// A target realize found no way to in this world (a failed fact for it at the tree's group and the orbs' level) gets the cheapest
        /// box that adds a refill orb level, offered and under its cap: the orbs change what the player can reach (Zip%'s zip movers' box,
        /// 512 u past every search: the orb beside it needs unlockJiggleDrops + moreRefreshOrbs over 1). (null, null): none needed or none.
        /// </summary>
        (string label, string why) ReachEnabler(string target, Func<string, FullTasGoal> goalOf, ISet<string> offer, MoveTable moves, double[] rates)
        {
            moves.AtTreeLevel(MoveTable.TreeGroup(EconomyPlan.TreeLevel(EconomyPlan.TreeBoxes(_eval))));
            moves.AtOrbs(EconomyPlan.OrbLevel(_eval));
            int failed = moves.FailuresHere(target);
            if (failed == 0) return (null, null);
            var orbBoxes = new HashSet<globalStats.globalUpgradeSet> { globalStats.globalUpgradeSet.unlockJiggleDrops, globalStats.globalUpgradeSet.moreRefreshOrbs };
            (string label, double ticks) best = (null, double.PositiveInfinity);
            foreach (string label in offer.Where(l => l.StartsWith("buy:", StringComparison.Ordinal)).OrderBy(l => l, StringComparer.Ordinal))
            {
                if (!(goalOf(label)?.boxes is [string id]) || !(_eval.Shop.Box(id) is upgradeBox box) || !orbBoxes.Contains(box.globalUpgrade)) continue;
                if (moves.HasFailed(MoveTable.TrapFrom, label, PlayerCaps.Of(_eval).Id)) continue;
                if (!(_eval.Shop.Need(new[] { id }, 1, shown: false) is double[] cost)) continue;
                double ticks = 0;
                for (int c = 0; c < cost.Length && c < rates.Length; c++)
                {
                    double missing = cost[c] - _eval.Shop.Holds((globalStats.Currencies)c);
                    if (missing > 0) ticks = Math.Max(ticks, missing / Math.Max(rates[c], 1e-6));
                }
                if (ticks < best.ticks) best = (label, ticks);
            }
            return best.label == null ? (null, null) : (best.label, $"no way to it from {failed} place(s) at orb level {EconomyPlan.OrbLevel(_eval)}: a refill orb level first");
        }

        /// <summary>A place is a trap after failed walks to this many targets from it, a quick restart landing back at it.</summary>
        const int TrapFailures = 3;
        /// <summary>This stage plans from before a trap (<see cref="Trapped"/>): it installs once it reaches its target, its goals fewer.</summary>
        bool _rollback;

        /// <summary>Why the place of goal <paramref name="kept"/> is a trap in its world, or null: failed walks to TrapFailures targets from
        /// it and every quick restart known from it landing back beside it (within 300 u).</summary>
        string Trapped(int kept, MoveTable moves)
        {
            string place = _labels[kept - 1];
            if (!moves.Places.TryGetValue(place, out Place p)) return null;
            Read(GoalState(kept).at);
            string caps = PlayerCaps.Of(_eval).Id;
            moves.AtTreeLevel(MoveTable.TreeGroup(EconomyPlan.TreeLevel(EconomyPlan.TreeBoxes(_eval))));
            moves.AtOrbs(EconomyPlan.OrbLevel(_eval));
            List<string> landings = moves.Restarts(place, caps).ToList();
            if (landings.Count == 0 || landings.Any(l => !moves.Places.TryGetValue(l, out Place q) || Math.Abs(q.X - p.X) > 300 || Math.Abs(q.Y - p.Y) > 300)) return null;
            int failed = moves.FailuresFrom(landings.Append(place).ToHashSet());
            Log($"trap check at goal {kept} {place}: every quick restart lands beside it ({string.Join(", ", landings)}); walks to {failed} target(s) failed");
            return failed < TrapFailures ? null : $"walks to {failed} targets failed from it and a quick restart lands back beside it";
        }

        /// <summary>The best's income is read over its last goals spanning at least this many ticks.</summary>
        const long IncomeSpan = 5000;

        /// <summary>What the best earned a tick per currency over its last goals to goal <paramref name="kept"/> (at least IncomeSpan ticks:
        /// laps, clones, atoms; Shop.Gained, a reset's loss left out): the income the economy rebuilds to after an atom touch, where the
        /// clones' rate right after one is nothing (Zip%'s buyatomlevel9, 60M GP, ranked by price against Cash boxes on that).</summary>
        double[] RecentIncome(int kept)
        {
            long end = _best.GoalPhysics[kept - 1];
            int from = kept - 1;
            while (from > 1 && end - _best.GoalPhysics[from - 1] < IncomeSpan) from--;
            double[] Gained(int goal)
            {
                Read(GoalState(goal).at);
                return Enumerable.Range(0, 7).Select(c => _eval.Shop.Gained((globalStats.Currencies)c)).ToArray();
            }
            double[] a = Gained(from), b = Gained(kept);
            double ticks = Math.Max(1, end - _best.GoalPhysics[from - 1]);
            return a.Zip(b, (x, y) => Math.Max(0, y - x) / ticks).ToArray();
        }

        /// <summary>
        /// An NP target the atoms can't reach at today's multipliers (docs/zip-handoff.md: buytree16's 625M NP needs ~104k NP a tick,
        /// two tier-3 atoms with Course 10's NP boxes and both atom-level boxes; at goal 1159's multipliers ~330 more atoms, GP of
        /// 1.2^370): over NP's cap even with what's in flight, and the GP for the atoms whose cap would hold it more than
        /// EnablerHorizon ticks of the clones' GP away. Then the box that raises NP's own equation (atom-level chance, nuclear power,
        /// triple threat: globalStats.RecalcNpRewardPerTick, AtomColliderSystem.determineAtomTier) soonest paid for at today's income,
        /// offered, its door open and under its cap; (null, why) when the target is in reach or no such box is.
        /// </summary>
        (string label, string why) Enabler(int kept, SimSnapshot at, string target, Func<string, FullTasGoal> goalOf, ISet<string> offer, MoveTable moves)
        {
            double[] rates = RecentIncome(kept);
            Read(at);
            if (ReachEnabler(target, goalOf, offer, moves, rates) is var (reach, reachWhy) && reach != null) return (reach, reachWhy);
            int np = (int)globalStats.Currencies.AtomicPower, gp = (int)globalStats.Currencies.GreenPower;
            if (!(goalOf(target)?.boxes is string[] targetBoxes) || !(_eval.Shop.Need(targetBoxes, 1, shown: false) is double[] price) || price[np] <= 0) return (null, null);
            if (price[np] <= Math.Max(_eval.NpCapAfterPending(), _eval.Shop.Holds(globalStats.Currencies.AtomicPower))) return (null, null);
            Economy model = _eval.Shop.Economy;
            if (model == null) return (null, null);
            string atomId = FullTasGoal.BoxId("AtomUpgrades/AtomUpgrade", 0);
            int k = 1;
            while (k <= EconomyPlan.MaxAtomsCount && !(model.NpAfterAtoms(k) is var (_, cap, _) && cap >= price[np])) k++;
            double gpNeed = k > EconomyPlan.MaxAtomsCount ? double.PositiveInfinity : _eval.Shop.Need(new[] { atomId }, k, shown: false)?[gp] ?? double.PositiveInfinity;
            double gpTicks = (gpNeed - _eval.Shop.Holds(globalStats.Currencies.GreenPower)) / Math.Max(rates[gp], 1e-9);
            if (gpTicks <= EnablerHorizon) return (null, null);
            string why = k > EconomyPlan.MaxAtomsCount ? $"no {EconomyPlan.MaxAtomsCount} atoms make NP's cap {price[np]:G3}"
                : $"{k} atoms for NP's cap {price[np]:G3}, {gpNeed:G3} GP, {gpTicks:G3} ticks of GP at {rates[gp]:G3} a tick";
            // NP's own equation, and a course kept through atom touches (each touch resets Courses 6 and 7: the GP and Cash the later
            // enablers cost start again from nothing, which nothing in the beam's estimate values).
            var raises = new HashSet<globalStats.globalUpgradeSet>
            {
                globalStats.globalUpgradeSet.atomLevelChance, globalStats.globalUpgradeSet.increasedNuclearPower, globalStats.globalUpgradeSet.tripleThreatIncrease,
                globalStats.globalUpgradeSet.exemptCourseFromAtomPrestige,
            };
            double npRate = Singleton<globalStats>.Instance?.NPrewardPerTick ?? 0;
            // An NP price: waited for under NP's cap; over it, the GP for the atoms whose cap holds it (at the recent GP income) and the
            // NP at their rate, as the beam's NpWait reaches it (Zip%'s buytree7: atoms, then the wait).
            double NpTicks(double cost, double missing)
            {
                if (cost <= _eval.NpCapAfterPending()) return npRate > 0 ? missing / npRate : double.PositiveInfinity;
                for (int n = 1; n <= EconomyPlan.MaxAtomsCount; n++)
                    if (model.NpAfterAtoms(n) is var (rate, cap, _) && cap >= cost)
                    {
                        double gpFor = Math.Max(0, (_eval.Shop.Need(new[] { atomId }, n, shown: false)?[gp] ?? double.PositiveInfinity) - _eval.Shop.Holds(globalStats.Currencies.GreenPower));
                        return gpFor / Math.Max(rates[gp], 1e-6) + (rate > 0 ? missing / rate : double.PositiveInfinity);
                    }
                return double.PositiveInfinity;
            }
            // The GP the atoms still need for the target's NP cap after a box's effect (its upgrade raised; an atom-level box also clears
            // the collider and resets the atoms' price to base), in ticks of the recent GP income: what an enabler buys the climb.
            double AtomsTicksAfter(globalStats.globalUpgradeSet u, double by, double gpLeft)
            {
                bool cleared = u == globalStats.globalUpgradeSet.atomLevelChance;
                for (int n = 1; n <= EconomyPlan.MaxAtomsCount; n++)
                    if (model.NpAfterAtoms(n, u, by, cleared) is var (_, cap, _) && cap >= price[np])
                    {
                        double gpFor = cleared ? _eval.Shop.FromBase(atomId, n) : _eval.Shop.Need(new[] { atomId }, n, shown: false)?[gp] ?? double.PositiveInfinity;
                        return Math.Max(0, gpFor - gpLeft) / Math.Max(rates[gp], 1e-6);
                    }
                return double.PositiveInfinity;
            }
            (string label, double ticks) best = (null, double.PositiveInfinity);
            var scored = new List<(string label, double afford, double after)>();
            foreach (string label in offer.Where(l => l.StartsWith("buy:", StringComparison.Ordinal)).OrderBy(l => l, StringComparer.Ordinal))
            {
                if (!(goalOf(label)?.boxes is [string id]) || !(_eval.Shop.Box(id) is upgradeBox box) || !raises.Contains(box.globalUpgrade)) continue;
                if (EconomyPlan.Gates.TryGetValue(label, out string door) && goalOf(door)?.boxes is [string doorId] && _eval.Shop.Box(doorId) is upgradeBox d && d.TimesUsed == 0) continue;
                if (!(_eval.Shop.Need(new[] { id }, 1, shown: false) is double[] cost)) continue;
                double ticks = 0;
                for (int c = 0; c < cost.Length && c < rates.Length; c++)
                {
                    double missing = cost[c] - _eval.Shop.Holds((globalStats.Currencies)c);
                    if (missing <= 0) continue;
                    if (c == np) { ticks = Math.Max(ticks, NpTicks(cost[c], missing)); continue; }
                    ticks = Math.Max(ticks, missing / Math.Max(rates[c], 1e-6));
                }
                // To the target through this box: paying for it, then the atoms its effect leaves (the GP held less its own GP price).
                double after = AtomsTicksAfter(box.globalUpgrade, Economy.EffectMult(box), _eval.Shop.Holds(globalStats.Currencies.GreenPower) - cost[gp]);
                scored.Add((label, ticks, after));
                ticks += after;
                if (ticks < best.ticks) best = (label, ticks);
            }
            Log("unattended: enablers (ticks to pay for, then the atoms' GP after): " + string.Join("; ", scored.OrderBy(x => x.afford + x.after).Take(6)
                .Select(x => $"{x.label} {x.afford:G3} + {x.after:G3}")) + $"; the atoms now: {gpTicks:G3}");
            return best.label == null ? (null, why + "; no box raising NP's equation can be paid for") : (best.label, why);
        }


        internal string ReportFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "fulltas", _name + ".report.json");
        /// <summary>This stage's report entry: target, plan and built ticks, the failed facts it added, its install and why it stopped.</summary>
        object _stageReport;

        /// <summary>A stage's beam width (0: UnattendedWidth), whether every goal was reached before it, and whether it installed a route.</summary>
        int _stageWidth;
        bool _allReached, _installed;

        /// <summary>
        /// --install (fulltas-plan --realize): a realized route that reaches further than the best becomes the run's, as the studio
        /// takes a record of other goals: its goals the run definition's (the source tree's runs\&lt;run&gt;.json, which the studio
        /// reloads), the trace header's (each goal's entry by label, else as <see cref="NewGoal"/> resolves it: no capture, whose
        /// replay would change the live scene), and the route the run file (fulltas\&lt;run&gt;.json).
        /// </summary>
        void Install(Route route, string[] goals)
        {
            WriteGoals(goals);
            WriteRunFile(route);
            _installed = true;
            Log($"install: {goals.Length} goals -> runs\\{_name}.json and {Path.GetFileName(_trace)}'s header; the route -> fulltas\\{Path.GetFileName(_runFile)}");
        }

        /// <summary>The run's goals written to its definition (runs\&lt;run&gt;.json) and the trace header.</summary>
        void WriteGoals(string[] goals)
        {
            string root = TechGrafts.SourceRoot() ?? throw new InvalidOperationException("--install: no source tree (runs\\) found from this engine");
            string def = Path.Combine(root, "runs", _name + ".json");
            if (!File.Exists(def)) throw new InvalidOperationException($"--install: no run definition {def}");
            var indented = new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var compact = new JsonSerializerOptions { Encoder = indented.Encoder };
            var byLabel = new Dictionary<string, JsonNode>();
            foreach (JsonElement g in Solve.ReadHeader(_trace).GetProperty("fulltas").GetProperty("goals").EnumerateArray())
                byLabel.TryAdd(g.GetProperty("label").GetString(), JsonNode.Parse(g.GetRawText()));
            var entries = new JsonArray(goals.Select(l => byLabel.TryGetValue(l, out JsonNode e) ? e.DeepClone()
                : NewGoal(l)?.header ?? throw new InvalidOperationException($"--install: no header entry for goal {l}; capture the trace again")).ToArray());
            SpliceHeaderGoals(_trace, entries.ToJsonString(compact));
            _labels = goals;
            _goals = goals.Length;
            JsonObject run = JsonNode.Parse(File.ReadAllText(def)).AsObject();
            run["goals"] = new JsonArray(goals.Select(g => (JsonNode)g).ToArray());
            Solve.WriteShared(def, run.ToJsonString(indented) + "\n");
        }

        /// <summary>The trace's header goals ("fulltas": {"goals": [...]}, in its first 16 MB) replaced by <paramref name="goals"/>, the
        /// rest of the file (its ticks, over a GB for a long run) copied as it is.</summary>
        internal static void SpliceHeaderGoals(string trace, string goals)
        {
            byte[] head;
            using (FileStream f = File.OpenRead(trace))
            {
                head = new byte[Math.Min(16 << 20, f.Length)];
                f.ReadExactly(head);
            }
            int run = head.AsSpan().IndexOf("\"fulltas\":{"u8), found = run < 0 ? -1 : head.AsSpan(run).IndexOf("\"goals\":["u8);
            if (found < 0) throw new InvalidOperationException(trace + ": no \"fulltas\" goals in its header");
            int open = run + found + "\"goals\":".Length, end = open, depth = 0;
            for (bool text = false; end < head.Length; end++)
            {
                byte c = head[end];
                if (text) { if (c == '\\') end++; else if (c == '"') text = false; }
                else if (c == '"') text = true;
                else if (c == '[') depth++;
                else if (c == ']' && --depth == 0) break;
            }
            if (end >= head.Length) throw new InvalidOperationException(trace + ": the header's goals don't end in its first 16 MB");
            string temp = trace + "." + Environment.ProcessId + ".tmp";
            using (FileStream o = File.Create(temp))
            using (FileStream f = File.OpenRead(trace))
            {
                o.Write(head, 0, open);
                o.Write(System.Text.Encoding.UTF8.GetBytes(goals));
                f.Position = end + 1;
                f.CopyTo(o);
            }
            Solve.Replace(temp, trace);
        }

        /// <summary>
        /// One unattended stage (Unattended): the economy planner from the best's furthest goal to its next buy (README-free settings:
        /// the Area 2 offer for an Area 2 target, beam UnattendedWidth to BeamStates), realized and installed when it gets further. With
        /// --try STEPS (fulltas-plan): realize's steps from goal --from toward --target instead, a diagnostic that writes nothing.
        /// Returns NotEconomy when the next goal is no economy buy.
        /// </summary>
        int PlanEconomy(string[] args)
        {
            Open();
            string trySteps = Solve.Option<string>(args, "--try", null);
            bool stage = trySteps == null;
            _rollback = false;
            if (stage && _best.Progress == _goals)
            {
                Log($"unattended: the best reaches all {_goals} goals");
                _allReached = true;
                _stageReport = new { stop = $"the best reaches all {_goals} goals" };
                return 0;
            }
            int kept = stage ? _best.Progress : GoalNumber(Solve.Option(args, "--from", ""), 1);
            if (kept < 1 || kept > _goals || kept > _best.Progress) throw new ArgumentException($"--from wants a goal the best reaches (1..{_best.Progress})");
            int nextBuy = Array.FindIndex(_labels, kept, l => l.StartsWith("buy:", StringComparison.Ordinal));
            if (stage && nextBuy < 0)
            {
                _stageReport = new { from = kept, stop = $"no buy goal after goal {kept}: the reach solve's" };
                return NotEconomy;
            }
            string[] targets = (stage ? _labels[nextBuy] : Solve.Option(args, "--target", "buy:buyds")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // The seeds the studio's last solve of the run listed (fulltas-<run>-seeds.txt), unless --seed gave some.
            string seedList = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), $"fulltas-{_name}-seeds.txt");
            if (_seedFiles.Count == 0 && File.Exists(seedList)) _seedFiles.AddRange(File.ReadAllLines(seedList).Select(l => l.Trim()).Where(l => l.Length > 0));
            string[] bonuses = Array.Empty<string>();
            string movesFile = MovesFile;
            // The trace's goals, not the tracker's (realize tracks the goals it played), each label resolved once: NewGoal restores
            // the start to find a reward box, which mid-search would leave the economy at the start.
            FullTasGoal[] traceGoals = _runGoals.Goals;
            var resolved = new Dictionary<string, FullTasGoal>();
            FullTasGoal GoalOf(string label) => resolved.TryGetValue(label, out FullTasGoal g) ? g
                : resolved[label] = traceGoals.FirstOrDefault(x => x.label == label) ?? NewGoal(label)?.goal;
            foreach (string b in targets)
                if (GoalOf(b) == null) throw new ArgumentException($"{b}: no such goal (a box token of FullTasGoal.Boxes, or a bonus of courses.json)");
            // An Area 2 target offers Area 2's boxes and laps (EconomyPlan.AreaTwoOffer); an Area 1 one every economy box, as before.
            HashSet<string> only = GoalOf(targets[^1])?.boxes is [string targetBox] && targetBox.StartsWith("zone 2/", StringComparison.Ordinal) ? EconomyPlan.AreaTwoOffer : null;

            MoveTable moves = MoveTable.Load(this, File.Exists(BuyTableFile) ? BuyTableFile : null, RoutesFile, _best, movesFile);
            // A trap at the best's end (no way on from its place, a quick restart landing back at it): the stage plans from the goal
            // before it, without it, and its box is a trap in this world (Zip%'s orb unlock bought first: the pit's way out is a
            // sequence-1 orb, usable only with a refresh orb level too).
            if (stage && kept > 1 && Trapped(kept, moves) is string trapWhy)
            {
                string trap = _labels[kept - 1];
                // Keyed on the world before the trap's buy: where the reach enabler chooses again.
                Read(GoalState(kept - 1).at);
                moves.AtTreeLevel(MoveTable.TreeGroup(EconomyPlan.TreeLevel(EconomyPlan.TreeBoxes(_eval))));
                moves.AtOrbs(EconomyPlan.OrbLevel(_eval));
                moves.Failed(MoveTable.TrapFrom, trap, PlayerCaps.Of(_eval).Id, trapWhy);
                moves.Save(movesFile);
                var goals = _runGoals.Goals.ToList();
                goals.RemoveAt(kept - 1);
                _runGoals = _runGoals.For(goals.ToArray());
                _labels = goals.Select(g => g.label).ToArray();
                _goals = _labels.Length;
                kept--;
                nextBuy = Array.FindIndex(_labels, kept, l => l.StartsWith("buy:", StringComparison.Ordinal));
                targets = new[] { _labels[nextBuy] };
                _rollback = true;
                Log($"unattended: goal {kept + 1} {trap} is a trap ({trapWhy}); planning from goal {kept} without it");
            }
            SimSnapshot at = GoalState(kept).at;
            if (stage && Affordable(at, GoalOf(targets[^1])?.boxes ?? Array.Empty<string>()) == true)
            {
                _stageReport = new { target = targets[^1], from = kept, stop = $"{targets[^1]} is paid for at goal {kept}: the reach solve's" };
                return NotEconomy;
            }
            if (stage && only != null && Enabler(kept, at, targets[^1], GoalOf, only, moves) is var (enabler, why) && enabler != null)
            {
                // In memory only: the run's definition takes the enabler with the install that buys it, so a stage that doesn't
                // leaves nothing behind and the next stage chooses afresh.
                Log($"unattended: {targets[^1]} is out of the atoms' reach now ({why}); first {enabler}");
                var goals = _runGoals.Goals.ToList();
                goals.Insert(nextBuy, GoalOf(enabler));
                _runGoals = _runGoals.For(goals.ToArray());
                _labels = goals.Select(g => g.label).ToArray();
                _goals = _labels.Length;
                targets = new[] { enabler };
                _eval.Restore(at);
            }
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
            Log($"plan from goal {kept} {place} (tick {_best.GoalPhysics[kept - 1]}) to {string.Join(", then ", targets)} (the best's: tick {bestTick}); {moves.Count} moves");
            _eval.Restore(at);
            globalStats start = Singleton<globalStats>.Instance;
            Log("plan start holds " + string.Join(", ", Enum.GetValues<globalStats.Currencies>().Select(cur => $"{cur} {_eval.Shop.Holds(cur):G4}"))
                + (start == null ? "" : $"; NP {start.NPrewardPerTick:G4} a tick, cap {start.NPrewardCap:G4}"));
            if (!stage) return TrySteps(kept, targetGoal - 1, trySteps, GoalOf, moves);
            var settings = new PlanSettings(targets, bonuses, "beam", _stageWidth > 0 ? _stageWidth : EconomyPlan.UnattendedWidth, EconomyPlan.BeamStates,
                double.PositiveInfinity, false, only, null);
            var failuresBefore = moves.Failures.ToHashSet();
            EconomyPlanResult free = PlanFrom(at, place, null, moves, settings, GoalOf);
            EconomyPlan.Print("free search", free);
            return RealizeRows(new List<(string name, EconomyPlanResult plan)> { ("free search", free) }, kept, targetGoal - 1, place, moves, movesFile, settings, GoalOf, failuresBefore);
        }

        /// <summary>
        /// fulltas-plan --realize: each plan printed above played with the loop builder from the best's real state at its start
        /// (<see cref="Realize"/>; a --compare order's steps kept first through every re-plan), the best's way on after the target
        /// joined (<see cref="Join"/>); the moves it measured saved (fulltas/&lt;run&gt;.moves.json or --moves), a table of what
        /// each plan said and what its build did, the fastest whole route's goal list in --out (`goals`, with every realization's
        /// steps under `realized`) and its route in --route (as the solver saves a run's best, fulltas/&lt;run&gt;.json).
        /// </summary>
        int RealizeRows(List<(string name, EconomyPlanResult plan)> rows, int kept, int targetIndex, string place, MoveTable moves, string movesFile,
            PlanSettings settings, Func<string, FullTasGoal> goalOf, ISet<(string from, string to, string caps, int tree, string why)> failuresBefore)
        {
            if (targetIndex < 0) throw new ArgumentException("the target is no goal of the run after --from: realize joins the best's way on after it");
            LoadSeeds();
            _planBase = _runGoals;
            FullTasGoal[] all = _runGoals.Goals;
            // From the stage's own goals: realize's Track calls and Install replace _labels.
            string targetLabel = all[targetIndex].label;
            var (start, route) = BuildFrom(all, kept, targetIndex);
            long startTick = _best.GoalPhysics[kept - 1];
            var realized = new List<(string name, Realization z, Route run, string[] goals)>();
            var partials = new List<(string name, Route run, string[] goals)>();
            foreach (var (name, plan) in rows)
            {
                IReadOnlyList<string> prefix = name == "free search" ? null : EconomyPlan.StepIds(name);
                Log($"realize {name}: from goal {kept} {place} on tick {startTick}");
                _factsFile = movesFile;
                Realization z = Realize(plan, start, route, startTick, place, prefix, moves, (s, p, pre) => PlanFrom(s, p, pre, moves, settings, goalOf), goalOf);
                moves.Save(movesFile);
                var (run, goals, _) = z.Done ? Join(z.Planned, z.Inputs, all, kept, targetIndex) : (null, null, null);
                if (run != null && run.Progress < kept + z.Planned.Count)
                    Log($"realize {name}: the joined route replays to goal {run.Progress} of {goals.Length} ({(run.Progress < goals.Length ? goals[run.Progress] : "")} next): {run.EndReason}");
                realized.Add((name, z, run, goals ?? all.Take(kept).Concat(z.Planned).Select(g => g.label).ToArray()));
                if (run == null && z.Inputs != null)
                {
                    // The build so far (or one past the target that the best's way on doesn't join), its steps' goals then the
                    // run's after them: the next stretch starts from it.
                    var list = all.Take(kept).Concat(z.Planned).Concat(all.Skip(z.Done ? targetIndex + 1 : targetIndex)).ToList();
                    Track(list);
                    partials.Add((name, Evaluate(z.Inputs), list.Select(g => g.label).ToArray()));
                }
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
            // With no whole route, the one that gets furthest (its goals so far, total -1): the next stretch is planned or solved from it.
            var written = whole.run != null ? (whole.name, whole.run, whole.goals)
                : realized.Where(r => r.run != null).Select(r => (r.name, r.run, r.goals)).Concat(partials).OrderByDescending(r => r.run.Progress).FirstOrDefault();
            bool reached = realized.FirstOrDefault().z?.Done == true;
            if (written.run != null && (written.run.Progress > _best.Progress || _rollback && reached)) Install(written.run, written.goals);
            else Log($"install: no route reaches further than the best's goal {_best.Progress}");
            Realization z0 = realized.FirstOrDefault().z;
            _stageReport = new
            {
                target = targetLabel, from = kept, fromTick = startTick, planTicks = z0?.PlanTicks, built = z0 != null && z0.Done ? z0.Tick : -1,
                installed = _installed, reaches = written.run?.Progress, stop = z0?.Why,
                failed = moves.Failures.Where(f => !failuresBefore.Contains(f)).Select(f => new { f.from, f.to, f.caps, f.tree, f.why }).ToList(),
                // A stop: every failed move the plan couldn't use (spec section 4), not only this stage's.
                known = z0 != null && z0.Done ? null : moves.Failures.Select(f => new { f.from, f.to, f.caps, f.tree, f.why }).ToList(),
            };
            return realized.All(r => r.z.Done) ? 0 : 3;
        }

        /// <summary>fulltas-plan --try: realize's steps (PlayStep: `lap:N`, `record:N`, `buy:box`, `all:box`, `atoms:N`; `@T` its planned
        /// ticks, 300 by default; a buy or atoms step stands for its price as Area 2's do) one after another from the best's state at
        /// goal <paramref name="kept"/>, each line its ticks, Cash, GP and NP after it and the wall time; 3 when one doesn't play.</summary>
        int TrySteps(int kept, int targetIndex, string steps, Func<string, FullTasGoal> goalOf, MoveTable moves)
        {
            if (targetIndex < 0) throw new ArgumentException("--try: --target must be a goal of the run after --from");
            _unattended = true;
            LoadSeeds();
            _planBase = _runGoals;
            var (at, route) = BuildFrom(_runGoals.Goals, kept, targetIndex);
            var z = new Realization { Inputs = route, State = at, Tick = _best.GoalPhysics[kept - 1] };
            foreach (string text in steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int atSign = text.IndexOf('@');
                string arg = (atSign < 0 ? text : text.Substring(0, atSign)).TrimEnd('!');
                int ticks = atSign < 0 ? 300 : int.Parse(text.Substring(atSign + 1).TrimEnd('!'), CultureInfo.InvariantCulture);
                string kind = arg.Contains(':') ? arg.Substring(0, arg.IndexOf(':')) : arg;
                arg = arg.TrimEnd('!');
                int levels = kind == "atoms" ? int.Parse(arg.Substring(6), CultureInfo.InvariantCulture) : 1;
                var clock = Stopwatch.StartNew();
                long from = z.Tick;
                // idle:PLACE: the plan's economy (SimEconomy.Idle: the player put at the place) from here, for comparing with the build's;
                // the build's state isn't moved.
                // where:PLACE: the movement table's place against its box's touch spot now (the plan's buys touch at the place).
                if (kind == "where")
                {
                    string place = arg.Substring(6);
                    Read(z.State);
                    string have = moves.Places.TryGetValue(place, out Place pl) ? $"{pl.X:0.0},{pl.Y:0.0}" : "none";
                    string spot = goalOf(place)?.boxes is [string id] && _eval.Shop.Box(id) is upgradeBox box && _eval.TouchSpot(box) is Vector2 t
                        ? $"{_eval.ToWorld(new ScenePos(t.x, t.y)).x:0.0},{_eval.ToWorld(new ScenePos(t.x, t.y)).y:0.0} (active {box.gameObject.activeInHierarchy}, used {box.TimesUsed}/{box.Cap})" : "none";
                    string fromPlace = _labels[kept - 1];
                    Move est = moves.Best(fromPlace, place, PlayerCaps.Of(_eval).Id);
                    string walked = moves.Places.TryGetValue(fromPlace, out Place fp) && moves.Places.TryGetValue(place, out Place tp) ? moves.Walked(fp, tp).ToString("0") : "?";
                    Console.WriteLine($"try {text}: the table's place {have}; the box's touch spot {spot}; the plan's move from {fromPlace}: {est?.Ticks.ToString() ?? "none"} ({est?.Source}), walk {walked} u");
                    continue;
                }
                // wait@T: the build stands still T ticks (the full simulation), to compare with the plan's idle:.
                if (kind == "wait")
                {
                    Track(_kept.Concat(z.Planned).ToList());
                    var standing = Enumerable.Repeat(new InputTick(), ticks).ToList();
                    SimSnapshot waited = AdvanceFrom(z.State.WithGoal(Fresh(_goals, _goals)), z.Inputs.Count, standing);
                    if (waited == null) { Console.WriteLine($"try {text}: the run ended standing"); return 3; }
                    (z.Inputs, z.State, z.Tick) = (z.Inputs.Concat(standing).ToList(), waited, z.Tick + ticks);
                    Read(z.State);
                    Economy model = _eval.Shop.Economy;
                    double[] rate = model?.CloneRates(0.02f) ?? new double[7];
                    Console.WriteLine($"try {text}: the build stood {ticks} ticks: Cash {_eval.Shop.Holds(globalStats.Currencies.Cash):G4}, GP {_eval.Shop.Holds(globalStats.Currencies.GreenPower):G4}"
                        + $"; NP {_eval.Shop.Holds(globalStats.Currencies.AtomicPower):G4} ({Singleton<globalStats>.Instance?.NPrewardPerTick:G4} a tick, atoms {string.Join("/", model?.AtomCounts ?? Array.Empty<int>())})"
                        + $"; green chance {model?.GlobalOf(globalStats.globalUpgradeSet.greenCloneClance):G4}%, clone rates a tick: Cash {rate[0]:G4}, GP {rate[1]:G4}; {model?.DescribeClones()}");
                    continue;
                }
                // plan / plan:STEPS (steps joined by ';'): the unattended planner from goal --from to the target (its first steps forced to
                // STEPS, as fulltas-plan --compare did), its schedule printed; a question about the beam's choices, nothing played.
                if (kind == "plan")
                {
                    string place = _labels[kept - 1];
                    string[] targets = { _labels[targetIndex] };
                    HashSet<string> only = goalOf(targets[0])?.boxes is [string tb] && tb.StartsWith("zone 2/", StringComparison.Ordinal) ? EconomyPlan.AreaTwoOffer : null;
                    var settings = new PlanSettings(targets, Array.Empty<string>(), "beam", EconomyPlan.UnattendedWidth, EconomyPlan.BeamStates, double.PositiveInfinity, false, only, null);
                    IReadOnlyList<string> prefix = arg.Length > 5 ? arg.Substring(5).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
                    EconomyPlanResult r = PlanFrom(GoalState(kept).at, place, prefix, moves, settings, goalOf);
                    EconomyPlan.Print(text, r);
                    Console.WriteLine($"try {(prefix == null ? "plan" : "plan:" + prefix.Count + " steps")}: target {(r.Done ? r.Tick.ToString() : "-")} ({r.Why}); {r.States} states, {clock.Elapsed.TotalSeconds:0} s");
                    continue;
                }
                if (kind == "idle" || kind == "finish")
                {
                    var economy = new SimEconomy(_eval, moves, label => goalOf(label)?.boxes);
                    economy.Restore(z.State);
                    double[] before = economy.Holds();
                    // finish:N@T: the plan's finish of course N on a T-tick course time (SimEconomy.Finish: Shop.CourseFinished).
                    if (kind == "finish") economy.Finish(arg.Substring(7) + "@" + ticks);
                    else economy.Idle(ticks, arg.Substring(5));
                    double[] after = economy.Holds();
                    string screens = string.Join(" ", _eval.World.All<clonesScript>().Select(c => _eval.Shop.Economy?.OnScreenOf(c) is bool on ? $"{c.transform.parent?.name}:{(on ? "on" : "off")}" : null).Where(t => t != null));
                    Console.WriteLine($"try {text}: the plan's economy: Cash {before[0]:G4} -> {after[0]:G4}, GP {before[1]:G4} -> {after[1]:G4}, "
                        + $"NP {before[2]:G4} -> {after[2]:G4}; clones {screens}");
                    continue;
                }
                _stepTicks = StepTicks;
                if (kind == "stick")
                {
                    // stick:X/Y@T: T ticks of that stick, no button, from the build: whether a stretch plays at all (a teleporter's
                    // up or down, a drop), then where the player is.
                    float[] v = arg.Substring(6).Split('/').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    var add = Enumerable.Repeat(new InputTick { x = v[0], y = v[1] }, ticks).ToList();
                    SimSnapshot next = AdvanceFrom(z.State, z.Inputs.Count, add);
                    if (next == null) { Console.WriteLine($"try {text}: the run ended"); return 3; }
                    z.Inputs = z.Inputs.Concat(add).ToList();
                    z.State = next;
                    z.Tick += ticks;
                    Read(next);
                    Vector3 p = _eval.Player.transform.position;
                    WorldPos w = _eval.ToWorld(new ScenePos(p.x, p.y));
                    globalStats stats = Singleton<globalStats>.Instance;
                    Console.WriteLine($"try {text}: at ({w.x:0.0}, {w.y:0.0}); teleporters {(globalStats.globalUpgradeDict.TryGetValue(globalStats.globalUpgradeSet.unlockTeleporters, out double tp) ? tp : 0)}, "
                        + $"visited [{string.Join(" ", stats?.visitedTeleporters ?? new List<int>())}]");
                    continue;
                }
                var played = PlayStep(new PlanStep(kind, arg, ticks), z, goalOf, out FullTasGoal[] goals, out _, stood: kind is "buy" or "atoms", levels: levels);
                _stepTicks = long.MaxValue;
                if (played == null)
                {
                    Console.WriteLine($"try {arg}: doesn't play from tick {from} ({clock.Elapsed.TotalSeconds:0.0} s): {_whyNot}");
                    return 3;
                }
                // A step marked ! is replayed 25 inputs at a time: the player, the courses whose clones are on screen, and the GP.
                if (text.Contains('!'))
                {
                    Track(_kept.Concat(z.Planned).Concat(goals).ToList());
                    SimSnapshot s = z.State.WithGoal(Fresh(_goals, _kept.Length + z.Planned.Count));
                    var added = played.Value.inputs.Skip(z.Inputs.Count).ToList();
                    var marks = new List<string>();
                    for (int k = 0; k < added.Count && s != null; k += 25)
                    {
                        s = AdvanceFrom(s, z.Inputs.Count + k, added.Skip(k).Take(25).ToList());
                        if (s == null) break;
                        Read(s);
                        string on = string.Join("+", _eval.World.All<clonesScript>().Where(c => _eval.Shop.Economy?.OnScreenOf(c) == true).Select(c => c.GetComponentInParent<courseScript>()?.courseNumber));
                        var p = _eval.PlayerWorld;
                        marks.Add($"{k + 25}:{p.x:0},{p.y:0} on {on} GP {_eval.Shop.Holds(globalStats.Currencies.GreenPower):G4}");
                    }
                    Console.WriteLine($"try {text} trail: " + string.Join(" | ", marks));
                }
                (z.Inputs, z.State, z.Tick) = played.Value;
                z.Planned.AddRange(goals);
                Read(z.State);
                Console.WriteLine($"try {arg}: tick {from} -> {z.Tick}, {z.Tick - from} ticks; inputs {SnapshotIndex.Hash(z.Inputs, z.Inputs.Count):x16}; Cash {_eval.Shop.Holds(globalStats.Currencies.Cash):G4}, "
                    + $"GP {_eval.Shop.Holds(globalStats.Currencies.GreenPower):G4}, NP {_eval.Shop.Holds(globalStats.Currencies.AtomicPower):G4}"
                    + $" ({Singleton<globalStats>.Instance.NPrewardPerTick:G4} a tick) ({clock.Elapsed.TotalSeconds:0.0} s)");
            }
            return 0;
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

        /// <summary>A step built more than this many ticks off its plan's tick is planned again from, once per goal position: a re-plan
        /// costs 10-20 minutes once the economy is large, and Zip%'s rarely moved the target (86,077 > 86,072 > 86,068 > 86,029 > 86,052
        /// for five rounds); the plan's order stays good through smaller drift, realize waiting for the economy where it must. An atom
        /// touch short of its atoms stands this much past the plan.</summary>
        const int DriftTicks = 1500;
        /// <summary>The goals pull's realize (not unattended) as before: re-plans at most PullRounds times, a drift past PullDrift ticks.</summary>
        const int PullRounds = 3, PullDrift = 10;
        int Drift => _unattended ? DriftTicks : PullDrift;
        /// <summary>The target's wait for its last Cash (the plan's stand) is at most this many ticks.</summary>
        const int TargetWait = 3000;
        /// <summary>A step the plan stood for where no lap earns (Area 2's NP waits) waits at most this many ticks.</summary>
        const int StandWait = 10000;
        /// <summary>Where realize saves a failed fact the moment it learns it (the stage's moves file; null: not saved early).</summary>
        string _factsFile;
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
        /// --off-ticks from the plan's tick for it, or doesn't play (a strict buy strays, the Cash isn't there on arrival, no way), the
        /// rest is planned again from the build's real state on the measured moves (<paramref name="replan"/>: its place, and the
        /// <paramref name="prefix"/> steps not played yet), after a failure that adds a fact to the table (MoveTable.Failed), and once per goal position after a drift; when a re-plan finds none, the plan plays on as it is, and a step
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
            // Goal positions a drift or a slow walk was planned again from: once each, so drift can't loop.
            var driftAt = new HashSet<int>();
            z.PlanTicks.Add(plan.Done ? plan.Tick : -1);
            if (!plan.Done) { z.Why = "the plan isn't done: " + plan.Why; return z; }
            if (plan.Tick > tickCap) { z.Why = $"the plan's target on tick {plan.Tick} comes after the cap, tick {tickCap}"; return z; }
            Log($"realize: round 0, the plan's target on tick {plan.Tick}");
            bool Again(string why)
            {
                if (!_unattended && z.Rounds == PullRounds) return false;
                z.Rounds++;
                var list = _kept.Concat(z.Planned).ToList();
                Track(list);
                EconomyPlanResult next = replan(z.State.WithGoal(Fresh(list.Count, list.Count)), from, pending);
                z.PlanTicks.Add(next.Done ? next.Tick : -1);
                Log($"realize: round {z.Rounds}, planned again from {from} on tick {z.Tick} ({why}): " + (next.Done ? $"the target on tick {next.Tick}" : "no plan (" + next.Why + ")"));
                z.Log.Add(new { round = z.Rounds, from, tick = z.Tick, why, plan = next.Done ? next.Tick : -1, estimated = next.Estimated });
                // No plan: the plan plays on as it is; a step that doesn't play stops it.
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
                int bound = s.Ticks + restarts.Sum(r => r.Ticks);
                // A stand's wait for its Cash is the economy's, not the walk's: no bound on its pieces.
                // A lap's bound is tighter: a library lap far slower than the course's known lap is a search's to beat (Course 6's 5.9 s
                // seed laps against its 0.94 s record, inside the walks' bound).
                _playBy = i < plan.Stands.Length && plan.Stands[i] ? long.MaxValue
                    : z.Tick + (s.Kind == "lap" || s.Kind == "record" ? Math.Max(2L * bound, bound + 100L) : Math.Max(3L * bound, bound + 500L));
                (List<InputTick> inputs, SimSnapshot state, long tick)? played;
                FullTasGoal[] goals;
                long walked;
                _stepTicks = _unattended ? StepTicks : long.MaxValue;
                try { played = PlayStep(s, z, goalOf, out goals, out walked, i < plan.Stands.Length && plan.Stands[i], i < plan.Levels.Length ? plan.Levels[i] : 1); }
                finally { _playBy = long.MaxValue; _stepTicks = long.MaxValue; }
                long planEnd = plan.Ends[i];
                int planTicks = s.Ticks + restarts.Sum(r => r.Ticks);
                string[] froms = restarts.Select(r => r.Arg).Prepend(from).ToArray();
                string[] estimated = plan.Estimated.Where(e => froms.Any(f => e.StartsWith(f + " -> " + to + " [", StringComparison.Ordinal))
                    || e.StartsWith(s.Arg + "'s course timer", StringComparison.Ordinal)).ToArray();
                if (played == null)
                {
                    string why = $"{s.Kind} {s.Arg} from {from} on tick {z.Tick} doesn't play ({_whyNot})";
                    Log("realize: " + why);
                    // A fact under every place the step started from (its restarts' landings too, where the plan stood), so no plan
                    // makes it again on this engine build; a failure that teaches nothing new ends the stage (it would loop).
                    Read(z.State);
                    moves.AtTreeLevel(MoveTable.TreeGroup(EconomyPlan.TreeLevel(EconomyPlan.TreeBoxes(_eval))));
                    moves.AtOrbs(EconomyPlan.OrbLevel(_eval));
                    bool learned = false;
                    foreach (string f in froms) learned |= moves.Failed(f, to, caps, why);
                    // Kept at once: a stage stopped or crashing before realize returns would lose it (and search it again).
                    if (learned && _factsFile != null) moves.Save(_factsFile);
                    if (_unattended && !learned) { z.Why = why + "; already known not to play, nothing new to plan on"; continue; }
                    // No way on from here and no quick restart known from it: one played to learn where it lands, so the next plan has a
                    // way out (Zip% at the orb unlock's box, far below Course 7: every walk back to Course 6 failed, a box at a time).
                    if (_unattended && !moves.Restarts(from, caps).Any())
                    {
                        var list = _kept.Concat(z.Planned).ToList();
                        Track(list);
                        foreach (var (pre, landed) in Restarts(z.State.WithGoal(Fresh(list.Count, list.Count)), z.Inputs, list.Count).Take(1))
                        {
                            var (spot, _) = Where(landed);
                            string landing = moves.AddLanding(spot.x, spot.y, caps);
                            moves.Measured(new Move(from, landing, pre.Count - 1, "measured", false), caps);
                            if (_factsFile != null) moves.Save(_factsFile);
                            Log($"realize: a quick restart from {from} lands at {landing}: the next plan may take it");
                        }
                    }
                    // The stage's own target: the next stage starts on what was learned, where an enabler (a refill orb for a box no way
                    // reaches) is weighed first; a re-plan to the target here would only try it from elsewhere, a search a time.
                    if (_unattended && s.Kind == "target") { z.Why = why + "; the next stage weighs the enablers"; continue; }
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
                // A step built far slower than planned isn't kept: its walk is measured above, so the plan made again from here
                // prices it as built and takes another way, or takes it knowingly (Zip% after buytree0, 2026-10-07: a library walk
                // of 4,884 ticks for the plan's 227 was kept and set the whole build back 93 s).
                if (walked > Math.Max(3L * planTicks, planTicks + 500L) && (!_unattended || driftAt.Add(z.Planned.Count)))
                {
                    string why = $"{s.Kind} {s.Arg} from {from} on tick {z.Tick} walked {walked} ticks for the plan's {planTicks}; not kept";
                    Log("realize: " + why);
                    z.Log.Add(new { round = z.Rounds, kind = s.Kind, step = s.Arg, from, planTicks, plan = planEnd, built = -1L, why, estimated });
                    if (Again(why)) continue;
                    if (!_unattended) { z.Why ??= why; continue; }
                }
                float after = course > 0 ? BestPath(state, course) : 0f;
                if (s.Kind == "record" && after < record) moves.Measured(new Move("start:" + course, "record:" + course, (int)Math.Round(after / _dt), "measured", false), caps);
                z.Inputs = inputs;
                z.State = state;
                z.Planned.AddRange(goals);
                double cash = Cash(state), gp = _eval.Shop.Holds(globalStats.Currencies.GreenPower);
                string planGp = i < plan.Held.Length && plan.Held[i]?.Length > 1 ? plan.Held[i][1].ToString("G4", CultureInfo.InvariantCulture) : "?";
                long off = end - planEnd;
                Log($"realize: {s.Kind} {s.Arg} from {from}: planned {planTicks} ticks to {planEnd} ({plan.Cash[i]:0} Cash, {planGp} GP), built {ticks} to {end} ({cash:0} Cash, {gp:G4} GP), {off:+0;-0}"
                    + (s.Kind == "record" ? $"; best time {record:0.00}s -> {after:0.00}s" : "") + (estimated.Length > 0 ? "; estimated: " + string.Join("; ", estimated) : ""));
                z.Log.Add(new { round = z.Rounds, kind = s.Kind, step = s.Arg, from, planTicks, plan = planEnd, planCash = plan.Cash[i], ticks, built = end, cash, off, estimated });
                if (pending.Count > 0 && pending[0] == s.Arg) pending.RemoveAt(0);
                z.Tick = end;
                from = course > 0 ? "finish:" + course : to.Split(MoveTable.SetJoin)[^1];
                restarts.Clear();
                i++;
                if (s.Kind == "target") { z.Done = true; break; }
                if (Math.Abs(off) > Drift && (!_unattended || driftAt.Add(z.Planned.Count))) Again($"built {off:+0;-0} ticks off the plan");
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
                if (s.Kind == "record" && RecordLap(z.State, z.Inputs, z.Planned, goals, n, improve: false, gateBy: z.Tick + Math.Max(3L * s.Ticks, s.Ticks + 500L)) is var (ri, rs, rt)) { _playedTicks = null; walked = rt - z.Tick; return (ri, rs, rt); }
                if (s.Kind == "record") Log($"realize: {s.Arg}: no record lap plays from here (the saved course routes, --seed); a lap instead");
                var lap = Lap(z.State, z.Inputs, z.Planned, goals);
                walked = lap == null ? 0 : lap.Value.tick - z.Tick;
                return lap;
            }
            // atoms:N touches the atom box (its buy-max buys all the GP pays for); all:box buys its box as many times as the plan's did,
            // while the next level is paid for.
            goals = EconomyPlan.PlaceOf(s).Split(MoveTable.SetJoin).Select(goalOf).ToArray();
            string[] boxes = goals.SelectMany(g => g.boxes ?? Array.Empty<string>()).ToArray();
            // An atom touch with fewer GP than the plan's buys fewer atoms (buy-max), and their NP is what the plan counted on:
            // it waits for the plan's levels' price (Zip%: a touch 1000 ticks early bought 1 atom, not the plan's tier-up).
            // A box whose NP is over NP's cap (no atom waiting or crafting to raise it) is bought by no walk, stand or search: the plan
            // is made again from here at once (Zip%'s buytree5 after an atom touch short of the plan's atoms: 9 minutes searched).
            if (boxes.Length > 0 && OverNpCap(z.State, boxes))
            {
                _whyNot = "not paid for: its NP is over NP's cap (more atoms first)";
                return null;
            }
            int need = s.Kind == "atoms" ? Math.Max(1, levels) : 1;
            bool? affordable = boxes.Length == 0 ? true : Affordable(z.State, boxes, need);
            var played = need > 1 && affordable != true ? null : Play(z.State, z.Inputs, z.Planned, goals, affordable == true);
            // A step the plan stood for (Area 2's buys: its Cash, or its box still growing) waits as the target's stand does.
            // Standing still where the step before left the player can buy that box again (a strict stray): then a piece search,
            // whose way waits for the Cash inside it, so its ticks aren't the walk's (walked -1: not measured).
            bool searched = false;
            if (played == null && stood && affordable != true)
            {
                // An atom touch short of the plan's atoms stands at most --off-ticks past the plan's step for them (Zip% after
                // buytree0: 1,225 ticks for the plan's 139), then touches with what it holds: the plan stands for them at the box,
                // this wait is where the step before left the player (its on-screen clones pay otherwise), and a plan made again
                // from here asked for the same atoms again, round after round. The next plan sees the atoms bought.
                if (need > 1)
                {
                    played = Waited(z, goals, s.Ticks, Math.Max(25, Math.Min(StandWait, s.Ticks + Drift)), need);
                    if (played == null)
                    {
                        Read(z.State);
                        double[] price = _eval.Shop.Need(boxes, need, shown: false);
                        int gp = (int)globalStats.Currencies.GreenPower;
                        Log($"realize: {goals[^1].label}: the plan's {need} atoms ({(price == null ? "?" : price[gp].ToString("G4", CultureInfo.InvariantCulture))} GP) not paid for by "
                            + $"{s.Ticks + Drift} ticks (GP {_eval.Shop.Holds(globalStats.Currencies.GreenPower):G4} on tick {z.Tick}); touching with what it holds");
                    }
                }
                if (played == null && Affordable(z.State, boxes) == true) played = Play(z.State, z.Inputs, z.Planned, goals, true);
                if (played == null) played = Waited(z, goals, s.Ticks, StandWait);
                if (played == null) { played = Play(z.State, z.Inputs, z.Planned, goals, true); searched = true; }
            }
            else if (played == null && affordable == false && (played = Waited(z, goals, s.Ticks)) == null) _whyNot += ", short of Cash";
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

        /// <summary>A buy short of Cash on arrival (the target's stand, or any buy the build reaches before its Cash): the fewest idle ticks where the step before left the
        /// player (5 at a time from the first standing pays for it less the walk, <paramref name="walk"/>), after which the builder's
        /// way buys it.</summary>
        (List<InputTick> inputs, SimSnapshot state, long tick)? Waited(Realization z, FullTasGoal[] goals, int walk, int most = TargetWait, int levels = 1)
        {
            var list = _kept.Concat(z.Planned).ToList();
            Track(list.Concat(goals).ToList());
            SimSnapshot here = z.State.WithGoal(Fresh(_goals, list.Count));
            string[] boxes = goals.SelectMany(g => g.boxes ?? Array.Empty<string>()).ToArray();
            List<InputTick> Idle(int n) => Enumerable.Repeat(new InputTick(), n).ToList();
            int paid = -1, stood = 0;
            // Each 25 ticks on from the last check's state, not replayed from here: a target's NP stand of thousands of ticks replayed
            // its whole wait at every check (Zip%'s buytree6 stand, 1,736 ticks planned: 13 minutes on one core).
            SimSnapshot end = here;
            for (int w = 25; w <= most && paid < 0; w += 25)
            {
                Track(list.Concat(goals).ToList());
                SimSnapshot s = AdvanceFrom(end, z.Inputs.Count + stood, Idle(25));
                if (s == null) break;
                end = s;
                stood = w;
                if (Affordable(s, boxes, levels) == true) paid = w;
            }
            if (paid < 0)
            {
                Read(end);
                _whyNot = $"{goals[^1].label}: short of Cash after {stood} ticks standing (then Cash {_eval.Shop.Holds(globalStats.Currencies.Cash):G4}, "
                    + $"GP {_eval.Shop.Holds(globalStats.Currencies.GreenPower):G4}, NP {_eval.Shop.Holds(globalStats.Currencies.AtomicPower):G4})";
                Log("realize: " + _whyNot);
                return null;
            }
            // ponytail: several levels wait out the walk too (an earlier start can arrive short of one); a check of what the touch
            // bought would save the walk's ticks.
            // From the wait that pays (a search allowed there) down, 5 ticks at a time, while the walk still arrives paid: the shortest
            // wait is the last that buys. Up from the wait less the walk, a target's NP stand (its walk the plan's whole stand, 1,736
            // ticks) played some 340 library walks that arrive short before one bought.
            (List<InputTick> inputs, SimSnapshot state, long tick)? bought = null;
            for (int w = paid; w >= (levels > 1 ? paid : Math.Max(0, paid - walk)); w -= 5)
            {
                Track(list.Concat(goals).ToList());
                SimSnapshot s = w == 0 ? here : AdvanceFrom(here, z.Inputs.Count, Idle(w));
                var played = s == null ? null : Play(s, z.Inputs.Concat(Idle(w)).ToList(), z.Planned, goals, w == paid);
                if (played == null) break;
                bought = played;
                _waited = w;
            }
            if (bought != null) { Log($"realize: {goals[^1].label} waited {_waited} ticks for its Cash"); return bought; }
            _whyNot = $"{goals[^1].label}: no way buys it after waiting {paid} ticks";
            return null;
        }
    }
}
