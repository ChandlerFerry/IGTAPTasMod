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

namespace IGTAP.EngineSim.Cli
{
    sealed class FullTas
    {
        const string Api = "http://127.0.0.1:17426";

        sealed class Route
        {
            public List<InputTick> Inputs;
            public int Progress;
            public long[] GoalInput, GoalPhysics;
            public long Last { get { return Progress == 0 ? long.MaxValue : GoalPhysics[Progress - 1]; } }
        }

        readonly string _trace;
        readonly int _threads, _window, _onlyGoal;
        readonly double _minutes, _windowMinutes, _stallMinutes;
        readonly bool _submit, _fresh, _unsolved;
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
        Dictionary<long, float> _visits;
        string _visitsFile;
        string _archiveDir;

        sealed class Seed
        {
            public string Name;
            public string[] Goals;
            public bool Standing, Restart;
            public int Level;
            public List<InputTick> Inputs;
        }
        const int MaxSeedStarts = 64, MaxSettle = 12;
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
            _window = Math.Max(1, Solve.Option(args, "--window", 2));
            _submit = !args.Contains("--no-submit");
            _fresh = args.Contains("--fresh");
            _unsolved = args.Contains("--unsolved");
            _onlyGoal = Solve.Option(args, "--goal", 0);
            for (int i = 1; i < args.Length - 1; i++) if (args[i] == "--seed") _seedFiles.Add(args[i + 1]);
        }

        public static int Run(string[] args) { return new FullTas(args).Search(); }

