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
    static class Solve
    {
        const string Api = "http://127.0.0.1:17426";

        public static int Run(string[] args)
        {
            string startTrace = args[0];
            int threads = Option(args, "--threads", Environment.ProcessorCount);
            double minutes = Option(args, "--minutes", 60.0);
            double stallMinutes = Option(args, "--stall-minutes", double.PositiveInfinity);
            bool submit = !args.Contains("--no-submit");
            bool fresh = args.Contains("--fresh");
            var seedPaths = new List<string>();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--seed") seedPaths.Add(args[i + 1]);
            OfflineSolver.ForcedPath forced = CliBoxes(args);
            string pathName = Option(args, "--path-name", "forced");
            bool customPath = !forced.IsEmpty;

            JsonElement header = ReadHeader(startTrace);
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            List<string> optional = Optional(args, course, caps);
            AddStandingPaths(forced, course, caps, optional);
            var seeds = new List<List<InputTick>> { ReadInputs(header) };
            foreach (string path in seedPaths)
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 256 }))
                    seeds.Add(ReadInputs(doc.RootElement));

            Func<Simulation> create = Creator(startTrace, header);
            string outDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(startTrace)), "offline-best");
            Directory.CreateDirectory(outDir);
            string label = course + "_" + caps;
            var target = new Target(course, caps);
            string archiveDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(startTrace)), "archive");
            string archiveFile = Path.Combine(archiveDir, ArchiveName(label, optional) + (customPath ? "-path-" + pathName : "") + ".json");
            OfflineSolver.Memory memory = fresh ? null : LoadMemory(archiveFile);
            if (memory != null)
                Console.WriteLine($"Resuming from {Path.GetFileName(archiveFile)}: {memory.Sources.Sum(s => s.Cells.Count)} cells over {memory.Sources.Count} prefixes, "
                    + $"{memory.Plateau.Count} plateau routes, {memory.Runs} runs before");
            var solver = new OfflineSolver(create, caps, seeds, memory, forced);
            if (!forced.IsEmpty)
                Console.WriteLine($"Forced path{(customPath ? " '" + pathName + "'" : "")}{(optional.Count == 0 ? "" : " with optional " + string.Join(", ", optional))}: via {(forced.Via.Count == 0 ? "-" : string.Join(" then ", forced.Via))}; avoid {(forced.Avoid.Count == 0 ? "-" : string.Join(", ", forced.Avoid))}");
            var clock = Stopwatch.StartNew();
            var pending = new BlockingQueue();
            TimeSpan lastBest = TimeSpan.Zero;
            var humanize = new Humanize.Background(create, forced, course, caps, line => Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {line}"));
            solver.Improved += (time, runTicks, inputs, how) =>
            {
                lastBest = clock.Elapsed;
                string file = Path.Combine(outDir, label + "-" + time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
                WriteShared(file, JsonSerializer.Serialize(new { course, capabilities = caps, time, runTicks, ticks = inputs.Select(t => t.ToJson()) }));
                Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] offline best {time:0.00}s via {how} ({inputs.Count} ticks, run {runTicks * .02:0.00}s from the start) -> {Path.GetFileName(file)}");
                if (submit && how != "seed" && how != "plateau") pending.Set(time, runTicks, inputs);
                humanize.Set(file, inputs);
            };
            Console.WriteLine($"Solving course {course} ({caps}) on {threads} threads from {Path.GetFileName(startTrace)} with {seeds.Count} seed routes");
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
                    + $"{runs} runs, {Interlocked.Read(ref solver.Finishes)} finishes, {Interlocked.Read(ref solver.Drifts)} drifts, {Interlocked.Read(ref solver.Splices)} splices, {Interlocked.Read(ref solver.Grafts)} grafts ({Interlocked.Read(ref solver.CornerGrafts)} corner hypers, {Interlocked.Read(ref solver.ClipsLanded)}/{Interlocked.Read(ref solver.ClipGrafts)} wall clips, {Interlocked.Read(ref solver.SetupsLanded)}/{Interlocked.Read(ref solver.SetupGrafts)} clip setups, {Interlocked.Read(ref solver.GateTaps)} gate taps), best {solver.Shared.Time:0.00}s"
                    + (forced.Via.Count > 0 ? $", forced path {solver.MostWaypoints}/{forced.Via.Count} boxes" : "")
                    + (solver.Shared.RunTicks == long.MaxValue ? "" : $" (run {solver.Shared.RunTicks * .02:0.00}s)") + ", branch replays matching "
                    + $"{Interlocked.Read(ref solver.BranchMatches)}/{Interlocked.Read(ref solver.BranchChecks)}, {solver.ArchiveStatus()}, heap {GC.GetTotalMemory(false) >> 20} MB, ws {Environment.WorkingSet >> 20} MB");
                lastRuns = runs; lastTicks = ticks; lastSeconds = s;
                if (++beats % 6 == 0) SaveMemory(archiveFile, solver.Export(5000));
            }
            SaveMemory(archiveFile, solver.Export(10000));
            solver.Stop();
            humanize.Finish();
            Console.WriteLine("Done. Best " + solver.Shared.Time.ToString("0.00", CultureInfo.InvariantCulture) + "s");
            return 0;
        }

        sealed record Target(int Course, string Caps)
        {
            public string Body(string caps, List<InputTick> inputs)
            {
                return JsonSerializer.Serialize(new { course = Course, capabilities = caps, ticks = inputs.Select(t => t.ToJson()) });
            }
        }

        static OfflineSolver.ForcedPath CliBoxes(string[] args)
        {
            var forced = new OfflineSolver.ForcedPath();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--via" || args[i] == "--avoid")
                {
                    float[] c = args[i + 1].Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    if (c.Length != 4) throw new ArgumentException(args[i] + " needs x0,y0,x1,y1, got " + args[i + 1]);
                    (args[i] == "--via" ? forced.Via : forced.Avoid).Add(new OfflineSolver.Box(c[0], c[1], c[2], c[3]));
                }
            return forced;
        }

        internal static OfflineSolver.ForcedPath ForcedPaths(string[] args, int course, string caps)
        {
            OfflineSolver.ForcedPath forced = CliBoxes(args);
            AddStandingPaths(forced, course, caps, Optional(args, course, caps));
            return forced;
        }

        internal static void WriteShared(string path, string contents)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            Replace(temp, path);
        }

        internal static void Replace(string temp, string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(temp, path, true); return; }
                catch (Exception ex) when ((ex is UnauthorizedAccessException || ex is IOException) && attempt < 50) { Thread.Sleep(20); }
            }
        }

        internal sealed record OptionalBlock(string Name, string Note, bool Default, OfflineSolver.ForcedPath Boxes);

        // The picked optional blocks: those marked "default": true, plus each `--optional <name>`, less each
        // `--no-optional <name>`.
        internal static List<string> Optional(string[] args, int course, string caps)
        {
            List<OptionalBlock> blocks = OptionalBlocks(course, caps, new OfflineSolver.ForcedPath());
            var names = blocks.Where(b => b.Default).Select(b => b.Name).ToList();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "--optional" && args[i] != "--no-optional") continue;
                string name = args[i + 1];
                if (!blocks.Any(b => b.Name == name))
                    throw new ArgumentException("forced-paths.json: no optional block '" + name + "' for course " + course + " " + caps
                        + (blocks.Count == 0 ? "" : " (has " + string.Join(", ", blocks.Select(b => b.Name)) + ")"));
                names.Remove(name);
                if (args[i] == "--optional") names.Add(name);
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        internal static string ArchiveName(string label, List<string> optional)
        {
            return label + (optional.Count == 0 ? "" : "-opt-" + string.Join("+", optional));
        }

        internal static void AddStandingPaths(OfflineSolver.ForcedPath forced, int course, string caps, List<string> optional)
        {
            foreach (OptionalBlock block in OptionalBlocks(course, caps, forced).Where(b => optional.Contains(b.Name)))
            {
                forced.Via.AddRange(block.Boxes.Via);
                forced.Avoid.AddRange(block.Boxes.Avoid);
            }
        }

        internal static List<OptionalBlock> OptionalBlocks(int course, string caps, OfflineSolver.ForcedPath standing)
        {
            var blocks = new List<OptionalBlock>();
            string file = Path.Combine(AppContext.BaseDirectory, "forced-paths.json");
            if (!File.Exists(file)) return blocks;
            using JsonDocument rules = JsonDocument.Parse(File.ReadAllText(file));
            var matching = rules.RootElement.GetProperty("paths").EnumerateArray()
                .Where(r => r.TryGetProperty("course", out JsonElement c) && c.GetInt32() == course
                    && r.GetProperty("capabilities").EnumerateArray().Any(k => k.GetString() == caps)).ToList();
            foreach (JsonElement rule in matching)
            {
                AddBoxes(standing, rule);
                if (rule.TryGetProperty("optional", out JsonElement optional))
                    foreach (JsonElement block in optional.EnumerateArray())
                    {
                        var boxes = new OfflineSolver.ForcedPath();
                        AddBoxes(boxes, block);
                        blocks.Add(new OptionalBlock(block.GetProperty("name").GetString(),
                            block.TryGetProperty("note", out JsonElement note) ? note.GetString() : "",
                            block.TryGetProperty("default", out JsonElement on) && on.GetBoolean(), boxes));
                    }
            }
            return blocks;
        }

        internal static OfflineSolver.ForcedPath FullTasPaths(string run)
        {
            var forced = new OfflineSolver.ForcedPath();
            string file = Path.Combine(AppContext.BaseDirectory, "forced-paths.json");
            if (!File.Exists(file)) return forced;
            using JsonDocument rules = JsonDocument.Parse(File.ReadAllText(file));
            foreach (JsonElement rule in rules.RootElement.GetProperty("paths").EnumerateArray())
                if (rule.TryGetProperty("fulltas", out JsonElement runs) && runs.EnumerateArray().Any(r => r.GetString() == run)
                    && rule.TryGetProperty("avoid", out JsonElement list))
                    foreach (JsonElement b in list.EnumerateArray())
                    {
                        float[] c = b.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                        forced.Avoid.Add(new OfflineSolver.Box(c[0], c[1], c[2], c[3]));
                    }
            return forced;
        }

        static void AddBoxes(OfflineSolver.ForcedPath forced, JsonElement rule)
        {
            foreach (string kind in new[] { "via", "avoid" })
                if (rule.TryGetProperty(kind, out JsonElement list))
                    foreach (JsonElement b in list.EnumerateArray())
                    {
                        float[] c = b.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                        (kind == "via" ? forced.Via : forced.Avoid).Add(new OfflineSolver.Box(c[0], c[1], c[2], c[3]));
                    }
        }

        internal static JsonElement ReadHeader(string trace)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(trace), new JsonDocumentOptions { MaxDepth = 256 });
            return doc.RootElement.Clone();
        }

        internal static Func<Simulation> Creator(string trace, JsonElement header)
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

        internal static List<InputTick> ReadInputs(JsonElement trace)
        {
            JsonElement list = trace.TryGetProperty("inputs", out JsonElement inputs) ? inputs : trace.GetProperty("ticks");
            return list.EnumerateArray().Select(InputTick.FromJson).ToList();
        }

        internal static T Option<T>(string[] args, string name, T fallback)
        {
            int i = Array.IndexOf(args, name);
            return i < 0 || i + 1 >= args.Length ? fallback : (T)Convert.ChangeType(args[i + 1], typeof(T), CultureInfo.InvariantCulture);
        }

        internal static string EncodeTicks(List<InputTick> ticks)
        {
            var text = new StringBuilder();
            for (int i = 0; i < ticks.Count;)
            {
                int j = i + 1;
                while (j < ticks.Count && ticks[j].Equals(ticks[i])) j++;
                InputTick t = ticks[i];
                int flags = (t.held ? 1 : 0) | (t.press ? 2 : 0) | (t.release ? 4 : 0) | (t.dash ? 8 : 0) | (t.reset ? 16 : 0)
                    | (t.y < 0f ? 32 : 0) | (t.y > 0f ? 64 : 0);
                if (text.Length > 0) text.Append(';');
                text.Append(t.x.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(flags).Append(',').Append(t.dashJump).Append(',').Append(j - i);
                if (t.ArmsLock || t.turnX.HasValue)
                    text.Append(',').Append(t.springDash).Append(',').Append(Number(t.lockX)).Append(',').Append(Number(t.lockY));
                if (t.turnX.HasValue) text.Append(',').Append(Number(t.turnX));
                i = j;
            }
            return text.ToString();
        }

        static string Number(float? v) { return v.HasValue ? v.Value.ToString("R", CultureInfo.InvariantCulture) : ""; }

        internal static List<InputTick> DecodeTicks(string text)
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
                    y = (flags & 32) != 0 ? -1f : (flags & 64) != 0 ? 1f : 0f,
                    held = (flags & 1) != 0, press = (flags & 2) != 0, release = (flags & 4) != 0, dash = (flags & 8) != 0, reset = (flags & 16) != 0,
                    dashJump = int.Parse(f[2], CultureInfo.InvariantCulture),
                    springDash = f.Length > 4 ? int.Parse(f[4], CultureInfo.InvariantCulture) : 0,
                    lockX = f.Length > 5 && f[5].Length > 0 ? float.Parse(f[5], CultureInfo.InvariantCulture) : null,
                    lockY = f.Length > 6 && f[6].Length > 0 ? float.Parse(f[6], CultureInfo.InvariantCulture) : null,
                    turnX = f.Length > 7 && f[7].Length > 0 ? float.Parse(f[7], CultureInfo.InvariantCulture) : null,
                };
                for (int n = int.Parse(f[3], CultureInfo.InvariantCulture); n > 0; n--) ticks.Add(t);
            }
            return ticks;
        }

        internal static void SaveMemory(string path, OfflineSolver.Memory memory)
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

        internal static OfflineSolver.Memory LoadMemory(string path)
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

        internal static readonly string[] Categories = { "none", "wj", "wjds", "wjdsdj", "wjdsdjbs", "wjdsdjbsom" };

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
                        if (!reply.Contains("loading") && !reply.Contains("Stop playback")) return;
                        Thread.Sleep(2000);
                        continue;
                    }
                    if (saved.GetBoolean()) Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {time:0.00}s also saved as {target.Course}_{caps}");
                    if (r.RootElement.TryGetProperty("farmSaved", out JsonElement farm) && farm.GetBoolean())
                        Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {time:0.00}s also saved as {target.Course}_{caps}'s farm route");
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
                        + (root.TryGetProperty("farmSaved", out JsonElement farmSaved) && farmSaved.GetBoolean() ? " - saved as the new farm route" : "")
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
