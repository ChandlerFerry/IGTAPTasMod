using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using FullTasTracker = IGTAPTasMod.FullTasTracker;
using Caps = IGTAPTasMod.Caps;
using MacroEngine = IGTAPTasMod.MacroEngine;

namespace IGTAP.EngineSim.Cli
{
    sealed partial class FullTas
    {
        internal sealed class Route
        {
            public List<InputTick> Inputs;
            public int Progress;
            public long[] GoalInput, GoalPhysics;
            /// <summary>Per goal, the frames the run had stood still in (dash freezes, pauses: RunResult.GoalHeldFrames); null for a
            /// route composed from others' goal ticks, which counts none until its replay times it.</summary>
            public long[] GoalHeld;
            /// <summary>How its replay from the start ended (Evaluate's), for the log.</summary>
            public string EndReason;
            /// <summary>The last goal's physics tick from the run's start (tick arithmetic: splits, rests, the library).</summary>
            public long End { get { return Progress == 0 ? long.MaxValue : GoalPhysics[Progress - 1]; } }
            /// <summary>The tick the run's time counts from: <see cref="TimerGoal"/>'s (a course record's start gate, a farm loop's lead
            /// finish), else 0.</summary>
            public long Origin { get { return TimerGoal > 0 && Progress >= TimerGoal ? GoalPhysics[TimerGoal - 1] : 0; } }
            /// <summary>The run's time in ticks, what better and worse compare: <see cref="End"/> less <see cref="Origin"/>.</summary>
            public long Last { get { return Progress == 0 ? long.MaxValue : End - Origin; } }
            /// <summary>Goal k's arrival on the run's own clock (its tick less the timer goal's once that is behind it).</summary>
            public long Clock(int k) { return GoalPhysics[k - 1] - (TimerGoal > 0 && k >= TimerGoal ? GoalPhysics[TimerGoal - 1] : 0); }
            /// <summary>The frames the run stood still in by goal k, on its own clock. A course record run is timed by the game
            /// timer, physics ticks from its start gate: none count there. A farm loop (<see cref="LoopTiming"/>) counts them from
            /// its lead finish.</summary>
            public long Held(int k)
            {
                if (GoalHeld == null || TimerGoal > 0 && !LoopTiming) return 0;
                return GoalHeld[k - 1] - (TimerGoal > 0 && k >= TimerGoal ? GoalHeld[TimerGoal - 1] : 0);
            }
            /// <summary>
            /// The run's time as the gate scores it, in live units (MacroEngine.LiveUnits): a Full-TAS run's ticks and the frames it
            /// stood still in (README ### Rules 31). What a whole run's better and worse compare; the tick arithmetic (splits,
            /// rests, windows, the library) stays on <see cref="Clock"/>.
            /// </summary>
            public long Live { get { return Progress == 0 ? long.MaxValue : MacroEngine.LiveUnits(Last, Held(Progress)); } }
            public double LiveSeconds { get { return Progress == 0 ? double.MaxValue : LiveSecondsAt(Progress); } }
            /// <summary>Goal k's arrival in live seconds on the run's own clock.</summary>
            public double LiveSecondsAt(int k) { return Math.Round(MacroEngine.LiveSeconds(Clock(k), Held(k)), 4); }
            /// <summary>Its nmg and major spans up to its last goal, once a classed ranking made them (<see cref="FullTas.Report"/>).</summary>
            public GlitchReport Report;
            /// <summary>Its difficulty, once a human class's whole-route ranking measured it (<see cref="FullTas.Tally"/>).</summary>
            public Difficulty.Tally? Tally;
        }

        /// <summary>
        /// A course run timed from a goal other than its start (`solve --auto`, CourseAuto.cs): that goal's number (1-based). A
        /// record run's start-gate crossing (the game timer), or a farm loop's lead finish (<see cref="LoopTiming"/>). 0: the run
        /// is timed from its start (every Full-TAS run).
        /// </summary>
        internal static int TimerGoal;

        /// <summary>A course farm loop (CourseLoop.cs): timed from <see cref="TimerGoal"/>, the lead's last finish, to the loop
        /// lap's finish in live time, held frames included.</summary>
        internal static bool LoopTiming;

        readonly string _trace;
        readonly int _threads, _window, _onlyGoal;
        readonly double _minutes, _windowMinutes, _stallMinutes;
        /// <summary>--piece-minutes: a piece search's budget (the buy table's runs set it to their --entry-minutes).</summary>
        double _pieceMinutes;
        readonly bool _submit, _fresh, _resetStudy;
        /// <summary>--lookahead N: a polish or repair window searches on N goals past its own and is scored there (arrival at the
        /// goal after next), so a way that reaches its own goal later but faster, and gains after it, is found and kept.</summary>
        readonly int _lookahead;
        /// <summary>--rng N: the windows' search randomness (worker i gets N + i), as solve's; --log-json FILE: one JSON line per
        /// window best (wall s, time, runTicks: the physics tick at the window's last goal, ticks simulated, how, process CPU s).</summary>
        readonly int _rng;
        readonly string _logJson;
        /// <summary>--reach-polish S: with --unsolved, each goal the reach gets to is built alone and the window of --window goals
        /// ending there polished for up to S seconds before the next goal is built, so later goals stand on the fast prefix (0: off).</summary>
        readonly double _reachPolish;
        bool _unsolved;
        /// <summary>The search ends once the best reaches every goal (a farm loop's lap repair, CourseLoop.cs).</summary>
        bool _reachOnly;
        string _name, _runFile, _closestFile, _outDir;
        string[] _labels;
        int _goals;
        float _dt;
        Func<Simulation> _create;
        Simulation _eval;
        SimSnapshot _origin;
        Route _best;
        Stopwatch _clock;
        Latest<Submission> _pending;
        readonly Dictionary<(int, string), List<List<InputTick>>> _memo = new Dictionary<(int, string), List<List<InputTick>>>();
        OfflineSolver.ForcedPath _path;

        public const float VisitCell = 16f;
        // Bumped when older visits can't be trusted. 2: solver threads used to share the game's statics (the last world
        // loaded won), so a worker's respawn after a rebase took another world's FloatingOrigin and drew far-off cells.
        const int VisitsVersion = 2;
        Dictionary<long, float> _visits;
        string _visitsFile;
        string _archiveDir;
        FullTasStates _states;

        sealed class Seed
        {
            public string Name;
            public string[] Goals;
            public bool Standing, Restart;
            /// <summary>The unlocks its saved route was solved with (its "capabilities"); usable in a window whose caps
            /// cover them.</summary>
            public Caps Caps;
            public List<InputTick> Inputs;
            /// <summary>Another run's start, where its player stands still before these inputs (null: none).</summary>
            public SeedStart At;
        }
        /// <summary>A Full-TAS run's start: the player's world position and facing (it stands still there).</summary>
        sealed class SeedStart
        {
            public float X, Y;
            public bool FacingRight;
        }
        const int MaxSeedStarts = 64, MaxSettle = 12;
        // Walking to a run seed's start: how far (world units), how many held ticks at most, how far off the stand may end, how
        // many walks to keep, and how long to stand there (a run's start is 10 ticks at rest; Still stops after 3).
        const float MaxWalk = 1500f, MaxWalkMiss = 8f, MaxWalkRise = 4f;
        const int MaxWalkTicks = 240, MaxWalks = 3, StandTicks = 7;
        readonly List<string> _seedFiles = new List<string>();
        /// <summary>--harvest: saved routes whose steps are filed as library pieces at the start (FullTasPlan.cs, HarvestRoute).</summary>
        readonly List<string> _harvestFiles = new List<string>();
        readonly List<Seed> _seeds = new List<Seed>();
        (int, long) _seededFrontier = (-1, -1);

        FullTas(string[] args)
        {
            _trace = args[0];
            _threads = Solve.Option(args, "--threads", Environment.ProcessorCount);
            _minutes = Solve.Option(args, "--minutes", 60.0);
            _windowMinutes = Solve.Option(args, "--window-minutes", 1.0);
            _stallMinutes = Solve.Option(args, "--stall-minutes", double.PositiveInfinity);
            _pieceMinutes = Solve.Option(args, "--piece-minutes", 3.0);
            _window = Math.Max(1, Solve.Option(args, "--window", 2));
            _submit = !args.Contains("--no-submit");
            _fresh = args.Contains("--fresh");
            _unsolved = args.Contains("--unsolved");
            _onlyGoal = Solve.Option(args, "--goal", 0);
            _resetStudy = args.Contains("--reset-study");
            _lookahead = Math.Max(0, Solve.Option(args, "--lookahead", 0));
            _rng = Solve.Option(args, "--rng", 0);
            _logJson = Solve.Option(args, "--log-json", (string)null);
            _reachPolish = Solve.Option(args, "--reach-polish", 0.0);
            if (_logJson != null) File.WriteAllText(_logJson, "");
            _seedFiles.AddRange(Solve.Args(args, "--seed"));
            // --seed-list: a file of seed paths, one per line (a run's seeds can pass Windows' 32767-character command line).
            foreach (string list in Solve.Args(args, "--seed-list"))
                _seedFiles.AddRange(File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0));
            _harvestFiles.AddRange(Solve.Args(args, "--harvest"));
            _auto = args.Contains("--auto");
            _args = args;
            _class = Glitches.Parse(Solve.Option(args, "--class", "major"));
            _tag = ParseTag(args);
        }

        public static int Run(string[] args)
        {
            int check = Array.IndexOf(args, "--check");
            // --waste-audit <route.json>: the waste audit of a route (FullTasAutoWaste.cs); searches and writes nothing.
            int audit = Array.IndexOf(args, "--waste-audit");
            if (audit > 0 && audit + 1 < args.Length) return new FullTas(args).WasteAuditCommand(args[audit + 1]);
            return check > 0 && check + 1 < args.Length ? new FullTas(args).Check(args[check + 1]) : new FullTas(args).Search();
        }

