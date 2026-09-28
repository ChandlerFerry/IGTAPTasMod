using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// lab: injected lab maps (labmaps/&lt;id&gt;.json) — a temporary piece of level, built additively in a world's empty space,
    /// solved exactly offline and played as a demo. <c>lab space</c> finds the empty regions to anchor one in; <c>lab solve</c>
    /// runs an exhaustive search over the human-legal input alphabet from the lab's start to its finish box and writes the
    /// minimum-tick demo (<c>labmaps/&lt;id&gt;.demo.json</c>); <c>lab replay</c> checks a demo offline. Lab-only: a lab map is
    /// never a route and never on a route's path (root README "### Rules").
    /// </summary>
    static class Lab
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static int Run(string[] args)
        {
            switch (args.FirstOrDefault())
            {
                case "space": return Space(args.Skip(1).ToArray());
                case "solve": return Solve(args.Skip(1).ToArray());
                case "replay": return Replay(args.Skip(1).ToArray());
                default:
                    Console.Error.WriteLine("usage:\n  lab space <trace.json | trace.world.json> [w h] [--route <trace.json>]... [--list N] [--lab <labmap.json>] [--index N]\n"
                        + "                                         empty rectangles in the world frame with no collider, trigger or zone\n"
                        + "                                         geometry (the world export, off colliders included), far from the routes'\n"
                        + "                                         paths; --lab records the best one (or --index N) as the lab map's anchor\n"
                        + "  lab solve <trace.json> <labmap.json> [--out <file>] [--max-ticks N]\n"
                        + "                                         injects the lab map into the trace's world and searches, exhaustively, for\n"
                        + "                                         the minimum-tick human-legal input sequence from the lab's start to its\n"
                        + "                                         finish box; writes labmaps/<id>.demo.json\n"
                        + "  lab replay <trace.json> <labmap.json> <demo.json> [--out <file>]\n"
                        + "                                         one demo replayed from the lab start, tick by tick");
                    return 2;
            }
        }

        // ---------------------------------------------------------------- lab space -----------------------------------------------

        /// <summary>An occupied cell of the world export: every collider shape (on or off), else its bounds (composites, tilemaps).</summary>
        static int Space(string[] args)
        {
            string source = args[0];
            string worldPath = source.EndsWith(".world.json", StringComparison.OrdinalIgnoreCase) ? source
                : Path.ChangeExtension(source, null) + ".world.json";
            float width = args.Length > 1 && !args[1].StartsWith("--") ? F(args[1]) : 400f;
            float height = args.Length > 2 && !args[2].StartsWith("--") ? F(args[2]) : 300f;
            int list = Opt(args, "--list", 12);
            string labPath = OptS(args, "--lab", null);
            int index = Opt(args, "--index", 0);
            var routePaths = new List<string>();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--route") routePaths.Add(args[i + 1]);
            if (!source.EndsWith(".world.json", StringComparison.OrdinalIgnoreCase) && !routePaths.Contains(source)) routePaths.Add(source);

            using JsonDocument world = JsonDocument.Parse(File.ReadAllText(worldPath), new JsonDocumentOptions { MaxDepth = 256 });
            JsonElement root = world.RootElement;
            Vector2 origin = WorldOrigin(root);
            var boxes = new List<(float x0, float y0, float x1, float y1)>();
            foreach (JsonElement o in root.GetProperty("objects").EnumerateArray())
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    string type = c.GetProperty("type").GetString();
                    if (!type.EndsWith("Collider2D", StringComparison.Ordinal)) continue;
                    if (c.TryGetProperty("shapes", out JsonElement shapes) && shapes.ValueKind == JsonValueKind.Object
                        && shapes.GetProperty("list").GetArrayLength() > 0)
                    {
                        foreach (JsonElement s in shapes.GetProperty("list").EnumerateArray())
                            boxes.Add(ShapeBox(s));
                    }
                    else if (c.TryGetProperty("props", out JsonElement props) && props.TryGetProperty("bounds", out JsonElement bounds)
                        && bounds.ValueKind == JsonValueKind.Object && bounds.TryGetProperty("m_Center", out JsonElement center))
                    {
                        JsonElement extents = bounds.GetProperty("m_Extents");
                        float cx = center.GetProperty("x").GetSingle(), cy = center.GetProperty("y").GetSingle();
                        float ex = extents.GetProperty("x").GetSingle(), ey = extents.GetProperty("y").GetSingle();
                        if (ex > 0f || ey > 0f) boxes.Add((cx - ex, cy - ey, cx + ex, cy + ey));
                    }
                }
            var path = new List<Vector2>();
            foreach (string route in routePaths.Where(File.Exists))
                using (JsonDocument trace = JsonDocument.Parse(File.ReadAllText(route), new JsonDocumentOptions { MaxDepth = 256 }))
                    foreach (JsonElement t in trace.RootElement.GetProperty("ticks").EnumerateArray())
                    {
                        JsonElement p = t.GetProperty("position");
                        path.Add(new Vector2(p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle()));
                    }

            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            foreach (var b in boxes) { x0 = MathF.Min(x0, b.x0); y0 = MathF.Min(y0, b.y0); x1 = MathF.Max(x1, b.x1); y1 = MathF.Max(y1, b.y1); }
            foreach (Vector2 p in path) { x0 = MathF.Min(x0, p.x); y0 = MathF.Min(y0, p.y); x1 = MathF.Max(x1, p.x); y1 = MathF.Max(y1, p.y); }
            if (float.IsInfinity(x0)) { Console.Error.WriteLine("Nothing in " + worldPath); return 1; }
            x0 -= 200f; y0 -= 200f; x1 += 200f; y1 += 200f;

            const float Cell = 8f;
            int cols = Math.Max(1, (int)MathF.Ceiling((x1 - x0) / Cell)), rows = Math.Max(1, (int)MathF.Ceiling((y1 - y0) / Cell));
            var free = new bool[cols * rows];
            Array.Fill(free, true);
            foreach (var (a, b, c, d) in boxes)
                Mark(free, cols, rows, x0, y0, Cell, a, b, c, d);
            Console.Error.WriteLine($"{boxes.Count} collider boxes from {Path.GetFileName(worldPath)}, {cols}x{rows} cells of {Cell} u, {path.Count} route samples");

            var candidates = MaximalRectangles(free, cols, rows, x0, y0, Cell, width, height);
            foreach (var r in candidates)
                r.distance = path.Count == 0 ? float.PositiveInfinity : path.Min(p => Distance(p, r));
            var ranked = candidates.OrderByDescending(r => r.distance).ThenByDescending(r => r.w * r.h).Take(list).ToList();
            Console.WriteLine($"Empty rectangles at least {width:0}x{height:0} u, world frame, by distance from the routes:");
            for (int i = 0; i < ranked.Count; i++)
            {
                var r = ranked[i];
                Console.WriteLine($"  #{i,2} x {r.x:0.#}..{r.x + r.w:0.#}, y {r.y:0.#}..{r.y + r.h:0.#} ({r.w:0}x{r.h:0})"
                    + (float.IsInfinity(r.distance) ? "" : $", {r.distance:0} u off the route"));
            }
            if (ranked.Count == 0) { Console.Error.WriteLine("No rectangle fits; try a smaller w h."); return 1; }
            if (labPath == null) return 0;

            var pick = ranked[Math.Min(index, ranked.Count - 1)];
            JsonObject lab = File.Exists(labPath) ? (JsonObject)JsonNode.Parse(File.ReadAllText(labPath)) : new JsonObject { ["schema"] = 1 };
            lab["id"] ??= Path.GetFileNameWithoutExtension(labPath);
            lab["name"] ??= lab["id"]!.GetValue<string>();
            lab["shapes"] ??= new JsonArray();
            lab["start"] ??= new JsonObject();
            lab["finish"] ??= new JsonArray(0f, 0f, 100f, 100f);
            lab["anchor"] = new JsonArray(Trim(pick.x), Trim(pick.y));
            lab["region"] = new JsonArray(Trim(pick.x), Trim(pick.y), Trim(pick.w), Trim(pick.h));
            File.WriteAllText(labPath, lab.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Recorded anchor {pick.x:0.#},{pick.y:0.#} (region {pick.w:0}x{pick.h:0}) in {labPath}");
            return 0;
        }

        static void Mark(bool[] free, int cols, int rows, float x0, float y0, float cell, float a, float b, float c, float d)
        {
            int i0 = Math.Max(0, (int)MathF.Floor((a - x0) / cell)), i1 = Math.Min(cols - 1, (int)MathF.Ceiling((c - x0) / cell));
            int j0 = Math.Max(0, (int)MathF.Floor((b - y0) / cell)), j1 = Math.Min(rows - 1, (int)MathF.Ceiling((d - y0) / cell));
            for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) free[j * cols + i] = false;
        }

        /// <summary>The maximal free rectangles at least w x h (one per histogram bar, contained ones dropped), as the
        /// largest-rectangle stack over each row.</summary>
        static List<RectCand> MaximalRectangles(bool[] free, int cols, int rows, float x0, float y0, float cell, float width, float height)
        {
            var result = new List<RectCand>();
            var heights = new int[cols];
            for (int j = 0; j < rows; j++)
            {
                for (int i = 0; i < cols; i++) heights[i] = free[j * cols + i] ? heights[i] + 1 : 0;
                var stack = new Stack<int>();
                for (int i = 0; i <= cols; i++)
                {
                    int h = i < cols ? heights[i] : 0;
                    while (stack.Count > 0 && heights[stack.Peek()] > h)
                    {
                        int bar = stack.Pop();
                        int left = stack.Count > 0 ? stack.Peek() + 1 : 0;
                        float w = (i - left) * cell, hh = heights[bar] * cell;
                        if (w >= width && hh >= height)
                        {
                            var rect = new RectCand { x = x0 + left * cell, y = y0 + (j + 1) * cell - hh, w = w, h = hh };
                            bool contained = false;
                            foreach (RectCand other in result)
                                if (rect.x >= other.x && rect.y >= other.y && rect.x + rect.w <= other.x + other.w && rect.y + rect.h <= other.y + other.h)
                                { contained = true; break; }
                            if (!contained)
                            {
                                result.RemoveAll(other => other.x >= rect.x && other.y >= rect.y
                                    && other.x + other.w <= rect.x + rect.w && other.y + other.h <= rect.y + rect.h);
                                result.Add(rect);
                            }
                        }
                    }
                    stack.Push(i);
                }
            }
            return result;
        }

        sealed class RectCand
        {
            public float x, y, w, h, distance;
        }

        static float Distance(Vector2 p, RectCand r)
        {
            float dx = MathF.Max(MathF.Max(r.x - p.x, p.x - (r.x + r.w)), 0f);
            float dy = MathF.Max(MathF.Max(r.y - p.y, p.y - (r.y + r.h)), 0f);
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        static (float x0, float y0, float x1, float y1) ShapeBox(JsonElement s)
        {
            JsonElement v = s.GetProperty("vertices");
            float[] a = v.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            for (int i = 0; i < a.Length; i += 2)
            {
                x0 = MathF.Min(x0, a[i]); x1 = MathF.Max(x1, a[i]);
                y0 = MathF.Min(y0, a[i + 1]); y1 = MathF.Max(y1, a[i + 1]);
            }
            float radius = s.GetProperty("radius").GetSingle();
            return (x0 - radius, y0 - radius, x1 + radius, y1 + radius);
        }

        /// <summary>The FloatingOrigin.currentOrigin the export was taken at: the frame its positions are in (world = scene - origin).</summary>
        static Vector2 WorldOrigin(JsonElement world)
        {
            foreach (JsonElement o in world.GetProperty("objects").EnumerateArray())
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray())
                {
                    if (c.GetProperty("type").GetString() != "FloatingOrigin") continue;
                    if (!c.TryGetProperty("fields", out JsonElement fields)) continue;
                    foreach (JsonElement entry in fields.EnumerateArray())
                        if (entry[1].GetString() == "currentOrigin")
                            return new Vector2(entry[2].GetProperty("x").GetSingle(), entry[2].GetProperty("y").GetSingle());
                }
            return Vector2.zero;
        }

        // ---------------------------------------------------------------- lab solve -----------------------------------------------

        /// <summary>The player's state at one node of the search: the input path from the lab start that reaches it (replayed
        /// in one run per extension, so the frontier needs no snapshots and every core can explore it).</summary>
        sealed class Node
        {
            public List<InputTick> Path;
            public int Ticks;
            public bool JumpHeld, CanDash;
            public string Key;
        }

        static int Solve(string[] args)
        {
            string tracePath = args[0], labPath = args[1];
            LabMapDef lab = LabMapDef.Load(labPath);
            int maxTicks = Opt(args, "--max-ticks", lab.maxTicks);
            string outPath = OptS(args, "--out", DefaultDemoPath(labPath));
            int threads = Opt(args, "--threads", Environment.ProcessorCount);

            var (sim, trace, _) = Open(tracePath, lab);
            Place(sim, lab);
            var (fx, fy, fw, fh) = lab.Finish;
            Vector2 anchor = new Vector2(lab.Anchor.x, lab.Anchor.y);
            sim.TargetCourse = -1;
            sim.TargetFinish = (fx + anchor.x + sim.Origin.x, fy + anchor.y + sim.Origin.y, fw, fh);
            if (TouchesAtStart(sim)) throw new InvalidOperationException("The lab's start is inside its finish box.");

            int alphabet = Alphabet(false, true).Count;
            Console.WriteLine($"lab {lab.id} at {anchor.x:0.#},{anchor.y:0.#} (world frame) in {Path.GetFileName(tracePath)}: "
                + $"{lab.shapes.Count} shapes, category {lab.category}; finish box {fw:0}x{fh:0} at {fx:0.#},{fy:0.#} from the anchor; "
                + $"search to {maxTicks} ticks, alphabet {alphabet} inputs/tick");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool startCanDash = GetMovementBool(sim, "dashUnlocked");
            var start = new Node { Path = new List<InputTick>(), Ticks = 0, JumpHeld = false, CanDash = startCanDash, Key = KeyOf(sim) };
            var visited = new Dictionary<string, int> { [start.Key] = 0 };
            var buckets = new List<Node>[maxTicks + 64];
            for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<Node>();
            buckets[0].Add(start);
            long states = 1, extensions = 0, dead = 0, dupes = 0;
            Node best = null;
            InputTick bestInput = default;
            int bound = maxTicks + 1;
            bool announced = false;
            // One world and simulation per worker (the game's statics are per thread), each replaying node paths from the lab
            // start. Ticks in order: the first finish is the minimum, and once one is found every state that could still reach
            // the finish box sooner (a state at tick T finishes at T + 1 at the earliest) is explored to prove it cannot.
            var workers = new System.Threading.ThreadLocal<Simulation>(() =>
            {
                var (worker, _, _) = Open(tracePath, lab);
                Place(worker, lab);
                return worker;
            });
            for (int ticks = 0; ticks < buckets.Length && ticks < bound; ticks++)
            {
                List<Node> bucket = buckets[ticks];
                var keep = new System.Collections.Concurrent.ConcurrentBag<Node>();
                var found = new System.Collections.Concurrent.ConcurrentBag<(Node node, InputTick input, int at)>();
                long explored = 0, localDead = 0;
                Parallel.ForEach(bucket, new ParallelOptions { MaxDegreeOfParallelism = threads }, node =>
                {
                    if (node.Ticks != ticks) return;
                    if (Volatile.Read(ref bound) <= ticks + 1) return;
                    Simulation sim2 = workers.Value;
                    foreach (InputTick input in Alphabet(node.JumpHeld, node.CanDash))
                    {
                        if (Volatile.Read(ref bound) <= ticks + 1) break;
                        Interlocked.Increment(ref explored);
                        Node child = Step(sim2, node, input, out RunResult run);
                        if (child == null)
                        {
                            if (run != null && run.Finished) found.Add((node, input, run.Stepped));
                            else Interlocked.Increment(ref localDead);
                            continue;
                        }
                        keep.Add(child);
                    }
                });
                foreach (var (node, input, at) in found)
                    if (at < bound) { best = node; bestInput = input; bound = at; }
                extensions += Interlocked.Read(ref explored);
                dead += Interlocked.Read(ref localDead);
                if (best != null && !announced)
                {
                    announced = true;
                    Console.WriteLine($"  finish at tick {bound} (input tick {best.Path.Count + 1}) after {extensions:N0} extensions; exploring every state below the bound to prove it is the minimum");
                }
                foreach (Node child in keep)
                {
                    if (child.Ticks >= bound) continue;
                    lock (visited)
                    {
                        if (visited.TryGetValue(child.Key, out int seen) && seen <= child.Ticks) { dupes++; continue; }
                        visited[child.Key] = child.Ticks;
                        states++;
                        buckets[child.Ticks].Add(child);
                    }
                }
                bucket.Clear();
                if (extensions > 0 && extensions / 5000 != (extensions - Interlocked.Read(ref explored)) / 5000)
                    Console.WriteLine($"  [{clock.Elapsed.TotalSeconds:0.#}s] {extensions:N0} extensions, {states:N0} states, {visited.Count:N0} keys, {dead:N0} dead ends, tick {ticks}, best {(best == null ? "-" : bound.ToString())}");
            }
            workers.Dispose();
            if (best == null)
            {
                Console.WriteLine($"No demo within {maxTicks} ticks: {extensions:N0} extensions, {states:N0} states, {visited.Count:N0} keys, {dead:N0} dead ends, {dupes:N0} duplicates, {clock.Elapsed.TotalSeconds:0.0}s");
                return 1;
            }

            List<InputTick> inputs = new List<InputTick>(best.Path);
            inputs.Add(bestInput);
            // The last inputs may sit past the finish (a dash's lock ticks finish mid-lock): drop any the replay doesn't need.
            while (inputs.Count > 1 && RunToFinish(sim, inputs.Take(inputs.Count - 1).ToList()) == bound) inputs.RemoveAt(inputs.Count - 1);
            int finishTick = RunToFinish(sim, inputs);
            if (finishTick != bound) throw new InvalidOperationException("The demo replays to tick " + finishTick + ", not the searched " + bound + ".");
            Console.WriteLine($"minimum-tick demo: {inputs.Count} input ticks, finish at physics tick {finishTick}; "
                + $"{extensions:N0} extensions, {states:N0} states kept, {visited.Count:N0} keys, {dead:N0} dead ends, {dupes:N0} duplicates, "
                + $"bound {finishTick} (every state below it exhausted), {clock.Elapsed.TotalSeconds:0.0}s");

            var search = new
            {
                states, explored = extensions, keys = visited.Count, deadEnds = dead, duplicates = dupes,
                bound = finishTick, complete = true, maxTicks, alphabet,
                quantization = "0.25 u position, 2 u/s velocity and momentum, 0.01 counters, 1 u large floats",
                seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
            };
            var file = new
            {
                schema = 1,
                lab = lab.id,
                name = "lab/" + lab.id,
                solvedIn = Path.GetFileName(tracePath),
                world = Path.GetFileName(Path.ChangeExtension(tracePath, null) + ".world.json"),
                ticks = finishTick,
                inputTicks = inputs.Count,
                search,
                inputs = inputs.Select(t => t.ToJson()).ToList(),
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("wrote " + outPath);
            return 0;
        }

        static string DefaultDemoPath(string labPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(labPath));
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(labPath) + ".demo.json");
        }

        static List<InputTick> PathOf(Node node) { return node.Path; }

        /// <summary>One input tick from a node: replay its path plus the new input from the lab start in one run (the trailing
        /// duplicate keeps the engine from ending at the input tick's end), and stop as the new input is consumed.</summary>
        static Node Step(Simulation sim, Node from, InputTick input, out RunResult run)
        {
            var path = new List<InputTick>(from.Path.Count + 2);
            path.AddRange(from.Path);
            path.Add(input);
            path.Add(input);
            string key = null;
            bool canDash = from.CanDash;
            run = sim.Run(path, null, afterTick: tick =>
            {
                // tick 0 is the first scheduler tick of a run that starts inside a lock (a dash's or spring's remaining ticks:
                // no input is consumed there). The new input tick is from.Path.Count + 1.
                if (tick <= from.Path.Count) return true;
                key = KeyOf(sim);
                canDash = GetMovementBool(sim, "dashUnlocked");
                return false;
            }, extraTicks: 400);
            if (key == null) return null;
            var childPath = new List<InputTick>(from.Path.Count + 1);
            childPath.AddRange(from.Path);
            childPath.Add(input);
            return new Node
            {
                Path = childPath,
                Ticks = run.Stepped, JumpHeld = input.press || input.held && !input.release || from.JumpHeld && !input.release && !input.press,
                CanDash = canDash, Key = key,
            };
        }

        /// <summary>Replays inputs from the lab start: the physics tick the finish box is touched on, or -1.</summary>
        static int RunToFinish(Simulation sim, List<InputTick> inputs)
        {
            if (inputs.Count == 0) return -1;
            var list = new List<InputTick>(inputs) { inputs[inputs.Count - 1] };
            int steps = 0;
            RunResult run = sim.Run(list, null, extraTicks: 400, onStep: s => steps++);
            return run.Finished ? steps : -1;
        }

        static bool TouchesAtStart(Simulation sim)
        {
            (float x, float y, float w, float h) = sim.TargetFinish;
            float x0 = x - w / 2f, x1 = x + w / 2f, y0 = y - h / 2f, y1 = y + h / 2f;
            Vector3 at = sim.Player.transform.position;
            foreach (BoxCollider2D box in sim.Player.GetComponents<BoxCollider2D>())
            {
                if (!box.enabled) continue;
                Vector3 scale = box.transform.lossyScale;
                float cx = at.x + box.offset.x * scale.x, cy = at.y + box.offset.y * scale.y;
                float hx = box.size.x * MathF.Abs(scale.x) / 2f, hy = box.size.y * MathF.Abs(scale.y) / 2f;
                if (cx + hx > x0 && cx - hx < x1 && cy + hy > y0 && cy - hy < y1) return true;
            }
            return false;
        }

        /// <summary>The human-legal input alphabet of one tick: stick x in {-1, -partial, 0, partial, 1}, y in {-1, 0, 1}, the jump
        /// button's press/hold/release/up by its state, and a dash pressed with any of its dash-jump frames (a dash press with
        /// no dash to spend is pruned: dashUnlocked is permanent in a lab run and the press can do nothing).</summary>
        static List<InputTick> Alphabet(bool jumpHeld, bool canDash)
        {
            float[] xs = { -1f, -0.55f, 0f, 0.55f, 1f };
            float[] ys = { -1f, 0f, 1f };
            var ticks = new List<InputTick>(420);
            foreach (float x in xs)
                foreach (float y in ys)
                {
                    var jump = jumpHeld
                        ? new[] { new InputTick { held = true }, new InputTick { release = true } }
                        : new[] { new InputTick { press = true, held = true }, new InputTick() };
                    for (int k = 0; k < jump.Length; k++)
                    {
                        InputTick j = jump[k];
                        j.x = x; j.y = y;
                        ticks.Add(j);
                        if (!canDash) continue;
                        for (int frame = 0; frame <= 12; frame++)
                        {
                            var d = j;
                            d.dash = true;
                            d.dashJump = frame;
                            ticks.Add(d);
                        }
                    }
                }
            return ticks;
        }

        // ---------------------------------------------------------------- lab replay ----------------------------------------------

        static int Replay(string[] args)
        {
            string tracePath = args[0], labPath = args[1], demoPath = args[2];
            LabMapDef lab = LabMapDef.Load(labPath);
            var (sim, _, _) = Open(tracePath, lab);
            Place(sim, lab);
            var (fx, fy, fw, fh) = lab.Finish;
            Vector2 anchor = new Vector2(lab.Anchor.x, lab.Anchor.y);
            sim.TargetCourse = -1;
            sim.TargetFinish = (fx + anchor.x + sim.Origin.x, fy + anchor.y + sim.Origin.y, fw, fh);
            using JsonDocument demo = JsonDocument.Parse(File.ReadAllText(demoPath));
            List<InputTick> inputs = demo.RootElement.GetProperty("inputs").EnumerateArray().Select(InputTick.FromJson).ToList();
            string outPath = OptS(args, "--out", null);
            var log = new StringBuilder();
            int steps = 0;
            RunResult run = sim.Run(new List<InputTick>(inputs) { inputs[inputs.Count - 1] }, null, extraTicks: 400, onStep: s =>
            {
                Vector3 p = sim.PlayerWorld;
                log.AppendLine($"{steps,3} in{(int)sim.Engine.RunTicks - 1,3} pos {p.x - anchor.x,9:0.#},{p.y - anchor.y,9:0.#}"
                    + $" V {sim.Player.Velocity.x,7:0.#},{sim.Player.Velocity.y,7:0.#} m {sim.Player.momentum.x,7:0.#},{sim.Player.momentum.y,7:0.#}"
                    + $" g{(sim.Player.onGround ? 1 : 0)} w{(sim.Player.OnWall ? 1 : 0)} {sim.Player.cutsceneMode}"
                    + $" dash{(sim.Player.dashActive ? 1 : 0)} f{sim.Player.framesSinceLastDash:0} R{(sim.Player.facingRight ? 1 : 0)}");
                steps++;
            });
            if (outPath != null) File.WriteAllText(outPath, log.ToString()); else Console.Write(log);
            Console.WriteLine($"demo: {inputs.Count} inputs, {steps} physics ticks; "
                + (run.Finished ? "finish at tick " + steps : "no finish (" + run.EndReason + ")"));
            return run.Finished ? 0 : 1;
        }

        // ---------------------------------------------------------------- shared --------------------------------------------------

        /// <summary>The trace's world with the lab map injected additively, the trace's start loaded.</summary>
        static (Simulation sim, JsonDocument trace, List<InputTick> inputs) Open(string tracePath, LabMapDef lab)
        {
            string worldPath = Path.ChangeExtension(tracePath, null) + ".world.json";
            World world = World.Load(worldPath);
            LabMap.Inject(world, lab);
            var sim = new Simulation(world);
            JsonDocument trace;
            using (FileStream file = File.OpenRead(tracePath)) trace = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 256 });
            sim.SetTarget(trace.RootElement);
            sim.LoadStart(trace.RootElement.GetProperty("start"));
            var inputs = trace.RootElement.GetProperty("inputs").EnumerateArray().Select(InputTick.FromJson).ToList();
            return (sim, trace, inputs);
        }

        /// <summary>The player at the lab's start (the probe/clip start): position and velocity in the world frame, facing, Movement fields.</summary>
        static void Place(Simulation sim, LabMapDef lab)
        {
            LabStart s = lab.start ?? new LabStart();
            (float ax, float ay) = lab.Anchor;
            var fields = new Dictionary<string, object>();
            if (s.fields != null)
                foreach (var (name, value) in s.fields) fields[name] = value;
            float vx = s.vx ?? 0f, vy = s.vy ?? 0f, mx = s.mx ?? 0f, my = s.my ?? 0f;
            if (s.vx.HasValue || s.vy.HasValue || s.mx.HasValue || s.my.HasValue)
            {
                fields["Velocity"] = Fmt(vx) + "," + Fmt(vy);
                fields["momentum"] = Fmt(mx) + "," + Fmt(my);
            }
            if (s.facing.HasValue) fields["facingRight"] = s.facing.Value > 0f ? "true" : "false";
            var start = new Dictionary<string, object>
            {
                ["position"] = new[] { ax + (s.x ?? 0f), ay + (s.y ?? 0f) },
                ["velocity"] = new[] { vx + mx, vy + my },
                ["scaleX"] = (s.facing ?? 1f) > 0f ? 1f : -1f,
                ["fields"] = fields,
            };
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(start));
            DemoStart.ApplyStart(sim, doc.RootElement, ".");
            // A lab start is often mid-air: the demo's inputs start on the first tick, as an in-game lab demo's do (no wait for
            // three grounded ticks, no start alignment, no wait for a course finish after the inputs).
            SetEngineField(sim, "_waitingForStable", false);
            SetEngineField(sim, "_preRollTicksRemaining", 0);
            SetEngineField(sim, "_requireCourseFinish", false);
            sim.SetStart(sim.Capture(0), 0);
        }

        static void SetEngineField(Simulation sim, string name, object value)
        {
            typeof(MacroEngine).GetField(name, Fields).SetValue(sim.Engine, value);
        }

        /// <summary>The dedupe key of a state: the world pose, velocity and body state quantized, every Movement field, and the
        /// macro engine's pending input state (a held jump, a dash-jump frame, a lock, the pending Invokes' timers).</summary>
        static string KeyOf(Simulation sim)
        {
            var sb = new StringBuilder(512);
            Vector3 p = sim.PlayerWorld;
            Quantize(sb, p.x, 0.25f); Quantize(sb, p.y, 0.25f);
            Quantize(sb, sim.Player.transform.localScale.x, 0.01f);
            var body = sim.Physics.PlayerBody();
            Quantize(sb, body.px, 0.25f); Quantize(sb, body.py, 0.25f);
            Quantize(sb, body.vx, 2f); Quantize(sb, body.vy, 2f); Quantize(sb, body.angle, 0.01f);
            foreach (FieldInfo f in MovementFields)
            {
                if (Simulation.MovementPresentationFields.Contains(f.Name)) continue;
                AddValue(sb, f.GetValue(sim.Player));
            }
            foreach (FieldInfo f in EngineFields) AddValue(sb, f.GetValue(sim.Engine));
            foreach (Scheduler.Pending p2 in sim.Scheduler.Queue)
            {
                sb.Append(p2.Method).Append(';').Append(p2.Due - sim.Scheduler.Tick - 1).Append(';')
                    .Append(p2.Target == null ? 0 : p2.Target.GetInstanceID()).Append('|');
            }
            return sb.ToString();
        }

        static bool GetMovementBool(Simulation sim, string name)
        {
            FieldInfo f = typeof(Movement).GetField(name, Fields);
            return f != null && f.GetValue(sim.Player) is bool b && b;
        }

        static readonly FieldInfo[] MovementFields = FieldState.For(typeof(Movement));
        static readonly FieldInfo[] EngineFields = new[]
        {
            "_rawJumpHeld", "_dashJumpFrame", "_springDashFrame", "_springTicks", "_lockArmed", "_lockX", "_lockY",
            "_pendingTurnX", "_turnArmed", "_turnX", "_tickLocked", "_tickStick", "_waitingForStable", "_preRollTicksRemaining",
        }.Select(n => typeof(MacroEngine).GetField(n, Fields)).Where(f => f != null).ToArray();

        static void AddValue(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null: sb.Append('~'); break;
                case bool b: sb.Append(b ? '1' : '0'); break;
                case char ch: sb.Append(ch).Append(';'); break;
                case string s: sb.Append(s).Append(';'); break;
                case float f: Quantize(sb, f, 1f); break;
                case double d: Quantize(sb, (float)d, 1f); break;
                case Vector2 v: Quantize(sb, v.x, 1f); Quantize(sb, v.y, 1f); break;
                case Vector3 v: Quantize(sb, v.x, 1f); Quantize(sb, v.y, 1f); Quantize(sb, v.z, 1f); break;
                case UnityEngine.Object o: sb.Append(o == null ? 0 : o.GetInstanceID()).Append(';'); break;
                case System.Collections.IEnumerable items:
                    foreach (object item in items) AddValue(sb, item);
                    sb.Append('~');
                    break;
                default: sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';'); break;
            }
        }

        /// <summary>Counters (under 100) keep 0.01, larger values (positions, velocities) round to the step given: states within the
        /// step are one search state.</summary>
        static void Quantize(StringBuilder sb, float value, float step)
        {
            float q = MathF.Abs(value) < 100f ? MathF.Round(value * 100f) : MathF.Round(value / step);
            sb.Append(q.ToString("0", CultureInfo.InvariantCulture)).Append(',');
        }

        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
        static string Fmt(float f) { return f.ToString("R", CultureInfo.InvariantCulture); }
        static JsonNode Trim(float f) { return (JsonValue)(double)Math.Round(f, 3); }
        static int Opt(string[] args, string name, int fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback; }
        static string OptS(string[] args, string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    }
}
