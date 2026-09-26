using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
            string output = Option(args, "--out") ?? throw new ArgumentException("map needs --out <file>");
            int threads = int.Parse(Option(args, "--threads") ?? Math.Max(1, Environment.ProcessorCount / 2).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            JsonElement header = Solve.ReadHeader(trace);
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            string label = course + "_" + caps;
            List<string> optional = Solve.Optional(args);
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(trace));
            string worldPath = System.IO.Path.ChangeExtension(trace, null) + ".world.json";
            Func<Simulation> create = Solve.Creator(trace, header);

            var routes = new List<(string name, string kind, List<InputTick> inputs)>();
            List<InputTick> own = Solve.ReadInputs(header);
            if (own.Count > 0) routes.Add((System.IO.Path.GetFileNameWithoutExtension(trace), "seed", own));
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i] == "--seed")
                    using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(args[i + 1]), new JsonDocumentOptions { MaxDepth = 256 }))
                        routes.Add((doc.RootElement.TryGetProperty("name", out JsonElement n) ? n.GetString() : System.IO.Path.GetFileNameWithoutExtension(args[i + 1]),
                            "seed", Solve.ReadInputs(doc.RootElement)));
            string best = FastestOfflineBest(dir, label);
            if (best != null)
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(best), new JsonDocumentOptions { MaxDepth = 256 }))
                    routes.Add(("offline best " + System.IO.Path.GetFileNameWithoutExtension(best).Substring(label.Length + 1) + "s", "best", Solve.ReadInputs(doc.RootElement)));
            OfflineSolver.Memory memory = Solve.LoadMemory(System.IO.Path.Combine(dir, "archive", Solve.ArchiveName(label, optional) + ".json"));
            if (memory != null)
                foreach (var (p, i) in memory.Plateau.Take(MaxPlateau).Select((p, i) => (p, i)))
                    routes.Add(("plateau " + (i + 1), "plateau", p));

            var reached = new ConcurrentDictionary<long, float>();
            List<List<InputTick>> sources = memory?.Sources.Select(s => s.Inputs).ToList() ?? new List<List<InputTick>>();
            int next = -1;
            var workers = Enumerable.Range(0, Math.Max(1, Math.Min(threads, sources.Count))).Select(_ => Task.Run(() =>
            {
                Simulation sim = create();
                var local = new Dictionary<long, float>();
                for (int k; (k = Interlocked.Increment(ref next)) < sources.Count;)
                    sim.Run(sources[k], extraTicks: 0, onStep: _ =>
                    {
                        Vector3 p = sim.Player.transform.position;
                        long key = ((long)Mathf.FloorToInt(p.x / Cell) << 32) | (uint)Mathf.FloorToInt(p.y / Cell);
                        float t = sim.Elapsed(sim.TimingCourse());
                        local[key] = local.TryGetValue(key, out float best) ? Earlier(best, t) : t;
                    });
                foreach (var pair in local) reached.AddOrUpdate(pair.Key, pair.Value, (_, t) => Earlier(t, pair.Value));
            })).ToArray();
            var paths = new List<Path>();
            {
                Simulation sim = create();
                foreach (var (name, kind, inputs) in routes)
                {
                    var path = new Path { Name = name, Kind = kind };
                    RunResult r = sim.Run(inputs, extraTicks: 0, onStep: _ =>
                    {
                        Vector3 p = sim.Player.transform.position;
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

            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            void Grow(float x, float y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            foreach (Path p in paths) for (int i = 0; i < p.Points.Count; i += 2) Grow(p.Points[i], p.Points[i + 1]);
            foreach (long key in reached.Keys) { Grow((key >> 32) * Cell, (int)key * Cell); Grow(((key >> 32) + 1) * Cell, ((int)key + 1) * Cell); }
            if (x0 > x1) throw new InvalidOperationException("Nothing to draw: no routes and no archive for " + label);
            const float pad = 300f;
            x0 -= pad; y0 -= pad; x1 += pad; y1 += pad;
            var forced = new OfflineSolver.ForcedPath();
            List<Solve.OptionalBlock> blocks = Solve.OptionalBlocks(course, caps, worldPath, forced);
            using JsonDocument world = JsonDocument.Parse(File.ReadAllText(worldPath), new JsonDocumentOptions { MaxDepth = 256 });
            float[] gate = null;
            try { Vector2 g = Solve.CourseGate(worldPath, course); gate = new[] { g.x, g.y }; }
            catch (InvalidOperationException) { }

            var result = new
            {
                course, capabilities = caps, label,
                bounds = new[] { x0, y0, x1, y1 },
                gate,
                solids = Solids(world.RootElement, x0, y0, x1, y1),
                markers = Markers(world.RootElement).Where(m => m.x >= x0 && m.x <= x1 && m.y >= y0 && m.y <= y1),
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
            Console.WriteLine($"map {label}: {paths.Count} routes, {sources.Count} explore prefixes, {reached.Count} cells ({reached.Count(v => v.Value >= 0f)} timed) -> {output}");
            return 0;
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

        static List<object> Solids(JsonElement world, float x0, float y0, float x1, float y1)
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
                        bodies[c.GetProperty("id").GetInt32()] = (pos.GetProperty("x").GetSingle(), pos.GetProperty("y").GetSingle(), (float)Math.Cos(angle), (float)Math.Sin(angle));
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

        static List<Marker> Markers(JsonElement world)
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
                    markers.Add(new Marker(kind, o.GetProperty("name").GetString(), Round(p.GetProperty("x").GetSingle()), Round(p.GetProperty("y").GetSingle())));
                    break;
                }
            }
            return markers;
        }

        static string Option(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i < 0 || i + 1 >= args.Length ? null : args[i + 1];
        }
    }
}
