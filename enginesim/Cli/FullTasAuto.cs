using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using UnityEngine;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    // --auto (docs/fulltas.md, "Auto"): an anytime loop that spends the search where the measured gap is, with no one choosing
    // goals or levers. It needs no reference route: every goal gets a lower bound (the distance to its target over the fastest
    // speed the engine has shown with those unlocks), the best's speed profile flags its slow stretches, and a bandit over
    // (lever, goal) arms learns which pays in ticks per CPU second. Every route a lever finds (window finishes, repaired
    // partials, plan finishes, polish results, old bests) is kept as a reference, so a later pull can graft from it. Each new
    // best is replayed on a fresh simulation before it is kept (the engine's results can depend on its history).
    sealed partial class FullTas
    {
        bool _auto;
        string[] _args;
        /// <summary>Commit's check (--auto): the route replayed on a fresh simulation, returned as that replay has it, or null to keep the best.</summary>
        Func<Route, string, Route> _verify;
        /// <summary>Repair's bound (--auto): why a partial repaired run can't beat the best any more (null: search on).</summary>
        Func<Route, string> _hopeless;
        /// <summary>Repair's harvest (--auto): every partial run a repair window reaches.</summary>
        Action<Route, string> _collect;
        /// <summary>Repair's start (--auto): sets the repair's CPU budget from the lead it chases.</summary>
        Action<Route> _repairBegin;
        /// <summary>A repair window's budget (--auto: --repair-minutes); null: --window-minutes.</summary>
        double? _repairMinutes;

        sealed class AutoRef
        {
            public string How;
            public List<InputTick> Inputs;
            public int Progress;
            public long[] GoalPhysics, GoalInput;
            public ulong Key;
        }

        sealed class Arm
        {
            public string Lever;
            public int Goal, Pulls, Wins;
            /// <summary>A scheduler pull's last goal (0: the lever's own).</summary>
            public int Limit;
            /// <summary>The last window pull's lead at its limit over the best it started from (ticks; 0: none).</summary>
            public long Lead;
            public Pull Pull;
            public double Cpu, Gain;
        }

        sealed class GoalRow
        {
            public int K;
            public string Label, Caps, Worst = "";
            public long Split, Arrival, Bound;
            public float Distance, VTop;
            public long RefArrival = -1, RefSplit = -1;
            public string RefArrivalHow, RefSplitHow;
            public double Deficit, Expected;
            public long Gap => Math.Max(0, Split - Bound);
            public long ArrivalLead => RefArrival >= 0 ? Math.Max(0, Arrival - RefArrival) : 0;
            public long SplitLead => RefSplit >= 0 ? Math.Max(0, Split - RefSplit) : 0;
        }

        struct Sample
        {
            public long Tick;
            public int Input, Progress;
            public float X, Y, CX, CY, Dist, Speed;
            public string Caps;
        }

        static readonly string[] AutoLevers = { "polish", "window", "lookahead", "span", "dropreset", "graft", "plan" };
        // A lever's share of a goal's expected headroom it is credited with before it has run there (the bandit's prior).
        static readonly Dictionary<string, double> LeverPrior = new Dictionary<string, double>
        {
            // From the VMAN work (2026-09-29): a window scored at a small goal 3-5 goals on gave the goal 11 gain where windows scored
            // at large area boxes found arrivals the next goals couldn't use; its resets at inputs 61-138 were teleports worth keeping.
            ["polish"] = .15, ["window"] = .2, ["lookahead"] = .3, ["span"] = .45, ["dropreset"] = .15, ["graft"] = .6, ["plan"] = .3,
        };
        // Expected headroom from each signal: another known route did it (proven), the best's own slow stretches, the bound.
        const double ProvenWeight = .6, DeficitWeight = .3, GapWeight = .05;
        // A tick's move longer than twice what the player's speed (|Velocity + momentum|) covers in it, plus this slack, is a quick
        // restart's or a respawn's teleport, not movement.
        const float TeleportSlack = 20f;
        const int SpeedWindow = 10, SlowStretch = 6, MaxRefs = 400;
        const float SlowShare = .35f;

        readonly List<AutoRef> _refs = new List<AutoRef>();
        readonly HashSet<ulong> _refKeys = new HashSet<ulong>();
        readonly Dictionary<(string, int), Arm> _arms = new Dictionary<(string, int), Arm>();
        readonly Dictionary<string, (int pulls, double cpu)> _leverCost = new Dictionary<string, (int, double)>();
        readonly HashSet<(string, int)> _grafted = new HashSet<(string, int)>();
        int _pullNo;
        readonly HashSet<(int goal, int offset)> _studied = new HashSet<(int, int)>();
        readonly HashSet<int> _planDead = new HashSet<int>();
        readonly Dictionary<string, (float speed, string how)> _vtop = new Dictionary<string, (float, string)>();
        List<GoalRow> _model;
        long[] _bounds;
        string _autoLog, _refsFile;
        JsonElement? _autoHeader;
        int _repairSlack = 40;
        bool _refsChanged;
        /// <summary>--focus a-b: the scheduler's regions and every lever's arms are goals a..b only (a pull's window starts there);
        /// combine still splices known routes anywhere. Default: every goal.</summary>
        int _focusFrom = 1, _focusTo = int.MaxValue;

        bool InFocus(int k) { return k >= _focusFrom && k <= _focusTo; }

        static double CpuSeconds()
        {
            using Process p = Process.GetCurrentProcess();
            return p.TotalProcessorTime.TotalSeconds;
        }

        JsonElement AutoHeader() { _autoHeader ??= Solve.ReadHeader(_trace); return _autoHeader.Value; }

        void AutoWrite(object e)
        {
            string line = JsonSerializer.Serialize(e, EventJson);
            File.AppendAllText(_autoLog, line + "\n");
            Console.WriteLine("@fulltas " + line);
        }

        long Split(long[] physics, int k) { return physics[k - 1] - (k == 1 ? 0 : physics[k - 2]); }

        string Splits(Route r) { return string.Join(" ", Enumerable.Range(1, r.Progress).Select(k => $"{k}:{Split(r.GoalPhysics, k)}")); }

        /// <summary>The unattended loop (see the class comment); returns when --minutes is up or no arm is left.</summary>
        void Auto(string[] args)
        {
            // --ref routes, replayed on this engine. A complete one faster than the best is the loop's start: the studio passes the
            // run's saved record, which the engine's run file trails when that record was found and judged elsewhere.
            _parts = ParseParts(Solve.Option(args, "--parts", string.Join(",", AllParts)));
            // A course run's goals are its waypoints: no economy to edit.
            if (_course != null) _parts.Remove("goals");
            // --parts goals: a run file whose goals an earlier --auto adopted from this trace's goals runs on them.
            if (Part("goals")) ResumeAdoptedGoals();
            var refs = new List<(string file, Route route)>();
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i] == "--ref") refs.Add((args[i + 1], Evaluate(global::IGTAP.EngineSim.Cli.Plan.ReadRoute(args[i + 1]))));
            foreach (var (file, r) in refs)
                if (r.Progress == _goals && (_best.Progress < _goals || r.Last < _best.Last))
                {
                    Log($"auto: --ref {Path.GetFileName(file)} ({Describe(r)}) beats the best ({Describe(_best)}): the loop starts from it");
                    _best = r;
                    WriteRunFile(r);
                }
            if (_best.Progress < _goals)
                throw new InvalidOperationException($"--auto improves a complete route; the best reaches {_best.Progress} of {_goals} goals (run the solve with --unsolved first)");
            // A repair chases the lead its window found: it gets --repair-cpu-per-tick CPU seconds per tick of that lead and is dropped
            // once it falls behind the best at a goal (--repair-slack ticks, default none).
            _repairSlack = Solve.Option(args, "--repair-slack", 0);
            _repairCpuPerTick = Solve.Option(args, "--repair-cpu-per-tick", 15.0);
            _repairMax = Solve.Option(args, "--repair-minutes", Math.Max(0.25, _windowMinutes));
            _repairMinutes = _repairMax;
            // Windows run side by side, each on a few threads, rather than one window on every thread.
            _parallel = Math.Max(1, Solve.Option(args, "--parallel", Math.Max(1, _threads / 4)));
            _strayRetime = _strayAvoid = Part("stray");
            _tailMinutes = Solve.Option(args, "--tail-minutes", _windowMinutes);
            _combineRepairMinutes = Solve.Option(args, "--combine-repair-minutes", _tailMinutes / 3);
            string focus = Solve.Option(args, "--focus", (string)null);
            if (focus != null)
            {
                string[] ends = focus.Split('-');
                if (ends.Length > 2 || !int.TryParse(ends[0], out _focusFrom) || !int.TryParse(ends[ends.Length - 1], out _focusTo)
                    || _focusFrom < 1 || _focusTo < _focusFrom || _focusFrom > _goals)
                    throw new ArgumentException($"--focus {focus}: a goal range a-b (or one goal a) within 1-{_goals}");
                _focusTo = Math.Min(_focusTo, _goals);
                // A focus is goals picked by number: the goals part, which renumbers them, stays off.
                if (_parts.Remove("goals")) Log("auto: --focus: the goals part is off (it edits the goal list the focus numbers)");
            }
            else _focusTo = _goals;
            string dir = Path.GetDirectoryName(Path.GetFullPath(_trace));
            _autoLog = Path.Combine(dir, "fulltas", _name + ".auto.jsonl");
            _refsFile = Path.Combine(dir, "fulltas", _name + ".auto-refs.json");
            Directory.CreateDirectory(Path.GetDirectoryName(_autoLog));
            // The planner's field cell (plan's --field-cell default): the bounds and the plan lever share it.
            DistanceField.CellSize = 8f;
            _verify = VerifyFresh;
            _hopeless = Hopeless;
            _repairBegin = RepairBegin;
            _collect = (r, how) => Collect(r, $"{how} #{_pullNo}");
            _autoCpu0 = CpuSeconds();
            _autoWall0 = _clock.Elapsed;
            Route start = _best;
            ReadPastSessions();
            AutoWrite(new { type = "autoStart", run = _name, threads = _threads, parallel = _parallel, minutes = _minutes, windowMinutes = _windowMinutes, best = RouteJson(_best),
                parts = _parts.OrderBy(p => Array.IndexOf(AllParts, p)).ToArray(), refs = args.Where((a, i) => i > 0 && args[i - 1] == "--ref").ToArray(),
                focus = new[] { _focusFrom, _focusTo } });
            if (focus != null) Log($"auto: focus goals {_focusFrom}-{_focusTo}: the scheduler and every lever pull windows starting there; combine splices anywhere");
            Log($"auto: parts {(_parts.Count == 0 ? "none" : string.Join(",", AllParts.Where(Part)))}");
            if (Part("lib"))
            {
                _lib = new StateLibrary(_goals, Solve.Option(args, "--library-runs", 150));
                double t0 = CpuSeconds();
                TraceRun(_best.Inputs, "the starting best");
                var (fast, median) = Chaining(_best.Inputs);
                Log($"auto: goal-state library: the starting best traced in {CpuSeconds() - t0:0.0} CPU s ({_lib.Describe()}); chaining: {fast} of {_goals} goal touches at 600+ u/s, median {median:0} u/s");
                _chain0 = (fast, median);
                _chainStart = _best.Inputs;
            }
            Collect(_best, "the starting best");
            if (!_fresh) LoadRefs();
            foreach (var (file, r) in refs) Collect(r, "--ref " + Path.GetFileName(file));
            if (_lib != null && _lib.Runs > 1) TryStitch("the known routes at the start");
            // The speed probes (a minute and a half on FalseEnding's 300 goals) are kept per run and start state: a restart reads
            // them back and only adds the best's own path (docs/fulltas.md, "Auto").
            if (_fresh || !LoadSpeeds()) { MeasureSpeeds(); SaveSpeeds(); }
            else Sustained(Profile(_best.Inputs, false), "the best's path");
            double m0 = CpuSeconds();
            BuildModel();
            PrintModel("start");
            Log($"auto: model built in {CpuSeconds() - m0:0.0} CPU s; the loop starts at {_clock.Elapsed.TotalSeconds:0} s");
            MakeParts();
            RefitParts();
            Outlook();

            _curve.Add((0, 0, _best.Last, "start", 0));
            var probes = new Queue<int>();
            // Measured headroom first: one short window probe per label (the lap phases of a repeated run: every `buy:buyclone2` is
            // one), at its goal with the most expected headroom, most expected first; the bandit then starts from what they gave.
            void Probe()
            {
                probes = new Queue<int>(_model.Where(r => InFocus(r.K)).GroupBy(r => r.Label).Select(g => g.OrderByDescending(r => r.Expected).ThenBy(r => r.K).First())
                    .OrderByDescending(r => r.Expected).Select(r => r.K));
            }
            Probe();
            double probeMinutes = Solve.Option(args, "--probe-minutes", _windowMinutes / 2);
            while (!TimeUp)
            {
                // At the start and after new bests (at most every few minutes): the economy goals' edits (--parts goals). An
                // adopted edit renumbers the goals, so the probes start over.
                if (GoalsDue())
                {
                    if (GoalsPull()) Probe();
                    continue;
                }
                // Every new best (and the start): the known routes' faster goal runs spliced into it (--parts combine).
                if (Part("combine") && !ReferenceEquals(_combinedFor, _best))
                {
                    _combinedFor = _best;
                    CombinePull();
                    continue;
                }
                // Then the known routes that are ahead of it at their last goal and died after it (--parts stray): Revive.
                if (Part("stray") && !ReferenceEquals(_revivedFor, _best))
                {
                    _revivedFor = _best;
                    RevivePull();
                    continue;
                }
                var batch = new List<(Arm arm, double budget, double score)>();
                // The scheduler's wave of window pulls (successive halving over the goal regions) replaces the probes and the
                // bandit while it has pulls to hand out.
                if (_sched != null)
                {
                    var wave = new List<Pull>();
                    int used = 0;
                    for (Pull p; used < _threads && (p = SafePart(() => _sched.Next(), "scheduler Next")) != null;)
                    {
                        wave.Add(p);
                        used += Math.Max(1, p.Threads);
                    }
                    if (wave.Count > 0)
                    {
                        int per = Math.Max(1, _threads / wave.Count);
                        foreach (Pull p in wave)
                        {
                            // The scheduler's regions are of the focus's goals only (MakeParts), numbered from its first goal.
                            int first = Math.Clamp(p.First + _focusFrom - 1, 1, _goals), limit = Math.Clamp(Math.Max(p.Limit, p.First) + _focusFrom - 1, first, _goals);
                            int k = RegionGoal(first, limit);
                            batch.Add((new Arm { Lever = p.Lever ?? "window", Goal = k, Limit = Math.Min(_goals, k + 1), Pull = p }, Math.Max(0.05, p.CpuSeconds / (60.0 * per)), double.NaN));
                        }
                        RunWindows(batch);
                        continue;
                    }
                    if (probes.Count > 0) probes.Clear();
                }
                if (probes.Count > 0)
                    while (probes.Count > 0 && batch.Count < _parallel)
                    {
                        int k = probes.Dequeue();
                        if (!_arms.TryGetValue(("window", k), out Arm arm)) _arms[("window", k)] = arm = new Arm { Lever = "window", Goal = k };
                        batch.Add((arm, probeMinutes, double.NaN));
                    }
                else
                {
                    List<(Arm arm, double score)> ranked = RankArms();
                    if (ranked.Count == 0) { Log("auto: no arm left to pull"); Outlook(exhausted: true); break; }
                    // The best arm alone when it isn't a window (polish, graft, reset study and plan use every thread); else the best
                    // windows whose goals don't overlap, side by side.
                    if (!Windowish(ranked[0].arm.Lever)) batch.Add((ranked[0].arm, Budget(ranked[0].arm), ranked[0].score));
                    else
                        foreach (var (arm, score) in ranked)
                        {
                            if (!Windowish(arm.Lever)) continue;
                            (int a, int b) span = (arm.Goal, WindowLimit(arm));
                            if (batch.Any(x => { int xa = x.arm.Goal, xb = WindowLimit(x.arm); return span.a <= xb && xa <= span.b; })) continue;
                            batch.Add((arm, Budget(arm), score));
                            if (batch.Count == _parallel) break;
                        }
                }
                if (batch.Count == 1 && !Windowish(batch[0].arm.Lever)) RunSingle(batch[0].arm, batch[0].budget, batch[0].score);
                else RunWindows(batch);
                if (_pullNo % 8 < batch.Count) PrintArms();
            }
            BuildModel();
            PrintModel("end");
            PrintArms();
            Log($"auto: {(start.Last * _dt).ToString("0.00", CultureInfo.InvariantCulture)}s ({start.Progress} goals) -> {Describe(_best)} ({_goals} goals) in {(_clock.Elapsed - _autoWall0).TotalMinutes:0.0} min, {CpuSeconds() - _autoCpu0:0} CPU s, {_pullNo} pulls");
            Log("auto: time-to-gain (wall s, CPU s, ticks, time, lever@goal): " + string.Join("; ",
                _curve.Select(c => $"{c.wall:0} s/{c.cpu:0} CPU s -> {c.ticks} ({c.ticks * _dt:0.00} s) {c.lever}@{c.goal}")));
            foreach (var g in _arms.Values.GroupBy(a => a.Lever).OrderByDescending(g => g.Sum(a => a.Gain)))
                Log($"auto: lever {g.Key}: {g.Sum(a => a.Pulls)} pulls, {g.Sum(a => a.Cpu):0} CPU s, {g.Sum(a => a.Gain):0} ticks gained"
                    + (g.Any(a => a.Gain > 0) ? " at goals " + string.Join(",", g.Where(a => a.Gain > 0).Select(a => $"{a.Goal}(-{a.Gain:0})")) : ""));
            Log("auto: pull order: " + string.Join(" ", _order));
            if (_lib != null)
            {
                var (fast, median) = Chaining(_best.Inputs);
                Log($"auto: chaining check: {fast} of {_goals} goal touches at 600+ u/s (median {median:0} u/s) on the final best, against {_chain0.fast} (median {_chain0.median:0} u/s) on the starting best; library {_lib.Describe()}");
                Log($"auto: stitches {_stitchExact} of {_stitchTried} replayed exactly as predicted");
                AutoWrite(new { type = "chaining", start = _chain0.fast, startMedian = Math.Round(_chain0.median), end = fast, endMedian = Math.Round(median), library = _lib.Describe() });
            }
            AutoWrite(new { type = "autoEnd", best = RouteJson(_best), pulls = _pullNo, cpu = Math.Round(CpuSeconds() - _autoCpu0, 1),
                curve = _curve.Select(c => new { wall = Math.Round(c.wall, 1), cpu = Math.Round(c.cpu, 1), ticks = c.ticks, lever = c.lever, goal = c.goal }) });
            SaveRefs();
        }

        const int CombineTries = 16, CombinePerRoute = 4, CombineRepairs = 4, CombineRepairGoals = 2;
        /// <summary>--combine-repair-minutes: one splice-in repair window's budget (default a third of --tail-minutes).</summary>
        double _combineRepairMinutes;
        readonly HashSet<ulong> _combineTried = new HashSet<ulong>();
        Route _combinedFor;

        /// <summary>One combine pass as a pull (bookkept only when it had a splice to try).</summary>
        void CombinePull()
        {
            Route before = _best;
            double c0 = CpuSeconds();
            TimeSpan w0 = _clock.Elapsed;
            (string outcome, int goal) = Combine();
            if (goal == 0) { Log("auto: " + outcome); return; }
            _pullNo++;
            if (!_arms.TryGetValue(("combine", goal), out Arm arm)) _arms[("combine", goal)] = arm = new Arm { Lever = "combine", Goal = goal };
            Account(arm, _pullNo, before, CpuSeconds() - c0, (_clock.Elapsed - w0).TotalSeconds, outcome, 0, double.NaN);
        }

        /// <summary>
        /// Combine: every known route (the references: window finds spliced to the end, repairs' partial runs, old bests, --ref
        /// routes) that gains on the best over a run of goals [a, b] is spliced in: the best's inputs to goal a-1, that route's from
        /// its goal a-1 to its goal b, the best's from its goal b on. The runs are where its lead over the best (the best's arrival
        /// minus its own) climbs from a low at goal a-1 to a high at goal b, at least 2 ticks; each route's CombinePerRoute largest,
        /// so a gain that needs the route's own re-synced goals after it (a find at 186 that loses 18 ticks again by 194 and makes
        /// them back by 264) is tried whole as well as alone. It joins gains two searches found at different goals (380.62 s at
        /// goals 275-279 and 380.52 s at 295-300 made 380.00 s).
        /// A stretch is spliced only where the route and the best agree at its start: the same full-state digest (StateKey and
        /// WorldDigest, the economy included) at the splice-in point, so the route's inputs play there as they did in the route;
        /// then the splice is replayed from the start and a missed goal gets the wait fix against the route-with-the-best's-tail.
        /// Where they differ (a route that branched off an older best, which changed the goals before), the goals a..a+1 after the
        /// splice-in are re-searched as a short tail window from the best's state (seeded with the route's own inputs, followed by
        /// the route's stretch and the best's tail: TailWindows), at most CombineRepairs a pass. The first whole run faster than
        /// the best is kept. At most CombineTries new splices a pass, largest gain first. Returns the outcome and the first goal of
        /// the first splice tried (0: none).
        /// </summary>
        (string outcome, int goal) Combine()
        {
            Route best = _best;
            var cands = new List<(long save, int a, int b, AutoRef r)>();
            foreach (AutoRef r in _refs)
            {
                int n = Math.Min(r.Progress, best.Progress);
                if (n == 0) continue;
                var lead = new long[n + 1];
                for (int g = 1; g <= n; g++) lead[g] = best.GoalPhysics[g - 1] - r.GoalPhysics[g - 1];
                bool Low(int i) => (i == 0 || lead[i] <= lead[i - 1]) && (i == n || lead[i] < lead[i + 1]);
                bool High(int j) => j > 0 && lead[j] > lead[j - 1] && (j == n || lead[j] >= lead[j + 1]);
                List<int> lows = Enumerable.Range(0, n + 1).Where(Low).ToList(), highs = Enumerable.Range(1, n).Where(High).ToList();
                cands.AddRange(lows.SelectMany(i => highs.Where(j => j > i && lead[j] - lead[i] >= 2).Select(j => (lead[j] - lead[i], i + 1, j, r)))
                    .OrderByDescending(c => c.Item1).Take(CombinePerRoute));
            }
            // The untried splices, largest gain first, with their input lists.
            var todo = new List<(long save, int a, int b, AutoRef r, int from, int rFrom, int rTo, int to, List<InputTick> inputs, ulong hash)>();
            foreach (var (save, a, b, r) in cands.OrderByDescending(c => c.save))
            {
                if (todo.Count >= 2 * CombineTries) break;
                int from = a == 1 ? 0 : (int)Math.Min(best.GoalInput[a - 2], best.Inputs.Count);
                int rFrom = a == 1 ? 0 : (int)Math.Min(r.GoalInput[a - 2], r.Inputs.Count);
                int rTo = (int)Math.Min(r.GoalInput[b - 1], r.Inputs.Count), to = (int)Math.Min(best.GoalInput[b - 1], best.Inputs.Count);
                if (rTo <= rFrom) continue;
                List<InputTick> inputs = best.Inputs.Take(from).Concat(r.Inputs.Skip(rFrom).Take(rTo - rFrom)).Concat(best.Inputs.Skip(to)).ToList();
                ulong hash = SnapshotIndex.Hash(inputs, inputs.Count);
                if (_combineTried.Contains(hash) || todo.Any(t => t.hash == hash)) continue;
                todo.Add((save, a, b, r, from, rFrom, rTo, to, inputs, hash));
            }
            if (todo.Count == 0) return ("combine: no new splice of a known route is faster than the best over any goals", 0);
            // The full-state digests at the splice-in points: one replay of the best, one of each route.
            Dictionary<int, ulong> bestAt = DigestsAt(best.Inputs, todo.Select(t => t.from).ToList());
            var refAt = todo.GroupBy(t => t.r).ToDictionary(g => g.Key, g => DigestsAt(g.Key.Inputs, g.Select(t => t.rFrom).ToList()));
            int plays = 0, repairs = 0, exact = 0, skipped = 0, firstGoal = 0;
            foreach (var (save, a, b, r, from, rFrom, rTo, to, inputs, hash) in todo)
            {
                if (plays >= CombineTries || TimeUp) break;
                bool same = bestAt.TryGetValue(from, out ulong d0) && refAt[r].TryGetValue(rFrom, out ulong d1) && d0 == d1;
                if (!same && (repairs >= CombineRepairs || !Part("tail"))) { skipped++; continue; }
                _combineTried.Add(hash);
                plays++;
                if (firstGoal == 0) firstGoal = a;
                string how = $"combine: goals {a}-{b} from {r.How} ({save} ticks there)";
                // The route to goal b, then the best's tail: what the splice follows after its start.
                Route follow = Composite(r, best, b, rTo, to);
                Route c;
                if (same)
                {
                    exact++;
                    c = TraceRun(inputs, how);
                    if (c.Progress < _goals) c = WaitFix(c, follow, from, how, from) ?? c;
                }
                else
                {
                    repairs++;
                    // The splice-in state differs: the goals after it re-found from the best's state at goal a-1.
                    var broken = new Route { Inputs = inputs, Progress = a - 1, GoalInput = best.GoalInput, GoalPhysics = best.GoalPhysics,
                        EndReason = $"the best's state at goal {a - 1} isn't the route's" };
                    c = TailWindows(broken, best, how + ", splice-in repaired", follow, CombineRepairGoals, _combineRepairMinutes, 2) ?? broken;
                }
                if (c.Progress == _goals && c.Last < _best.Last)
                {
                    Commit(c, how);
                    if (_best.Last < best.Last) return ($"{how}{(same ? "" : ", splice-in repaired")}: {Describe(_best)}", a);
                }
            }
            string counts = $"{exact} with the same state at the splice-in, {repairs} repaired there, {skipped} over the repair budget";
            return plays == 0 ? ($"combine: no splice to try this pass ({counts})", 0) : ($"combine: {plays} splices ({counts}), none faster as a whole run", firstGoal);
        }

        /// <summary>A known route to its goal b (input rTo), then the best's inputs from its goal b (input to): its goal ticks the
        /// route's to goal b and the best's, moved, after.</summary>
        Route Composite(AutoRef r, Route best, int b, int rTo, int to)
        {
            var inputs = r.Inputs.Take(rTo).Concat(best.Inputs.Skip(to)).ToList();
            long dt = r.GoalPhysics[b - 1] - best.GoalPhysics[b - 1], di = rTo - to;
            var gi = new long[_goals];
            var gp = new long[_goals];
            for (int k = 0; k < _goals; k++)
            {
                gi[k] = k < b ? r.GoalInput[k] : best.GoalInput[k] + di;
                gp[k] = k < b ? r.GoalPhysics[k] : best.GoalPhysics[k] + dt;
            }
            return new Route { Inputs = inputs, Progress = _goals, GoalInput = gi, GoalPhysics = gp };
        }

        /// <summary>The full-state digest (StateKey mixed with WorldDigest: the player, the economy, the goals; not the clock) after
        /// each of these input counts of a route, from one replay on the loop's simulation; a count past the route's end is left out.</summary>
        Dictionary<int, ulong> DigestsAt(List<InputTick> inputs, List<int> counts)
        {
            var want = new HashSet<int>(counts.Where(n => n <= inputs.Count));
            var got = new Dictionary<int, ulong>();
            Simulation sim = _eval;
            sim.SetStart(_origin, 0);
            sim.Goals.Limit = _goals;
            if (want.Remove(0)) { sim.Restore(_origin); got[0] = Mix(StateKey(sim), (ulong)(uint)WorldDigest(sim)); }
            if (want.Count == 0) return got;
            int max = want.Max();
            var run = inputs.GetRange(0, max).Concat(new[] { inputs[max - 1] }).ToList();
            sim.Run(run, null, tick =>
            {
                if (want.Contains(tick)) got[tick] = Mix(StateKey(sim), (ulong)(uint)WorldDigest(sim));
                return tick < max;
            }, 1000000);
            sim.SetStart(_origin, 0);
            return got;
        }

        /// <summary>A partial known route to its last goal p, then the best's inputs after the best's goal p: with the first input
        /// that differs from the best and the input the best's tail starts at.</summary>
        (List<InputTick> inputs, int changedStart, int changedEnd) WithBestTail(AutoRef r, Route best)
        {
            int p = r.Progress;
            var inputs = r.Inputs.Take((int)Math.Min(r.GoalInput[p - 1], r.Inputs.Count)).ToList();
            int changedEnd = inputs.Count, changedStart = 0;
            while (changedStart < changedEnd && changedStart < best.Inputs.Count && inputs[changedStart].Equals(best.Inputs[changedStart])) changedStart++;
            inputs.AddRange(best.Inputs.Skip(PrefixAt(best, p + 1)));
            return (inputs, changedStart, changedEnd);
        }

        /// <summary>At most this many known routes a revive pass tries, and how often one route is tried (each on a new best).</summary>
        const int RevivePerPass = 3, ReviveAttempts = 2;
        readonly Dictionary<ulong, int> _reviveTried = new Dictionary<ulong, int>();
        Route _revivedFor;

        /// <summary>One revive pass as a pull (bookkept only when it tried a route).</summary>
        void RevivePull()
        {
            Route before = _best;
            double c0 = CpuSeconds();
            TimeSpan w0 = _clock.Elapsed;
            (string outcome, int goal) = Revive();
            if (goal == 0) { Log("auto: " + outcome); return; }
            _pullNo++;
            if (!_arms.TryGetValue(("revive", goal), out Arm arm)) _arms[("revive", goal)] = arm = new Arm { Lever = "revive", Goal = goal };
            Account(arm, _pullNo, before, CpuSeconds() - c0, (_clock.Elapsed - w0).TotalSeconds, outcome, 0, double.NaN);
        }

        /// <summary>
        /// --parts stray, at the start and after every new best: the known routes that are ahead of the best at their last goal p
        /// (window finds and repairs whose tails died, from this run or an earlier one) spliced with the best's tail after goal p and
        /// rescued as a fresh find's merge would be: the wait fix with the whole-stretch re-timing, then tail windows that avoid the
        /// stray box. Largest lead first, RevivePerPass a pass, each route at most ReviveAttempts times. The stray study measured it
        /// (docs/fulltas.md). Returns the outcome and the first route's last goal (0: none tried).
        /// </summary>
        (string outcome, int goal) Revive()
        {
            Route best = _best;
            int tried = 0, first = 0;
            foreach (AutoRef r in _refs.Where(r => r.Progress > 0 && r.Progress < _goals && best.GoalPhysics[r.Progress - 1] > r.GoalPhysics[r.Progress - 1])
                .OrderByDescending(r => best.GoalPhysics[r.Progress - 1] - r.GoalPhysics[r.Progress - 1]).ToList())
            {
                if (tried >= RevivePerPass || TimeUp) break;
                int n = _reviveTried.TryGetValue(r.Key, out int was) ? was : 0;
                if (n >= ReviveAttempts) continue;
                _reviveTried[r.Key] = n + 1;
                tried++;
                int p = r.Progress;
                if (first == 0) first = p;
                var (inputs, changedStart, changedEnd) = WithBestTail(r, best);
                string how = $"revive: {r.How} (goal {p}, {best.GoalPhysics[p - 1] - r.GoalPhysics[p - 1]} ticks ahead)";
                Route broken = TraceRun(inputs, how), c = broken;
                if (c.Progress < _goals) c = WaitFix(broken, best, changedEnd, how, changedStart) ?? broken;
                if ((c.Progress < _goals || c.Last >= _best.Last) && broken.Progress < _goals && Part("tail")) c = TailWindows(broken, best, how) ?? c;
                if (c.Progress == _goals && c.Last < _best.Last)
                {
                    Commit(c, how);
                    if (_best.Last < best.Last) return ($"{how}: {Describe(_best)}", p);
                }
            }
            return tried == 0 ? ("revive: no known route ahead of the best is left to try", 0) : ($"revive: {tried} known routes ahead of the best, none rescued faster", first);
        }

        readonly Dictionary<int, int> _regionPulls = new Dictionary<int, int>();

        /// <summary>
        /// The goal a scheduler pull over a label region [first, limit] searches (a window of it and the goal after): the region's
        /// goals that carry its label, most expected first, the next one each time the region is pulled, then round again.
        /// </summary>
        int RegionGoal(int first, int limit)
        {
            string label = _labels[first - 1];
            List<int> ks = Enumerable.Range(first, limit - first + 1).Where(k => _labels[k - 1] == label)
                .OrderByDescending(k => _model[k - 1].Expected).ThenBy(k => k).ToList();
            int n = _regionPulls.TryGetValue(first, out int was) ? was : 0;
            _regionPulls[first] = n + 1;
            return ks[n % ks.Count];
        }

        int _parallel;
        (int fast, float median) _chain0;
        List<InputTick> _chainStart;
        double _repairCpuPerTick, _repairMax, _autoCpu0;
        TimeSpan _autoWall0;
        readonly List<(double wall, double cpu, long ticks, string lever, int goal)> _curve = new List<(double, double, long, string, int)>();
        readonly List<string> _order = new List<string>();

        static bool Windowish(string lever) { return lever == "window" || lever == "lookahead" || lever == "span"; }

        int WindowLimit(Arm arm)
        {
            if (arm.Limit > 0) return arm.Limit;
            return arm.Lever == "lookahead" ? Math.Min(_goals, arm.Goal + 2) : arm.Lever == "span" ? SpanEnd(arm.Goal) : Math.Min(_goals, arm.Goal + 1);
        }

        void StartPull(Arm arm, double budget, double score)
        {
            _pullNo++;
            Log($"auto: pull {_pullNo}: {arm.Lever} at goal {arm.Goal} ({_labels[arm.Goal - 1]}), {(double.IsNaN(score) ? "first-round probe" : $"score {score:0.0000} ticks/CPU s")}, "
                + $"expected {_model[arm.Goal - 1].Expected:0.0} ticks, arm {arm.Pulls} pulls {arm.Cpu:0} CPU s {arm.Gain:0} gained, budget {budget:0.00} min");
            // The studio's "searching now" line (stdout only).
            var spot = Spot(arm.Lever, arm.Goal);
            Emit(new { type = "pullStart", pull = _pullNo, lever = arm.Lever, goal = arm.Goal, label = _labels[arm.Goal - 1], budget = Math.Round(budget, 2),
                expected = Math.Round(_model[arm.Goal - 1].Expected, 1), pulls = spot.pulls, cpu = Math.Round(spot.cpu), gained = spot.gain });
        }

        /// <summary>One arm on every thread (a polish, graft, reset study or plan pull).</summary>
        void RunSingle(Arm arm, double budget, double score)
        {
            StartPull(arm, budget, score);
            int pull = _pullNo;
            Route before = _best;
            double c0 = CpuSeconds();
            TimeSpan w0 = _clock.Elapsed;
            string outcome;
            try { outcome = Pull(arm, budget); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is NullReferenceException || ex is IndexOutOfRangeException)
            {
                outcome = "failed: " + ex.GetBaseException().Message;
            }
            Account(arm, pull, before, CpuSeconds() - c0, (_clock.Elapsed - w0).TotalSeconds, outcome, budget, score);
        }

        /// <summary>
        /// Window arms side by side from the same best, each an OfflineSolver on its share of the threads (no archive: a short search
        /// from the goal state), then merged one at a time, the latest window first so each later gain lands on an unchanged prefix;
        /// an earlier window's merge replays the tail with those gains in it, and re-finds it (Repair) where it no longer follows.
        /// </summary>
        void RunWindows(List<(Arm arm, double budget, double score)> batch)
        {
            Route best = _best;
            int per = Math.Max(1, _threads / batch.Count);
            double c0 = CpuSeconds();
            TimeSpan w0 = _clock.Elapsed;
            var jobs = new List<(Arm arm, int pull, double budget, double score, int k, int limit, int prefixLength, OfflineSolver solver, Thread[] threads, List<List<InputTick>> found, long[] bestAt)>();
            foreach (var (arm, budget, score) in batch)
            {
                StartPull(arm, budget, score);
                int k = arm.Goal, limit = WindowLimit(arm), prefixLength = PrefixAt(best, k);
                List<InputTick> prefix = best.Inputs.GetRange(0, prefixLength);
                long baseline = best.GoalPhysics[limit - 1];
                Func<Simulation> create = () =>
                {
                    Simulation s = _create();
                    s.Goals.Limit = limit;
                    if (prefix.Count == 0) return s;
                    SimSnapshot at = s.Advance(prefix) ?? throw new InvalidOperationException("the prefix ended before its last input");
                    s.SetStart(at, prefix.Count);
                    return s;
                };
                var labels = _labels.Skip(k - 1).Take(limit - k + 1).ToList();
                var windowSeeds = new List<List<InputTick>> { best.Inputs.Skip(prefixLength).ToList() };
                windowSeeds.AddRange(MacroSeeds(best, k, prefixLength));
                var solver = new OfflineSolver(create, WindowCaps(_runGoals.Goals.Skip(k - 1).Take(limit - k + 1)), windowSeeds,
                    null, Solve.FullTasPaths(_name, labels, _labels), VisitCell);
                var found = new List<List<InputTick>>();
                long[] bestAt = { long.MaxValue };
                solver.Improved += (time, runTicks, inputs, how) =>
                {
                    long atGoal = runTicks - Simulation.StrictFinishTicks;
                    lock (found)
                    {
                        bestAt[0] = Math.Min(bestAt[0], atGoal);
                        if (atGoal < baseline) found.Add(inputs);
                    }
                };
                jobs.Add((arm, _pullNo, budget, score, k, limit, prefixLength, solver, solver.Start(per), found, bestAt));
            }
            var stopped = new bool[jobs.Count];
            while (stopped.Any(x => !x))
            {
                Thread.Sleep(250);
                for (int i = 0; i < jobs.Count; i++)
                    if (!stopped[i] && (TimeUp || (_clock.Elapsed - w0).TotalMinutes >= jobs[i].budget)) { jobs[i].solver.Stop(); stopped[i] = true; }
            }
            foreach (var j in jobs) foreach (Thread t in j.threads) t.Join(60000);
            double search = CpuSeconds() - c0, wall = (_clock.Elapsed - w0).TotalSeconds;
            Log($"auto: {jobs.Count} windows side by side on {per} threads each: {search:0} CPU s, {wall:0} s; "
                + string.Join(", ", jobs.Select(j => $"goals {j.k}-{j.limit} {(j.found.Count > 0 ? $"{j.found.Count} faster (goal {j.limit} at {j.bestAt[0]} against {best.GoalPhysics[j.limit - 1]})" : "nothing faster")}")));
            foreach (var j in jobs.OrderByDescending(j => j.k))
            {
                _pullNo = j.pull;
                Route before = _best;
                double m0 = CpuSeconds();
                TimeSpan mw = _clock.Elapsed;
                string outcome = $"goals {j.k}-{j.limit}: nothing faster";
                List<List<InputTick>> found;
                lock (j.found) found = j.found.ToList();
                if (found.Count > 0)
                {
                    string how = $"{j.arm.Lever} {j.k}-{j.limit} #{j.pull}";
                    List<InputTick> prefix = best.Inputs.Take(j.prefixLength).ToList();
                    foreach (List<InputTick> f in Enumerable.Reverse(found).Take(5)) Collect(Evaluate(prefix.Concat(f).ToList()), how);
                    // The merge re-finds the goals after goal limit from these finds itself: a graft bridge from there would repeat it.
                    _grafted.Add((how, j.limit));
                    _findsHopeless = false;
                    try { MergeAuto($"faster through goals {j.k}-{j.limit} ({j.arm.Lever}, auto)", _best, PrefixAt(_best, j.k), j.limit, found); }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is NullReferenceException || ex is IndexOutOfRangeException)
                    {
                        Log($"auto: merge of goals {j.k}-{j.limit} failed: {ex.GetBaseException().Message}");
                    }
                    outcome = $"goals {j.k}-{j.limit}: {found.Count} faster through goal {j.limit} (at {j.bestAt[0]} against {best.GoalPhysics[j.limit - 1]})";
                }
                // A window's own lead feeds the scheduler only while its finds can still win: one whose every find the cost-to-go
                // dropped arrived in states the later goals can't use (VMAN: 35 such windows, none converted).
                j.arm.Lead = !_findsHopeless && j.bestAt[0] < best.GoalPhysics[j.limit - 1] ? best.GoalPhysics[j.limit - 1] - j.bestAt[0] : 0;
                Account(j.arm, j.pull, before, search / jobs.Count + CpuSeconds() - m0, wall + (_clock.Elapsed - mw).TotalSeconds, outcome, j.budget, j.score);
            }
        }

        /// <summary>A pull's bookkeeping: the arm's and the lever's CPU and gain, the log, the jsonl, and the model after a gain.</summary>
        void Account(Arm arm, int pull, Route before, double cpu, double wall, string outcome, double budget, double score)
        {
            long gain = Math.Max(0, before.Last - _best.Last);
            arm.Pulls++;
            arm.Cpu += cpu;
            arm.Gain += gain;
            if (gain > 0) arm.Wins++;
            CountSpot(arm.Lever, arm.Goal, cpu, gain);
            var lc = _leverCost.TryGetValue(arm.Lever, out var was) ? was : (0, 0.0);
            _leverCost[arm.Lever] = (lc.Item1 + 1, lc.Item2 + cpu);
            if (arm.Pull != null) SafePart(() => { _sched.Report(arm.Pull, gain, cpu, gain > 0 ? 0 : arm.Lead); return 0; }, "scheduler Report");
            _order.Add($"{arm.Lever}@{arm.Goal}" + (gain > 0 ? $"(-{gain})" : ""));
            Log($"auto: pull {pull} {arm.Lever} goal {arm.Goal}: {outcome}; {(gain > 0 ? $"GAINED {gain} ticks" : "no gain")} in {cpu:0} CPU s, {wall:0} s; best {Describe(_best)}");
            AutoWrite(new { type = "pull", pull, lever = arm.Lever, goal = arm.Goal, label = _labels[arm.Goal - 1], budget, outcome, gain, cpu = Math.Round(cpu, 1),
                wall = Math.Round((_clock.Elapsed - _autoWall0).TotalSeconds, 1), best = Seconds(_best.Last), score });
            if (gain > 0)
            {
                _curve.Add(((_clock.Elapsed - _autoWall0).TotalSeconds, CpuSeconds() - _autoCpu0, _best.Last, arm.Lever, arm.Goal));
                var changed = Enumerable.Range(1, _goals).Where(k => Split(before.GoalPhysics, k) != Split(_best.GoalPhysics, k))
                    .Select(k => $"{k}:{Split(before.GoalPhysics, k)}->{Split(_best.GoalPhysics, k)}");
                Log($"auto: record {Describe(before)} -> {Describe(_best)} at {(_clock.Elapsed - _autoWall0).TotalSeconds:0} s by {arm.Lever} at goal {arm.Goal} (-{gain} ticks); splits changed: {string.Join(" ", changed)}");
                AutoWrite(new { type = "record", lever = arm.Lever, goal = arm.Goal, gain, time = Seconds(_best.Last), ticks = _best.Last,
                    wall = Math.Round((_clock.Elapsed - _autoWall0).TotalSeconds, 1), cpu = Math.Round(CpuSeconds() - _autoCpu0, 1),
                    splits = Enumerable.Range(1, _goals).Select(k => Split(_best.GoalPhysics, k)).ToArray() });
            }
            if (gain > 0 || _refsChanged)
            {
                if (gain > 0) { BuildModel(); if (_lib != null) { TraceRun(_best.Inputs, $"new best by {arm.Lever}@{arm.Goal}"); RefitParts(); } } else Refresh();
                if (_refsChanged) SaveRefs();
                _refsChanged = false;
            }
            Outlook();
        }

        // ---------------------------------------------------------------- the bandit

        double LeverCost(string lever)
        {
            // The budget's CPU seconds (polish and graft have none: a guess) as one pull's worth, then the mean with every pull made: a
            // pull that ends early (a plan with no finish) doesn't make the lever look free.
            double wm = _windowMinutes * 60 * _threads;
            double guess = lever switch { "polish" => 20.0 * _threads, "graft" => 40.0 * _threads, "plan" => 1.5 * wm, "lookahead" => 1.2 * wm, "span" => 2 * wm, _ => wm };
            var c = _leverCost.TryGetValue(lever, out var known) ? known : (0, 0.0);
            return Math.Max(1, (guess + c.Item2) / (1 + c.Item1));
        }

        double Prior(string lever, GoalRow row)
        {
            if (lever == "graft") return LeverPrior[lever] * Math.Max(row.ArrivalLead, row.SplitLead);
            return LeverPrior[lever] * row.Expected;
        }

        /// <summary>Every eligible arm by expected ticks per CPU second, (prior + gained) / (a pull's cost + CPU spent), best first.</summary>
        List<(Arm arm, double score)> RankArms()
        {
            var ranked = new List<(Arm, double)>();
            foreach (GoalRow row in _model.Where(r => InFocus(r.K)))
                foreach (string lever in AutoLevers)
                {
                    if (!Eligible(lever, row.K)) continue;
                    if (!_arms.TryGetValue((lever, row.K), out Arm arm)) _arms[(lever, row.K)] = arm = new Arm { Lever = lever, Goal = row.K };
                    double score = (Prior(lever, row) + arm.Gain) / (LeverCost(lever) + arm.Cpu);
                    if (score > 0) ranked.Add((arm, score));
                }
            return ranked.OrderByDescending(r => r.Item2).ToList();
        }

        bool Eligible(string lever, int k)
        {
            switch (lever)
            {
                case "lookahead": return k + 2 <= _goals;
                case "span": return SpanEnd(k) > 0;
                case "dropreset": return NextReset(k) >= 0;
                case "graft": return GraftCandidate(k) != null;
                case "plan": return !_planDead.Contains(k);
                case "polish": return !Polished(k);
                default: return true;
            }
        }

        double Budget(Arm arm)
        {
            // A window's archive resumes it, so a pulled-again arm searches on for longer.
            double m = _windowMinutes * Math.Min(4, 1 + arm.Pulls);
            // The planner needs minutes to its first finish (Planner3: about 1 min on goals 6-7, 8 threads).
            return arm.Lever == "plan" ? Math.Max(2.0, 1.5 * m) : m;
        }

        string Pull(Arm arm, double budget)
        {
            int k = arm.Goal;
            switch (arm.Lever)
            {
                case "polish": return PullPolish(k);
                case "window": return PullWindow(k, Math.Min(_goals, k + 1), budget);
                case "lookahead": return PullWindow(k, Math.Min(_goals, k + 2), budget);
                case "span": return PullWindow(k, SpanEnd(k), 2 * budget);
                case "dropreset": return PullDropReset(k);
                case "graft": return PullGraft(k);
                case "plan": return PullPlan(k, budget);
            }
            return "unknown lever";
        }

        // A goal whose target is this small (an area goal's rect, units squared) or a gate or box is where a long window is scored.
        const float SmallGoal = 300f * 300f;

        /// <summary>
        /// The goal a long window from goal k is scored at: the first small goal 3-5 goals on (a buy, a gate, or an area goal's rect of at
        /// most 300x300), else none. Scored at a large area box, a window wins by touching its near edge in a state the next goals can't
        /// use, and the repair after it loses the gain (Vman4 on VMAN goals 10-14 and 15-18); scored at a small goal further on, the gain
        /// is one the goals after it keep.
        /// </summary>
        int SpanEnd(int k)
        {
            for (int j = k + 2; j <= Math.Min(_goals, k + 4); j++)
            {
                FullTasGoal g = _runGoals.Goals[j - 1];
                if (g.kind != FullTasGoal.At || g.w * g.h <= SmallGoal) return j;
            }
            return 0;
        }

        int PrefixAt(Route r, int k) { return k <= 1 ? 0 : (int)Math.Min(r.GoalInput[k - 2], r.Inputs.Count); }

        // ---------------------------------------------------------------- levers

        /// <summary>Polish (Polish.cs) over goal k's inputs only: re-timings, stick values, jump frames, dropped or moved resets. Its
        /// sweeps are deterministic, so a goal polished with nothing kept isn't polished again until the best changes (VMAN: 444
        /// repeats, 3,400 CPU s, no gain). A course run's avoid boxes are its category's rules, so its polish keeps out of them.</summary>
        string PullPolish(int k)
        {
            Route best = _best;
            int from = PrefixAt(best, k), to = (int)Math.Min(best.GoalInput[k - 1], best.Inputs.Count);
            OfflineSolver.ForcedPath avoid = Solve.CourseRuns.ContainsKey(_name) ? Solve.FullTasPaths(_name) : new OfflineSolver.ForcedPath();
            var polish = new Polish(() => { Simulation s = _create(); s.Goals.Limit = _goals; return s; }, avoid, _threads, line => Log(line));
            try
            {
                Polish.Result p = polish.Run(best.Inputs, maxSweeps: 3, from: from, to: to);
                if (_best == best && !Polished(k)) _polished.Add(k);
                if (p.Kept == 0) return $"polish inputs {from}-{to}: nothing kept ({p.Ticks} ticks)";
                Route r = Evaluate(p.Inputs);
                Collect(r, $"polish of goal {k} #{_pullNo}");
                if (Better(r, _best)) Commit(r, $"polish of goal {k}'s inputs ({p.Kept} changes)");
                return $"polish inputs {from}-{to}: {p.Kept} kept, {Describe(r)} ({p.Ticks} ticks)";
            }
            finally { polish.Stop(); }
        }

        Route _polishedBest;
        readonly HashSet<int> _polished = new HashSet<int>();

        /// <summary>Whether goal k was polished on the current best (the set empties when the best changes).</summary>
        bool Polished(int k)
        {
            if (_polishedBest != _best) { _polishedBest = _best; _polished.Clear(); }
            return _polished.Contains(k);
        }

        /// <summary>A window search from goal k-1's state through goal limit (limit k+2: the goal after next, so a way that reaches goal
        /// k later with more speed can win), then the solve's merge: splice, and re-find the later goals when the old tail breaks.</summary>
        string PullWindow(int k, int limit, double budget)
        {
            Route best = _best;
            int prefixLength = PrefixAt(best, k);
            var seeds = new List<List<InputTick>> { best.Inputs.Skip(prefixLength).ToList() };
            List<List<InputTick>> found = SearchWindow("polish", best, prefixLength, limit, seeds, best.GoalPhysics[limit - 1], budget, 0);
            if (found.Count == 0) return $"goals {k}-{limit}: nothing faster";
            List<InputTick> prefix = best.Inputs.Take(prefixLength).ToList();
            string how = $"window {k}-{limit} #{_pullNo}";
            foreach (List<InputTick> f in Enumerable.Reverse(found).Take(5)) Collect(Evaluate(prefix.Concat(f).ToList()), how);
            // The merge re-finds the goals after goal limit from these finds itself: a graft bridge from there would repeat it.
            _grafted.Add((how, limit));
            MergeAuto($"faster through goals {k}-{limit} (auto)", best, prefixLength, limit, found);
            return $"goals {k}-{limit}: {found.Count} faster through goal {limit}";
        }

        /// <summary>The first quick restart in goal k's inputs not studied yet (-1: none).</summary>
        int NextReset(int k)
        {
            Route best = _best;
            int lo = PrefixAt(best, k), hi = (int)Math.Min(best.GoalInput[k - 1], best.Inputs.Count);
            for (int i = Math.Max(1, lo); i < hi; i++)
                if (best.Inputs[i].reset && !_studied.Contains((k, i - lo))) return i;
            return -1;
        }

        string PullDropReset(int k)
        {
            int q = NextReset(k);
            _studied.Add((k, q - PrefixAt(_best, k)));
            return StudyReset(_best, q, GoalsBy(_best, q));
        }

        AutoRef GraftCandidate(int k)
        {
            Route best = _best;
            long arrival = best.GoalPhysics[k - 1], split = Split(best.GoalPhysics, k);
            return _refs.Where(r => r.Progress >= k && !_grafted.Contains((r.How, k)) && (r.GoalPhysics[k - 1] < arrival || Split(r.GoalPhysics, k) < split))
                .OrderBy(r => Math.Min(r.GoalPhysics[k - 1] - arrival, Split(r.GoalPhysics, k) - split)).FirstOrDefault();
        }

        /// <summary>
        /// A known route that does goal k better: reached sooner, its way there kept and the later goals re-found (Repair, as the
        /// transplant's bridges); split shorter, its inputs picked up from the best's states nearest its path by place and speed
        /// (the transplant's splices, FullTasTransplant.cs), not by tick.
        /// </summary>
        string PullGraft(int k)
        {
            AutoRef r = GraftCandidate(k);
            _grafted.Add((r.How, k));
            Route best = _best;
            var said = new List<string>();
            if (r.GoalPhysics[k - 1] < best.GoalPhysics[k - 1])
            {
                Route run = Evaluate(r.Inputs.Take((int)Math.Min(r.GoalInput[k - 1], r.Inputs.Count)).ToList(), k);
                if (run.Progress >= k && run.GoalPhysics[k - 1] < best.GoalPhysics[k - 1])
                {
                    Log($"auto graft: bridge from {r.How} at goal {k}: tick {run.GoalPhysics[k - 1]} against the best's {best.GoalPhysics[k - 1]}; re-finding goals {k + 1}-{_goals}");
                    Repair($"the way to goal {k} of {r.How} (auto bridge)", best, new Route { Inputs = run.Inputs, Progress = k, GoalInput = run.GoalInput, GoalPhysics = run.GoalPhysics, EndReason = run.EndReason });
                    said.Add($"bridge from {r.How}");
                }
            }
            if (_best == best && Split(r.GoalPhysics, k) < Split(best.GoalPhysics, k))
            {
                Reference saved = _reference;
                _reference = ReferenceOf(r.Inputs);
                _leads.Clear();
                try
                {
                    using var lanes = new Lanes(Math.Max(1, _threads), _create, _goals);
                    long gain = Splices(lanes, r.Inputs, k, false);
                    if (gain > 0) Splices(lanes, r.Inputs, k, true);
                    said.Add($"reanchor of {r.How} ({(gain > 0 ? gain + " ticks sooner at goal " + k : "none sooner")})");
                }
                finally { _reference = saved; }
                foreach (var (p, lead) in _leads) Collect(Evaluate(lead.inputs), $"graft lead at goal {p} #{_pullNo}");
                _leads.Clear();
            }
            return said.Count == 0 ? $"{r.How}: no longer better at goal {k}" : string.Join("; ", said);
        }

        /// <summary>The contact planner (Plan.cs, Planner.cs) on goal k's window, the best's window its guide; its finishes go to
        /// the merge (the old tail re-found where it breaks). A goal whose plan finds no finish is dropped for the plan lever.</summary>
        string PullPlan(int k, double minutes)
        {
            Route best = _best;
            global::IGTAP.EngineSim.Cli.Plan.Window w = global::IGTAP.EngineSim.Cli.Plan.Window.Build(_trace, AutoHeader(), best.Inputs, k - 1, k, partial: false);
            var options = new Planner.Options
            {
                Threads = _threads, Width = Math.Max(48, 2 * _threads), MaxWidth = 768, Cell = 16f, Cap = 8, Labels = 2,
                Seconds = minutes * 60, MomentumSlack = 30, FromGoal = k - 1,
            };
            options.Guides.Add(w.OldWindow);
            var finishes = new List<Planner.Route>();
            var planner = new Planner(w.Create, w.Caps, w.Path, null, options);
            planner.Found += f => { lock (finishes) finishes.Add(f); };
            planner.Run(line => Log("plan: " + line));
            if (finishes.Count == 0)
            {
                _planDead.Add(k);
                return $"plan goal {k}: no finish in {minutes:0.0} min ({planner.Ticks} ticks); the plan lever stops for this goal";
            }
            int prefixLength = PrefixAt(best, k);
            // Merge tries the last first: by arrival less the speed carried (plan's --momentum-worth, 60 u/s per tick).
            List<List<InputTick>> found = finishes.OrderByDescending(f => f.RunTicks - f.Speed / 60f).TakeLast(8).Select(f => f.Inputs).ToList();
            List<InputTick> prefix = best.Inputs.Take(prefixLength).ToList();
            string how = $"plan of goal {k} #{_pullNo}";
            foreach (List<InputTick> f in found) Collect(Evaluate(prefix.Concat(f).ToList()), how);
            _grafted.Add((how, k));
            MergeAuto($"plan of goal {k} (auto)", best, prefixLength, k, found);
            return $"plan goal {k}: {finishes.Count} finishes, fastest {finishes.Min(f => f.RunTicks) - w.StartPhysics} ticks against the best's {w.OldWindowPhysics}";
        }

        // ---------------------------------------------------------------- commit check, repair bound, references

        /// <summary>The route replayed from the start on a new simulation (its own thread and world, as a fresh process would):
        /// kept as that replay has it when it still beats the best; null otherwise.</summary>
        Route VerifyFresh(Route r, string where)
        {
            Route fresh = null;
            Exception failed = null;
            var t = new Thread(() =>
            {
                try
                {
                    Simulation sim = _create();
                    sim.Goals.Limit = _goals;
                    RunResult rr = sim.Run(r.Inputs, null, null, 1000000);
                    fresh = new Route { Inputs = r.Inputs, Progress = rr.Progress, GoalInput = rr.GoalInputTicks, GoalPhysics = rr.GoalPhysicsTicks, EndReason = rr.EndReason };
                }
                catch (Exception ex) { failed = ex; }
            }, 64 << 20);
            t.Start();
            t.Join();
            if (failed != null || fresh == null)
            {
                Log($"fresh check of {Describe(r)} ({where}) failed: {failed?.GetBaseException().Message}; not kept");
                return null;
            }
            string same = fresh.Progress == r.Progress && fresh.GoalPhysics.Take(fresh.Progress).SequenceEqual(r.GoalPhysics.Take(r.Progress)) ? "same goal ticks"
                : $"differs from this process's replay ({Describe(r)})";
            if (!Better(fresh, _best))
            {
                Log($"fresh check ({where}): a new simulation replays it as {Describe(fresh)}, {same}: no faster than the best {Describe(_best)}; not kept");
                AutoWrite(new { type = "check", where, kept = false, time = fresh.Progress == _goals ? Seconds(fresh.Last) : (double?)null, progress = fresh.Progress });
                return null;
            }
            // The chaining check is a diagnostic: every kept best logs how many goal touches it crosses at speed against the record
            // the loop started from, and which goals changed.
            string chain = "";
            if (_lib != null && fresh.Progress == _goals)
            {
                var (fast, median) = Chaining(fresh.Inputs);
                chain = $"; chaining {fast} of {_goals} at speed (start {_chain0.fast})" + (fast != _chain0.fast ? $": {ChainingDiff(_chainStart, fresh.Inputs)}" : "");
            }
            Log($"fresh check ({where}): {Describe(fresh)} from a new simulation, {same}{chain}; per goal {Splits(fresh)}");
            AutoWrite(new { type = "check", where, kept = true, time = Seconds(fresh.Last), progress = fresh.Progress });
            Collect(fresh, where);
            return fresh;
        }

        /// <summary>
        /// A repaired run can't win any more: even at every later goal's lower bound it ends no sooner than the best, or it is more
        /// than --repair-slack ticks behind the best at its last goal (the window's gain is gone and more).
        /// </summary>
        string Hopeless(Route run)
        {
            int p = run.Progress;
            if (p == 0 || p > _best.Progress) return null;
            long at = run.Clock(p);
            if (_bounds != null)
            {
                long floor = at + Enumerable.Range(p, Math.Max(0, _goals - p)).Sum(j => _bounds[j]);
                if (floor >= _best.Last) return $"at goal {p} (tick {at}) even the later goals' lower bounds end at tick {floor}, no sooner than the best's {_best.Last}";
            }
            long behind = at - _best.Clock(p);
            if (behind > _repairSlack) return $"{behind} ticks behind the best at goal {p} (--repair-slack {_repairSlack})";
            string ctgBound = CostToGoBound(run);
            if (ctgBound != null) return ctgBound;
            if (_repairAt is (double cpu, long lead))
            {
                double spent = CpuSeconds() - cpu, budget = _repairCpuPerTick * lead;
                if (spent >= budget) return $"its budget is spent ({spent:0} CPU s for a {lead}-tick lead, --repair-cpu-per-tick {_repairCpuPerTick})";
                // The next window gets what's left of it, on every thread.
                _repairMinutes = Math.Clamp((budget - spent) / (60.0 * _threads), 0.05, _repairMax);
            }
            return null;
        }

        (double cpu, long lead)? _repairAt;

        /// <summary>A repair starts: its budget is --repair-cpu-per-tick CPU seconds per tick it is ahead of the best at its goal.</summary>
        void RepairBegin(Route run)
        {
            int p = run.Progress;
            long lead = p > 0 && p <= _best.Progress ? _best.GoalPhysics[p - 1] - run.GoalPhysics[p - 1] : 1;
            _repairAt = (CpuSeconds(), Math.Max(1, lead));
        }

        /// <summary>Files a route as a reference (its goal ticks from its replay): a later graft or the gap model can use it.</summary>
        void Collect(Route r, string how)
        {
            if (r == null || r.Progress == 0) return;
            List<InputTick> inputs = r.Inputs.Take((int)Math.Min(r.GoalInput[r.Progress - 1], r.Inputs.Count)).ToList();
            ulong key = SnapshotIndex.Hash(inputs, inputs.Count);
            if (!_refKeys.Add(key)) return;
            _refs.Add(new AutoRef { How = how, Inputs = inputs, Progress = r.Progress, GoalPhysics = (long[])r.GoalPhysics.Clone(), GoalInput = (long[])r.GoalInput.Clone(), Key = key });
            _refsChanged = true;
            if (_refs.Count > MaxRefs) Prune();
        }

        /// <summary>Keeps the references that hold some goal's soonest arrival or shortest split (three of each per goal).</summary>
        HashSet<AutoRef> Useful()
        {
            var keep = new HashSet<AutoRef>();
            for (int k = 1; k <= _goals; k++)
            {
                var reach = _refs.Where(r => r.Progress >= k).ToList();
                foreach (AutoRef r in reach.OrderBy(r => r.GoalPhysics[k - 1]).Take(3)) keep.Add(r);
                foreach (AutoRef r in reach.OrderBy(r => Split(r.GoalPhysics, k)).Take(3)) keep.Add(r);
            }
            return keep;
        }

        void Prune()
        {
            HashSet<AutoRef> keep = Useful();
            _refs.RemoveAll(r => !keep.Contains(r));
        }

        void SaveRefs()
        {
            HashSet<AutoRef> keep = Useful();
            string temp = _refsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new
            {
                name = _name, goals = _labels,
                refs = _refs.Where(keep.Contains).Select(r => new { how = r.How, progress = r.Progress, goalPhysicsTicks = r.GoalPhysics, inputs = r.Inputs.Select(t => t.ToJson()) }),
            }));
            Solve.Replace(temp, _refsFile);
        }

        void LoadRefs()
        {
            if (!File.Exists(_refsFile)) return;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_refsFile), new JsonDocumentOptions { MaxDepth = 256 });
            if (!doc.RootElement.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).SequenceEqual(_labels)) return;
            int n = 0;
            foreach (JsonElement r in doc.RootElement.GetProperty("refs").EnumerateArray())
            {
                List<InputTick> refInputs = r.GetProperty("inputs").EnumerateArray().Select(InputTick.FromJson).ToList();
                Route route = _lib != null ? TraceRun(refInputs, r.GetProperty("how").GetString() + " (earlier --auto)") : Evaluate(refInputs);
                if (route.Progress == 0) continue;
                Collect(route, r.GetProperty("how").GetString() + " (earlier --auto)");
                n++;
            }
            Log($"auto: {n} references from {Path.GetFileName(_refsFile)}");
        }

        /// <summary>A replayable route's own path per physics tick, as a transplant's probe gives the old route's (world place,
        /// Velocity + momentum, facing, the input played).</summary>
        Reference ReferenceOf(List<InputTick> inputs)
        {
            var rows = new List<(long t, WorldPos p, Vector2 v, bool right, int input)>();
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            _eval.Run(inputs, null, null, 1000000, _ => rows.Add((_eval.Engine.PhysicsTicks, _eval.PlayerWorld, _eval.Player.Velocity + _eval.Player.momentum,
                _eval.Player.facingRight, (int)_eval.Engine.RunTicks - 1)));
            int count = (int)rows.Max(r => r.t) + 1;
            var reference = new Reference { X = new float[count], Y = new float[count], VX = new float[count], VY = new float[count], Right = new bool[count],
                FirstTick = Enumerable.Repeat(-1, inputs.Count + 1).ToArray(), InputAt = Enumerable.Repeat(-1, count).ToArray() };
            foreach (var r in rows)
            {
                int t = (int)r.t;
                reference.X[t] = r.p.x;
                reference.Y[t] = r.p.y;
                reference.VX[t] = r.v.x;
                reference.VY[t] = r.v.y;
                reference.Right[t] = r.right;
                int input = Math.Min(r.input, inputs.Count - 1);
                reference.InputAt[t] = input;
                if (input >= 0 && reference.FirstTick[input] < 0) reference.FirstTick[input] = t;
            }
            return reference;
        }

        // ---------------------------------------------------------------- the gap model

        /// <summary>Replays inputs from the start: per physics tick the player's place (world and capture frame), speed
        /// (|Velocity + momentum|), unlocks, goals reached and, with distances, the walkable distance to the current goal's target.</summary>
        List<Sample> Profile(List<InputTick> inputs, bool distances)
        {
            var s = new List<Sample>();
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            DistanceField field = distances ? DistanceField.For(_eval) : null;
            int avoidFor = -1, capsFor = -1;
            string caps = "";
            _eval.Run(inputs, null, null, 1000000, _ =>
            {
                int p = _eval.Progress;
                if (p != capsFor) { capsFor = p; caps = PlayerCaps.Of(_eval.Player).Id; }
                if (field != null && p != avoidFor && p < _goals)
                {
                    avoidFor = p;
                    field.SetAvoid(Solve.FullTasPaths(_name, new[] { _labels[p] }, _labels).Avoid);
                }
                WorldPos w = _eval.PlayerWorld;
                CapturePos c = _eval.PlayerAtCapture;
                float d = field == null || p >= _goals ? float.NaN : field.Distance(c);
                s.Add(new Sample { Tick = _eval.Engine.PhysicsTicks, Input = (int)_eval.Engine.RunTicks, Progress = p, X = w.x, Y = w.y, CX = c.x, CY = c.y,
                    Dist = float.IsInfinity(d) ? float.NaN : d, Speed = (_eval.Player.Velocity + _eval.Player.momentum).magnitude, Caps = caps });
            });
            return s;
        }

        /// <summary>The fastest path speed (units per physics tick over SpeedWindow ticks, teleports excluded) per unlock set.</summary>
        void Sustained(List<Sample> s, string how)
        {
            float sum = 0f;
            var steps = new Queue<float>();
            for (int i = 1; i < s.Count; i++)
            {
                float dx = s[i].X - s[i - 1].X, dy = s[i].Y - s[i - 1].Y, step = (float)Math.Sqrt(dx * dx + dy * dy);
                if (Jumped(s[i - 1], s[i]) || s[i].Caps != s[i - 1].Caps) { steps.Clear(); sum = 0f; continue; }
                steps.Enqueue(step);
                sum += step;
                if (steps.Count > SpeedWindow) sum -= steps.Dequeue();
                if (steps.Count < SpeedWindow) continue;
                float v = sum / SpeedWindow;
                if (!_vtop.TryGetValue(s[i].Caps, out var known) || v > known.speed) _vtop[s[i].Caps] = (v, $"{how}, tick {s[i].Tick}");
            }
        }

        bool Jumped(Sample a, Sample b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, step = (float)Math.Sqrt(dx * dx + dy * dy);
            return step > 2f * Math.Max(a.Speed, b.Speed) * _dt + TeleportSlack;
        }

        /// <summary>
        /// Top speeds from the engine: the best's own path, then from the best's state at each goal the moves that build speed played
        /// on their own (a dash each way, dash jumps, and with the omnidash the down-dash landing chain of 2-6 links, both sides): the
        /// fastest path speed any shows per unlock set.
        /// </summary>
        void MeasureSpeeds()
        {
            double c0 = CpuSeconds();
            Sustained(Profile(_best.Inputs, false), "the best's path");
            var peaks = new SortedDictionary<int, float>();
            for (int k = 1; k <= _goals; k++)
            {
                int prefixLength = PrefixAt(_best, k);
                _eval.SetStart(_origin, 0);
                _eval.Goals.Limit = _goals;
                SimSnapshot at = prefixLength == 0 ? _origin : _eval.Advance(_best.Inputs.GetRange(0, prefixLength));
                if (at == null) continue;
                _eval.SetStart(at, prefixLength);
                _eval.Restore(at);
                string capsId = PlayerCaps.Of(_eval.Player).Id;
                bool omni = capsId.Contains("om", StringComparison.OrdinalIgnoreCase);
                foreach (var (moves, links) in SpeedMoves(omni))
                {
                    var s = new List<Sample>();
                    float peak = 0f, first = float.NaN;
                    _eval.Run(moves, null, null, 20, _ =>
                    {
                        WorldPos w = _eval.PlayerWorld;
                        float v = (_eval.Player.Velocity + _eval.Player.momentum).magnitude;
                        if (float.IsNaN(first)) first = v;
                        peak = Math.Max(peak, v);
                        s.Add(new Sample { Tick = _eval.Engine.PhysicsTicks, X = w.x, Y = w.y, Speed = v, Caps = capsId });
                    });
                    Sustained(s, $"speed probe from goal {k - 1}" + (links > 0 ? $", a {links}-link bounce chain" : ""));
                    // What the chain itself adds to the speed it started with (the growth per link the old route lived on).
                    float added = peak - (float.IsNaN(first) ? 0f : first);
                    if (links > 0 && (!peaks.TryGetValue(links, out float p) || added > p)) peaks[links] = added;
                }
            }
            _eval.SetStart(_origin, 0);
            Log("auto: top path speed per unlock set (units per physics tick over " + SpeedWindow + " ticks): "
                + string.Join("; ", _vtop.OrderBy(v => v.Key).Select(v => $"{v.Key} {v.Value.speed:0.0} ({v.Value.how})")));
            if (peaks.Count > 0) Log("auto: bounce chain speed added (|Velocity + momentum| peak less its start) by links: " + string.Join(", ", peaks.Select(p => $"{p.Key}: {p.Value:0}")));
            Log($"auto: speed probes {CpuSeconds() - c0:0} CPU s");
        }

        static InputTick Stick(float x, float y, bool held, bool press = false, bool release = false)
        {
            return new InputTick { x = x, y = y, held = held || press, press = press, release = release && !held && !press };
        }

        static IEnumerable<(List<InputTick> moves, int links)> SpeedMoves(bool omni)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                var dash = new List<InputTick> { new InputTick { x = side, dash = true } };
                dash.AddRange(Enumerable.Repeat(Stick(side, 0f, false), 40));
                yield return (dash, 0);
                foreach (int jump in new[] { 3, 5, 7 })
                {
                    var super = new List<InputTick> { new InputTick { x = side, dash = true, dashJump = jump, lockX = side } };
                    super.AddRange(Enumerable.Repeat(Stick(side, 0f, true), 6));
                    super.Add(Stick(side, 0f, false, release: true));
                    super.AddRange(Enumerable.Repeat(Stick(side, 0f, false), 40));
                    yield return (super, 0);
                }
                if (!omni) continue;
                // Planner.BounceChain's shape: a dash straight down with a jump frame, the jump out of the landing held 3 ticks, air.
                foreach (int links in new[] { 2, 3, 4, 5, 6 })
                    foreach (int gap in new[] { 3, 7 })
                        foreach (int jump in new[] { 3, 6 })
                        {
                            var t = new List<InputTick>();
                            for (int l = 0; l < links; l++)
                            {
                                InputTick d = Stick(0f, -1f, false);
                                d.dash = true;
                                d.dashJump = jump;
                                d.lockX = side;
                                t.Add(d);
                                for (int i = 0; i < 3; i++) t.Add(Stick(side, 0f, true));
                                t.Add(Stick(side, 0f, false, release: true));
                                for (int i = 0; i < gap; i++) t.Add(Stick(side, 0f, false));
                            }
                            t.AddRange(Enumerable.Repeat(Stick(side, 0f, false), 20));
                            yield return (t, links);
                        }
            }
        }

        /// <summary>
        /// Per goal: the best's split; the lower bound (the walkable distance, DistanceField with the run's avoid boxes, else the
        /// straight line, from where the best starts the goal or lands after a teleport in it, over the top path speed with those
        /// unlocks); the speed-deficit stretches (ticks the best moves toward the target at under a third of what it manages in that
        /// goal: standing starts, loops back, resets, waits) and the ticks each would save; the known routes' soonest arrival and
        /// shortest split. Expected headroom = proven (a known route did it) + deficit + a share of the gap to the bound.
        /// </summary>
        void BuildModel()
        {
            // The field's grid covers every place asked about so far and coarsens as it grows (DistanceField.Build): a first pass
            // grows it over the whole route, so the measured pass has one grid from start to end.
            Profile(_best.Inputs, true);
            List<Sample> s = Profile(_best.Inputs, true);
            var rows = new List<GoalRow>();
            for (int k = 1; k <= _goals; k++)
            {
                long arrival = _best.GoalPhysics[k - 1], start = k == 1 ? 0 : _best.GoalPhysics[k - 2];
                List<Sample> seg = s.Where(x => x.Tick > start && x.Tick <= arrival).ToList();
                var row = new GoalRow { K = k, Label = _labels[k - 1], Split = arrival - start, Arrival = arrival };
                if (seg.Count == 0) { row.Bound = 1; rows.Add(row); continue; }
                Sample end = seg[seg.Count - 1];
                FullTasGoal goal = _runGoals.Goals[k - 1];
                float Straight(Sample x)
                {
                    if (goal.kind == FullTasGoal.At)
                    {
                        float dx = Math.Max(0f, Math.Abs(x.CX - goal.x) - goal.w / 2f), dy = Math.Max(0f, Math.Abs(x.CY - goal.y) - goal.h / 2f);
                        return (float)Math.Sqrt(dx * dx + dy * dy);
                    }
                    float ex = x.X - end.X, ey = x.Y - end.Y;
                    return (float)Math.Sqrt(ex * ex + ey * ey);
                }
                float Dist(Sample x)
                {
                    float st = Straight(x);
                    if (float.IsNaN(x.Dist) || x.Progress != k - 1) return st;
                    // An area goal's rect is exact (the walk can't be shorter than the line); a gate's walk is to its own box.
                    return goal.kind == FullTasGoal.At ? Math.Max(st, x.Dist) : x.Dist;
                }
                row.Caps = seg[0].Caps;
                row.VTop = _vtop.TryGetValue(row.Caps, out var v) ? Math.Max(1f, v.speed) : 1f;
                // Where the goal can be started from: its first tick, and after each teleport in it (a quick restart costs a tick).
                float d0 = Dist(seg[0]);
                double best = d0 / row.VTop;
                for (int i = 1; i < seg.Count; i++)
                {
                    if (Jumped(seg[i - 1], seg[i])) best = Math.Min(best, 1 + Dist(seg[i]) / row.VTop);
                }
                row.Distance = d0;
                row.Bound = Math.Max(1, (long)Math.Ceiling(best));
                // Speed deficit: progress toward the target per tick against what the best manages in this goal (its 80th percentile).
                var rates = new List<float>();
                for (int i = 1; i < seg.Count; i++) rates.Add(Dist(seg[i - 1]) - Dist(seg[i]));
                var positive = rates.Where(r => r > 0f).OrderBy(r => r).ToList();
                float attain = positive.Count == 0 ? 1f : Math.Max(1f, positive[(int)(positive.Count * .8)]);
                var stretches = new List<(double saved, string what)>();
                for (int i = 0; i < rates.Count;)
                {
                    if (rates[i] >= SlowShare * attain) { i++; continue; }
                    int j = i;
                    while (j < rates.Count && rates[j] < SlowShare * attain) j++;
                    int len = j - i;
                    if (len >= SlowStretch)
                    {
                        double made = rates.Skip(i).Take(len).Sum(), saved = len - Math.Max(0, made) / attain;
                        bool teleport = Enumerable.Range(i + 1, len).Any(m => m < seg.Count && Jumped(seg[m - 1], seg[m]));
                        double moved = Enumerable.Range(i + 1, len).Where(m => m < seg.Count).Average(m => Math.Sqrt(Math.Pow(seg[m].X - seg[m - 1].X, 2) + Math.Pow(seg[m].Y - seg[m - 1].Y, 2)));
                        string kind = teleport ? "reset" : made < 0 ? "loops back" : moved < 1.5 ? "standing/wait" : "slow";
                        stretches.Add((saved, $"{kind} {len} ticks from input {seg[i].Input} (~{saved:0} saved)"));
                    }
                    i = j;
                }
                row.Deficit = stretches.Sum(x => x.saved);
                row.Worst = string.Join(", ", stretches.OrderByDescending(x => x.saved).Take(2).Select(x => x.what));
                rows.Add(row);
            }
            _model = rows;
            _bounds = rows.Select(r => r.Bound).ToArray();
            Refresh();
        }

        /// <summary>The known routes' per-goal records and every goal's expected headroom (after a reference is filed).</summary>
        void Refresh()
        {
            foreach (GoalRow row in _model)
            {
                int k = row.K;
                var reach = _refs.Where(r => r.Progress >= k).ToList();
                AutoRef soonest = reach.OrderBy(r => r.GoalPhysics[k - 1]).FirstOrDefault(), shortest = reach.OrderBy(r => Split(r.GoalPhysics, k)).FirstOrDefault();
                row.RefArrival = soonest?.GoalPhysics[k - 1] ?? -1;
                row.RefArrivalHow = soonest?.How;
                row.RefSplit = shortest == null ? -1 : Split(shortest.GoalPhysics, k);
                row.RefSplitHow = shortest?.How;
                row.Expected = ProvenWeight * Math.Max(row.ArrivalLead, row.SplitLead) + DeficitWeight * row.Deficit + GapWeight * row.Gap;
            }
        }

        void PrintModel(string when)
        {
            var b = new StringBuilder();
            b.AppendLine($"auto: per-goal model ({when}), best {Describe(_best)}; bound = distance / top path speed for the goal's unlocks");
            b.AppendLine(" goal label                                    split  bound   gap  dist   v/tick  deficit  known  expect  slowest stretches");
            foreach (GoalRow r in _model)
                b.AppendLine($" {r.K,4} {r.Label,-40} {r.Split,5} {r.Bound,6} {r.Gap,5} {r.Distance,6:0} {r.VTop,7:0.0} {r.Deficit,8:0} {(r.RefSplit < 0 ? "-" : r.RefSplit.ToString(CultureInfo.InvariantCulture)),6} {r.Expected,7:0.0}  {r.Worst}");
            long bound = _model.Sum(r => r.Bound);
            b.Append($" total {_best.Last} ticks ({Describe(_best)}); lower bound {bound} ticks ({bound * _dt:0.00} s), deficit {_model.Sum(r => r.Deficit):0} ticks, "
                + $"most expected: {string.Join(", ", _model.OrderByDescending(r => r.Expected).Take(6).Select(r => $"{r.K} ({r.Expected:0})"))}");
            Log(b.ToString());
            AutoWrite(new { type = "model", when, best = Seconds(_best.Last), bound = _model.Sum(r => r.Bound),
                goals = _model.Select(r => new { goal = r.K, label = r.Label, split = r.Split, bound = r.Bound, gap = r.Gap, distance = Math.Round(r.Distance), vtop = Math.Round(r.VTop, 1),
                    caps = r.Caps, deficit = Math.Round(r.Deficit, 1), refSplit = r.RefSplit, refArrival = r.RefArrival, expected = Math.Round(r.Expected, 1), slow = r.Worst }) });
        }

        void PrintArms()
        {
            var rows = _arms.Values.Where(a => a.Pulls > 0).OrderByDescending(a => a.Gain).ThenByDescending(a => a.Pulls).Take(12)
                .Select(a => $"{a.Lever}@{a.Goal}: {a.Pulls} pulls, {a.Cpu:0} CPU s, {a.Gain:0} gained");
            Log("auto: arms " + string.Join("; ", rows) + "; lever cost per pull: "
                + string.Join(", ", AutoLevers.Select(l => $"{l} {LeverCost(l):0} CPU s")));
        }
    }
}
