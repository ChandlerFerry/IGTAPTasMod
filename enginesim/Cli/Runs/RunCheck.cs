using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    // Each run and trace replays in a child process: the engine is thread-static, and a crash must stay with its trace.
    // A baseline entry keeps its inputs' size:mtime; a recaptured trace or a newly saved best is a warning, not a failure,
    // because check judges the engine, not the routes.
    static class RunCheck
    {
        const long SmallTrace = 20L << 20;

        public static int Run(string[] args)
        {
            if (args.FirstOrDefault() == "--one-run") return OneRun(args[1], args[2]);
            string repo = Solve.Option<string>(args, "--repo", null) ?? TechGrafts.SourceRoot();
            if (repo == null || !Directory.Exists(Path.Combine(repo, "runs"))) { Console.Error.WriteLine("check: no source tree with runs\\ (--repo DIR)"); return 2; }
            string traces = Solve.Option<string>(args, "--traces", null) ?? TechGrafts.ResolveTechDir(".");
            bool accept = args.Contains("--accept"), full = args.Contains("--full"), runsOnly = args.Contains("--runs-only");
            string baselineFile = Path.Combine(repo, "enginesim", "check", "baseline.json");
            var clock = Stopwatch.StartNew();

            var jobs = new List<(string kind, string key, string stamp, string[] argv)>();
            foreach (string run in CompleteRuns(repo))
            {
                string trace = Path.Combine(traces, "fulltas-" + run + ".json"), best = Path.Combine(traces, "fulltas", run + ".json");
                if (!File.Exists(trace) || !File.Exists(best)) { jobs.Add(("run", run, null, null)); continue; }
                jobs.Add(("run", run, Stamp(trace) + "|" + Stamp(best), new[] { "check", "--one-run", trace, best }));
            }
            if (!runsOnly)
                foreach (string trace in Directory.EnumerateFiles(traces, "*.json").Where(f => !f.EndsWith(".world.json", StringComparison.OrdinalIgnoreCase))
                    .Where(f => File.Exists(Path.ChangeExtension(f, null) + ".world.json")).Where(f => full || new FileInfo(f).Length < SmallTrace)
                    .OrderBy(f => f, StringComparer.Ordinal))
                    jobs.Add(("trace", Path.GetFileName(trace), Stamp(trace), new[] { "replay", trace }));

            var results = new ConcurrentDictionary<string, string>();
            string exe = Environment.ProcessPath;
            Parallel.ForEach(jobs.Where(j => j.argv != null), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, j =>
                results[j.kind + ":" + j.key] = Child(exe, j.argv, j.kind));

            var now = new JsonObject { ["engine"] = BuildInfo.Commit, ["runs"] = new JsonObject(), ["traces"] = new JsonObject() };
            foreach (var j in jobs.Where(j => j.argv != null))
            {
                string got = results[j.kind + ":" + j.key];
                if (j.kind == "run")
                {
                    JsonNode row;
                    try { row = JsonNode.Parse(got); }
                    catch (JsonException) { row = new JsonObject { ["error"] = got }; }
                    row["stamp"] = j.stamp;
                    now["runs"][j.key] = row;
                }
                else now["traces"][j.key] = new JsonObject { ["stamp"] = j.stamp, ["line"] = got };
            }

            if (accept)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(baselineFile));
                File.WriteAllText(baselineFile, now.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                Console.WriteLine($"check: baseline written ({now["runs"].AsObject().Count} runs, {now["traces"].AsObject().Count} traces, {clock.Elapsed.TotalSeconds:0.0} s): {baselineFile}");
                return 0;
            }
            if (!File.Exists(baselineFile)) { Console.Error.WriteLine("check: no baseline yet: run check --accept and commit " + baselineFile); return 2; }
            JsonObject was = JsonNode.Parse(File.ReadAllText(baselineFile)).AsObject();
            int differ = 0, warned = 0, compared = 0;
            foreach (string section in new[] { "runs", "traces" })
            {
                if (runsOnly && section == "traces") continue;
                JsonObject before = was[section]?.AsObject() ?? new JsonObject(), after = now[section].AsObject();
                foreach (var (key, b) in before)
                {
                    if (!after.TryGetPropertyValue(key, out JsonNode a))
                    {
                        Console.WriteLine($"  skipped {section} {key}: not found now (no trace or best)");
                        warned++;
                        continue;
                    }
                    if ((string)b["stamp"] != (string)a["stamp"])
                    {
                        Console.WriteLine($"  changed since baseline {section} {key}: its inputs were recaptured or a new best saved (check --accept to re-baseline)");
                        warned++;
                        continue;
                    }
                    compared++;
                    string why = section == "runs" ? RunDiff(b, a) : (string)b["line"] == (string)a["line"] ? null : $"was: {b["line"]}\n      now: {a["line"]}";
                    if (why == null) continue;
                    differ++;
                    Console.WriteLine($"  DIFFERS {section} {key}: {why}");
                }
                int added = after.Count(kv => !before.ContainsKey(kv.Key));
                if (added > 0) Console.WriteLine($"  {added} {section} not in the baseline (new since it; check --accept adds them)");
            }
            Console.WriteLine($"check: {compared} compared, {differ} differ, {warned} warnings, engine {BuildInfo.Commit} against baseline {was["engine"]} ({clock.Elapsed.TotalSeconds:0.0} s)");
            return differ == 0 ? 0 : 1;
        }

        static IEnumerable<string> CompleteRuns(string repo)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(repo, "runs"), "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
                if (d.RootElement.TryGetProperty("section", out JsonElement s) && s.ValueKind == JsonValueKind.String && s.GetString() == "complete")
                    yield return Path.GetFileNameWithoutExtension(file);
            }
        }

        static string Stamp(string file)
        {
            var f = new FileInfo(file);
            return f.Length + ":" + f.LastWriteTimeUtc.Ticks;
        }

        static string Child(string exe, string[] argv, string kind)
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in argv) psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi);
            Task<string> err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            string[] lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            if (kind == "run") return lines.LastOrDefault(l => l.StartsWith("{", StringComparison.Ordinal)) ?? "error: " + Last(err.Result, output);
            string name = Path.GetFileName(argv[1]) + ": ";
            return lines.FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal)) ?? "error: " + Last(err.Result, output);
        }

        static string Last(string err, string output)
        {
            string text = (err.Trim().Length > 0 ? err : output).Trim();
            string first = text.Split('\n').FirstOrDefault()?.Trim() ?? "";
            return first.Length == 0 ? "no output" : first;
        }

        static string RunDiff(JsonNode b, JsonNode a)
        {
            if (b["error"] != null || a["error"] != null) return (string)b["error"] == (string)a["error"] ? null : $"error was {b["error"]}, now {a["error"]}";
            if ((int)b["of"] != (int)a["of"]) return $"its goals changed ({b["of"]} -> {a["of"]})";
            JsonArray gb = b["goals"].AsArray(), ga = a["goals"].AsArray();
            for (int g = 0; g < Math.Min(gb.Count, ga.Count); g++)
            {
                long[] x = gb[g].AsArray().Select(v => (long)v).ToArray(), y = ga[g].AsArray().Select(v => (long)v).ToArray();
                if (!x.SequenceEqual(y)) return $"goal {g + 1}: input/physics/held {string.Join("/", x)} -> {string.Join("/", y)}";
            }
            if ((int)b["reached"] != (int)a["reached"]) return $"reaches {b["reached"]} -> {a["reached"]} of {a["of"]} goals ({a["end"]})";
            if ((string)b["end"] != (string)a["end"]) return $"ends \"{b["end"]}\" -> \"{a["end"]}\"";
            if ((double)b["live"] != (double)a["live"]) return $"live {b["live"]} -> {a["live"]} s";
            return null;
        }

        static int OneRun(string trace, string best)
        {
            JsonElement header = Solve.ReadHeader(trace);
            string name = header.GetProperty("fulltas").GetProperty("name").GetString();
            Simulation sim = Solve.Creator(trace, header)();
            sim.StrictBuys = true;
            List<InputTick> inputs;
            using (JsonDocument saved = JsonDocument.Parse(File.ReadAllText(best))) inputs = Solve.ReadInputs(saved.RootElement);
            RunResult r = sim.Run(inputs, null, null, 1000000);
            int n = r.Progress;
            double live = n == 0 ? 0 : Math.Round(MacroEngine.LiveSeconds(r.GoalPhysicsTicks[n - 1], r.GoalHeldFrames[n - 1]), 4);
            var goals = Enumerable.Range(0, n).Select(g => new[] { r.GoalInputTicks[g], r.GoalPhysicsTicks[g], r.GoalHeldFrames[g] }).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new { run = name, reached = n, of = sim.Goals.Goals.Length, end = FullTasTracker.Unexplained(r.EndReason), live, goals }));
            return 0;
        }
    }
}
