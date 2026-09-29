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
    /// </summary>
    static class SearchBench
    {
        public static int Run(string[] args)
        {
            string suitePath = Path.GetFullPath(args[0]);
            int seeds = Solve.Option(args, "--seeds", 3);
            double minutes = Solve.Option(args, "--minutes", 1.0);
            int threads = Solve.Option(args, "--threads", Environment.ProcessorCount);
            string armsArg = Solve.Option(args, "--arms", "{\"default\": []}");
            var arms = JsonSerializer.Deserialize<Dictionary<string, string[]>>(armsArg.TrimStart().StartsWith("{") ? armsArg : File.ReadAllText(armsArg));
            JsonNode suite = JsonNode.Parse(File.ReadAllText(suitePath));
            string traces = Solve.Option(args, "--traces", (string)suite["traces"] ?? TechGrafts.ResolveTechDir("."));
            string root = Path.Combine(Path.GetTempPath(), "igtap-searchbench-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            string outFile = Solve.Option(args, "--out", Path.Combine(root, "searchbench.json"));
            bool keep = args.Contains("--keep");
            Directory.CreateDirectory(root);
            // One cache copy for the whole bench: the first run mines it if the build changed; the real folder's is never written.
            string techCache = Path.Combine(root, "tech-grafts.cache.json");
            string realCache = Path.Combine(traces, "tech-grafts.cache.json");
            if (File.Exists(realCache)) File.Copy(realCache, techCache);

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
