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

namespace IGTAP.EngineSim.Cli
{
    sealed partial class FullTas
    {
        const string Api = "http://127.0.0.1:17426";

        sealed class Route
        {
            public List<InputTick> Inputs;
            public int Progress;
            public long[] GoalInput, GoalPhysics;
            /// <summary>How its replay from the start ended (Evaluate's), for the log.</summary>
            public string EndReason;
            public long Last { get { return Progress == 0 ? long.MaxValue : GoalPhysics[Progress - 1]; } }
        }

        readonly string _trace;
        readonly int _threads, _window, _onlyGoal, _onlySegment;
        readonly double _minutes, _windowMinutes, _stallMinutes, _pieceMinutes;
        readonly bool _submit, _fresh, _unsolved, _resets;
        string _name, _caps, _runFile, _closestFile, _outDir;
        string[] _labels;
        int _goals;
        float _dt;
        Func<Simulation> _create;
        Simulation _eval;
        SimSnapshot _origin;
        Route _best;
        Stopwatch _clock;
        Latest _pending;
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
            public int Level;
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
            _onlySegment = Solve.Option(args, "--segment", 0);
            _resets = args.Contains("--resets") || _onlySegment > 0;
            for (int i = 1; i < args.Length - 1; i++) if (args[i] == "--seed") _seedFiles.Add(args[i + 1]);
        }

        public static int Run(string[] args) { return new FullTas(args).Search(); }

