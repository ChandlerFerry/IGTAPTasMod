using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
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
        }

        sealed record Marker(string kind, string name, float x, float y);

        public static int Run(string[] args)
        {
            string trace = args[0];
            string output = Solve.Option(args, "--out", (string)null) ?? throw new ArgumentException("map needs --out <file>");
            int threads = int.Parse(Solve.Option(args, "--threads", (string)null) ?? Math.Max(1, Environment.ProcessorCount - 2).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            JsonElement header = Solve.ReadHeader(trace);
            if (header.TryGetProperty("fulltas", out JsonElement fulltas)) return FullTasMap(trace, header, fulltas, output);
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            string label = course + "_" + caps;
            List<string> optional = Solve.Optional(args);
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(trace));
            string worldPath = System.IO.Path.ChangeExtension(trace, null) + ".world.json";
            string archivePath = System.IO.Path.Combine(dir, "archive", Solve.ArchiveName(label, optional) + ".json");
            string best = FastestOfflineBest(dir, label);
            List<string> seedFiles = Enumerable.Range(1, Math.Max(0, args.Length - 2)).Where(i => args[i] == "--seed").Select(i => args[i + 1]).ToList();

            // Replays are deterministic in the start trace, its world and the engine build, so the map only changes when one
            // of those or a drawn input changes: an unchanged request returns the last map as is.
            string engine = EngineStamp();
            string simKey = engine + "|" + Stamp(trace) + "|" + Stamp(worldPath);
            string outputKey = string.Join("\n", new[] { simKey, Stamp(archivePath), best ?? "-", Stamp(best),
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
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(best), new JsonDocumentOptions { MaxDepth = 256 }))
                    routes.Add(("offline best " + System.IO.Path.GetFileNameWithoutExtension(best).Substring(label.Length + 1) + "s", "best", Solve.ReadInputs(doc.RootElement)));
            OfflineSolver.Memory memory = Solve.LoadMemory(archivePath);
            if (memory != null)
                foreach (var (p, i) in memory.Plateau.Take(MaxPlateau).Select((p, i) => (p, i)))
                    routes.Add(("plateau " + (i + 1), "plateau", p));

            // Each explore prefix's cells are cached by its inputs' hash, so only prefixes new since the last map replay.
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
                        Vector3 p = sim.PlayerWorld;
                        long key = ((long)Mathf.FloorToInt(p.x / Cell) << 32) | (uint)Mathf.FloorToInt(p.y / Cell);
                        float t = sim.Elapsed(sim.TimingCourse());
                        local[key] = local.TryGetValue(key, out float earliest) ? Earlier(earliest, t) : t;
                    });
                    cells[missing[m]] = new SourceCells(local.Keys.ToArray(), local.Values.ToArray());
                }
            })).ToArray();
            var paths = new List<Path>();
            Vector3 origin;
            {
                Simulation sim = create();
                origin = sim.Origin;
                foreach (var (name, kind, inputs) in routes)
                {
                    var path = new Path { Name = name, Kind = kind };
                    RunResult r = sim.Run(inputs, extraTicks: 0, onStep: _ =>
                    {
                        Vector3 p = sim.PlayerWorld;
                        if (path.TimerStart < 0 && sim.TimingCourse() != null) path.TimerStart = path.Points.Count / 2;
                        path.Points.Add(Round(p.x));
                        path.Points.Add(Round(p.y));
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

            var result = new
            {
                course, capabilities = caps, label,
                bounds = new[] { x0, y0, x1, y1 },
                solids = Solids(world.RootElement, origin, x0, y0, x1, y1),
                markers = Markers(world.RootElement, origin).Where(m => m.x >= x0 && m.x <= x1 && m.y >= y0 && m.y <= y1),
                avoid = forced.Avoid.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }),
                via = forced.Via.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }),
                optional = blocks.Select(b => new
                {
                    name = b.Name, note = b.Note, on = optional.Contains(b.Name),
                    avoid = b.Boxes.Avoid.Select(x => new[] { x.X0, x.Y0, x.X1, x.Y1 }),
                    via = b.Boxes.Via.Select(x => new[] { x.X0, x.Y0, x.X1, x.Y1 }),
                }),
                routes = paths.Select(p => new { name = p.Name, kind = p.Kind, finished = p.Finished, time = p.Time, timerStart = p.TimerStart, points = p.Points }),
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

        // A Full-TAS run's map: where its search windows went (FullTas's visits file, colored by the run time of the first
        // arrival), the run's best so far, its at: goals (green boxes, numbered markers) and its avoid boxes (pink).
        static int FullTasMap(string trace, JsonElement header, JsonElement fulltas, string output)
        {
            string name = fulltas.GetProperty("name").GetString();
            string[] labels = fulltas.GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray();
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
            Vector3 origin = sim.Origin;
            float dt = sim.World.FixedDeltaTime;
            foreach (var (routeName, kind, inputs) in routes)
            {
                var path = new Path { Name = routeName, Kind = kind };
                sim.Goals.Limit = labels.Length;
                RunResult r = sim.Run(inputs, extraTicks: 1000000, onStep: _ =>
                {
                    Vector3 p = sim.PlayerWorld;
                    path.Points.Add(Round(p.x));
                    path.Points.Add(Round(p.y));
                });
                path.Finished = r.Progress == labels.Length;
                path.Time = r.Progress == 0 ? 0f : r.GoalPhysicsTicks[r.Progress - 1] * dt;
                path.Name += $" ({r.Progress}/{labels.Length} goals)";
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
            Dictionary<long, float> visits = FullTas.ReadVisits(FullTas.VisitsFile(dir, name), labels) ?? new Dictionary<long, float>();
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

            var result = new
            {
                course = 0, capabilities = header.GetProperty("capabilities").GetString(), label = "fulltas/" + name, fulltas = name,
                bounds = new[] { x0, y0, x1, y1 },
                solids = Solids(world.RootElement, origin, x0, y0, x1, y1),
                markers = Markers(world.RootElement, origin).Where(m => m.x >= x0 && m.x <= x1 && m.y >= y0 && m.y <= y1)
                    .Concat(goals.Select(g => new Marker("goal", "goal " + g.index + " " + g.label, Round((g.box[0] + g.box[2]) / 2), Round((g.box[1] + g.box[3]) / 2)))),
                avoid = avoid.Avoid.Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }),
                via = goals.Select(g => g.box),
                routes = paths.Select(p => new { name = p.Name, kind = p.Kind, finished = p.Finished, time = p.Time, timerStart = -1, points = p.Points }),
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

        // 2: cells keyed in the game world's frame (1 keyed them in the capture's scene frame).
        const int CellsVersion = 2;

        static string Stamp(string path)
        {
            if (path == null || !File.Exists(path)) return "-";
            var info = new FileInfo(path);
            return info.Length + "@" + info.LastWriteTimeUtc.Ticks;
        }

        // The simulator's code (this build and the game assembly it runs) decides every replay.
        static string EngineStamp()
        {
            return string.Join(";", Directory.GetFiles(AppContext.BaseDirectory, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => System.IO.Path.GetFileName(f) + "=" + Stamp(f)));
        }

        static Hash HashInputs(List<InputTick> inputs)
        {
            byte[] bytes = new byte[inputs.Count * 29];
            Span<byte> b = bytes;
            foreach (InputTick t in inputs)
            {
                BinaryPrimitives.WriteSingleLittleEndian(b, t.x);
                BinaryPrimitives.WriteSingleLittleEndian(b[4..], t.y);
                b[8] = (byte)((t.held ? 1 : 0) | (t.press ? 2 : 0) | (t.release ? 4 : 0) | (t.dash ? 8 : 0) | (t.reset ? 16 : 0)
                    | (t.lockX.HasValue ? 32 : 0) | (t.lockY.HasValue ? 64 : 0));
                BinaryPrimitives.WriteInt32LittleEndian(b[9..], t.dashJump);
                BinaryPrimitives.WriteInt32LittleEndian(b[13..], t.springDash);
                BinaryPrimitives.WriteSingleLittleEndian(b[17..], t.lockX ?? 0f);
                BinaryPrimitives.WriteSingleLittleEndian(b[21..], t.lockY ?? 0f);
                BinaryPrimitives.WriteInt32LittleEndian(b[25..], 0);
                b = b[29..];
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

        static string FastestOfflineBest(string dir, string label)
        {
            string root = System.IO.Path.Combine(dir, "offline-best");
            if (!Directory.Exists(root)) return null;
            return new[] { root, System.IO.Path.Combine(root, "judged") }.Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, label + "-*.json"))
                .Select(f => (f, ok: float.TryParse(System.IO.Path.GetFileNameWithoutExtension(f).Substring(label.Length + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float t), t))
                .Where(e => e.ok).OrderBy(e => e.t).Select(e => e.f).FirstOrDefault();
        }

        // Every map coordinate is in the game world's frame: the export's scene position minus its FloatingOrigin.
        static List<object> Solids(JsonElement world, Vector3 origin, float x0, float y0, float x1, float y1)
        {
            // A collider on a Rigidbody2D exports its shapes in the body's local space (the physics puts them on that
            // body); the Grid tilemaps' body sits at the FloatingOrigin, so they're drawn at body position + rotation.
            var bodies = new Dictionary<int, (float x, float y, float cos, float sin)>();
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                    if (c.GetProperty("type").GetString() == "UnityEngine.Rigidbody2D" && c.TryGetProperty("props", out JsonElement bp))
                    {
                        JsonElement pos = bp.GetProperty("position");
                        double angle = bp.GetProperty("rotation").GetDouble() * Math.PI / 180.0;
                        bodies[c.GetProperty("id").GetInt32()] = (pos.GetProperty("x").GetSingle() - origin.x, pos.GetProperty("y").GetSingle() - origin.y, (float)Math.Cos(angle), (float)Math.Sin(angle));
                    }
            var solids = new List<object>();
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
            {
                if (!o.GetProperty("activeInHierarchy").GetBoolean()) continue;
                var components = o.GetProperty("components").EnumerateArray().ToList();
                bool spike = components.Any(c => c.GetProperty("type").GetString() == "spikeScript");
                foreach (JsonElement c in components)
                {
                    if (!c.TryGetProperty("shapes", out JsonElement shapes) || shapes.ValueKind != JsonValueKind.Object) continue;
                    if (c.TryGetProperty("enabled", out JsonElement enabled) && !enabled.GetBoolean()) continue;
                    JsonElement props = c.GetProperty("props");
                    if (props.TryGetProperty("isActiveAndEnabled", out JsonElement live) && !live.GetBoolean()) continue;
                    bool trigger = props.TryGetProperty("isTrigger", out JsonElement t) && t.GetBoolean();
                    if (trigger && !spike) continue;
                    (float x, float y, float cos, float sin)? body = props.TryGetProperty("attachedRigidbody", out JsonElement rb) && rb.ValueKind == JsonValueKind.Object
                        && rb.TryGetProperty("$ref", out JsonElement id) && bodies.TryGetValue(id.GetInt32(), out var found) ? found : null;
                    foreach (JsonElement s in shapes.GetProperty("list").EnumerateArray())
                    {
                        float[] v = s.GetProperty("vertices").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                        if (v.Length < 2) continue;
                        if (body is { } b)
                            for (int i = 0; i + 1 < v.Length; i += 2)
                            {
                                float lx = v[i], ly = v[i + 1];
                                v[i] = b.x + b.cos * lx - b.sin * ly;
                                v[i + 1] = b.y + b.sin * lx + b.cos * ly;
                            }
                        else
                            for (int i = 0; i + 1 < v.Length; i += 2) { v[i] -= origin.x; v[i + 1] -= origin.y; }
                        float r = s.TryGetProperty("radius", out JsonElement rad) ? rad.GetSingle() : 0f;
                        float sx0 = float.MaxValue, sy0 = float.MaxValue, sx1 = float.MinValue, sy1 = float.MinValue;
                        for (int i = 0; i + 1 < v.Length; i += 2) { sx0 = Math.Min(sx0, v[i]); sx1 = Math.Max(sx1, v[i]); sy0 = Math.Min(sy0, v[i + 1]); sy1 = Math.Max(sy1, v[i + 1]); }
                        if (sx1 + r < x0 || sx0 - r > x1 || sy1 + r < y0 || sy0 - r > y1) continue;
                        string type = s.GetProperty("type").GetString();
                        string k = type == "Polygon" ? "poly" : type == "Circle" ? "circle" : type == "Capsule" ? "capsule" : "line";
                        solids.Add(new { k, spike, p = v.Select(Round), r });
                    }
                }
            }
            return solids;
        }

        static List<Marker> Markers(JsonElement world, Vector3 origin)
        {
            var kinds = new Dictionary<string, string> { ["startGate"] = "start", ["endGate"] = "end", ["checkpointScript"] = "checkpoint", ["SpringScript"] = "spring" };
            var markers = new List<Marker>();
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
            {
                if (!o.GetProperty("activeInHierarchy").GetBoolean()) continue;
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    if (!kinds.TryGetValue(c.GetProperty("type").GetString(), out string kind)) continue;
                    JsonElement p = o.GetProperty("position");
                    markers.Add(new Marker(kind, o.GetProperty("name").GetString(), Round(p.GetProperty("x").GetSingle() - origin.x), Round(p.GetProperty("y").GetSingle() - origin.y)));
                    break;
                }
            }
            return markers;
        }
    }
}
