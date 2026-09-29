using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    // Where the game's ultra (CheckForSuperJump: a hyper at least 4 u below lastHyperY, the dash the way the pre-dash
    // momentum went, "Did an ultra!") can be done: every floor a hyper can land on, and from launch points along them,
    // two chained wavedashes replayed in the engine, recording each hyper that passes the game's own check.
    static class Ultras
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo CanHyper = typeof(Movement).GetField("canHyper", Fields);
        static readonly FieldInfo LastHyperY = typeof(Movement).GetField("lastHyperY", Fields);
        static readonly FieldInfo PreDashMomentum = typeof(Movement).GetField("preDashMomentum", Fields);
        static readonly FieldInfo DashDirection = typeof(Movement).GetField("dashDirection", Fields);
        static readonly FieldInfo DefaultSize = typeof(Movement).GetField("defaultColliderSize", Fields);
        static readonly FieldInfo DefaultOffset = typeof(Movement).GetField("defaultColliderOffset", Fields);
        static readonly FieldInfo DashSize = typeof(Movement).GetField("dashColliderSize", Fields);
        static readonly FieldInfo DashOffset = typeof(Movement).GetField("dashColliderOffset", Fields);

        // Floor kinds, in the output's order.
        static readonly string[] Kinds = { "metal", "moss", "moving" };

        // Launch search: the first wavedash (hop, then a down-diagonal dash `Delay` input ticks later whose jump is
        // pressed on dash frame `Jump`), then the second one `Gap` input ticks after the first dash.
        static readonly int[] Delay = { 1, 2, 3 };
        static readonly int[] Jump = { 4, 6, 8, 10 };
        static readonly int[] Hold = { 3, 24 };
        const int MaxGap = 14;
        const int Settle = 3;
        const float Reach = 2600f, MaxDrop = 2000f;

        sealed record Segment(float X0, float Y0, float X1, float Y1, int Kind, bool Stand);

        readonly struct Box
        {
            public readonly float X0, Y0, X1, Y1;
            public Box(float x0, float y0, float x1, float y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        }

        sealed class Grid
        {
            const float Cell = 512f;
            readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            public readonly List<Map.WorldShape> Shapes = new List<Map.WorldShape>();
            readonly List<Box> _bounds = new List<Box>();

            public void Add(Map.WorldShape s)
            {
                Box b = Bounds(s);
                int id = Shapes.Count;
                Shapes.Add(s);
                _bounds.Add(b);
                for (int cx = Key(b.X0); cx <= Key(b.X1); cx++)
                    for (int cy = Key(b.Y0); cy <= Key(b.Y1); cy++)
                    {
                        long k = ((long)cx << 32) | (uint)cy;
                        if (!_cells.TryGetValue(k, out List<int> list)) _cells[k] = list = new List<int>();
                        list.Add(id);
                    }
            }

            static int Key(float v) { return (int)Math.Floor(v / Cell); }

            public bool Hits(Box q)
            {
                for (int cx = Key(q.X0); cx <= Key(q.X1); cx++)
                    for (int cy = Key(q.Y0); cy <= Key(q.Y1); cy++)
                        if (_cells.TryGetValue(((long)cx << 32) | (uint)cy, out List<int> list))
                            foreach (int id in list)
                            {
                                Box b = _bounds[id];
                                if (b.X1 <= q.X0 || b.X0 >= q.X1 || b.Y1 <= q.Y0 || b.Y0 >= q.Y1) continue;
                                if (Overlaps(Shapes[id], q)) return true;
                            }
                return false;
            }

            // Whether the segment a→b touches or crosses a shape.
            public bool Crosses(float ax, float ay, float bx, float by)
            {
                var q = new Box(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
                for (int cx = Key(q.X0); cx <= Key(q.X1); cx++)
                    for (int cy = Key(q.Y0); cy <= Key(q.Y1); cy++)
                        if (_cells.TryGetValue(((long)cx << 32) | (uint)cy, out List<int> list))
                            foreach (int id in list)
                            {
                                Box b = _bounds[id];
                                if (b.X1 < q.X0 || b.X0 > q.X1 || b.Y1 < q.Y0 || b.Y0 > q.Y1) continue;
                                if (SegmentCrosses(Shapes[id], b, ax, ay, bx, by)) return true;
                            }
                return false;
            }
        }

        public static int Run(string[] args)
        {
            string trace = args[0];
            string output = Solve.Option(args, "--out", (string)null) ?? throw new ArgumentException("ultras needs --out <file>");
            string coursesPath = Solve.Option(args, "--courses", (string)null);
            int threads = Solve.Option(args, "--threads", Math.Max(1, Environment.ProcessorCount - 2));
            float spacing = Solve.Option(args, "--spacing", 32f);
            int limit = Solve.Option(args, "--limit", int.MaxValue);
            string worldPath = System.IO.Path.ChangeExtension(trace, null) + ".world.json";
            string outputKey = string.Join("\n", Map.EngineStamp(), Map.Stamp(trace), Map.Stamp(worldPath), Map.Stamp(coursesPath),
                spacing.ToString(CultureInfo.InvariantCulture), limit.ToString(CultureInfo.InvariantCulture));
            string keyPath = output + ".key";
            if (File.Exists(output) && File.Exists(keyPath) && File.ReadAllText(keyPath) == outputKey)
            {
                Console.WriteLine($"ultras: inputs unchanged -> {output}");
                return 0;
            }
            JsonElement header = Solve.ReadHeader(trace);
            // The launches here model FloatingOrigin's rebase themselves (see Play), on starts placed anywhere: the engine's own is off.
            Func<Simulation> engine = Solve.Creator(trace, header);
            Func<Simulation> create = () => { Simulation sim = engine(); sim.Rebases = false; return sim; };
            using JsonDocument world = JsonDocument.Parse(File.ReadAllText(worldPath), new JsonDocumentOptions { MaxDepth = 256 });

            Simulation probe = create();
            CapturePos origin = probe.WorldOriginInCapture;
            Vector2 stand = (Vector2)DefaultSize.GetValue(probe.Player), standOffset = (Vector2)DefaultOffset.GetValue(probe.Player);
            Vector2 dash = (Vector2)DashSize.GetValue(probe.Player), dashOffset = (Vector2)DashOffset.GetValue(probe.Player);
            FloatingOrigin rebase = probe.World.All<FloatingOrigin>().FirstOrDefault();
            _threshold = rebase?.Threshold ?? float.PositiveInfinity;
            _use2D = rebase?.Use2DDistance ?? false;

            // Floors only where the launches' engine (the world as exported) has them; the drawn solids show every zone.
            List<Map.WorldShape> shapes = Map.Shapes(world.RootElement, origin, allStates: false);
            var blockers = new Grid();
            var spikes = new Grid();
            foreach (Map.WorldShape s in shapes)
            {
                if (s.Player) continue;
                if (s.Spike) spikes.Add(s);
                else if (!s.Trigger) blockers.Add(s);
            }
            List<Segment> floors = Floors(shapes, blockers, spikes, stand.x / 2f, dash.y, stand.y);

            // Launch points: along every floor the dot can stand on, every `spacing` units and at both ends, where a
            // hyper-able floor more than 4 lower lies within reach that way, or where a run that way can cross the
            // FloatingOrigin threshold (see Play).
            var lower = new Grid();
            foreach (Segment f in floors)
                lower.Add(new Map.WorldShape("Edges", new[] { f.X0, f.Y0, f.X1, f.Y1 }, 0f, 7, "", false, false, false, false));
            var launches = new List<(float x, float y, float side)>();
            foreach (Segment f in floors.Where(f => f.Stand))
            {
                float length = f.X1 - f.X0;
                int n = Math.Max(1, (int)Math.Ceiling(length / spacing));
                for (int i = 0; i <= n; i++)
                {
                    float x = f.X0 + length * i / n, y = f.Y0 + (f.Y1 - f.Y0) * i / n;
                    foreach (float side in new[] { -1f, 1f })
                    {
                        var reach = side > 0 ? new Box(x, y - MaxDrop, x + Reach, y - 4f) : new Box(x - Reach, y - MaxDrop, x, y - 4f);
                        // Near the threshold on the inside, a jump, a climb or a run can cross it before the next hyper.
                        float scene = Scene(new Vector2(x + origin.x, y + origin.y)).magnitude;
                        if (lower.Hits(reach) || scene <= _threshold && scene > _threshold - Reach) launches.Add((x, y, side));
                    }
                }
            }
            Console.WriteLine($"ultras: {floors.Count} floor segments ({floors.Sum(f => f.X1 - f.X0):0} u), {launches.Count} launches ({threads} threads)");
            if (launches.Count > limit) launches = launches.Where((_, i) => i % Math.Max(1, launches.Count / limit) == 0).Take(limit).ToList();

            var found = new List<object>[launches.Count];
            int next = -1, done = 0;
            DateTime began = DateTime.UtcNow;
            var workers = Enumerable.Range(0, Math.Max(1, Math.Min(threads, launches.Count))).Select(_ => Task.Run(() =>
            {
                Simulation sim = create();
                SimSnapshot start = sim.Advance(new List<InputTick>());
                for (int k; (k = Interlocked.Increment(ref next)) < launches.Count;)
                {
                    found[k] = Search(sim, start, launches[k], stand, standOffset, dash, dashOffset);
                    int d = Interlocked.Increment(ref done);
                    if (d % 500 == 0) Console.WriteLine($"[{(DateTime.UtcNow - began):mm\\:ss}] {d}/{launches.Count} launches");
                }
            })).ToArray();
            Task.WaitAll(workers);

            // The whole level (every zone and world state), not only the floors of the zones the export had loaded.
            var level = Map.LevelBounds(world.RootElement, origin);
            float x0 = Math.Min(level.x0, floors.Min(f => f.X0)) - 300f, x1 = Math.Max(level.x1, floors.Max(f => f.X1)) + 300f;
            float y0 = Math.Min(level.y0, floors.Min(f => Math.Min(f.Y0, f.Y1))) - 300f, y1 = Math.Max(level.y1, floors.Max(f => Math.Max(f.Y0, f.Y1))) + 300f;
            // A course at its start gate; a bonus at its finish (its reward box), where the bonus is, not the course it
            // starts from.
            var labels = new List<(string name, float x, float y)>();
            if (coursesPath != null && File.Exists(coursesPath))
                using (JsonDocument courses = JsonDocument.Parse(File.ReadAllText(coursesPath)))
                    foreach (JsonElement c in courses.RootElement.EnumerateArray())
                    {
                        if (c.TryGetProperty("to", out JsonElement to) && to.ValueKind != JsonValueKind.Null) continue;
                        if (c.TryGetProperty("overgrown", out JsonElement og) && og.ValueKind == JsonValueKind.True) continue;
                        int id = c.GetProperty("id").GetInt32();
                        string name = c.TryGetProperty("name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : "Course " + id;
                        bool bonus = c.TryGetProperty("endW", out JsonElement ew) && ew.GetSingle() > 0f && c.TryGetProperty("endH", out JsonElement eh) && eh.GetSingle() > 0f;
                        labels.Add(bonus ? (name, c.GetProperty("endX").GetSingle(), c.GetProperty("endY").GetSingle())
                            : (name, c.GetProperty("x").GetSingle(), c.GetProperty("y").GetSingle()));
                    }
            // The dot hovers a few units over its floor (the ground rays reach 22 below it), so each ultra's height from
            // lastHyperY is put on the floor segment just under it.
            var ultras = found.Where(f => f != null).SelectMany(f => f).Cast<object[]>().ToList();
            foreach (object[] u in ultras)
            {
                int under = FloorUnder(floors, (float)u[3], (float)u[4]);
                if (under >= 0) u[4] = R(FloorY(floors[under], (float)u[3]));
            }

            // Every floor pair, then each engine ultra on the pair it joins: lastHyperY from the launch floor (a single
            // hyper) or the first hyper's floor (the second of a chain) to the ultra's floor. The biggest drop is the
            // pair's example; a pair only the engine found (the flight line check missed it, or an origin shift) is added.
            float hop = HopHeight(probe, stand, standOffset, floors);
            List<Pair> pairs = Pairs(floors, blockers, stand.x / 2f, stand.y / 2f, hop);
            Console.WriteLine($"ultras: a held jump rises {hop:0} u; {pairs.Count} floor pairs by flight line");
            var pairIndex = new Dictionary<(int, int), int>();
            for (int i = 0; i < pairs.Count; i++) pairIndex[(pairs[i].From, pairs[i].To)] = i;
            var example = new Dictionary<int, int>();
            for (int k = 0; k < ultras.Count; k++)
            {
                object[] u = ultras[k];
                bool chained = (int)u[6] == 2;
                float fx = chained ? (float)u[13] : (float)u[0], fyv = chained ? (float)u[15] : (float)u[1];
                int from = FloorNear(floors, fx, fyv), to = FloorNear(floors, (float)u[3], (float)u[4]);
                if (from < 0 || to < 0) continue;
                if (!pairIndex.TryGetValue((from, to), out int p))
                {
                    pairIndex[(from, to)] = p = pairs.Count;
                    pairs.Add(new Pair(from, to, (float)u[5], fx, FloorY(floors[from], fx), (float)u[3], FloorY(floors[to], (float)u[3]), false));
                }
                if (!example.TryGetValue(p, out int e) || (float)u[5] > (float)ultras[e][5]) example[p] = k;
            }
            Map.TreeGround tree = Map.Grown(world.RootElement, origin, x0, y0, x1, y1);
            var result = new
            {
                world = System.IO.Path.GetFileName(worldPath),
                bounds = new[] { x0, y0, x1, y1 },
                // The world states the level is drawn in (solids' and markers' `when`); `current`: the capture's, which the
                // floors and ultras are from.
                states = Map.WorldStates(world.RootElement).states,
                player = new { stand = new[] { stand.x, stand.y }, dash = new[] { dash.x, dash.y } },
                kinds = Kinds,
                floors = floors.Select(f => new object[] { R(f.X0), R(f.Y0), R(f.X1), R(f.Y1), f.Kind, f.Stand ? 1 : 0 }),
                solids = Map.Solids(world.RootElement, origin, x0, y0, x1, y1),
                // The trees' ground, where it grows once its tree upgrades are bought (none of it in a fresh world), and
                // each segment's place in the tree's purchase order.
                grown = tree.Shapes, treeSteps = tree.Steps,
                // Start/end gates, checkpoints, springs, teleporters, refill orbs and secrets (bonus rewards), in every zone.
                markers = Map.Markers(world.RootElement, origin).Where(m => m.x >= x0 && m.x <= x1 && m.y >= y0 && m.y <= y1),
                labels = labels.GroupBy(l => (l.x, l.y)).Select(g => new { name = string.Join(" · ", g.Select(l => l.name)), x = g.Key.x, y = g.Key.y }),
                // Ground-layer triggers (upgrade boxes): the ground rays hit them, so they are floors too.
                boxes = shapes.Where(s => s.Layer == 7 && s.Trigger && !s.Spike && !s.Player && s.Type == "Polygon").Select(s => s.V.Select(R)),
                launches = launches.Count,
                // FloatingOrigin in a fresh VMAN world: the rebase fires when the player leaves this circle (a pair of
                // vertical lines with Use2DDistance).
                rebase = new
                {
                    threshold = _threshold, use2D = _use2D, center = new[] { R(-origin.x), R(-origin.y) },
                    // [x, y, physics tick, left origin x, y] of each rebase on the run's best so far.
                    route = RouteRebases(trace, header, create, origin),
                },
                ultraFields = new[] { "fromX", "fromY", "side", "x", "y", "drop", "hyper", "hold", "delay", "jump", "gap", "jump2", "landX", "prevX", "shift", "prevY" },
                ultras,
                // [from floor, to floor, drop, from x, y, to x, y, flight line clear 0/1, example ultra or -1].
                pairFields = new[] { "from", "to", "drop", "ax", "ay", "bx", "by", "line", "ultra" },
                pairs = pairs.Select((p, i) => new object[] { p.From, p.To, R(p.Drop), R(p.Ax), R(p.Ay), R(p.Bx), R(p.By), p.Line ? 1 : 0,
                    example.TryGetValue(i, out int e) ? e : -1 }),
            };
            string temp = output + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(result));
            File.Move(temp, output, true);
            File.WriteAllText(keyPath, outputKey);
            Console.WriteLine($"ultras: {pairs.Count} floor pairs ({example.Count} with an engine ultra), {ultras.Count} ultras from {found.Count(f => f is { Count: > 0 })} of {launches.Count} launches -> {output}");
            return 0;
        }

        // Where the Full-TAS run's best so far (the trace's `fulltas/<run>.json`) makes FloatingOrigin rebase: these sims
        // run with Rebases off, so each crossing of the threshold from the current origin is found here, and the next
        // one is measured from the new origin. `center`: the origin the player left (game-world coordinates).
        static List<object> RouteRebases(string trace, JsonElement header, Func<Simulation> create, CapturePos origin)
        {
            var points = new List<object>();
            if (float.IsInfinity(_threshold) || !header.TryGetProperty("fulltas", out JsonElement fulltas)) return points;
            string[] labels = fulltas.GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray();
            string runFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(trace)), "fulltas", fulltas.GetProperty("name").GetString() + ".json");
            if (!File.Exists(runFile)) return points;
            List<InputTick> inputs;
            using (JsonDocument saved = JsonDocument.Parse(File.ReadAllText(runFile), new JsonDocumentOptions { MaxDepth = 256 }))
            {
                JsonElement root = saved.RootElement;
                inputs = Solve.ReadInputs(root);
                string[] solved = root.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToArray();
                long[] goalInput = root.GetProperty("goalInputTicks").EnumerateArray().Select(t => t.GetInt64()).ToArray();
                inputs = inputs.GetRange(0, FullTas.CurrentInputs(solved, goalInput, labels, inputs.Count));
            }
            Simulation sim = create();
            sim.Goals.Limit = labels.Length;
            Vector2 shift = Vector2.zero, pending = Vector2.zero;
            int due = -1;
            sim.Run(inputs, extraTicks: 1000000, onStep: tick =>
            {
                if (due > 0 && --due == 0) shift += pending;
                else if (due < 0)
                {
                    Vector2 at = Scene((Vector2)sim.Player.transform.position - shift);
                    if (at.magnitude <= _threshold) return;
                    pending = at;
                    due = 1;
                    WorldPos p = sim.PlayerWorld;
                    points.Add(new object[] { R(p.x), R(p.y), tick, R(shift.x - origin.x), R(shift.y - origin.y) });
                }
            });
            return points;
        }

        static float R(float v) { return (float)Math.Round(v, 1); }

        static float FloorY(Segment f, float x)
        {
            float t = f.X1 > f.X0 ? Math.Clamp((x - f.X0) / (f.X1 - f.X0), 0f, 1f) : 0f;
            return f.Y0 + (f.Y1 - f.Y0) * t;
        }

        // The floor segment just under a point the dot was at (it hovers a few units over its floor), or -1.
        static int FloorUnder(List<Segment> floors, float x, float y)
        {
            int best = -1;
            float top = float.NaN;
            for (int i = 0; i < floors.Count; i++)
            {
                Segment f = floors[i];
                if (x < f.X0 - 2f || x > f.X1 + 2f) continue;
                float fy = FloorY(f, x);
                if (fy <= y + 4f && fy >= y - 30f && (best < 0 || fy > top)) { best = i; top = fy; }
            }
            return best;
        }

        // How high a held jump lifts the dot, measured in the engine from the middle of the longest floor it stands on.
        static float HopHeight(Simulation sim, Vector2 stand, Vector2 standOffset, List<Segment> floors)
        {
            Segment f = floors.Where(s => s.Stand).OrderByDescending(s => s.X1 - s.X0).First();
            float x = (f.X0 + f.X1) / 2f, y = FloorY(f, x);
            sim.Restore(sim.Advance(new List<InputTick>()));
            Movement m = sim.Player;
            m.Velocity = Vector2.zero;
            m.momentum = Vector2.zero;
            ScenePos hop = sim.ToScene(new WorldPos(x - standOffset.x, y + stand.y / 2f - standOffset.y + 1f));
            m.transform.position = new Vector3(hop.x, hop.y, m.transform.position.z);
            SimSnapshot placed = sim.Capture(0);
            var t = new List<InputTick>();
            for (int i = 0; i < 90; i++) t.Add(new InputTick { press = i == Settle, held = i > Settle && i < Settle + 45 });
            float from = float.NaN, top = float.MinValue;
            sim.Run(t, placed, extraTicks: 0, onStep: tick =>
            {
                float py = sim.PlayerWorld.y;
                if (tick == Settle) from = py;
                top = Math.Max(top, py);
            });
            return top - from;
        }

        // The floor an engine hyper was on: the nearest segment within 64 whose surface is not above the point (a hyper
        // in coyote time is a little past its floor's end), or -1.
        static int FloorNear(List<Segment> floors, float x, float y)
        {
            int best = -1;
            float gap = 64f;
            for (int i = 0; i < floors.Count; i++)
            {
                Segment f = floors[i];
                float cx = Math.Clamp(x, f.X0, f.X1), cy = FloorY(f, cx);
                if (cy > y + 4f) continue;
                float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d < gap) { gap = d; best = i; }
            }
            return best;
        }

        // One way an ultra can go from floor `From` to floor `To`: lastHyperY written on From (a hyper there, or
        // standing on it: the game writes it on every grounded tick), then the ultra's hyper on To. `Drop`: the most
        // From's floor is above To's along a clear flight line (`Line`), from A to B.
        readonly record struct Pair(int From, int To, float Drop, float Ax, float Ay, float Bx, float By, bool Line);

        // Every ordered pair of floors with To more than 4 below From (the game's check: the hyper's y under
        // lastHyperY - 4), at most Reach across and MaxDrop down, joined by a flight line no solid crosses; a floor
        // sloping more than 4 pairs with itself (downhill). A line runs between the dot's centres over each floor: from
        // From's ends and middle, or just past either end (walking off it), to To's ends and middle, or just past either
        // end (coming in under a ledge beside it), straight or over a hop (up to `hop` above the higher end halfway
        // across, then down). A pair whose every line is blocked has a wall, ceiling or the floor itself between them.
        // Lines, not arcs: the engine's ultras add the pairs they reach that this misses.
        static List<Pair> Pairs(List<Segment> floors, Grid blockers, float half, float lift, float hop)
        {
            var found = new List<Pair>[floors.Count];
            (float x, float y)[] Spots(Segment f) => new[] { (f.X0, f.Y0), ((f.X0 + f.X1) / 2f, (f.Y0 + f.Y1) / 2f), (f.X1, f.Y1),
                (f.X0 - half - 1f, f.Y0), (f.X1 + half + 1f, f.Y1) };
            Parallel.For(0, floors.Count, i =>
            {
                var list = found[i] = new List<Pair>();
                Segment a = floors[i];
                (float x, float y)[] from = Spots(a);
                float aTop = Math.Max(a.Y0, a.Y1), aLow = Math.Min(a.Y0, a.Y1);
                for (int j = 0; j < floors.Count; j++)
                {
                    Segment b = floors[j];
                    if (aTop - Math.Min(b.Y0, b.Y1) <= 4f || aLow - Math.Max(b.Y0, b.Y1) > MaxDrop
                        || Math.Max(b.X0 - a.X1, a.X0 - b.X1) > Reach) continue;
                    (float x, float y)[] to = Spots(b);
                    float drop = 4f;
                    (float x, float y) pa = default, pb = default;
                    foreach (var p in from)
                        foreach (var q in to)
                        {
                            float d = p.y - q.y;
                            if (d <= drop || d > MaxDrop || Math.Abs(p.x - q.x) > Reach) continue;
                            float ay = p.y + lift, by = q.y + lift, mx = (p.x + q.x) / 2f, my = Math.Max(ay, by) + hop;
                            if (blockers.Crosses(p.x, ay, q.x, by) && (blockers.Crosses(p.x, ay, mx, my) || blockers.Crosses(mx, my, q.x, by))) continue;
                            drop = d; pa = p; pb = q;
                        }
                    if (drop > 4f) list.Add(new Pair(i, j, drop, pa.x, pa.y, pb.x, pb.y, true));
                }
            });
            return found.SelectMany(l => l).ToList();
        }

        // Floors a hyper can land on: upward faces (normal at most 60° from up) of Ground-layer colliders (triggers
        // too: the ground rays hit them, Physics2D.queriesHitTriggers), where the dash box (and, for `Stand`, the
        // standing box) fits on top, clear of spikes (their touch kills; a Spikes-layer hit under the feet cancels
        // the ground).
        static List<Segment> Floors(List<Map.WorldShape> shapes, Grid blockers, Grid spikes, float half, float dashHeight, float standHeight)
        {
            const float step = 4f;
            var floors = new List<Segment>();
            foreach (Map.WorldShape s in shapes)
            {
                if (s.Layer != 7 || s.Player || s.Spike || (s.Type != "Polygon" && s.Type != "Edges")) continue;
                int kind = s.Moving ? 2 : s.Tag == "Moss" ? 1 : 0;
                foreach (var (ax, ay, bx, by) in UpwardEdges(s))
                {
                    int n = Math.Max(1, (int)Math.Ceiling((bx - ax) / step));
                    int runStart = -1;
                    bool runStand = false;
                    for (int i = 0; i <= n + 1; i++)
                    {
                        bool ok = false, standOk = false;
                        if (i <= n)
                        {
                            float x = ax + (bx - ax) * i / n, y = ay + (by - ay) * i / n;
                            ok = !blockers.Hits(new Box(x - half + .5f, y + .5f, x + half - .5f, y + dashHeight))
                                && !spikes.Hits(new Box(x - half - 2f, y - 22f, x + half + 2f, y + dashHeight + 2f));
                            standOk = ok && !blockers.Hits(new Box(x - half + .5f, y + .5f, x + half - .5f, y + standHeight))
                                && !spikes.Hits(new Box(x - half - 2f, y - 22f, x + half + 2f, y + standHeight + 2f));
                        }
                        if (runStart >= 0 && (!ok || standOk != runStand))
                        {
                            int end = i - 1;
                            floors.Add(new Segment(ax + (bx - ax) * runStart / n, ay + (by - ay) * runStart / n,
                                ax + (bx - ax) * end / n, ay + (by - ay) * end / n, kind, runStand));
                            runStart = -1;
                        }
                        if (ok && runStart < 0) { runStart = i; runStand = standOk; }
                    }
                }
            }
            return floors;
        }

        static IEnumerable<(float ax, float ay, float bx, float by)> UpwardEdges(Map.WorldShape s)
        {
            float[] v = s.V;
            int count = v.Length / 2;
            bool closed = s.Type == "Polygon";
            float cx = 0f, cy = 0f;
            for (int i = 0; i < count; i++) { cx += v[2 * i]; cy += v[2 * i + 1]; }
            cx /= count; cy /= count;
            for (int i = 0; i < (closed ? count : count - 1); i++)
            {
                int j = (i + 1) % count;
                float ax = v[2 * i], ay = v[2 * i + 1], bx = v[2 * j], by = v[2 * j + 1];
                float dx = bx - ax, dy = by - ay, len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1f) continue;
                float nx = -dy / len, ny = dx / len;
                if (closed ? nx * ((ax + bx) / 2f - cx) + ny * ((ay + by) / 2f - cy) < 0f : ny < 0f) { nx = -nx; ny = -ny; }
                if (ny < .5f) continue;
                if (ax > bx) (ax, ay, bx, by) = (bx, by, ax, ay);
                yield return (ax, ay, bx, by);
            }
        }

        static Box Bounds(Map.WorldShape s)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i + 1 < s.V.Length; i += 2) { x0 = Math.Min(x0, s.V[i]); x1 = Math.Max(x1, s.V[i]); y0 = Math.Min(y0, s.V[i + 1]); y1 = Math.Max(y1, s.V[i + 1]); }
            return new Box(x0 - s.R, y0 - s.R, x1 + s.R, y1 + s.R);
        }

        static bool Overlaps(Map.WorldShape s, Box q)
        {
            float[] v = s.V;
            if (s.Type == "Polygon" && v.Length >= 6)
            {
                int count = v.Length / 2;
                for (int i = 0; i < count; i++)
                {
                    int j = (i + 1) % count;
                    float nx = -(v[2 * j + 1] - v[2 * i + 1]), ny = v[2 * j] - v[2 * i];
                    float p0 = float.MaxValue, p1 = float.MinValue;
                    for (int k = 0; k < count; k++) { float p = nx * v[2 * k] + ny * v[2 * k + 1]; p0 = Math.Min(p0, p); p1 = Math.Max(p1, p); }
                    float b0 = float.MaxValue, b1 = float.MinValue;
                    foreach (var (x, y) in new[] { (q.X0, q.Y0), (q.X1, q.Y0), (q.X0, q.Y1), (q.X1, q.Y1) })
                    { float p = nx * x + ny * y; b0 = Math.Min(b0, p); b1 = Math.Max(b1, p); }
                    if (p1 <= b0 || b1 <= p0) return false;
                }
                return true;
            }
            if (s.Type == "Edges" || s.Type == "Polygon")
            {
                int count = v.Length / 2;
                for (int i = 0; i + 1 < count; i++)
                    if (SegmentHits(v[2 * i], v[2 * i + 1], v[2 * i + 2], v[2 * i + 3], q)) return true;
                return false;
            }
            return true; // circles and capsules: their bounds already overlap
        }

        // Whether the segment a→b touches or crosses shape `s` (bounds `b`): through a polygon's edge or from inside
        // it, across an edge chain; circles and capsules by their bounds.
        static bool SegmentCrosses(Map.WorldShape s, Box b, float ax, float ay, float bx, float by)
        {
            float[] v = s.V;
            int count = v.Length / 2;
            bool polygon = s.Type == "Polygon" && count >= 3;
            if (!polygon && s.Type != "Edges") return SegmentHits(ax, ay, bx, by, b);
            for (int i = 0; i < (polygon ? count : count - 1); i++)
            {
                int j = (i + 1) % count;
                if (SegmentsMeet(ax, ay, bx, by, v[2 * i], v[2 * i + 1], v[2 * j], v[2 * j + 1])) return true;
            }
            return polygon && Inside(v, ax, ay);
        }

        static bool SegmentsMeet(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy)
        {
            static float Cross(float ox, float oy, float px, float py, float qx, float qy) => (px - ox) * (qy - oy) - (py - oy) * (qx - ox);
            float d1 = Cross(cx, cy, dx, dy, ax, ay), d2 = Cross(cx, cy, dx, dy, bx, by);
            float d3 = Cross(ax, ay, bx, by, cx, cy), d4 = Cross(ax, ay, bx, by, dx, dy);
            if ((d1 > 0f) != (d2 > 0f) && (d3 > 0f) != (d4 > 0f) && d1 != 0f && d2 != 0f && d3 != 0f && d4 != 0f) return true;
            return d1 == 0f && Within(cx, cy, dx, dy, ax, ay) || d2 == 0f && Within(cx, cy, dx, dy, bx, by)
                || d3 == 0f && Within(ax, ay, bx, by, cx, cy) || d4 == 0f && Within(ax, ay, bx, by, dx, dy);
        }

        static bool Within(float ax, float ay, float bx, float by, float px, float py)
        {
            return px >= Math.Min(ax, bx) && px <= Math.Max(ax, bx) && py >= Math.Min(ay, by) && py <= Math.Max(ay, by);
        }

        static bool Inside(float[] v, float x, float y)
        {
            bool inside = false;
            int count = v.Length / 2;
            for (int i = 0, j = count - 1; i < count; j = i++)
                if ((v[2 * i + 1] > y) != (v[2 * j + 1] > y)
                    && x < (v[2 * j] - v[2 * i]) * (y - v[2 * i + 1]) / (v[2 * j + 1] - v[2 * i + 1]) + v[2 * i]) inside = !inside;
            return inside;
        }

        static bool SegmentHits(float ax, float ay, float bx, float by, Box q)
        {
            float t0 = 0f, t1 = 1f, dx = bx - ax, dy = by - ay;
            foreach (var (p, d) in new[] { (-dx, ax - q.X0), (dx, q.X1 - ax), (-dy, ay - q.Y0), (dy, q.Y1 - ay) })
            {
                if (p == 0f) { if (d <= 0f) return false; continue; }
                float t = d / p;
                if (p < 0f) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
            }
            return t0 < t1;
        }

        // One launch: stand the dot on the floor, then hop + wavedash, then a second wavedash `Gap` input ticks later,
        // over the grid of timings. Returns each distinct ultra (a hyper that passed the game's ultra check).
        static List<object> Search(Simulation sim, SimSnapshot start, (float x, float y, float side) at, Vector2 stand, Vector2 standOffset,
            Vector2 dash, Vector2 dashOffset)
        {
            sim.Restore(start);
            Movement m = sim.Player;
            m.dashUnlocked = true;
            m.omniDashUnlocked = true;
            m.wallJumpUnlocked = true;
            m.maxAirDashes = 2;
            m.airDashesLeft = 2;
            m.Velocity = Vector2.zero;
            m.momentum = Vector2.zero;
            m.facingRight = at.side > 0f;
            ScenePos placedAt = sim.ToScene(new WorldPos(at.x - standOffset.x, at.y + stand.y / 2f - standOffset.y + 1f));
            m.transform.position = new Vector3(placedAt.x, placedAt.y, m.transform.position.z);
            SimSnapshot placed = sim.Capture(0);

            var ultras = new List<object>();
            var seen = new HashSet<(int, int)>();
            foreach (int hold in Hold)
            {
                (int delay, int jump)? first = null;
                foreach (int delay in Delay)
                {
                    foreach (int jump in Jump)
                    {
                        List<Hyper> hypers = Play(sim, placed, Inputs(at.side, delay, jump, hold, 0, 0), dash, dashOffset, 1);
                        if (hypers.Count == 0) continue;
                        first = (delay, jump);
                        Record(ultras, seen, at, hypers, hold, delay, jump, 0, 0);
                        break;
                    }
                    if (first != null) break;
                }
                if (first is not { } f) continue;
                for (int gap = 1; gap <= MaxGap; gap++)
                    foreach (int jump2 in Jump)
                        Record(ultras, seen, at, Play(sim, placed, Inputs(at.side, f.delay, f.jump, hold, gap, jump2), dash, dashOffset, 2), hold, f.delay, f.jump, gap, jump2);
            }
            return ultras;
        }

        static float _threshold = float.PositiveInfinity;
        static bool _use2D;

        // FloatingOrigin's distance: the reference position, horizontal only with Use2DDistance.
        static Vector2 Scene(Vector2 p) { return _use2D ? new Vector2(p.x, 0f) : p; }

        // LandX: where the dash touched down (canHyper set); X: where the hyper's jump left the floor; Shift: a
        // FloatingOrigin rebase happened since lastHyperY was last written, so the game compared against a stale value
        // and Drop is the real height difference (possibly none at all).
        readonly record struct Hyper(int Index, float LandX, float X, float Floor, float Drop, bool Ultra, bool Shift);

        static void Record(List<object> ultras, HashSet<(int, int)> seen, (float x, float y, float side) at, List<Hyper> hypers,
            int hold, int delay, int jump, int gap, int jump2)
        {
            for (int i = 0; i < hypers.Count; i++)
            {
                Hyper h = hypers[i];
                if (!h.Ultra || !seen.Add(((int)Math.Floor(h.X / 8f), (int)Math.Floor(h.Floor / 4f)))) continue;
                float prevX = i > 0 ? hypers[i - 1].X : at.x, prevY = i > 0 ? hypers[i - 1].Floor : at.y;
                ultras.Add(new object[] { R(at.x), R(at.y), (int)at.side, R(h.X), R(h.Floor), R(h.Drop), h.Index, hold, delay, jump, gap, jump2, R(h.LandX), R(prevX), h.Shift ? 1 : 0, R(prevY) });
            }
        }

        static List<InputTick> Inputs(float side, int delay, int jump, int hold, int gap, int jump2)
        {
            var t = new List<InputTick>();
            int dash1 = Settle + delay, dash2 = gap > 0 ? dash1 + gap : -1;
            int length = Math.Max(dash1, dash2) + 40;
            for (int i = 0; i < length; i++) t.Add(new InputTick());
            InputTick hop = t[Settle]; hop.press = true; hop.held = true; t[Settle] = hop;
            for (int i = Settle + 1; i < dash1; i++) { InputTick k = t[i]; k.held = true; t[i] = k; }
            if (dash1 > Settle + 1) { InputTick r = t[dash1 - 1]; r.held = false; r.release = true; t[dash1 - 1] = r; }
            WaveDash(t, dash1, side, jump, hold);
            if (dash2 > 0) WaveDash(t, dash2, side, jump2, hold);
            return t;
        }

        // OfflineSolver.OmniDash's down-diagonal wavedash: dash (side, -1) with the jump pressed on dash frame `jump`
        // and held `hold` input ticks, the stick held `side` after it.
        static void WaveDash(List<InputTick> t, int at, float side, int jump, int hold)
        {
            InputTick d = t[at];
            d.dash = true; d.x = side; d.y = -1f; d.dashJump = jump; d.press = false; d.held = false; d.release = false;
            t[at] = d;
            for (int i = at + 1; i < t.Count; i++) { InputTick k = t[i]; k.x = side; k.y = 0f; k.held = false; k.release = false; k.press = false; t[i] = k; }
            int end = Math.Min(t.Count - 1, at + 1 + hold);
            for (int i = at + 1; i < end; i++) { InputTick k = t[i]; k.held = true; t[i] = k; }
            InputTick r = t[end]; r.release = true; t[end] = r;
        }

        // Replays the inputs and reports every hyper (canHyper before the tick, the jump taken in it) with the game's
        // ultra check done on the state it used: Sign(preDashMomentum.x) == Sign(dashDirection.x) and the lifted
        // position below the old lastHyperY - 4.
        //
        // These sims run with Rebases off (a launch placed anywhere would rebase at once), so FloatingOrigin's rebase is
        // modelled here: when the player gets more than Threshold from the origin (FloatingOrigin.LateUpdate), the game
        // moves every root object by that position after the next physics step (MoveOrigin waits for WaitForFixedUpdate)
        // but leaves lastHyperY as it was. In the sim's unmoved frame that is lastHyperY raised by the offset's y. Only a
        // launch that starts inside the threshold does it; one outside would have rebased on the way there, somewhere unknown.
        static List<Hyper> Play(Simulation sim, SimSnapshot placed, List<InputTick> inputs, Vector2 dash, Vector2 dashOffset, int wanted)
        {
            var hypers = new List<Hyper>();
            Movement m = sim.Player;
            bool canHyper = false;
            float last = 0f, landX = float.NaN, stale = 0f;
            int after = -1, due = -1;
            Vector2 shift = Vector2.zero, pending = Vector2.zero;
            bool rebases = Scene((Vector2)m.transform.position).magnitude <= _threshold;
            sim.Run(inputs, placed, extraTicks: 60, onStep: _ =>
            {
                bool nowCan = (bool)CanHyper.GetValue(m);
                if (nowCan && !canHyper) landX = sim.PlayerWorld.x;
                float nowLast = (float)LastHyperY.GetValue(m);
                if (canHyper && !m.onGround && m.Velocity.y > 0f && nowLast != last)
                {
                    float x = ((Vector2)DashDirection.GetValue(m)).x, pre = ((Vector2)PreDashMomentum.GetValue(m)).x;
                    bool ultra = Mathf.Sign(pre) == Mathf.Sign(x) && nowLast < last - 4f;
                    float feet = nowLast - sim.WorldOriginInCapture.y - 2f + dashOffset.y - dash.y / 2f;
                    hypers.Add(new Hyper(hypers.Count + 1, landX, sim.ToWorld(ScenePos.From(m.transform.position)).x, feet, last - stale - nowLast, ultra, stale != 0f));
                    if (hypers.Count >= wanted) after = 0;
                }
                // `stale`: how far a rebase has moved the lastHyperY the game last wrote.
                if (nowLast != last) stale = 0f;
                canHyper = nowCan;
                last = nowLast;
                if (due > 0 && --due == 0)
                {
                    last += pending.y;
                    stale += pending.y;
                    LastHyperY.SetValue(m, last);
                    shift += pending;
                }
                else if (rebases && due < 0)
                {
                    Vector2 at = Scene((Vector2)m.transform.position - shift);
                    if (at.magnitude > _threshold) { pending = at; due = 1; }
                }
                if (after >= 0) after++;
            }, afterTick: _ => after < 0);
            return hypers;
        }
    }
}