        int Search()
        {
            string from = Open();
            Console.WriteLine($"Full-TAS {_name}: {_goals} goals, windows of {_window}, {_threads} threads; best from {from}: {Describe(_best)}");
            Emit(new { type = "session", run = _name, goals = _labels, window = _window, threads = _threads, minutes = _minutes, windowMinutes = _windowMinutes,
                from, best = RouteJson(_best), resets = _resets, segment = _onlySegment });
            foreach (string path in _seedFiles) _seeds.Add(ReadSeed(path));
            if (_seeds.Count > 0)
                Console.WriteLine($"Seeds: {_seeds.Count} saved routes for " + string.Join(", ", _seeds.SelectMany(s => s.Goals).Distinct()
                    .Select(g => g + " (" + _seeds.Count(s => s.Goals.Contains(g)) + ")")));
            if (_submit)
            {
                _pending = new Latest();
                new Thread(Submitter) { IsBackground = true }.Start();
            }
            new Thread(WatchStdin) { IsBackground = true }.Start();

            (int, long) reachFrontier = (-1, -1);
            int reachRound = 0, reachBack = 0;
            string stopped = null;
            while (!TimeUp && stopped == null)
            {
                Route best = _best;
                int reached = best.Progress;
                if (reached < _goals && (_unsolved || reached == 0))
                {
                    if (SeedFrontier()) continue;
                    if (IsLoop(reached))
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
                // --resets: the route cut at its quick restarts, each piece polished on its own (FullTasSegments.cs).
                if (_resets)
                {
                    (string level, string text) end = PolishSegments();
                    if (end.text == null) continue;
                    Log("stopped: " + end.text);
                    Emit(new { type = "note", level = end.level, text = "Solve by resets stopped: " + end.text + "." });
                    break;
                }
                // --goal N: only the window of --window goals ending at goal N (from goal N - window on), over and over.
                int lastGoal = _onlyGoal > 0 ? Math.Min(_onlyGoal, _best.Progress) : 0;
                int firstWindow = _onlyGoal > 0 ? Math.Max(0, lastGoal - _window) : 0;
                int endWindow = _onlyGoal > 0 ? firstWindow + 1 : _best.Progress;
                // Loop goals are polished by rebuilding them from faster pieces; the windows search what's left.
                if (_onlyGoal == 0) PolishLoops();
                for (int i = firstWindow; i < endWindow && !TimeUp; i++)
                {
                    best = _best;
                    int limit = _onlyGoal > 0 ? lastGoal : Math.Min(i + _window, best.Progress);
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
            _caps = header.GetProperty("capabilities").GetString();
            string dir = Path.GetDirectoryName(Path.GetFullPath(_trace));
            _runFile = Path.Combine(dir, "fulltas", _name + ".json");
            _closestFile = Path.Combine(dir, "fulltas", _name + ".closest.json");
            _outDir = Path.Combine(dir, "offline-best");
            // Every purchase is a goal: a route that buys a box other than its next goal's spends Cash and levels the goals
            // (and the plans that wrote them) don't count, so no search or check here plays one (docs/fulltas.md).
            Func<Simulation> make = Solve.Creator(_trace, header);
            _create = () => { Simulation sim = make(); sim.StrictBuys = true; return sim; };
            _clock = Stopwatch.StartNew();
            _eval = _create();
            _origin = _eval.Start;
            _dt = _eval.World.FixedDeltaTime;
            _runGoals = _eval.Goals;
            ReloadAvoids();
            _visitsFile = VisitsFile(dir, _name);
            _archiveDir = Path.Combine(dir, "archive", "fulltas-" + _name);
            string startKey = StartKey(header).ToString("x16");
            if (StartChanged(dir, _name, startKey))
            {
                Log($"Start state changed since the last solve of {_name}: dropping its archive and visits map");
                Directory.Delete(_archiveDir, true);
                File.Delete(_visitsFile);
            }
            Directory.CreateDirectory(_archiveDir);
            File.WriteAllText(StartFile(dir, _name), startKey);
            _visits = _fresh ? new Dictionary<long, float>() : ReadVisits(_visitsFile, _labels) ?? new Dictionary<long, float>();
            LoadPieces(dir);
            _states = new FullTasStates(_archiveDir, _fresh);
            if (_states.Index.Count > 0) Log($"goal states: {_states.Index.Count} in {Path.Combine("archive", "fulltas-" + _name, "states")}");

            _best = Padded(Evaluate(Solve.ReadInputs(header)));
            string from = Path.GetFileName(_trace);
            if (!_fresh && File.Exists(_runFile))
            {
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(_runFile));
                List<InputTick> inputs = Solve.ReadInputs(saved.RootElement);
                string[] solved = saved.RootElement.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToArray();
                long[] goalInput = saved.RootElement.GetProperty("goalInputTicks").EnumerateArray().Select(t => t.GetInt64()).ToArray();
                int keep = CurrentInputs(solved, goalInput, _labels, inputs.Count);
                if (keep < inputs.Count) inputs.RemoveRange(keep, inputs.Count - keep);
                Route run = Padded(Evaluate(inputs));
                if (!Better(_best, run)) { _best = run; from = "fulltas/" + Path.GetFileName(_runFile); }
            }
            LoadKnown(dir);
            return from;
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
            return new { progress = r.Progress, time = r.Progress == 0 ? (double?)null : Seconds(r.Last),
                splits = Enumerable.Range(0, r.Progress).Select(k => Seconds(r.GoalPhysics[k])).ToArray() };
        }

        bool TimeUp { get { return _stopRequested || _clock.Elapsed.TotalMinutes >= _minutes; } }

        volatile bool _stopRequested;

        /// <summary>
        /// A "stop" line on stdin (the studio's Stop) ends the solve as its time limit would: the window ends, its archive and
        /// visits are written, and the process exits.
        /// </summary>
        void WatchStdin()
        {
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
            if (r.Progress == _goals) return (r.Last * _dt).ToString("0.00", CultureInfo.InvariantCulture) + "s";
            return r.Progress + "/" + _goals + " goals" + (r.Progress > 0 ? " (" + (r.Last * _dt).ToString("0.00", CultureInfo.InvariantCulture) + "s)" : "");
        }

        static bool Better(Route a, Route b)
        {
            if (a.Progress != b.Progress) return a.Progress > b.Progress;
            return a.Progress > 0 && a.Last < b.Last;
        }

        Route Evaluate(List<InputTick> inputs, int limit = -1)
        {
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = limit < 0 ? _goals : limit;
            RunResult r = _eval.Run(inputs, null, null, 1000000);
            return new Route { Inputs = inputs, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks, EndReason = r.EndReason };
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
            var key = (_eval.MovementFingerprint(), _labels[first]);
            string caps = WindowCaps(_runGoals.Goals.Skip(first).Take(limit - first));
            if (_memo.TryGetValue(key, out List<List<InputTick>> known))
                foreach (List<InputTick> r in known)
                    if (!seeds.Any(s => s.SequenceEqual(r))) seeds.Add(r);
            string title = kind + " goals " + (first + 1) + "-" + limit + "/" + _goals;
            string span = first == limit - 1 ? _labels[first] : _labels[first] + ".." + _labels[limit - 1];
            List<(string how, List<InputTick> inputs)> starts = SeedStarts(at, prefix, new HashSet<string>(_labels.Skip(first).Take(limit - first)),
                CanRestartAfter(prefix, first));
            foreach (var s in starts) if (!seeds.Any(r => r.SequenceEqual(s.inputs))) seeds.Add(s.inputs);
            if (starts.Count > 0) Log($"{title} {span}: {starts.Count} starts from saved routes");
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
            string archiveFile = Path.Combine(_archiveDir, (first + 1) + "-" + limit + "-" + ArchiveHash(prefix, _labels.Skip(first).Take(limit - first)).ToString("x16") + ".json");
            var labels = new HashSet<string>(_labels.Skip(first).Take(limit - first));
            List<Seed> walkers = kind == "reach" ? _seeds.Where(s => s.At != null && s.Goals.Any(labels.Contains)).ToList() : new List<Seed>();
            var tried = new HashSet<long>();
            List<List<InputTick>> found = Explore(id, title, span, create, caps, seeds, archiveFile, baseline, budgetMinutes,
                stallMinutes ?? (kind == "reach" ? 0.0 : _stallMinutes), kind == "reach" ? prefix : null, _labels.Skip(first).Take(limit - first).ToList(), out List<InputTick> bestInputs,
                walkers.Count == 0 ? null : solver => CellWalks(solver, at, prefix.Count, limit, walkers, tried, title));
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
        /// A reach window passes its <paramref name="closestPrefix"/> for the studio's Test best.
        /// </summary>
        List<List<InputTick>> Explore(int id, string title, string span, Func<Simulation> create, string caps, List<List<InputTick>> seeds,
            string archiveFile, long baseline, double budgetMinutes, double stallMinutes, List<InputTick> closestPrefix, ICollection<string> goals,
            out List<InputTick> bestInputs, Func<OfflineSolver, List<List<InputTick>>> walks = null)
        {
            var found = new List<List<InputTick>>();
            ReloadAvoids(goals);
            OfflineSolver.Memory memory = _fresh ? null : Solve.LoadMemory(archiveFile);
            if (memory != null)
                Log($"Resuming from {Path.GetFileName(archiveFile)}: {memory.Sources.Sum(s => s.Cells.Count)} cells over {memory.Sources.Count} prefixes, "
                    + $"{memory.Plateau.Count} plateau routes, {memory.Runs} runs before");
            var solver = new OfflineSolver(create, caps, seeds, memory, _path, VisitCell);
            solver.Resume = _states.Index;
            TimeSpan begin = _clock.Elapsed, last = begin;
            bool finished = false;
            long windowBest = long.MaxValue;
            solver.Improved += (time, runTicks, inputs, how) =>
            {
                // A Full-TAS sim is strict: its run ends StrictFinishTicks after its last goal, and the baseline is that goal's tick.
                long atGoal = runTicks - Simulation.StrictFinishTicks;
                last = _clock.Elapsed;
                finished = true;
                windowBest = Math.Min(windowBest, atGoal);
                if (atGoal < baseline)
                {
                    found.Add(inputs);
                    if (found.Count == 1) Emit(new { type = "found", id, time = Seconds(atGoal) });
                }
            };
            object Progress()
            {
                lock (solver.Shared)
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
                lock (solver.Shared) if (finished && (_clock.Elapsed - last).TotalMinutes >= stallMinutes) break;
                if ((_clock.Elapsed - shown).TotalSeconds >= 15)
                {
                    shown = _clock.Elapsed;
                    Log($"{title} {span} best {Describe(_best)} ({found.Count} candidates), {Interlocked.Read(ref solver.Runs)} runs, {solver.ArchiveStatus()}, heap {GC.GetTotalMemory(false) >> 20} MB");
                    List<List<InputTick>> walked = walks?.Invoke(solver);
                    if (walked?.Count > 0)
                        lock (solver.Shared)
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
            lock (solver.Shared)
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
            string temp = _closestFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new
            {
                name = _name,
                goals = _labels,
                reached = replayed.Progress,
                next = replayed.Progress < _goals ? _labels[replayed.Progress] : null,
                distance = float.IsFinite(replayed.Distance) ? Math.Round(replayed.Distance, 1) : (double?)null,
                inputs = inputs.Select(t => t.ToJson()),
            }));
            Solve.Replace(temp, _closestFile);
        }

        /// <summary>What the search presses for these goals, from the player <see cref="_eval"/> holds now and the boxes they buy.</summary>
        string WindowCaps(IEnumerable<FullTasGoal> goals)
        {
            bool dash = _eval.Player.dashUnlocked, omni = dash && _eval.Player.omniDashUnlocked;
            foreach (FullTasGoal goal in goals)
                foreach (string box in goal.boxes ?? Array.Empty<string>())
                {
                    upgradeBox.movementUpgrades? move = _eval.Shop.MovementUpgrade(box);
                    dash |= move == upgradeBox.movementUpgrades.dash || move == upgradeBox.movementUpgrades.freeVmanOmnidash;
                    omni |= move == upgradeBox.movementUpgrades.freeVmanOmnidash;
                }
            return omni ? "omni" : dash ? "wjds" : _caps;
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

        List<(string how, List<InputTick> inputs)> SeedStarts(SimSnapshot at, List<InputTick> prefix, HashSet<string> labels, bool restart)
        {
            var starts = new List<(string how, List<InputTick> inputs)>();
            _eval.Restore(at);
            Movement player = _eval.Player;
            int level = !player.wallJumpUnlocked ? 0 : !player.dashUnlocked ? 1 : !player.doubleJumpUnlocked ? 2 : !player.blockSwapUnlocked ? 3 : !player.omniDashUnlocked ? 4 : 5;
            List<Seed> matching = _seeds.Where(s => s.Level <= level && s.Goals.Any(labels.Contains)).OrderByDescending(s => s.Level).ToList();
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
                }
                list.Add((s.Name + ", as it is", s.Inputs));
                if (restart)
                    foreach (int k in Enumerable.Range(0, MaxSettle + 1).OrderBy(k => Math.Abs(k - Math.Max(0, settle))))
                        if (!(s.Standing && k == settle)) list.Add((s.Name + ", after a quick restart and " + k + " idle ticks", Restarted(s, k)));
                return list;
            }).ToList();
            foreach (Seed bonus in matching.Where(s => s.Standing && s.Name.StartsWith("bonus", StringComparison.OrdinalIgnoreCase)))
            {
                string token = bonus.Name.Substring(0, bonus.Name.IndexOf('_'));
                var list = new List<(string how, List<InputTick> inputs)>();
                foreach (Seed transition in _seeds.Where(s => s.Level <= level && !s.Standing
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
                UnityEngine.Vector3 p = _eval.PlayerWorld;
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
                var spot = new UnityEngine.Vector2(s.At.X + _eval.Origin.x, s.At.Y + _eval.Origin.y);
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
            List<(string how, List<InputTick> inputs)> starts = SeedStarts(at, prefix, new HashSet<string> { _labels[reached] }, CanRestartAfter(prefix, reached));
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
                Level = !root.TryGetProperty("capabilities", out JsonElement caps) || caps.ValueKind != JsonValueKind.String ? 0
                    : Math.Max(0, Array.IndexOf(Solve.Categories, caps.GetString())),
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
            foreach (List<InputTick> route in Enumerable.Reverse(found).Take(5))
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
                if (broken == null && candidate.Progress < best.Progress && candidate.Progress >= limit) broken = candidate;
            }
            if (broken != null)
            {
                Emit(new { type = "note", level = "info", text = $"Faster through goal {limit}, but the old route after it no longer works from there; re-finding goals {limit + 1}-{best.Progress}." });
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
                Log("a window route missed its goals when replayed from the start; dropped");
                return null;
            }
            int end = (int)through.GoalInput[limit - 1];
            if (end > inputs.Count) Pad(inputs, end);
            else inputs.RemoveRange(end, inputs.Count - end);
            if (tail.Progress >= limit) inputs.AddRange(tail.Inputs.Skip((int)tail.GoalInput[limit - 1]));
            for (int i = 1; i < inputs.Count; i++) if (inputs[i].reset && inputs[i - 1].reset) return null;
            return Evaluate(inputs);
        }

        void Repair(string where, Route old, Route run)
        {
            while (!TimeUp)
            {
                if (run.Progress >= old.Progress)
                {
                    if (Better(run, _best)) Commit(run, where + ", then re-found the later goals");
                    else
                    {
                        Log($"repaired run {Describe(run)} is no better; dropped");
                        Emit(new { type = "dropped", text = "Re-found the later goals, but the whole run is no faster; dropped." });
                    }
                    return;
                }
                if (run.Last >= _best.Last)
                {
                    Log($"repair dropped: goal {run.Progress}/{_goals} reached no sooner than the best's last goal");
                    Emit(new { type = "dropped", text = $"Repair gave up: it reaches goal {run.Progress} no sooner than the record reaches goal {_best.Progress}." });
                    return;
                }
                int reached = run.Progress, limit = Math.Min(reached + _window, old.Progress);
                int prefixLength = (int)Math.Min(run.GoalInput[reached - 1], run.Inputs.Count);
                var seeds = new List<List<InputTick>> { run.Inputs.Skip(prefixLength).ToList(), old.Inputs.Skip((int)old.GoalInput[reached - 1]).ToList() };
                List<List<InputTick>> found = SearchWindow("repair", run, prefixLength, limit, seeds, long.MaxValue, _windowMinutes, 0);
                if (found.Count == 0)
                {
                    Log($"repair dropped: no route to {_labels[reached]} from the new state");
                    Emit(new { type = "dropped", text = $"Repair gave up: no way to goal {reached + 1} from the new state." });
                    return;
                }
                Route next = Splice(run, prefixLength, found[found.Count - 1], limit, old);
                if (next == null || next.Progress <= reached)
                {
                    Log("repair dropped: its route missed its goals when replayed from the start");
                    Emit(new { type = "dropped", text = "Repair gave up: its route didn't reproduce when replayed from the start." });
                    return;
                }
                run = next;
            }
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
            return padded.Progress == r.Progress && padded.Last == r.Last ? padded : r;
        }

        void Commit(Route r, string where)
        {
            r = Padded(r);
            Route previous = _best;
            _best = r;
            Directory.CreateDirectory(Path.GetDirectoryName(_runFile));
            string temp = _runFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new
            {
                name = _name,
                goals = _labels,
                inputs = r.Inputs.Select(t => t.ToJson()),
                goalInputTicks = r.GoalInput,
                goalPhysicsTicks = r.GoalPhysics,
                total = r.Progress == _goals ? r.Last : -1,
            }));
            Solve.Replace(temp, _runFile);
            SaveKnown();
            string file = _runFile;
            if (r.Progress == _goals)
            {
                float time = r.Last * _dt;
                Directory.CreateDirectory(_outDir);
                file = Path.Combine(_outDir, "fulltas-" + _name + "-" + time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
                Solve.WriteShared(file, JsonSerializer.Serialize(new { fulltas = _name, time, runTicks = r.Last, ticks = r.Inputs.Select(t => t.ToJson()) }));
                _pending?.Set(r.Inputs);
            }
            Log($"new best {Describe(r)} ({where}) -> {Path.GetFileName(file)}");
            Emit(new { type = "record", how = where, previous = RouteJson(previous), best = RouteJson(r), file = Path.GetFileName(file),
                complete = r.Progress == _goals, firstComplete = r.Progress == _goals && previous.Progress < _goals });
        }

        sealed class Latest
        {
            List<InputTick> _item;
            public void Set(List<InputTick> inputs) { lock (this) { _item = inputs; Monitor.Pulse(this); } }
            public void SetIfEmpty(List<InputTick> inputs) { lock (this) { if (_item == null) { _item = inputs; Monitor.Pulse(this); } } }
            public List<InputTick> Take()
            {
                lock (this)
                {
                    while (_item == null) Monitor.Wait(this);
                    List<InputTick> item = _item;
                    _item = null;
                    return item;
                }
            }
        }

        void Submitter()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            int deferred = 0;
            while (true)
            {
                List<InputTick> inputs = _pending.Take();
                string body = JsonSerializer.Serialize(new { name = _name, ticks = inputs.Select(t => t.ToJson()) });
                try
                {
                    string reply = http.PostAsync(Api + "/api/fulltas/submit", new StringContent(body, Encoding.UTF8, "application/json")).Result
                        .Content.ReadAsStringAsync().Result;
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
