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

        // `area`: a checkpoint's trigger shapes (polygons, game-world x, y pairs), where touching it sets respawnPoint.
        internal sealed record Marker(string kind, string name, float x, float y,
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float[][] area = null);

        public static int Run(string[] args)
        {
            string trace = args[0];
            string output = Solve.Option(args, "--out", (string)null) ?? throw new ArgumentException("map needs --out <file>");
            int threads = int.Parse(Solve.Option(args, "--threads", (string)null) ?? Math.Max(1, Environment.ProcessorCount - 2).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            JsonElement header = Solve.ReadHeader(trace);
            if (header.TryGetProperty("fulltas", out JsonElement fulltas)) return FullTasMap(trace, header, fulltas, Solve.Option(args, "--goals", (string)null), output);
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            string label = course + "_" + caps;
            List<string> optional = Solve.Optional(args, course, caps);
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(trace));
            string worldPath = System.IO.Path.ChangeExtension(trace, null) + ".world.json";
            string archivePath = System.IO.Path.Combine(dir, "archive", Solve.ArchiveName(label, optional) + ".json");
            string best = FastestOfflineBest(dir, label);
            List<string> seedFiles = Enumerable.Range(1, Math.Max(0, args.Length - 2)).Where(i => args[i] == "--seed").Select(i => args[i + 1]).ToList();

            string engine = EngineStamp();
            string simKey = engine + "|" + Stamp(trace) + "|" + Stamp(worldPath);
            string human = best == null ? null : HumanFile(dir, best);
            string outputKey = string.Join("\n", new[] { simKey, Stamp(archivePath), best ?? "-", Stamp(best), Stamp(human),
                Stamp(System.IO.Path.Combine(AppContext.BaseDirectory, "forced-paths.json")), string.Join("+", optional) }
                .Concat(seedFiles.Select(f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))));
            string keyPath = output + ".key";
            if (File.Exists(output) && File.Exists(keyPath) && File.ReadAllText(keyPath) == outputKey)
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
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(best), new JsonDocumentOptions { MaxDepth = 256 }))
                    routes.Add(("offline best " + System.IO.Path.GetFileNameWithoutExtension(best).Substring(label.Length + 1) + "s", "best", Solve.ReadInputs(doc.RootElement)));
                // Its human version (humanize), so the studio can ghost it against the best or a saved route.
                if (human != null)
                    using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(human), new JsonDocumentOptions { MaxDepth = 256 }))
                        routes.Add(("human " + System.IO.Path.GetFileNameWithoutExtension(best).Substring(label.Length + 1) + "s", "human", Solve.ReadInputs(doc.RootElement)));
            }
            OfflineSolver.Memory memory = Solve.LoadMemory(archivePath);
            if (memory != null)
                foreach (var (p, i) in memory.Plateau.Take(MaxPlateau).Select((p, i) => (p, i)))
                    routes.Add(("plateau " + (i + 1), "plateau", p));

            List<List<InputTick>> sources = memory?.Sources.Select(s => s.Inputs).ToList() ?? new List<List<InputTick>>();
            string cellsPath = System.IO.Path.ChangeExtension(archivePath, ".mapcells.bin");
            Dictionary<Hash, SourceCells> cached = ReadCells(cellsPath, simKey);
            var hashes = new Hash[sources.Count];
            var cells = new SourceCells[sources.Count];
            Parallel.For(0, sources.Count, k =>
            {
                hashes[k] = HashInputs(sources[k]);
                if (cached.TryGetValue(hashes[k], out SourceCells hit)) cells[k] = hit;
            });
            int[] missing = Enumerable.Range(0, sources.Count).Where(k => cells[k] == null).ToArray();
            int next = -1;
            var workers = Enumerable.Range(0, Math.Max(1, Math.Min(threads, missing.Length))).Select(_ => Task.Run(() =>
            {
                if (missing.Length == 0) return;
                Simulation sim = create();
                var local = new Dictionary<long, float>();
                for (int m; (m = Interlocked.Increment(ref next)) < missing.Length;)
                {
                    local.Clear();
                    sim.Run(sources[missing[m]], extraTicks: 0, onStep: _ =>
                    {
                        WorldPos p = sim.PlayerWorldPos();
                        long key = ((long)Mathf.FloorToInt(p.x / Cell) << 32) | (uint)Mathf.FloorToInt(p.y / Cell);
                        float t = sim.Elapsed(sim.TimingCourse());
                        local[key] = local.TryGetValue(key, out float earliest) ? Earlier(earliest, t) : t;
                    });
                    cells[missing[m]] = new SourceCells(local.Keys.ToArray(), local.Values.ToArray());
                }
            })).ToArray();
            var paths = new List<Path>();
            CapturePos origin;
            {
                Simulation sim = create();
                origin = sim.WorldOriginInCapture();
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
            Task.WaitAll(workers);
            var reached = new Dictionary<long, float>();
            foreach (SourceCells s in cells)
                for (int i = 0; i < s.Keys.Length; i++)
                    reached[s.Keys[i]] = reached.TryGetValue(s.Keys[i], out float t) ? Earlier(t, s.Times[i]) : s.Times[i];
            if (missing.Length > 0 || cached.Count != sources.Count) WriteCells(cellsPath, simKey, hashes, cells);

            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            void Grow(float x, float y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            foreach (Path p in paths) for (int i = 0; i < p.Points.Count; i += 2) Grow(p.Points[i], p.Points[i + 1]);
            foreach (long key in reached.Keys) { Grow((key >> 32) * Cell, (int)key * Cell); Grow(((key >> 32) + 1) * Cell, ((int)key + 1) * Cell); }
            if (x0 > x1) throw new InvalidOperationException("Nothing to draw: no routes and no archive for " + label);
            const float pad = 300f;
            x0 -= pad; y0 -= pad; x1 += pad; y1 += pad;
            var forced = new OfflineSolver.ForcedPath();
            List<Solve.OptionalBlock> blocks = Solve.OptionalBlocks(course, caps, forced);
            using JsonDocument world = JsonDocument.Parse(File.ReadAllText(worldPath), new JsonDocumentOptions { MaxDepth = 256 });

            TreeGround tree = Grown(world.RootElement, origin, x0, y0, x1, y1);
            var result = new
            {
                course, capabilities = caps, label,
                bounds = new[] { x0, y0, x1, y1 },
                solids = Solids(world.RootElement, origin, x0, y0, x1, y1),
                grown = tree.Shapes, treeSteps = tree.Steps,
                markers = Markers(world.RootElement, origin).Where(m => m.x >= x0 && m.x <= x1 && m.y >= y0 && m.y <= y1),
                avoid = forced.Avoid.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }),
                via = forced.Via.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }),
                optional = blocks.Select(b => new
                {
                    name = b.Name, note = b.Note, on = optional.Contains(b.Name),
                    avoid = b.Boxes.Avoid.Select(x => new[] { x.X0, x.Y0, x.X1, x.Y1 }),
                    via = b.Boxes.Via.Select(x => new[] { x.X0, x.Y0, x.X1, x.Y1 }),
                }),
                routes = paths.Select(p => new
                {
                    name = p.Name, kind = p.Kind, finished = p.Finished, time = p.Time, timerStart = p.TimerStart, points = p.Points,
                    cells = p.Cells.Select(v => new[] { (int)(v.Key >> 32), (int)v.Key, v.Value < 0f ? -1f : (float)Math.Round(v.Value, 2) }),
                    state = p.State,
                }),
                explored = new
                {
                    cell = Cell, runs = memory?.Runs ?? 0, prefixes = sources.Count,
                    cells = reached.Select(v => new[] { (int)(v.Key >> 32), (int)v.Key, v.Value < 0f ? -1f : (float)Math.Round(v.Value, 2) }),
                },
            };
            string temp = output + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(result));
            File.Move(temp, output, true);
            File.WriteAllText(keyPath, outputKey);
            Console.WriteLine($"map {label}: {paths.Count} routes, {sources.Count} explore prefixes ({missing.Length} replayed), {reached.Count} cells ({reached.Count(v => v.Value >= 0f)} timed) -> {output}");
            return 0;
        }

        static int FullTasMap(string trace, JsonElement header, JsonElement fulltas, string goalsFile, string output)
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
            CapturePos origin = sim.WorldOriginInCapture();
            float dt = sim.World.FixedDeltaTime;
            foreach (var (routeName, kind, inputs) in routes)
            {
                var path = new Path { Name = routeName, Kind = kind };
                sim.Goals.Limit = traced.Length;
                RunResult r = sim.Run(inputs, extraTicks: 1000000, onStep: _ => Step(path, sim, sim.Elapsed(sim.TimingCourse())));
                path.Finished = r.Progress == traced.Length;
                path.Time = r.Progress == 0 ? 0f : r.GoalPhysicsTicks[r.Progress - 1] * dt;
                path.Name += $" ({r.Progress}/{traced.Length} goals)";
                paths.Add(path);
            }

            var goals = new List<(int index, string label, float[] box)>();
            for (int i = 0; i < labels.Length; i++)
            {
                if (!labels[i].StartsWith("at:", StringComparison.Ordinal)) continue;
                float[] v = labels[i].Substring(3).Split(',').Take(4).Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                goals.Add((i + 1, labels[i], new[] { v[0] - v[2] / 2, v[1] - v[3] / 2, v[0] + v[2] / 2, v[1] + v[3] / 2 }));
            }
            OfflineSolver.ForcedPath avoid = Solve.FullTasPaths(name);
            Dictionary<long, float> visits = (FullTas.StartChanged(dir, name, FullTas.StartKey(header).ToString("x16")) ? null
                : FullTas.ReadVisits(FullTas.VisitsFile(dir, name), labels)) ?? new Dictionary<long, float>();
            const float cell = FullTas.VisitCell;

            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            void Grow(float x, float y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            foreach (Path p in paths) for (int i = 0; i < p.Points.Count; i += 2) Grow(p.Points[i], p.Points[i + 1]);
            foreach (long key in visits.Keys) { Grow((key >> 32) * cell, (int)key * cell); Grow(((key >> 32) + 1) * cell, ((int)key + 1) * cell); }
            foreach (var g in goals) { Grow(g.box[0], g.box[1]); Grow(g.box[2], g.box[3]); }
            if (x0 > x1) throw new InvalidOperationException("Nothing to draw: no best, no explored cells and no at: goals for Full-TAS " + name);
            const float pad = 300f;
            x0 -= pad; y0 -= pad; x1 += pad; y1 += pad;
            using JsonDocument world = JsonDocument.Parse(File.ReadAllText(worldPath), new JsonDocumentOptions { MaxDepth = 256 });

            TreeGround tree = Grown(world.RootElement, origin, x0, y0, x1, y1);
            var result = new
            {
                course = 0, capabilities = header.GetProperty("capabilities").GetString(), label = "fulltas/" + name, fulltas = name,
                bounds = new[] { x0, y0, x1, y1 },
                solids = Solids(world.RootElement, origin, x0, y0, x1, y1),
                grown = tree.Shapes, treeSteps = tree.Steps,
                markers = Markers(world.RootElement, origin).Where(m => m.x >= x0 && m.x <= x1 && m.y >= y0 && m.y <= y1)
                    .Concat(goals.Select(g => new Marker("goal", "goal " + g.index + " " + g.label, Round((g.box[0] + g.box[2]) / 2), Round((g.box[1] + g.box[3]) / 2)))),
                avoid = avoid.Avoid.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }),
                via = goals.Select(g => g.box),
                goals = goals.Select(g => new { index = g.index, label = g.label, box = g.box }),
                routes = paths.Select(p => new
                {
                    name = p.Name, kind = p.Kind, finished = p.Finished, time = p.Time, timerStart = -1, points = p.Points,
                    cells = p.Cells.Select(v => new[] { (int)(v.Key >> 32), (int)v.Key, v.Value < 0f ? -1f : (float)Math.Round(v.Value, 2) }),
                    state = p.State,
                }),
                explored = new
                {
                    cell, runs = 0, prefixes = 0,
                    cells = visits.Select(v => new[] { (int)(v.Key >> 32), (int)v.Key, (float)Math.Round(v.Value, 2) }),
                },
            };
            string temp = output + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(result));
            File.Move(temp, output, true);
            Console.WriteLine($"map fulltas/{name}: {paths.Count} routes, {visits.Count} explored cells, {goals.Count} at: goals, {avoid.Avoid.Count} avoid boxes -> {output}");
            return 0;
        }

        readonly record struct Hash(ulong A, ulong B);

        sealed record SourceCells(long[] Keys, float[] Times);

        const int CellsVersion = 2;

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

        static Hash HashInputs(List<InputTick> inputs)
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

        static Dictionary<Hash, SourceCells> ReadCells(string path, string simKey)
        {
            var cells = new Dictionary<Hash, SourceCells>();
            if (!File.Exists(path)) return cells;
            try
            {
                using var reader = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20));
                if (reader.ReadInt32() != CellsVersion || reader.ReadString() != simKey) return cells;
                for (int n = reader.ReadInt32(); n > 0; n--)
                {
                    var hash = new Hash(reader.ReadUInt64(), reader.ReadUInt64());
                    int count = reader.ReadInt32();
                    var keys = new long[count];
                    var times = new float[count];
                    for (int i = 0; i < count; i++) { keys[i] = reader.ReadInt64(); times[i] = reader.ReadSingle(); }
                    cells[hash] = new SourceCells(keys, times);
                }
            }
            catch (EndOfStreamException) { cells.Clear(); }
            return cells;
        }

        static void WriteCells(string path, string simKey, Hash[] hashes, SourceCells[] cells)
        {
            string temp = path + ".tmp";
            var written = new HashSet<Hash>();
            using (var writer = new BinaryWriter(new BufferedStream(File.Create(temp), 1 << 20)))
            {
                writer.Write(CellsVersion);
                writer.Write(simKey);
                writer.Write(hashes.Distinct().Count());
                for (int k = 0; k < hashes.Length; k++)
                {
                    if (!written.Add(hashes[k])) continue;
                    writer.Write(hashes[k].A);
                    writer.Write(hashes[k].B);
                    writer.Write(cells[k].Keys.Length);
                    for (int i = 0; i < cells[k].Keys.Length; i++) { writer.Write(cells[k].Keys[i]); writer.Write(cells[k].Times[i]); }
                }
            }
            File.Move(temp, path, true);
        }

        static float Round(float v) { return (float)Math.Round(v, 1); }

        static float Earlier(float a, float b) { return a < 0f ? b : b < 0f ? a : Math.Min(a, b); }

        // One tick of a replayed route's path: the world position, the first arrival in its 16-unit cell (`t`, the
        // explore cells' time: the course timer, or the run time where the run is timed from its start) and the
        // player's physical state fingerprint. The studio's ghost compare draws two paths over the map with their first
        // differing fingerprint tick and the per-cell `t` delta (`/api/enginesim/ghost`).
        static void Step(Path path, Simulation sim, float t)
        {
            WorldPos p = sim.PlayerWorldPos();
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
            WorldPos p = sim.PlayerWorldPos();
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
        // `Up`: a spring's launch direction (its trigger box is the shape).
        internal sealed record WorldShape(string Type, float[] V, float R, int Layer, string Tag, bool Trigger, bool Spike, bool Moving, bool Player, string Tone = null, float[] Up = null);

        // The objects that are on. With `allStates` (a map of the whole world): as if every zone were loaded. The export
        // holds the world with only the player's zone loaded, and a run loads each zone it enters (ZoneLoader._LoadZone:
        // the zone's root and its ZoneDisableLoadList.toLoad on).
        internal static HashSet<int> OnObjects(JsonElement world, bool allStates)
        {
            var on = new HashSet<int>();
            var objects = world.GetProperty("objects").EnumerateArray().ToList();
            if (!allStates)
            {
                foreach (JsonElement o in objects) if (o.GetProperty("activeInHierarchy").GetBoolean()) on.Add(o.GetProperty("id").GetInt32());
                return on;
            }
            var zoned = new HashSet<int>();
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

        // With `allStates`, both colours of coloured blocks too: each coloured composite's geometry with its tilemap on
        // (`sourceShapes`), whichever colour is active.
        internal static List<WorldShape> Shapes(JsonElement world, CapturePos origin, bool allStates)
        {
            var bodies = Bodies(world, origin);
            HashSet<int> on = OnObjects(world, allStates);
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
                    foreach (JsonElement s in shapes.GetProperty("list").EnumerateArray())
                    {
                        float[] v = s.GetProperty("vertices").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                        if (v.Length < 2) continue;
                        ToWorld(v, body, origin);
                        float r = s.TryGetProperty("radius", out JsonElement rad) ? rad.GetSingle() : 0f;
                        shapesOut.Add(new WorldShape(s.GetProperty("type").GetString(), v, r, layer, tag, trigger, spike, body is { moving: true }, player, tone, up));
                    }
                }
            }
            return shapesOut;
        }

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
                string k = s.Type == "Polygon" ? "poly" : s.Type == "Circle" ? "circle" : s.Type == "Capsule" ? "capsule" : "line";
                solids.Add(new { k, spike = s.Spike, p = v.Select(Round), r, tone = s.Tone, spring = s.Up?.Select(e => (float)Math.Round(e, 3)) });
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

        internal static List<Marker> Markers(JsonElement world, CapturePos origin)
        {
            // Refill orbs (JiggleDropScript on a trigger capsule) by what they give back: the name says which.
            var kinds = new Dictionary<string, string> { ["startGate"] = "start", ["endGate"] = "end", ["checkpointScript"] = "checkpoint",
                ["TeleporterScript"] = "teleporter", ["JiggleDropScript"] = "refill" };
            var markers = new List<Marker>();
            HashSet<int> on = OnObjects(world, allStates: true);
            var bodies = Bodies(world, origin);
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
            {
                if (!on.Contains(o.GetProperty("id").GetInt32())) continue;
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    if (!kinds.TryGetValue(c.GetProperty("type").GetString(), out string kind)) continue;
                    string name = o.GetProperty("name").GetString();
                    if (kind == "refill") kind = name.Contains("Full") ? "fullRefill" : name.Contains("Jump") ? "jumpRefill" : "dashRefill";
                    JsonElement p = o.GetProperty("position");
                    markers.Add(new Marker(kind, name, Round(p.GetProperty("x").GetSingle() - origin.x), Round(p.GetProperty("y").GetSingle() - origin.y),
                        kind == "checkpoint" ? TriggerArea(o, bodies, origin) : null));
                    break;
                }
            }
            return markers;
        }

        // An object's enabled collider shapes as game-world polygons (a box collider is one 4-corner polygon).
        static float[][] TriggerArea(JsonElement o, Dictionary<int, (float x, float y, float cos, float sin, bool moving)> bodies, CapturePos origin)
        {
            var area = new List<float[]>();
            foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
            {
                if (!c.TryGetProperty("shapes", out JsonElement shapes) || shapes.ValueKind != JsonValueKind.Object) continue;
                if (c.TryGetProperty("enabled", out JsonElement enabled) && !enabled.GetBoolean()) continue;
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
