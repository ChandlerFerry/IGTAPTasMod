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
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    // Offline solver driver: N worker threads (one world each) from a trace's start state; every new best (faster, or
    // as fast in fewer physics ticks from the run start) is written to disk and, unless --no-submit, replayed in the
    // running game's turbo (which saves it as the category's route when the game agrees it is better). A seed's own
    // result is not new, so it never goes to the game.
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
            // --fresh: ignore the search state earlier solves of this target left in archive/ (it is still written).
            bool fresh = args.Contains("--fresh");
            // --lead <trace>: a course segment right after a transition that ends at its start gate, searched from the
            // transition's start (TasPlugin.EngineSimTrace with lead: true). Seeds are aligned on the segment's own start
            // trace as usual, then run after the transition's saved route; each best is the lap's time with its lead-in.
            string leadTrace = Option(args, "--lead", (string)null);
            var seedPaths = new List<string>();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--seed") seedPaths.Add(args[i + 1]);

            JsonElement header = ReadHeader(startTrace);
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            // A chain segment's start trace (TasPlugin.EngineSimTrace): its bests are the segment's, submitted as {chain, segment}.
            string chain = header.TryGetProperty("chain", out JsonElement chainName) ? chainName.GetString() : null;
            int segment = chain != null ? header.GetProperty("segment").GetInt32() : -1;
            var seeds = new List<List<InputTick>> { ReadInputs(header) };
            var aligns = new List<(string path, Vector2 at, List<InputTick> route)>();
            foreach (string path in seedPaths)
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 256 }))
                {
                    List<InputTick> route = ReadInputs(doc.RootElement);
                    // A standalone route seeding a chain segment: where it starts (TasPlugin.LaunchSolve), to walk there first.
                    if (chain != null && doc.RootElement.TryGetProperty("align", out JsonElement at))
                        aligns.Add((path, new Vector2(at.GetProperty("x").GetSingle(), at.GetProperty("y").GetSingle()), route));
                    else seeds.Add(route);
                }

            Func<Simulation> create = Creator(startTrace, header);
            if (aligns.Count > 0)
            {
                // A simulation runs on the thread that made it: the analyses (quick restart first) get their own.
                var analyses = new SeedAlign.Analysis[aligns.Count];
                var analyzer = new Thread(() =>
                {
                    Simulation analysis = create();
                    for (int i = 0; i < aligns.Count; i++) analyses[i] = SeedAlign.Analyze(analysis, aligns[i].at, aligns[i].route);
                });
                analyzer.Start();
                analyzer.Join();
                Simulation alignSim = create();
                for (int i = 0; i < aligns.Count; i++)
                {
                    var (path, at, route) = aligns[i];
                    List<List<InputTick>> carried = SeedAlign.Seeds(alignSim, analyses[i], route, out string note);
                    Console.WriteLine($"Seed {Path.GetFileName(path)} (starts at {at.x:0.0},{at.y:0.0}): {note}");
                    if (carried.Count > 0) seeds.AddRange(carried);
                    else seeds.Add(route);
                }
            }
            if (leadTrace != null)
            {
                // The lap's seeds after the transition's saved route (the lead trace's first `inputs` of its inputs, which
                // go on with the lap's saved route: the pair as the chain plays it).
                header = ReadHeader(leadTrace);
                if (chain == null || header.GetProperty("segment").GetInt32() != segment) throw new InvalidOperationException("--lead needs the same chain segment's lead trace.");
                List<InputTick> pair = ReadInputs(header);
                List<InputTick> transition = pair.Take(header.GetProperty("lead").GetProperty("inputs").GetInt32()).ToList();
                seeds = new[] { pair }.Concat(seeds.Select(s => transition.Concat(s).ToList())).ToList();
                create = Creator(leadTrace, header);
                startTrace = leadTrace;
            }
            string outDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(startTrace)), "offline-best");
            Directory.CreateDirectory(outDir);
            string label = chain != null ? "chain-" + chain + "-" + segment + "_" + caps + (leadTrace != null ? "-lead" : "") : course + "_" + caps;
            var target = new Target(course, caps, chain, segment, leadTrace != null);
            // Earlier solves' search state for this target (OfflineSolver.Memory): kept next to offline-best/, not in it,
            // since the game judges every file there.
            string archiveDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(startTrace)), "archive");
            string archiveFile = Path.Combine(archiveDir, label + ".json");
            OfflineSolver.Memory memory = fresh ? null : LoadMemory(archiveFile);
            if (memory != null)
                Console.WriteLine($"Resuming from {Path.GetFileName(archiveFile)}: {memory.Sources.Sum(s => s.Cells.Count)} cells over {memory.Sources.Count} prefixes, "
                    + $"{memory.Plateau.Count} plateau routes, {memory.Runs} runs before");
            var solver = new OfflineSolver(create, caps, seeds, memory);
            var clock = Stopwatch.StartNew();
            var pending = new BlockingQueue();
            TimeSpan lastBest = TimeSpan.Zero;
            solver.Improved += (time, runTicks, inputs, how) =>
            {
                lastBest = clock.Elapsed;
                string file = Path.Combine(outDir, label + "-" + time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(file, JsonSerializer.Serialize(new { course, capabilities = caps, chain, segment, lead = leadTrace != null, time, runTicks, ticks = inputs.Select(ToJson) }));
                Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] offline best {time:0.00}s via {how} ({inputs.Count} ticks, run {runTicks * .02:0.00}s from the start) -> {Path.GetFileName(file)}");
                // A chain segment's seeds come from elsewhere (standalone routes, stale ones), so its first finish is new too.
                // A seed's or a saved plateau route's result was found before (its file already written).
                if (submit && ((how != "seed" && how != "plateau") || chain != null)) pending.Set(time, runTicks, inputs);
            };
            Console.WriteLine($"Solving {(chain != null ? $"chain {chain} segment {segment} (course {course})" : $"course {course}")} ({caps}) on {threads} threads from {Path.GetFileName(startTrace)}"
                + (leadTrace != null ? " with its lead-in (the transition before it)" : "") + $" with {seeds.Count} seed routes");
            solver.Start(threads);
            Thread confirmer = submit ? new Thread(() => Confirm(target, pending, clock)) { IsBackground = true } : null;
            confirmer?.Start();
            long lastRuns = 0, lastTicks = 0;
            double lastSeconds = 0;
            int beats = 0;
            while (clock.Elapsed.TotalMinutes < minutes
                && (float.IsInfinity(solver.Shared.Time) || (clock.Elapsed - lastBest).TotalMinutes < stallMinutes))
            {
                Thread.Sleep(10000);
                double s = clock.Elapsed.TotalSeconds;
                long runs = Interlocked.Read(ref solver.Runs), ticks = Interlocked.Read(ref solver.Ticks);
                Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {(runs - lastRuns) / (s - lastSeconds):0} runs/s, {(ticks - lastTicks) / (s - lastSeconds):0} ticks/s, "
                    + $"{runs} runs, {Interlocked.Read(ref solver.Finishes)} finishes, {Interlocked.Read(ref solver.Drifts)} drifts, best {solver.Shared.Time:0.00}s"
                    + (solver.Shared.RunTicks == long.MaxValue ? "" : $" (run {solver.Shared.RunTicks * .02:0.00}s)") + ", branch replays matching "
                    + $"{Interlocked.Read(ref solver.BranchMatches)}/{Interlocked.Read(ref solver.BranchChecks)}, heap {GC.GetTotalMemory(false) >> 20} MB, ws {Environment.WorkingSet >> 20} MB");
                lastRuns = runs; lastTicks = ticks; lastSeconds = s;
                // Every minute: a solve the studio stops is killed, so the search state is saved as it goes.
                if (++beats % 6 == 0) SaveMemory(archiveFile, solver.Export(5000));
            }
            SaveMemory(archiveFile, solver.Export(10000));
            solver.Stop();
            Console.WriteLine("Done. Best " + solver.Shared.Time.ToString("0.00", CultureInfo.InvariantCulture) + "s");
            return 0;
        }

        static object ToJson(InputTick t) { return new { t.x, t.held, t.press, t.release, t.dash, t.dashJump }; }

        // What a submit is for: a course's category, or a chain segment (judged in its chain's category only), with its
        // lead-in: the inputs start at the transition before it.
        sealed record Target(int Course, string Caps, string Chain, int Segment, bool Lead)
        {
            public string Body(string caps, List<InputTick> inputs)
            {
                return Chain != null
                    ? JsonSerializer.Serialize(new { chain = Chain, segment = Segment, lead = Lead, ticks = inputs.Select(ToJson) })
                    : JsonSerializer.Serialize(new { course = Course, capabilities = caps, ticks = inputs.Select(ToJson) });
            }
        }

        static JsonElement ReadHeader(string trace)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(trace), new JsonDocumentOptions { MaxDepth = 256 });
            return doc.RootElement.Clone();
        }

        // A simulation of the trace's start, scored like the trace's run.
        static Func<Simulation> Creator(string trace, JsonElement header)
        {
            string worldPath = Path.ChangeExtension(trace, null) + ".world.json";
            JsonElement start = header.GetProperty("start");
            return () =>
            {
                var sim = new Simulation(World.Load(worldPath));
                sim.SetTarget(header);
                sim.LoadStart(start);
                return sim;
            };
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

        // Inputs as runs of equal ticks, "x,flags,dashJump,count" joined by ';' (flags: held 1, press 2, release 4,
        // dash 8): a prefix is mostly long runs of the same stick.
        static string EncodeTicks(List<InputTick> ticks)
        {
            var text = new StringBuilder();
            for (int i = 0; i < ticks.Count;)
            {
                int j = i + 1;
                while (j < ticks.Count && ticks[j].Equals(ticks[i])) j++;
                InputTick t = ticks[i];
                int flags = (t.held ? 1 : 0) | (t.press ? 2 : 0) | (t.release ? 4 : 0) | (t.dash ? 8 : 0);
                if (text.Length > 0) text.Append(';');
                text.Append(t.x.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(flags).Append(',').Append(t.dashJump).Append(',').Append(j - i);
                i = j;
            }
            return text.ToString();
        }

        static List<InputTick> DecodeTicks(string text)
        {
            var ticks = new List<InputTick>();
            if (text.Length == 0) return ticks;
            foreach (string run in text.Split(';'))
            {
                string[] f = run.Split(',');
                int flags = int.Parse(f[1], CultureInfo.InvariantCulture);
                var t = new InputTick
                {
                    x = float.Parse(f[0], CultureInfo.InvariantCulture),
                    held = (flags & 1) != 0, press = (flags & 2) != 0, release = (flags & 4) != 0, dash = (flags & 8) != 0,
                    dashJump = int.Parse(f[2], CultureInfo.InvariantCulture),
                };
                for (int n = int.Parse(f[3], CultureInfo.InvariantCulture); n > 0; n--) ticks.Add(t);
            }
            return ticks;
        }

        // Written to a temporary file and moved over the old one: a kill mid-write leaves the last whole save.
        static void SaveMemory(string path, OfflineSolver.Memory memory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new
            {
                runs = memory.Runs,
                plateau = memory.Plateau.Select(EncodeTicks),
                sources = memory.Sources.Select(s => new { ticks = EncodeTicks(s.Inputs), cells = s.Cells.Select(c => new object[] { c.Key, c.Length, c.Picks, c.Rank }) }),
            }));
            File.Move(temp, path, true);
        }

        static OfflineSolver.Memory LoadMemory(string path)
        {
            if (!File.Exists(path)) return null;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            return new OfflineSolver.Memory
            {
                Runs = root.GetProperty("runs").GetInt64(),
                Plateau = root.GetProperty("plateau").EnumerateArray().Select(p => DecodeTicks(p.GetString())).ToList(),
                Sources = root.GetProperty("sources").EnumerateArray().Select(s => new OfflineSolver.Memory.Source
                {
                    Inputs = DecodeTicks(s.GetProperty("ticks").GetString()),
                    Cells = s.GetProperty("cells").EnumerateArray().Select(c => new OfflineSolver.Memory.SavedCell
                    {
                        Key = c[0].GetInt64(), Length = c[1].GetInt32(), Picks = c[2].GetInt32(), Rank = c[3].GetSingle(),
                    }).ToList(),
                }).ToList(),
            };
        }

        // Latest offline best waiting for turbo confirmation (older ones are superseded).
        sealed class BlockingQueue
        {
            (float time, long runTicks, List<InputTick> inputs)? _item;
            public void Set(float time, long runTicks, List<InputTick> inputs) { lock (this) { _item = (time, runTicks, inputs); Monitor.Pulse(this); } }
            public void SetIfEmpty(float time, long runTicks, List<InputTick> inputs) { lock (this) { if (_item == null) { _item = (time, runTicks, inputs); Monitor.Pulse(this); } } }
            public (float time, long runTicks, List<InputTick> inputs) Take()
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
                var (time, runTicks, inputs) = pending.Take();
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
                        pending.SetIfEmpty(time, runTicks, inputs);
                        continue;
                    }
                    float turbo = root.GetProperty("time").GetSingle();
                    long turboTicks = root.GetProperty("runTicks").GetInt64();
                    bool agree = finished.GetBoolean() && Math.Abs(turbo - time) < .0001f && turboTicks == runTicks;
                    Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] turbo check of {time:0.00}s (run {runTicks} ticks): "
                        + (finished.GetBoolean() ? "finish " + turbo.ToString("0.00", CultureInfo.InvariantCulture) + "s (run " + turboTicks + " ticks)" : "no finish")
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
