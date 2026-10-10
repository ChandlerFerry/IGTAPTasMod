using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using IGTAP.EngineSim;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// searchbench &lt;suite.json&gt; [--seeds N] [--minutes M] [--threads T] [--arms {name: [solve args]}] [--traces dir] [--out file]
    /// [--keep]: runs `solve --rng s --no-submit --fresh --no-humanize` per course, arm and seed (s = 1..N) as a child process, each
    /// on its own temp copy of the start trace (so its archive and offline bests stay there) with its own tech-graft cache copy, and
    /// reports per arm the median/min/max of the wall time to the first finish, to the course's target time, and the final best.
    /// An arm's `--exe path` (taken out of its solve args) runs that build instead of this one: two builds benched on the same seeds.
    /// A suite with "cases" (searchbench --suite grafts) is the planted-waste suite: see <see cref="Case"/>.
    /// </summary>
    static class SearchBench
    {
        public static int Run(string[] args)
        {
            // --suite NAME: the build's searchbench-NAME.json (grafts: the planted-waste suite, below).
            string suitePath = Path.GetFullPath(args.Contains("--suite")
                ? Path.Combine(AppContext.BaseDirectory, "searchbench-" + Solve.Option(args, "--suite", "") + ".json") : args[0]);
            int seeds = Solve.Option(args, "--seeds", 3);
            double minutes = Solve.Option(args, "--minutes", 1.0);
            int threads = Solve.Option(args, "--threads", Environment.ProcessorCount);
            string armsArg = Solve.Option(args, "--arms", "{\"default\": []}");
            var arms = JsonSerializer.Deserialize<Dictionary<string, string[]>>(armsArg.TrimStart().StartsWith("{") ? armsArg : File.ReadAllText(armsArg));
            JsonNode suite = JsonNode.Parse(File.ReadAllText(suitePath));
            string traces = Solve.Option(args, "--traces", (string)suite["traces"] ?? TechGrafts.ResolveTechDir("."));
            string root = Path.Combine(Path.GetTempPath(), "igtap-searchbench-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            string outFile = Solve.Option(args, "--out", Path.Combine(root, suite["cases"] != null ? "searchbench.jsonl" : "searchbench.json"));
            bool keep = args.Contains("--keep");
            Directory.CreateDirectory(root);
            // One cache copy for the whole bench: the first run mines it if the build changed; the real folder's is never written.
            string techCache = Path.Combine(root, "tech-grafts.cache.json");
            string realCache = Path.Combine(traces, "tech-grafts.cache.json");
            if (File.Exists(realCache)) File.Copy(realCache, techCache);
            if (suite["cases"] != null) return Planted(args, suite, traces, root, outFile, techCache, realCache, seeds, minutes, threads, arms, keep);

            var results = new JsonArray();
            foreach (JsonNode course in suite["courses"].AsArray())
            {
                string name = (string)course["name"], trace = Path.Combine(traces, (string)course["trace"]);
                double target = (double)course["target"];
                string[] courseArgs = course["args"]?.AsArray().Select(a => (string)a).ToArray() ?? Array.Empty<string>();
                // The arms take turns seed by seed (seed 1 of each arm, then seed 2 ...), so a load that comes and goes on the
                // machine falls on every arm alike.
                var runsByArm = arms.Keys.ToDictionary(a => a, a => new List<(double? first, double? toTarget, double? best, JsonArray bests)>());
                for (int s = 1; s <= seeds; s++)
                {
                    foreach (var (arm, armSpec) in arms)
                    {
                        var runs = runsByArm[arm];
                        int exeAt = Array.IndexOf(armSpec, "--exe");
                        string exe = exeAt >= 0 ? Path.GetFullPath(armSpec[exeAt + 1]) : Environment.ProcessPath;
                        string[] armArgs = exeAt >= 0 ? armSpec.Where((_, i) => i != exeAt && i != exeAt + 1).ToArray() : armSpec;
                        // Another build mines under another cache key: its own copy, so the builds don't re-mine over each other.
                        string cache = exeAt >= 0 ? Path.Combine(root, "tech-grafts." + arm + ".cache.json") : techCache;
                        if (exeAt >= 0 && !File.Exists(cache) && File.Exists(realCache)) File.Copy(realCache, cache);
                        string dir = Path.Combine(root, name, arm, "seed" + s);
                        Directory.CreateDirectory(dir);
                        string copy = Path.Combine(dir, Path.GetFileName(trace));
                        File.Copy(trace, copy);
                        File.Copy(Path.ChangeExtension(trace, null) + ".world.json", Path.ChangeExtension(copy, null) + ".world.json");
                        string log = Path.Combine(dir, "bests.jsonl");
                        var solveArgs = new List<string> { "solve", copy, "--rng", s.ToString(CultureInfo.InvariantCulture), "--no-submit", "--fresh", "--no-humanize",
                            "--minutes", minutes.ToString(CultureInfo.InvariantCulture), "--threads", threads.ToString(CultureInfo.InvariantCulture),
                            "--log-json", log, "--tech-cache", cache };
                        solveArgs.AddRange(courseArgs);
                        solveArgs.AddRange(armArgs);
                        Console.WriteLine($"{name} / {arm} / seed {s}: {(exeAt >= 0 ? exe + " " : "")}{string.Join(" ", solveArgs.Skip(2))}");
                        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, WorkingDirectory = dir };
                        foreach (string a in solveArgs) start.ArgumentList.Add(a);
                        using (Process p = Process.Start(start))
                        using (FileStream output = File.Create(Path.Combine(dir, "solve.log")))
                        {
                            p.StandardOutput.BaseStream.CopyTo(output);
                            p.WaitForExit();
                            if (p.ExitCode != 0) throw new InvalidOperationException($"solve exited {p.ExitCode}: see {Path.Combine(dir, "solve.log")}");
                        }
                        var bests = new JsonArray();
                        foreach (string line in File.ReadAllLines(log).Where(l => l.Length > 0)) bests.Add(JsonNode.Parse(line));
                        var finishes = bests.Where(b => b["how"]?.GetValue<string>() != "seed").ToList();
                        double? first = finishes.Count == 0 ? null : (double)finishes[0]["wall"];
                        double? toTarget = finishes.Where(b => (double)b["time"] <= target + 1e-4).Select(b => (double?)(double)b["wall"]).FirstOrDefault();
                        double? best = bests.Count == 0 ? null : (double)bests[^1]["time"];
                        Console.WriteLine($"  first finish at {Show(first)} s, target {target:0.00} at {Show(toTarget)} s, best {Show(best)}");
                        runs.Add((first, toTarget, best, bests));
                        if (!keep) Directory.Delete(dir, true);
                    }
                }
                foreach (var (arm, runs) in runsByArm)
                {
                    results.Add(new JsonObject
                    {
                        ["course"] = name, ["arm"] = arm, ["target"] = target,
                        ["firstFinish"] = Stats(runs.Select(r => r.first)),
                        ["toTarget"] = Stats(runs.Select(r => r.toTarget)),
                        ["best"] = Stats(runs.Select(r => r.best)),
                        ["runs"] = new JsonArray(runs.Select((r, i) => (JsonNode)new JsonObject
                        {
                            ["rng"] = i + 1, ["firstFinish"] = r.first, ["toTarget"] = r.toTarget, ["best"] = r.best, ["bests"] = r.bests,
                        }).ToArray()),
                    });
                }
            }
            var report = new JsonObject
            {
                ["suite"] = suitePath, ["seeds"] = seeds, ["minutes"] = minutes, ["threads"] = threads,
                ["arms"] = JsonNode.Parse(JsonSerializer.Serialize(arms)), ["results"] = results,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
            File.WriteAllText(outFile, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"\n{"course",-14} {"arm",-12} {"first finish s (med min max)",-30} {"to target s (med min max)",-30} best (med min max)");
            foreach (JsonNode r in results)
                Console.WriteLine($"{(string)r["course"],-14} {(string)r["arm"],-12} {Row(r["firstFinish"]),-30} {Row(r["toTarget"]),-30} {Row(r["best"])}");
            Console.WriteLine("-: not reached in that run (counts as never, so a median of - means half the runs or more missed it). -> " + outFile);
            if (!keep)
            {
                foreach (string cache in Directory.GetFiles(root, "tech-grafts*.cache.json")) File.Delete(cache);
                foreach (string d in Directory.GetDirectories(root)) Directory.Delete(d, true);
            }
            return 0;
        }

        /// <summary>
        /// A planted-waste case (searchbench-grafts.json): a known-good stretch of a saved route (a course route trace's inputs, or a
        /// Full-TAS route's goals From+1..To) with a waste planted in it. Scores are course-timer ticks (a course) or the physics
        /// tick of goal To (a Full-TAS window). A case without a waste is open: the route as it is, to beat by Gain ticks.
        /// </summary>
        sealed class Case
        {
            public string Name, Waste, Trace, Invalid;
            public bool FullTas;
            public int From, To, At = -1, Rejoin;
            /// <summary>A Full-TAS case's window by its first and last goals' ids (the trace's: goals From+1..To from 1).</summary>
            public string FirstId, LastId;
            public long Original, Target, Seed, AtPhysics;
            /// <summary>The seed from the run's start (a Full-TAS one cut after goal To).</summary>
            public List<InputTick> Inputs;
        }

        static int Planted(string[] args, JsonNode suite, string traces, string root, string outFile, string techCache, string realCache,
            int seeds, double minutes, int threads, Dictionary<string, string[]> arms, bool keep)
        {
            int slack = Solve.Option(args, "--slack", (int?)suite["slack"] ?? 2);
            HashSet<string> only = args.Contains("--case") ? new HashSet<string>(Solve.Option(args, "--case", "").Split(',')) : null;
            var cases = new List<(Case c, JsonNode spec)>();
            foreach (JsonNode spec in suite["cases"].AsArray().Where(s => only == null || only.Contains((string)s["name"])))
            {
                Case c = Solve.OnThread(() => Prepare(traces, spec));
                cases.Add((c, spec));
                Console.WriteLine($"{c.Name}: {c.Waste}" + (c.Invalid != null ? " INVALID: " + c.Invalid
                    : $" {Window(c)}, original {c.Original}, seed {c.Seed} (+{c.Seed - c.Original}), target {c.Target}"
                        + (c.At >= 0 ? $", planted at input {c.At} of the window (physics tick {c.AtPhysics}), rejoin {c.Rejoin:+0;-0;0}" : "")));
            }
            // --check: plant and replay only, no search (after an engine change: which cases still plant, and where).
            if (args.Contains("--check")) return cases.Any(k => k.c.Invalid != null) ? 1 : 0;

            var lines = new List<JsonObject>();
            foreach (var (c, spec) in cases.Where(k => k.c.Invalid == null))
            {
                string caseDir = Path.Combine(root, c.Name);
                Directory.CreateDirectory(caseDir);
                string traceCopy = Path.Combine(caseDir, Path.GetFileName(c.Trace));
                WriteTrace(c, traceCopy);
                string[] caseArgs = spec["args"]?.AsArray().Select(a => (string)a).ToArray() ?? Array.Empty<string>();
                var runsByArm = arms.Keys.ToDictionary(a => a, a => new List<JsonObject>());
                for (int s = 1; s <= seeds; s++)
                    foreach (var (arm, armSpec) in arms)
                    {
                        int exeAt = Array.IndexOf(armSpec, "--exe");
                        string exe = exeAt >= 0 ? Path.GetFullPath(armSpec[exeAt + 1]) : Environment.ProcessPath;
                        string[] armArgs = exeAt >= 0 ? armSpec.Where((_, i) => i != exeAt && i != exeAt + 1).ToArray() : armSpec;
                        string cache = exeAt >= 0 ? Path.Combine(root, "tech-grafts." + arm + ".cache.json") : techCache;
                        if (exeAt >= 0 && !File.Exists(cache) && File.Exists(realCache)) File.Copy(realCache, cache);
                        string dir = Path.Combine(caseDir, arm, "seed" + s);
                        Directory.CreateDirectory(dir);
                        string copy = Path.Combine(dir, Path.GetFileName(traceCopy));
                        File.Copy(traceCopy, copy);
                        File.Copy(Path.ChangeExtension(c.Trace, null) + ".world.json", Path.ChangeExtension(copy, null) + ".world.json");
                        string log = Path.Combine(dir, "bests.jsonl");
                        string M = minutes.ToString(CultureInfo.InvariantCulture);
                        // A Full-TAS case polishes its one window (--goal To, --window To - From) for the whole budget.
                        var runArgs = c.FullTas
                            ? new List<string> { "fulltas", copy, "--goal", c.LastId, "--window", (c.To - c.From).ToString(CultureInfo.InvariantCulture),
                                "--window-minutes", M }
                            : new List<string> { "solve", copy, "--no-humanize", "--no-whole" };
                        runArgs.AddRange(new[] { "--rng", s.ToString(CultureInfo.InvariantCulture), "--no-submit", "--fresh", "--minutes", M,
                            "--threads", threads.ToString(CultureInfo.InvariantCulture), "--log-json", log, "--tech-cache", cache });
                        runArgs.AddRange(caseArgs);
                        runArgs.AddRange(armArgs);
                        Console.WriteLine($"{c.Name} / {arm} / seed {s}: {(exeAt >= 0 ? exe + " " : "")}{string.Join(" ", runArgs.Skip(2))}");
                        JsonObject run = RunCase(c, exe, runArgs, dir, log, slack, minutes);
                        run["rng"] = s;
                        Console.WriteLine($"  seed {Show(run["seed"])}, best {Show(run["best"])} (target {c.Target}{(c.Waste == "open" ? "" : " +" + slack)}): "
                            + ((bool)run["reached"] ? $"reached at {(double)run["cpu"]:0.0} CPU s, {(double)run["wall"]:0.0} s" : "not reached"));
                        runsByArm[arm].Add(run);
                        if (!keep) Directory.Delete(dir, true);
                    }
                foreach (var (arm, runs) in runsByArm)
                {
                    var line = new JsonObject
                    {
                        ["case"] = c.Name, ["waste"] = c.Waste, ["arm"] = arm, ["trace"] = Path.GetFileName(c.Trace),
                        ["goals"] = c.FullTas ? new JsonArray(c.FirstId, c.LastId) : null, ["window"] = c.FullTas ? new JsonArray(c.From + 1, c.To) : null, ["at"] = c.At >= 0 ? (int?)c.At : null, ["atPhysics"] = c.At >= 0 ? (long?)c.AtPhysics : null,
                        ["rejoin"] = c.At >= 0 ? (int?)c.Rejoin : null,
                        ["original"] = c.Original, ["seed"] = c.Seed, ["target"] = c.Target, ["slack"] = c.Waste == "open" ? 0 : slack,
                        ["reached"] = runs.Count(r => (bool)r["reached"]), ["runs"] = runs.Count,
                        ["cpu"] = Stats(runs.Select(r => (bool)r["reached"] ? (double?)r["cpu"] : null)),
                        ["wall"] = Stats(runs.Select(r => (bool)r["reached"] ? (double?)r["wall"] : null)),
                        ["best"] = Stats(runs.Select(r => (double?)(long?)r["best"])),
                        ["runsDetail"] = new JsonArray(runs.Select(r => (JsonNode)r).ToArray()),
                    };
                    Console.WriteLine("@searchbench " + line.ToJsonString());
                    lines.Add(line);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
            File.WriteAllLines(outFile, lines.Select(l => l.ToJsonString()));
            Console.WriteLine($"\n{"case",-22} {"waste",-9} {"window",-16} {"target",7} {"seed",12} {"reached",8}  {"CPU s to target (med min max)",-30} best - target (med min max)");
            foreach (var (c, _) in cases.Where(k => k.c.Invalid != null))
                Console.WriteLine($"{c.Name,-22} {c.Waste,-9} INVALID: {c.Invalid}");
            foreach (JsonObject l in lines)
            {
                long target = (long)l["target"];
                string over(JsonNode v) => v == null ? "-" : ((long)(double)v - target).ToString("+0;-0;0", CultureInfo.InvariantCulture);
                JsonNode best = l["best"];
                string window = l["window"] is JsonArray g ? $"goals {(int)g[0]}-{(int)g[1]}" : "course";
                Console.WriteLine($"{(string)l["case"] + (arms.Count > 1 ? "/" + (string)l["arm"] : ""),-22} {(string)l["waste"],-9} {window,-16} {target,7} "
                    + $"{$"{(long)l["seed"]} (+{(long)l["seed"] - target})",12} {$"{(int)l["reached"]}/{(int)l["runs"]}",8}  {Row(l["cpu"]),-30} {over(best["median"])} {over(best["min"])} {over(best["max"])}");
            }
            Console.WriteLine($"reached: the search's best within {slack} ticks of the original (open cases: at or under the target) in the budget ({minutes} min, "
                + $"{threads} threads, {seeds} seeds); CPU s from the seed's replay to that best (-: not reached). -> {outFile}");
            if (!keep)
            {
                foreach (string cache in Directory.GetFiles(root, "tech-grafts*.cache.json")) File.Delete(cache);
                foreach (string d in Directory.GetDirectories(root)) Directory.Delete(d, true);
            }
            return 0;
        }

        static string Window(Case c) => c.FullTas ? $"goals {c.From + 1}-{c.To}" : "course";

        /// <summary>One search of a case as a child process, stopped as soon as its log shows a best within the slack (the budget
        /// otherwise). CPU and wall count from the log's first line (the seed's replay: setup left out).</summary>
        static JsonObject RunCase(Case c, string exe, List<string> runArgs, string dir, string log, int slack, double minutes)
        {
            long goal = c.Waste == "open" ? c.Target : c.Target + slack;
            var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardInput = true, WorkingDirectory = dir };
            foreach (string a in runArgs) start.ArgumentList.Add(a);
            long? seed = null, best = null;
            double? cpu0 = null, wall0 = null, cpu = null, wall = null;
            int read = 0;
            using (Process p = Process.Start(start))
            using (FileStream output = File.Create(Path.Combine(dir, "solve.log")))
            {
                System.Threading.Tasks.Task copy = p.StandardOutput.BaseStream.CopyToAsync(output);
                var clock = Stopwatch.StartNew();
                bool done = false;
                while (!done)
                {
                    done = p.WaitForExit(200);
                    string text;
                    try
                    {
                        using var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var r = new StreamReader(f);
                        text = r.ReadToEnd();
                    }
                    catch (IOException) { continue; }
                    string[] complete = text.Split('\n');
                    for (; read < complete.Length - 1; read++)
                    {
                        JsonNode b = JsonNode.Parse(complete[read]);
                        long score = c.FullTas ? (long)b["runTicks"] : (long)Math.Round((double)b["time"] / 0.02);
                        double bCpu = (double?)b["cpu"] ?? p.TotalProcessorTime.TotalSeconds, bWall = (double)b["wall"];
                        cpu0 ??= bCpu;
                        wall0 ??= bWall;
                        seed ??= score;
                        best = Math.Min(best ?? long.MaxValue, score);
                        if (score <= goal && cpu == null)
                        {
                            cpu = bCpu - cpu0;
                            wall = bWall - wall0;
                        }
                    }
                    if (cpu != null && !done)
                    {
                        p.Kill(true);
                        p.WaitForExit();
                        done = true;
                    }
                    // Setup and the last window's save aside, a run past its budget is stuck: stopped.
                    else if (!done && clock.Elapsed.TotalMinutes > minutes + 5)
                    {
                        p.Kill(true);
                        p.WaitForExit();
                        done = true;
                    }
                }
                copy.Wait();
                if (cpu == null && seed == null && p.ExitCode != 0) throw new InvalidOperationException($"{runArgs[0]} exited {p.ExitCode}: see {Path.Combine(dir, "solve.log")}");
            }
            return new JsonObject
            {
                ["seed"] = seed, ["best"] = best, ["reached"] = cpu != null,
                ["cpu"] = cpu == null ? null : Math.Round(cpu.Value, 2), ["wall"] = wall == null ? null : Math.Round(wall.Value, 2),
            };
        }

        /// <summary>
        /// Replays the case's route, plants its waste at the first input of its "at" range where the planted route still gets
        /// there at least minCost (default 5) ticks later (a bonk only where the jump hits a ceiling), and keeps that as the seed.
        /// </summary>
        static Case Prepare(string traces, JsonNode spec)
        {
            var c = new Case { Name = (string)spec["name"], Waste = (string)spec["waste"] ?? "open", Trace = Path.Combine(traces, (string)spec["trace"]) };
            JsonElement header = Solve.ReadHeader(c.Trace);
            c.FullTas = header.TryGetProperty("fulltas", out _);
            List<InputTick> route = Plan.ReadRoute(spec["route"] != null ? Path.Combine(traces, (string)spec["route"]) : c.Trace);
            Simulation sim = Solve.Creator(c.Trace, header)();
            if (c.FullTas)
            {
                // The window by its first and last goals' ids (the trace's).
                string[] ids = FullTas.HeaderGoals(header.GetProperty("fulltas")).Ids;
                c.FirstId = (string)spec["goals"][0];
                c.LastId = (string)spec["goals"][1];
                c.From = Array.IndexOf(ids, c.FirstId);
                c.To = Array.IndexOf(ids, c.LastId) + 1;
                if (c.From < 0 || c.To <= c.From) throw new InvalidOperationException($"searchbench case {c.Name}: goals {c.FirstId}..{c.LastId} aren't a window of {Path.GetFileName(c.Trace)}'s goals");
                sim.StrictBuys = true;
                sim.Goals.Limit = c.To;
            }
            long? Score(RunResult r) => c.FullTas ? (r.Progress >= c.To ? r.GoalPhysicsTicks[c.To - 1] : null)
                : r.Finished ? (long)Math.Round(r.Time / 0.02) : null;
            var physics = new long[route.Count + 1];
            RunResult original = sim.Run(route, null, null, 1000000, _ =>
            {
                long i = sim.Engine.RunTicks - 1;
                if (i >= 0 && i < route.Count && physics[i] == 0) physics[i] = sim.Engine.PhysicsTicks;
            });
            if (Score(original) is not long score)
            {
                c.Invalid = "the route doesn't " + (c.FullTas ? "reach goal " + c.To : "finish") + ": " + original.EndReason;
                return c;
            }
            c.Original = score;
            int from = c.From == 0 ? 0 : (int)original.GoalInputTicks[c.From - 1];
            if (c.FullTas) route = route.GetRange(0, Math.Min(route.Count, (int)original.GoalInputTicks[c.To - 1] + 1));
            // The window's runs start after the prefix, on window inputs (the goals' input ticks stay the run's).
            if (from > 0) sim.SetStart(sim.Advance(route.GetRange(0, from)), from);
            if (c.Waste == "open")
            {
                (c.Target, c.Seed, c.Inputs) = (c.Original - ((int?)spec["gain"] ?? 1), c.Original, route);
                return c;
            }
            c.Target = c.Original;
            int minCost = (int?)spec["minCost"] ?? 5, maxCost = (int?)spec["maxCost"] ?? 100, hold = (int?)spec["hold"] ?? 8;
            JsonArray range = spec["at"].AsArray(), rejoin = spec["rejoin"]?.AsArray();
            // The rejoin shifts in order 0, 1, -1, 2, -2 ... 8, -8 (a "rejoin" [lo, hi] instead: those, in order).
            IEnumerable<int> Shifts() => rejoin != null ? Enumerable.Range((int)rejoin[0], (int)rejoin[1] - (int)rejoin[0] + 1)
                : Enumerable.Range(0, 17).Select(k => (k + 1) / 2 * (k % 2 == 1 ? 1 : -1));
            for (int at = (int)range[0]; at <= (int)range[1] && from + at < route.Count; at++)
            {
                int i = from + at;
                (List<InputTick> waste, int j) = Plant(route, i, c.Waste, spec);
                if (waste == null) continue;
                foreach (int m in Shifts())
                {
                    List<InputTick> planted = Rejoin(waste, j, m);
                    if (planted == null) continue;
                    // A bonk: rising at 150+ one physics tick, not rising the next, in the air, in the ticks after the press.
                    bool bonked = false;
                    float vy = 0f;
                    RunResult r = sim.Run(planted.GetRange(from, planted.Count - from), null, null, 1000000, c.Waste != "bonk" ? null : _ =>
                    {
                        long run = from + sim.Engine.RunTicks - 1;
                        float now = sim.Player.Velocity.y;
                        if (run >= i && run <= i + hold + 15 && vy >= 150f && now <= 0f && !sim.Player.onGround) bonked = true;
                        vy = now;
                    });
                    if (c.Waste == "bonk" && !bonked) break;
                    if (Score(r) is not long s || s - c.Original < minCost || s - c.Original > maxCost) continue;
                    (c.At, c.Rejoin, c.AtPhysics, c.Seed) = (at, m, physics[i], s);
                    c.Inputs = c.FullTas ? planted.GetRange(0, Math.Min(planted.Count, (int)r.GoalInputTicks[c.To - 1] + 1)) : planted;
                    return c;
                }
            }
            c.Invalid = $"no input {range[0]}..{range[1]} of the window plants it {minCost}-{maxCost} ticks slower and still " + (c.FullTas ? "reaching goal " + c.To : "finishing");
            return c;
        }

        /// <summary>
        /// The route with a waste planted at input i, and the input the original goes on from after it (null where the waste
        /// doesn't apply): dashrun (a super chain played as dashes and a run: from a super at i, len ticks (default 12) with every
        /// dash's jump and every jump press dropped), bonk (a jump pressed at i and held hold ticks (default 8), on a walk: no jump
        /// or dash there), dashpair (a dash back and a dash forward inserted at i, running either way), restart (a quick restart
        /// inserted at i, then the inputs since the last restart before i, or the start, played again: a filler lap).
        /// </summary>
        static (List<InputTick>, int) Plant(List<InputTick> route, int i, string waste, JsonNode spec)
        {
            var r = new List<InputTick>(route);
            InputTick t;
            switch (waste)
            {
                case "dashrun":
                    if (!r[i].dash || r[i].dashJump == 0) return (null, 0);
                    int end = Math.Min(r.Count, i + ((int?)spec["len"] ?? 12));
                    for (int k = i; k < end; k++)
                    {
                        t = r[k];
                        (t.dashJump, t.held, t.press, t.release) = (0, false, false, i > 0 && k == i && r[i - 1].held);
                        r[k] = t;
                    }
                    return (r, end);
                case "bonk":
                    int hold = (int?)spec["hold"] ?? 8;
                    if (i + hold >= r.Count || r.Skip(Math.Max(0, i - 1)).Take(hold + 2).Any(k => k.held || k.press || k.release || k.dash)) return (null, 0);
                    for (int k = i; k <= i + hold; k++)
                    {
                        t = r[k];
                        (t.held, t.press, t.release) = (k < i + hold, k == i, k == i + hold);
                        r[k] = t;
                    }
                    return (r, i + hold + 1);
                case "dashpair":
                    float dir = Math.Sign(r[i].x);
                    if (dir == 0f || r[i].dash) return (null, 0);
                    InputTick back = new InputTick { x = -dir, held = i > 0 && r[i - 1].held && !r[i - 1].release, dash = true }, forth = back;
                    forth.x = dir;
                    r.InsertRange(i, new[] { back, forth });
                    return (r, i + 2);
                case "restart":
                    int last = i == 0 ? -1 : r.FindLastIndex(i - 1, k => k.reset);
                    t = r[i];
                    t.reset = true;
                    List<InputTick> again = last >= 0 ? r.GetRange(last, i - last) : new[] { t }.Concat(r.GetRange(0, i)).ToList();
                    r.InsertRange(i, again);
                    return (r, i + again.Count);
                default: throw new ArgumentException("unknown waste " + waste);
            }
        }

        /// <summary>A planted route whose original inputs go on m inputs later (m &gt; 0: m of them dropped) or sooner (m &lt; 0: -m
        /// ticks of the stick there, no new edge, first) after the waste, so it can finish again; a hold that goes on after a
        /// released waste presses at the join.</summary>
        static List<InputTick> Rejoin(List<InputTick> planted, int j, int m)
        {
            if (j + Math.Max(m, 0) >= planted.Count) return null;
            var r = new List<InputTick>(planted);
            if (m > 0) r.RemoveRange(j, m);
            else if (m < 0) r.InsertRange(j, Enumerable.Repeat(r[j].Coast(), -m));
            if (j > 0 && r[j].held && !r[j].press && !r[j - 1].held)
            {
                InputTick t = r[j];
                t.press = true;
                r[j] = t;
            }
            return r;
        }

        /// <summary>The case's trace with its seed as the trace's inputs (what solve and fulltas seed from); a Full-TAS trace's goals
        /// cut after goal To, so the window's goal is the run's last.</summary>
        static void WriteTrace(Case c, string path)
        {
            JsonNode trace = JsonNode.Parse(File.ReadAllText(c.Trace), null, new JsonDocumentOptions { MaxDepth = 256 });
            trace["inputs"] = new JsonArray(c.Inputs.Select(t => JsonSerializer.SerializeToNode(t.ToJson())).ToArray());
            if (c.FullTas)
            {
                JsonArray goals = trace["fulltas"]["goals"].AsArray();
                trace["fulltas"]["goals"] = new JsonArray(goals.Take(c.To).Select(g => g.DeepClone()).ToArray());
            }
            File.WriteAllText(path, trace.ToJsonString());
        }

        static string Show(JsonNode x) => x == null ? "-" : x.ToString();

        // Median, min and max over every run, a run that never got there counting as never (null, above every number).
        static JsonObject Stats(IEnumerable<double?> values)
        {
            List<double> v = values.Select(x => x ?? double.PositiveInfinity).OrderBy(x => x).ToList();
            double median = v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
            double? N(double x) => double.IsInfinity(x) ? null : Math.Round(x, 3);
            return new JsonObject { ["median"] = N(median), ["min"] = N(v[0]), ["max"] = N(v[^1]), ["reached"] = v.Count(x => !double.IsInfinity(x)) };
        }

        static string Show(double? x) => x.HasValue ? x.Value.ToString("0.00", CultureInfo.InvariantCulture) : "-";

        static string Row(JsonNode s) => $"{Show((double?)s["median"])} {Show((double?)s["min"])} {Show((double?)s["max"])} ({(int)s["reached"]} reached)";
    }
}
