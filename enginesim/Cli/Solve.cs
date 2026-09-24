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
    // route when the game agrees it is faster).
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
            var seeds = new List<List<InputTick>> { ReadInputs(header) };
            foreach (string path in seedPaths)
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 256 }))
                    seeds.Add(ReadInputs(doc.RootElement));

            string worldPath = Path.ChangeExtension(startTrace, null) + ".world.json";
            JsonElement start = header.GetProperty("start");
            Func<Simulation> create = () =>
            {
                var sim = new Simulation(World.Load(worldPath)) { TargetCourse = course };
                sim.LoadStart(start);
                return sim;
            };
            string outDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(startTrace)), "offline-best");
            Directory.CreateDirectory(outDir);
            string label = course + "_" + caps;
            var solver = new OfflineSolver(create, caps, seeds);
            var clock = Stopwatch.StartNew();
            var pending = new BlockingQueue();
            TimeSpan lastBest = TimeSpan.Zero;
            solver.Improved += (time, inputs, how) =>
            {
                lastBest = clock.Elapsed;
                string file = Path.Combine(outDir, label + "-" + time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(file, JsonSerializer.Serialize(new { course, capabilities = caps, time, ticks = inputs.Select(ToJson) }));
                Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] offline best {time:0.00}s via {how} ({inputs.Count} ticks) -> {Path.GetFileName(file)}");
                if (submit) pending.Set(time, inputs);
            };
            Console.WriteLine($"Solving course {course} ({caps}) on {threads} threads from {Path.GetFileName(startTrace)} with {seeds.Count} seed routes");
            solver.Start(threads);
            Thread confirmer = submit ? new Thread(() => Confirm(course, caps, pending, clock)) { IsBackground = true } : null;
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

        static List<InputTick> ReadInputs(JsonElement trace)
        {
            return trace.GetProperty("inputs").EnumerateArray().Select(t => new InputTick
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

        static void Confirm(int course, string caps, BlockingQueue pending, Stopwatch clock)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            while (true)
            {
                var (time, inputs) = pending.Take();
                string body = JsonSerializer.Serialize(new { course, capabilities = caps, ticks = inputs.Select(ToJson) });
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
