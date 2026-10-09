using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    static class Map
    {
        const float Cell = 16f;
        const int MaxPlateau = 8;

        sealed class Path
        {
            public string Name, Kind;
            public bool Finished;
            public float Time;
            public int TimerStart = -1;
            public readonly List<float> Points = new List<float>();
            // The first arrival in each cell along the route (`Earlier`, the explore cells' time) and, per tick, the
            // player's physical state fingerprint: the first tick two routes' fingerprints differ is where their
            // bodies part (input and buffer fields stay out of it).
            public readonly Dictionary<long, float> Cells = new Dictionary<long, float>();
            public readonly List<int> State = new List<int>();
        }

        // `area`: a checkpoint's trigger shapes (polygons, game-world x, y pairs), where touching it sets respawnPoint,
        // or an upgrade box's trigger box (touching it buys it).
        // `spawn`: the player's box where a death or quick restart puts it at that checkpoint (x0, y0, x1, y1; x, y is
        // its centre). `when`: the world state it's only in (WorldStates), none: every one. `box`: kind "box" only, an
        // upgrade box's buy info.
        internal sealed record Marker(string kind, string name, float x, float y,
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float[][] area = null,
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string when = null,
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float[] spawn = null,
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BoxInfo box = null);

        public static int Run(string[] args)
        {
            string trace = args[0];
            string output = Solve.Option(args, "--out", (string)null) ?? throw new ArgumentException("map needs --out <file>");
            int threads = int.Parse(Solve.Option(args, "--threads", (string)null) ?? Math.Max(1, Environment.ProcessorCount - 2).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            JsonElement header = Solve.ReadHeader(trace);
            if (header.TryGetProperty("fulltas", out JsonElement fulltas))
                return FullTasMap(trace, header, fulltas, Solve.Option(args, "--goals", (string)null), output,
                    int.Parse(Solve.Option(args, "--stretch", "-1"), CultureInfo.InvariantCulture));
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            string label = course + "_" + caps;
            List<string> optional = Solve.Optional(args, course, caps);
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(trace));
            string worldPath = System.IO.Path.ChangeExtension(trace, null) + ".world.json";
            string archivePath = System.IO.Path.Combine(dir, "archive", Solve.ArchiveName(label, optional) + ".json");
            string best = FastestOfflineBest(dir, label);
            List<string> seedFiles = Solve.Args(args, "--seed").ToList();

            string engine = EngineStamp();
            // The explore cells' cache is the simulation's (not the CLI's, rebuilt with every map or solve edit).
            string simKey = string.Join(";", engine.Split(';').Where(d => !d.StartsWith("IGTAP.EngineSim.dll=", StringComparison.OrdinalIgnoreCase)))
                + "|" + Stamp(trace) + "|" + Stamp(worldPath);
            string human = best == null ? null : HumanFile(dir, best);
            string outputKey = string.Join("\n", new[] { engine, simKey, Stamp(archivePath), best ?? "-", Stamp(best), Stamp(human),
                Stamp(System.IO.Path.Combine(AppContext.BaseDirectory, "forced-paths.json")), string.Join("+", optional) }
                .Concat(seedFiles.Select(f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))));
            string keyPath = output + ".key";
            if (!args.Contains("--closest") && File.Exists(output) && File.Exists(keyPath) && File.ReadAllText(keyPath) == outputKey)
            {
                Console.WriteLine($"map {label}: inputs unchanged -> {output}");
                return 0;
            }
            Func<Simulation> create = Solve.Creator(trace, header);

            var routes = new List<(string name, string kind, List<InputTick> inputs)>();
            List<InputTick> own = Solve.ReadInputs(header);
            if (own.Count > 0) routes.Add((System.IO.Path.GetFileNameWithoutExtension(trace), "seed", own));
            foreach (string seedFile in seedFiles)
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(seedFile), new JsonDocumentOptions { MaxDepth = 256 }))
                    routes.Add((doc.RootElement.TryGetProperty("name", out JsonElement n) ? n.GetString() : System.IO.Path.GetFileNameWithoutExtension(seedFile),
                        "seed", Solve.ReadInputs(doc.RootElement)));
            if (best != null)
            {
                routes.Add(("offline best " + System.IO.Path.GetFileNameWithoutExtension(best).Substring(label.Length + 1) + "s", "best", global::IGTAP.EngineSim.Cli.Plan.ReadRoute(best)));
                // Its human version (humanize), so the studio can ghost it against the best or a saved route.
                if (human != null)
                    routes.Add(("human " + System.IO.Path.GetFileNameWithoutExtension(best).Substring(label.Length + 1) + "s", "human", global::IGTAP.EngineSim.Cli.Plan.ReadRoute(human)));
            }
            OfflineSolver.Memory memory = Solve.LoadMemory(archivePath);
            if (memory != null)
                foreach (var (p, i) in memory.Plateau.Take(MaxPlateau).Select((p, i) => (p, i)))
                    routes.Add(("plateau " + (i + 1), "plateau", p));

            List<InputRuns> sources = memory?.Sources.Select(s => s.Inputs).ToList() ?? new List<InputRuns>();
            string cellsPath = System.IO.Path.ChangeExtension(archivePath, ".mapcells.bin");
            // The cache is every cell the prefixes replayed so far reached (its earliest time) and their hashes: only new
            // prefixes replay, and a prefix the archive has dropped since keeps its cells (the map is where the search went).
            var (seen, reached) = memory == null ? (new HashSet<Hash>(), new Dictionary<long, float>()) : ReadCells(cellsPath, simKey);
            var hashes = new Hash[sources.Count];
            Parallel.For(0, sources.Count, k => hashes[k] = HashInputs(sources[k]));
            var current = new HashSet<Hash>();
            // --closest: every prefix replays (not only new ones), and the one whose player came nearest the finish box (straight
            // line, capture frame) is drawn as a route and written beside the map (<map>.closest.json), for a target no run finishes.
            bool closest = args.Contains("--closest");
            int[] missing = Enumerable.Range(0, sources.Count).Where(k => current.Add(hashes[k]) && (closest || !seen.Contains(hashes[k]))).ToArray();
            int replayed = 0;
            float closestDistance = float.PositiveInfinity;
            int closestSource = -1;
            object closestLock = new object();
            Action workers = Solve.Workers(threads, missing.Length, () => (sim: create(), local: new Dictionary<long, float>()), (w, m) =>
            {
                var (sim, local) = w;
                local.Clear();
                float nearest = float.PositiveInfinity;
                sim.Run(sources[missing[m]], extraTicks: 0, onStep: _ =>
                {
                    WorldPos p = sim.PlayerWorld;
                    long key = ((long)Mathf.FloorToInt(p.x / Cell) << 32) | (uint)Mathf.FloorToInt(p.y / Cell);
                    float t = sim.Elapsed(sim.TimingCourse());
                    local[key] = local.TryGetValue(key, out float earliest) ? Earlier(earliest, t) : t;
                    if (closest) nearest = Math.Min(nearest, FinishDistance(sim));
                });
                if (nearest < closestDistance)
                    lock (closestLock)
                        if (nearest < closestDistance) { closestDistance = nearest; closestSource = missing[m]; }
                lock (reached)
                    foreach (var (key, t) in local) reached[key] = reached.TryGetValue(key, out float earliest) ? Earlier(earliest, t) : t;
                Interlocked.Increment(ref replayed);
            });
            var paths = new List<Path>();
            CapturePos origin;
            // Nothing run yet (a new target's first map, before a solve keeps an archive): the frame is the start and the
            // gates it finishes at (a transition's next course's start gate, a course's end gate) and a bonus's finish rect,
            // so avoid boxes can be placed before the first solve.
            var frame = new List<float[]>();
            {
                Simulation sim = create();
                origin = sim.WorldOriginInCapture;
                WorldPos at = sim.PlayerWorld;
                frame.Add(new[] { at.x, at.y, at.x, at.y });
                foreach (MonoBehaviour gate in sim.World.All<startGate>().Cast<MonoBehaviour>().Concat(sim.World.All<endGate>()))
                {
                    courseScript c = gate.GetComponentInParent<courseScript>();
                    int want = gate is startGate ? sim.TargetStart : sim.TargetCourse;
                    if (c == null || want == 0 || c.courseNumber != want) continue;
                    WorldPos g = sim.ToWorld(new ScenePos(gate.transform.position.x, gate.transform.position.y));
                    frame.Add(new[] { g.x, g.y, g.x, g.y });
                }
                if (sim.TargetFinish.W > 0f)
                {
                    WorldPos f = sim.ToWorld(sim.TargetFinish.At);
                    frame.Add(new[] { f.x, f.y, f.x, f.y });
                }
                foreach (var (name, kind, inputs) in routes)
                {
                    var path = new Path { Name = name, Kind = kind };
                    RunResult r = sim.Run(inputs, extraTicks: 0, onStep: _ =>
                    {
                        if (path.TimerStart < 0 && sim.TimingCourse() != null) path.TimerStart = path.Points.Count / 2;
                        Step(path, sim, sim.Elapsed(sim.TimingCourse()));
                    });
                    path.Finished = r.Finished;
                    path.Time = r.Time;
                    paths.Add(path);
                }
            }
            // The map is written as soon as the routes are run, with the cells cached so far, and again every few seconds
            // while the explore prefixes replay (`explored.replayed` of `toReplay`), so the studio draws it a bit at a time.
            // The level is clipped to the first write's bounds until the last write.
            using JsonDocument world = JsonDocument.Parse(File.ReadAllText(worldPath), new JsonDocumentOptions { MaxDepth = 256 });
            var states = WorldStates(world.RootElement).states;
            List<Zip> allZips = Zips(world.RootElement, origin);
            List<Marker> allMarkers = Markers(world.RootElement, origin);
            (float x0, float y0, float x1, float y1) drawn = default;
            List<object> solids = null;
            TreeGround tree = null;
            string Json(bool last)
            {
                KeyValuePair<long, float>[] now;
                lock (reached) now = reached.ToArray();
                int done = Volatile.Read(ref replayed);
                var bounds = Bounds(paths, now.Select(v => v.Key), Cell, paths.Count == 0 && now.Length == 0 ? frame : Enumerable.Empty<float[]>()).Value;
                if (solids == null || last && drawn != bounds)
                {
                    drawn = bounds;
                    solids = Solids(world.RootElement, origin, drawn.x0, drawn.y0, drawn.x1, drawn.y1);
                    tree = Grown(world.RootElement, origin, drawn.x0, drawn.y0, drawn.x1, drawn.y1);
                }
                var (x0, y0, x1, y1) = drawn;
                // Read again each write: the studio adds avoid boxes while the map draws.
                var forced = new OfflineSolver.ForcedPath();
                List<Solve.OptionalBlock> blocks = Solve.OptionalBlocks(course, caps, forced);
                return JsonSerializer.Serialize(new
                {
                    course, capabilities = caps, label,
                    bounds = new[] { bounds.x0, bounds.y0, bounds.x1, bounds.y1 },
                    // The world states the level is drawn in (solids' and markers' `when`); `current`: the capture's.
                    states,
                    solids,
                    zips = allZips.Where(z => z.box[2] >= x0 && z.box[0] <= x1 && z.box[3] >= y0 && z.box[1] <= y1),
                    grown = tree.Shapes, treeSteps = tree.Steps,
                    markers = allMarkers.Where(m => m.x >= x0 && m.x <= x1 && m.y >= y0 && m.y <= y1),
                    avoid = forced.Avoid.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }),
                    via = forced.Via.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }),
                    optional = blocks.Select(b => new
                    {
                        name = b.Name, note = b.Note, on = optional.Contains(b.Name),
                        avoid = b.Boxes.Avoid.Select(x => new[] { x.X0, x.Y0, x.X1, x.Y1 }),
                        via = b.Boxes.Via.Select(x => new[] { x.X0, x.Y0, x.X1, x.Y1 }),
                    }),
                    routes = paths.Select(p => RouteJson(p, p.TimerStart)),
                    explored = new
                    {
                        cell = Cell, runs = memory?.Runs ?? 0, prefixes = sources.Count,
                        replayed = done, toReplay = missing.Length,
                        cells = now.Select(v => new[] { (int)(v.Key >> 32), (int)v.Key, v.Value < 0f ? -1f : (float)Math.Round(v.Value, 2) }),
                    },
                });
            }
            if (missing.Length > 0)
            {
                Task replay = Task.Run(workers);
                do Solve.WriteShared(output, Json(false));
                while (!replay.Wait(3000));
                replay.GetAwaiter().GetResult();
            }
            else workers();
            if (closestSource >= 0)
            {
                List<InputTick> inputs = sources[closestSource].ToList();
                var path = new Path { Name = $"closest: {closestDistance:0} u from the finish", Kind = "closest" };
                Simulation sim = create();
                RunResult r = sim.Run(inputs, extraTicks: 0, onStep: _ => Step(path, sim, sim.Elapsed(sim.TimingCourse())));
                path.Finished = r.Finished;
                path.Time = r.Time;
                paths.Add(path);
                string file = System.IO.Path.ChangeExtension(output, ".closest.json");
                Solve.WriteShared(file, JsonSerializer.Serialize(new { name = label + " closest", distance = Math.Round(closestDistance, 1),
                    inputs = inputs.Select(t => t.ToJson()) }));
                Console.WriteLine($"map {label}: closest run {closestDistance:0} u from the finish, {inputs.Count} inputs -> {file}");
            }
            else if (closest) Console.WriteLine($"map {label}: --closest needs a finish box and an archive");
            if (memory != null && (missing.Length > 0 || !seen.SetEquals(current))) WriteCells(cellsPath, simKey, current, reached);
            Solve.WriteShared(output, Json(true));
            File.WriteAllText(keyPath, outputKey);
            Console.WriteLine($"map {label}: {paths.Count} routes, {sources.Count} explore prefixes ({missing.Length} replayed), {reached.Count} cells ({reached.Count(v => v.Value >= 0f)} timed) -> {output}");
            return 0;
        }

        /// <param name="stretch">One stretch alone (the goal heading for, numbered from 0 as the studio shows it; -1: the whole
        /// run): the best's path from the goal before it to it, the box around that, its goal and the avoid boxes that cut there.</param>
        static int FullTasMap(string trace, JsonElement header, JsonElement fulltas, string goalsFile, string output, int stretch = -1)
        {
            string name = fulltas.GetProperty("name").GetString();
            // The trace's goals are the ones it was captured with; --goals (the run's goals now, a JSON array of labels) wins,
            // so a goal moved since the capture is drawn where it is now. The replay itself checks the trace's goals.
            string[] traced = fulltas.GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray();
            string[] labels = goalsFile == null ? traced : JsonSerializer.Deserialize<string[]>(File.ReadAllText(goalsFile));
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(trace));
            string worldPath = System.IO.Path.ChangeExtension(trace, null) + ".world.json";
            string runFile = System.IO.Path.Combine(dir, "fulltas", name + ".json");
            Func<Simulation> create = Solve.Creator(trace, header);

            var routes = new List<(string name, string kind, List<InputTick> inputs)>();
            if (File.Exists(runFile))
                using (JsonDocument saved = JsonDocument.Parse(File.ReadAllText(runFile), new JsonDocumentOptions { MaxDepth = 256 }))
                {
                    JsonElement root = saved.RootElement;
                    List<InputTick> inputs = Solve.ReadInputs(root);
                    string[] solved = root.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToArray();
                    long[] goalInput = root.GetProperty("goalInputTicks").EnumerateArray().Select(t => t.GetInt64()).ToArray();
                    int keep = FullTas.CurrentInputs(solved, goalInput, labels, inputs.Count);
                    routes.Add(("best so far", "best", inputs.GetRange(0, keep)));
                }
            else
            {
                List<InputTick> own = Solve.ReadInputs(header);
                if (own.Count > 0) routes.Add((System.IO.Path.GetFileNameWithoutExtension(trace), "seed", own));
            }

            var paths = new List<Path>();
            Simulation sim = create();
            CapturePos origin = sim.WorldOriginInCapture;
            float dt = sim.World.FixedDeltaTime;
            foreach (var (routeName, kind, inputs) in routes)
            {
                var path = new Path { Name = routeName, Kind = kind };
                sim.Goals.Limit = traced.Length;
                RunResult r = sim.Run(inputs, extraTicks: 1000000, onStep: _ => { if (stretch < 0 || sim.Progress == stretch) Step(path, sim, sim.Elapsed(sim.TimingCourse())); });
                path.Finished = r.Progress == traced.Length;
                path.Time = r.Progress == 0 ? 0f : r.GoalPhysicsTicks[r.Progress - 1] * dt;
                path.Name += $" ({r.Progress}/{traced.Length} goals)";
                paths.Add(path);
            }

            var goals = new List<(int index, string label, float[] box)>();
            for (int i = 0; i < labels.Length; i++)
            {
                if (!labels[i].StartsWith("at:", StringComparison.Ordinal) || stretch >= 0 && i != stretch) continue;
                float[] v = labels[i].Substring(3).Split(',').Take(4).Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                goals.Add((i + 1, labels[i], new[] { v[0] - v[2] / 2, v[1] - v[3] / 2, v[0] + v[2] / 2, v[1] + v[3] / 2 }));
            }
            OfflineSolver.ForcedPath avoid = Solve.FullTasPaths(name, null, labels);
            if (stretch >= 0) avoid.Avoid.RemoveAll(b => !b.Live(stretch));
            Dictionary<long, float> visits = stretch >= 0 || FullTas.StartChanged(dir, name, FullTas.StartKey(header).ToString("x16")) ? new Dictionary<long, float>()
                : FullTas.ReadVisits(FullTas.VisitsFile(dir, name), labels) ?? new Dictionary<long, float>();
            const float cell = FullTas.VisitCell;

            var (x0, y0, x1, y1) = Bounds(paths, visits.Keys, cell, goals.Select(g => g.box))
                ?? throw new InvalidOperationException("Nothing to draw: no best, no explored cells and no at: goals for Full-TAS " + name);
            using JsonDocument world = JsonDocument.Parse(File.ReadAllText(worldPath), new JsonDocumentOptions { MaxDepth = 256 });

            TreeGround tree = Grown(world.RootElement, origin, x0, y0, x1, y1);
            var result = new
            {
                course = 0, capabilities = header.GetProperty("capabilities").GetString(), label = "fulltas/" + name + (stretch >= 0 ? $" goal {stretch}" : ""), fulltas = name,
                stretch = stretch >= 0 ? stretch : (int?)null,
                bounds = new[] { x0, y0, x1, y1 },
                states = WorldStates(world.RootElement).states,
                solids = Solids(world.RootElement, origin, x0, y0, x1, y1),
                zips = Zips(world.RootElement, origin).Where(z => z.box[2] >= x0 && z.box[0] <= x1 && z.box[3] >= y0 && z.box[1] <= y1),
                grown = tree.Shapes, treeSteps = tree.Steps,
                markers = Markers(world.RootElement, origin).Where(m => m.x >= x0 && m.x <= x1 && m.y >= y0 && m.y <= y1)
                    .Concat(goals.Select(g => new Marker("goal", "goal " + (g.index - 1) + " " + g.label, Round((g.box[0] + g.box[2]) / 2), Round((g.box[1] + g.box[3]) / 2)))),
                // Each box and when it cuts: from After goals reached until Until (0: no end), and its one-way sides (OfflineSolver.Box).
                avoid = avoid.Avoid.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1, b.After, b.Until, b.From }),
                via = goals.Select(g => g.box),
                goals = goals.Select(g => new { index = g.index, label = g.label, box = g.box }),
                routes = paths.Select(p => RouteJson(p, -1)),
                explored = new
                {
                    cell, runs = 0, prefixes = 0,
                    cells = visits.Select(v => new[] { (int)(v.Key >> 32), (int)v.Key, (float)Math.Round(v.Value, 2) }),
                },
            };
            Solve.WriteShared(output, JsonSerializer.Serialize(result));
            Console.WriteLine($"map fulltas/{name}: {paths.Count} routes, {visits.Count} explored cells, {goals.Count} at: goals, {avoid.Avoid.Count} avoid boxes -> {output}");
            return 0;
        }

        /// <summary>The box around the paths' points, the cells (cell units on a side) and the boxes, 300 units of margin
        /// around; null when there is nothing to draw.</summary>
        static (float x0, float y0, float x1, float y1)? Bounds(List<Path> paths, IEnumerable<long> cells, float cell, IEnumerable<float[]> boxes)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            void Grow(float x, float y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            foreach (Path p in paths) for (int i = 0; i < p.Points.Count; i += 2) Grow(p.Points[i], p.Points[i + 1]);
            foreach (long key in cells) { Grow((key >> 32) * cell, (int)key * cell); Grow(((key >> 32) + 1) * cell, ((int)key + 1) * cell); }
            foreach (float[] b in boxes) { Grow(b[0], b[1]); Grow(b[2], b[3]); }
            if (x0 > x1) return null;
            const float pad = 300f;
            return (x0 - pad, y0 - pad, x1 + pad, y1 + pad);
        }

        static object RouteJson(Path p, int timerStart)
        {
            return new
            {
                name = p.Name, kind = p.Kind, finished = p.Finished, time = p.Time, timerStart, points = p.Points,
                cells = p.Cells.Select(v => new[] { (int)(v.Key >> 32), (int)v.Key, v.Value < 0f ? -1f : (float)Math.Round(v.Value, 2) }),
                state = p.State,
            };
        }

        readonly record struct Hash(ulong A, ulong B);


        const int CellsVersion = 3;

        internal static string Stamp(string path)
        {
            if (path == null || !File.Exists(path)) return "-";
            var info = new FileInfo(path);
            return info.Length + "@" + info.LastWriteTimeUtc.Ticks;
        }

        internal static string EngineStamp()
        {
            return string.Join(";", Directory.GetFiles(AppContext.BaseDirectory, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => System.IO.Path.GetFileName(f) + "=" + Stamp(f)));
        }

        static Hash HashInputs(IReadOnlyList<InputTick> inputs)
        {
            // 29 bytes a tick, plus 4 for a freeze stick's vertical (turnY), 8 for a pause and 4 for its frame offset, so older
            // inputs keep their hash.
            byte[] bytes = new byte[inputs.Count * 29 + inputs.Count(t => t.turnY.HasValue) * 4 + inputs.Count(t => t.pause > 0) * 8
                + inputs.Count(t => t.pause > 0 && t.pauseFrame != 0) * 4];
            Span<byte> b = bytes;
            foreach (InputTick t in inputs)
            {
                BinaryPrimitives.WriteSingleLittleEndian(b, t.x);
                BinaryPrimitives.WriteSingleLittleEndian(b[4..], t.y);
                b[8] = (byte)((t.held ? 1 : 0) | (t.press ? 2 : 0) | (t.release ? 4 : 0) | (t.dash ? 8 : 0) | (t.reset ? 16 : 0)
                    | (t.lockX.HasValue ? 32 : 0) | (t.lockY.HasValue ? 64 : 0) | (t.turnX.HasValue ? 128 : 0));
                BinaryPrimitives.WriteInt32LittleEndian(b[9..], t.dashJump);
                BinaryPrimitives.WriteInt32LittleEndian(b[13..], t.springDash);
                BinaryPrimitives.WriteSingleLittleEndian(b[17..], t.lockX ?? 0f);
                BinaryPrimitives.WriteSingleLittleEndian(b[21..], t.lockY ?? 0f);
                BinaryPrimitives.WriteSingleLittleEndian(b[25..], t.turnX ?? 0f);
                b = b[29..];
                if (t.turnY.HasValue)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(b, t.turnY.Value);
                    b = b[4..];
                }
                if (t.pause <= 0) continue;
                BinaryPrimitives.WriteInt32LittleEndian(b, t.pause);
                BinaryPrimitives.WriteInt32LittleEndian(b[4..], t.pauseTick);
                b = b[8..];
                if (t.pauseFrame == 0) continue;
                BinaryPrimitives.WriteInt32LittleEndian(b, t.pauseFrame);
                b = b[4..];
            }
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(bytes, digest);
            return new Hash(BinaryPrimitives.ReadUInt64LittleEndian(digest), BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]));
        }

        static (HashSet<Hash> seen, Dictionary<long, float> reached) ReadCells(string path, string simKey)
        {
            var seen = new HashSet<Hash>();
            var reached = new Dictionary<long, float>();
            if (!File.Exists(path)) return (seen, reached);
            try
            {
                using var reader = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20));
                if (reader.ReadInt32() != CellsVersion || reader.ReadString() != simKey) return (seen, reached);
                for (int n = reader.ReadInt32(); n > 0; n--) seen.Add(new Hash(reader.ReadUInt64(), reader.ReadUInt64()));
                for (int n = reader.ReadInt32(); n > 0; n--) reached[reader.ReadInt64()] = reader.ReadSingle();
            }
            catch (EndOfStreamException) { seen.Clear(); reached.Clear(); }
            return (seen, reached);
        }

        static void WriteCells(string path, string simKey, HashSet<Hash> hashes, Dictionary<long, float> reached)
        {
            string temp = path + ".tmp";
            using (var writer = new BinaryWriter(new BufferedStream(File.Create(temp), 1 << 20)))
            {
                writer.Write(CellsVersion);
                writer.Write(simKey);
                writer.Write(hashes.Count);
                foreach (Hash h in hashes) { writer.Write(h.A); writer.Write(h.B); }
                writer.Write(reached.Count);
                foreach (var (key, t) in reached) { writer.Write(key); writer.Write(t); }
            }
            File.Move(temp, path, true);
        }

        static float Round(float v) { return (float)Math.Round(v, 1); }

        /// <summary>The player's straight-line distance to the finish box, capture frame; infinite without one.</summary>
        static float FinishDistance(Simulation sim)
        {
            CaptureRect f = sim.TargetFinish;
            if (f.W <= 0f) return float.PositiveInfinity;
            CapturePos p = sim.PlayerAtCapture;
            float dx = Math.Max(0f, Math.Abs(p.x - f.At.x) - f.W / 2f), dy = Math.Max(0f, Math.Abs(p.y - f.At.y) - f.H / 2f);
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        static float Earlier(float a, float b) { return a < 0f ? b : b < 0f ? a : Math.Min(a, b); }

        // One tick of a replayed route's path: the world position, the first arrival in its 16-unit cell (`t`, the
        // explore cells' time: the course timer, or the run time where the run is timed from its start) and the
        // player's physical state fingerprint. The studio's ghost compare draws two paths over the map with their first
        // differing fingerprint tick and the per-cell `t` delta (`/api/enginesim/ghost`).
        static void Step(Path path, Simulation sim, float t)
        {
            WorldPos p = sim.PlayerWorld;
            path.Points.Add(Round(p.x));
            path.Points.Add(Round(p.y));
            long key = ((long)Mathf.FloorToInt(p.x / Cell) << 32) | (uint)Mathf.FloorToInt(p.y / Cell);
            path.Cells[key] = Earlier(path.Cells.TryGetValue(key, out float earliest) ? earliest : -1f, t);
            path.State.Add(PhysicalFingerprint(sim));
        }

        // The player's physical state at one tick, rounded to what the path draws: position (0.1), `Velocity`,
        // `momentum` and the body's velocity (0.01), ground/wall, the dash state and the jumps/dashes left. Input and
        // buffer fields (the stick, press buffers, coyote counters) stay out: a ghost compare's divergence tick is
        // where the bodies part, not where two input streams first differ (`jumpBuffer` ticks before anything moves).
        static readonly FieldInfo[] PhysicalFields = new[] { "Velocity", "momentum", "onGround", "OnWall", "dashActive",
                "framesSinceLastDash", "airDashesLeft", "airJumpsLeft", "wallJumpsLeft" }
            .Select(name => typeof(Movement).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(f => f != null).ToArray();

        static int PhysicalFingerprint(Simulation sim)
        {
            var h = new HashCode();
            WorldPos p = sim.PlayerWorld;
            h.Add(Mathf.RoundToInt(p.x * 10f));
            h.Add(Mathf.RoundToInt(p.y * 10f));
            foreach (FieldInfo f in PhysicalFields)
            {
                object value = f.GetValue(sim.Player);
                if (value is Vector2 v) { h.Add(Mathf.RoundToInt(v.x * 100f)); h.Add(Mathf.RoundToInt(v.y * 100f)); }
                else if (value is float x) h.Add(Mathf.RoundToInt(x * 100f));
                else if (value is bool b) h.Add(b ? 1 : 0);
                else if (value is int i) h.Add(i);
                else if (value is Enum e) h.Add(Convert.ToInt32(e));
                else h.Add(value == null ? 0 : value.GetHashCode());
            }
            var body = sim.Physics.PlayerBody();
            h.Add(Mathf.RoundToInt(body.vx * 100f));
            h.Add(Mathf.RoundToInt(body.vy * 100f));
            return h.ToHashCode();
        }

        // A best's human version (humanize): `<best>-human.json` beside it, or in `offline-best\` when the judged best
        // has since moved to `offline-best\judged\`.
        static string HumanFile(string dir, string best)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(best) + "-human.json";
            string root = System.IO.Path.Combine(dir, "offline-best");
            return new[] { System.IO.Path.Combine(root, "judged"), root }.Where(Directory.Exists)
                .Select(d => System.IO.Path.Combine(d, name)).FirstOrDefault(File.Exists);
        }

        static string FastestOfflineBest(string dir, string label)
        {
            string root = System.IO.Path.Combine(dir, "offline-best");
            if (!Directory.Exists(root)) return null;
            return new[] { root, System.IO.Path.Combine(root, "judged") }.Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, label + "-*.json"))
                .Select(f => (f, ok: float.TryParse(System.IO.Path.GetFileNameWithoutExtension(f).Substring(label.Length + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float t), t))
                .Where(e => e.ok).OrderBy(e => e.t).Select(e => e.f).FirstOrDefault();
        }

        // One collider shape of an enabled collider on an object that is on, in game-world coordinates. `Tone`: "blue" or
        // "orange" for a coloured block or spike (colouredBlockSwapper), which only collides while its colour is active.
        // `Up`: a spring's launch direction (its trigger box is the shape). `When`: the world state it's only in
        // (WorldStates, "<state>:<option>"), none: every one. An object that is off in the capture has no shapes for a
        // composite, so it comes as one "Area": its outline paths end to end in V, `Rings` the length of each, filled
        // even-odd (islands and their holes).
        internal sealed record WorldShape(string Type, float[] V, float R, int Layer, string Tag, bool Trigger, bool Spike, bool Moving, bool Player, string Tone = null, float[] Up = null, string When = null, int[] Rings = null);

        // A world state a map switches between: `options`, the capture's (`current`) among them.
        internal sealed record WorldState(string id, string label, string[] options, string current);

        // The world states and, per state root object, the "<state>:<option>" it and everything under it are only in.
        // Area 1: normal or overgrown (OvergrowthLoadScript turns overgrowthEnable on and overgrowthDisable off when
        // Area 1 is overgrown, the reverse otherwise).
        // WorldStates, OnObjects and StateOf read the whole world; a map calls them for every layer it draws, so each result
        // is kept for the world it came from.
        static readonly Dictionary<(JsonElement, string), object> Memo = new Dictionary<(JsonElement, string), object>();
        static T Remember<T>(JsonElement world, string what, Func<T> make)
        {
            lock (Memo)
                if (Memo.TryGetValue((world, what), out object v)) return (T)v;
            T made = make();
            lock (Memo) Memo[(world, what)] = made;
            return made;
        }

        internal static (List<WorldState> states, Dictionary<int, string> roots) WorldStates(JsonElement world) =>
            Remember(world, "states", () => MakeWorldStates(world));

        static (List<WorldState> states, Dictionary<int, string> roots) MakeWorldStates(JsonElement world)
        {
            var states = new List<WorldState>();
            var roots = new Dictionary<int, string>();
            var active = new Dictionary<int, bool>();
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray()) active[o.GetProperty("id").GetInt32()] = o.GetProperty("activeSelf").GetBoolean();
            // Component id → its object; the upgrade boxes under an object (its own and its children's, as
            // GetComponentsInChildren).
            var owner = new Dictionary<int, int>();
            var components = new Dictionary<int, JsonElement>();
            var children = new Dictionary<int, List<int>>();
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
            {
                int id = o.GetProperty("id").GetInt32();
                int parent = o.GetProperty("parent").GetInt32();
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
                list.Add(id);
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    owner[c.GetProperty("id").GetInt32()] = id;
                    components[c.GetProperty("id").GetInt32()] = c;
                }
            }
            var boxesOf = components.Where(kv => kv.Value.GetProperty("type").GetString() == "upgradeBox").ToLookup(kv => owner[kv.Key], kv => kv.Key);
            IEnumerable<int> BoxesUnder(int id)
            {
                foreach (int cid in boxesOf[id]) yield return cid;
                if (children.TryGetValue(id, out var kids))
                    foreach (int k in kids)
                        foreach (int b in BoxesUnder(k)) yield return b;
            }
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    string type = c.GetProperty("type").GetString();
                    if (type != "OvergrowthLoadScript" && type != "Zone2to1TransitionController") continue;
                    var f = Fields(c);
                    // One reference or an array of them.
                    List<int> Refs(string name) => !f.TryGetValue(name, out JsonElement v) ? new List<int>()
                        : (v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : new[] { v }.AsEnumerable())
                            .Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("$ref", out _)).Select(r => r.GetProperty("$ref").GetInt32()).Where(id => active.ContainsKey(id) || owner.ContainsKey(id)).ToList();
                    if (type == "Zone2to1TransitionController")
                    {
                        // The path from Zone 2 into Area 1 above Course 10 opens with Area 1's overgrowth (buying the
                        // omnidash sets both, upgradeBox): the closed ground goes, the open ground and first trunk come.
                        foreach (int id in Refs("closedObject")) roots[id] = "area1:normal|vman";
                        foreach (int id in Refs("openObject").Concat(Refs("trunks").Take(1))) roots[id] = "area1:overgrown";
                        // The rest under the path's root (its zone activators, checkpoints, loading area) is in the tunnel
                        // the open path is: only there when it's open. The other trunks and the closed ground keep theirs.
                        var keep = new HashSet<int>(Refs("closedObject").Concat(Refs("trunks")));
                        int self = owner.TryGetValue(c.GetProperty("id").GetInt32(), out int so) ? so : o.GetProperty("id").GetInt32();
                        if (children.TryGetValue(self, out var under))
                            foreach (int k in under) if (!keep.Contains(k) && !roots.ContainsKey(k)) roots[k] = "area1:overgrown";
                        continue;
                    }
                    List<int> overgrown = Refs("overgrowthEnable"), normal = Refs("overgrowthDisable");
                    foreach (int id in overgrown) roots[id] = "area1:overgrown";
                    foreach (int id in normal) roots[id] = "area1:normal|vman";
                    // Overgrowth (SetA1CourseTiers) hides the Area 1 courses' movement, prestige, breaker-light and
                    // unlock-prestige boxes (a bonus's reward among them) and shows the atom box and boxesToEnable.
                    foreach (int course in Refs("courses"))
                        foreach (int box in BoxesUnder(owner.TryGetValue(course, out int co) ? co : course))
                            if (Fields(components[box]) is var bf && bf.TryGetValue("upgrade", out JsonElement up) && up.ValueKind == JsonValueKind.Number
                                && (up.GetInt32() is 1 or 6 or 10 || up.GetInt32() == 0 && bf.TryGetValue("globalUpgrade", out JsonElement gu) && gu.ValueKind == JsonValueKind.Number && gu.GetInt32() == 10))
                                roots[owner[box]] = "area1:normal|vman";
                    foreach (int box in Refs("boxesToEnable").Append(Refs("rpAtomBox").FirstOrDefault()).Where(b => b != 0))
                        if (owner.TryGetValue(box, out int bo)) roots[bo] = "area1:overgrown";
                    // A Vman speedrun is Area 1 as normal plus the boxes only a Vman run shows (the free omnidash:
                    // upgradeBox.freeVmanOmnidash, on only while VmanScript.isCurrentlyVman).
                    var vmanBoxes = components.Where(kv => kv.Value.GetProperty("type").GetString() == "upgradeBox" && Fields(kv.Value) is var vf
                        && vf.TryGetValue("upgrade", out JsonElement vu) && vu.ValueKind == JsonValueKind.Number && vu.GetInt32() == 1
                        && vf.TryGetValue("movementUpgrade", out JsonElement vm) && vm.ValueKind == JsonValueKind.Number && vm.GetInt32() == 7).Select(kv => owner[kv.Key]).ToList();
                    foreach (int id in vmanBoxes) roots[id] = "area1:vman";
                    states.Add(new WorldState("area1", "Area 1", new[] { "normal", "overgrown", "vman" },
                        overgrown.Any(id => active[id]) ? "overgrown" : vmanBoxes.Any(id => active.TryGetValue(id, out bool on) && on) ? "vman" : "normal"));
                }
            return (states, roots);
        }

        // The objects that are on. With `allStates` (a map of the whole world): as if every zone were loaded (the export
        // holds the world with only the player's zone loaded, and a run loads each zone it enters: ZoneLoader._LoadZone,
        // the zone's root and its ZoneDisableLoadList.toLoad on), in every world state (WorldStates' roots on).
        internal static HashSet<int> OnObjects(JsonElement world, bool allStates) =>
            Remember(world, allStates ? "on:all" : "on", () => MakeOnObjects(world, allStates));

        static HashSet<int> MakeOnObjects(JsonElement world, bool allStates)
        {
            var on = new HashSet<int>();
            var objects = world.GetProperty("objects").EnumerateArray().ToList();
            if (!allStates)
            {
                foreach (JsonElement o in objects) if (o.GetProperty("activeInHierarchy").GetBoolean()) on.Add(o.GetProperty("id").GetInt32());
                return on;
            }
            var zoned = new HashSet<int>(WorldStates(world).roots.Keys);
            foreach (JsonElement o in objects)
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    string type = c.GetProperty("type").GetString();
                    if ((type != "ZoneLoader" && type != "ZoneDisableLoadList") || !c.TryGetProperty("fields", out JsonElement fields)) continue;
                    foreach (JsonElement f in fields.EnumerateArray())
                        if ((f[1].GetString() == "allZones" || f[1].GetString() == "toLoad") && f[2].ValueKind == JsonValueKind.Array)
                            foreach (JsonElement r in f[2].EnumerateArray())
                                if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("$ref", out JsonElement id)) zoned.Add(id.GetInt32());
                }
            var nodes = objects.ToDictionary(o => o.GetProperty("id").GetInt32(),
                o => (parent: o.GetProperty("parent").GetInt32(), self: o.GetProperty("activeSelf").GetBoolean()));
            var known = new Dictionary<int, bool>();
            bool On(int id)
            {
                if (id == 0 || !nodes.TryGetValue(id, out var n)) return true;
                if (known.TryGetValue(id, out bool v)) return v;
                return known[id] = (n.self || zoned.Contains(id)) && On(n.parent);
            }
            foreach (int id in nodes.Keys) if (On(id)) on.Add(id);
            return on;
        }

        // Per object, the world state it's only in (its nearest WorldStates root's); objects in every state are absent.
        static Dictionary<int, string> StateOf(JsonElement world) => Remember(world, "when", () => MakeStateOf(world));

        static Dictionary<int, string> MakeStateOf(JsonElement world)
        {
            Dictionary<int, string> roots = WorldStates(world).roots;
            var parent = new Dictionary<int, int>();
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray()) parent[o.GetProperty("id").GetInt32()] = o.GetProperty("parent").GetInt32();
            var when = new Dictionary<int, string>();
            foreach (int start in parent.Keys)
                for (int id = start; id != 0 && parent.ContainsKey(id); id = parent[id])
                    if (roots.TryGetValue(id, out string state)) { when[start] = state; break; }
            return when;
        }

        static Dictionary<int, (float x, float y, float cos, float sin, bool moving)> Bodies(JsonElement world, CapturePos origin)
        {
            var bodies = new Dictionary<int, (float x, float y, float cos, float sin, bool moving)>();
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                    if (c.GetProperty("type").GetString() == "UnityEngine.Rigidbody2D" && c.TryGetProperty("props", out JsonElement bp))
                    {
                        JsonElement pos = bp.GetProperty("position");
                        double angle = bp.GetProperty("rotation").GetDouble() * Math.PI / 180.0;
                        bool moving = bp.TryGetProperty("bodyType", out JsonElement bt) && bt.GetInt32() != (int)RigidbodyType2D.Static;
                        bodies[c.GetProperty("id").GetInt32()] = (pos.GetProperty("x").GetSingle() - origin.x, pos.GetProperty("y").GetSingle() - origin.y, (float)Math.Cos(angle), (float)Math.Sin(angle), moving);
                    }
            return bodies;
        }

        // Shape or path vertices (relative to the collider's attached body, or scene positions without one) in
        // game-world coordinates, in place.
        static void ToWorld(float[] v, (float x, float y, float cos, float sin, bool moving)? body, CapturePos origin)
        {
            if (body is { } b)
                for (int i = 0; i + 1 < v.Length; i += 2)
                {
                    float lx = v[i], ly = v[i + 1];
                    v[i] = b.x + b.cos * lx - b.sin * ly;
                    v[i + 1] = b.y + b.sin * lx + b.cos * ly;
                }
            else
                for (int i = 0; i + 1 < v.Length; i += 2) { v[i] -= origin.x; v[i + 1] -= origin.y; }
        }

        static (float x, float y, float cos, float sin, bool moving)? BodyOf(JsonElement props, Dictionary<int, (float x, float y, float cos, float sin, bool moving)> bodies)
        {
            return props.TryGetProperty("attachedRigidbody", out JsonElement rb) && rb.ValueKind == JsonValueKind.Object
                && rb.TryGetProperty("$ref", out JsonElement id) && bodies.TryGetValue(id.GetInt32(), out var found) ? found : null;
        }

        // With `allStates`, every zone and world state (OnObjects), and both colours of coloured blocks: each coloured
        // composite's geometry with its tilemap on (`sourceShapes`), whichever colour is active.
        internal static List<WorldShape> Shapes(JsonElement world, CapturePos origin, bool allStates)
        {
            var bodies = Bodies(world, origin);
            HashSet<int> on = OnObjects(world, allStates);
            Dictionary<int, string> when = allStates ? StateOf(world) : new Dictionary<int, string>();
            var tones = new Dictionary<int, string>();
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                    if (c.GetProperty("type").GetString() == "colouredBlockSwapper" && c.TryGetProperty("fields", out JsonElement fields))
                        foreach (JsonElement f in fields.EnumerateArray())
                            if ((f[1].GetString() == "blue" || f[1].GetString() == "orange") && f[2].ValueKind == JsonValueKind.Array)
                                foreach (JsonElement r in f[2].EnumerateArray())
                                    if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("$ref", out JsonElement id)) tones[id.GetInt32()] = f[1].GetString();
            var shapesOut = new List<WorldShape>();
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
            {
                int oid = o.GetProperty("id").GetInt32();
                if (!on.Contains(oid)) continue;
                bool live = o.GetProperty("activeInHierarchy").GetBoolean();
                string state = when.TryGetValue(oid, out string w) ? w : null;
                string tone = tones.TryGetValue(oid, out string t0) ? t0 : null;
                var components = o.GetProperty("components").EnumerateArray().ToList();
                bool spike = components.Any(c => c.GetProperty("type").GetString() == "spikeScript");
                bool player = components.Any(c => c.GetProperty("type").GetString() == "Movement");
                // A spring launches along its transform.up (SpringScript.OnTriggerStay2D → Movement.hitSpring).
                float[] up = null;
                if (components.Any(c => c.GetProperty("type").GetString() == "SpringScript"))
                {
                    JsonElement q = o.GetProperty("rotation");
                    float qx = q.GetProperty("x").GetSingle(), qy = q.GetProperty("y").GetSingle(), qz = q.GetProperty("z").GetSingle(), qw = q.GetProperty("w").GetSingle();
                    up = new[] { 2f * (qx * qy - qw * qz), 1f - 2f * (qx * qx + qz * qz) };
                }
                int layer = o.GetProperty("layer").GetInt32();
                string tag = o.GetProperty("tag").GetString();
                foreach (JsonElement c in components)
                {
                    bool both = allStates && tone != null && c.GetProperty("type").GetString() == "UnityEngine.CompositeCollider2D";
                    if (!c.TryGetProperty(both ? "sourceShapes" : "shapes", out JsonElement shapes) || shapes.ValueKind != JsonValueKind.Object) continue;
                    if (!both && c.TryGetProperty("enabled", out JsonElement enabled) && !enabled.GetBoolean()) continue;
                    JsonElement props = c.GetProperty("props");
                    bool trigger = props.TryGetProperty("isTrigger", out JsonElement t) && t.GetBoolean();
                    (float x, float y, float cos, float sin, bool moving)? body = BodyOf(props, bodies);
                    var loops = new List<(string type, float[] v, float r, int[] rings)>();
                    foreach (JsonElement s in shapes.GetProperty("list").EnumerateArray())
                        loops.Add((s.GetProperty("type").GetString(), s.GetProperty("vertices").EnumerateArray().Select(e => e.GetSingle()).ToArray(),
                            s.TryGetProperty("radius", out JsonElement rad) ? rad.GetSingle() : 0f, null));
                    // An off composite has no shapes: its outline paths, all of them one even-odd area (islands and holes).
                    if (!live && loops.Count == 0 && c.TryGetProperty("paths", out JsonElement paths) && paths.ValueKind == JsonValueKind.Array)
                    {
                        float[][] rings = paths.EnumerateArray().Select(path => path.EnumerateArray().Select(e => e.GetSingle()).ToArray()).Where(p => p.Length >= 6).ToArray();
                        if (rings.Length > 0) loops.Add(("Area", rings.SelectMany(p => p).ToArray(), 0f, rings.Select(p => p.Length).ToArray()));
                    }
                    foreach (var (type, v, r, rings) in loops)
                    {
                        if (v.Length < 2) continue;
                        ToWorld(v, body, origin);
                        shapesOut.Add(new WorldShape(type, v, r, layer, tag, trigger, spike, body is { moving: true }, player, tone, up, state, rings));
                    }
                }
            }
            return shapesOut;
        }

        // Where the drawn level (Solids, every zone and world state) lies: the extent of a map of the whole world.
        internal static (float x0, float y0, float x1, float y1) LevelBounds(JsonElement world, CapturePos origin)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (WorldShape s in Shapes(world, origin, allStates: true))
            {
                if (s.Player || s.Trigger && !s.Spike && s.Up == null) continue;
                for (int i = 0; i + 1 < s.V.Length; i += 2)
                {
                    x0 = Math.Min(x0, s.V[i] - s.R); x1 = Math.Max(x1, s.V[i] + s.R);
                    y0 = Math.Min(y0, s.V[i + 1] - s.R); y1 = Math.Max(y1, s.V[i + 1] + s.R);
                }
            }
            return (x0, y0, x1, y1);
        }

        // `when`: the world state it's only in (WorldShape.When); the viewers draw the state picked.
        internal static List<object> Solids(JsonElement world, CapturePos origin, float x0, float y0, float x1, float y1)
        {
            var solids = new List<object>();
            foreach (WorldShape s in Shapes(world, origin, allStates: true))
            {
                if (s.Trigger && !s.Spike && s.Up == null) continue;
                float[] v = s.V;
                float r = s.R;
                float sx0 = float.MaxValue, sy0 = float.MaxValue, sx1 = float.MinValue, sy1 = float.MinValue;
                for (int i = 0; i + 1 < v.Length; i += 2) { sx0 = Math.Min(sx0, v[i]); sx1 = Math.Max(sx1, v[i]); sy0 = Math.Min(sy0, v[i + 1]); sy1 = Math.Max(sy1, v[i + 1]); }
                if (sx1 + r < x0 || sx0 - r > x1 || sy1 + r < y0 || sy0 - r > y1) continue;
                string k = s.Type == "Polygon" ? "poly" : s.Type == "Area" ? "area" : s.Type == "Circle" ? "circle" : s.Type == "Capsule" ? "capsule" : "line";
                solids.Add(new { k, spike = s.Spike, p = v.Select(Round), r, tone = s.Tone, spring = s.Up?.Select(e => (float)Math.Round(e, 3)), when = s.When, rings = s.Rings });
            }
            return solids;
        }

        // The ground the game's trees grow: TreeController's segments, each turned on when an upgrade box that names
        // it in SegmentsToTrigger is bought (TreeSegmentActivationComponent.growTreePart), as polygons in Solids'
        // format. A fresh world has none of it grown, so the engine and the other layers don't have it; this is where it
        // will be. Off composites keep their outlines (`paths`) but no shapes, so a tree platform is its outline.
        //
        // `Steps`: one label per segment, the fewest tree purchases that grow it. Plant Tree is purchase 1 and grows its
        // segments; each grown segment shows its own box, whose purchase grows the segments it names (one more
        // purchase), and a segment with autoTriggerNext grows the next one along with it. `via`: the box that grows it,
        // `box`: the box it carries.
        internal sealed record TreeGround(List<object> Shapes, List<object> Steps);

        internal static TreeGround Grown(JsonElement world, CapturePos origin, float x0, float y0, float x1, float y1)
        {
            var objects = world.GetProperty("objects").EnumerateArray().ToList();
            var byId = objects.ToDictionary(o => o.GetProperty("id").GetInt32());
            var owner = new Dictionary<int, int>();
            var components = new Dictionary<int, JsonElement>();
            foreach (JsonElement o in objects)
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    owner[c.GetProperty("id").GetInt32()] = o.GetProperty("id").GetInt32();
                    components[c.GetProperty("id").GetInt32()] = c;
                }
            JsonElement? Field(JsonElement c, string name)
            {
                if (c.TryGetProperty("fields", out JsonElement fields))
                    foreach (JsonElement f in fields.EnumerateArray()) if (f[1].GetString() == name) return f[2];
                return null;
            }
            int Ref(JsonElement? v) => v is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("$ref", out JsonElement id) ? id.GetInt32() : 0;
            string Name(int objectId) => byId.TryGetValue(objectId, out JsonElement o) ? o.GetProperty("name").GetString() : "?";

            // Segment GameObject → (tree number, step, via, box).
            var steps = new Dictionary<int, (int tree, int step, string via, string box)>();
            foreach (JsonElement o in objects)
                foreach (JsonElement tc in o.GetProperty("components").EnumerateArray())
                {
                    if (tc.GetProperty("type").GetString() != "TreeController" || Field(tc, "TreeSegments") is not { ValueKind: JsonValueKind.Array } list) continue;
                    int treeId = tc.GetProperty("id").GetInt32(), tree = Field(tc, "treeNumber")?.GetInt32() ?? 0;
                    var segs = list.EnumerateArray().Select(r => components.TryGetValue(Ref(r), out JsonElement s) ? s : (JsonElement?)null).ToList();
                    // Every box on this tree: its GameObject → the segments its purchase grows.
                    var triggers = new Dictionary<int, int[]>();
                    foreach (JsonElement b in components.Values)
                        if (b.GetProperty("type").GetString() == "upgradeBox" && Ref(Field(b, "upgradeTree")) == treeId && Field(b, "SegmentsToTrigger") is { ValueKind: JsonValueKind.Array } grows)
                            triggers[owner[b.GetProperty("id").GetInt32()]] = grows.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                    var queue = new Queue<(int box, int step)>();
                    int plant = owner.TryGetValue(Ref(Field(tc, "plantTreeBox")), out int p) ? p : 0;
                    queue.Enqueue((plant, 1));
                    var seen = new HashSet<int>();
                    void Grow(int index, int step, int via)
                    {
                        if (index < 0 || index >= segs.Count || segs[index] is not { } s || !seen.Add(index)) return;
                        int box = Ref(Field(s, "box"));
                        steps[owner[s.GetProperty("id").GetInt32()]] = (tree, step, Name(via), box != 0 ? Name(box) : null);
                        if (box != 0) queue.Enqueue((box, step + 1));
                        if (Field(s, "autoTriggerNext") is { ValueKind: JsonValueKind.True }) Grow(Field(s, "NextSegmentToAutoTriggerIndex")?.GetInt32() ?? -1, step, via);
                    }
                    while (queue.Count > 0)
                    {
                        var (box, step) = queue.Dequeue();
                        if (triggers.TryGetValue(box, out int[] grows)) foreach (int index in grows) Grow(index, step, box);
                    }
                    // A segment no box grows (none found) keeps its ground, with no step.
                    for (int index = 0; index < segs.Count; index++)
                        if (segs[index] is { } s && !seen.Contains(index))
                            steps[owner[s.GetProperty("id").GetInt32()]] = (tree, 0, null, Ref(Field(s, "box")) is var b and not 0 ? Name(b) : null);
                }

            var parent = objects.ToDictionary(o => o.GetProperty("id").GetInt32(), o => o.GetProperty("parent").GetInt32());
            int SegmentOf(int id)
            {
                for (; id != 0 && parent.ContainsKey(id); id = parent[id]) if (steps.ContainsKey(id)) return id;
                return 0;
            }
            var bodies = Bodies(world, origin);
            HashSet<int> on = OnObjects(world, allStates: true);
            var shapes = new List<object>();
            var extent = new Dictionary<int, (float x0, float y0, float x1, float y1)>();
            foreach (JsonElement o in objects)
            {
                int oid = o.GetProperty("id").GetInt32(), segment = SegmentOf(oid);
                if (on.Contains(oid) || segment == 0) continue;
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    if (!c.TryGetProperty("props", out JsonElement props) || !props.TryGetProperty("isTrigger", out JsonElement trigger) || trigger.GetBoolean()) continue;
                    if (props.TryGetProperty("compositeOperation", out JsonElement op) && op.ValueKind == JsonValueKind.Number && op.GetInt32() != 0) continue;
                    var polygons = new List<float[]>();
                    if (c.TryGetProperty("paths", out JsonElement paths) && c.GetProperty("type").GetString() == "UnityEngine.CompositeCollider2D")
                        foreach (JsonElement path in paths.EnumerateArray()) polygons.Add(path.EnumerateArray().Select(e => e.GetSingle()).ToArray());
                    else if (c.TryGetProperty("shapes", out JsonElement list) && list.ValueKind == JsonValueKind.Object)
                        foreach (JsonElement s in list.GetProperty("list").EnumerateArray())
                            if (s.GetProperty("type").GetString() == "Polygon") polygons.Add(s.GetProperty("vertices").EnumerateArray().Select(e => e.GetSingle()).ToArray());
                    var body = BodyOf(props, bodies);
                    foreach (float[] v in polygons.Where(v => v.Length >= 6))
                    {
                        ToWorld(v, body, origin);
                        float sx0 = float.MaxValue, sy0 = float.MaxValue, sx1 = float.MinValue, sy1 = float.MinValue;
                        for (int i = 0; i + 1 < v.Length; i += 2) { sx0 = Math.Min(sx0, v[i]); sx1 = Math.Max(sx1, v[i]); sy0 = Math.Min(sy0, v[i + 1]); sy1 = Math.Max(sy1, v[i + 1]); }
                        if (sx1 < x0 || sx0 > x1 || sy1 < y0 || sy0 > y1) continue;
                        shapes.Add(new { k = "poly", spike = false, p = v.Select(Round), r = 0f });
                        extent[segment] = extent.TryGetValue(segment, out var e) ? (Math.Min(e.x0, sx0), Math.Min(e.y0, sy0), Math.Max(e.x1, sx1), Math.Max(e.y1, sy1)) : (sx0, sy0, sx1, sy1);
                    }
                }
            }
            var labels = extent.Select(kv => (object)new { tree = steps[kv.Key].tree, step = steps[kv.Key].step, via = steps[kv.Key].via, box = steps[kv.Key].box,
                x = Round((kv.Value.x0 + kv.Value.x1) / 2f), y = Round((kv.Value.y0 + kv.Value.y1) / 2f) }).ToList();
            return new TreeGround(shapes, labels);
        }

        // Upgrade box currencies (upgradeBox.upgradeCurrency).
        static readonly string[] Currencies = { "W", "GP", "NP", "number", "CD", "RP", "BP" };

        // What an upgrade box gives (upgradeBox.upgrade, then .globalUpgrade or .movementUpgrade), the enum order the
        // game serializes: the same tables tools/upgrade-boxes.py decodes with.
        static readonly string[] BoxLocal = { "GLOBAL", "Movement", "cloneCount", "cashPerLoop", "fastCloneChance", "bigCloneChance",
            "prestige", "cloneMult", "DUMMY_cloneCountPlural", "GreenCloneRewardBase", "activateNextBreakerLights",
            "enableCloneDustGeneration", "moreWatts", "baseRP" };
        static readonly string[] BoxGlobal = { "cashPerLoop", "fastCloneChance", "maxCloneFastness", "bigCloneChance", "maxCloneBigness",
            "cloneMult", "spawnNewAtom", "atomLevelChance", "greenCloneClance", "treeGrowth", "unlockPrestige", "openGate",
            "increasedWatts", "increasedGreenPower", "increasedNuclearPower", "area2DoorOpen", "zipMoversUnlocked",
            "exemptCourseFromAtomPrestige", "tripleThreatIncrease", "unlockJiggleDrops", "additionalCDdigits", "compBoostUnlocked",
            "compBoostTime", "compBoostStrength", "IncreasedCloneDust", "moreRefreshOrbs", "unlockTeleporters", "makeCourseBuyMax",
            "redCloneChance", "blueCloneChance", "NpCapIsSoftcap", "compBoostDoublesNP", "vmanTime", "startVmanSpeedrun", "moreWatts",
            "moreGP", "increasedRP", "moreRP", "RPcap", "GPAtomsDontResetCourses", "NpGenerationSpeed", "VmanStartsWithOmniDash",
            "wattPower", "MoreRpCap" };
        static readonly string[] BoxMovement = { "dash", "wallJump", "doubleJump", "swapBlocksOnce", "unlockBlockSwap", "endDemo", "OmniDash", "freeVmanOmnidash" };

        // The upgrade names that only stack cash or a stat (a count, a chance, a duration): a free box giving one of
        // these is a weak bonus beside one that unlocks something (bonusBoost vs bonusUnique).
        static readonly HashSet<string> BoxBoosts = new HashSet<string> { "cloneCount", "cashPerLoop", "fastCloneChance", "bigCloneChance",
            "cloneMult", "DUMMY_cloneCountPlural", "GreenCloneRewardBase", "moreWatts", "baseRP", "maxCloneFastness", "maxCloneBigness",
            "spawnNewAtom", "atomLevelChance", "greenCloneClance", "increasedWatts", "increasedGreenPower", "increasedNuclearPower",
            "tripleThreatIncrease", "additionalCDdigits", "compBoostTime", "compBoostStrength", "IncreasedCloneDust", "moreRefreshOrbs",
            "redCloneChance", "blueCloneChance", "compBoostDoublesNP", "vmanTime", "moreGP", "increasedRP", "moreRP", "RPcap",
            "NpGenerationSpeed", "wattPower", "MoreRpCap" };

        // An upgrade box's buy info (a kind "box" marker). `upgrade`: the enum names as tools/upgrade-boxes.py prints
        // them ("GLOBAL:cashPerLoop", "Movement:doubleJump", "cashPerLoop"); `gives` the same worded. `cap`: how often
        // it can be bought (1: one-off). `category`, first match wins: secret (isSecret, a bonus's hidden reward;
        // `number` its achievement digits) > movement (an ability) > tree (under a TreeController) > bonusUnique/
        // bonusBoost (free, cap 1: a bonus's finish reward; what it gives decides good from weak) > achievement (`ach`
        // the id it triggers) > unlock (any other one-off) > course (a repeatable). `fresh`: on in the fresh world
        // (activeInHierarchy), else only later (grown, another zone).
        internal sealed record BoxInfo(string upgrade, string gives, double cost, string currency, int cap, string category,
            bool fresh, bool secret, bool tree,
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string number = null,
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string ach = null);

        static BoxInfo BuyInfo(JsonElement c, JsonElement o, bool tree)
        {
            var f = Fields(c);
            int pick(string name, int fallback = 0) => f.TryGetValue(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
            string local = BoxLocal[Math.Clamp(pick("upgrade"), 0, BoxLocal.Length - 1)];
            string tail = local == "GLOBAL" ? BoxGlobal[Math.Clamp(pick("globalUpgrade"), 0, BoxGlobal.Length - 1)]
                : local == "Movement" ? BoxMovement[Math.Clamp(pick("movementUpgrade"), 0, BoxMovement.Length - 1)] : local;
            string upgrade = (local == "GLOBAL" ? "GLOBAL:" : local == "Movement" ? "Movement:" : "") + tail;
            string gives = Words(tail) + (local == "GLOBAL" ? " (global)" : local == "Movement" ? " (movement ability)" : " (local)");
            double cost = f.TryGetValue("baseUpgradeCost", out JsonElement bc) && bc.ValueKind == JsonValueKind.Number ? bc.GetDouble() : 0;
            string currency = Currencies[Math.Clamp(pick("upgradeCurrency"), 0, Currencies.Length - 1)];
            int cap = Math.Max(1, pick("Cap", 1));
            bool secret = f.TryGetValue("isSecret", out JsonElement s) && s.ValueKind == JsonValueKind.True;
            string achievement = f.TryGetValue("achievement", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : "";
            string number = achievement.Length > 0 && achievement.All(char.IsDigit) ? achievement : null;
            string ach = achievement.StartsWith("ACH_") ? achievement : null;
            string category = secret ? "secret"
                : local == "Movement" ? "movement"
                : tree ? "tree"
                : cost <= 0 && cap == 1 ? (BoxBoosts.Contains(tail) ? "bonusBoost" : "bonusUnique")
                : ach != null ? "achievement"
                : cap == 1 ? "unlock" : "course";
            return new BoxInfo(upgrade, gives, cost, currency, cap, category,
                o.GetProperty("activeInHierarchy").GetBoolean(), secret, tree, number, ach);
        }

        // Whether a box is a tree upgrade: it names a tree (upgradeTree) or sits under a TreeController's root or segments.
        static bool TreeOf(JsonElement c, JsonElement o, HashSet<int> trees, Dictionary<int, int> parents)
        {
            if (Fields(c).TryGetValue("upgradeTree", out JsonElement t) && t.ValueKind == JsonValueKind.Object) return true;
            for (int id = o.GetProperty("id").GetInt32(); id != 0 && parents.TryGetValue(id, out int p); id = p) if (trees.Contains(id)) return true;
            return false;
        }

        // The in-game name a box shows: its nameDisplay TMP's m_text where an export carries text (the world exports do
        // not serialize TMP fields today), else the object's name comes out of Markers' own `name`.
        static string DisplayName(Dictionary<string, JsonElement> f, Dictionary<int, JsonElement> components)
        {
            return f.TryGetValue("upgradeNameDisplay", out JsonElement v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("$ref", out JsonElement id)
                && components.TryGetValue(id.GetInt32(), out JsonElement t) && Fields(t).TryGetValue("m_text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String && text.GetString().Trim() is { Length: > 0 } shown ? shown : null;
        }

        // The initialisms an upgrade name keeps ("baseRP" → "base RP", "RPcap" → "RP cap").
        static readonly HashSet<string> Initialisms = new HashSet<string> { "W", "GP", "NP", "CD", "RP", "BP" };

        // An upgrade name worded for reading ("cashPerLoop" → "cash per loop"): camel humps split, an initialism's caps
        // run kept whole ("CDdigits" → "CD digits", else the run splits before its last letter, "GPAtoms…" → "GP atoms…").
        static string Words(string name)
        {
            var words = new List<string>();
            string token = "";
            foreach (char ch in name + "_")
            {
                if (ch == '_' || ch == ' ') { if (token.Length > 0) { words.Add(token); token = ""; } continue; }
                if (char.IsUpper(ch))
                {
                    if (token.Length > 0 && !char.IsUpper(token[token.Length - 1])) { words.Add(token); token = ""; }
                    token += ch;
                    continue;
                }
                if (token.Length > 1 && token.All(char.IsUpper))
                {
                    string prefix = Initialisms.Contains(token) ? token
                        : Initialisms.FirstOrDefault(i => token.StartsWith(i, StringComparison.Ordinal)) ?? token.Substring(0, token.Length - 1);
                    words.Add(prefix);
                    token = token.Substring(prefix.Length);
                }
                token += ch;
            }
            return string.Join(" ", words.Select(w => Initialisms.Contains(w.ToUpperInvariant()) ? w.ToUpperInvariant() : w.ToLowerInvariant()));
        }

        internal static List<Marker> Markers(JsonElement world, CapturePos origin)
        {
            // Refill orbs (JiggleDropScript on a trigger capsule) by what they give back: the name says which.
            var kinds = new Dictionary<string, string> { ["startGate"] = "start", ["endGate"] = "end", ["checkpointScript"] = "checkpoint",
                ["TeleporterScript"] = "teleporter", ["FakeCreditsControlScript"] = "falseEnding", ["EndCreditsTrigger"] = "trueEnding", ["JiggleDropScript"] = "refill", ["upgradeBox"] = "box" };
            var markers = new List<Marker>();
            HashSet<int> on = OnObjects(world, allStates: true);
            Dictionary<int, string> when = StateOf(world);
            var bodies = Bodies(world, origin);
            // Every component by id (with its object), every object's parent and the objects carrying an upgrade box:
            // display names and tree lookups need the first two, boxes are emitted whatever the capture has on.
            var components = new Dictionary<int, JsonElement>();
            var owners = new Dictionary<int, int>();
            var parents = new Dictionary<int, int>();
            var boxes = new HashSet<int>();
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
            {
                int id = o.GetProperty("id").GetInt32();
                parents[id] = o.GetProperty("parent").GetInt32();
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    components[c.GetProperty("id").GetInt32()] = c;
                    owners[c.GetProperty("id").GetInt32()] = id;
                    if (c.GetProperty("type").GetString() == "upgradeBox") boxes.Add(id);
                }
            }
            // The root and segments a TreeController hangs its boxes under: a box below one is a tree upgrade.
            var trees = new HashSet<int>();
            foreach (JsonElement c in components.Values)
            {
                if (c.GetProperty("type").GetString() != "TreeController") continue;
                trees.Add(owners[c.GetProperty("id").GetInt32()]);
                if (Fields(c).TryGetValue("TreeSegments", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement r in list.EnumerateArray())
                        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("$ref", out JsonElement id) && owners.TryGetValue(id.GetInt32(), out int segment)) trees.Add(segment);
            }
            var box = PlayerBox(world);
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
            {
                // A box marker comes out however the capture has the box (a tree's boxes only exist once bought): the
                // map shows where one that is only there later will be too, `box.fresh` saying which is which.
                int id = o.GetProperty("id").GetInt32();
                if (!on.Contains(id) && !boxes.Contains(id)) continue;
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    if (!kinds.TryGetValue(c.GetProperty("type").GetString(), out string kind)) continue;
                    string name = o.GetProperty("name").GetString();
                    JsonElement p = o.GetProperty("position");
                    float x = Round(p.GetProperty("x").GetSingle() - origin.x), y = Round(p.GetProperty("y").GetSingle() - origin.y);
                    // A checkpoint's and a gate's trigger: where touching it respawns there, starts or ends the timer.
                    float[][] area = kind is "checkpoint" or "start" or "end" or "falseEnding" or "trueEnding" ? TriggerArea(o, bodies, origin, triggersOnly: false) : null;
                    float[] spawn = null;
                    BoxInfo info = null;
                    if (kind == "checkpoint" && box != null)
                    {
                        // A respawn puts the player's standing box at the checkpoint 12 units down (Movement.respawn;
                        // checkpointScript writes respawnPoint = its position), so x, y is the spawn's centre.
                        float cy = Round(y - 12f);
                        spawn = new[] { Round(x + box.Value.ox - box.Value.hx), Round(cy + box.Value.oy - box.Value.hy),
                            Round(x + box.Value.ox + box.Value.hx), Round(cy + box.Value.oy + box.Value.hy) };
                        y = cy;
                    }
                    if (kind == "refill") kind = name.Contains("Full") ? "fullRefill" : name.Contains("Jump") ? "jumpRefill" : "dashRefill";
                    // An end gate that isn't the course's end (endGate.isEndOfCourse off) only stops the course's timer:
                    // leaving the course that way ends the run without a finish (courseScript.stopTracking).
                    else if (kind == "end" && Fields(c).TryGetValue("isEndOfCourse", out JsonElement eoc) && eoc.ValueKind == JsonValueKind.False) kind = "exit";
                    else if (kind == "box")
                    {
                        // Every upgrade box at its trigger box, where touching it buys it (a bonus's finish): what it
                        // gives and costs in `box`, the marker at the trigger box's middle.
                        info = BuyInfo(c, o, TreeOf(c, o, trees, parents));
                        name = DisplayName(Fields(c), components) ?? name;
                        area = TriggerArea(o, bodies, origin, triggersOnly: true);
                        if (area.Length > 0)
                        {
                            float[] b = area[0];
                            x = Round((b.Where((_, i) => i % 2 == 0).Min() + b.Where((_, i) => i % 2 == 0).Max()) / 2f);
                            y = Round((b.Where((_, i) => i % 2 == 1).Min() + b.Where((_, i) => i % 2 == 1).Max()) / 2f);
                        }
                    }
                    markers.Add(new Marker(kind, name, x, y, area, when.TryGetValue(o.GetProperty("id").GetInt32(), out string state) ? state : null, spawn, info));
                    break;
                }
            }
            return markers;
        }

        // Zip movers and moving platforms (PlatformMover): the moving part runs its Positions in order (local positions,
        // wrapping to the first), each leg a Lerp from the one before (PlatformMover.IE_AdvanceToNextState). `box`: its
        // collider where it rests now; `path`: its box's middle at each position in travel order, starting there;
        // `corridor`: per leg, the hull of the box at both ends (the ground it sweeps). `zip`: a zip mover (PlatformType
        // 1), which only moves once zip movers are unlocked.
        internal sealed record Zip(string name, bool zip, float[] box, float[][] path, float[][] corridor,
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string when = null);

        internal static List<Zip> Zips(JsonElement world, CapturePos origin)
        {
            var zips = new List<Zip>();
            HashSet<int> on = OnObjects(world, allStates: true);
            Dictionary<int, string> when = StateOf(world);
            var bodies = Bodies(world, origin);
            var byId = world.GetProperty("objects").EnumerateArray().ToDictionary(o => o.GetProperty("id").GetInt32());
            foreach (JsonElement o in byId.Values)
            {
                int oid = o.GetProperty("id").GetInt32();
                if (!on.Contains(oid)) continue;
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    if (c.GetProperty("type").GetString() != "PlatformMover") continue;
                    var f = Fields(c);
                    if (!f.TryGetValue("Positions", out JsonElement positions) || positions.ValueKind != JsonValueKind.Array) break;
                    var local = positions.EnumerateArray().Select(p => p.GetProperty("position")).Select(p => (x: p.GetProperty("x").GetSingle(), y: p.GetProperty("y").GetSingle())).ToList();
                    if (local.Count < 2) break;
                    int at = f.TryGetValue("indexInPositions", out JsonElement ix) && ix.ValueKind == JsonValueKind.Number ? Math.Clamp(ix.GetInt32(), 0, local.Count - 1) : 0;
                    // The collider's world box now (its non-trigger shapes).
                    float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                    foreach (float[] poly in TriggerArea(o, bodies, origin, triggersOnly: false))
                        for (int i = 0; i + 1 < poly.Length; i += 2) { x0 = Math.Min(x0, poly[i]); x1 = Math.Max(x1, poly[i]); y0 = Math.Min(y0, poly[i + 1]); y1 = Math.Max(y1, poly[i + 1]); }
                    if (x0 > x1) break;
                    // Local to world: the parent's scale (the moving part isn't rotated), from where it is now.
                    JsonElement lp = o.GetProperty("localPosition"), ls = o.GetProperty("localScale"), gs = o.GetProperty("lossyScale");
                    float sx = gs.GetProperty("x").GetSingle() / Math.Max(1e-6f, ls.GetProperty("x").GetSingle()), sy = gs.GetProperty("y").GetSingle() / Math.Max(1e-6f, ls.GetProperty("y").GetSingle());
                    float nowX = lp.GetProperty("x").GetSingle(), nowY = lp.GetProperty("y").GetSingle();
                    var path = new List<float[]>();
                    var corridor = new List<float[]>();
                    float[] BoxAt((float x, float y) p) => new[] { x0 + (p.x - nowX) * sx, y0 + (p.y - nowY) * sy, x1 + (p.x - nowX) * sx, y1 + (p.y - nowY) * sy };
                    for (int k = 0; k <= local.Count; k++)
                    {
                        var p = local[(at + k) % local.Count];
                        float[] b = BoxAt(p);
                        path.Add(new[] { Round((b[0] + b[2]) / 2), Round((b[1] + b[3]) / 2) });
                        if (k == 0) continue;
                        float[] a = BoxAt(local[(at + k - 1) % local.Count]);
                        corridor.Add(Hull(new[] { (a[0], a[1]), (a[2], a[1]), (a[2], a[3]), (a[0], a[3]), (b[0], b[1]), (b[2], b[1]), (b[2], b[3]), (b[0], b[3]) }));
                    }
                    bool zip = f.TryGetValue("PlatformType", out JsonElement pt) && pt.ValueKind == JsonValueKind.Number && pt.GetInt32() == 1;
                    zips.Add(new Zip(o.GetProperty("parent").GetInt32() is int parent && byId.TryGetValue(parent, out JsonElement po) ? po.GetProperty("name").GetString() : o.GetProperty("name").GetString(),
                        zip, new[] { Round(x0), Round(y0), Round(x1), Round(y1) }, path.ToArray(), corridor.ToArray(), when.TryGetValue(oid, out string w) ? w : null));
                    break;
                }
            }
            return zips;
        }

        // The convex hull of points (monotone chain), as a flat x, y polygon.
        static float[] Hull((float x, float y)[] pts)
        {
            var p = pts.Distinct().OrderBy(q => q.x).ThenBy(q => q.y).ToArray();
            if (p.Length < 3) return p.SelectMany(q => new[] { Round(q.x), Round(q.y) }).ToArray();
            static float Cross((float x, float y) o, (float x, float y) a, (float x, float y) b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            var h = new List<(float x, float y)>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                foreach (var q in pass == 0 ? p : p.Reverse())
                {
                    while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 0) h.RemoveAt(h.Count - 1);
                    h.Add(q);
                }
                h.RemoveAt(h.Count - 1);
            }
            return h.SelectMany(q => new[] { Round(q.x), Round(q.y) }).ToArray();
        }

        static Dictionary<string, JsonElement> Fields(JsonElement component)
        {
            var fields = new Dictionary<string, JsonElement>();
            if (component.TryGetProperty("fields", out JsonElement list))
                foreach (JsonElement f in list.EnumerateArray()) fields[f[1].GetString()] = f[2];
            return fields;
        }

        // The player's standing box (Movement.defaultColliderSize/Offset: what Movement.respawn restores) as half
        // extents and offset, read from the export's player.
        static (float hx, float hy, float ox, float oy)? PlayerBox(JsonElement world)
        {
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    if (c.GetProperty("type").GetString() != "Movement") continue;
                    var f = Fields(c);
                    if (!f.TryGetValue("defaultColliderSize", out JsonElement size) || !f.TryGetValue("defaultColliderOffset", out JsonElement offset)) return null;
                    return (size.GetProperty("x").GetSingle() / 2f, size.GetProperty("y").GetSingle() / 2f,
                        offset.GetProperty("x").GetSingle(), offset.GetProperty("y").GetSingle());
                }
            return null;
        }

        // An object's enabled collider shapes (with `triggersOnly`, its trigger colliders') as game-world polygons (a box
        // collider is one 4-corner polygon).
        static float[][] TriggerArea(JsonElement o, Dictionary<int, (float x, float y, float cos, float sin, bool moving)> bodies, CapturePos origin, bool triggersOnly)
        {
            var area = new List<float[]>();
            foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
            {
                if (!c.TryGetProperty("shapes", out JsonElement shapes) || shapes.ValueKind != JsonValueKind.Object) continue;
                if (c.TryGetProperty("enabled", out JsonElement enabled) && !enabled.GetBoolean()) continue;
                if (triggersOnly && !(c.GetProperty("props").TryGetProperty("isTrigger", out JsonElement t) && t.GetBoolean())) continue;
                var body = BodyOf(c.GetProperty("props"), bodies);
                foreach (JsonElement s in shapes.GetProperty("list").EnumerateArray())
                {
                    if (s.GetProperty("type").GetString() != "Polygon") continue;
                    float[] v = s.GetProperty("vertices").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    if (v.Length < 6) continue;
                    ToWorld(v, body, origin);
                    area.Add(v.Select(Round).ToArray());
                }
            }
            return area.ToArray();
        }
    }
}
