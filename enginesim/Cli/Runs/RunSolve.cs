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
            RunContext run = RunContext.Open(args[0]);
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
            var lib = SegmentLibrary.Load(RunFiles.FileOf(run, ".segments.json"), seeds.Select(x => Path.Combine(run.Dir, "fulltas", x + ".segments.json")));
            var facts = new SegmentFacts(RunFiles.FileOf(run, ".failures.json"));
            var search = new SegmentSearch(run) { Stop = () => _stop };
            var plan = new RunPlan(run.HeaderIds, run.HeaderGoals.Select(g => g.label).ToArray());
            (float, float, bool) startAt = Solve.OnThread(() => { Simulation sim = run.Make(); sim.Restore(sim.Start); return (sim.PlayerWorld.x, sim.PlayerWorld.y, sim.Player.facingRight); });

            List<InputTick> route = new List<InputTick>();
            string from = "nothing (a new run)";
            foreach (string file in new[] { run.RunFile }.Concat(seeds.Select(x => Path.Combine(run.Dir, "fulltas", x + ".json"))).Where(File.Exists))
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
                route = Solve.ReadInputs(d.RootElement);
                from = Path.GetFileName(file);
                break;
            }
            Emit(new { type = "session", run = run.Name, goals = plan.Labels, ids = plan.Ids, threads, engine = BuildInfo.Commit, from, segments = lib.Count });
            Log($"solve-run {run.Name}: {plan.Count} goals, {threads} threads, engine {BuildInfo.Commit}, from {from} ({route.Count} inputs), {lib.Count} library segments");

            var improver = new Improver(run, lib, search, facts, threads) { Log = Log };
            var pending = new BlockingCollection<Built>(new ConcurrentQueue<Built>(), 1);
            Built submitted = null;
            // What the run file's route does on this engine, not what it recorded (a fidelity fix can have broken it).
            (int savedReached, double savedLive) = (0, double.MaxValue);
            if (File.Exists(run.RunFile) && from == Path.GetFileName(run.RunFile))
                (savedReached, savedLive) = Solve.OnThread(() =>
                {
                    Simulation sim = run.Make();
                    RunResult r = sim.Run(route, null, null, 1000000);
                    return (r.Progress, r.Progress == plan.Count ? Math.Round(MacroEngine.LiveSeconds(r.GoalPhysicsTicks[^1], r.GoalHeldFrames[^1]), 4) : double.MaxValue);
                });
            Log($"the run file's route reaches {savedReached} of {plan.Count} goals on this engine" + (savedLive < double.MaxValue ? $" in {savedLive:0.0000} s" : ""));
            improver.Event += Emit;
            improver.NewBest += (b, how) =>
            {
                // Never replace the run's best with a worse route (a start that harvests less than the file's route reaches).
                if (b.Complete ? b.Live < savedLive - 1e-4 || savedReached < b.Plan.Count : b.Reached > savedReached)
                {
                    string backup = RunFiles.FileOf(run, ".before-v2.json");
                    if (File.Exists(run.RunFile) && !File.Exists(backup)) File.Copy(run.RunFile, backup);
                    RunFiles.WriteRunFile(run, b, startAt);
                    savedReached = b.Reached;
                }
                RunFiles.WriteWaits(run, b);
                RunFiles.WriteFailure(run, b.Failure, b.Failure == null ? null : b.Plan.Ids[b.Failure.Goal]);
                lib.Save();
                Emit(new
                {
                    type = "record", how, complete = b.Complete, reached = b.Reached, of = b.Plan.Count, time = b.Reached == 0 ? (double?)null : b.Live,
                    ticks = b.Reached == 0 ? (long?)null : b.Physics[^1], waits = b.Waits.Sum(w => w.Ticks),
                });
                Emit(new { type = "waits", waits = b.Waits.OrderByDescending(w => w.Ticks).Take(50).Select(w => new { goal = w.Goal + 1, id = b.Plan.Ids[w.Goal], w.Label, x = Math.Round(w.X), y = Math.Round(w.Y), w.Ticks, w.Currency, w.Short }) });
                if (b.Failure != null) Emit(new { type = "failure", b.Failure.Kind, goal = b.Failure.Goal + 1, id = b.Plan.Ids[b.Failure.Goal], b.Failure.Label, b.Failure.Text });
                if (b.Complete && b.Live < savedLive - 1e-4)
                {
                    savedLive = b.Live;
                    Log($"offline best -> {Path.GetFileName(RunFiles.WriteOfflineBest(run, b))}");
                    if (submit) { while (pending.TryTake(out _)) { } pending.TryAdd(b); }
                }
            };
            if (submit) new Thread(() => Submitter(run, pending)) { IsBackground = true }.Start();
            improver.Run(plan, route, () => _stop);
            lib.Save();
            Emit(new { type = "done", run = run.Name, complete = improver.Best?.Complete ?? false, time = improver.Best?.Live, reached = improver.Best?.Reached ?? 0 });
            Log("solve-run: done" + (_stop ? " (stopped)" : ""));
            // Let a pending submission finish before the process ends.
            for (int i = 0; i < 600 && submit && pending.Count > 0; i++) Thread.Sleep(100);
            return 0;
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
                string body = JsonSerializer.Serialize(new { name = run.Name, ticks = b.Inputs.Select(t => t.ToJson()), goals, goalsFrom });
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
