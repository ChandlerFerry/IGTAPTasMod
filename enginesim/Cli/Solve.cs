using System;
using System.Buffers;
using System.Collections.Concurrent;
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
        internal const string Api = "http://127.0.0.1:17426";

        public static int Run(string[] args)
        {
            // --auto: the Full-TAS --auto loop over the course's waypoints (CourseAuto.cs).
            if (args.Contains("--auto")) return FullTas.CourseAuto(args);
            string startTrace = args[0];
            int threads = Option(args, "--threads", Environment.ProcessorCount);
            double minutes = Option(args, "--minutes", 60.0);
            double stallMinutes = Option(args, "--stall-minutes", double.PositiveInfinity);
            bool submit = !args.Contains("--no-submit");
            bool fresh = args.Contains("--fresh");
            bool techGrafts = !args.Contains("--no-tech-grafts");
            bool guide = args.Contains("--guide");
            double guideWeight = Option(args, "--guide-weight", 1.0);
            int rng = Option(args, "--rng", 0);
            string logJson = Option(args, "--log-json", (string)null);
            if (logJson != null) File.WriteAllText(logJson, "");
            List<string> seedPaths = Args(args, "--seed").ToList();
            OfflineSolver.ForcedPath forced = CliBoxes(args);
            string pathName = Option(args, "--path-name", "forced");
            bool customPath = !forced.IsEmpty;

            JsonElement header = ReadHeader(startTrace);
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            List<string> optional = Optional(args, course, caps);
            AddStandingPaths(forced, course, caps, optional);
            guide |= forced.Guide;
            if (guide) DistanceField.Enabled = true;
            var seeds = new List<List<InputTick>> { ReadInputs(header) };
            seeds.AddRange(seedPaths.Select(Plan.ReadRoute));

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
            TechGrafts mined = techGrafts ? TechGrafts.Load(Path.GetDirectoryName(Path.GetFullPath(startTrace)), techDir: Option(args, "--tech-dir", (string)null),
                cacheFile: Option(args, "--tech-cache", (string)null)) : null;
            if (mined != null && args.Contains("--no-graft-specs")) mined.Patterns.RemoveAll(p => p.Spec);
            if (mined != null && args.Contains("--no-route-grafts")) mined.Patterns.RemoveAll(p => p.Category == TechGrafts.RouteCategory);
            if (mined != null)
                Console.WriteLine($"Tech grafts: {mined.Patterns.Count} patterns from {mined.Demos.Count(d => d.Patterns.Count > 0)} of {mined.Demos.Count} tech demos"
                    + $" ({(mined.FromCache ? "cached" : "mined")}, {Path.GetFileName(mined.CacheFile)}), {mined.Patterns.Count(p => p.Category == TechGrafts.RouteCategory)} of them from the saved routes");
            // --plan: the planner (and the timing polish) first, within this solve's time; its route seeds the search.
            var clock = Stopwatch.StartNew();
            if (args.Contains("--plan"))
            {
                var options = new Planner.Options { Threads = threads, Width = Math.Max(48, 2 * threads), Seconds = Option(args, "--plan-minutes", minutes / 3) * 60.0 };
                // The seeds that finish guide it (their cost to go); unseeded, the field's distance does.
                options.Guides.AddRange(seeds.Where(s => s.Count > 0));
                void PlanLog(string line) => Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {line}");
                (float cell, Vector2 clearance) = (DistanceField.CellSize, DistanceField.Clearance);
                Plan.Field(args);
                Planner.Route planned = Plan.Plan1(create, caps, forced, mined, options, PlanLog, out Planner planner);
                (DistanceField.CellSize, DistanceField.Clearance) = (cell, clearance);
                if (planned != null)
                {
                    Polish.Result polished = Polish.Once(create, forced, threads, PlanLog, planned.Inputs);
                    seeds.Add(polished.Inputs);
                    PlanLog($"plan seed {polished.Time:0.00}s ({polished.RunTicks} physics ticks): planner {planner.Ticks} ticks, polish {polished.Ticks} ticks");
                    if (logJson != null)
                        File.AppendAllText(logJson, JsonSerializer.Serialize(new { wall = clock.Elapsed.TotalSeconds, time = polished.Time, runTicks = polished.RunTicks,
                            ticks = planner.Ticks + polished.Ticks, how = "plan" }) + "\n");
                }
                else PlanLog($"plan: no finish ({planner.Ticks} ticks)");
            }
            var solver = new OfflineSolver(create, caps, seeds, memory, forced, mined: mined, guide: (float)(guide ? guideWeight : 0.0), rng: rng)
                { MinedDraws = Option(args, "--mined-draws", 1) };
            if (!forced.IsEmpty)
                Console.WriteLine($"Forced path{(customPath ? " '" + pathName + "'" : "")}{(optional.Count == 0 ? "" : " with optional " + string.Join(", ", optional))}: via {(forced.Via.Count == 0 ? "-" : string.Join(" then ", forced.Via))}; avoid {(forced.Avoid.Count == 0 ? "-" : string.Join(", ", forced.Avoid))}");
            clock.Restart();
            var pending = new Latest<Pending>();
            TimeSpan lastBest = TimeSpan.Zero;
            Humanize.Background humanize = args.Contains("--no-humanize") ? null
                : new Humanize.Background(create, forced, course, caps, threads, line => Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {line}"));
            solver.Improved += (time, runTicks, inputs, how) =>
            {
                lastBest = clock.Elapsed;
                string file = Path.Combine(outDir, label + "-" + time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
                WriteShared(file, JsonSerializer.Serialize(new { course, capabilities = caps, time, runTicks, ticks = inputs.Select(t => t.ToJson()) }));
                Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] offline best {time:0.00}s via {how} ({inputs.Count} ticks, run {runTicks * .02:0.00}s from the start) -> {Path.GetFileName(file)}");
                if (submit && how != "seed" && how != "plateau") pending.Set(new Pending(time, runTicks, inputs));
                humanize?.Set(file, inputs);
                // Raised one at a time, in the order the bests were found, so the appends never interleave.
                if (logJson != null)
                    File.AppendAllText(logJson, JsonSerializer.Serialize(new { wall = clock.Elapsed.TotalSeconds, time, runTicks, ticks = Interlocked.Read(ref solver.Ticks), how,
                        cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds }) + "\n");
            };
            Console.WriteLine($"Solving course {course} ({caps}) on {threads} threads from {Path.GetFileName(startTrace)} with {seeds.Count} seed routes"
                + (guide ? $", guide weight {guideWeight}" : ", no guide"));
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
                    + $"{runs} runs, {Interlocked.Read(ref solver.Finishes)} finishes, {Interlocked.Read(ref solver.Drifts)} drifts, {Interlocked.Read(ref solver.Splices)} splices, {Interlocked.Read(ref solver.TechGrafted)} tech grafts ({Interlocked.Read(ref solver.ChainGrafts)} chained, {Interlocked.Read(ref solver.ExpectsMet)}/{Interlocked.Read(ref solver.ExpectChecks)} expected contacts, {Interlocked.Read(ref solver.TechGraftedAtStep)} at step contacts), best {solver.Shared.Time:0.00}s"
                    + (forced.Via.Count > 0 ? $", forced path {solver.MostWaypoints}/{forced.Via.Count} boxes" : "")
                    + (solver.Shared.RunTicks == long.MaxValue ? "" : $" (run {solver.Shared.RunTicks * .02:0.00}s)")
                    + ", " + solver.GuideStatus() + ", branch replays matching "
                    + $"{Interlocked.Read(ref solver.BranchMatches)}/{Interlocked.Read(ref solver.BranchChecks)}, {solver.ArchiveStatus()}, heap {GC.GetTotalMemory(false) >> 20} MB, ws {Environment.WorkingSet >> 20} MB");
                lastRuns = runs; lastTicks = ticks; lastSeconds = s;
                if (++beats % 6 == 0) SaveMemory(archiveFile, solver.Export(5000));
            }
            SaveMemory(archiveFile, solver.Export(10000));
            solver.Stop();
            humanize?.Finish();
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
                    (args[i] == "--via" ? forced.Via : forced.Avoid).Add(new OfflineSolver.Box(new WorldPos(c[0], c[1]), new WorldPos(c[2], c[3])));
                }
            return forced;
        }

        internal static OfflineSolver.ForcedPath ForcedPaths(string[] args, int course, string caps)
        {
            OfflineSolver.ForcedPath forced = CliBoxes(args);
            AddStandingPaths(forced, course, caps, Optional(args, course, caps));
            return forced;
        }

        // The temp file is per process: runs sharing a file (the loop pieces) collided on one temp name.
        internal static void WriteShared(string path, string contents)
        {
            string temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, contents);
            Replace(temp, path);
        }

        /// <summary>An exclusive lock on path + ".lock" across processes, held until disposed.</summary>
        internal static FileStream Lock(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) when (attempt < 1000) { Thread.Sleep(10); }
            }
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
            foreach (PathRule rule in PathRules().Where(r => r.Course == course && r.Capabilities.Contains(caps)))
            {
                AddBoxes(standing, rule.Via, rule.Avoid);
                if (rule.Guide) standing.Guide = true;
                foreach (PathBlock block in rule.Optional ?? Array.Empty<PathBlock>())
                {
                    var boxes = new OfflineSolver.ForcedPath();
                    AddBoxes(boxes, block.Via, block.Avoid);
                    blocks.Add(new OptionalBlock(block.Name, block.Note ?? "", block.Default, boxes));
                }
            }
            return blocks;
        }

        /// <summary>forced-paths.json's rules (a course's and category's boxes, a Full-TAS run's avoid boxes). The studio edits the
        /// file while a solve runs, so it is read again whenever it has changed.</summary>
        sealed record PathRule(int? Course, string[] Capabilities, string[] Fulltas, float[][] Via, float[][] Avoid, bool Guide, PathBlock[] Optional);
        sealed record PathBlock(string Name, string Note, bool Default, float[][] Via, float[][] Avoid, string[] OffFor, string After);
        sealed record PathsFile(PathRule[] Paths);
        static Tuple<DateTime, PathRule[]> _pathRules;

        static PathRule[] PathRules()
        {
            string file = Path.Combine(AppContext.BaseDirectory, "forced-paths.json");
            if (!File.Exists(file)) return Array.Empty<PathRule>();
            DateTime written = File.GetLastWriteTimeUtc(file);
            Tuple<DateTime, PathRule[]> read = _pathRules;
            if (read == null || read.Item1 != written)
                _pathRules = read = Tuple.Create(written, JsonSerializer.Deserialize<PathsFile>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).Paths);
            return read.Item2;
        }

        /// <summary>
        /// The avoid boxes of the run's Full-TAS rules for a window heading for <paramref name="goals"/>: each rule's "avoid", plus
        /// its "optional" blocks except those whose "offFor" lists one of the goals (null goals: every block, as the map draws them).
        /// A block's "after" (a goal label of the run, <paramref name="labels"/>) makes its boxes cut a run only once it has reached
        /// that goal; without the run's labels, or when the run has no such goal, the block is off.
        /// </summary>
        internal static OfflineSolver.ForcedPath FullTasPaths(string run, ICollection<string> goals = null, IReadOnlyList<string> labels = null)
        {
            var forced = new OfflineSolver.ForcedPath();
            // A course searched as a Full-TAS run (solve --auto): its course and category's rules, set when the run started.
            if (CourseRuns.TryGetValue(run, out OfflineSolver.ForcedPath course))
            {
                forced.Avoid.AddRange(course.Avoid);
                return forced;
            }
            foreach (PathRule rule in PathRules().Where(r => r.Fulltas != null && r.Fulltas.Contains(run)))
            {
                AddBoxes(forced, null, rule.Avoid);
                foreach (PathBlock block in rule.Optional ?? Array.Empty<PathBlock>())
                {
                    if (goals != null && block.OffFor != null && block.OffFor.Any(goals.Contains)) continue;
                    int after = 0;
                    if (block.After != null)
                    {
                        int index = labels == null ? -1 : labels.ToList().LastIndexOf(block.After);
                        if (index < 0) continue;
                        after = index + 1;
                    }
                    AddBoxes(forced, null, block.Avoid, after);
                }
            }
            return forced;
        }

        /// <summary>The course runs searched as Full-TAS runs in this process (solve --auto, CourseAuto.cs), by run name: their
        /// course rules' avoid boxes (forced-paths.json's rules for the course and category, with the picked optional blocks).</summary>
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, OfflineSolver.ForcedPath> CourseRuns =
            new System.Collections.Concurrent.ConcurrentDictionary<string, OfflineSolver.ForcedPath>();

        static void AddBoxes(OfflineSolver.ForcedPath forced, float[][] via, float[][] avoid, int after = 0)
        {
            foreach (float[] c in via ?? Array.Empty<float[]>()) forced.Via.Add(new OfflineSolver.Box(new WorldPos(c[0], c[1]), new WorldPos(c[2], c[3]), after));
            foreach (float[] c in avoid ?? Array.Empty<float[]>()) forced.Avoid.Add(new OfflineSolver.Box(new WorldPos(c[0], c[1]), new WorldPos(c[2], c[3]), after));
        }

        /// <summary>
        /// The trace without its "ticks" (the game's state after every tick, which only `replay` reads): a Full-TAS trace runs to a
        /// gigabyte of them, past what one string holds. Streamed, so the file is never in memory whole. A trace that plays its
        /// "ticks" as its inputs (a tech trace: no "inputs" before them) keeps them.
        /// </summary>
        internal static JsonElement ReadHeader(string trace)
        {
            var output = new ArrayBufferWriter<byte>();
            using (FileStream stream = File.OpenRead(trace))
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = 256 }))
            {
                byte[] buffer = new byte[1 << 20];
                int filled = 0;
                bool final = false, sawInputs = false, skipValue = false;
                int skipDepth = -1;
                var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 256 });
                while (true)
                {
                    while (!final && filled < buffer.Length)
                    {
                        int read = stream.Read(buffer, filled, buffer.Length - filled);
                        if (read == 0) final = true;
                        filled += read;
                    }
                    var reader = new Utf8JsonReader(buffer.AsSpan(0, filled), final, state);
                    while (reader.Read())
                    {
                        JsonTokenType token = reader.TokenType;
                        if (skipDepth >= 0)
                        {
                            if ((token == JsonTokenType.EndArray || token == JsonTokenType.EndObject) && reader.CurrentDepth == skipDepth) skipDepth = -1;
                            continue;
                        }
                        if (skipValue)
                        {
                            skipValue = false;
                            if (token == JsonTokenType.StartArray || token == JsonTokenType.StartObject) skipDepth = reader.CurrentDepth;
                            continue;
                        }
                        switch (token)
                        {
                            case JsonTokenType.StartObject: writer.WriteStartObject(); break;
                            case JsonTokenType.EndObject: writer.WriteEndObject(); break;
                            case JsonTokenType.StartArray: writer.WriteStartArray(); break;
                            case JsonTokenType.EndArray: writer.WriteEndArray(); break;
                            case JsonTokenType.PropertyName:
                                string name = reader.GetString();
                                if (reader.CurrentDepth == 1 && name == "inputs") sawInputs = true;
                                if (reader.CurrentDepth == 1 && name == "ticks" && sawInputs) skipValue = true;
                                else writer.WritePropertyName(name);
                                break;
                            case JsonTokenType.String: writer.WriteStringValue(reader.GetString()); break;
                            case JsonTokenType.Number: writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true); break;
                            case JsonTokenType.True: writer.WriteBooleanValue(true); break;
                            case JsonTokenType.False: writer.WriteBooleanValue(false); break;
                            case JsonTokenType.Null: writer.WriteNullValue(); break;
                        }
                    }
                    if (final) break;
                    state = reader.CurrentState;
                    int used = (int)reader.BytesConsumed;
                    if (used == 0 && filled == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                    Buffer.BlockCopy(buffer, used, buffer, 0, filled - used);
                    filled -= used;
                }
            }
            using JsonDocument doc = JsonDocument.Parse(output.WrittenMemory, new JsonDocumentOptions { MaxDepth = 256 });
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
                sim.LoadStart(start, canonical: true);
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

        /// <summary>The value after every <paramref name="name"/> (a repeatable option), in order.</summary>
        internal static IEnumerable<string> Args(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) yield return args[i + 1];
        }

        /// <summary>f on a thread of its own (64 MB stack), joined: a Simulation made there leaves the caller's thread-static game
        /// state as it was. Its exception is rethrown wrapped.</summary>
        internal static T OnThread<T>(Func<T> f)
        {
            T value = default;
            Exception failed = null;
            var t = new Thread(() => { try { value = f(); } catch (Exception ex) { failed = ex; } }, 64 << 20);
            t.Start();
            t.Join();
            if (failed != null) throw new InvalidOperationException(failed.GetBaseException().Message, failed);
            return value;
        }

        internal static void OnThread(Action f) { OnThread(() => { f(); return 0; }); }

        /// <summary>Starts work(state, i) for every i below count on up to <paramref name="threads"/> threads of their own (64 MB
        /// stacks), each with its state made on it by init (a Simulation: the game's statics are per thread); the returned wait
        /// joins them and rethrows the first failure.</summary>
        internal static Action Workers<T>(int threads, int count, Func<T> init, Action<T, int> work)
        {
            int next = -1;
            Exception failed = null;
            Thread[] pool = Enumerable.Range(0, Math.Min(threads, count)).Select(_ => new Thread(() =>
            {
                try
                {
                    T state = init();
                    for (int i; (i = Interlocked.Increment(ref next)) < count;) work(state, i);
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref failed, ex, null); }
            }, 64 << 20) { IsBackground = true }).ToArray();
            foreach (Thread t in pool) t.Start();
            return () =>
            {
                foreach (Thread t in pool) t.Join();
                if (failed != null) throw new InvalidOperationException(failed.GetBaseException().Message, failed);
            };
        }

        /// <summary>POSTs a JSON body to the plugin's API and returns its reply.</summary>
        internal static string Post(HttpClient http, string path, string body)
        {
            return http.PostAsync(Api + path, new StringContent(body, Encoding.UTF8, "application/json")).Result.Content.ReadAsStringAsync().Result;
        }

        internal static string EncodeTicks(List<InputTick> ticks) => EncodeRuns(Runs(ticks));

        static IEnumerable<(InputTick tick, int count)> Runs(List<InputTick> ticks)
        {
            for (int i = 0; i < ticks.Count;)
            {
                int j = i + 1;
                while (j < ticks.Count && ticks[j].Equals(ticks[i])) j++;
                yield return (ticks[i], j - i);
                i = j;
            }
        }

        /// <summary>Runs of equal ticks as text (adjacent runs are expected to differ: each is written as one).</summary>
        static string EncodeRuns(IEnumerable<(InputTick tick, int count)> runs)
        {
            var text = new StringBuilder();
            foreach (var (t, count) in runs)
            {
                int flags = (t.held ? 1 : 0) | (t.press ? 2 : 0) | (t.release ? 4 : 0) | (t.dash ? 8 : 0) | (t.reset ? 16 : 0)
                    | (t.y < 0f ? 32 : 0) | (t.y > 0f ? 64 : 0);
                if (text.Length > 0) text.Append(';');
                text.Append(t.x.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(flags).Append(',').Append(t.dashJump).Append(',').Append(count);
                bool pause = t.pause > 0;
                if (t.ArmsLock || t.turnX.HasValue || t.turnY.HasValue || pause)
                    text.Append(',').Append(t.springDash).Append(',').Append(Number(t.lockX)).Append(',').Append(Number(t.lockY));
                if (t.turnX.HasValue || t.turnY.HasValue || pause) text.Append(',').Append(Number(t.turnX));
                if (t.turnY.HasValue || pause) text.Append(',').Append(Number(t.turnY));
                if (pause) text.Append(',').Append(t.pause).Append(',').Append(t.pauseTick);
                if (pause && t.pauseFrame != 0) text.Append(',').Append(t.pauseFrame);
            }
            return text.ToString();
        }

        static string Number(float? v) { return v.HasValue ? v.Value.ToString("R", CultureInfo.InvariantCulture) : ""; }

        internal static List<InputTick> DecodeTicks(string text)
        {
            var ticks = new List<InputTick>();
            foreach (var (t, count) in DecodeRuns(text))
                for (int n = count; n > 0; n--) ticks.Add(t);
            return ticks;
        }

        static IEnumerable<(InputTick tick, int count)> DecodeRuns(string text)
        {
            if (text.Length == 0) yield break;
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
                    turnY = f.Length > 8 && f[8].Length > 0 ? float.Parse(f[8], CultureInfo.InvariantCulture) : null,
                    pause = f.Length > 9 ? int.Parse(f[9], CultureInfo.InvariantCulture) : 0,
                    pauseTick = f.Length > 10 ? int.Parse(f[10], CultureInfo.InvariantCulture) : 0,
                    pauseFrame = f.Length > 11 ? int.Parse(f[11], CultureInfo.InvariantCulture) : 0,
                };
                yield return (t, int.Parse(f[3], CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Writes the archive streamed, each source's inputs as the count it shares with the previous source ("shared") and the rest:
        /// a Full-TAS window's sources all repeat the window's prefix, which written whole passed .NET's 1 G-char string limit.
        /// </summary>
        internal static void SaveMemory(string path, OfflineSolver.Memory memory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            InputRuns previous = null;
            using (FileStream file = File.Create(temp))
                JsonSerializer.Serialize(file, new
                {
                    runs = memory.Runs,
                    plateau = memory.Plateau.Select(EncodeTicks),
                    sources = memory.Sources.Select(s =>
                    {
                        int shared = previous == null ? 0 : InputRuns.SharedPrefix(previous, s.Inputs);
                        previous = s.Inputs;
                        return new { shared, ticks = EncodeRuns(s.Inputs.Runs(shared)),
                            cells = s.Cells.Select(c => new object[] { c.Key, c.Length, c.Picks, c.Rank }) };
                    }),
                });
            File.Move(temp, path, true);
        }

        internal static OfflineSolver.Memory LoadMemory(string path)
        {
            if (!File.Exists(path)) return null;
            using FileStream file = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(file);
            JsonElement root = doc.RootElement;
            InputRuns previous = null;
            return new OfflineSolver.Memory
            {
                Runs = root.GetProperty("runs").GetInt64(),
                Plateau = root.GetProperty("plateau").EnumerateArray().Select(p => DecodeTicks(p.GetString())).ToList(),
                Sources = root.GetProperty("sources").EnumerateArray().Select(s =>
                {
                    int shared = s.TryGetProperty("shared", out JsonElement n) ? n.GetInt32() : 0;
                    var inputs = new InputRuns.Builder();
                    if (previous != null) inputs.AddRange(previous, shared);
                    foreach (var (tick, count) in DecodeRuns(s.GetProperty("ticks").GetString())) inputs.Add(tick, count);
                    previous = inputs.Build();
                    return new OfflineSolver.Memory.Source
                    {
                        Inputs = previous,
                        Cells = s.GetProperty("cells").EnumerateArray().Select(c => new OfflineSolver.Memory.SavedCell
                        {
                            Key = c[0].GetInt64(), Length = c[1].GetInt32(), Picks = c[2].GetInt32(), Rank = c[3].GetSingle(),
                        }).ToList(),
                    };
                }).ToList(),
            };
        }

        sealed record Pending(float Time, long RunTicks, List<InputTick> Inputs);

        static void SubmitOther(HttpClient http, Target target, string caps, float time, List<InputTick> inputs, Stopwatch clock)
        {
            string body = target.Body(caps, inputs);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    string reply = Post(http, "/api/enginesim/submit", body);
                    using JsonDocument r = JsonDocument.Parse(reply);
                    if (!r.RootElement.TryGetProperty("saved", out JsonElement saved))
                    {
                        if (!reply.Contains("loading") && !reply.Contains("Stop playback"))
                        {
                            if (!reply.Contains(" category; it "))
                                Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {time:0.00}s as {target.Course}_{caps} not judged: {reply}");
                            return;
                        }
                        Thread.Sleep(2000);
                        continue;
                    }
                    if (saved.GetBoolean()) Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {time:0.00}s also saved as {target.Course}_{caps}");
                    if (r.RootElement.TryGetProperty("farmSaved", out JsonElement farm) && farm.GetBoolean())
                        Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {time:0.00}s also saved as {target.Course}_{caps}'s farm route");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {time:0.00}s as {target.Course}_{caps} not judged: {ex.GetBaseException().Message}");
                    return;
                }
            }
        }

        static void Confirm(Target target, Latest<Pending> pending, Stopwatch clock)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            while (true)
            {
                Pending next = pending.Take();
                var (time, runTicks, inputs) = (next.Time, next.RunTicks, next.Inputs);
                foreach (string other in IGTAPTasMod.Categories.All.Select(d => d.Id).Where(id => id != target.Caps))
                    SubmitOther(http, target, other, time, inputs, clock);
                string body = target.Body(target.Caps, inputs);
                try
                {
                    string reply = Post(http, "/api/enginesim/submit", body);
                    using JsonDocument r = JsonDocument.Parse(reply);
                    JsonElement root = r.RootElement;
                    if (!root.TryGetProperty("finished", out JsonElement finished))
                    {
                        Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] turbo check of {time:0.00}s deferred: {reply}");
                        Thread.Sleep(2000);
                        pending.SetIfEmpty(next);
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
    /// <summary>A one-item mailbox: Set replaces what is there, Take waits for an item and empties it.</summary>
    sealed class Latest<T> where T : class
    {
        T _item;
        public void Set(T item) { lock (this) { _item = item; Monitor.Pulse(this); } }
        public void SetIfEmpty(T item) { lock (this) { if (_item == null) { _item = item; Monitor.Pulse(this); } } }
        public T Take()
        {
            lock (this)
            {
                while (_item == null) Monitor.Wait(this);
                T item = _item;
                _item = null;
                return item;
            }
        }
    }

    /// <summary>A thread of its own holding a state made on it (a Simulation: the game's statics are per thread), running the
    /// jobs posted to it in order.</summary>
    sealed class Worker<T>
    {
        readonly BlockingCollection<Action<T>> _jobs = new BlockingCollection<Action<T>>();
        readonly string _name;

        public Worker(Func<T> make, string name)
        {
            _name = name;
            new Thread(() =>
            {
                T state = make();
                foreach (Action<T> job in _jobs.GetConsumingEnumerable()) job(state);
            }) { IsBackground = true, Name = name }.Start();
        }

        public void Post(Action<T> job) { _jobs.Add(job); }

        /// <summary>Runs job on the worker and waits for it; its exception is rethrown wrapped.</summary>
        public void Do(Action<T> job)
        {
            using var done = new ManualResetEventSlim();
            Exception failure = null;
            Post(j => { try { job(j); } catch (Exception ex) { failure = ex; } finally { done.Set(); } });
            done.Wait();
            if (failure != null) throw new InvalidOperationException(_name + " replay failed", failure);
        }

        public void Stop() { _jobs.CompleteAdding(); }
    }
}
