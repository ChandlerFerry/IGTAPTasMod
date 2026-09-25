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
    // Offline solver driver: N worker threads (one world each) from a trace's start state; every new best is
    // written to disk and, unless --no-submit, replayed in the running game's turbo (which saves it as the category's
    // route when the game agrees it is faster). A seed's own time is not new, so it never goes to the game.
    static class Solve
    {
        const string Api = "http://127.0.0.1:17426";

        public static int Run(string[] args)
        {
            string startTrace = args[0];
            int threads = Option(args, "--threads", Environment.ProcessorCount);
            double minutes = Option(args, "--minutes", 60.0);
            // Stop early once this many minutes pass without a new best (after the first finish).
            double stallMinutes = Option(args, "--stall-minutes", double.PositiveInfinity);
            bool submit = !args.Contains("--no-submit");
            var seedPaths = new List<string>();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--seed") seedPaths.Add(args[i + 1]);

            JsonElement header;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(startTrace), new JsonDocumentOptions { MaxDepth = 256 }))
                header = doc.RootElement.Clone();
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            // A chain segment's start trace (TasPlugin.EngineSimTrace): its bests are the segment's, submitted as {chain, segment}.
            string chain = header.TryGetProperty("chain", out JsonElement chainName) ? chainName.GetString() : null;
            int segment = chain != null ? header.GetProperty("segment").GetInt32() : -1;
            var seeds = new List<List<InputTick>> { ReadInputs(header) };
            foreach (string path in seedPaths)
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 256 }))
                    seeds.Add(ReadInputs(doc.RootElement));

            string worldPath = Path.ChangeExtension(startTrace, null) + ".world.json";
            JsonElement start = header.GetProperty("start");
            Func<Simulation> create = () =>
            {
                var sim = new Simulation(World.Load(worldPath));
                sim.SetTarget(header);
                sim.LoadStart(start);
                return sim;
            };
            string outDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(startTrace)), "offline-best");
            Directory.CreateDirectory(outDir);
            string label = chain != null ? "chain-" + chain + "-" + segment + "_" + caps : course + "_" + caps;
            var target = new Target(course, caps, chain, segment);
            var solver = new OfflineSolver(create, caps, seeds);
            var clock = Stopwatch.StartNew();
            var pending = new BlockingQueue();
            TimeSpan lastBest = TimeSpan.Zero;
            solver.Improved += (time, inputs, how) =>
            {
                lastBest = clock.Elapsed;
                string file = Path.Combine(outDir, label + "-" + time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(file, JsonSerializer.Serialize(new { course, capabilities = caps, chain, segment, time, ticks = inputs.Select(ToJson) }));
                Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] offline best {time:0.00}s via {how} ({inputs.Count} ticks) -> {Path.GetFileName(file)}");
                // A chain segment's seeds come from elsewhere (standalone routes, stale ones), so its first finish is new too.
                if (submit && (how != "seed" || chain != null)) pending.Set(time, inputs);
            };
            Console.WriteLine($"Solving {(chain != null ? $"chain {chain} segment {segment} (course {course})" : $"course {course}")} ({caps}) on {threads} threads from {Path.GetFileName(startTrace)} with {seeds.Count} seed routes");
            solver.Start(threads);
            Thread confirmer = submit ? new Thread(() => Confirm(target, pending, clock)) { IsBackground = true } : null;
            confirmer?.Start();
            long lastRuns = 0, lastTicks = 0;
            double lastSeconds = 0;
            while (clock.Elapsed.TotalMinutes < minutes
                && (float.IsInfinity(solver.Shared.Time) || (clock.Elapsed - lastBest).TotalMinutes < stallMinutes))
            {
                Thread.Sleep(10000);
                double s = clock.Elapsed.TotalSeconds;
                long runs = Interlocked.Read(ref solver.Runs), ticks = Interlocked.Read(ref solver.Ticks);
                Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {(runs - lastRuns) / (s - lastSeconds):0} runs/s, {(ticks - lastTicks) / (s - lastSeconds):0} ticks/s, "
                    + $"{runs} runs, {Interlocked.Read(ref solver.Finishes)} finishes, best {solver.Shared.Time:0.00}s, branch replays matching "
                    + $"{Interlocked.Read(ref solver.BranchMatches)}/{Interlocked.Read(ref solver.BranchChecks)}, heap {GC.GetTotalMemory(false) >> 20} MB, ws {Environment.WorkingSet >> 20} MB");
                lastRuns = runs; lastTicks = ticks; lastSeconds = s;
            }
            solver.Stop();
            Console.WriteLine("Done. Best " + solver.Shared.Time.ToString("0.00", CultureInfo.InvariantCulture) + "s");
            return 0;
        }

        static object ToJson(InputTick t) { return new { t.x, t.held, t.press, t.release, t.dash, t.dashJump }; }

        // What a submit is for: a course's category, or a chain segment (judged in its chain's category only).
        sealed record Target(int Course, string Caps, string Chain, int Segment)
        {
            public string Body(string caps, List<InputTick> inputs)
            {
                return Chain != null
                    ? JsonSerializer.Serialize(new { chain = Chain, segment = Segment, ticks = inputs.Select(ToJson) })
                    : JsonSerializer.Serialize(new { course = Course, capabilities = caps, ticks = inputs.Select(ToJson) });
            }
        }

        static List<InputTick> ReadInputs(JsonElement trace)
        {
            // A trace has "inputs"; solve's own best files (offline-best/*.json) have "ticks".
            JsonElement list = trace.TryGetProperty("inputs", out JsonElement inputs) ? inputs : trace.GetProperty("ticks");
            return list.EnumerateArray().Select(t => new InputTick
            {
                x = t.GetProperty("x").GetSingle(),
                held = t.GetProperty("held").GetBoolean(),
                press = t.GetProperty("press").GetBoolean(),
                release = t.GetProperty("release").GetBoolean(),
                dash = t.GetProperty("dash").GetBoolean(),
                dashJump = t.GetProperty("dashJump").GetInt32(),
            }).ToList();
        }

        static T Option<T>(string[] args, string name, T fallback)
        {
            int i = Array.IndexOf(args, name);
            return i < 0 || i + 1 >= args.Length ? fallback : (T)Convert.ChangeType(args[i + 1], typeof(T), CultureInfo.InvariantCulture);
        }

        // Latest offline best waiting for turbo confirmation (older ones are superseded).
        sealed class BlockingQueue
        {
            (float time, List<InputTick> inputs)? _item;
            public void Set(float time, List<InputTick> inputs) { lock (this) { _item = (time, inputs); Monitor.Pulse(this); } }
            public void SetIfEmpty(float time, List<InputTick> inputs) { lock (this) { if (_item == null) { _item = (time, inputs); Monitor.Pulse(this); } } }
            public (float time, List<InputTick> inputs) Take()
            {
                lock (this)
                {
                    while (_item == null) Monitor.Wait(this);
                    var item = _item.Value;
                    _item = null;
                    return item;
                }
            }
        }

        static readonly string[] Categories = { "none", "wj", "wjdash", "wjdashaj" };

        static void SubmitOther(HttpClient http, Target target, string caps, float time, List<InputTick> inputs, Stopwatch clock)
        {
            string body = target.Body(caps, inputs);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    string reply = http.PostAsync(Api + "/api/enginesim/submit", new StringContent(body, Encoding.UTF8, "application/json")).Result
                        .Content.ReadAsStringAsync().Result;
                    using JsonDocument r = JsonDocument.Parse(reply);
                    if (!r.RootElement.TryGetProperty("saved", out JsonElement saved))
                    {
                        // Busy or loading: retry. Anything else (a category the course doesn't have) is final.
                        if (!reply.Contains("loading") && !reply.Contains("Stop playback")) return;
                        Thread.Sleep(2000);
                        continue;
                    }
                    if (saved.GetBoolean()) Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {time:0.00}s also saved as {target.Course}_{caps}");
                    return;
                }
                catch (Exception) { return; }
            }
        }

        static void Confirm(Target target, BlockingQueue pending, Stopwatch clock)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            while (true)
            {
                var (time, inputs) = pending.Take();
                // The same inputs may be valid in the course's other categories too (a WJ route never needs dash; a WJDASH
                // route may never dash); the gate replays them under each category's unlocks and decides. A chain has one.
                if (target.Chain == null)
                    foreach (string other in Categories.Where(c => c != target.Caps))
                        SubmitOther(http, target, other, time, inputs, clock);
                string body = target.Body(target.Caps, inputs);
                try
                {
                    string reply = http.PostAsync(Api + "/api/enginesim/submit", new StringContent(body, Encoding.UTF8, "application/json")).Result
                        .Content.ReadAsStringAsync().Result;
                    using JsonDocument r = JsonDocument.Parse(reply);
                    JsonElement root = r.RootElement;
                    if (!root.TryGetProperty("finished", out JsonElement finished))
                    {
                        // Busy with another solver's submit: retry unless a newer best is already waiting.
                        Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] turbo check of {time:0.00}s deferred: {reply}");
                        Thread.Sleep(2000);
                        pending.SetIfEmpty(time, inputs);
                        continue;
                    }
                    float turbo = root.GetProperty("time").GetSingle();
                    bool agree = finished.GetBoolean() && Math.Abs(turbo - time) < .0001f;
                    Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] turbo check of {time:0.00}s: "
                        + (finished.GetBoolean() ? "finish " + turbo.ToString("0.00", CultureInfo.InvariantCulture) + "s" : "no finish")
                        + (agree ? " (agrees)" : " (DISAGREES with offline)")
                        + (root.GetProperty("saved").GetBoolean() ? " - saved as the new route" : "")
                        + (root.TryGetProperty("rejected", out JsonElement rejected) && rejected.ValueKind == JsonValueKind.String ? " - " + rejected.GetString() : ""));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] turbo check skipped (game not reachable: {ex.GetBaseException().Message}); best kept on disk");
                }
            }
        }
    }
}
