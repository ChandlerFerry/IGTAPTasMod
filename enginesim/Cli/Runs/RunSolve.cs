using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    // `solve-run <trace> [--threads N]`: one command, nothing to decide. It starts from the run's best (or its seed runs'),
    // builds the rest of the run, then improves it until it converges or "stop" arrives on stdin.
    static class RunSolve
    {
        static readonly JsonSerializerOptions EventJson = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        static volatile bool _stop;
        static readonly Stopwatch Clock = Stopwatch.StartNew();

        static void Emit(object e) { lock (EventJson) Console.WriteLine("@fulltas " + JsonSerializer.Serialize(e, EventJson)); }
        static void Log(string m) { lock (EventJson) Console.WriteLine($"[{Clock.Elapsed:hh\\:mm\\:ss}] {m}"); }

        public static int Run(string[] args)
        {
            if (args.Length == 0) { Console.Error.WriteLine("usage: solve-run <fulltas trace> [--threads N]"); return 2; }
            RunContext run = RunContext.Open(args[0], Glitches.Parse(Solve.Option(args, "--class", "major")));
            int threads = Solve.Option(args, "--threads", Math.Max(1, Environment.ProcessorCount - 2));
            bool submit = !args.Contains("--no-submit");
            new Thread(WatchStdin) { IsBackground = true }.Start();

            string def = RunFiles.RunDef(run);
            string[] seeds = Array.Empty<string>();
            if (def != null)
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(def));
                if (d.RootElement.TryGetProperty("seeds", out JsonElement s)) seeds = s.EnumerateArray().Select(x => x.GetString()).ToArray();
            }
            // One library for every class of a run: a segment is only ever used where it replays, under the class's own rules.
            var lib = SegmentLibrary.Load(Path.Combine(run.Dir, "fulltas", run.Name + ".segments.json"), seeds.Select(x => Path.Combine(run.Dir, "fulltas", x + ".segments.json")));
            var facts = new SegmentFacts(RunFiles.FileOf(run, ".failures.json"));
            var search = new SegmentSearch(run) { Stop = () => _stop };
            var plan = new RunPlan(run.HeaderIds, run.HeaderGoals.Select(g => g.label).ToArray());
            (float, float, bool) startAt = Solve.OnThread(() => { Simulation sim = run.Make(); sim.Restore(sim.Start); return (sim.PlayerWorld.x, sim.PlayerWorld.y, sim.Player.facingRight); });

            List<InputTick> route = new List<InputTick>();
            string from = "nothing (a new run)";
            // A class run with no route of its own starts from the run's major best.
            foreach (string file in new[] { run.RunFile, Path.Combine(run.Dir, "fulltas", run.Name + ".json") }.Concat(seeds.Select(x => Path.Combine(run.Dir, "fulltas", x + ".json"))).Where(File.Exists))
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
                route = Solve.ReadInputs(d.RootElement);
                from = Path.GetFileName(file);
                // The run's own best keeps its plan's goals: an incomplete best never reaches the gate, so the header doesn't have them.
                if (file == run.RunFile && d.RootElement.TryGetProperty("goals", out JsonElement saved))
                {
                    var own = new RunPlan(saved.EnumerateArray().Select(g => g.GetProperty("id").GetString()).ToArray(),
                        saved.EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray());
                    if (!own.Same(plan) && FullTasGoal.EditRefused(plan.Labels, own.Labels) == null) plan = own;
                }
                break;
            }
            Log($"solve-run {run.Name}: {plan.Count} goals, {threads} threads, engine {BuildInfo.Commit}, from {from} ({route.Count} inputs), {lib.Count} library segments");
            var improver = new Improver(run, lib, search, facts, threads) { Log = Log };
            var pending = new BlockingCollection<Built>(new ConcurrentQueue<Built>(), 1);
            // What the starting route does on this engine, not what it recorded (a fidelity fix can have broken it).
            (int savedReached, double savedLive, object startJson) = (0, double.MaxValue, new { progress = 0, time = (double?)null, splits = Array.Empty<double>() });
            if (route.Count > 0)
                (savedReached, savedLive, startJson) = Solve.OnThread(() =>
                {
                    Simulation sim = run.Make();
                    sim.UseGoals(sim.Goals.For(run.GoalsOf(plan)));
                    RunResult r = sim.Run(route, null, null, 1000000);
                    double[] splits = Enumerable.Range(0, r.Progress).Select(g => Math.Round(MacroEngine.LiveSeconds(r.GoalPhysicsTicks[g], r.GoalHeldFrames[g]), 4)).ToArray();
                    return (r.Progress, r.Progress == plan.Count ? splits[^1] : double.MaxValue, (object)new { progress = r.Progress, time = r.Progress == 0 ? (double?)null : splits[^1], splits });
                });
            Log($"the starting route reaches {savedReached} of {plan.Count} goals on this engine" + (savedLive < double.MaxValue ? $" in {savedLive:0.0000} s" : ""));
            // A seed run's route isn't this run's file: any route of this run's own beats having none.
            if (from != Path.GetFileName(run.RunFile)) (savedReached, savedLive) = (0, double.MaxValue);
            Emit(new { type = "session", run = run.Name, goals = plan.Ids.Select((id, i) => new { id, goal = plan.Labels[i] }), threads, engine = BuildInfo.Commit, from, segments = lib.Count, best = startJson });
            object last = startJson;
            improver.Event += Emit;
            improver.NewBest += (b, how) =>
            {
                // Never replace the run's best with a worse route (a start that harvests less than the file's route reaches), nor
                // keep one its class doesn't allow (a major span in an nmg run).
                string refused = Glitches.NoMajors(run.Class) && (b.Complete ? b.Live < savedLive - 1e-4 || savedReached < b.Plan.Count : b.Reached > savedReached) ? MajorSpan(run, b) : null;
                if (refused != null) Log($"not kept for {Glitches.Key(run.Class)}: {refused}");
                if (refused == null && (b.Complete ? b.Live < savedLive - 1e-4 || savedReached < b.Plan.Count : b.Reached > savedReached))
                {
                    string backup = RunFiles.FileOf(run, ".before-v2.json");
                    if (File.Exists(run.RunFile) && !File.Exists(backup)) File.Copy(run.RunFile, backup);
                    RunFiles.WriteRunFile(run, b, startAt);
                    savedReached = b.Reached;
                }
                RunFiles.WriteWaits(run, b);
                RunFiles.WriteFailure(run, b.Failure, b.Failure == null ? null : b.Plan.Ids[b.Failure.Goal]);
                lib.Save();
                object now = RouteJson(b);
                Emit(new { type = "record", how, previous = last, best = now, complete = b.Complete, waits = b.Waits.Sum(w => w.Ticks) });
                last = now;
                Emit(new { type = "waits", waits = b.Waits.OrderByDescending(w => w.Ticks).Take(50).Select(w => new { goal = w.Goal + 1, id = b.Plan.Ids[w.Goal], label = w.Label, x = Math.Round(w.X), y = Math.Round(w.Y), ticks = w.Ticks, currency = w.Currency, @short = w.Short }) });
                if (b.Failure != null) Emit(new { type = "failure", kind = b.Failure.Kind, goal = b.Failure.Goal + 1, id = b.Plan.Ids[b.Failure.Goal], label = b.Failure.Label, text = b.Failure.Text });
                if (refused == null && b.Complete && b.Live < savedLive - 1e-4)
                {
                    savedLive = b.Live;
                    Log($"offline best -> {Path.GetFileName(RunFiles.WriteOfflineBest(run, b))}");
                    if (submit) { while (pending.TryTake(out _)) { } pending.TryAdd(b); }
                }
            };
            if (submit) new Thread(() => Submitter(run, pending)) { IsBackground = true }.Start();
            string segment = Solve.Option<string>(args, "--segment", null);
            if (segment != null)
            {
                int goal = Array.IndexOf(plan.Ids, segment);
                if (goal < 0) { Console.Error.WriteLine($"solve-run: {run.Name} has no goal with id {segment}"); return 2; }
                improver.RunSegment(plan, route, goal, () => _stop);
            }
            else improver.Run(plan, route, () => _stop);
            lib.Save();
            Emit(new { type = "done", run = run.Name, best = improver.Best == null ? last : RouteJson(improver.Best) });
            Log("solve-run: done" + (_stop ? " (stopped)" : ""));
            // Let a pending submission finish before the process ends.
            for (int i = 0; i < 600 && submit && pending.Count > 0; i++) Thread.Sleep(100);
            return 0;
        }

        static object RouteJson(Built b) => new
        {
            progress = b.Reached, time = b.Reached == 0 ? (double?)null : b.Live, ticks = b.Reached == 0 ? (long?)null : b.Physics[^1],
            splits = Enumerable.Range(0, b.Reached).Select(g => Math.Round(MacroEngine.LiveSeconds(b.Physics[g], b.Held[g]), 4)).ToArray(),
        };

        // The engine's tech checks on the route, as the gate runs them: a class without major techs keeps no route with a major span.
        static string MajorSpan(RunContext run, Built b)
        {
            string file = Path.Combine(Path.GetTempPath(), $"solve-run-{Environment.ProcessId}-techs.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new { inputs = b.Inputs.Select(t => t.ToJson()), goals = b.Plan.Ids.Select((id, i) => new { id, goal = b.Plan.Labels[i] }) }));
            var psi = new ProcessStartInfo(Environment.ProcessPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in new[] { "techs-used", run.Trace, file, "--json", "--no-difficulty" }) psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            File.Delete(file);
            string line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{", StringComparison.Ordinal));
            if (line == null) return "techs-used gave no result: " + output.Trim();
            using JsonDocument d = JsonDocument.Parse(line);
            foreach (JsonElement span in d.RootElement.GetProperty("spans").EnumerateArray())
                if (span.GetProperty("category").GetString() == Glitches.MajorCategory)
                    return $"it uses {span.GetProperty("tech").GetString()} (major) at input tick {span.GetProperty("tick")}";
            return null;
        }

        static void WatchStdin()
        {
            try
            {
                for (string line; (line = Console.In.ReadLine()) != null;)
                    if (line.Trim() == "stop") { Log("stop requested: ending after the current batch"); _stop = true; return; }
            }
            catch (IOException) { }
        }

        // The game judges every strictly faster complete run (turbo from a fresh new game); its goals go along, so the gate adopts
        // a plan's economy goals when the run's list still is the one they were made from.
        static void Submitter(RunContext run, BlockingCollection<Built> pending)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            foreach (Built b in pending.GetConsumingEnumerable())
            {
                string[] fromIds = run.HeaderIds.ToArray(), fromLabels = run.HeaderGoals.Select(g => g.label).ToArray();
                RunFiles.InstallGoals(run, b.Plan);
                object goals = b.Plan.Ids.Select((id, i) => new { id, goal = b.Plan.Labels[i] }).ToArray();
                object goalsFrom = new object[] { fromIds.Select((id, i) => new { id, goal = fromLabels[i] }).ToArray() };
                string body = run.Class == RunClass.Major
                    ? JsonSerializer.Serialize(new { name = run.Name, ticks = b.Inputs.Select(t => t.ToJson()), goals, goalsFrom })
                    : JsonSerializer.Serialize(new { name = run.Name, @class = Glitches.Key(run.Class), ticks = b.Inputs.Select(t => t.ToJson()), goals, goalsFrom });
                for (int attempt = 1; attempt <= 30; attempt++)
                {
                    try
                    {
                        using JsonDocument r = JsonDocument.Parse(Solve.Post(http, "/api/fulltas/submit", body));
                        JsonElement root = r.RootElement;
                        if (!root.TryGetProperty("finished", out JsonElement finished)) { Thread.Sleep(2000); continue; }
                        bool saved = root.TryGetProperty("saved", out JsonElement s) && s.ValueKind == JsonValueKind.True;
                        double? time = root.TryGetProperty("time", out JsonElement t) && t.ValueKind == JsonValueKind.Number ? Math.Round(t.GetDouble(), 4) : null;
                        string reason = root.TryGetProperty("reason", out JsonElement why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
                        Log($"gate: {(finished.GetBoolean() ? "finish " + time : "no finish")}{(saved ? ", saved" : ", not saved")}{(reason != null ? " - " + reason : "")}");
                        Emit(new { type = "gate", finished = finished.GetBoolean(), time, saved, reason });
                        if (!Confirm.Compare(b, root, Emit, Log))
                        {
                            Confirm.Investigate(run, b, http, Emit, Log);
                            Log("the engine and the game disagree: nothing more is built on this engine (see the run's .confirm.json)");
                            Emit(new { type = "failure", kind = "disagree", text = "the game's replay parts from the engine's; fix the engine (check, the fidelity log) before solving on" });
                            _stop = true;
                        }
                        break;
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is AggregateException || ex is TaskCanceledException)
                    {
                        Log($"gate: the game isn't reachable ({ex.GetBaseException().Message}); the best is kept on disk");
                        Emit(new { type = "note", level = "warn", text = "Couldn't reach the game to judge the complete route; it's kept on disk." });
                        break;
                    }
                }
            }
        }
    }
}
