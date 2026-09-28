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
            /// <summary>The highest world y the player can still reach with any inputs (a dead node is below the finish box).</summary>
            public float MaxY;
            /// <summary>The player's world position (for the time-aware reachability prune).</summary>
            public float X, Y;
            /// <summary>On real ground (onGround with the coyote window reset): the negative control's predicate.</summary>
            public bool SolidGround;
        }

        static int Solve(string[] args)
        {
            string tracePath = args[0], labPath = args[1];
            LabMapDef lab = LabMapDef.Load(labPath);
            int maxTicks = Opt(args, "--max-ticks", lab.maxTicks);
            string outPath = OptS(args, "--out", DefaultDemoPath(labPath));
            int threads = Opt(args, "--threads", Environment.ProcessorCount);
            // --exact: dedupe on the bit-exact state (no quantization). The default key is coarse (1 u, 10 u/s): it finds a
            // demo fast for an upper bound B; the exact pass then proves nothing shorter exists (or finds the true minimum).
            bool exact = args.Contains("--exact");
            // The negative control: no jump press (nor a dash's dash-jump frame) unless the state is on solid ground. Every
            // jump a lab's start can press is the tech's own (a coyote window, or a buffered jump for a dash boost), so a run
            // that finds nothing proves the finish needs the tech.
            bool control = args.Contains("--forbid-jump-not-grounded");

            var (sim, trace, _) = Open(tracePath, lab);
            Place(sim, lab);
            Configure(sim, lab);
            var (fx, fy, fw, fh) = lab.Finish;
            Vector2 anchor = new Vector2(lab.Anchor.x, lab.Anchor.y);
            if (TouchesAtStart(sim)) throw new InvalidOperationException("The lab's start is inside its finish box.");

            int alphabet = Alphabet(false, true, false).Count;
            Console.WriteLine($"lab {lab.id} at {anchor.x:0.#},{anchor.y:0.#} (world frame) in {Path.GetFileName(tracePath)}: "
                + $"{lab.shapes.Count} shapes, category {lab.category}; finish box {fw:0}x{fh:0} at {fx:0.#},{fy:0.#} from the anchor; "
                + $"search to {maxTicks} ticks, alphabet {alphabet} inputs/tick, {(exact ? "bit-exact" : "quantized")} key{(control ? ", control run (no jump press off solid ground)" : "")}");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool startCanDash = GetMovementBool(sim, "dashUnlocked");
            WorldPos startAt = sim.PlayerWorld;
            var start = new Node { Path = new List<InputTick>(), Ticks = 0, JumpHeld = false, CanDash = startCanDash,
                Key = KeyOf(sim, exact), MaxY = MaxReach(sim, control), X = startAt.x, Y = startAt.y,
                SolidGround = GroundCheckGrounds(sim, startAt.x, startAt.y) };
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
            float boxBottom = fy + anchor.y - fh / 2f;
            float bx0 = fx + anchor.x - fw / 2f, bx1 = fx + anchor.x + fw / 2f;
            float by0 = boxBottom, by1 = fy + anchor.y + fh / 2f;
            long prunedHeight = 0, prunedTime = 0;
            var workers = new System.Threading.ThreadLocal<Simulation>(() =>
            {
                var (worker, _, _) = Open(tracePath, lab);
                Place(worker, lab);
                Configure(worker, lab);
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
                    // Reachability (admissible: only provably dead states are cut). Height: no inputs can lift the player into
                    // the finish box (MaxReach over-estimates every rise the game can give). Time: the game's own
                    // maxTranslationSpeed (100 u/physics step) is the most any input can move the player either way, so a state
                    // more than that per remaining tick from the box cannot finish before the bound.
                    if (node.MaxY < boxBottom) { Interlocked.Increment(ref prunedHeight); return; }
                    float dx = MathF.Max(MathF.Max(bx0 - node.X, node.X - bx1), 0f), dy = MathF.Max(MathF.Max(by0 - node.Y, node.Y - by1), 0f);
                    if (MathF.Max(dx, dy) > (maxTicks - node.Ticks) * 100f) { Interlocked.Increment(ref prunedTime); return; }
                    Simulation sim2 = workers.Value;
                    foreach (InputTick input in Alphabet(node.JumpHeld, node.CanDash, control && !node.SolidGround))
                    {
                        if (Volatile.Read(ref bound) <= ticks + 1) break;
                        Interlocked.Increment(ref explored);
                        Node child = Step(sim2, node, input, exact, control, out RunResult run);
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
            if (best == null)
            {
                Console.WriteLine($"No demo within {maxTicks} ticks: {extensions:N0} extensions, {states:N0} states, {visited.Count:N0} keys, {dead:N0} dead ends, {dupes:N0} duplicates, prunes {prunedHeight:N0} height / {prunedTime:N0} time, {clock.Elapsed.TotalSeconds:0.0}s");
                workers.Dispose();
                return 1;
            }

            List<InputTick> inputs = new List<InputTick>(best.Path);
            inputs.Add(bestInput);
            Console.WriteLine("  inputs: " + string.Join(" | ", inputs.Select(TickText)));
            // The search's workers set the shared input hooks on their own thread; the one created on this thread took
            // this thread's hook, so the verification runs on that worker's simulation (its own world and lab).
            Simulation verify = workers.Value;
            int finishTick = RunToFinish(verify, inputs);
            if (finishTick != bound)
            {
                foreach (TickRow row in Observe(verify, inputs))
                    Console.WriteLine($"  row {row.Tick}: {row.X:0.#},{row.Y:0.#} V {row.Vx:0.#},{row.Vy:0.#} g{(row.Ground ? 1 : 0)} solid{(row.Solid ? 1 : 0)}");
                throw new InvalidOperationException("The demo replays to tick " + finishTick + ", not the searched " + bound + ".");
            }
            // The last inputs may sit past the finish (a dash's lock ticks finish mid-lock): drop any the replay doesn't need.
            while (inputs.Count > 1 && RunToFinish(verify, inputs.Take(inputs.Count - 1).ToList()) == bound) inputs.RemoveAt(inputs.Count - 1);
            List<TickRow> rows = Observe(verify, inputs);
            string signature = TechSignature(rows);
            Console.WriteLine("tech: " + (signature ?? "NONE FOUND (the demo performs no known tech)"));
            Console.WriteLine($"minimum-tick demo: {inputs.Count} input ticks, finish at physics tick {finishTick}; "
                + $"{extensions:N0} extensions, {states:N0} states kept, {visited.Count:N0} keys, {dead:N0} dead ends, {dupes:N0} duplicates, "
                + $"prunes {prunedHeight:N0} height / {prunedTime:N0} time, bound {finishTick} (every state below it exhausted), {clock.Elapsed.TotalSeconds:0.0}s");

            var search = new
            {
                states, explored = extensions, keys = visited.Count, deadEnds = dead, duplicates = dupes,
                bound = finishTick, complete = true, maxTicks, alphabet, exact, control, prunedHeight, prunedTime, tech = signature,
                quantization = exact ? "none: bit-exact floats (position, velocity, momentum, body, every Movement field)" : "1 u position, 10 u/s velocity and momentum, 1 u counters",
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
            workers.Dispose();
            Console.WriteLine("wrote " + outPath);
            return 0;
        }

        static string DefaultDemoPath(string labPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(labPath));
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(labPath) + ".demo.json");
        }

        /// <summary>One input tick from a node: replay its path plus the new input from the lab start in one run (the trailing
        /// duplicate keeps the engine from ending at the input tick's end), and stop as the new input is consumed.</summary>
        static Node Step(Simulation sim, Node from, InputTick input, bool exact, bool control, out RunResult run)
        {
            var path = new List<InputTick>(from.Path.Count + 2);
            path.AddRange(from.Path);
            path.Add(input);
            path.Add(input);
            string key = null;
            bool canDash = from.CanDash;
            float maxY = from.MaxY, x = from.X, y = from.Y;
            bool solid = from.SolidGround;
            run = sim.Run(path, null, afterTick: tick =>
            {
                // tick 0 is the first scheduler tick of a run that starts inside a lock (a dash's or spring's remaining ticks:
                // no input is consumed there). The new input tick is from.Path.Count + 1.
                if (tick <= from.Path.Count) return true;
                key = KeyOf(sim, exact);
                canDash = GetMovementBool(sim, "dashUnlocked");
                maxY = MaxReach(sim, control);
                WorldPos at = sim.PlayerWorld;
                x = at.x; y = at.y;
                solid = GroundCheckGrounds(sim, x, y);
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
                CanDash = canDash, Key = key, MaxY = maxY, X = x, Y = y, SolidGround = solid,
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
            CaptureRect finish = sim.TargetFinish;
            float x = finish.At.x, y = finish.At.y, w = finish.W, h = finish.H;
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

        sealed class TickRow
        {
            public int Tick;
            public float X, Y, Vx, Vy, Mx, My, Coyote, MaxCoyote, SinceDash;
            public bool Ground, Dash, Solid;
        }

        static TickRow Row(Simulation sim, int tick)
        {
            Vector2 v = (Vector2)VelocityField.GetValue(sim.Player), m = (Vector2)MomentumField.GetValue(sim.Player);
            WorldPos p = sim.PlayerWorld;
            return new TickRow { Tick = tick, X = p.x, Y = p.y, Vx = v.x, Vy = v.y, Mx = m.x, My = m.y,
                Ground = GetMovementBool(sim, "onGround"), Coyote = GetMovementFloat(sim, "coyoteFrames"),
                MaxCoyote = GetMovementFloat(sim, "maxCoyoteFrames"), Dash = GetMovementBool(sim, "dashActive"),
                SinceDash = GetMovementFloat(sim, "framesSinceLastDash"), Solid = GroundCheckGrounds(sim, p.x, p.y) };
        }

        /// <summary>Every physics tick of a demo from the lab start (the first row is the lab start itself).</summary>
        static List<TickRow> Observe(Simulation sim, List<InputTick> inputs)
        {
            var rows = new List<TickRow> { Row(sim, 0) };
            sim.Run(new List<InputTick>(inputs) { inputs[inputs.Count - 1] }, null, extraTicks: 400,
                onStep: s => rows.Add(Row(sim, s + 1)));
            return rows;
        }

        /// <summary>
        /// The tech a demo performs, from the Movement state on the jump tick (the in-game overlay detector's own conditions):
        /// a coyote jump is a ground jump taken with the coyote window counting (onGround held after the ground rays stopped
        /// hitting: coyoteFrames below maxCoyoteFrames), and the dash boost (the superbounce) is SpecialWallJump's 730 up plus
        /// 410 up momentum at a wall touch within dashBoostMax frames of a dash.
        /// </summary>
        static string TechSignature(List<TickRow> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                TickRow before = rows[i - 1], after = rows[i];
                if (after.Vy - before.Vy > 500f && !before.Solid && before.Ground)
                    return "coyote jump: tick " + after.Tick + " takes Velocity.y " + before.Vy.ToString("0") + " -> " + after.Vy.ToString("0")
                        + " with onGround held at coyoteFrames " + before.Coyote.ToString("0") + "/" + before.MaxCoyote.ToString("0")
                        + " and the ground check's rays already missing at x " + before.X.ToString("0.#") + " (the ground branch cannot ground there)";
                if (after.Vy > 600f && after.My > 300f && before.Dash && before.SinceDash > 0f && before.SinceDash < 7f)
                    return "dash boost bounce (superbounce): tick " + after.Tick + " takes Velocity.y " + after.Vy.ToString("0")
                        + " with momentum.y " + after.My.ToString("0") + ", " + before.SinceDash.ToString("0") + " frames into the dash at x " + before.X.ToString("0.#");
            }
            return null;
        }

        /// <summary>The human-legal input alphabet of one tick: stick x in {-1, -partial, 0, partial, 1}, y in {-1, 0, 1}, the jump
        /// button's press/hold/release/up by its state, and a dash pressed with any of its dash-jump frames (a dash press with
        /// no dash to spend is pruned: dashUnlocked is permanent in a lab run and the press can do nothing).</summary>
        static List<InputTick> Alphabet(bool jumpHeld, bool canDash, bool noJumpPress)
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
                        if (noJumpPress && j.press) continue;
                        j.x = x; j.y = y;
                        ticks.Add(j);
                        if (!canDash) continue;
                        for (int frame = 0; frame <= 12; frame++)
                        {
                            if (noJumpPress && frame > 0) continue;
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
            Configure(sim, lab);
            Vector2 anchor = new Vector2(lab.Anchor.x, lab.Anchor.y);
            using JsonDocument demo = JsonDocument.Parse(File.ReadAllText(demoPath));
            List<InputTick> inputs = demo.RootElement.GetProperty("inputs").EnumerateArray().Select(InputTick.FromJson).ToList();
            string outPath = OptS(args, "--out", null);
            var log = new StringBuilder();
            int steps = 0;
            RunResult run = sim.Run(new List<InputTick>(inputs) { inputs[inputs.Count - 1] }, null, extraTicks: 400, onStep: s =>
            {
                WorldPos p = sim.PlayerWorld;
                log.AppendLine($"{steps,3} in{(int)sim.Engine.RunTicks - 1,3} pos {p.x - anchor.x,9:0.#},{p.y - anchor.y,9:0.#}"
                    + $" V {sim.Player.Velocity.x,7:0.#},{sim.Player.Velocity.y,7:0.#} m {sim.Player.momentum.x,7:0.#},{sim.Player.momentum.y,7:0.#}"
                    + $" g{(sim.Player.onGround ? 1 : 0)} w{(sim.Player.OnWall ? 1 : 0)} {sim.Player.cutsceneMode}"
                    + $" dash{(sim.Player.dashActive ? 1 : 0)} f{sim.Player.framesSinceLastDash:0} R{(sim.Player.facingRight ? 1 : 0)}"
                    + $" cf{GetMovementFloat(sim, "coyoteFrames"):0}/{GetMovementFloat(sim, "maxCoyoteFrames"):0}");
                steps++;
            });
            if (outPath != null) File.WriteAllText(outPath, log.ToString()); else Console.Write(log);
            string signature = TechSignature(Observe(sim, inputs));
            Console.WriteLine($"demo: {inputs.Count} inputs, {steps} physics ticks; "
                + (run.Finished ? "finish at tick " + steps : "no finish (" + run.EndReason + ")")
                + "; tech: " + (signature ?? "none"));
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

        /// <summary>The lab's finish box (the capture frame) and no course finish: what a lab run is scored on.</summary>
        static void Configure(Simulation sim, LabMapDef lab)
        {
            var (fx, fy, fw, fh) = lab.Finish;
            (float ax, float ay) = lab.Anchor;
            sim.TargetCourse = -1;
            sim.TargetFinish = new CaptureRect(sim.ToCapture(new WorldPos(fx + ax, fy + ay)), fw, fh);
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
        static string KeyOf(Simulation sim, bool exact)
        {
            var sb = new StringBuilder(512);
            WorldPos p = sim.PlayerWorld;
            Quantize(sb, p.x, 1f); Quantize(sb, p.y, 1f);
            Quantize(sb, sim.Player.transform.localScale.x, 0.01f);
            var body = sim.Physics.PlayerBody();
            Quantize(sb, body.px, 1f); Quantize(sb, body.py, 1f);
            Quantize(sb, body.vx, 10f); Quantize(sb, body.vy, 10f); Quantize(sb, body.angle, 0.1f);
            foreach (FieldInfo f in MovementFields)
            {
                if (Simulation.MovementPresentationFields.Contains(f.Name)) continue;
                AddValue(sb, f.GetValue(sim.Player), exact);
            }
            foreach (FieldInfo f in EngineFields) AddValue(sb, f.GetValue(sim.Engine), exact);
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

        /// <summary>
        /// The game's own ground check at a world position (Movement.FixedUpdate's ground-branch condition: the three
        /// groundRaycastLayer rays from y - 18 down 22 u, minus the two blockingGroundRaycastLayer ones, and a jump's
        /// recentlyJumped blocks it). False = the ground rays have stopped hitting: the coyote window's own state.
        /// </summary>
        static bool GroundCheckGrounds(Simulation sim, float x, float y)
        {
            if (GetMovementBool(sim, "recentlyJumped")) return false;
            float f = sim.Player.transform.localScale.x >= 0f ? 1f : -1f;
            int ground = GetMovementInt(sim, "groundRaycastLayer"), blocking = GetMovementInt(sim, "blockingGroundRaycastLayer");
            bool h1 = Physics2D.Raycast(new Vector2(x - 15f * f, y - 18f), Vector2.down, 22f, ground);
            bool h2 = Physics2D.Raycast(new Vector2(x - 9f * f, y - 18f), Vector2.down, 22f, blocking);
            bool h3 = Physics2D.Raycast(new Vector2(x + 25f * f, y - 18f), Vector2.down, 22f, ground);
            bool h4 = Physics2D.Raycast(new Vector2(x + 21f * f, y - 18f), Vector2.down, 22f, blocking);
            bool h5 = Physics2D.Raycast(new Vector2(x + 6f * f, y - 18f), Vector2.down, 22f, ground);
            float vy = ((Vector2)VelocityField.GetValue(sim.Player)).y;
            return (h5 && !h2 && !h4) || (h1 && !h2) || (h3 && !h4 && vy < 200f);
        }

        static int GetMovementInt(Simulation sim, string name)
        {
            FieldInfo f = typeof(Movement).GetField(name, Fields);
            object v = f == null ? null : f.GetValue(sim.Player);
            return v is LayerMask m ? m.value : v is int i ? i : v is float x ? (int)x : 0;
        }

        static float GetMovementFloat(Simulation sim, string name)
        {
            FieldInfo f = typeof(Movement).GetField(name, Fields);
            object v = f == null ? null : f.GetValue(sim.Player);
            return v is float x ? x : v is int i ? i : 0f;
        }

        /// <summary>
        /// The highest world y the player can still reach with any future inputs: the ballistic arc of Velocity + momentum
        /// (momentum assumed not to decay) plus every jump and boost still in reach. A lab map has no springs or moving
        /// geometry, so a ground or coyote jump (jumpForce 805 + the apex's anti-grav under 400 u), an air jump (745), each
        /// wall jump left (wallJumpsLeft: 730 + a bounce's 220 momentum under 400 u) and one dash (a super's 805 or an
        /// omnidash's ground bounce 1405 under 900 u) cover everything the game can give.
        /// </summary>
        static float MaxReach(Simulation sim, bool control = false)
        {
            if (control) { Vector2 cv = (Vector2)VelocityField.GetValue(sim.Player), cm = (Vector2)MomentumField.GetValue(sim.Player);
                float cup = MathF.Max(0f, cv.y + cm.y); return sim.PlayerWorld.y + cup * cup / 2700f + 50f; }
            Vector2 v = (Vector2)VelocityField.GetValue(sim.Player), m = (Vector2)MomentumField.GetValue(sim.Player);
            float up = MathF.Max(0f, v.y + m.y);
            float gain = up * up / 2700f + 50f;
            if (GetMovementBool(sim, "onGround") || GetMovementFloat(sim, "coyoteFrames") > 0f || GetMovementFloat(sim, "wallCoyoteFrames") > 0f) gain += 400f;
            if (GetMovementBool(sim, "doubleJumpUnlocked") && GetMovementFloat(sim, "airJumpsLeft") > 0f) gain += 400f;
            if (GetMovementBool(sim, "wallJumpUnlocked"))
                gain += 400f * MathF.Min(4f, MathF.Max(0f, GetMovementFloat(sim, "wallJumpsLeft")));
            if (GetMovementBool(sim, "dashUnlocked") && GetMovementFloat(sim, "airDashesLeft") > 0f) gain += 900f;
            return sim.PlayerWorld.y + gain;
        }

        static readonly FieldInfo VelocityField = typeof(Movement).GetField("Velocity", Fields),
            MomentumField = typeof(Movement).GetField("momentum", Fields);
        static readonly FieldInfo[] MovementFields = FieldState.For(typeof(Movement));
        static readonly FieldInfo[] EngineFields = new[]
        {
            "_rawJumpHeld", "_dashJumpFrame", "_springDashFrame", "_springTicks", "_lockArmed", "_lockX", "_lockY",
            "_pendingTurnX", "_turnArmed", "_turnX", "_tickLocked", "_tickStick", "_waitingForStable", "_preRollTicksRemaining",
        }.Select(n => typeof(MacroEngine).GetField(n, Fields)).Where(f => f != null).ToArray();

        static void AddValue(StringBuilder sb, object value, bool exact)
        {
            switch (value)
            {
                case null: sb.Append('~'); break;
                case bool b: sb.Append(b ? '1' : '0'); break;
                case char ch: sb.Append(ch).Append(';'); break;
                case string s: sb.Append(s).Append(';'); break;
                case float f: if (exact) sb.Append(BitConverter.SingleToInt32Bits(f)).Append(','); else Quantize(sb, f, 1f); break;
                case double d: if (exact) sb.Append(BitConverter.DoubleToInt64Bits(d)).Append(','); else Quantize(sb, (float)d, 1f); break;
                case Vector2 v: if (exact) { sb.Append(BitConverter.SingleToInt32Bits(v.x)).Append(',').Append(BitConverter.SingleToInt32Bits(v.y)).Append(','); }
                    else { Quantize(sb, v.x, 10f); Quantize(sb, v.y, 10f); } break;
                case Vector3 v: if (exact) { sb.Append(BitConverter.SingleToInt32Bits(v.x)).Append(',').Append(BitConverter.SingleToInt32Bits(v.y)).Append(',').Append(BitConverter.SingleToInt32Bits(v.z)).Append(','); }
                    else { Quantize(sb, v.x, 10f); Quantize(sb, v.y, 10f); Quantize(sb, v.z, 10f); } break;
                case UnityEngine.Object o: sb.Append(o == null ? 0 : o.GetInstanceID()).Append(';'); break;
                case System.Collections.IEnumerable items:
                    foreach (object item in items) AddValue(sb, item, exact);
                    sb.Append('~');
                    break;
                default: sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';'); break;
            }
        }

        /// <summary>States within one step of each other are one search state (a demo is replayed exactly before it counts).</summary>
        static void Quantize(StringBuilder sb, float value, float step)
        {
            sb.Append(MathF.Round(value / step).ToString("0", CultureInfo.InvariantCulture)).Append(',');
        }

        static string TickText(InputTick k)
        {
            return $"{k.x:0.##},{k.y:0.##},{(k.press ? "P" : k.held ? "H" : "")}{(k.release ? "R" : "")}{(k.dash ? "D" : "")}{(k.dashJump > 0 ? "J" + k.dashJump : "")}";
        }

        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
        static string Fmt(float f) { return f.ToString("R", CultureInfo.InvariantCulture); }
        static JsonNode Trim(float f) { return (JsonValue)(double)Math.Round(f, 3); }
        static int Opt(string[] args, string name, int fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback; }
        static string OptS(string[] args, string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    }
}
