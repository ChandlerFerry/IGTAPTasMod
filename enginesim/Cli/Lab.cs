using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
                        + "  lab solve <trace.json> <labmap.json> [--out <file>] [--max-ticks N] [--threads N] [--exact]\n"
                        + "            [--forbid-jump-not-grounded] [--forbid-wall-coyote-jump] [--forbid-boosted-double-jump]\n"
                        + "            [--from <demo.json> [--from-inputs N]] [--check-bound [--check-depth N]] [--check-rollouts N [--seed S]]\n"
                        + "                                         injects the lab map into the trace's world and searches, exhaustively, for\n"
                        + "                                         the minimum-tick human-legal input sequence from the lab's start to its\n"
                        + "                                         finish box; writes labmaps/<id>.demo.json. The controls: no jump press\n"
                        + "                                         off solid ground; no wall jump unless the wall rays hit; no double jump\n"
                        + "                                         at Velocity.y 200 or more. --from: every run starts with a demo's first\n"
                        + "                                         N inputs (all of them without --from-inputs). A state with its dash\n"
                        + "                                         spent, off ground and wall, whose highest reach by the last tick keeps\n"
                        + "                                         the collider under the box is cut (the free-flight bound);\n"
                        + "                                         --check-bound cuts nothing and checks every state against it,\n"
                        + "                                         --check-rollouts checks it on N random runs instead of searching\n"
                        + "  lab replay <trace.json> <labmap.json> <demo.json> [--out <file>] [--write-path [--path-ticks N]]\n"
                        + "                                         one demo replayed from the lab start, tick by tick; --write-path\n"
                        + "                                         (re)writes the demo's ghost path (--path-ticks N: its own first N\n"
                        + "                                         physics ticks, no tail, finish or not: a lab's compared ghosts)");
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
            /// <summary>The free-flight bound (<see cref="LabFlight"/>): the highest the player's collider top can be on any tick up
            /// to the search's last (world frame); +infinity where the bound doesn't apply.</summary>
            public float FlightTop = float.PositiveInfinity;
            /// <summary>--check-bound: per physics tick from the lab start, the lowest free-flight bound of this node and every
            /// ancestor (a descendant's actual y above it would prove the bound wrong).</summary>
            public float[] Envelope;
            /// <summary>Why the free-flight bound doesn't cover this state (null where it does).</summary>
            public string FlightWhy;
        }

        /// <summary>The free-flight bound's settings for Step: the search's last tick, the boost control's double jump gate, and
        /// --check-bound (keep every state's bound curve to check its descendants against).</summary>
        sealed class FlightOptions
        {
            public int MaxTicks;
            public bool Boost, Keep;
        }

        /// <summary>A reason without its numbers, to count states by it.</summary>
        static string Reason(string why)
        {
            if (why == null) return "?";
            int cut = why.IndexOfAny("0123456789(-".ToCharArray());
            return (cut > 0 ? why.Substring(0, cut) : why).TrimEnd(' ', ',', ':');
        }

        static int Solve(string[] args)
        {
            string tracePath = args[0], labPath = args[1];
            LabMapDef lab = LabMapDef.Load(labPath);
            int maxTicks = Opt(args, "--max-ticks", lab.maxTicks);
            string outPath = OptS(args, "--out", DefaultDemoPath(labPath));
            int threads = Math.Max(1, Opt(args, "--threads", Environment.ProcessorCount));
            // --exact: dedupe on the bit-exact state (no quantization). The default key is coarse (1 u, 10 u/s): it finds a
            // demo fast for an upper bound B; the exact pass then proves nothing shorter exists (or finds the true minimum).
            bool exact = args.Contains("--exact");
            // The negative control: no jump press (nor a dash's dash-jump frame) unless the state is on solid ground. Every
            // jump a lab's start can press is the tech's own (a coyote window, or a buffered jump for a dash boost), so a run
            // that finds nothing proves the finish needs the tech.
            bool control = args.Contains("--forbid-jump-not-grounded");
            // The wall control: no wall jump unless the wall rays hit on that tick (the cling branch ran). Ground jumps and wall
            // jumps off a wall stay legal; a wall jump off wall coyote frames (the rays already missing) or off a stored count
            // does not, so a run that finds nothing proves the finish needs one.
            bool wallControl = args.Contains("--forbid-wall-coyote-jump");
            // The boost control: no double jump that finds Velocity.y at 200 or more (only the 700 gate after a jump that wasn't
            // a normal one lets it through). Every other jump, a double jump under 200 included, stays legal, so a run that finds
            // nothing within a bound proves the finish needs the boosted double jump by then.
            bool boostControl = args.Contains("--forbid-boosted-double-jump");
            // The free-flight bound (LabFlight): a state with its dash spent, off ground and wall, whose highest reach by the last
            // tick keeps its collider under the finish box is cut (admissible: LabFlight's summary argues why). --check-bound cuts
            // nothing and checks the bound instead: no state may ever be above the bound any of its ancestors computed.
            bool checkBound = args.Contains("--check-bound");
            // --check-depth N: with --check-bound, expand states only up to tick N (the bounds still run to --max-ticks).
            int checkDepth = checkBound ? Opt(args, "--check-depth", maxTicks) : maxTicks;
            // --check-rollouts N [--seed S]: N random runs from the start to --max-ticks instead of the search, each state checked
            // against the bounds of every earlier state of its run (the bound's long horizons, which a breadth-first check can't reach).
            int rollouts = Opt(args, "--check-rollouts", 0);
            var flight = new FlightOptions { MaxTicks = maxTicks, Boost = boostControl, Keep = checkBound || rollouts > 0 };
            // --from <demo.json> --from-inputs N: the search starts from the state a demo's first N inputs reach (replayed from
            // the lab start, so the state is the game's own, its pending Invokes included) and every run it finds or rules out
            // begins with them: a control over everything that can follow a fixed opening.
            string fromPath = OptS(args, "--from", null);
            List<InputTick> prefix = null;
            if (fromPath != null)
            {
                using JsonDocument from = JsonDocument.Parse(File.ReadAllText(fromPath));
                List<InputTick> demoInputs = from.RootElement.GetProperty("inputs").EnumerateArray().Select(InputTick.FromJson).ToList();
                int count = Opt(args, "--from-inputs", demoInputs.Count);
                if (count < 1 || count > demoInputs.Count) throw new ArgumentException("--from-inputs takes 1 to " + demoInputs.Count + " inputs of " + fromPath + ".");
                prefix = demoInputs.Take(count).ToList();
            }

            var (sim, trace, _) = Open(tracePath, lab);
            Place(sim, lab);
            Configure(sim, lab);
            var (fx, fy, fw, fh) = lab.Finish;
            Vector2 anchor = new Vector2(lab.Anchor.x, lab.Anchor.y);
            if (TouchesAtStart(sim)) throw new InvalidOperationException("The lab's start is inside its finish box.");
            // The player's collider around the transform over every collider config: the finish is a touch of the box by it.
            var (colliderTop, colliderBottom, colliderReach) = LabFlight.Extents(sim.Player);
            bool springs = lab.shapes.Any(s => s.spring != null);

            int alphabet = Alphabet(false, true, false).Count;
            Console.WriteLine($"lab {lab.id} at {anchor.x:0.#},{anchor.y:0.#} (world frame) in {Path.GetFileName(tracePath)}: "
                + $"{lab.shapes.Count} shapes, category {lab.category}; finish box {fw:0}x{fh:0} at {fx:0.#},{fy:0.#} from the anchor; "
                + $"search to {maxTicks} ticks, alphabet {alphabet} inputs/tick, {(exact ? "bit-exact" : "quantized")} key{(control ? ", control run (no jump press off solid ground)" : "")}{(wallControl ? ", wall control (no wall jump unless the wall rays hit)" : "")}{(boostControl ? ", boost control (no double jump at Velocity.y 200 or more)" : "")}");
            Console.WriteLine("  " + LabFlight.Describe(sim) + (checkBound ? "; --check-bound: no cut, every state checked against its ancestors' bounds" : ""));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool startCanDash = GetMovementBool(sim, "dashUnlocked");
            WorldPos startAt = sim.PlayerWorld;
            var start = new Node { Path = new List<InputTick>(), Ticks = 0, JumpHeld = false, CanDash = startCanDash,
                Key = KeyOf(sim, exact), MaxY = MaxReach(sim, control), X = startAt.x, Y = startAt.y,
                SolidGround = GroundCheckGrounds(sim, startAt) };
            if (prefix != null)
            {
                bool held = false;
                foreach (InputTick k in prefix.Take(prefix.Count - 1)) held = k.press || k.held && !k.release || held && !k.release && !k.press;
                var opening = new Node { Path = prefix.Take(prefix.Count - 1).ToList(), JumpHeld = held, CanDash = startCanDash,
                    MaxY = start.MaxY, X = start.X, Y = start.Y, SolidGround = start.SolidGround };
                SimSnapshot none = null;
                start = Step(sim, opening, prefix[prefix.Count - 1], ref none, exact, control, wallControl, boostControl, flight, out RunResult openingRun, out _)
                    ?? throw new InvalidOperationException("The opening (" + prefix.Count + " inputs of " + fromPath + ") "
                        + (openingRun == null ? "breaks a control" : openingRun.Finished ? "already finishes" : "ends the run: " + openingRun.EndReason) + ".");
                Console.WriteLine($"  from {Path.GetFileName(fromPath)}'s first {prefix.Count} inputs: physics tick {start.Ticks}, "
                    + $"{start.X - anchor.x:0.#},{start.Y - anchor.y:0.#} from the anchor; free-flight bound: "
                    + (float.IsPositiveInfinity(start.FlightTop) ? "none (" + start.FlightWhy + ")" : $"collider top at most {start.FlightTop - anchor.y:0.#} by tick {maxTicks}"));
            }
            if (rollouts > 0)
                return CheckRollouts(sim, start, rollouts, Opt(args, "--seed", 1), maxTicks, exact, control, wallControl, boostControl, flight,
                    fy + anchor.y - fh / 2f, colliderTop, anchor);
            var visited = new Dictionary<string, int> { [start.Key] = start.Ticks };
            var buckets = new List<Node>[maxTicks + 64];
            for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<Node>();
            buckets[start.Ticks].Add(start);
            long states = 1, extensions = 0, dead = 0, dupes = 0;
            Node best = null;
            InputTick bestInput = default;
            int bound = maxTicks + 1;
            bool announced = false;
            // One world and simulation per dedicated worker thread (the game's statics are per thread): --threads worlds, each
            // expanding the nodes it takes from the tick's bucket. A node's path replays from the lab start once, in its first
            // input's run, which captures the node's state as it reaches it; its other inputs resume from that state. Ticks in
            // order: the first finish is the minimum, and once one is found every state that could still reach the finish box
            // sooner (a state at tick T finishes at T + 1 at the earliest) is explored to prove it cannot.
            float boxBottom = fy + anchor.y - fh / 2f;
            float bx0 = fx + anchor.x - fw / 2f, bx1 = fx + anchor.x + fw / 2f;
            float by0 = boxBottom, by1 = fy + anchor.y + fh / 2f;
            long prunedHeight = 0, prunedTime = 0, prunedFlight = 0, explored = 0, localDead = 0, boundChecks = 0, boundViolations = 0;
            var violations = new List<string>();
            float highestCut = float.NegativeInfinity;
            var unbounded = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
            List<Node> bucket = null;
            int bucketTicks = 0, nextNode = -1;
            // Per node of the bucket, in its order: the children kept and the finishes found, merged in that order, so the
            // search is the same however the nodes fell to the workers.
            List<Node>[] children = null;
            List<(InputTick input, int at)>[] finishes = null;
            bool searching = true;
            Exception failure = null;
            var phase = new Barrier(threads + 1);

            void Expand(Simulation worker, int index)
            {
                Node node = bucket[index];
                int ticks = bucketTicks;
                if (node.Ticks != ticks) return;
                if (Volatile.Read(ref bound) <= ticks + 1) return;
                // Reachability (admissible: only provably dead states are cut; the finish is a touch of the box by the player's
                // collider, so each test allows for its extents). Free flight: the dash spent, off ground and wall, the highest
                // the collider's top can be by the last tick is under the box (LabFlight). Height: no inputs can lift the player
                // into the finish box (MaxReach over-estimates every rise the game can give but a spring's, so a lab with a
                // spring has no height prune). Time: the game's own maxTranslationSpeed (100 u/physics step) is the most any
                // input can move the player either way, and a spring's push (hitSpring's 10 u position write) on top of it,
                // so a state more than that per remaining tick from the box cannot finish before the bound.
                if (float.IsPositiveInfinity(node.FlightTop)) unbounded.AddOrUpdate(Reason(node.FlightWhy), 1, (_, n) => n + 1);
                else if (!checkBound && node.FlightTop <= boxBottom)
                {
                    Interlocked.Increment(ref prunedFlight);
                    lock (violations) highestCut = MathF.Max(highestCut, node.FlightTop);
                    return;
                }
                if (!springs && node.MaxY + colliderTop <= boxBottom) { Interlocked.Increment(ref prunedHeight); return; }
                float dx = MathF.Max(MathF.Max(bx0 - (node.X + colliderReach), node.X - colliderReach - bx1), 0f);
                float dy = MathF.Max(MathF.Max(by0 - (node.Y + colliderTop), node.Y + colliderBottom - by1), 0f);
                if (MathF.Max(dx, dy) > (maxTicks - node.Ticks) * (springs ? 110f : 100f)) { Interlocked.Increment(ref prunedTime); return; }
                SimSnapshot state = null;
                List<Node> kept = null;
                List<(InputTick, int)> done = null;
                foreach (InputTick input in Alphabet(node.JumpHeld, node.CanDash, control && !node.SolidGround))
                {
                    if (Volatile.Read(ref bound) <= ticks + 1) break;
                    Interlocked.Increment(ref explored);
                    Node child = Step(worker, node, input, ref state, exact, control, wallControl, boostControl, flight, out RunResult run, out int stepped);
                    if (child == null)
                    {
                        if (run != null && run.Finished)
                        {
                            (done ??= new List<(InputTick, int)>()).Add((input, stepped));
                            // A finish is a touch of the box: the bound's collider top has to reach it on that tick.
                            if (checkBound && node.Envelope != null && stepped < node.Envelope.Length && node.Envelope[stepped] + colliderTop <= boxBottom)
                            {
                                Interlocked.Increment(ref boundViolations);
                                lock (violations) if (violations.Count < 10) violations.Add($"a finish on tick {stepped} under the bound");
                            }
                        }
                        else Interlocked.Increment(ref localDead);
                        continue;
                    }
                    if (checkBound && node.Envelope != null)
                    {
                        Interlocked.Increment(ref boundChecks);
                        if (child.Ticks < node.Envelope.Length && child.Y > node.Envelope[child.Ticks])
                        {
                            Interlocked.Increment(ref boundViolations);
                            lock (violations)
                                if (violations.Count < 10)
                                    violations.Add($"tick {child.Ticks}: y {child.Y - anchor.y:0.###} above the bound {node.Envelope[child.Ticks] - anchor.y:0.###} after "
                                        + string.Join(" | ", child.Path.Select(TickText)));
                        }
                        child.Envelope ??= (float[])node.Envelope.Clone();
                        for (int t = 0; t < child.Envelope.Length; t++) child.Envelope[t] = MathF.Min(child.Envelope[t], node.Envelope[t]);
                    }
                    (kept ??= new List<Node>()).Add(child);
                }
                children[index] = kept;
                finishes[index] = done;
            }

            var pool = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
            {
                Simulation worker = null;
                try
                {
                    var (opened, _, _) = Open(tracePath, lab);
                    Place(opened, lab);
                    Configure(opened, lab);
                    worker = opened;
                }
                catch (Exception e) { Interlocked.CompareExchange(ref failure, e, null); }
                while (true)
                {
                    phase.SignalAndWait();
                    if (!searching) return;
                    try
                    {
                        int i;
                        while (worker != null && (i = Interlocked.Increment(ref nextNode)) < bucket.Count) Expand(worker, i);
                    }
                    catch (Exception e) { Interlocked.CompareExchange(ref failure, e, null); }
                    phase.SignalAndWait();
                }
            }, 16 << 20) { IsBackground = true, Name = "lab-solve" }).ToList();
            pool.ForEach(t => t.Start());
            try
            {
                for (int ticks = 0; ticks < buckets.Length && ticks < bound && ticks < checkDepth; ticks++)
                {
                    bucket = buckets[ticks];
                    bucketTicks = ticks;
                    nextNode = -1;
                    children = new List<Node>[bucket.Count];
                    finishes = new List<(InputTick, int)>[bucket.Count];
                    explored = localDead = 0;
                    phase.SignalAndWait();
                    phase.SignalAndWait();
                    if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                    for (int i = 0; i < bucket.Count; i++)
                        if (finishes[i] != null)
                            foreach (var (input, at) in finishes[i])
                                if (at < bound) { best = bucket[i]; bestInput = input; bound = at; }
                    extensions += explored;
                    dead += localDead;
                    if (best != null && !announced)
                    {
                        announced = true;
                        Console.WriteLine($"  finish at tick {bound} (input tick {best.Path.Count + 1}) after {extensions:N0} extensions; exploring every state below the bound to prove it is the minimum");
                    }
                    foreach (List<Node> kept in children)
                    {
                        if (kept == null) continue;
                        foreach (Node child in kept)
                        {
                            if (child.Ticks >= bound) continue;
                            if (visited.TryGetValue(child.Key, out int seen) && seen <= child.Ticks) { dupes++; continue; }
                            visited[child.Key] = child.Ticks;
                            states++;
                            buckets[child.Ticks].Add(child);
                        }
                    }
                    if (bucket.Count > 0)
                        Console.WriteLine($"  [{clock.Elapsed.TotalSeconds:0.#}s] tick {ticks}: {bucket.Count:N0} states expanded ({explored:N0} extensions); {states:N0} states, "
                            + $"{visited.Count:N0} keys, {dead:N0} dead ends, prunes {prunedFlight:N0} flight / {prunedHeight:N0} height / {prunedTime:N0} time, best {(best == null ? "-" : bound.ToString())}"
                            + (unbounded.IsEmpty ? "" : "; without the free-flight bound so far: " + string.Join(", ", unbounded.OrderByDescending(p => p.Value).Take(4).Select(p => $"{p.Value:N0} {p.Key}"))));
                    bucket.Clear();
                }
            }
            finally
            {
                searching = false;
                phase.SignalAndWait();
                pool.ForEach(t => t.Join());
                phase.Dispose();
            }
            string boundLine = "free-flight bound: " + (checkBound
                ? $"checked, not cut (states expanded to tick {checkDepth}): {boundChecks:N0} states against their ancestors' bounds, {boundViolations:N0} above one"
                : $"{prunedFlight:N0} states cut" + (prunedFlight > 0 ? $" (the highest collider top any of them could reach: {highestCut - anchor.y:0.##}, the box's bottom {boxBottom - anchor.y:0.##})" : "")) + "; states it didn't cover: "
                + (unbounded.IsEmpty ? "none" : string.Join(", ", unbounded.OrderByDescending(p => p.Value).Select(p => $"{p.Value:N0} {p.Key}")));
            Console.WriteLine(boundLine);
            foreach (string v in violations) Console.WriteLine("  BOUND VIOLATED: " + v);
            if (best == null)
            {
                Console.WriteLine($"No demo within {maxTicks} ticks: {extensions:N0} extensions, {states:N0} states, {visited.Count:N0} keys, {dead:N0} dead ends, {dupes:N0} duplicates, prunes {prunedFlight:N0} flight / {prunedHeight:N0} height / {prunedTime:N0} time, {clock.Elapsed.TotalSeconds:0.0}s");
                return boundViolations > 0 ? 3 : 1;
            }

            List<InputTick> inputs = new List<InputTick>(best.Path);
            inputs.Add(bestInput);
            Console.WriteLine("  inputs: " + string.Join(" | ", inputs.Select(TickText)));
            // Each worker set the shared input hooks on its own thread; the simulation made on this thread holds this thread's,
            // so the verification runs on it (its own world and lab).
            Simulation verify = sim;
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
                + $"prunes {prunedFlight:N0} flight / {prunedHeight:N0} height / {prunedTime:N0} time, bound {finishTick} (every state below it exhausted), {clock.Elapsed.TotalSeconds:0.0}s");

            var search = new
            {
                states, explored = extensions, keys = visited.Count, deadEnds = dead, duplicates = dupes,
                bound = finishTick, complete = true, maxTicks, alphabet, exact, control, wallControl, boostControl,
                from = fromPath == null ? null : Path.GetFileName(fromPath), fromInputs = prefix?.Count, prunedFlight, prunedHeight, prunedTime, tech = signature,
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
                path = GhostPath(tracePath, lab, inputs, finishTick),
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("wrote " + outPath);
            return 0;
        }

        /// <summary>
        /// --check-rollouts: random runs from the start to the last tick (a new press, jump or dash, on 1 input in 8, else an
        /// input without one; each from the alphabet), each state checked against the lowest free-flight bound any earlier state of its run
        /// gave for its tick, and a finish against the box. Reports the checks, the violations and the tightest slack seen.
        /// </summary>
        static int CheckRollouts(Simulation sim, Node start, int rollouts, int seed, int maxTicks, bool exact, bool control, bool wallControl,
            bool boostControl, FlightOptions flight, float boxBottom, float colliderTop, Vector2 anchor)
        {
            var rng = new System.Random(seed);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long checks = 0, violations = 0, steps = 0, finishes = 0, bounded = 0;
            float tightest = float.PositiveInfinity;
            string tightestAt = null;
            for (int r = 0; r < rollouts; r++)
            {
                Node node = start;
                float[] envelope = start.Envelope == null ? null : (float[])start.Envelope.Clone();
                var path = new List<InputTick>();
                while (node.Ticks < maxTicks)
                {
                    List<InputTick> alphabet = Alphabet(node.JumpHeld, node.CanDash, control && !node.SolidGround);
                    List<InputTick> plain = alphabet.Where(k => !k.dash && !k.press).ToList(), presses = alphabet.Where(k => k.dash || k.press).ToList();
                    InputTick input = (rng.Next(8) > 0 || presses.Count == 0) && plain.Count > 0 ? plain[rng.Next(plain.Count)] : presses[rng.Next(presses.Count)];
                    SimSnapshot none = null;
                    Node child = Step(sim, node, input, ref none, exact, control, wallControl, boostControl, flight, out RunResult run, out int stepped);
                    steps++;
                    if (child == null)
                    {
                        if (run != null && run.Finished)
                        {
                            finishes++;
                            if (envelope != null && stepped < envelope.Length && envelope[stepped] + colliderTop <= boxBottom)
                            {
                                violations++;
                                Console.WriteLine($"  BOUND VIOLATED: a finish on tick {stepped} under the bound after "
                                    + string.Join(" | ", node.Path.Append(input).Select(TickText)));
                            }
                        }
                        break;
                    }
                    if (envelope != null && child.Ticks < envelope.Length && !float.IsPositiveInfinity(envelope[child.Ticks]))
                    {
                        checks++;
                        float slack = envelope[child.Ticks] - child.Y;
                        if (slack < tightest) { tightest = slack; tightestAt = $"tick {child.Ticks}, y {child.Y - anchor.y:0.###}"; }
                        if (slack < 0f)
                        {
                            violations++;
                            if (violations <= 10)
                                Console.WriteLine($"  BOUND VIOLATED: tick {child.Ticks}: y {child.Y - anchor.y:0.###} above the bound {envelope[child.Ticks] - anchor.y:0.###} after "
                                    + string.Join(" | ", child.Path.Select(TickText)));
                        }
                    }
                    if (child.Envelope != null)
                    {
                        bounded++;
                        if (envelope == null) envelope = (float[])child.Envelope.Clone();
                        else for (int t = 0; t < envelope.Length; t++) envelope[t] = MathF.Min(envelope[t], child.Envelope[t]);
                    }
                    node = child;
                }
                if ((r + 1) % 1000 == 0)
                    Console.WriteLine($"  [{clock.Elapsed.TotalSeconds:0.#}s] {r + 1:N0} runs, {steps:N0} steps, {checks:N0} checks, {violations:N0} violations");
            }
            Console.WriteLine($"free-flight bound check: {rollouts:N0} random runs to tick {maxTicks} (seed {seed}), {steps:N0} steps, {bounded:N0} states with a bound, "
                + $"{checks:N0} states checked against an earlier state's bound, {violations:N0} above it, {finishes:N0} finishes; tightest slack "
                + (tightestAt == null ? "-" : $"{tightest:0.###} u ({tightestAt})") + $", {clock.Elapsed.TotalSeconds:0.0}s");
            return violations > 0 ? 3 : 0;
        }

        static string DefaultDemoPath(string labPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(labPath));
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(labPath) + ".demo.json");
        }

        /// <summary>One input tick from a node: its path plus the new input (the trailing duplicate keeps the engine from ending
        /// at the input tick's end), stopping as the new input is consumed. With no <paramref name="state"/> yet the run replays
        /// the path from the lab start and captures the node's state as it reaches it (its input tick from.Path.Count); with one
        /// it resumes there, with the same step budget, so a node's path replays once for its whole alphabet.
        /// <paramref name="stepped"/> counts physics ticks from the lab start either way. The wall control
        /// (<paramref name="wallControl"/>) ends a run whose jump would be a wall jump off no wall (coyote or stored), the boost
        /// control (<paramref name="boostControl"/>) one whose jump would be a double jump at Velocity.y 200 or more. The child
        /// carries the free-flight bound (<see cref="LabFlight"/>) to <paramref name="flight"/>'s last tick.</summary>
        static Node Step(Simulation sim, Node from, InputTick input, ref SimSnapshot state, bool exact, bool control, bool wallControl,
            bool boostControl, FlightOptions flight, out RunResult run, out int stepped)
        {
            int at = from.Path.Count;
            var path = new List<InputTick>(at + 2);
            path.AddRange(from.Path);
            path.Add(input);
            path.Add(input);
            string key = null;
            bool canDash = from.CanDash;
            float maxY = from.MaxY, x = from.X, y = from.Y;
            bool solid = from.SolidGround, illegal = false;
            SimSnapshot resume = state, captured = null;
            int steps = 0;
            float flightTop = float.PositiveInfinity;
            float[] envelope = null;
            string flightWhy = null;
            sim.BeforeInput = wallControl || boostControl ? m =>
            {
                if (illegal) return;
                if (wallControl && WallJumpOffWall(m)) { illegal = true; sim.Engine.Abort("control: a wall jump off no wall"); }
                else if (boostControl && BoostedDoubleJump(m)) { illegal = true; sim.Engine.Abort("control: a boosted double jump"); }
            } : null;
            // A resumed run has from.Ticks physics ticks behind it: its budget is what is left of the run from the start's.
            run = sim.Run(path, resume, onStep: _ => steps++, afterTick: tick =>
            {
                // tick 0 is the first scheduler tick of a run that starts inside a lock (a dash's or spring's remaining ticks:
                // no input is consumed there). The new input tick is from.Path.Count + 1.
                if (illegal) return false;
                if (tick <= at)
                {
                    if (resume == null && tick == at && at > 0) captured = sim.Capture(tick);
                    return true;
                }
                key = KeyOf(sim, exact);
                canDash = GetMovementBool(sim, "dashUnlocked");
                maxY = MaxReach(sim, control);
                WorldPos p = sim.PlayerWorld;
                x = p.x; y = p.y;
                solid = GroundCheckGrounds(sim, p);
                int now = (resume == null ? 0 : from.Ticks) + steps, horizon = flight.MaxTicks - now;
                float[] curve = horizon >= 1 ? LabFlight.Bound(sim, horizon, flight.Boost, out flightWhy) : null;
                if (horizon < 1) flightWhy = "no ticks left";
                if (curve != null)
                {
                    float top = LabFlight.Extents(sim.Player).top;
                    flightTop = float.NegativeInfinity;
                    for (int t = 1; t < curve.Length; t++) flightTop = MathF.Max(flightTop, curve[t] + top);
                    if (flight.Keep)
                    {
                        envelope = new float[flight.MaxTicks + 1];
                        Array.Fill(envelope, float.PositiveInfinity);
                        for (int t = 0; t < curve.Length; t++) envelope[now + t] = curve[t];
                    }
                }
                return false;
            }, extraTicks: resume == null ? 400 : 400 + at - from.Ticks);
            sim.BeforeInput = null;
            if (captured != null) state = captured;
            stepped = (resume == null ? 0 : from.Ticks) + run.Stepped;
            // A run a control ended is dead, never a finish.
            if (illegal) run = null;
            if (key == null) return null;
            var childPath = new List<InputTick>(at + 1);
            childPath.AddRange(from.Path);
            childPath.Add(input);
            return new Node
            {
                Path = childPath,
                Ticks = stepped, JumpHeld = input.press || input.held && !input.release || from.JumpHeld && !input.release && !input.press,
                CanDash = canDash, Key = key, MaxY = maxY, X = x, Y = y, SolidGround = solid,
                FlightTop = flightTop, FlightWhy = flightWhy, Envelope = envelope,
            };
        }

        static readonly FieldInfo JumpBufferField = typeof(Movement).GetField("jumpBuffer", Fields),
            WallCoyoteField = typeof(Movement).GetField("wallCoyoteFrames", Fields),
            DashBufferField = typeof(Movement).GetField("dashBuffer", Fields),
            NormalJumpField = typeof(Movement).GetField("MostRecentUpwardsMoveWasNormalJump", Fields);

        /// <summary>
        /// At getPlayerControlledInput's start: the buffered jump is about to be a wall jump (Movement's branch: not grounded,
        /// OnWall or wallCoyoteFrames above 0, a wall jump left) on a tick the cling branch did not run (it resets
        /// wallCoyoteFrames to maxWallCoyoteFrames with OnWall): a wall coyote jump or a stored one, off no wall.
        /// </summary>
        static bool WallJumpOffWall(Movement m)
        {
            float wallCoyote = (float)WallCoyoteField.GetValue(m);
            return (bool)JumpBufferField.GetValue(m) && !m.onGround && (m.OnWall || wallCoyote > 0f)
                && !(m.OnWall && wallCoyote >= m.maxWallCoyoteFrames) && m.wallJumpsLeft > 0 && m.wallJumpUnlocked;
        }

        /// <summary>
        /// At getPlayerControlledInput's start: the buffered jump is about to be a boosted double jump. Movement's branches in
        /// order: a dash press the dash branch takes returns first (1601-1666), a grounded jump is a ground jump (1676), a jump
        /// with OnWall or wallCoyoteFrames above 0 goes to the wall branch (1707) whether or not a wall jump is left, and the
        /// air jump (1757-1774) needs wallCoyoteFrames under its max and an air jump left. It is boosted when it finds
        /// Velocity.y at 200 or more, which only the 700 gate after a jump that wasn't a normal one lets through; it then keeps
        /// half of it (745 + v/2).
        /// </summary>
        static bool BoostedDoubleJump(Movement m)
        {
            if ((bool)DashBufferField.GetValue(m) && m.dashCooldown < 0f && m.dashUnlocked
                && m.wallJumpMovementLockRemaining < m.wallJumpMovementLockFrames - 5f) return false;
            float wallCoyote = (float)WallCoyoteField.GetValue(m);
            if (!(bool)JumpBufferField.GetValue(m) || m.onGround || m.OnWall || wallCoyote > 0f) return false;
            if (!m.doubleJumpUnlocked || !(wallCoyote < m.maxWallCoyoteFrames) || m.airJumpsLeft <= 0) return false;
            float vy = ((Vector2)VelocityField.GetValue(m)).y;
            return !(bool)NormalJumpField.GetValue(m) && vy >= 200f && vy < 700f;
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
            public int WallJumps;
            /// <summary>OnWall and wallCoyoteFrames as getPlayerControlledInput found them on this tick (after the contact branch;
            /// NaN when the tick ran no input: a dash or a freeze), and maxWallCoyoteFrames.</summary>
            public bool InputOnWall;
            public float InputWallCoyote = float.NaN, MaxWallCoyote;
            /// <summary>The tick's jump is a boosted double jump (<see cref="BoostedDoubleJump"/> at getPlayerControlledInput's
            /// start), and the Velocity.y it found there.</summary>
            public bool InputBoostedDoubleJump;
            public float InputVy = float.NaN;
        }

        static TickRow Row(Simulation sim, int tick)
        {
            Vector2 v = (Vector2)VelocityField.GetValue(sim.Player), m = (Vector2)MomentumField.GetValue(sim.Player);
            WorldPos p = sim.PlayerWorld;
            return new TickRow { Tick = tick, X = p.x, Y = p.y, Vx = v.x, Vy = v.y, Mx = m.x, My = m.y,
                Ground = GetMovementBool(sim, "onGround"), Coyote = GetMovementFloat(sim, "coyoteFrames"),
                MaxCoyote = GetMovementFloat(sim, "maxCoyoteFrames"), Dash = GetMovementBool(sim, "dashActive"),
                SinceDash = GetMovementFloat(sim, "framesSinceLastDash"), Solid = GroundCheckGrounds(sim, p),
                WallJumps = sim.Player.wallJumpsLeft, MaxWallCoyote = sim.Player.maxWallCoyoteFrames };
        }

        /// <summary>Every physics tick of a demo from the lab start (the first row is the lab start itself).</summary>
        static List<TickRow> Observe(Simulation sim, List<InputTick> inputs)
        {
            var rows = new List<TickRow> { Row(sim, 0) };
            bool onWall = false, boosted = false;
            float wallCoyote = float.NaN, vy = float.NaN;
            sim.BeforeInput = m =>
            {
                onWall = m.OnWall;
                wallCoyote = (float)WallCoyoteField.GetValue(m);
                boosted = BoostedDoubleJump(m);
                vy = ((Vector2)VelocityField.GetValue(m)).y;
            };
            sim.Run(new List<InputTick>(inputs) { inputs[inputs.Count - 1] }, null, extraTicks: 400, onStep: s =>
            {
                TickRow row = Row(sim, s + 1);
                row.InputOnWall = onWall;
                row.InputWallCoyote = wallCoyote;
                row.InputBoostedDoubleJump = boosted;
                row.InputVy = vy;
                rows.Add(row);
                wallCoyote = vy = float.NaN;
                boosted = false;
            });
            sim.BeforeInput = null;
            return rows;
        }

        /// <summary>Ticks the ghost path runs on past the finish (3 s), so the jump's whole arc and its reach show.</summary>
        const int GhostTail = 150;

        /// <summary>
        /// The demo's ghost path (<c>path</c> in the demo file, the studio preview's and the in-game ghost's): the player's position
        /// relative to the anchor and facing (±1) at the lab start and after every physics tick, to the finish and
        /// <see cref="GhostTail"/> ticks on with the stick held full the way the dot faces at the finish and the jump button let go
        /// (the run carries on, so the distance the tech buys shows), with no finish box to stop at. Frames where time stands
        /// still (a dash freeze, a pause) add a point per physics tick's worth of them (5.6 at 280 fps), so the ghost keeps 1x's
        /// pace and a pauselift's climb shows over the time the pause takes instead of in one step. <paramref name="tail"/> 0 (a
        /// comparison ghost, <c>--path-ticks</c>): the demo's own first <paramref name="finishTick"/> physics ticks, nothing after.
        /// </summary>
        static List<float[]> GhostPath(string tracePath, LabMapDef lab, List<InputTick> inputs, int finishTick, int tail = GhostTail)
        {
            var (sim, trace, _) = Open(tracePath, lab);
            using (trace)
            {
                Place(sim, lab);
                sim.TargetCourse = -1;
                (float ax, float ay) = lab.Anchor;
                // [x, y, facing, animation, animator speed]: the position relative to the anchor, the sprite's facing (the sign of
                // localScale.x, which a wall cling sets to the wall's side), and Movement's Animator "Animation" state with its
                // speed (the run cycle's speed follows Velocity.x), so the in-game ghost plays the player's own animations.
                float[] Point()
                {
                    WorldPos p = sim.PlayerWorld;
                    Animator animator = sim.Player.animator;
                    return new[] { MathF.Round(p.x - ax, 1), MathF.Round(p.y - ay, 1), sim.Player.transform.localScale.x < 0f ? -1f : 1f,
                        animator == null ? 0f : animator.GetInteger("Animation"), animator == null ? 1f : MathF.Round(animator.speed, 2) };
                }
                var path = new List<float[]> { Point() };
                int keep = finishTick + tail;
                // The ghost shows each jump at its fullest: from a jump press on, the button stays held until the tick before the
                // next press (no jump cut: Movement cuts a normal jump to 50 up when it's let go while Velocity.y is 150-580), and
                // after the last press to the end. The engine ends with its inputs, so tail ticks carry the run on past the finish,
                // the stick held the way it faces there.
                bool facingRight = true;
                sim.Run(new List<InputTick>(inputs) { inputs[inputs.Count - 1] }, null, extraTicks: 0,
                    onStep: s => { if (s + 1 == finishTick) facingRight = sim.Player.facingRight; });
                var run = new List<InputTick>(inputs.Take(Math.Min(inputs.Count, finishTick)));
                bool holding = false;
                for (int i = 0; i < run.Count; i++)
                {
                    if (run[i].press) { holding = true; continue; }
                    if (!holding) continue;
                    bool pressNext = i + 1 < run.Count && run[i + 1].press;
                    run[i] = run[i] with { held = !pressNext, release = pressNext };
                    if (pressNext) holding = false;
                }
                while (run.Count < keep + 1) run.Add(new InputTick { x = facingRight ? 1f : -1f, held = holding });
                // Frames per physics tick at playback's fixed clock (280 fps, 50 Hz: 5.6).
                float framesPerTick = MacroEngine.PlaybackFrameRate * sim.World.FixedDeltaTime, still = 0f;
                int ticks = 0;
                sim.OnFrame = (_, stood) =>
                {
                    if (!stood || ticks >= keep) return;
                    if (++still < framesPerTick) return;
                    still -= framesPerTick;
                    path.Add(Point());
                };
                sim.Run(run, null, extraTicks: 0, onStep: _ => { if (ticks++ < keep) path.Add(Point()); });
                sim.OnFrame = null;
                return path;
            }
        }

        /// <summary>
        /// The tech a demo performs, from the Movement state on the jump tick (the in-game overlay detector's own conditions):
        /// a coyote jump is a ground jump taken with the coyote window counting (onGround held after the ground rays stopped
        /// hitting: coyoteFrames below maxCoyoteFrames), the dash boost (the superbounce) is SpecialWallJump's 730 up plus
        /// 410 up momentum at a wall touch within dashBoostMax frames of a dash, and a step-up lift is a rise in a dash with no
        /// upward speed (a pauselift when it takes more step-ups than one tick's rendered frames give). A wall jump (a wall
        /// jump spent in getPlayerControlledInput) is a wall coyote jump when the contact branch found no wall that tick
        /// (OnWall held, wallCoyoteFrames counting down from maxWallCoyoteFrames), and a stored wall jump when OnWall was off
        /// and wallCoyoteFrames still above 0 (a count a landing kept: the ground branch never counts it down). A boosted
        /// double jump is an air jump that found Velocity.y at 200 or more (<see cref="BoostedDoubleJump"/>).
        /// </summary>
        static string TechSignature(List<TickRow> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                TickRow before = rows[i - 1], after = rows[i];
                if (after.InputBoostedDoubleJump)
                    return "boosted double jump: tick " + after.Tick + " finds Velocity.y " + after.InputVy.ToString("0") + " and keeps half of it (745 + "
                        + (after.InputVy / 2f).ToString("0") + "), Velocity.y " + after.Vy.ToString("0") + " after the tick, with momentum ("
                        + after.Mx.ToString("0") + ", " + after.My.ToString("0") + ") at x " + before.X.ToString("0.#") + ", y " + before.Y.ToString("0.#");
                // A wall jump spent in getPlayerControlledInput (the contact branch's own SpecialWallJump, the superbounce off a wall,
                // zeroes wallCoyoteFrames and OnWall first), with no wall found on that tick.
                if (after.WallJumps < before.WallJumps && (after.InputOnWall ? after.InputWallCoyote < after.MaxWallCoyote : after.InputWallCoyote > 0f))
                {
                    int cling = i - 1;
                    while (cling > 0 && !(rows[cling].InputOnWall && rows[cling].InputWallCoyote >= rows[cling].MaxWallCoyote)) cling--;
                    int grounded = 0;
                    for (int k = cling + 1; k < i; k++) if (rows[k].Ground) grounded++;
                    // getPlayerControlledInput's dash boost: within dashBoostMax frames of a dash the wall jump is
                    // SpecialWallJump(0.6, 0.6, up * dashBoostUpwards): 276 sideways and 410 momentum up on top of the 730.
                    bool boost = after.My - before.My > 300f;
                    string jump = "tick " + after.Tick + " takes Velocity (" + after.Vx.ToString("0") + ", " + after.Vy.ToString("0") + ")"
                        + (boost ? " + momentum.y " + (after.My - before.My).ToString("0") + " (the dash boost, " + before.SinceDash.ToString("0") + " frames after the dash)" : "")
                        + " with wallCoyoteFrames " + after.InputWallCoyote.ToString("0") + "/" + after.MaxWallCoyote.ToString("0") + ", " + (i - cling)
                        + " ticks after the last cling tick (" + rows[cling].Tick + ")";
                    if (after.InputOnWall)
                        return "wall coyote " + (boost ? "superbounce" : "jump") + ": " + jump + ", OnWall held with the wall rays already missing at x " + before.X.ToString("0.#");
                    return "stored " + (boost ? "superbounce" : "wall jump") + ": " + jump + ", OnWall off at x " + before.X.ToString("0.#") + ", y " + before.Y.ToString("0.#")
                        + (grounded > 0 ? ", the count carried over " + grounded + " ticks on the ground" : "");
                }
                // Movement.Update's step-up (806-816): the dot rises in the dash with no upward Velocity, 56 u per rendered frame.
                // A tick has at most 6 frames at 280 fps, so more lifts than that came in frames where time stood still: a pause.
                float rise = after.Y - before.Y;
                if (rise > 40f && after.Dash && Math.Abs(after.Vy) < 1f && after.My < 1f)
                {
                    int lifts = (int)MathF.Round(rise / 56f);
                    return (lifts > 6 ? "pauselift" : "step-up lift (inwall wallbounce)") + ": tick " + after.Tick + " rises " + rise.ToString("0.#")
                        + " u (" + lifts + " step-ups of 56 u) in the dash with Velocity.y " + after.Vy.ToString("0") + " at x " + after.X.ToString("0.#");
                }
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
            using JsonDocument demo = JsonDocument.Parse(File.ReadAllText(demoPath));
            lab.UseDemoStart(demo.RootElement);
            var (sim, _, _) = Open(tracePath, lab);
            Place(sim, lab);
            Configure(sim, lab);
            Vector2 anchor = new Vector2(lab.Anchor.x, lab.Anchor.y);
            List<InputTick> inputs = demo.RootElement.GetProperty("inputs").EnumerateArray().Select(InputTick.FromJson).ToList();
            string outPath = OptS(args, "--out", null);
            var log = new StringBuilder();
            int steps = 0;
            // The lab's spikes: after each step, every one the player's hurtbox overlaps (a collider of the player's whose
            // includeLayers takes the spike's layer) or whose spikeScript counter is still up, as "spike<n> <counter>", "*" on
            // an overlap: a touch is OnTriggerEnter2D on its first tick and OnTriggerStay2D (counter + 1) after, so a spike
            // crossed inside its grace shows "*0", "*1", then nothing.
            FieldInfo spikeCounter = typeof(spikeScript).GetField("counter", Fields);
            var spikes = sim.World.All<spikeScript>().Where(k => k.name.StartsWith(lab.id + " ", StringComparison.Ordinal))
                .Select(k => (name: k.name.Substring(lab.id.Length + 1).Split(' ')[0], script: k, collider: k.GetComponent<Collider2D>())).ToArray();
            RunResult run = sim.Run(new List<InputTick>(inputs) { inputs[inputs.Count - 1] }, null, extraTicks: 400, onStep: s =>
            {
                WorldPos p = sim.PlayerWorld;
                log.Append($"{steps,3} in{(int)sim.Engine.RunTicks - 1,3} pos {p.x - anchor.x,9:0.#},{p.y - anchor.y,9:0.#}"
                    + $" V {sim.Player.Velocity.x,7:0.#},{sim.Player.Velocity.y,7:0.#} m {sim.Player.momentum.x,7:0.#},{sim.Player.momentum.y,7:0.#}"
                    + $" g{(sim.Player.onGround ? 1 : 0)} solid{(GroundCheckGrounds(sim, p) ? 1 : 0)} w{(sim.Player.OnWall ? 1 : 0)} {sim.Player.cutsceneMode}"
                    + $" dash{(sim.Player.dashActive ? 1 : 0)} f{sim.Player.framesSinceLastDash:0} R{(sim.Player.facingRight ? 1 : 0)}"
                    + $" cf{GetMovementFloat(sim, "coyoteFrames"):0}/{GetMovementFloat(sim, "maxCoyoteFrames"):0}"
                    + $" wc{GetMovementFloat(sim, "wallCoyoteFrames"):0}/{GetMovementFloat(sim, "maxWallCoyoteFrames"):0} wj{sim.Player.wallJumpsLeft}");
                foreach (var (name, script, collider) in spikes)
                {
                    int counter = (int)spikeCounter.GetValue(script);
                    bool overlap = sim.Player.GetComponents<BoxCollider2D>().Any(c => c.enabled && (c.includeLayers.value & (1 << collider.gameObject.layer)) != 0 && c.IsTouching(collider));
                    if (overlap || counter > 0) log.Append($" spike{name} {(overlap ? "*" : "")}{counter}");
                }
                log.AppendLine();
                steps++;
            });
            if (outPath != null) File.WriteAllText(outPath, log.ToString()); else Console.Write(log);
            string signature = TechSignature(Observe(sim, inputs));
            Console.WriteLine($"demo: {inputs.Count} inputs, {steps} physics ticks; "
                + (run.Finished ? "finish at tick " + steps : "no finish (" + run.EndReason + ")")
                + "; tech: " + (signature ?? "none"));
            // --write-path: (re)write the demo's ghost path, as `lab solve` writes it, after the lab moved or the demo was solved
            // before demos carried one: to the finish, then the tail. --path-ticks N: the demo's own first N physics ticks and no
            // tail, finish or not, for a lab's `ghosts` (the ghosts compared side by side all play their own inputs over the
            // same ticks, a comparison demo that misses the finish on purpose included).
            int pathTicks = Opt(args, "--path-ticks", 0);
            if (args.Contains("--write-path") && (run.Finished || pathTicks > 0))
            {
                var file = JsonNode.Parse(File.ReadAllText(demoPath))!.AsObject();
                file["path"] = JsonSerializer.SerializeToNode(pathTicks > 0 ? GhostPath(tracePath, lab, inputs, pathTicks, 0) : GhostPath(tracePath, lab, inputs, steps));
                File.WriteAllText(demoPath, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("wrote the ghost path to " + demoPath + (pathTicks > 0 ? " (its first " + pathTicks + " physics ticks)" : ""));
            }
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
            DemoStart.ApplyLabStart(sim, lab);
        }

        /// <summary>The dedupe key of a state: the world pose, velocity and body state quantized, every Movement field, and the
        /// macro engine's pending input state (a held jump, a dash-jump frame, a lock, the pending Invokes' timers). Exact: the
        /// pose (world and scene frame), the scale, the Box2D body and the interpolation's ends as float bits too, the player's
        /// colliders, and every script the run has touched (a spike's grace counter) by instance id and field. Not in it: toggled
        /// objects (a dash's block swap), coroutines, the RNG and Box2D's warm-start cache.</summary>
        static string KeyOf(Simulation sim, bool exact)
        {
            var sb = new StringBuilder(512);
            WorldPos p = sim.PlayerWorld;
            Quantize(sb, p.x, 1f); Quantize(sb, p.y, 1f);
            Quantize(sb, sim.Player.transform.localScale.x, 0.01f);
            var body = sim.Physics.PlayerBody();
            Quantize(sb, body.px, 1f); Quantize(sb, body.py, 1f);
            Quantize(sb, body.vx, 10f); Quantize(sb, body.vy, 10f); Quantize(sb, body.angle, 0.1f);
            if (exact)
            {
                Vector3 scene = sim.Player.transform.position, scale = sim.Player.transform.localScale;
                foreach (float f in new[] { p.x, p.y, scene.x, scene.y, scene.z, scale.x, scale.y, body.px, body.py, body.angle, body.vx, body.vy, body.w })
                    sb.Append(BitConverter.SingleToInt32Bits(f)).Append(',');
                foreach (FieldInfo f in StepEnds) { Vector3 v = (Vector3)f.GetValue(sim); sb.Append(BitConverter.SingleToInt32Bits(v.x)).Append(',').Append(BitConverter.SingleToInt32Bits(v.y)).Append(','); }
                foreach (BoxCollider2D c in sim.Player.GetComponents<BoxCollider2D>())
                    AddValue(sb, new object[] { c.offset, c.size, c.excludeLayers.value, c.enabled }, true);
                foreach (MonoBehaviour script in (HashSet<MonoBehaviour>)TouchedField.GetValue(sim))
                {
                    sb.Append(script.GetInstanceID()).Append(':');
                    foreach (FieldInfo f in FieldState.For(script.GetType())) AddValue(sb, f.GetValue(script), true);
                }
                sb.Append('|');
            }
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
        /// recentlyJumped blocks it). False = the ground rays have stopped hitting: the coyote window's own state. The rays
        /// are cast in the scene frame, as the game casts them (the world point plus FloatingOrigin.currentOrigin).
        /// </summary>
        static bool GroundCheckGrounds(Simulation sim, WorldPos at)
        {
            if (GetMovementBool(sim, "recentlyJumped")) return false;
            ScenePos s = sim.ToScene(at);
            float x = s.x, y = s.y;
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
        /// (momentum assumed not to decay) plus every jump and boost it has or a landing gives back. A lab map has no moving
        /// geometry, and a lab with a spring skips this prune (a spring's launch isn't in it), so one ground or coyote jump
        /// (jumpForce 805 + the apex's anti-grav under 400 u: any state may still land under its arc and jump), the air jumps
        /// (745), each wall jump (730 + a bounce's 220 momentum under 400 u, a stored wall coyote jump among them) and one
        /// dash (a super's 805 or an omnidash's ground bounce 1405 under 900 u) cover everything the game can give; a landing
        /// refills to the max counts. The negative control only presses jump on solid ground: one ground jump and the dash.
        /// </summary>
        static float MaxReach(Simulation sim, bool control = false)
        {
            Vector2 v = (Vector2)VelocityField.GetValue(sim.Player), m = (Vector2)MomentumField.GetValue(sim.Player);
            float up = MathF.Max(0f, v.y + m.y);
            float gain = up * up / 2700f + 50f + 400f;
            float Most(string left, string max) => MathF.Max(0f, MathF.Max(GetMovementFloat(sim, left), GetMovementFloat(sim, max)));
            if (GetMovementBool(sim, "dashUnlocked") && Most("airDashesLeft", "maxAirDashes") > 0f) gain += 900f;
            if (control) return sim.PlayerWorld.y + gain;
            if (GetMovementBool(sim, "doubleJumpUnlocked")) gain += 400f * Most("airJumpsLeft", "maxAirJumps");
            if (GetMovementBool(sim, "wallJumpUnlocked")) gain += 400f * MathF.Min(4f, Most("wallJumpsLeft", "maxWallJumps"));
            return sim.PlayerWorld.y + gain;
        }

        static readonly FieldInfo[] StepEnds = { typeof(Simulation).GetField("_stepFrom", Fields), typeof(Simulation).GetField("_stepTo", Fields) };
        static readonly FieldInfo TouchedField = typeof(Simulation).GetField("_touched", Fields);
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
        static JsonNode Trim(float f) { return (JsonValue)(double)Math.Round(f, 3); }
        static int Opt(string[] args, string name, int fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback; }
        static string OptS(string[] args, string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    }
}
