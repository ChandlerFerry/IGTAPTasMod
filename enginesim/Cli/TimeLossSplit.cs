using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// <c>timeloss split &lt;trace.json&gt; &lt;route.json&gt; --ref &lt;route.json | captured trace.json&gt; [--within U] [--json]</c>: where a
    /// route loses time to a reference run of the same target, anchor by anchor. Both are cut at their anchors
    /// (<see cref="Difficulty.Anchors(Simulation, List{InputTick}, SimSnapshot, bool)"/>, the route's own inputs): the route
    /// replayed, the reference too unless it is a captured trace (a turbo capture's per-tick "ticks"), whose anchors come from
    /// the states it captured, as it played then; what its inputs do on this engine is said beside it (routes judged before
    /// a frame-grid change may play differently now). Each route anchor takes the reference's nearest anchor of its kind
    /// within --within u (default <see cref="Within"/>) after the last one taken; the starts match, and the ends when of one
    /// kind. Per anchor: both runs' physics ticks and their difference, and the difference in ticks the timer has counted (a
    /// course's starts where the player enters its start, not at the route's start; the table's timer row). The losing
    /// segment is the first between matched anchors where that timed difference grows, with both players' speeds at its ends.
    /// </summary>
    static class TimeLossSplit
    {
        const string Usage = "usage: timeloss split <trace.json> <route.json> --ref <route.json | captured trace.json> [--within U] [--json]";
        /// <summary>The farthest (u) a reference anchor may be from the route's to match it.</summary>
        const float Within = 100f;
        /// <summary>A step that moves the player this much (u) farther than its velocities carry it is a respawn's teleport.</summary>
        const float Teleport = 32f;

        /// <summary>One route segment, anchor <see cref="From"/> to the next (<see cref="To"/>, input ticks), against the reference
        /// between the anchors matched to its ends (null where either end has none): physics ticks, and the ticks of them the timer
        /// counted (null without a timer start), whose differences add up to the difference in time.</summary>
        internal sealed class Row
        {
            public int From, To;
            public string Kind, Next;
            public long Ticks;
            public long? RefTicks, Delta;
            public long? Timed, RefTimed, TimedDelta;
        }

        /// <summary>A run cut at its anchors: where its timer started (physics ticks; null: it never did) and the player then.</summary>
        sealed class Side
        {
            public string Name;
            /// <summary>Read from a captured trace's states, not replayed.</summary>
            public bool Captured;
            public bool Finished;
            /// <summary>Why an unfinished replay stopped (RunResult.EndReason).</summary>
            public string EndReason;
            public float Time;
            public long Physics;
            public long? Timer;
            public MoveState TimerMove;
            public List<Difficulty.Anchor> Anchors;
            /// <summary>A captured trace's inputs replayed on this engine.</summary>
            public RunResult Today;

            /// <summary>The ticks the timer has counted after <paramref name="physics"/> physics ticks: none before its start, so
            /// a course time is its finish's.</summary>
            public long? Timed(long physics) => Timer == null ? null : Math.Max(0, physics - Timer.Value);
        }

        sealed class Result
        {
            public Side Route, Ref;
            public float Within;
            /// <summary>The reference anchor matched to each route anchor (-1: none).</summary>
            public int[] Match;
            public List<Row> Rows;
            /// <summary>The matched route anchors the losing segment runs between (-1: the route loses nowhere).</summary>
            public int LoseFrom = -1, LoseTo = -1;
        }

        internal static int Run(string[] args)
        {
            string refPath = Solve.Option(args, "--ref", (string)null);
            if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) || args[1].StartsWith("--", StringComparison.Ordinal) || refPath == null)
                throw new ArgumentException(Usage);
            Result r = Compare(args[0], args[1], refPath, Solve.Option(args, "--within", Within));
            Console.WriteLine(args.Contains("--json") ? JsonSerializer.Serialize(Json(r)) : Table(r));
            return 0;
        }

        /// <summary>The route's segments against the reference (<see cref="Row"/>), for the report.</summary>
        internal static List<Row> Rows(string tracePath, string routePath, string refPath) => Compare(tracePath, routePath, refPath, Within).Rows;

        static Result Compare(string tracePath, string routePath, string refPath, float within)
        {
            var (header, labels, inputs) = TechsUsed.Load(tracePath, routePath, "timeloss");
            // One simulation for both runs: the game's statics are per thread, so a second one here would take them over.
            Simulation sim = TechsUsed.Create(tracePath, header, labels);
            var r = new Result { Route = Replay(sim, Path.GetFileName(routePath), inputs), Within = within };
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(refPath), new JsonDocumentOptions { MaxDepth = 256 }))
            {
                // A route file's "ticks" are its inputs (Plan.ReadRoute); a capture's are its states, beside its "inputs".
                List<InputTick> refInputs = Solve.ReadInputs(doc.RootElement);
                string name = Path.GetFileName(refPath);
                if (!IsCapture(doc.RootElement)) r.Ref = Replay(sim, name, refInputs);
                else
                {
                    r.Ref = Captured(doc.RootElement, name);
                    r.Ref.Today = sim.Run(refInputs, null, null, sim.Goals != null ? 1000000 : 400);
                }
            }
            r.Match = Align(r.Route.Anchors, r.Ref.Anchors, within);
            r.Rows = Segments(r);
            Losing(r);
            return r;
        }

        /// <summary>The run replayed: its anchors, and its timer's last start from the tech record's steps (a run timed from its
        /// start: 0).</summary>
        static Side Replay(Simulation sim, string name, List<InputTick> inputs)
        {
            List<Difficulty.Anchor> anchors = Difficulty.Anchors(sim, inputs, null, false, out TechSteps record, out RunResult run, out int[] goals);
            var side = new Side
            {
                Name = name, Anchors = anchors, Physics = anchors[anchors.Count - 1].Physics,
                Finished = sim.Goals != null ? goals.Length == sim.Goals.Limit : run.Finished, EndReason = run.EndReason,
                Time = sim.Goals == null ? run.Time : goals.Length == 0 ? 0f : MacroEngine.LiveSeconds(run.GoalPhysicsTicks[goals.Length - 1], run.GoalHeldFrames[goals.Length - 1]),
            };
            if (sim.TimedFromRunStart)
            {
                side.Timer = 0;
                side.TimerMove = anchors[0].Move;
            }
            else TimerStart(record.Steps, side);
            return side;
        }

        /// <summary>The last step the timer started on (it restarts at each crossing of the start).</summary>
        static void TimerStart(List<TechStep> steps, Side side)
        {
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].Timing && (i == 0 || !steps[i - 1].Timing))
                {
                    side.Timer = steps[i].Physics;
                    side.TimerMove = steps[i].After;
                }
        }

        /// <summary>A turbo capture (tools/enginesim-capture.py): a start state and per-tick states, not just inputs.</summary>
        static bool IsCapture(JsonElement root)
        {
            return root.TryGetProperty("start", out JsonElement start) && start.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ticks", out JsonElement ticks) && ticks.ValueKind == JsonValueKind.Array
                && ticks.GetArrayLength() > 0 && ticks[0].TryGetProperty("position", out _);
        }

        /// <summary>A captured trace's run as it played: its steps rebuilt from the per-tick states (input and physics ticks, the
        /// player in the world frame, the course's timer, a teleport as a respawn) and cut at their anchors as a replay's are; an
        /// anchor's player is the state after the first step its input tick is next on.</summary>
        static Side Captured(JsonElement root, string name)
        {
            int course = root.TryGetProperty("course", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            float dt = root.GetProperty("fixedDeltaTime").GetSingle();
            MoveState start = State(root.GetProperty("start")), before = start;
            var steps = new List<TechStep>();
            foreach (JsonElement t in root.GetProperty("ticks").EnumerateArray())
            {
                var st = new TechStep { Index = steps.Count, After = State(t), Timing = course > 0 && Tracking(t, course) };
                foreach (JsonElement f in t.GetProperty("engine").EnumerateArray())
                {
                    string field = f[1].GetString();
                    if (field == "_runTicks") st.Tick = f[2].GetInt32();
                    else if (field == "_physicsTicks") st.Physics = f[2].GetInt64();
                }
                float dx = st.After.X - before.X, dy = st.After.Y - before.Y;
                float carry = (MathF.Sqrt(before.Vx * before.Vx + before.Vy * before.Vy) + MathF.Sqrt(st.After.Vx * st.After.Vx + st.After.Vy * st.After.Vy)) * dt;
                if (MathF.Sqrt(dx * dx + dy * dy) > carry + Teleport) st.Calls.Add(new TechEvent { Call = TechCall.Respawn, State = st.After });
                steps.Add(st);
                before = st.After;
            }
            JsonElement result = root.GetProperty("result");
            bool finished = result.GetProperty("finished").GetBoolean() && steps.Count > 0;
            List<Difficulty.Anchor> anchors = Difficulty.Anchors(steps, finished ? new[] { steps[steps.Count - 1].Tick } : Array.Empty<int>(), 0, false);
            for (int i = 0; i < anchors.Count; i++)
            {
                TechStep at = i == 0 ? null : steps.FirstOrDefault(st => st.Tick >= anchors[i].Tick) ?? steps[steps.Count - 1];
                anchors[i] = new Difficulty.Anchor { Kind = anchors[i].Kind, Tick = anchors[i].Tick, Physics = at?.Physics ?? 0, Move = at?.After ?? start };
            }
            var side = new Side
            {
                Name = name, Captured = true, Anchors = anchors, Finished = finished, Physics = anchors[anchors.Count - 1].Physics,
                Time = finished ? result.GetProperty("time").GetSingle() : 0f,
            };
            if (course <= 0)
            {
                side.Timer = 0;
                side.TimerMove = start;
            }
            else TimerStart(steps, side);
            return side;
        }

        /// <summary>The player of a captured state as the anchors read it: the world frame (the scene position less
        /// FloatingOrigin's), Movement.Velocity, its contacts, dash and resources.</summary>
        static MoveState State(JsonElement t)
        {
            JsonElement p = t.GetProperty("position");
            float ox = 0f, oy = 0f;
            if (t.TryGetProperty("origin", out JsonElement o) && o.ValueKind == JsonValueKind.Object)
            {
                ox = o.GetProperty("x").GetSingle();
                oy = o.GetProperty("y").GetSingle();
            }
            var s = new MoveState { X = p.GetProperty("x").GetSingle() - ox, Y = p.GetProperty("y").GetSingle() - oy };
            foreach (JsonElement f in t.GetProperty("movement").EnumerateArray())
            {
                JsonElement v = f[2];
                switch (f[1].GetString())
                {
                    case "Velocity": s.Vx = v.GetProperty("x").GetSingle(); s.Vy = v.GetProperty("y").GetSingle(); break;
                    case "onGround": s.Ground = v.GetBoolean(); break;
                    case "OnWall": s.Wall = v.GetBoolean(); break;
                    case "dashActive": s.Dash = v.GetBoolean(); break;
                    case "isDead": s.Dead = v.GetBoolean(); break;
                    case "framesSinceLastDash": s.SinceDash = v.GetSingle(); break;
                    case "airJumpsLeft": s.AirJumps = v.GetInt32(); break;
                    case "wallJumpsLeft": s.WallJumps = v.GetInt32(); break;
                    case "airDashesLeft": s.AirDashes = v.GetInt32(); break;
                }
            }
            return s;
        }

        /// <summary>The course's timer runs in this captured state (courseScript.tracking).</summary>
        static bool Tracking(JsonElement t, int course)
        {
            foreach (JsonElement c in t.GetProperty("courses").EnumerateArray())
            {
                bool tracking = false, it = false;
                foreach (JsonElement f in c.GetProperty("fields").EnumerateArray())
                {
                    string name = f[1].GetString();
                    if (name == "tracking") tracking = f[2].GetBoolean();
                    else if (name == "courseNumber") it = f[2].GetInt32() == course;
                }
                if (it) return tracking;
            }
            return false;
        }

        /// <summary>In order: each route anchor takes the nearest reference anchor of its kind within <paramref name="within"/> u
        /// after the last one taken; the starts match, and the ends when of one kind.</summary>
        static int[] Align(List<Difficulty.Anchor> route, List<Difficulty.Anchor> reference, float within)
        {
            var match = new int[route.Count];
            int last = 0, end = reference.Count - 1;
            for (int i = 1; i < route.Count; i++)
            {
                Difficulty.Anchor a = route[i];
                match[i] = -1;
                if (i == route.Count - 1)
                {
                    if (end > last && reference[end].Kind == a.Kind) match[i] = end;
                    continue;
                }
                float best = within * within;
                for (int j = last + 1; j < end; j++)
                {
                    if (reference[j].Kind != a.Kind) continue;
                    float dx = reference[j].Move.X - a.Move.X, dy = reference[j].Move.Y - a.Move.Y, d = dx * dx + dy * dy;
                    if (d > best) continue;
                    best = d;
                    match[i] = j;
                }
                if (match[i] >= 0) last = match[i];
            }
            return match;
        }

        static List<Row> Segments(Result r)
        {
            var rows = new List<Row>();
            List<Difficulty.Anchor> a = r.Route.Anchors, b = r.Ref.Anchors;
            for (int i = 0; i + 1 < a.Count; i++)
            {
                var row = new Row
                {
                    From = a[i].Tick, To = a[i + 1].Tick, Kind = a[i].Kind, Next = a[i + 1].Kind, Ticks = a[i + 1].Physics - a[i].Physics,
                    Timed = r.Route.Timed(a[i + 1].Physics) - r.Route.Timed(a[i].Physics),
                };
                int j = r.Match[i], k = r.Match[i + 1];
                if (j >= 0 && k >= 0)
                {
                    row.RefTicks = b[k].Physics - b[j].Physics;
                    row.Delta = row.Ticks - row.RefTicks;
                    row.RefTimed = r.Ref.Timed(b[k].Physics) - r.Ref.Timed(b[j].Physics);
                    row.TimedDelta = row.Timed - row.RefTimed;
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>The route's difference at a matched anchor: in timed ticks when both runs have a timer, else in physics ticks.</summary>
        static long Difference(Result r, int i)
        {
            Difficulty.Anchor a = r.Route.Anchors[i], b = r.Ref.Anchors[r.Match[i]];
            long? timed = r.Route.Timed(a.Physics) - r.Ref.Timed(b.Physics);
            return timed ?? a.Physics - b.Physics;
        }

        /// <summary>The first pair of consecutive matched anchors the difference grows between.</summary>
        static void Losing(Result r)
        {
            int prev = 0;
            for (int i = 1; i < r.Match.Length; i++)
            {
                if (r.Match[i] < 0) continue;
                if (Difference(r, i) > Difference(r, prev))
                {
                    r.LoseFrom = prev;
                    r.LoseTo = i;
                    return;
                }
                prev = i;
            }
        }

        static float Speed(MoveState m) => MathF.Sqrt(m.Vx * m.Vx + m.Vy * m.Vy);

        static string Player(MoveState m) => $"({m.X:0.0}, {m.Y:0.0}) v ({m.Vx:0}, {m.Vy:0}) |v| {Speed(m):0}";

        static string Head(Side s)
        {
            string run = (s.Finished ? $"finished, {s.Time:0.00} s" : $"not finished ({s.EndReason ?? "its last state"})") + $", {s.Physics} physics ticks, timer from "
                + (s.Timer == null ? "- (never started)" : $"physics {s.Timer} at {Player(s.TimerMove)}");
            if (!s.Captured) return $"{s.Name} (replayed): {run}";
            RunResult t = s.Today;
            return $"{s.Name} (captured states): {run}\n  its inputs on this engine: "
                + (t.Finished ? $"finished, {t.Time:0.00} s, {t.PhysicsTicks} physics ticks" : $"not finished ({t.EndReason})");
        }

        static string Signed(long? v) => v == null ? "-" : v > 0 ? "+" + v : v.ToString();

        static string Table(Result r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("route " + Head(r.Route));
            sb.AppendLine("ref   " + Head(r.Ref));
            sb.AppendLine($"anchors matched by kind within {r.Within:0.#} u; delta: route - ref physics ticks; timed: route - ref ticks counted by the timer"
                + " (the timer row: where each run's timer starts, no anchor)");
            sb.AppendLine("   #  kind     x        y         route  ref#  ref    delta  timed  |v| route  |v| ref");
            List<Difficulty.Anchor> a = r.Route.Anchors, b = r.Ref.Anchors;
            for (int i = 0; i < a.Count; i++)
            {
                if (i > 0 && r.Route.Timer > a[i - 1].Physics && r.Route.Timer <= a[i].Physics && r.Ref.Timer != null)
                {
                    MoveState t = r.Route.TimerMove;
                    sb.AppendLine($"      {"timer",-7} {t.X,8:0.0} {t.Y,8:0.0}  {r.Route.Timer,5}  {"-",4}  {r.Ref.Timer,-5}  {Signed(r.Route.Timer - r.Ref.Timer),5}  "
                        + $"{Signed(0),5}  {Speed(t),9:0}  {Speed(r.Ref.TimerMove):0}");
                }
                int j = r.Match[i];
                Difficulty.Anchor x = a[i];
                string refCols = j < 0 ? $"{"-",4}  {"-",-5}  {"-",5}  {"-",5}  {"-",9}  -"
                    : $"{j,4}  {b[j].Physics,-5}  {Signed(x.Physics - b[j].Physics),5}  {Signed(r.Route.Timed(x.Physics) - r.Ref.Timed(b[j].Physics)),5}  {Speed(x.Move),9:0}  {Speed(b[j].Move):0}";
                sb.AppendLine($"{i,4}{(i == r.LoseTo ? '*' : ' ')} {x.Kind,-7} {x.Move.X,8:0.0} {x.Move.Y,8:0.0}  {x.Physics,5}  {refCols}");
            }
            int[] unmatched = Enumerable.Range(1, b.Count - 1).Where(j => !r.Match.Contains(j)).ToArray();
            sb.AppendLine("ref anchors unmatched: " + (unmatched.Length == 0 ? "none"
                : string.Join(", ", unmatched.Select(j => $"#{j} {b[j].Kind} at physics {b[j].Physics} ({b[j].Move.X:0.0}, {b[j].Move.Y:0.0})"))));
            if (r.LoseTo < 0)
            {
                sb.Append("losing segment: none (the route's difference never grows between matched anchors)");
                return sb.ToString();
            }
            Difficulty.Anchor from = a[r.LoseFrom], to = a[r.LoseTo], refFrom = b[r.Match[r.LoseFrom]], refTo = b[r.Match[r.LoseTo]];
            long grow = Difference(r, r.LoseTo) - Difference(r, r.LoseFrom);
            bool timed = r.Route.Timer != null && r.Ref.Timer != null;
            sb.AppendLine($"losing segment: #{r.LoseFrom} {from.Kind} -> #{r.LoseTo} {to.Kind} (route physics {from.Physics}-{to.Physics}, ref {refFrom.Physics}-{refTo.Physics}): "
                + $"{Signed(grow)} {(timed ? "timed" : "physics")} ticks");
            sb.AppendLine($"  at #{r.LoseFrom} {from.Kind}: route {Player(from.Move)}; ref {Player(refFrom.Move)}");
            if (timed && r.Route.Timer > from.Physics && r.Route.Timer <= to.Physics)
                sb.AppendLine($"  timer start: route physics {r.Route.Timer} {Player(r.Route.TimerMove)}; ref physics {r.Ref.Timer} {Player(r.Ref.TimerMove)}");
            sb.Append($"  at #{r.LoseTo} {to.Kind}: route {Player(to.Move)}; ref {Player(refTo.Move)}");
            return sb.ToString();
        }

        static object Point(Difficulty.Anchor x) => new { kind = x.Kind, tick = x.Tick, physics = x.Physics, x = x.Move.X, y = x.Move.Y, vx = x.Move.Vx, vy = x.Move.Vy };

        static object Json(Side s) => new
        {
            name = s.Name, captured = s.Captured, finished = s.Finished, endReason = s.EndReason, time = s.Time, physics = s.Physics, timer = s.Timer,
            timerAt = s.Timer == null ? null : new { x = s.TimerMove.X, y = s.TimerMove.Y, vx = s.TimerMove.Vx, vy = s.TimerMove.Vy },
            today = s.Today == null ? null : new { finished = s.Today.Finished, time = s.Today.Time, physics = s.Today.PhysicsTicks, reason = s.Today.EndReason },
            anchors = s.Anchors.Select(Point),
        };

        static object Json(Result r) => new
        {
            route = Json(r.Route), @ref = Json(r.Ref), within = r.Within,
            anchors = r.Route.Anchors.Select((x, i) => new
            {
                index = i, kind = x.Kind, tick = x.Tick, physics = x.Physics, @ref = r.Match[i] < 0 ? (int?)null : r.Match[i],
                refPhysics = r.Match[i] < 0 ? (long?)null : r.Ref.Anchors[r.Match[i]].Physics,
                delta = r.Match[i] < 0 ? (long?)null : x.Physics - r.Ref.Anchors[r.Match[i]].Physics,
                timed = r.Match[i] < 0 ? null : r.Route.Timed(x.Physics) - r.Ref.Timed(r.Ref.Anchors[r.Match[i]].Physics),
            }),
            segments = r.Rows.Select(x => new
            {
                from = x.From, to = x.To, kind = x.Kind, next = x.Next, ticks = x.Ticks, refTicks = x.RefTicks, delta = x.Delta,
                timed = x.Timed, refTimed = x.RefTimed, timedDelta = x.TimedDelta,
            }),
            losing = r.LoseTo < 0 ? null : new { from = r.LoseFrom, to = r.LoseTo, grow = Difference(r, r.LoseTo) - Difference(r, r.LoseFrom) },
        };
    }
}