        int Search()
        {
            JsonElement header;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_trace), new JsonDocumentOptions { MaxDepth = 256 }))
                header = doc.RootElement.Clone();
            if (!header.TryGetProperty("fulltas", out JsonElement fulltas)) throw new InvalidOperationException("Not a Full-TAS trace (no \"fulltas\" header): " + _trace);
            _name = fulltas.GetProperty("name").GetString();
            _labels = fulltas.GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray();
            _goals = _labels.Length;
            _caps = header.GetProperty("capabilities").GetString();
            string dir = Path.GetDirectoryName(Path.GetFullPath(_trace));
            _runFile = Path.Combine(dir, "fulltas", _name + ".json");
            _closestFile = Path.Combine(dir, "fulltas", _name + ".closest.json");
            _outDir = Path.Combine(dir, "offline-best");
            _create = Solve.Creator(_trace, header);
            _clock = Stopwatch.StartNew();
            _eval = _create();
            _origin = _eval.Start;
            _dt = _eval.World.FixedDeltaTime;
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
            Console.WriteLine($"Full-TAS {_name}: {_goals} goals, windows of {_window}, {_threads} threads; best from {from}: {Describe(_best)}");
            Emit(new { type = "session", run = _name, goals = _labels, window = _window, threads = _threads, minutes = _minutes, windowMinutes = _windowMinutes,
                from, best = RouteJson(_best) });
            foreach (string path in _seedFiles) _seeds.Add(ReadSeed(path));
            if (_seeds.Count > 0)
                Console.WriteLine($"Seeds: {_seeds.Count} saved routes for " + string.Join(", ", _seeds.SelectMany(s => s.Goals).Distinct()
                    .Select(g => g + " (" + _seeds.Count(s => s.Goals.Contains(g)) + ")")));
            if (_submit)
            {
                _pending = new Latest();
                new Thread(Submitter) { IsBackground = true }.Start();
            }

            (int, long) reachFrontier = (-1, -1);
            int reachRound = 0, reachBack = 0;
            while (!TimeUp)
            {
                Route best = _best;
                int reached = best.Progress;
                if (reached < _goals && (_unsolved || reached == 0))
                {
                    if (SeedFrontier()) continue;
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
                    if (++reachBack > Math.Min(reached, MaxReachBack)) { reachBack = 0; reachRound++; }
                    continue;
                }
                Phase("polish");
                // --goal N: only the window of --window goals ending at goal N (from goal N - window on), over and over.
                int lastGoal = _onlyGoal > 0 ? Math.Min(_onlyGoal, _best.Progress) : 0;
                int firstWindow = _onlyGoal > 0 ? Math.Max(0, lastGoal - _window) : 0;
                int endWindow = _onlyGoal > 0 ? firstWindow + 1 : _best.Progress;
                for (int i = firstWindow; i < endWindow && !TimeUp; i++)
                {
                    best = _best;
                    int limit = _onlyGoal > 0 ? lastGoal : Math.Min(i + _window, best.Progress);
                    int prefixLength = i == 0 ? 0 : (int)Math.Min(best.GoalInput[i - 1], best.Inputs.Count);
                    var seeds = new List<List<InputTick>> { best.Inputs.Skip(prefixLength).ToList() };
                    List<List<InputTick>> found = SearchWindow("polish", best, prefixLength, limit, seeds, best.GoalPhysics[limit - 1], _windowMinutes, 0);
                    if (found.Count > 0) Merge("faster through goals " + (i + 1) + "-" + limit, best, prefixLength, limit, found);
                }
            }
            Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] Done. Best {Describe(_best)}");
            Emit(new { type = "done", best = RouteJson(_best) });
            return 0;
        }

        const int MaxReachBack = 3;
        string _phase;
        int _windowId;

        void Phase(string phase)
        {
            if (_phase == phase) return;
            _phase = phase;
            Log(phase == "reach" ? "phase: find a complete route first" : "phase: polish the goals the route reaches, from goal 1");
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

        bool TimeUp { get { return _clock.Elapsed.TotalMinutes >= _minutes; } }

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

        void ReloadAvoids()
        {
            OfflineSolver.ForcedPath path = Solve.FullTasPaths(_name);
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
            return new Route { Inputs = inputs, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks };
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

        List<List<InputTick>> SearchWindow(string kind, Route route, int prefixLength, int limit, List<List<InputTick>> seeds, long baseline, double budgetMinutes, int attempt)
        {
            var found = new List<List<InputTick>>();
            List<InputTick> prefix = route.Inputs.GetRange(0, prefixLength);
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            SimSnapshot at = _eval.Advance(prefix);
            if (at == null)
            {
                Log(kind + ": the route ends before its first " + prefixLength + " inputs do; skipped");
                Emit(new { type = "note", level = "warn", text = "Skipped a " + kind + " window: the route ends before its first " + prefixLength + " inputs do." });
                return found;
            }
            _eval.Restore(at);
            int first = _eval.Progress;
            if (first >= limit) return found;
            var key = (_eval.MovementFingerprint(), _labels[first]);
            string caps = WindowCaps(first, limit);
            if (_memo.TryGetValue(key, out List<List<InputTick>> known))
                foreach (List<InputTick> r in known)
                    if (!seeds.Any(s => s.SequenceEqual(r))) seeds.Add(r);
            string title = kind + " goals " + (first + 1) + "-" + limit + "/" + _goals;
            string span = first == limit - 1 ? _labels[first] : _labels[first] + ".." + _labels[limit - 1];
            List<(string how, List<InputTick> inputs)> starts = SeedStarts(at, prefix, first, limit);
            foreach (var s in starts) if (!seeds.Any(r => r.SequenceEqual(s.inputs))) seeds.Add(s.inputs);
            if (starts.Count > 0) Log($"{title} {span}: {starts.Count} starts from saved routes");
            int id = ++_windowId;
            Emit(new { type = "window", id, kind, first, limit, attempt, budget = Math.Round(budgetMinutes * 60, 1),
                start = first == 0 ? 0.0 : Seconds(route.GoalPhysics[first - 1]), target = baseline == long.MaxValue ? (double?)null : Seconds(baseline),
                savedStarts = starts.Count });

            Func<Simulation> create = () =>
            {
                Simulation sim = _create();
                SimSnapshot state = sim.Advance(prefix) ?? throw new InvalidOperationException(title + ": the prefix replayed differently on a worker");
                sim.SetStart(state, prefix.Count);
                sim.Goals.Limit = limit;
                return sim;
            };
            ReloadAvoids();
            string archiveFile = Path.Combine(_archiveDir, (first + 1) + "-" + limit + "-" + ArchiveHash(prefix, first, limit).ToString("x16") + ".json");
            OfflineSolver.Memory memory = _fresh ? null : Solve.LoadMemory(archiveFile);
            if (memory != null)
                Log($"Resuming from {Path.GetFileName(archiveFile)}: {memory.Sources.Sum(s => s.Cells.Count)} cells over {memory.Sources.Count} prefixes, "
                    + $"{memory.Plateau.Count} plateau routes, {memory.Runs} runs before");
            var solver = new OfflineSolver(create, caps, seeds, memory, _path, VisitCell);
            TimeSpan begin = _clock.Elapsed, last = begin;
            bool finished = false;
            long windowBest = long.MaxValue;
            solver.Improved += (time, runTicks, inputs, how) =>
            {
                last = _clock.Elapsed;
                finished = true;
                windowBest = Math.Min(windowBest, runTicks);
                if (runTicks < baseline)
                {
                    found.Add(inputs);
                    if (found.Count == 1) Emit(new { type = "found", id, time = Seconds(runTicks) });
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
                lock (solver.Shared) if (finished && (kind == "reach" || (_clock.Elapsed - last).TotalMinutes >= _stallMinutes)) break;
                if ((_clock.Elapsed - shown).TotalSeconds >= 15)
                {
                    shown = _clock.Elapsed;
                    Log($"{title} {span} best {Describe(_best)} ({found.Count} candidates), {Interlocked.Read(ref solver.Runs)} runs, {solver.ArchiveStatus()}, heap {GC.GetTotalMemory(false) >> 20} MB");
                }
                if ((_clock.Elapsed - emitted).TotalSeconds >= 1)
                {
                    emitted = _clock.Elapsed;
                    Emit(Progress());
                    if (kind == "reach") OfferClosest(solver.Closest(), prefix);
                }
                if ((_clock.Elapsed - saved).TotalSeconds >= 60) { saved = _clock.Elapsed; Solve.SaveMemory(archiveFile, solver.Export(5000)); }
            }
            Solve.SaveMemory(archiveFile, solver.Export(10000));
            solver.Stop();
            foreach (Thread t in threads) t.Join(60000);
            List<InputTick> bestInputs;
            lock (solver.Shared) bestInputs = solver.Shared.Inputs;
            if (kind == "reach") OfferClosest(solver.Closest(), prefix);
            if (bestInputs != null) Remember(key, bestInputs);
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

        string WindowCaps(int first, int limit)
        {
            bool dash = _eval.Player.dashUnlocked, omni = dash && _eval.Player.omniDashUnlocked;
            for (int g = first; g < limit; g++)
                foreach (string box in _eval.Goals.Goals[g].boxes ?? Array.Empty<string>())
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

        ulong ArchiveHash(List<InputTick> prefix, int first, int limit)
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
                hash = Feed(hash, t.reset);
            }
            for (int i = first; i < limit; i++)
            {
                hash = Feed(hash, 0xff, 1);
                foreach (byte b in Encoding.UTF8.GetBytes(_labels[i])) hash = Feed(hash, b, 1);
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
                goals = _labels,
                cell = VisitCell,
                cells = _visits.Select(v => new[] { (float)(int)(v.Key >> 32), (float)(int)v.Key, (float)Math.Round(v.Value, 2) }),
            }));
        }

        List<(string how, List<InputTick> inputs)> SeedStarts(SimSnapshot at, List<InputTick> prefix, int first, int limit)
        {
            var starts = new List<(string how, List<InputTick> inputs)>();
            var labels = new HashSet<string>(_labels.Skip(first).Take(limit - first));
            _eval.Restore(at);
            Movement player = _eval.Player;
            int level = !player.wallJumpUnlocked ? 0 : !player.dashUnlocked ? 1 : !player.doubleJumpUnlocked ? 2 : !player.blockSwapUnlocked ? 3 : !player.omniDashUnlocked ? 4 : 5;
            List<Seed> matching = _seeds.Where(s => s.Level <= level && s.Goals.Any(labels.Contains)).OrderByDescending(s => s.Level).ToList();
            if (matching.Count == 0) return starts;
            bool restart = (prefix.Count == 0 || !prefix[prefix.Count - 1].reset) && (first == 0 || !_labels[first - 1].StartsWith("at:", StringComparison.Ordinal));
            _eval.SetStart(at, prefix.Count);
            _eval.Goals.Limit = _goals;
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
            if (_eval.Progress != reached) return false;
            List<(string how, List<InputTick> inputs)> starts = SeedStarts(at, prefix, reached, reached + 1);
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