        /// <summary>
        /// The economy as a `--ticks-out` row (and `replay --ticks-out`) carries it, under the names the plugin's 1x probe rows and
        /// trace ticks use (TasPlugin.EconomyNow), for tools/economy-diff.py: each currency held, the atom collider's atoms per tier
        /// and waiting atoms, globalStats' NP per tick, its cap, the comp boost's NP factor and the clone dust multiplier, and the
        /// upgrade trees' grown segments, and how far each door box's door is from its box (the atom box's, the Area 2 door box's:
        /// upgradeBox.doorToOpen, a PlatformMover; -1 without one).
        /// </summary>
        internal static Dictionary<string, object> EconomyRow(Simulation sim)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            Func<globalStats.Currencies, double> held = c => sim.Shop.Holds(c);
            return new Dictionary<string, object>
            {
                ["cash"] = held(globalStats.Currencies.Cash), ["gp"] = held(globalStats.Currencies.GreenPower),
                ["np"] = held(globalStats.Currencies.AtomicPower), ["cd"] = held(globalStats.Currencies.CloneDust),
                ["rp"] = held(globalStats.Currencies.RedPower), ["atoms"] = sim.Shop.Economy?.AtomCounts ?? Array.Empty<int>(),
                ["waiting"] = sim.Shop.Economy?.WaitingAtoms ?? 0, ["npTick"] = stats?.NPrewardPerTick ?? 0.0, ["npCap"] = stats?.NPrewardCap ?? 0.0,
                ["npBoost"] = stats?.EffectOfNpCompBoost ?? 1.0, ["cdMult"] = sim.Shop.Economy?.CdMult ?? 1.0,
                ["gridCalls"] = sim.Shop.Economy?.GridCalls ?? 0, ["segments"] = sim.World.All<TreeController>().Sum(t => t.GrownSegments.Count),
                ["doorAtom"] = Door(sim, globalStats.globalUpgradeSet.spawnNewAtom), ["doorArea2"] = Door(sim, globalStats.globalUpgradeSet.area2DoorOpen),
            };
        }

        static readonly System.Reflection.FieldInfo DoorToOpen = typeof(upgradeBox).GetField("doorToOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        /// <summary>The distance from the global box of this kind that has a door to its door (rounded to 0.01 u); -1: none.</summary>
        static double Door(Simulation sim, globalStats.globalUpgradeSet kind)
        {
            foreach (upgradeBox b in sim.World.All<upgradeBox>())
                if (b.upgrade == localUpgrades.localUpgradeSet.GLOBAL && b.globalUpgrade == kind && DoorToOpen.GetValue(b) is PlatformMover door && (object)door != null)
                    return Math.Round(UnityEngine.Vector2.Distance(door.transform.position, b.transform.position), 2);
            return -1;
        }

        /// <summary>
        /// --check &lt;route.json&gt; [--goals FILE]: replays a route (a run file's "inputs" or an offline best's "ticks") from the trace's
        /// start under the trace's goals (or FILE's "goals") and prints each goal's input and physics tick, where the replay ended and why. Searches and writes
        /// nothing: a quick look at an old or foreign route before seeding a solve with it.
        /// </summary>
        int Check(string file)
        {
            JsonElement header = Solve.ReadHeader(_trace);
            if (!header.TryGetProperty("fulltas", out JsonElement fulltas)) throw new InvalidOperationException("Not a Full-TAS trace (no \"fulltas\" header): " + _trace);
            _name = fulltas.GetProperty("name").GetString();
            _labels = fulltas.GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray();
            _goals = _labels.Length;
            _eval = Solve.Creator(_trace, header)();
            _eval.Engine.HumanClass = Human;
            // --loose-buys: a stray purchase doesn't end the replay (to line it up with a trace capture, which plays on).
            _eval.StrictBuys = !_args.Contains("--loose-buys");
            var bought = new List<string>();
            _eval.Purchased = path => bought.Add($"{path} (tick {_eval.Engine.PhysicsTicks}, input {_eval.Engine.RunTicks})");
            _origin = _eval.Start;
            _dt = _eval.World.FixedDeltaTime;
            // --goals FILE: under the goal list a JSON file's "goals" holds (fulltas-plan --out, a route file), not the trace's.
            string goalsFile = Solve.Option<string>(_args, "--goals", null);
            if (goalsFile != null)
            {
                string[] labels = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(goalsFile))["goals"].AsArray().Select(g => g.GetValue<string>()).ToArray();
                FullTasTracker trace = _eval.Goals;
                _goals = labels.Length;
                _origin = _origin.WithGoal(Fresh(_goals, 0));
                FullTasGoal[] list = labels.Select(l => trace.Goals.FirstOrDefault(g => g.label == l) ?? NewGoal(l)?.goal
                    ?? throw new ArgumentException($"--goals {goalsFile}: no goal {l}")).ToArray();
                _eval.UseGoals(trace.For(list));
                _labels = labels;
            }
            List<InputTick> inputs = global::IGTAP.EngineSim.Cli.Plan.ReadRoute(file);
            Route r = Evaluate(inputs);
            long last = 0;
            for (int g = 0; g < _goals; g++)
            {
                long t = g < r.Progress ? r.GoalPhysics[g] : -1;
                Console.WriteLine($"goal {g + 1,2} {_labels[g],-42} " + (t < 0 ? "-" : $"{t,5} ({r.LiveSecondsAt(g + 1).ToString("0.00", CultureInfo.InvariantCulture),6} s live, {r.Held(g + 1),3} held, +{t - last,4}) input {r.GoalInput[g]}"));
                if (t >= 0) last = t;
            }
            WorldPos at = _eval.PlayerWorld;
            Console.WriteLine($"{Path.GetFileName(file)}: {inputs.Count} inputs, {r.Progress} of {_goals} goals"
                + (r.Progress == _goals ? $", {r.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s live ({r.Last} ticks, {r.Held(r.Progress)} held frames)" : "")
                + $"; ended after input {_eval.Engine.RunTicks} at {at.x.ToString("0.0", CultureInfo.InvariantCulture)},{at.y.ToString("0.0", CultureInfo.InvariantCulture)}: {r.EndReason}");
            Console.WriteLine($"purchases ({(_eval.Goals.Loose ? "loose" : "strict")} buys): " + (bought.Count == 0 ? "none" : string.Join("; ", bought)));
            // --ticks-out FILE: every physics tick of the replay (physics and input count, world position, velocity, momentum, held frames,
            // ground, air dashes and jumps left, Cash) as JSON
            // lines, to line up with a 1x probe (tools/probe.py, rows by `physics`) where the two split.
            int ticksOut = Array.IndexOf(_args, "--ticks-out");
            if (ticksOut > 0 && ticksOut + 1 < _args.Length)
            {
                using var w = new StreamWriter(_args[ticksOut + 1]);
                _eval.SetStart(_origin, 0);
                _eval.Goals.Limit = _goals;
                if (_eval.Shop.Economy != null)
                    _eval.Shop.Economy.Spawned = (course, id, onScreen, divisor, i, j, pay) =>
                    {
                        UnityEngine.Random.State s = UnityEngine.Random.state;
                        w.WriteLine(JsonSerializer.Serialize(new { kind = "clone", physics = _eval.Engine.PhysicsTicks, input = _eval.Engine.RunTicks, course, id, onScreen, divisor, i, j, pay,
                            random = new[] { s.s0, s.s1, s.s2, s.s3 } }));
                    };
                UnityEngine.Random.Drawn = caller => w.WriteLine(JsonSerializer.Serialize(new { kind = "rng", physics = _eval.Engine.PhysicsTicks, input = _eval.Engine.RunTicks, caller }));
                _eval.Run(inputs, null, null, 1000000, _ =>
                {
                    WorldPos p = _eval.PlayerWorld;
                    UnityEngine.Random.State rng = UnityEngine.Random.state;
                    w.WriteLine(JsonSerializer.Serialize(new { physics = _eval.Engine.PhysicsTicks, input = _eval.Engine.RunTicks, goal = _eval.Progress, x = p.x, y = p.y,
                        vx = _eval.Player.Velocity.x, vy = _eval.Player.Velocity.y, mx = _eval.Player.momentum.x, my = _eval.Player.momentum.y,
                        held = _eval.Engine.HeldFrames, ground = _eval.Player.onGround, dashes = _eval.Player.airDashesLeft, jumps = _eval.Player.airJumpsLeft, omni = _eval.Player.omniDashUnlocked, maxJumps = _eval.Player.maxAirJumps,
                        cash = _eval.Shop.CashNow, npBoost = Singleton<globalStats>.Instance?.EffectOfNpCompBoost ?? 1.0,
                        rewards = _eval.Courses.Select(c => new[] { c.courseNumber, c.reward, c.rewardTier, c.gameObject.activeInHierarchy ? 1 : 0, c.compBoostTimeLeft }),
                        clones = _eval.Shop.Economy?.CloneStates(), econ = EconomyRow(_eval),
                        random = new[] { rng.s0, rng.s1, rng.s2, rng.s3 } }));
                });
                Console.WriteLine($"ticks -> {_args[ticksOut + 1]}");
            }
            // --retune GOALS --out FILE: the route's waits re-timed to their Cash from the start (Retune, FullTasAutoIdleRng.cs) against
            // GOALS, a JSON array of each goal's input where the route last played through (another engine build's --check), so a route
            // an economy change broke plays again when only its waits are off; the result, when it reaches every goal, to FILE.
            int retune = Array.IndexOf(_args, "--retune");
            if (retune > 0 && retune + 1 < _args.Length)
            {
                long[] touches = JsonSerializer.Deserialize<long[]>(File.ReadAllText(_args[retune + 1]));
                var b = new Route { Inputs = inputs, Progress = _goals, GoalInput = touches, GoalPhysics = new long[_goals] };
                Route t = Retune(_eval, _origin, b, inputs, 0, out string lost);
                Console.WriteLine($"retuned: {(t == null ? lost : $"{t.Progress} of {_goals} goals" + (t.Progress == _goals ? $", {Describe(t)}" : $" ({t.EndReason})"))}");
                string outFile = Solve.Option(_args, "--out", (string)null);
                if (t != null && t.Progress == _goals && outFile != null)
                    File.WriteAllText(outFile, JsonSerializer.Serialize(new { fulltas = _name, time = t.LiveSeconds, runTicks = t.Last, goals = _labels, ticks = t.Inputs.Select(x => x.ToJson()) }));
            }
            // --chain-vs OLD: the chaining check (FullTasAutoLib.cs) of this route against an older one.
            int vs = Array.IndexOf(_args, "--chain-vs");
            if (vs > 0 && vs + 1 < _args.Length)
            {
                List<InputTick> old = global::IGTAP.EngineSim.Cli.Plan.ReadRoute(_args[vs + 1]);
                var (a, am) = Chaining(old);
                var (b, bm) = Chaining(inputs);
                Console.WriteLine($"chaining: {b} of {_goals} goal touches at {ChainSpeed:0}+ u/s (median {bm:0}) against {a} (median {am:0}) on {Path.GetFileName(_args[vs + 1])}; {ChainingDiff(old, inputs)}");
            }
            return 0;
        }

        int Search()
        {
            string from = Open();
            Console.WriteLine($"Full-TAS {_name}: {_goals} goals, windows of {_window}, {_threads} threads; best from {from}: {Describe(_best)}");
            if (Classed)
            {
                Console.WriteLine($"Class {Glitches.Key(_class)}" + (Human ? $" (score: live time + {_weights.Grace} s a spike-grace tick"
                    + (_weights.Techs.Count > 0 ? ", " + _weights.TechsText : "")
                    + (PricesDifficulty ? $", {_weights.FramePerfect} s a frame-perfect input and {_weights.BackToBack} s a back-to-back pair of a whole route" : "") + ")" : "")
                    + $", run file fulltas/{Path.GetFileName(_runFile)}" + (Report(_best).Undetected.Count == 0 ? "" : $"; undetected nmg/major techs: {string.Join(", ", _best.Report.Undetected)}"));
                // The class's run file starts as the route the solve starts from (the trace's or the major best, when better).
                if (from != "fulltas/" + Path.GetFileName(_runFile)) WriteRunFile(_best);
            }
            Emit(new { type = "session", run = _name, goals = _labels, window = _window, threads = _threads, minutes = _minutes, windowMinutes = _windowMinutes,
                from, best = RouteJson(_best) });
            foreach (string path in _seedFiles) _seeds.Add(ReadSeed(path));
            AddHumanSeeds();
            if (_seeds.Count > 0)
                Console.WriteLine($"Seeds: {_seeds.Count} saved routes for " + string.Join(", ", _seeds.SelectMany(s => s.Goals).Distinct()
                    .Select(g => g + " (" + _seeds.Count(s => s.Goals.Contains(g)) + ")")));
            if (_submit)
            {
                _pending = new Latest<Submission>();
                new Thread(Submitter) { IsBackground = true }.Start();
            }
            new Thread(WatchStdin) { IsBackground = true }.Start();
            // --auto: the unattended gap-driven loop (FullTasAuto.cs); then stops.
            if (_auto)
            {
                Auto(_args);
                Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] Done. Best {Describe(_best)}");
                Emit(new { type = "done", best = RouteJson(_best) });
                return 0;
            }
            // --reset-study: each reset in the best against the way on without it (FullTasResets.cs); then stops.
            if (_resetStudy)
            {
                ReadFocus(_args);
                ResetStudy();
                Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] Done. Best {Describe(_best)}");
                Emit(new { type = "done", best = RouteJson(_best) });
                return 0;
            }
            // --wallreset-study: each quick restart of the best and the wall clings that carry a wall coyote count through it
            // (FullTasAutoWallReset.cs); then stops.
            if (_args.Contains("--wallreset-study"))
            {
                ReadFocus(_args);
                WallResetStudy();
                Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] Done. Best {Describe(_best)}");
                return 0;
            }
            // --rng-study: the best's waits for Cash, what draws the RNG in them, and the RNG classes a wait's hops reach
            // (FullTasAutoIdleRng.cs); then stops.
            if (_args.Contains("--rng-study"))
            {
                RngStudy();
                Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] Done. Best {Describe(_best)}");
                return 0;
            }
            (int, long) reachFrontier = (-1, -1);
            int reachRound = 0, reachBack = 0, polishedTo = 0;
            string stopped = null;
            while (!TimeUp && stopped == null)
            {
                Route best = _best;
                int reached = best.Progress;
                if (_reachOnly && reached == _goals) break;
                if (_reachPolish > 0 && _unsolved && reached > polishedTo)
                {
                    polishedTo = reached;
                    PolishFrontier();
                    continue;
                }
                if (reached < _goals && (_unsolved || reached == 0))
                {
                    if (SeedFrontier() || GraftKnownTails()) continue;
                    // A course run's finish is its lap's end, reached as a waypoint is: loop pieces are Full-TAS laps from a restart.
                    if (IsLoop(reached) && _course == null)
                    {
                        stopped = BuildFrontier();
                        continue;
                    }
                    Phase("reach");
                    (int, long) frontier = (reached, reached == 0 ? 0L : best.GoalPhysics[reached - 1]);
                    if (frontier != reachFrontier) { reachFrontier = frontier; reachRound = 0; reachBack = 0; }
                    int i = reached - Math.Min(reachBack, reached);
                    int prefixLength = i == 0 ? 0 : (int)Math.Min(best.GoalInput[i - 1], best.Inputs.Count);
                    var seeds = new List<List<InputTick>> { best.Inputs.Skip(prefixLength).ToList() };
                    seeds.AddRange(DelayedResets(seeds[0]));
                    List<List<InputTick>> found = SearchWindow("reach", best, prefixLength, reached + 1, seeds, long.MaxValue, _windowMinutes * (1 + reachRound), reachRound + 1);
                    if (found.Count > 0) Merge(i == reached ? "reached goal " + (reached + 1) : "reached goal " + (reached + 1) + " by redoing goals " + (i + 1) + "-" + reached,
                        best, prefixLength, reached + 1, found);
                    if (_best.Progress > reached) continue;
                    if (++reachBack > Math.Min(reached, MaxReachBack))
                    {
                        reachBack = 0;
                        if (++reachRound == MaxReachRounds)
                            stopped = $"no route to goal {reached + 1} ({_labels[reached]}) in {MaxReachRounds} rounds of reach windows";
                    }
                    continue;
                }
                Phase("polish");
                // --goal N: only the window of --window goals ending at goal N (from goal N - window on), over and over.
                int lastGoal = _onlyGoal > 0 ? Math.Min(_onlyGoal, _best.Progress) : 0;
                int firstWindow = _onlyGoal > 0 ? Math.Max(0, lastGoal - _window) : 0;
                int endWindow = _onlyGoal > 0 ? firstWindow + 1 : _best.Progress;
                // Loop goals are polished by rebuilding them from faster pieces; the windows search what's left.
                if (_onlyGoal == 0) PolishLoops();
                for (int i = firstWindow; i < endWindow && !TimeUp; i++)
                {
                    best = _best;
                    int limit = _onlyGoal > 0 ? lastGoal : Math.Min(i + _window + _lookahead, best.Progress);
                    if (_onlyGoal == 0 && Enumerable.Range(i, limit - i).All(IsLoop)) continue;
                    int prefixLength = i == 0 ? 0 : (int)Math.Min(best.GoalInput[i - 1], best.Inputs.Count);
                    var seeds = new List<List<InputTick>> { best.Inputs.Skip(prefixLength).ToList() };
                    List<List<InputTick>> found = SearchWindow("polish", best, prefixLength, limit, seeds, best.GoalPhysics[limit - 1], _windowMinutes, 0);
                    if (found.Count > 0) Merge("faster through goals " + (i + 1) + "-" + limit, best, prefixLength, limit, found);
                }
            }
            if (stopped != null)
            {
                Log("stopped: " + stopped);
                Emit(new { type = "note", level = "error", text = "Stopped: " + stopped + ". Searching on wouldn't change that; change the goals, the avoid boxes or the plan." });
            }
            Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] Done. Best {Describe(_best)}");
            Emit(new { type = "done", best = RouteJson(_best) });
            return 0;
        }

        /// <summary>Reads the trace, sets up the simulations, pieces and archive, and loads the best (the saved run's, if no worse); returns where it came from.</summary>
        string Open()
        {
            JsonElement header = Solve.ReadHeader(_trace);
            if (!header.TryGetProperty("fulltas", out JsonElement fulltas)) throw new InvalidOperationException("Not a Full-TAS trace (no \"fulltas\" header): " + _trace);
            _name = fulltas.GetProperty("name").GetString();
            _labels = fulltas.GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray();
            _goals = _labels.Length;
            // --tech-weight <tech>=<seconds>: this solve's own price of a tech's spans in a human class (docs/fulltas.md "Classes").
            _weights = HumanWeights.Of(fulltas).WithTechWeights(_args);
            if (_weights.Techs.Count > 0 && !Human) throw new ArgumentException("--tech-weight prices a human class's score: give --class human-major or human-nmg");
            if (PricesDifficulty) _meter = new Difficulty.Meter(_trace, header, _labels, _threads);
            _runKey = _name + Suffix;
            string dir = Path.GetDirectoryName(Path.GetFullPath(_trace));
            _runFile = Path.Combine(dir, "fulltas", _runKey + ".json");
            _closestFile = Path.Combine(dir, "fulltas", _runKey + ".closest.json");
            _outDir = Path.Combine(dir, "offline-best");
            // Every purchase is a goal: a route that buys a box other than its next goal's spends Cash and levels the goals
            // (and the plans that wrote them) don't count, so no search or check here plays one (docs/fulltas.md).
            Func<Simulation> make = Solve.Creator(_trace, header);
            _create = () => { Simulation sim = make(); sim.StrictBuys = true; sim.Engine.HumanClass = Human; return sim; };
            _clock = Stopwatch.StartNew();
            _eval = _create();
            _origin = _eval.Start;
            _dt = _eval.World.FixedDeltaTime;
            _runGoals = _eval.Goals;
            ReloadAvoids();
            _visitsFile = VisitsFile(dir, _runKey);
            _archiveDir = Path.Combine(dir, "archive", "fulltas-" + _runKey);
            string startKey = StartKey(header).ToString("x16");
            bool startChanged = StartChanged(dir, _runKey, startKey);
            _eval.Restore(_origin);
            _startAt = (_eval.PlayerWorld.x, _eval.PlayerWorld.y, _eval.Player.facingRight);
            if (!_fresh && File.Exists(_runFile)) RefuseOtherStart();
            if (startChanged)
            {
                Log($"Start state changed since the last solve of {_runKey}: dropping its archive and visits map");
                Directory.Delete(_archiveDir, true);
                File.Delete(_visitsFile);
            }
            Directory.CreateDirectory(_archiveDir);
            File.WriteAllText(StartFile(dir, _runKey), startKey);
            _visits = _fresh ? new Dictionary<long, float>() : ReadVisits(_visitsFile, _labels) ?? new Dictionary<long, float>();
            LoadPieces(dir);
            _states = new FullTasStates(_archiveDir, _fresh, Path.ChangeExtension(_trace, ".world.json"));
            if (_states.Index.Count > 0) Log($"goal states: {_states.Index.Count} in {Path.Combine("archive", "fulltas-" + _runKey, "states")}");

            _best = Padded(Evaluate(Solve.ReadInputs(header)));
            string from = Path.GetFileName(_trace);
            // A classed solve starts from the run's major best too when it ranks better in the class.
            string majorFile = Path.Combine(dir, "fulltas", _name + ".json");
            if (Classed && !_fresh && File.Exists(majorFile))
            {
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(majorFile));
                List<InputTick> inputs = Solve.ReadInputs(saved.RootElement);
                string[] solved = saved.RootElement.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToArray();
                long[] goalInput = saved.RootElement.GetProperty("goalInputTicks").EnumerateArray().Select(t => t.GetInt64()).ToArray();
                int keep = CurrentInputs(solved, goalInput, _labels, inputs.Count);
                if (keep < inputs.Count) inputs.RemoveRange(keep, inputs.Count - keep);
                Route major = Padded(Evaluate(inputs));
                if (Better(major, _best))
                {
                    _best = major;
                    from = "fulltas/" + Path.GetFileName(majorFile);
                }
            }
            if (!_fresh && File.Exists(_runFile))
            {
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(_runFile));
                List<InputTick> inputs = Solve.ReadInputs(saved.RootElement);
                string[] solved = saved.RootElement.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToArray();
                long[] goalInput = saved.RootElement.GetProperty("goalInputTicks").EnumerateArray().Select(t => t.GetInt64()).ToArray();
                int keep = CurrentInputs(solved, goalInput, _labels, inputs.Count), stored = inputs.Count;
                if (keep < inputs.Count) inputs.RemoveRange(keep, inputs.Count - keep);
                Route run = Padded(Evaluate(inputs));
                int recorded = Recorded(solved, goalInput, _labels, stored);
                // The engine plays the saved best differently (another build, a fidelity fix): the solve repairs it from where the
                // replay stops, after keeping the file as it was (fulltas/<run>.before-repair-<n>-of-<m>.json), so the shorter
                // replay the solve then saves over it never loses the old route.
                if (run.Progress < recorded)
                {
                    string backup = Path.Combine(Path.GetDirectoryName(_runFile), $"{_runKey}.before-repair-{recorded}-of-{_goals}.json");
                    if (!File.Exists(backup)) File.Copy(_runFile, backup);
                    Log($"fulltas/{Path.GetFileName(_runFile)} recorded {recorded} goals, but replayed from {Path.GetFileName(_trace)} it reaches {run.Progress} "
                        + $"({(run.Progress < _goals ? _labels[run.Progress] + ": " : "")}{run.EndReason}); repairing from there (the old file is kept as {Path.GetFileName(backup)})");
                    // The goals it lost are reached again first (its old inputs past the replay's end seed those windows).
                    _unsolved = true;
                }
                if (!Better(_best, run))
                {
                    _best = run;
                    from = "fulltas/" + Path.GetFileName(_runFile);
                    // A run file written without its goal ticks (a hand-merged route) reads as reaching no goal in the studio,
                    // which cuts its reset segments from those ticks: write them back from this replay.
                    if (keep == stored && !goalInput.SequenceEqual(run.GoalInput))
                    {
                        WriteRunFile(run);
                        Log($"{Path.GetFileName(_runFile)}: goal ticks rewritten from its replay ({run.Progress} of {_goals} goals)");
                    }
                }
            }
            HarvestRuns(dir);
            foreach (string file in _harvestFiles) HarvestRoute(file);
            return from;
        }

        (float x, float y, bool facingRight) _startAt;

        /// <summary>How far (world units) a trace's start may lie from the one its run file was solved from.</summary>
        const float StartTolerance = 0.5f;

        /// <summary>
        /// Stops the solve, leaving the run file as it is, when the trace starts where its best wasn't solved from (startAt): a new
        /// game whose player moved before the start was taken (real input while it loaded: FalseEnding2-Any%'s start stood 95.6
        /// units left of the spawn, facing left) replays the best to its first goal only.
        /// </summary>
        void RefuseOtherStart()
        {
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(_runFile));
            if (!saved.RootElement.TryGetProperty("startAt", out JsonElement at)) return;
            float x = at.GetProperty("x").GetSingle(), y = at.GetProperty("y").GetSingle();
            bool right = at.GetProperty("facingRight").GetBoolean();
            if (Math.Abs(x - _startAt.x) > StartTolerance || Math.Abs(y - _startAt.y) > StartTolerance || right != _startAt.facingRight)
                throw new InvalidOperationException($"{Path.GetFileName(_trace)} starts at ({_startAt.x:0.##}, {_startAt.y:0.##}) facing {(_startAt.facingRight ? "right" : "left")}, "
                    + $"but fulltas/{Path.GetFileName(_runFile)} was solved from ({x:0.##}, {y:0.##}) facing {(right ? "right" : "left")}; the run file is left as it is: "
                    + "capture the start again (a fresh new game, no input while it loads), or pass --fresh to solve from this start anyway");
        }

        /// <summary>
        /// The goals a run file records reaching that are still the run's (its leading goals with the same labels and a goal tick).
        /// A replay that reaches fewer means this engine or start plays the best differently; solving on from it would save a
        /// partial run over the best (a build without the buy-max fix replayed FalseEnding2-Any%'s 409.34 s best to goal 282 of 300
        /// and wrote that back).
        /// </summary>
        static int Recorded(string[] solved, long[] goalInput, string[] labels, int count)
        {
            int n = 0;
            while (n < solved.Length && n < labels.Length && n < goalInput.Length && solved[n] == labels[n] && goalInput[n] >= 0 && goalInput[n] <= count + 2) n++;
            return n;
        }

        const int MaxReachBack = 3, MaxReachRounds = 3;
        string _phase;
        int _windowId;

        void Phase(string phase)
        {
            if (_phase == phase) return;
            _phase = phase;
            Log(phase == "reach" ? "phase: find a complete route first"
                : phase == "build" ? "phase: build the loop goals (buys, laps) from pieces"
                : "phase: polish the goals the route reaches, from goal 1");
            Emit(new { type = "phase", phase });
        }

        static readonly JsonSerializerOptions EventJson = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        static void Emit(object e) { Console.WriteLine("@fulltas " + JsonSerializer.Serialize(e, EventJson)); }

        double Seconds(long ticks) { return Math.Round(ticks * _dt, 4); }

        object RouteJson(Route r)
        {
            return new { progress = r.Progress, time = r.Progress == 0 ? (double?)null : r.LiveSeconds, ticks = r.Progress == 0 ? (long?)null : r.Last,
                splits = Enumerable.Range(1, r.Progress).Select(k => r.LiveSecondsAt(k)).ToArray() };
        }

        bool TimeUp { get { return _stopRequested || _clock.Elapsed.TotalMinutes >= _minutes; } }

        // The process's, not a run's: a farm loop's lap repair runs a FullTas before the loop's own, and one watcher reads stdin for both.
        static volatile bool _stopRequested;
        static int _watchingStdin;

        /// <summary>
        /// A "stop" line on stdin (the studio's Stop) ends the solve as its time limit would: the window ends, its archive and
        /// visits are written, and the process exits.
        /// </summary>
        void WatchStdin()
        {
            if (Interlocked.Exchange(ref _watchingStdin, 1) != 0) return;
            for (string line; (line = Console.In.ReadLine()) != null;)
                if (line.Trim() == "stop")
                {
                    Log("stop requested: ending the window and saving");
                    _stopRequested = true;
                    return;
                }
        }

        static List<List<InputTick>> DelayedResets(List<InputTick> tail)
        {
            var variants = new List<List<InputTick>>();
            var resets = Enumerable.Range(1, Math.Max(0, tail.Count - 1)).Where(k => tail[k].reset).Take(4).ToList();
            foreach (int at in resets)
                foreach (int wait in new[] { 1, 2, 3, 5, 8, 12, 20 })
                {
                    var v = new List<InputTick>(tail);
                    InputTick hold = v[at - 1];
                    hold.press = hold.release = hold.dash = hold.reset = false;
                    hold.dashJump = 0;
                    v.InsertRange(at, Enumerable.Repeat(hold, wait));
                    variants.Add(v);
                }
            return variants;
        }

        void Log(string message) { Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] {message}"); }

        /// <summary>Loads the run's avoid boxes for a window heading for goals (null: every optional block on).</summary>
        void ReloadAvoids(ICollection<string> goals = null)
        {
            OfflineSolver.ForcedPath path = Solve.FullTasPaths(_name, goals, _labels);
            string now = string.Join(", ", path.Avoid), before = _path == null ? "" : string.Join(", ", _path.Avoid);
            _path = path;
            if (now == before) return;
            Log("avoid (forced-paths.json): " + (now.Length == 0 ? "none" : now));
            Emit(new { type = "note", level = "info", text = $"Avoid boxes now {path.Avoid.Count} (forced-paths.json)." });
        }

        string Describe(Route r)
        {
            if (r.Progress == _goals) return r.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s" + ClassNote(r);
            return r.Progress + "/" + _goals + " goals" + (r.Progress > 0 ? " (" + r.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s)" : "") + ClassNote(r);
        }

        /// <summary>The ranking every acceptance uses: more goals, then <see cref="Ahead"/> (live time, or the class's rank).</summary>
        bool Better(Route a, Route b)
        {
            if (a.Progress != b.Progress) return a.Progress > b.Progress;
            return a.Progress > 0 && Ahead(a, b);
        }

        Route Evaluate(List<InputTick> inputs, int limit = -1)
        {
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = limit < 0 ? _goals : limit;
            RunResult r = _eval.Run(inputs, null, null, 1000000);
            return new Route { Inputs = inputs, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks, GoalHeld = r.GoalHeldFrames, EndReason = r.EndReason };
        }

        internal static int CurrentInputs(string[] solved, long[] goalInputTicks, string[] labels, int count)
        {
            int changed = 0;
            while (changed < solved.Length && changed < labels.Length && solved[changed] == labels[changed]) changed++;
            if (changed == solved.Length && changed == labels.Length) return count;
            int reached = 0;
            while (reached < changed && reached < goalInputTicks.Length && goalInputTicks[reached] >= 0) reached++;
            if (reached < changed) return count;
            return changed == 0 ? 0 : (int)Math.Min(goalInputTicks[changed - 1], count);
        }

        List<List<InputTick>> SearchWindow(string kind, Route route, int prefixLength, int limit, List<List<InputTick>> seeds, long baseline, double budgetMinutes, int attempt,
            double? stallMinutes = null)
        {
            List<InputTick> prefix = route.Inputs.GetRange(0, prefixLength);
            // A classed window's baseline needs the route's report: made before the replay below leaves _eval at the window's start.
            if (Classed && baseline != long.MaxValue) Report(route);
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            // The replay to the window's start also keeps every goal state along the way on disk (docs/fulltas.md).
            SimSnapshot at = _states.ReplayAndRecord(_eval, route.Inputs, prefixLength, new SnapshotStore(_eval));
            if (at == null)
            {
                Log(kind + ": the route ends before its first " + prefixLength + " inputs do; skipped");
                Emit(new { type = "note", level = "warn", text = "Skipped a " + kind + " window: the route ends before its first " + prefixLength + " inputs do." });
                return new List<List<InputTick>>();
            }
            _eval.Restore(at);
            int first = _eval.Progress;
            if (first >= limit) return new List<List<InputTick>>();
            baseline = Allowance(route, first, limit, baseline);
            var key = (_eval.MovementFingerprint(), _labels[first]);
            string caps = WindowCaps(_runGoals.Goals.Skip(first).Take(limit - first));
            var seeded = new HashSet<List<InputTick>>(seeds, SequenceComparer.Instance);
            if (_memo.TryGetValue(key, out List<List<InputTick>> known))
                foreach (List<InputTick> r in known)
                    if (seeded.Add(r)) seeds.Add(r);
            string title = kind + " goals " + (first + 1) + "-" + limit + "/" + _goals;
            string span = first == limit - 1 ? _labels[first] : _labels[first] + ".." + _labels[limit - 1];
            List<(string how, List<InputTick> inputs)> starts = SeedStarts(at, prefix, new HashSet<string>(_labels.Skip(first).Take(limit - first)),
                CanRestartAfter(prefix, first), caps);
            foreach (var s in starts) if (seeded.Add(s.inputs)) seeds.Add(s.inputs);
            if (starts.Count > 0) Log($"{title} {span}: {starts.Count} starts from saved routes");
            (List<InputTick> chain, int chained) = PieceChain(at, prefix, first, limit);
            if (chained > 0)
            {
                if (seeded.Add(chain)) seeds.Add(chain);
                Log($"{title} {span}: library pieces play goals {first + 1}-{first + chained}");
            }
            int id = ++_windowId;
            // Goals the route logs by the prefix's end: one more than first when the last one is touched in the no-input ticks
            // after its input tick (a dash), which read the next input, so the window takes it again.
            int logged = 0;
            while (logged < route.Progress && route.GoalInput[logged] <= prefixLength) logged++;
            Emit(new { type = "window", id, kind, first, limit, logged, attempt, budget = Math.Round(budgetMinutes * 60, 1),
                start = first == 0 ? 0.0 : Seconds(route.GoalPhysics[first - 1]), target = baseline == long.MaxValue ? (double?)null : Seconds(baseline),
                savedStarts = starts.Count });

            Func<Simulation> create = () =>
            {
                (Simulation sim, SimSnapshot state) = Prefixed(prefix, title);
                sim.SetStart(state, prefix.Count);
                sim.Goals.Limit = limit;
                return sim;
            };
            string archiveFile = Path.Combine(_archiveDir, (first + 1) + "-" + limit + "-" + ArchiveHash(prefix, _labels.Skip(first).Take(limit - first)).ToString("x16")
                + (_noResets ? "-noreset" : "") + ".json");
            if (_noResets) seeds.RemoveAll(s => s.Any(t => t.reset));
            var labels = new HashSet<string>(_labels.Skip(first).Take(limit - first));
            List<Seed> walkers = kind == "reach" ? _seeds.Where(s => s.At != null && s.Goals.Any(labels.Contains)).ToList() : new List<Seed>();
            var tried = new HashSet<long>();
            List<List<InputTick>> found = Explore(id, title, span, create, caps, seeds, archiveFile, baseline, budgetMinutes,
                stallMinutes ?? (kind == "reach" ? 0.0 : _stallMinutes), kind == "reach" ? prefix : null, _labels.Skip(first).Take(limit - first).ToList(), out List<InputTick> bestInputs,
                walkers.Count == 0 ? null : solver => CellWalks(solver, at, prefix.Count, limit, walkers, tried, title), first);
            if (bestInputs != null) Remember(key, bestInputs);
            return found;
        }

        const int PrefixTries = 3;

        /// <summary>
        /// A fresh simulation that has replayed prefix, and the state after it. A worker's replay has now and then ended early
        /// where the evaluator's didn't (seen once at the end of a 30,000-input Full-TAS prefix); a fresh simulation replays it
        /// again before the worker gives up, so one bad replay doesn't take the whole solve down. The run's stored goal state
        /// at the prefix's end starts the simulation without a replay at all; a miss keeps it there for the next one.
        /// </summary>
        (Simulation sim, SimSnapshot state) Prefixed(List<InputTick> prefix, string title)
        {
            for (int attempt = 1; ; attempt++)
            {
                Simulation sim = _create();
                SnapshotStore store = new SnapshotStore(sim);
                ulong hash = SnapshotIndex.Hash(prefix, prefix.Count);
                // The window's own inputs sit this far into the run: a resume from a stored goal state counts from here.
                _states.Index.Base = prefix.Count;
                _states.Index.BaseHash = hash;
                SimSnapshot state = _states.Load(store, hash, 0);
                if (state != null) return (sim, state);
                try { state = _states.ReplayAndRecord(sim, prefix, prefix.Count, store); }
                catch (NotSupportedException) { state = sim.Advance(prefix); }   // this state can't be stored; replay stays the way there
                if (state != null) return (sim, state);
                Log($"{title}: a worker's replay of the {prefix.Count}-input prefix ended early ({sim.Engine.Status}), try {attempt} of {PrefixTries}");
                if (attempt == PrefixTries) throw new InvalidOperationException(title + ": the prefix replayed differently on a worker");
            }
        }

        /// <summary>
        /// One offline search from <paramref name="create"/>'s start for its goals, for up to <paramref name="budgetMinutes"/>, ended
        /// early once it has found a route and gone <paramref name="stallMinutes"/> without a faster one. Returns the routes faster than
        /// <paramref name="baseline"/> in the order found; <paramref name="bestInputs"/> is its best (null: none reached the goals).
        /// A reach window passes its <paramref name="closestPrefix"/> for the studio's Test best. A class's window prices its finds
        /// from goal <paramref name="penaltyFrom"/>'s tick (<see cref="WindowPenalty"/>; null: the goals its start has).
        /// </summary>
        List<List<InputTick>> Explore(int id, string title, string span, Func<Simulation> create, string caps, List<List<InputTick>> seeds,
            string archiveFile, long baseline, double budgetMinutes, double stallMinutes, List<InputTick> closestPrefix, ICollection<string> goals,
            out List<InputTick> bestInputs, Func<OfflineSolver, List<List<InputTick>>> walks = null, int? penaltyFrom = null, bool resume = true)
        {
            var found = new List<List<InputTick>>();
            ReloadAvoids(goals);
            OfflineSolver.Memory memory = _fresh ? null : Solve.LoadMemory(archiveFile);
            if (memory != null)
                Log($"Resuming from {Path.GetFileName(archiveFile)}: {memory.Sources.Sum(s => s.Cells.Count)} cells over {memory.Sources.Count} prefixes, "
                    + $"{memory.Plateau.Count} plateau routes, {memory.Runs} runs before");
            // --parts grafts: the tech library in every window (FullTasAutoWaste.cs), unless a part set its own.
            var solver = new OfflineSolver(create, caps, seeds, memory, _path, VisitCell, _mined ?? AutoGrafts(caps), rng: _rng);
            if (_mined == null) TuneGrafts(solver, 0, 0, 0);
            bool ownModes = _findModes == null && solver.Mined != null;
            if (ownModes) _findModes = new HashSet<string>();
            // The run's stored goal states stand for a cell's prefix only when the window starts from the prefix as replayed
            // (resume: false when create alters that start).
            solver.Resume = resume ? _states.Index : null;
            solver.NoResets = _noResets;
            // A class's window ranks its finds by score inside the window (FullTasClasses.cs); the baseline is the best's score there.
            solver.Penalty = WindowPenalty(penaltyFrom);
            TimeSpan begin = _clock.Elapsed, last = begin;
            bool finished = false;
            long windowBest = long.MaxValue;
            // Guards found, finished, last and windowBest: the solver raises Improved from its workers (outside its own lock).
            var window = new object();
            solver.Improved += (time, runTicks, inputs, how) =>
            {
                // A Full-TAS sim is strict: its run ends StrictFinishTicks after its last goal, and the baseline is that goal's tick.
                long atGoal = runTicks - Simulation.StrictFinishTicks;
                lock (window)
                {
                    last = _clock.Elapsed;
                    finished = true;
                    if (atGoal < windowBest)
                    {
                        Log($"{title} {span} finish {FindText(solver, inputs, atGoal)} at {(_clock.Elapsed - begin).TotalSeconds:0.0}s, "
                            + $"{Interlocked.Read(ref solver.Ticks)} ticks simulated");
                        if (_logJson != null)
                            File.AppendAllText(_logJson, JsonSerializer.Serialize(new { wall = _clock.Elapsed.TotalSeconds, time = Seconds(atGoal), runTicks = atGoal,
                                ticks = Interlocked.Read(ref solver.Ticks), how, cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds }) + "\n");
                    }
                    windowBest = Math.Min(windowBest, atGoal);
                    if (atGoal < baseline)
                    {
                        found.Add(inputs);
                        _findModes?.Add(how);
                        GraftFind(how);
                        if (found.Count == 1) Emit(new { type = "found", id, time = Seconds(atGoal) });
                    }
                }
            };
            object Progress()
            {
                lock (window)
                    return new { type = "status", id, elapsed = Math.Round((_clock.Elapsed - begin).TotalSeconds, 1), candidates = found.Count,
                        best = windowBest == long.MaxValue ? (double?)null : Seconds(windowBest), runs = solver.Runs };
            }
            void Status()
            {
                Log($"{title} {span} best {Describe(_best)} ({found.Count} candidates)");
                Emit(Progress());
            }
            Status();
            Thread[] threads = solver.Start(_threads);
            TimeSpan shown = begin, emitted = begin, saved = begin;
            while (!TimeUp && (_clock.Elapsed - begin).TotalMinutes < budgetMinutes)
            {
                Thread.Sleep(250);
                lock (window) if (finished && (_clock.Elapsed - last).TotalMinutes >= stallMinutes) break;
                if ((_clock.Elapsed - shown).TotalSeconds >= 15)
                {
                    shown = _clock.Elapsed;
                    Log($"{title} {span} best {Describe(_best)} ({found.Count} candidates), {Interlocked.Read(ref solver.Runs)} runs, {Interlocked.Read(ref solver.Ticks)} ticks, {solver.ArchiveStatus()}, heap {GC.GetTotalMemory(false) >> 20} MB");
                    List<List<InputTick>> walked = walks?.Invoke(solver);
                    if (walked?.Count > 0)
                        lock (window)
                        {
                            found.AddRange(walked);
                            finished = true;
                            last = _clock.Elapsed;
                        }
                }
                if ((_clock.Elapsed - emitted).TotalSeconds >= 1)
                {
                    emitted = _clock.Elapsed;
                    Emit(Progress());
                    if (closestPrefix != null) OfferClosest(solver.Closest(), closestPrefix);
                }
                if ((_clock.Elapsed - saved).TotalSeconds >= 60)
                {
                    // The studio's explored map reads the visits file: write it as the window goes, not only at its end.
                    saved = _clock.Elapsed;
                    Solve.SaveMemory(archiveFile, solver.Export(5000));
                    SaveVisits(solver.Visits());
                }
            }
            Solve.SaveMemory(archiveFile, solver.Export(10000));
            solver.Stop();
            foreach (Thread t in threads) t.Join(60000);
            List<InputTick> best;
            lock (solver.Shared) best = solver.Shared.Inputs;
            bestInputs = best;
            if (closestPrefix != null) OfferClosest(solver.Closest(), closestPrefix);
            SaveVisits(solver.Visits());
            Status();
            GraftTally(solver);
            if (solver.Mined != null)
                Log($"{title} {span}: {solver.TechGrafted} tech grafts of {solver.Runs} runs ({solver.Mined.Patterns.Select(p => p.Tech).Distinct().Count()} techs); finds via {(_findModes == null || _findModes.Count == 0 ? "-" : string.Join(", ", _findModes))}");
            if (ownModes) _findModes = null;
            lock (window)
            {
                Emit(new { type = "windowEnd", id, candidates = found.Count, best = windowBest == long.MaxValue ? (double?)null : Seconds(windowBest) });
                return found.ToList();
            }
        }

        OfflineSolver.Attempt _closest;
        (int, float, int) _closestClaim = (-1, 0f, -1);

        // The reach windows' attempt furthest toward the goals this session (most goals reached, then nearest the next area
        // goal, then fewest inputs), from the run's start, for the studio's Test best. Written when it gets closer. The
        // solver's claim comes from a cell, which a worker may have filed while branching from a snapshot its inputs don't
        // reproduce, so the attempt is replayed from the start and judged by where that replay ends.
        void OfferClosest(OfflineSolver.Attempt a, List<InputTick> prefix)
        {
            if (a == null) return;
            var claim = (a.Progress, a.Distance, a.Inputs.Count);
            if (claim.Equals(_closestClaim)) return;
            _closestClaim = claim;
            List<InputTick> inputs = prefix.Concat(a.Inputs).ToList();
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            SimSnapshot end = _eval.Advance(inputs);
            if (end == null) return;
            _eval.Restore(end);
            var replayed = new OfflineSolver.Attempt { Progress = _eval.Progress, Distance = OfflineSolver.GoalDistance(_eval), Inputs = inputs };
            float Far(float d) => float.IsNaN(d) ? float.PositiveInfinity : d;
            if (_closest != null && (replayed.Progress < _closest.Progress || replayed.Progress == _closest.Progress
                && (Far(replayed.Distance) > Far(_closest.Distance) || Far(replayed.Distance) == Far(_closest.Distance) && inputs.Count >= _closest.Inputs.Count))) return;
            _closest = replayed;
            Directory.CreateDirectory(Path.GetDirectoryName(_closestFile));
            Solve.WriteShared(_closestFile, JsonSerializer.Serialize(new
            {
                name = _name,
                goals = _labels,
                reached = replayed.Progress,
                next = replayed.Progress < _goals ? _labels[replayed.Progress] : null,
                distance = float.IsFinite(replayed.Distance) ? Math.Round(replayed.Distance, 1) : (double?)null,
                inputs = inputs.Select(t => t.ToJson()),
            }));
        }

        /// <summary>What the search presses for these goals: the unlocks the player <see cref="_eval"/> holds now
        /// (<see cref="PlayerCaps"/>) plus what the boxes they buy give, as the caps id the solver reads.</summary>
        string WindowCaps(IEnumerable<FullTasGoal> goals, Caps? from = null)
        {
            return _eval.Shop.AfterBuys(from ?? PlayerCaps.Of(_eval), goals.SelectMany(g => g.boxes ?? Array.Empty<string>())).Id;
        }

        void Remember((int, string) key, List<InputTick> route)
        {
            if (!_memo.TryGetValue(key, out List<List<InputTick>> list)) _memo[key] = list = new List<List<InputTick>>();
            if (list.Any(r => r.SequenceEqual(route))) return;
            list.Add(route);
            if (list.Count > 4) list.RemoveAt(0);
        }

        internal static string VisitsFile(string traceDir, string run) { return Path.Combine(traceDir, "archive", "fulltas-" + run + ".visits.json"); }

        internal static string StartFile(string traceDir, string run) { return Path.Combine(traceDir, "archive", "fulltas-" + run, "start.key"); }

        /// <summary>The archive and visits map of the run were built from another start state (a recapture that changed it).</summary>
        internal static bool StartChanged(string traceDir, string run, string startKey)
        {
            string file = StartFile(traceDir, run);
            return File.Exists(file) && File.ReadAllText(file).Trim() != startKey;
        }

        /// <summary>
        /// The trace's start state as the search sees it: the player's fields, position, origin and velocity. Instance ids are left
        /// out (they change from capture to capture of the same state).
        /// </summary>
        internal static ulong StartKey(JsonElement header)
        {
            JsonElement start = header.GetProperty("start");
            ulong hash = 14695981039346656037UL;
            void Add(string text)
            {
                foreach (byte b in Encoding.UTF8.GetBytes(text)) hash = Feed(hash, b, 1);
                hash = Feed(hash, 0xff, 1);
            }
            foreach (JsonElement field in start.GetProperty("movement").EnumerateArray())
            {
                string value = field[2].GetRawText();
                if (value.Contains("\"$ref\"", StringComparison.Ordinal)) continue;
                Add(field[1].GetString());
                Add(value);
            }
            foreach (string part in new[] { "position", "origin", "velocity" })
                if (start.TryGetProperty(part, out JsonElement v)) Add(v.GetRawText());
            return hash;
        }

        static ulong ArchiveHash(List<InputTick> prefix, IEnumerable<string> labels)
        {
            ulong hash = 14695981039346656037UL;
            foreach (InputTick t in prefix)
            {
                hash = Feed(hash, t.x);
                hash = Feed(hash, t.y);
                hash = Feed(hash, t.held);
                hash = Feed(hash, t.press);
                hash = Feed(hash, t.release);
                hash = Feed(hash, t.dash);
                hash = Feed(hash, t.dashJump, 4);
                hash = Feed(hash, t.springDash, 4);
                hash = Feed(hash, t.lockX.HasValue);
                if (t.lockX.HasValue) hash = Feed(hash, t.lockX.Value);
                hash = Feed(hash, t.lockY.HasValue);
                if (t.lockY.HasValue) hash = Feed(hash, t.lockY.Value);
                if (t.turnX.HasValue) hash = Feed(hash, t.turnX.Value);
                if (t.turnY.HasValue) hash = Feed(hash, t.turnY.Value);
                hash = Feed(hash, t.reset);
                if (t.pause > 0) hash = Feed(hash, ((long)t.pauseTick << 32) | (uint)t.pause, 8);
                if (t.pause > 0 && t.pauseFrame != 0) hash = Feed(hash, t.pauseFrame, 4);
            }
            foreach (string label in labels)
            {
                hash = Feed(hash, 0xff, 1);
                foreach (byte b in Encoding.UTF8.GetBytes(label)) hash = Feed(hash, b, 1);
            }
            return hash;
        }

        static ulong Feed(ulong hash, long value, int bytes)
        {
            for (int i = 0; i < bytes; i++) hash = (hash ^ (byte)(value >> (i * 8))) * 1099511628211UL;
            return hash;
        }

        static ulong Feed(ulong hash, float value) { return Feed(hash, BitConverter.SingleToInt32Bits(value == 0f ? 0f : value), 4); }
        static ulong Feed(ulong hash, bool value) { return Feed(hash, value ? 1 : 0, 1); }

        internal static Dictionary<long, float> ReadVisits(string file, string[] labels)
        {
            if (!File.Exists(file)) return null;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = doc.RootElement;
            if (root.GetProperty("cell").GetSingle() != VisitCell
                || !root.TryGetProperty("version", out JsonElement version) || version.GetInt32() != VisitsVersion
                || (labels != null && !root.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).SequenceEqual(labels))) return null;
            var visits = new Dictionary<long, float>();
            foreach (JsonElement c in root.GetProperty("cells").EnumerateArray())
                visits[((long)c[0].GetInt32() << 32) | (uint)c[1].GetInt32()] = c[2].GetSingle();
            return visits;
        }

        void SaveVisits(Dictionary<long, float> window)
        {
            foreach (var v in window)
                if (!_visits.TryGetValue(v.Key, out float t) || v.Value < t) _visits[v.Key] = v.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(_visitsFile));
            Solve.WriteShared(_visitsFile, JsonSerializer.Serialize(new
            {
                version = VisitsVersion,
                goals = _labels,
                cell = VisitCell,
                cells = _visits.Select(v => new[] { (float)(int)(v.Key >> 32), (float)(int)v.Key, (float)Math.Round(v.Value, 2) }),
            }));
        }

        /// <summary>Whether the route may quick restart right after these inputs: not twice in a row, and not taking back an area goal.</summary>
        bool CanRestartAfter(List<InputTick> prefix, int reached)
        {
            return (prefix.Count == 0 || !prefix[prefix.Count - 1].reset) && (reached == 0 || !_labels[reached - 1].StartsWith("at:", StringComparison.Ordinal));
        }

        List<(string how, List<InputTick> inputs)> SeedStarts(SimSnapshot at, List<InputTick> prefix, HashSet<string> labels, bool restart, string windowCaps)
        {
            var starts = new List<(string how, List<InputTick> inputs)>();
            Caps caps = Caps.Parse(windowCaps);
            _eval.Restore(at);
            List<Seed> matching = _seeds.Where(s => s.Caps.IsSubsetOf(caps) && s.Goals.Any(labels.Contains)).OrderByDescending(s => Unlocks(s.Caps)).ToList();
            if (matching.Count == 0) return starts;
            _eval.SetStart(at, prefix.Count);
            _eval.Goals.Limit = _goals;
            var walks = matching.Where(s => s.At != null).ToDictionary(s => s, WalkedStarts);
            int settle = -1;
            if (restart)
            {
                int still = _eval.Still(new List<InputTick> { new InputTick { reset = true } }, 300);
                settle = still < 1 ? -1 : still - 1;
            }
            List<InputTick> Restarted(Seed s, int idle)
            {
                var inputs = new List<InputTick>(idle + 1 + s.Inputs.Count) { new InputTick { reset = true } };
                inputs.AddRange(Enumerable.Repeat(new InputTick(), idle));
                inputs.AddRange(s.Inputs);
                return inputs;
            }
            var perSeed = matching.Select(s =>
            {
                var list = new List<(string how, List<InputTick> inputs)>();
                if (walks.TryGetValue(s, out var walked)) list.AddRange(walked);
                if (restart && s.Standing && settle >= 0) list.Add((s.Name + ", after a quick restart and " + settle + " ticks to stand still", Restarted(s, settle)));
                if (restart && s.Restart && s.Inputs.Count > 0)
                {
                    List<InputTick> own = s.Inputs.ToList();
                    InputTick head = own[0];
                    head.reset = true;
                    own[0] = head;
                    list.Add((s.Name + ", with a quick restart on its first tick", own));
                    // A quick restart carries what the ticks before it left running past the respawn: a dash's end (EndDash's
                    // Velocity.y = 10), a jump released on the restart's tick (jumpCutBuffer, 15 ticks, cuts the lap's first jump),
                    // and the facing a dash at rest takes; and the lap's first dash moves by the frames its tick gets on the
                    // 280 fps grid (6, 5, 6, 5, 6). A tap either way and five consecutive idle counts before the restart set them
                    // as the route's own restart had them.
                    foreach (int k in new[] { 6, 7, 8, 9, 10, 17, 18, 19, 20, 21 })
                        foreach (float tap in new[] { 1f, -1f, 0f })
                            list.Add((s.Name + ", after " + (tap == 0f ? "" : tap > 0f ? "a tap right and " : "a tap left and ") + k
                                + " idle ticks and a quick restart",
                                (tap == 0f ? new List<InputTick>() : new List<InputTick> { new InputTick { x = tap } })
                                    .Concat(Enumerable.Repeat(new InputTick(), k)).Concat(own).ToList()));
                }
                list.Add((s.Name + ", as it is", s.Inputs));
                if (restart && !s.Restart)
                    foreach (int k in Enumerable.Range(0, MaxSettle + 1).OrderBy(k => Math.Abs(k - Math.Max(0, settle))))
                        if (!(s.Standing && k == settle)) list.Add((s.Name + ", after a quick restart and " + k + " idle ticks", Restarted(s, k)));
                return list;
            }).ToList();
            // A restart seed's ways are alternatives for one start (the phases above), not tiers of one seed: each gets a start
            // of its own ahead of the rest, or the standing seeds' tiers fill MaxSeedStarts first.
            var spread = new List<List<(string how, List<InputTick> inputs)>>();
            for (int i = 0; i < matching.Count; i++)
                if (matching[i].Restart && restart)
                {
                    spread.AddRange(perSeed[i].Select(v => new List<(string how, List<InputTick> inputs)> { v }));
                    perSeed[i] = new List<(string how, List<InputTick> inputs)>();
                }
            perSeed.InsertRange(0, spread);
            foreach (Seed bonus in matching.Where(s => s.Standing && s.Name.StartsWith("bonus", StringComparison.OrdinalIgnoreCase)))
            {
                string token = bonus.Name.Substring(0, bonus.Name.IndexOf('_'));
                var list = new List<(string how, List<InputTick> inputs)>();
                foreach (Seed transition in _seeds.Where(s => s.Caps.IsSubsetOf(caps) && !s.Standing
                    && s.Name.IndexOf("to" + token + "_", StringComparison.OrdinalIgnoreCase) > 0))
                    foreach (int idle in new[] { 0, 3, 6, 10, 16 })
                    {
                        var inputs = new List<InputTick>(transition.Inputs);
                        InputTick rest = inputs.Count > 0 ? inputs[inputs.Count - 1] : new InputTick();
                        rest = new InputTick { held = rest.held };
                        inputs.AddRange(Enumerable.Repeat(rest, idle));
                        inputs.AddRange(bonus.Inputs);
                        list.Add((transition.Name + ", " + idle + " ticks, then " + bonus.Name, inputs));
                    }
                if (list.Count > 0) perSeed.Insert(0, list);
            }
            for (int tier = 0; starts.Count < MaxSeedStarts && perSeed.Any(l => tier < l.Count); tier++)
                foreach (var list in perSeed)
                    if (tier < list.Count && starts.Count < MaxSeedStarts && !starts.Any(s => s.inputs.SequenceEqual(list[tier].inputs)))
                        starts.Add(list[tier]);
            return starts;
        }

        /// <summary>How many unlocks a seed's caps have (flags and counts): the order seeds are preferred in, most unlocks first.</summary>
        static int Unlocks(Caps caps)
        {
            return (caps.WallJump ? 1 : 0) + caps.Dashes + caps.AirJumps + (caps.BlockSwap ? 1 : 0) + (caps.Orbs ? 1 : 0)
                + (caps.Zip ? 1 : 0) + (caps.Omni ? 1 : 0);
        }

        /// <summary>
        /// A seed from another run's start (<see cref="Seed.At"/>): that run's player stood still there, facing one way, before its
        /// inputs, so from here walk to that spot (held for n ticks, the last one maybe partial), stand still, tap the start's way
        /// once when the walk faced the other, stand still again, then play the seed. The closest stands first; none when the spot
        /// is too far or no walk ends on its floor facing its way. The start must be _eval's.
        /// </summary>
        List<(string how, List<InputTick> inputs)> WalkedStarts(Seed s)
        {
            var found = new List<(float miss, string how, List<InputTick> inputs)>();
            SeedStart to = s.At;
            _eval.Restore(_eval.Start);
            float dx = to.X - _eval.PlayerWorld.x;
            if (Math.Abs(dx) > MaxWalk) return new List<(string, List<InputTick>)>();
            float dir = dx < 0f ? -1f : 1f, face = to.FacingRight ? 1f : -1f;
            // Walks, stands, taps `turn` of the start's way (0: none) and stands again: how far past the spot it stands, null when
            // it never stands on the spot's floor.
            float? Stand(int n, float last, float turn)
            {
                var walk = Enumerable.Range(0, n).Select(i => new InputTick { x = i == n - 1 ? dir * last : dir }).ToList();
                int still = _eval.Still(walk, 300);
                if (still < 0) return null;
                if (turn > 0f)
                {
                    walk.AddRange(Enumerable.Repeat(new InputTick(), still - walk.Count));
                    walk.Add(new InputTick { x = face * turn });
                    still = _eval.Still(walk, 300);
                    if (still < 0) return null;
                }
                WorldPos p = _eval.PlayerWorld;
                if (Math.Abs(p.y - to.Y) > MaxWalkRise) return null;
                float miss = Math.Abs(p.x - to.X);
                if (miss <= MaxWalkMiss && _eval.Player.facingRight == to.FacingRight)
                {
                    walk.AddRange(Enumerable.Repeat(new InputTick(), still - walk.Count + StandTicks));
                    walk.AddRange(s.Inputs);
                    found.Add((miss, $"{s.Name}, after walking {n} ticks to its start ({miss:0.0} off) and standing still", walk));
                }
                return (p.x - to.X) * dir;
            }
            // Walking faces the walk's way: a start facing the other way takes the tap.
            float tap = face == dir ? 0f : 1f;
            int best = -1, blocked = 0;
            float bestMiss = float.MaxValue;
            float? before = null;
            for (int n = 0; n <= MaxWalkTicks && blocked < 5; n++)
            {
                float? past = Stand(n, 1f, tap);
                if (past.HasValue && Math.Abs(past.Value) < bestMiss) { bestMiss = Math.Abs(past.Value); best = n; }
                if (past > MaxWalkMiss) break;
                // A wall ends the walk: longer ones stand on the same spot.
                blocked = past.HasValue && past == before ? blocked + 1 : 0;
                before = past;
            }
            if (best >= 0)
                foreach (int n in new[] { best - 1, best, best + 1 }.Where(n => n > 0))
                    for (int last = 1; last <= 10; last++)
                        for (int turn = (int)tap; turn <= 10 * tap; turn++) Stand(n, last / 10f, turn / 10f);
            return found.OrderBy(f => f.miss).GroupBy(f => f.miss).Select(g => g.First()).Take(MaxWalks).Select(f => (f.how, f.inputs)).ToList();
        }

        // A reach window's walks to a run seed's start from its explored cells: per check, the cells nearest the start, within
        // a walk of it and a couple of squares above or below it.
        const int CellWalksPerCheck = 4;
        const float CellWalkRise = 96f;

        /// <summary>
        /// <see cref="WalkedStarts"/> from the reach window's explored cells near a run seed's start, not only from the window's
        /// start (the goal before may be far below the seed's floor, and the search gets there): the inputs to each cell, the
        /// walk and the seed, for those that reach the window's last goal. <paramref name="tried"/> holds the cells tried so far.
        /// </summary>
        List<List<InputTick>> CellWalks(OfflineSolver solver, SimSnapshot at, int prefixLength, int limit, List<Seed> walkers, HashSet<long> tried, string title)
        {
            var found = new List<List<InputTick>>();
            foreach (Seed s in walkers)
            {
                var spot = _eval.ToCapture(new WorldPos(s.At.X, s.At.Y)).AsVector2();
                foreach (List<InputTick> cell in solver.Near(spot, p => p < _goals && s.Goals.Contains(_labels[p]), MaxWalk, CellWalkRise, tried, CellWalksPerCheck))
                {
                    _eval.SetStart(at, prefixLength);
                    _eval.Goals.Limit = _goals;
                    SimSnapshot there = _eval.Advance(cell);
                    if (there == null) continue;
                    _eval.SetStart(there, prefixLength + cell.Count);
                    foreach (var (how, walk) in WalkedStarts(s))
                    {
                        List<InputTick> inputs = cell.Concat(walk).ToList();
                        _eval.SetStart(at, prefixLength);
                        RunResult r = _eval.Run(inputs);
                        if (r.Progress < limit) continue;
                        Log($"{title}: {how}, from an explored cell {cell.Count} inputs in, reaches goal {r.Progress}/{_goals}");
                        found.Add(inputs);
                        break;
                    }
                }
            }
            return found;
        }

        bool SeedFrontier()
        {
            Route best = _best;
            int reached = best.Progress;
            if (reached == _goals || !_seeds.Any(s => s.Goals.Contains(_labels[reached]))) return false;
            (int, long) frontier = (reached, reached == 0 ? 0L : best.GoalPhysics[reached - 1]);
            if (_seededFrontier == frontier) return false;
            _seededFrontier = frontier;
            int prefixLength = reached == 0 ? 0 : (int)Math.Min(best.GoalInput[reached - 1], best.Inputs.Count);
            List<InputTick> prefix = best.Inputs.GetRange(0, prefixLength);
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            SimSnapshot at = _eval.Advance(prefix);
            if (at == null) return false;
            _eval.Restore(at);
            // The last goal can be touched in the no-input ticks after its input tick (a dash): the seeds' replay takes it again.
            if (_eval.Progress != reached && _eval.Progress != reached - 1) return false;
            List<(string how, List<InputTick> inputs)> starts = SeedStarts(at, prefix, new HashSet<string> { _labels[reached] }, CanRestartAfter(prefix, reached),
                WindowCaps(_runGoals.Goals.Skip(reached).Take(1)));
            string title = $"seeds for goal {reached + 1}/{_goals} {_labels[reached]}";
            RunResult top = null;
            int reaching = 0;
            (string how, List<InputTick> inputs) chosen = default;
            _eval.SetStart(at, prefixLength);
            _eval.Goals.Limit = _goals;
            foreach (var s in starts)
            {
                RunResult r = _eval.Run(s.inputs);
                if (r.Progress <= reached) continue;
                reaching++;
                if (top == null || r.Progress > top.Progress
                    || r.Progress == top.Progress && r.GoalPhysicsTicks[r.Progress - 1] < top.GoalPhysicsTicks[top.Progress - 1]) { top = r; chosen = s; }
            }
            if (top == null)
            {
                Log($"{title}: none of {starts.Count} starts from saved routes reaches it");
                Emit(new { type = "note", level = "info", text = $"None of {starts.Count} saved-route starts reach goal {reached + 1}; searching for it instead." });
                return false;
            }
            Log($"{title}: {chosen.how} reaches goal {top.Progress}/{_goals} {_labels[top.Progress - 1]} at "
                + (top.GoalPhysicsTicks[top.Progress - 1] * _dt).ToString("0.00", CultureInfo.InvariantCulture) + $"s ({reaching} of {starts.Count} starts reach it)");
            Route run = Splice(best, prefixLength, chosen.inputs, top.Progress, best);
            if (run == null || !Better(run, _best))
            {
                Log(title + ": the whole run with it does no better; dropped");
                Emit(new { type = "note", level = "info", text = $"A saved route reaches goal {top.Progress}, but the whole run with it does no better; dropped." });
                return false;
            }
            Commit(run, "from a saved route (" + chosen.how + ")");
            return true;
        }

        (int, long) _graftedFrontier = (-1, -1);
        List<(string name, string[] labels, long[] goalInput, List<InputTick> inputs)> _knownRuns;
        /// <summary>A known tail is grafted from the frontier and up to this many goals before it.</summary>
        const int GraftBack = 8, GraftShift = 4;

        /// <summary>
        /// Reach by known tails: where the best stops, the goals after it as another saved route of this run played them (the run
        /// file's copies fulltas/&lt;run&gt;.*.json, each with its goal list and goal ticks: the solve's .before-repair-* backups, older
        /// bests kept by hand). From goal i (the frontier, then up to GraftBack goals before it): the best's inputs to goal i, then
        /// that route's from where it reached the same goal with the same goals from there to the end, its start shifted by
        /// -GraftShift..+GraftShift inputs. Kept when the whole run reaches further or as far sooner (Better). Once per frontier.
        /// FalseEnding-Any%: after a goals edit the reach windows stalled in the ending (goals 277-280), which the old route's
        /// ending from buy:buydj on finished once spliced in by hand.
        /// </summary>
        bool GraftKnownTails()
        {
            Route best = _best;
            int reached = best.Progress;
            (int, long) frontier = (reached, reached == 0 ? 0L : best.GoalPhysics[reached - 1]);
            if (reached == 0 || reached == _goals || _graftedFrontier == frontier) return false;
            _graftedFrontier = frontier;
            _knownRuns ??= KnownRuns();
            if (_knownRuns.Count == 0) return false;
            (List<InputTick> inputs, int progress, long tick, string how) top = (null, reached, best.GoalPhysics[reached - 1], null);
            for (int i = reached; i >= Math.Max(1, reached - GraftBack) && !TimeUp; i--)
            {
                int prefixLength = (int)Math.Min(best.GoalInput[i - 1], best.Inputs.Count);
                List<InputTick> prefix = best.Inputs.GetRange(0, prefixLength);
                SimSnapshot at = null;
                foreach (var (name, labels, goalInput, inputs) in _knownRuns)
                {
                    int j = labels.Length - (_goals - i);
                    if (j < 1 || j > goalInput.Length || labels[j - 1] != _labels[i - 1] || !labels.Skip(j).SequenceEqual(_labels.Skip(i))) continue;
                    if (at == null)
                    {
                        _eval.SetStart(_origin, 0);
                        _eval.Goals.Limit = _goals;
                        if ((at = _eval.Advance(prefix)) == null) break;
                    }
                    for (int s = 0; s <= 2 * GraftShift; s++)
                    {
                        int shift = (s + 1) / 2 * (s % 2 == 0 ? -1 : 1);
                        int from = (int)goalInput[j - 1] + shift;
                        if (from < 0 || from >= inputs.Count || prefixLength > 0 && prefix[prefixLength - 1].reset && inputs[from].reset) continue;
                        List<InputTick> tail = inputs.GetRange(from, inputs.Count - from);
                        _eval.SetStart(at, prefixLength);
                        _eval.Goals.Limit = _goals;
                        RunResult r = _eval.Run(tail);
                        if (r.Progress == 0) continue;
                        long tick = r.GoalPhysicsTicks[r.Progress - 1];
                        if (r.Progress > top.progress || r.Progress == top.progress && tick < top.tick)
                            top = (prefix.Concat(tail).ToList(), r.Progress, tick, $"{name}'s goals {i + 1}-{_goals} from its input {from} (its goal {j}{(shift == 0 ? "" : $" {shift:+0;-0} inputs")})");
                    }
                }
                if (top.progress == _goals) break;
            }
            Route run = top.inputs == null ? null : Evaluate(top.inputs);
            if (run == null || !Better(run, best))
            {
                Log($"known tails for goal {reached + 1}/{_goals}: none of {_knownRuns.Count} saved routes of this run gets further from goals {Math.Max(1, reached - GraftBack)}-{reached}");
                return false;
            }
            Commit(run, "a known tail: " + top.how);
            return true;
        }

        /// <summary>The run file's copies beside it (fulltas/&lt;run&gt;.*.json) that carry their goals and goal ticks.</summary>
        List<(string, string[], long[], List<InputTick>)> KnownRuns()
        {
            var runs = new List<(string, string[], long[], List<InputTick>)>();
            string dir = Path.GetDirectoryName(_runFile);
            if (!Directory.Exists(dir)) return runs;
            foreach (string file in Directory.GetFiles(dir, _runKey + ".*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                if (Path.GetFileName(file).StartsWith(_runKey + ".auto", StringComparison.Ordinal) || OtherClassFile(Path.GetFileName(file))) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 256 });
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("goals", out JsonElement goals) || !root.TryGetProperty("goalInputTicks", out JsonElement ticks)
                        || !root.TryGetProperty("inputs", out _)) continue;
                    runs.Add((Path.GetFileName(file), goals.EnumerateArray().Select(g => g.GetString()).ToArray(), ticks.EnumerateArray().Select(t => t.GetInt64()).ToArray(), Solve.ReadInputs(root)));
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidOperationException) { }
            }
            Log($"known tails: {runs.Count} saved routes of {_name} beside its run file");
            return runs;
        }

        /// <summary>
        /// Each course seed's human versions in offline-best (<c>&lt;course&gt;_&lt;caps&gt;-&lt;time&gt;-human.json</c>, fastest first) as
        /// seeds of their own: a run reaches a course's restart a little differently from the category start the TAS line was
        /// found from, and the looser inputs can still finish there (FalseEnding%-280's goal 200: `3_WJ_DS 3.68` died after a
        /// landing 0.01 u off, the 3.72's human version finished).
        /// </summary>
        void AddHumanSeeds()
        {
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "offline-best");
            if (!Directory.Exists(dir)) return;
            foreach (Seed course in _seeds.ToList())
            {
                string finish = course.Goals.FirstOrDefault(g => g.StartsWith("finish:", StringComparison.Ordinal));
                if (course.At != null || !course.Standing || finish == null) continue;
                string prefix = finish.Substring("finish:".Length) + "_" + course.Caps.Id + "-";
                var files = Directory.GetFiles(dir, prefix + "*-human.json")
                    .Select(f => (file: f, time: float.TryParse(Path.GetFileName(f).Substring(prefix.Length).Split('-')[0], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float t) ? t : float.MaxValue))
                    .OrderBy(f => f.time);
                foreach (var (file, _) in files)
                {
                    Seed human = ReadSeed(file);
                    if (_seeds.Any(s => s.Inputs.SequenceEqual(human.Inputs))) continue;
                    human.Name = Path.GetFileNameWithoutExtension(file);
                    human.Goals = course.Goals;
                    human.Standing = true;
                    human.Restart = course.Restart;
                    human.Caps = course.Caps;
                    _seeds.Add(human);
                }
            }
        }

        static Seed ReadSeed(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 256 });
            JsonElement root = doc.RootElement;
            bool Flag(string name) { return root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True; }
            var goals = new List<string>();
            if (root.TryGetProperty("goals", out JsonElement g) && g.ValueKind == JsonValueKind.Array) goals.AddRange(g.EnumerateArray().Select(e => e.GetString()));
            else if (root.TryGetProperty("course", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() > 0)
                goals.AddRange(new[] { "start:" + c.GetInt32(), "finish:" + c.GetInt32() });
            else if (root.TryGetProperty("token", out JsonElement t) && t.ValueKind == JsonValueKind.String && t.GetString().StartsWith("bonus", StringComparison.OrdinalIgnoreCase))
                goals.Add("reward:" + t.GetString().ToLowerInvariant());
            return new Seed
            {
                Name = root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : Path.GetFileName(path),
                Goals = goals.ToArray(),
                Standing = Flag("standing"),
                Restart = Flag("restart"),
                Caps = !root.TryGetProperty("capabilities", out JsonElement caps) || caps.ValueKind != JsonValueKind.String
                    || !Caps.TryParse(caps.GetString(), out Caps seedCaps) ? default(Caps) : seedCaps,
                Inputs = Solve.ReadInputs(root),
                At = root.TryGetProperty("trace", out JsonElement trace) && trace.ValueKind == JsonValueKind.String ? StartOf(trace.GetString()) : null,
            };
        }

        /// <summary>A Full-TAS trace's start (its player stands still there), null when the trace is missing.</summary>
        static SeedStart StartOf(string trace)
        {
            if (!File.Exists(trace)) return null;
            JsonElement start = Solve.ReadHeader(trace).GetProperty("start");
            JsonElement position = start.GetProperty("position"), origin = start.GetProperty("origin");
            // The movement state is [type, field, value] rows.
            JsonElement facing = start.GetProperty("movement").EnumerateArray().First(f => f[1].GetString() == "facingRight")[2];
            return new SeedStart
            {
                X = position.GetProperty("x").GetSingle() - origin.GetProperty("x").GetSingle(),
                Y = position.GetProperty("y").GetSingle() - origin.GetProperty("y").GetSingle(),
                FacingRight = facing.GetBoolean(),
            };
        }

        void Merge(string where, Route best, int prefixLength, int limit, List<List<InputTick>> found)
        {
            Route broken = null;
            int missed = 0, slower = 0;
            foreach (List<InputTick> route in Enumerable.Reverse(found).Take(MergeFinds))
            {
                Route candidate = Splice(best, prefixLength, route, limit, best);
                if (candidate == null) { missed++; continue; }
                if (Better(candidate, _best))
                {
                    Commit(candidate, candidate.Progress >= limit ? where
                        : $"faster to goal {candidate.Progress} (its route to goal {limit} misses it in the whole run)");
                    return;
                }
                slower++;
                // Only one ahead at the window's last goal is worth re-finding the rest for (spliced into the whole run, a window that
                // was faster on its own can arrive later: a buy waiting on cash).
                if (broken == null && candidate.Progress < best.Progress && candidate.Progress >= limit
                    && AheadAt(candidate, best, limit)) broken = candidate;
            }
            if (broken == null)
            {
                // Faster through the window, and the old route after it still reaches every goal, only later: it was played for the
                // old state. Re-find the goals from where it falls behind (Repair), rather than dropping the faster window.
                Route slow = found.Count == 0 ? null : Splice(best, prefixLength, found[found.Count - 1], limit, best);
                if (slow != null && slow.Progress >= best.Progress && slow.Progress >= limit && AheadAt(slow, best, limit)) broken = slow;
            }
            if (broken != null)
            {
                double gain = Seconds(best.Clock(limit) - broken.Clock(limit));
                string tail = broken.Progress >= best.Progress
                    ? $"finishes {broken.LiveSeconds - best.LiveSeconds:+0.00;-0.00}s against the record"
                    : $"stops at goal {broken.Progress} of {best.Progress}";
                Emit(new { type = "note", level = "info", gain, text = $"Faster through goal {limit} by {gain:0.00}s, but the old route after it {tail}; "
                    + $"re-finding goals {limit + 1}-{best.Progress}. Worth up to −{gain:0.00}s if they come back as fast as before." });
                Repair(where, best, broken);
                return;
            }
            Emit(new { type = "dropped", missed, slower, text = missed > 0 && slower == 0
                ? $"The route found through goal {limit} didn't reproduce when replayed from the start; dropped."
                : best.Progress < limit ? $"The route found to goal {limit} doesn't get there when replayed as part of the whole run; dropped."
                : $"The route found through goal {limit} is faster there, but the whole run ends up slower; dropped." });
        }

        Route Splice(Route head, int prefixLength, List<InputTick> window, int limit, Route tail)
        {
            List<InputTick> inputs = head.Inputs.Take(prefixLength).Concat(window).ToList();
            Route through = Evaluate(inputs, limit);
            if (through.Progress < limit)
            {
                Log("a window route missed its goals when replayed from the start; dropped" + Diverged(head.Inputs.Take(prefixLength).ToList(), window, limit));
                return null;
            }
            int end = (int)through.GoalInput[limit - 1];
            if (end > inputs.Count) Pad(inputs, end);
            else
            {
                // A goal reached in ticks that take no input (TouchGrass-ANY%'s 269-tick fall to the checkpoint below Course 4) counts
                // the input tick before them: cut there, the run's inputs are spent and it ends 20 ticks into them (the gate's idle
                // rule). With no old tail to follow, keep the next input pending, as the window's own run had it.
                int keep = tail.Progress >= limit ? end : Math.Min(end + 1, inputs.Count);
                inputs.RemoveRange(keep, inputs.Count - keep);
            }
            if (tail.Progress >= limit) inputs.AddRange(tail.Inputs.Skip((int)tail.GoalInput[limit - 1]));
            for (int i = 1; i < inputs.Count; i++) if (inputs[i].reset && inputs[i - 1].reset) return null;
            return Evaluate(inputs);
        }

        /// <summary>
        /// Why a window's route missed its goals from the start: the window from its start as a worker gets it (the stored or
        /// replayed state at the prefix's end), and after a straight replay of the prefix, against the whole route played through
        /// from the run's start as the evaluator does; the first physics tick the player differs (position or a value field of the
        /// player or the input engine) and what differs there. Each simulation runs on a thread of its own: the game's statics and
        /// the engine hooks are per thread, and a second simulation on a thread takes them over from the first.
        /// </summary>
        string Diverged(List<InputTick> prefix, List<InputTick> window, int limit)
        {
            try
            {
                static Dictionary<string, string> Fields(Simulation sim)
                {
                    var f = new Dictionary<string, string>();
                    const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    foreach (var (prefixName, target) in new (string, object)[] { ("", sim.Player), ("engine.", sim.Engine) })
                        foreach (System.Reflection.FieldInfo x in target.GetType().GetFields(all))
                            if (x.FieldType.IsValueType || x.FieldType == typeof(string))
                                f[prefixName + x.Name] = Convert.ToString(x.GetValue(target), CultureInfo.InvariantCulture);
                    return f;
                }
                (Dictionary<long, (WorldPos at, Dictionary<string, string> fields)> ticks, int progress, string end) Play(Func<(Simulation, SimSnapshot, List<InputTick>, long)> setup)
                {
                    return Solve.OnThread(() =>
                    {
                        var (sim, from, inputs, inputBase) = setup();
                        var ticks = new Dictionary<long, (WorldPos at, Dictionary<string, string> fields)>();
                        sim.SetStart(from, inputBase);
                        sim.Goals.Limit = limit;
                        // As the solver's goal mode runs a window: 400 idle ticks past its inputs, and as many again.
                        RunResult r = sim.Run(inputs, null, null, 400 + inputs.Count, _ => ticks[sim.Engine.PhysicsTicks] = (sim.PlayerWorld, Fields(sim)));
                        return (ticks, r.Progress, r.EndReason);
                    });
                }
                var worker = Play(() => { var (sim, state) = Prefixed(prefix, "diverge"); return (sim, state, window, prefix.Count); });
                var straight = Play(() => { Simulation sim = _create(); return (sim, sim.Advance(prefix), window, prefix.Count); });
                var through = Play(() => { Simulation sim = _create(); return (sim, sim.Start, prefix.Concat(window).ToList(), 0); });
                string First((Dictionary<long, (WorldPos at, Dictionary<string, string> fields)> ticks, int progress, string end) a,
                    (Dictionary<long, (WorldPos at, Dictionary<string, string> fields)> ticks, int progress, string end) b)
                {
                    string[] counters = { "engine._runTicks", "engine._stepIndex", "engine._pressedStep", "engine._quickRestartStep", "engine._physicsTicks", "engine._countEveryPhysicsTick" };
                    foreach (long t in a.ticks.Keys.Where(b.ticks.ContainsKey).OrderBy(t => t))
                    {
                        var (aa, fa) = a.ticks[t];
                        var (ab, fb) = b.ticks[t];
                        var differ = fa.Where(kv => !counters.Contains(kv.Key) && fb.TryGetValue(kv.Key, out string v) && v != kv.Value).ToList();
                        if (aa.x == ab.x && aa.y == ab.y && differ.Count == 0) continue;
                        return $"first differs at physics tick {t}: at ({aa.x:0.00}, {aa.y:0.00}) vs ({ab.x:0.00}, {ab.y:0.00})"
                            + (differ.Count > 0 ? "; " + string.Join(", ", differ.Take(10).Select(kv => $"{kv.Key} {kv.Value} vs {fb[kv.Key]}")) : "");
                    }
                    return "the same every tick";
                }
                return $" [diverge: goal {worker.progress} from the worker's start ({worker.end}), {straight.progress} after a straight replay of the prefix ({straight.end}),"
                    + $" {through.progress} played through from the start ({through.end}); worker vs through: " + First(worker, through) + "; straight vs through: " + First(straight, through) + "]";
            }
            catch (Exception ex) { return $" [diverge check failed: {ex.GetBaseException().Message}]"; }
        }

        void Repair(string where, Route old, Route run)
        {
            _repairBegin?.Invoke(run);
            int from = Enumerable.Range(1, Math.Min(run.Progress, old.Progress)).OrderByDescending(k => old.Clock(k) - run.Clock(k)).FirstOrDefault();
            string lost = from > 0 && old.Clock(from) > run.Clock(from)
                ? $" (−{Seconds(old.Clock(from) - run.Clock(from)):0.00}s through goal {from} not kept)" : "";
            int resynced = 0;
            while (!TimeUp)
            {
                if (run.Progress >= old.Progress)
                {
                    if (Better(run, _best)) { Commit(run, where + ", then re-found the later goals"); return; }
                    // Complete but slower: it gets ahead and the old inputs after that lose it again, played from a state they weren't
                    // made for. Search on from the goal where it first falls behind the best, while that moves forward.
                    int behind = Enumerable.Range(0, Math.Min(run.Progress, _best.Progress)).FirstOrDefault(g => ScoreAt(run, g + 1) > ScoreAt(_best, g + 1), -1);
                    if (behind > resynced)
                    {
                        Log($"repaired run {Describe(run)} is no better: it falls behind the best at goal {behind + 1}; re-finding from there");
                        resynced = behind;
                        run = new Route { Inputs = run.Inputs.Take((int)Math.Min(run.GoalInput[behind - 1], run.Inputs.Count)).ToList(), Progress = behind,
                            GoalInput = run.GoalInput, GoalPhysics = run.GoalPhysics, GoalHeld = run.GoalHeld, EndReason = run.EndReason };
                        continue;
                    }
                    Log($"repaired run {Describe(run)} is no better; dropped");
                    Emit(new { type = "dropped", text = "Re-found the later goals, but the whole run is no faster; dropped." + lost });
                    return;
                }
                if (run.Live >= Bar(_best, false))
                {
                    Log($"repair dropped: goal {run.Progress}/{_goals} reached no sooner than the best's last goal");
                    Emit(new { type = "dropped", text = $"Repair gave up: it reaches goal {run.Progress} no sooner than the record reaches goal {_best.Progress}." + lost });
                    return;
                }
                string hopeless = _hopeless?.Invoke(run);
                if (hopeless != null)
                {
                    Log("repair dropped: " + hopeless);
                    Emit(new { type = "dropped", text = "Repair gave up: " + hopeless + "." + lost });
                    return;
                }
                int reached = run.Progress, limit = Math.Min(reached + _window + _lookahead, old.Progress);
                int prefixLength = (int)Math.Min(run.GoalInput[reached - 1], run.Inputs.Count);
                var seeds = new List<List<InputTick>> { run.Inputs.Skip(prefixLength).ToList(), old.Inputs.Skip((int)old.GoalInput[reached - 1]).ToList() };
                List<List<InputTick>> found = SearchWindow("repair", run, prefixLength, limit, seeds, long.MaxValue, _repairMinutes ?? _windowMinutes, 0);
                if (found.Count == 0 && Rejoin(run, old, reached) is Route rejoined)
                {
                    run = rejoined;
                    _collect?.Invoke(rejoined, where + " (repairing)");
                    continue;
                }
                if (found.Count == 0)
                {
                    Log($"repair dropped: no route to {_labels[reached]} from the new state");
                    Emit(new { type = "dropped", text = $"Repair gave up: no way to goal {reached + 1} from the new state." + lost });
                    return;
                }
                Route next = Splice(run, prefixLength, found[found.Count - 1], limit, old);
                if (next == null || next.Progress <= reached)
                {
                    Log("repair dropped: its route missed its goals when replayed from the start");
                    Emit(new { type = "dropped", text = "Repair gave up: its route didn't reproduce when replayed from the start." + lost });
                    return;
                }
                run = next;
                _collect?.Invoke(next, where + " (repairing)");
            }
        }

        /// <summary>How many goals back a stuck repair looks for a goal to rejoin the old way on at (<see cref="Rejoin"/>).</summary>
        const int RejoinGoals = 16;

        /// <summary>
        /// A repair with no route to goal <paramref name="reached"/> + 1: the run's inputs to one of the RejoinGoals goals before, then
        /// the old route's inputs from that goal on, played from where the repair got to. The latest such goal whose splice gets past
        /// the stuck goal; null when none does. The windows' own finds can leave the player where the old inputs of the next goal
        /// don't play (FalseEnding-Any%: a payout break re-found to goal 273, 60 ticks ahead, then no way into at:20300.4's
        /// 100-unit box), while the old inputs from a goal before them still do.
        /// </summary>
        Route Rejoin(Route run, Route old, int reached)
        {
            for (int k = reached - 1; k >= Math.Max(1, reached - RejoinGoals) && !TimeUp; k--)
            {
                long a = run.GoalInput[k - 1], b = old.GoalInput[k - 1];
                if (a < 0 || b < 0 || b > old.Inputs.Count) continue;
                var inputs = run.Inputs.Take((int)Math.Min(a, run.Inputs.Count)).ToList();
                Pad(inputs, a);
                inputs.AddRange(old.Inputs.Skip((int)b));
                Route r = Evaluate(inputs);
                if (r.Progress <= reached) continue;
                Log($"repair: no route to {_labels[reached]} from the new state; rejoined the old way on at goal {k} ({_labels[k - 1]}): {Describe(r)}");
                return r;
            }
            return null;
        }

        static void Pad(List<InputTick> inputs, long count)
        {
            bool held = inputs.Count > 0 && inputs[inputs.Count - 1].held;
            while (inputs.Count < count) inputs.Add(new InputTick { held = held });
        }

        Route Padded(Route r)
        {
            if (r.Progress == 0 || r.GoalInput[r.Progress - 1] <= r.Inputs.Count) return r;
            List<InputTick> inputs = r.Inputs.ToList();
            Pad(inputs, r.GoalInput[r.Progress - 1]);
            Route padded = Evaluate(inputs);
            return padded.Progress == r.Progress && padded.Live == r.Live ? padded : r;
        }

        void Commit(Route r, string where)
        {
            r = Padded(r);
            if (_verify != null && (r = _verify(r, where)) == null) return;
            if (_course != null && _course.Loop && !LoopKeeps(r, where)) return;
            Route previous = _best;
            // A class writes and submits only a route its inputs play from the run's start to the goal ticks it claims (the report it
            // was ranked by is then its own).
            if (Classed && !Replays(r))
            {
                RunResult played = _reports[ArchiveHash(r.Inputs, _labels)].run;
                Log($"not kept ({where}): {Describe(r)} doesn't replay from the start as claimed ({played.Progress} of {_goals} goals: {played.EndReason})");
                return;
            }
            // A route a lever chose by live time alone (a goals edit, a re-timing) is kept in a class only when it ranks ahead there.
            if (Classed && r.Progress == previous.Progress && !Ahead(r, previous))
            {
                Log($"not kept ({where}): {Describe(r)} ranks behind the best {Describe(previous)} in {Glitches.Key(_class)}");
                return;
            }
            _best = r;
            WriteRunFile(r);
            string file = _runFile;
            if (r.Progress == _goals && Clean(r))
            {
                float time = (float)r.LiveSeconds;
                Directory.CreateDirectory(_outDir);
                file = Path.Combine(_outDir, "fulltas-" + _runKey + "-" + time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
                // A route of goals the loop adopted (--parts goals) carries them and the lists they came from: the game judges it
                // under them and takes them as the run's when it saves the record.
                string[] goals = _goalLineage.Count == 0 ? null : _labels;
                List<string[]> goalsFrom = _goalLineage.Count == 0 ? null : _goalLineage.ToList();
                Solve.WriteShared(file, Classed
                    ? JsonSerializer.Serialize(new { fulltas = _name, @class = Glitches.Key(_class), time, score = Human ? ScoreSeconds(r) : (double?)null, runTicks = r.Last, goals, goalsFrom,
                        ticks = r.Inputs.Select(t => t.ToJson()) })
                    : JsonSerializer.Serialize(new { fulltas = _name, time, runTicks = r.Last, goals, goalsFrom, ticks = r.Inputs.Select(t => t.ToJson()) }));
                _pending?.Set(new Submission { Inputs = r.Inputs, Goals = goals, GoalsFrom = goalsFrom });
            }
            // An nmg class's route with a major span is the search's working route only: nothing of it is saved or submitted.
            GlitchSpan major = Clean(r) ? null : r.Report.Spans.First(s => s.Category == Glitches.MajorCategory);
            Log($"new best {Describe(r)} ({where}) -> " + (major == null ? Path.GetFileName(file) : $"not saved: it uses {major.Tech} (major) at input tick {major.Tick}"));
            Emit(new { type = "record", how = where, previous = RouteJson(previous), best = RouteJson(r), file = major == null ? Path.GetFileName(file) : null,
                complete = r.Progress == _goals, firstComplete = r.Progress == _goals && previous.Progress < _goals, goalsEdit = _goalsEdit,
                goals = _goalsEdit == null ? null : _labels });
            _committed?.Invoke(r, where);
            // Every complete best has its waits re-timed to their Cash from the start: an edit (a goals adoption, a window)
            // moves the Cash every later wait was made for, and the loop's own re-timings start at a buy site.
            if (r.Progress != _goals || _retuningCommit) return;
            _retuningCommit = true;
            try
            {
                Route re = Retune(_eval, _origin, r, r.Inputs, 0);
                if (re != null && re.Progress == _goals && Ahead(re, r)) Commit(re, where + ", waits re-timed from the start");
            }
            finally { _retuningCommit = false; }
        }

        bool _retuningCommit;

        /// <summary>After a new best is kept (a course run: CourseAuto writes the course's offline best); null for a Full-TAS run.</summary>
        Action<Route, string> _committed;

        /// <summary>The run file (fulltas/&lt;run&gt;.json, a class's fulltas/&lt;run&gt;.&lt;class&gt;[.&lt;tag&gt;].json with its class, score and
        /// spans): the route's inputs with the goal ticks it reaches them at. An nmg class never writes a route with a major span.</summary>
        void WriteRunFile(Route r)
        {
            if (!Clean(r)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_runFile));
            Solve.WriteShared(_runFile, RunJson(r, RunFields(r)));
        }

        object RunFields(Route r)
        {
            return new
            {
                name = _name,
                goals = _labels,
                inputs = r.Inputs.Select(t => t.ToJson()),
                goalInputTicks = r.GoalInput,
                goalPhysicsTicks = r.GoalPhysics,
                goalHeldFrames = r.GoalHeld,
                total = r.Progress == _goals ? r.Last : -1,
                live = r.Progress == _goals ? r.LiveSeconds : -1,
                startAt = new { x = _startAt.x, y = _startAt.y, facingRight = _startAt.facingRight },
                goalsFrom = _goalLineage.Count == 0 ? null : _goalLineage,
            };
        }

        /// <summary>A complete route for the gate, with its goals when they are the loop's adopted ones (null: the run's).</summary>
        sealed class Submission
        {
            public List<InputTick> Inputs;
            public string[] Goals;
            public List<string[]> GoalsFrom;
        }

        void Submitter()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            int deferred = 0;
            while (true)
            {
                Submission inputs = _pending.Take();
                string body = Classed
                    ? JsonSerializer.Serialize(new { name = _name, @class = Glitches.Key(_class), ticks = inputs.Inputs.Select(t => t.ToJson()), goals = inputs.Goals, goalsFrom = inputs.GoalsFrom })
                    : JsonSerializer.Serialize(new { name = _name, ticks = inputs.Inputs.Select(t => t.ToJson()), goals = inputs.Goals, goalsFrom = inputs.GoalsFrom });
                try
                {
                    string reply = Solve.Post(http, "/api/fulltas/submit", body);
                    using JsonDocument r = JsonDocument.Parse(reply);
                    JsonElement root = r.RootElement;
                    if (!root.TryGetProperty("finished", out JsonElement finished))
                    {
                        Log("gate check deferred: " + reply);
                        if (++deferred < 30)
                        {
                            Thread.Sleep(2000);
                            _pending.SetIfEmpty(inputs);
                        }
                        else Emit(new { type = "note", level = "error", text = "The game kept refusing to check the complete route (" + reply + "); it's kept on disk, not saved." });
                        continue;
                    }
                    deferred = 0;
                    bool done = finished.GetBoolean();
                    double? seconds = done && root.TryGetProperty("time", out JsonElement time) && time.ValueKind == JsonValueKind.Number ? Math.Round(time.GetDouble(), 4) : (double?)null;
                    bool kept = root.TryGetProperty("saved", out JsonElement saved) && saved.ValueKind == JsonValueKind.True;
                    string why = root.TryGetProperty("reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
                    Log("gate check: " + (seconds != null ? "finish " + seconds.Value.ToString("0.00", CultureInfo.InvariantCulture) + "s" : "no finish")
                        + (kept ? " - saved as the new route" : " - not saved") + (why != null ? " - " + why : ""));
                    Emit(new { type = "gate", finished = done, time = seconds, saved = kept, reason = why });
                }
                catch (Exception ex)
                {
                    deferred = 0;
                    Log($"gate check skipped (game not reachable: {ex.GetBaseException().Message}); best kept on disk");
                    Emit(new { type = "note", level = "warn", text = $"Couldn't reach the game to check the complete route ({ex.GetBaseException().Message}); it's kept on disk." });
                }
            }
        }
    }
}
