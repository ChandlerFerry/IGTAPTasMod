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
        readonly int _threads, _window;
        readonly double _minutes, _windowMinutes, _stallMinutes;
        readonly bool _submit, _fresh;
        string _name, _caps, _runFile, _outDir;
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
            _outDir = Path.Combine(dir, "offline-best");
            string worldPath = Path.ChangeExtension(_trace, null) + ".world.json";
            JsonElement start = header.GetProperty("start");
            _create = () =>
            {
                var sim = new Simulation(World.Load(worldPath));
                sim.SetTarget(header);
                sim.LoadStart(start);
                return sim;
            };
            _clock = Stopwatch.StartNew();
            _eval = _create();
            _origin = _eval.Start;
            _dt = _eval.World.FixedDeltaTime;

            _best = Padded(Evaluate(Solve.ReadInputs(header)));
            string from = Path.GetFileName(_trace);
            if (!_fresh && File.Exists(_runFile))
            {
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(_runFile));
                Route run = Padded(Evaluate(Solve.ReadInputs(saved.RootElement)));
                if (!Better(_best, run)) { _best = run; from = "fulltas/" + Path.GetFileName(_runFile); }
            }
            Console.WriteLine($"Full-TAS {_name}: {_goals} goals, windows of {_window}, {_threads} threads; best from {from}: {Describe(_best)}");
            foreach (string path in _seedFiles) _seeds.Add(ReadSeed(path));
            if (_seeds.Count > 0)
                Console.WriteLine($"Seeds: {_seeds.Count} saved routes for " + string.Join(", ", _seeds.SelectMany(s => s.Goals).Distinct()
                    .Select(g => g + " (" + _seeds.Count(s => s.Goals.Contains(g)) + ")")));
            if (_submit)
            {
                _pending = new Latest();
                new Thread(Submitter) { IsBackground = true }.Start();
            }

            while (!TimeUp)
            {
                bool searched = false;
                while (!TimeUp && SeedFrontier()) searched = true;
                bool frontierFirst = _best.Progress < _goals;
                for (int step = 0; !TimeUp; step++)
                {
                    Route best = _best;
                    int reached = best.Progress;
                    int i;
                    if (frontierFirst) { if (step > reached) break; i = step == 0 ? reached : step - 1; }
                    else { if (step >= _goals) break; i = step; }
                    if (i > reached) break;
                    if (i == reached && SeedFrontier())
                    {
                        step = -1;
                        searched = true;
                        continue;
                    }
                    int limit = reached == _goals ? Math.Min(i + _window, _goals) : i == reached ? reached + 1 : Math.Min(i + _window, reached);
                    int prefixLength = i == 0 ? 0 : (int)Math.Min(best.GoalInput[i - 1], best.Inputs.Count);
                    long baseline = reached >= limit ? best.GoalPhysics[limit - 1] : long.MaxValue;
                    var seeds = new List<List<InputTick>> { best.Inputs.Skip(prefixLength).ToList() };
                    if (i == reached) seeds.AddRange(DelayedResets(seeds[0]));
                    List<List<InputTick>> found = SearchWindow("window " + (i + 1) + "/" + _goals, best, prefixLength, limit, seeds, baseline);
                    searched = true;
                    if (found.Count > 0) Merge("window " + (i + 1), best, prefixLength, limit, found);
                    if (frontierFirst && _best.Progress > reached) { step = -1; frontierFirst = _best.Progress < _goals; }
                }
                if (!searched) break;
            }
            Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] Done. Best {Describe(_best)}");
            return 0;
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

        List<List<InputTick>> SearchWindow(string title, Route route, int prefixLength, int limit, List<List<InputTick>> seeds, long baseline)
        {
            var found = new List<List<InputTick>>();
            List<InputTick> prefix = route.Inputs.GetRange(0, prefixLength);
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            SimSnapshot at = _eval.Advance(prefix);
            if (at == null)
            {
                Log(title + ": the route ends before its first " + prefixLength + " inputs do; skipped");
                return found;
            }
            _eval.Restore(at);
            int first = _eval.Progress;
            if (first >= limit) return found;
            var key = (_eval.MovementFingerprint(), _labels[first]);
            string caps = _eval.Player.dashUnlocked ? "wjds" : _caps;
            if (_memo.TryGetValue(key, out List<List<InputTick>> known))
                foreach (List<InputTick> r in known)
                    if (!seeds.Any(s => s.SequenceEqual(r))) seeds.Add(r);
            string span = first == limit - 1 ? _labels[first] : _labels[first] + ".." + _labels[limit - 1];
            List<(string how, List<InputTick> inputs)> starts = SeedStarts(at, prefix, first, limit);
            foreach (var s in starts) if (!seeds.Any(r => r.SequenceEqual(s.inputs))) seeds.Add(s.inputs);
            if (starts.Count > 0) Log($"{title} {span}: {starts.Count} starts from saved routes");

            Func<Simulation> create = () =>
            {
                Simulation sim = _create();
                SimSnapshot state = sim.Advance(prefix) ?? throw new InvalidOperationException(title + ": the prefix replayed differently on a worker");
                sim.SetStart(state, prefix.Count);
                sim.Goals.Limit = limit;
                return sim;
            };
            var solver = new OfflineSolver(create, caps, seeds);
            TimeSpan begin = _clock.Elapsed, last = begin;
            bool finished = false;
            solver.Improved += (time, runTicks, inputs, how) =>
            {
                last = _clock.Elapsed;
                finished = true;
                if (runTicks < baseline) found.Add(inputs);
            };
            int Improvements() { lock (solver.Shared) return found.Count; }
            void Status() { Log($"{title} {span} best {Describe(_best)} ({Improvements()} improvements)"); }
            Status();
            Thread[] threads = solver.Start(_threads);
            TimeSpan shown = begin;
            while (!TimeUp && (_clock.Elapsed - begin).TotalMinutes < _windowMinutes)
            {
                Thread.Sleep(250);
                lock (solver.Shared) if (finished && (_clock.Elapsed - last).TotalMinutes >= _stallMinutes) break;
                if ((_clock.Elapsed - shown).TotalSeconds >= 15) { shown = _clock.Elapsed; Status(); }
            }
            solver.Stop();
            foreach (Thread t in threads) t.Join(60000);
            List<InputTick> windowBest;
            lock (solver.Shared) windowBest = solver.Shared.Inputs;
            if (windowBest != null) Remember(key, windowBest);
            Status();
            lock (solver.Shared) return found.ToList();
        }

        void Remember((int, string) key, List<InputTick> route)
        {
            if (!_memo.TryGetValue(key, out List<List<InputTick>> list)) _memo[key] = list = new List<List<InputTick>>();
            if (list.Any(r => r.SequenceEqual(route))) return;
            list.Add(route);
            if (list.Count > 4) list.RemoveAt(0);
        }

        List<(string how, List<InputTick> inputs)> SeedStarts(SimSnapshot at, List<InputTick> prefix, int first, int limit)
        {
            var starts = new List<(string how, List<InputTick> inputs)>();
            var labels = new HashSet<string>(_labels.Skip(first).Take(limit - first));
            _eval.Restore(at);
            Movement player = _eval.Player;
            int level = !player.wallJumpUnlocked ? 0 : !player.dashUnlocked ? 1 : !player.doubleJumpUnlocked ? 2 : !player.blockSwapUnlocked ? 3 : 4;
            List<Seed> matching = _seeds.Where(s => s.Level <= level && s.Goals.Any(labels.Contains)).OrderByDescending(s => s.Level).ToList();
            if (matching.Count == 0) return starts;
            bool restart = prefix.Count == 0 || !prefix[prefix.Count - 1].reset;
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
                return false;
            }
            Log($"{title}: {chosen.how} reaches goal {top.Progress}/{_goals} {_labels[top.Progress - 1]} at "
                + (top.GoalPhysicsTicks[top.Progress - 1] * _dt).ToString("0.00", CultureInfo.InvariantCulture) + $"s ({reaching} of {starts.Count} starts reach it)");
            Route run = Splice(best, prefixLength, chosen.inputs, top.Progress, best);
            if (run == null || !Better(run, _best))
            {
                Log(title + ": the whole run with it does no better; dropped");
                return false;
            }
            Commit(run, "seeded: " + chosen.how);
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
                    : caps.GetString() == "wjdsdjbs" ? 4 : caps.GetString() == "wjdsdj" ? 3 : caps.GetString() == "wjds" ? 2 : caps.GetString() == "wj" ? 1 : 0,
                Inputs = Solve.ReadInputs(root),
            };
        }

        void Merge(string where, Route best, int prefixLength, int limit, List<List<InputTick>> found)
        {
            Route broken = null;
            foreach (List<InputTick> route in Enumerable.Reverse(found).Take(5))
            {
                Route candidate = Splice(best, prefixLength, route, limit, best);
                if (candidate == null) continue;
                if (Better(candidate, _best)) { Commit(candidate, where); return; }
                if (broken == null && candidate.Progress < best.Progress && candidate.Progress >= limit) broken = candidate;
            }
            if (broken != null) Repair(where, best, broken);
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
                    if (Better(run, _best)) Commit(run, where + ", repaired");
                    else Log($"repaired run {Describe(run)} is no better; dropped");
                    return;
                }
                if (run.Last >= _best.Last)
                {
                    Log($"repair dropped: goal {run.Progress}/{_goals} reached no sooner than the best's last goal");
                    return;
                }
                int reached = run.Progress, limit = Math.Min(reached + _window, old.Progress);
                int prefixLength = (int)Math.Min(run.GoalInput[reached - 1], run.Inputs.Count);
                var seeds = new List<List<InputTick>> { run.Inputs.Skip(prefixLength).ToList(), old.Inputs.Skip((int)old.GoalInput[reached - 1]).ToList() };
                List<List<InputTick>> found = SearchWindow("repair " + (reached + 1) + "/" + _goals, run, prefixLength, limit, seeds, long.MaxValue);
                if (found.Count == 0)
                {
                    Log($"repair dropped: no route to {_labels[reached]} from the new state");
                    return;
                }
                Route next = Splice(run, prefixLength, found[found.Count - 1], limit, old);
                if (next == null || next.Progress <= reached)
                {
                    Log("repair dropped: its route missed its goals when replayed from the start");
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
                        continue;
                    }
                    deferred = 0;
                    Log("gate check: " + (finished.GetBoolean() && root.TryGetProperty("time", out JsonElement time) && time.ValueKind == JsonValueKind.Number
                            ? "finish " + time.GetSingle().ToString("0.00", CultureInfo.InvariantCulture) + "s" : "no finish")
                        + (root.TryGetProperty("saved", out JsonElement saved) && saved.ValueKind == JsonValueKind.True ? " - saved as the new route" : " - not saved")
                        + (root.TryGetProperty("reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String ? " - " + reason.GetString() : ""));
                }
                catch (Exception ex)
                {
                    deferred = 0;
                    Log($"gate check skipped (game not reachable: {ex.GetBaseException().Message}); best kept on disk");
                }
            }
        }
    }
}
