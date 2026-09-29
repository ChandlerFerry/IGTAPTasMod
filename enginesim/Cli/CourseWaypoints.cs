using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using FullTasTracker = IGTAPTasMod.FullTasTracker;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>How <see cref="CourseWaypoints.For"/> places a course's waypoints.</summary>
    sealed class WaypointOptions
    {
        /// <summary>Goal-to-goal stretches per course (--waypoints N): N - 1 area waypoints spread over the record, whatever its
        /// length. 1 or less: none (only the checkpoints, via boxes and the finish).</summary>
        public int Stretches = 6;
        /// <summary>Physics ticks per stretch instead (--waypoint-ticks T, a density; 0: <see cref="Stretches"/> decides).</summary>
        public int TicksPer;
        /// <summary>The floor: never two waypoints (or a waypoint and the start, the timer's start or the finish) closer than
        /// this many physics ticks along the record (--waypoint-min-ticks).</summary>
        public int MinTicks = 16;
        /// <summary>The course's own checkpoints (checkpointScript triggers) the record touches (--no-checkpoint-waypoints).</summary>
        public bool Checkpoints = true;
        /// <summary>The course's forced-path "via" boxes (forced-paths.json), in route order (--no-via-waypoints).</summary>
        public bool Via = true;
        /// <summary>Record timing: a start:&lt;course&gt; goal at the record's last start-gate crossing (where the game timer starts),
        /// after every waypoint touched before it. Farm timing (--farm): none.</summary>
        public bool Record = true;
        /// <summary>Keeps every Kth waypoint in route order, checkpoints and automatic ones alike (the 1st, K+1th, ...; via boxes
        /// always stay; --waypoint-stride K): the ablation that checks the waypoints don't force a slower line.</summary>
        public int Stride = 1;
        /// <summary>How far an area waypoint reaches across the record's motion each way, at most (--waypoint-reach), up to the
        /// first solid the ray from the player meets: alternate lines through the same corridor count, the next corridor doesn't.</summary>
        public float Reach = 360f;
        public Action<string> Log;

        public static WaypointOptions Parse(string[] args, Action<string> log = null)
        {
            return new WaypointOptions
            {
                Stretches = Solve.Option(args, "--waypoints", 6),
                TicksPer = Solve.Option(args, "--waypoint-ticks", 0),
                MinTicks = Math.Max(1, Solve.Option(args, "--waypoint-min-ticks", 16)),
                Checkpoints = !args.Contains("--no-checkpoint-waypoints"),
                Via = !args.Contains("--no-via-waypoints"),
                Record = !args.Contains("--farm"),
                Stride = Math.Max(1, Solve.Option(args, "--waypoint-stride", 1)),
                Reach = Solve.Option(args, "--waypoint-reach", 360f),
                Log = log,
            };
        }
    }

    /// <summary>
    /// A course as a multi-goal problem: area goals (at:) along the record's path, then the finish, so the Full-TAS --auto parts
    /// search it goal to goal. The waypoints are checkpoints, not cut points: a run carries its momentum through them. Placed
    /// where the record changes phase (a landing after a long airtime, a wall contact, a reversal, a spring or a zip's hand-off,
    /// a dash) in preference to even spacing, about <see cref="WaypointOptions.Stretches"/> stretches per course with a floor of
    /// <see cref="WaypointOptions.MinTicks"/> ticks between waypoints; the course's own checkpoints and via boxes always. Each
    /// area waypoint is a band across the record's motion: thick enough that no tick can step over it, as wide as the corridor
    /// (to the first solid, <see cref="WaypointOptions.Reach"/> at most). A band the record's path also enters at another time (a
    /// course that comes back past it) gets from= sides that only the intended entry has. Rects are in the capture frame (what
    /// the offline goal check reads). The record is replayed under the goals before they are returned: a waypoint it doesn't
    /// reach in order is dropped.
    /// </summary>
    static class CourseWaypoints
    {
        public static List<FullTasGoal> For(string trace, IReadOnlyList<InputTick> route, WaypointOptions o)
        {
            return Build(trace, route, o).Goals;
        }

        /// <summary>One waypoint: its goal, why it is there and the record's tick at it (the tick it was placed at).</summary>
        public sealed class Waypoint
        {
            public FullTasGoal Goal;
            public string Why;
            public int Tick;
            public bool Forced;
        }

        public sealed class Result
        {
            public List<FullTasGoal> Goals;
            public List<Waypoint> Waypoints;
            /// <summary>The record's physics tick at each goal under the returned goals (-1: missed), and its finish tick.</summary>
            public long[] Reached;
            public int FinishTick, GateTick;
            public List<string> Notes = new List<string>();
        }

        // ---- the record's path ----

        sealed class Path
        {
            public int N;
            public float[] X, Y, X0, X1, Y0, Y1, VX, VY, Up, Down, Left, Right;
            public bool[] Ground, Wall, Dash, Spring, Timing, Respawned;
            public int[] TimingCourse;
            public long[] Physics;
            public bool Finished;
            public float Time;
        }

        static Path Replay(Simulation sim, IReadOnlyList<InputTick> route, float reach)
        {
            var x = new List<float>(); var y = new List<float>();
            var x0 = new List<float>(); var x1 = new List<float>(); var y0 = new List<float>(); var y1 = new List<float>();
            var vx = new List<float>(); var vy = new List<float>();
            var up = new List<float>(); var down = new List<float>(); var left = new List<float>(); var right = new List<float>();
            var ground = new List<bool>(); var wall = new List<bool>(); var dash = new List<bool>(); var spring = new List<bool>();
            var timing = new List<bool>(); var respawned = new List<bool>(); var course = new List<int>(); var physics = new List<long>();
            BoxCollider2D[] boxes = sim.Player.GetComponents<BoxCollider2D>();
            int layer = ContactRays.GroundLayer(sim.Player);
            Vector2 respawn = sim.Player.respawnPoint;
            float cast = reach + 60f;
            float Ray(Vector2 from, Vector2 dir)
            {
                RaycastHit2D hit = Physics2D.Raycast(from, dir, cast, layer);
                return hit ? hit.distance : cast;
            }
            RunResult r = sim.Run(route, null, null, 400, _ =>
            {
                Movement m = sim.Player;
                Vector3 p = m.transform.position;
                CapturePos c = sim.ToCapture(new ScenePos(p.x, p.y));
                float dx = c.x - p.x, dy = c.y - p.y;
                float bx0 = float.PositiveInfinity, by0 = float.PositiveInfinity, bx1 = float.NegativeInfinity, by1 = float.NegativeInfinity;
                foreach (BoxCollider2D box in boxes)
                {
                    if (!box.enabled) continue;
                    Vector3 scale = box.transform.lossyScale;
                    float cx = p.x + box.offset.x * scale.x, cy = p.y + box.offset.y * scale.y;
                    float hx = box.size.x * Math.Abs(scale.x) / 2f, hy = box.size.y * Math.Abs(scale.y) / 2f;
                    bx0 = Math.Min(bx0, cx - hx); bx1 = Math.Max(bx1, cx + hx);
                    by0 = Math.Min(by0, cy - hy); by1 = Math.Max(by1, cy + hy);
                }
                x.Add(c.x); y.Add(c.y);
                x0.Add(bx0 + dx); x1.Add(bx1 + dx); y0.Add(by0 + dy); y1.Add(by1 + dy);
                vx.Add(m.Velocity.x + m.momentum.x); vy.Add(m.Velocity.y + m.momentum.y);
                var at = new Vector2(p.x, p.y);
                up.Add(Ray(at, Vector2.up)); down.Add(Ray(at, Vector2.down)); left.Add(Ray(at, Vector2.left)); right.Add(Ray(at, Vector2.right));
                ground.Add(m.onGround); wall.Add(m.OnWall); dash.Add(m.dashActive);
                spring.Add((sim.Events & StepEvents.SpringHit) != 0);
                courseScript t = sim.TimingCourse();
                timing.Add(t != null); course.Add(t?.courseNumber ?? 0);
                respawned.Add(m.respawnPoint != respawn);
                respawn = m.respawnPoint;
                physics.Add(sim.Engine.PhysicsTicks);
            });
            return new Path
            {
                N = x.Count, X = x.ToArray(), Y = y.ToArray(), X0 = x0.ToArray(), X1 = x1.ToArray(), Y0 = y0.ToArray(), Y1 = y1.ToArray(),
                VX = vx.ToArray(), VY = vy.ToArray(), Up = up.ToArray(), Down = down.ToArray(), Left = left.ToArray(), Right = right.ToArray(),
                Ground = ground.ToArray(), Wall = wall.ToArray(), Dash = dash.ToArray(), Spring = spring.ToArray(), Timing = timing.ToArray(),
                Respawned = respawned.ToArray(), TimingCourse = course.ToArray(), Physics = physics.ToArray(), Finished = r.Finished, Time = r.Time,
            };
        }

        // ---- phase changes ----

        /// <summary>A place the record changes phase: a waypoint prefers these to even spacing, the higher priority first.</summary>
        readonly struct Phase
        {
            public readonly int Tick, Priority;
            public readonly string Kind;
            public Phase(int tick, int priority, string kind) { Tick = tick; Priority = priority; Kind = kind; }
        }

        const int LongAir = 15, Smooth = 5;
        const float HandOff = 600f;

        static List<Phase> Phases(Path p)
        {
            var list = new List<Phase>();
            int air = 0, lastReversal = -100;
            for (int i = 1; i < p.N; i++)
            {
                air = p.Ground[i - 1] ? 0 : air + 1;
                bool land = p.Ground[i] && !p.Ground[i - 1], wall = p.Wall[i] && !p.Wall[i - 1], dash = p.Dash[i] && !p.Dash[i - 1];
                if (p.Spring[i]) list.Add(new Phase(i, 4, "spring"));
                if (land) list.Add(new Phase(i, air >= LongAir ? 3 : 1, air >= LongAir ? "landing after " + air + " ticks in the air" : "landing"));
                if (wall) list.Add(new Phase(i, 3, "wall contact"));
                if (dash) list.Add(new Phase(i, 2, "dash"));
                float jump = (float)Math.Sqrt((p.VX[i] - p.VX[i - 1]) * (p.VX[i] - p.VX[i - 1]) + (p.VY[i] - p.VY[i - 1]) * (p.VY[i] - p.VY[i - 1]));
                if (jump > HandOff && !land && !wall && !dash && !p.Spring[i]) list.Add(new Phase(i, 2, "hand-off"));
                // A reversal: the path Smooth ticks on turns back on the path Smooth ticks before (both real moves).
                if (i >= Smooth && i + Smooth < p.N && i - lastReversal > Smooth)
                {
                    float ax = p.X[i] - p.X[i - Smooth], ay = p.Y[i] - p.Y[i - Smooth], bx = p.X[i + Smooth] - p.X[i], by = p.Y[i + Smooth] - p.Y[i];
                    float la = (float)Math.Sqrt(ax * ax + ay * ay), lb = (float)Math.Sqrt(bx * bx + by * by);
                    if (la > 20f && lb > 20f && ax * bx + ay * by < -.3f * la * lb)
                    {
                        list.Add(new Phase(i, 3, "reversal"));
                        lastReversal = i;
                    }
                }
            }
            return list;
        }

        // ---- rects ----

        struct Rect
        {
            public float X0, X1, Y0, Y1;
            public int From;
            public float W => X1 - X0;
            public float H => Y1 - Y0;
        }

        static bool Overlaps(Path p, int i, Rect r) { return p.X1[i] > r.X0 && p.X0[i] < r.X1 && p.Y1[i] > r.Y0 && p.Y0[i] < r.Y1; }

        /// <summary>The sides the player's box at tick i - 1 was wholly outside of the rect on (FullTasGoal.EnteredFrom's test).</summary>
        static int Outside(Path p, int i, Rect r)
        {
            if (i <= 0) return 0;
            int j = i - 1, s = 0;
            if (p.X1[j] <= r.X0) s |= FullTasGoal.FromLeft;
            if (p.X0[j] >= r.X1) s |= FullTasGoal.FromRight;
            if (p.Y1[j] <= r.Y0) s |= FullTasGoal.FromBottom;
            if (p.Y0[j] >= r.Y1) s |= FullTasGoal.FromTop;
            return s;
        }

        /// <summary>
        /// The rect's from= sides: 0 when the record's path enters it only on the pass at tick t (a gap under minTicks between
        /// touches is the same pass); else the sides that pass enters from and no other pass does. -1: no side tells them apart.
        /// </summary>
        static int Sides(Path p, Rect r, int t, int minTicks)
        {
            var entries = new List<(int start, int end)>();
            for (int i = 0; i < p.N; i++)
            {
                if (!Overlaps(p, i, r)) continue;
                int s = i;
                while (i + 1 < p.N && Overlaps(p, i + 1, r)) i++;
                if (entries.Count > 0 && s - entries[entries.Count - 1].end <= minTicks) entries[entries.Count - 1] = (entries[entries.Count - 1].start, i);
                else entries.Add((s, i));
            }
            int mine = entries.FindIndex(e => e.start <= t && t <= e.end);
            if (mine < 0) return -1;
            if (entries.Count == 1) return 0;
            int sides = Outside(p, entries[mine].start, r), others = 0;
            for (int k = 0; k < entries.Count; k++)
                if (k != mine) others |= Outside(p, entries[k].start, r);
            // Entries merged into one pass re-enter inside it; any re-entry could count too, so its sides are out.
            for (int i = entries[mine].start + 1; i <= entries[mine].end; i++)
                if (Overlaps(p, i, r) && !Overlaps(p, i - 1, r)) others |= Outside(p, i, r);
            int allowed = sides & ~others;
            return allowed == 0 ? -1 : allowed;
        }

        /// <summary>A band across the record's motion at tick t, or null when it can't be told from the record's other passes
        /// (or the player stands still there).</summary>
        static Rect? Band(Path p, int t, WaypointOptions o)
        {
            int a = Math.Max(0, t - 4), b = Math.Min(p.N - 1, t + 4);
            float dx = p.X[b] - p.X[a], dy = p.Y[b] - p.Y[a];
            if (dx * dx + dy * dy < 64f) { dx = p.VX[t]; dy = p.VY[t]; }
            if (dx * dx + dy * dy < 1f) return null;
            bool across = Math.Abs(dx) >= Math.Abs(dy);
            // Thick enough that the fastest tick near t can't step over it (the goal is checked once a tick).
            float step = 0f;
            for (int i = Math.Max(1, t - 3); i <= Math.Min(p.N - 1, t + 3); i++)
                step = Math.Max(step, across ? Math.Abs(p.X[i] - p.X[i - 1]) : Math.Abs(p.Y[i] - p.Y[i - 1]));
            float size = across ? p.X1[t] - p.X0[t] : p.Y1[t] - p.Y0[t];
            float thick = Math.Max(40f, step - size + 16f);
            float half = (across ? p.Y1[t] - p.Y0[t] : p.X1[t] - p.X0[t]) / 2f;
            float neg = Math.Clamp(across ? p.Down[t] : p.Left[t], half, o.Reach), pos = Math.Clamp(across ? p.Up[t] : p.Right[t], half, o.Reach);
            for (int shrink = 0; shrink < 3; shrink++)
            {
                Rect r = across
                    ? new Rect { X0 = p.X[t] - thick / 2f, X1 = p.X[t] + thick / 2f, Y0 = p.Y[t] - neg, Y1 = p.Y[t] + pos }
                    : new Rect { X0 = p.X[t] - neg, X1 = p.X[t] + pos, Y0 = p.Y[t] - thick / 2f, Y1 = p.Y[t] + thick / 2f };
                r = Rounded(r);
                int sides = Sides(p, r, t, o.MinTicks);
                if (sides >= 0) { r.From = sides; return r; }
                neg = Math.Max(half, neg / 2f);
                pos = Math.Max(half, pos / 2f);
            }
            return null;
        }

        /// <summary>A fixed rect (a checkpoint's trigger, a via box) with the from= sides its pass at t needs, or null.</summary>
        static Rect? Fixed(Path p, Rect r, int t, int minTicks)
        {
            r = Rounded(r);
            int sides = Sides(p, r, t, minTicks);
            if (sides < 0) return null;
            r.From = sides;
            return r;
        }

        /// <summary>The rect as its label writes it (centre and size to 0.1), so the goal checked is the goal labelled.</summary>
        static Rect Rounded(Rect r)
        {
            float cx = R((r.X0 + r.X1) / 2f), cy = R((r.Y0 + r.Y1) / 2f), w = R(r.W), h = R(r.H);
            return new Rect { X0 = cx - w / 2f, X1 = cx + w / 2f, Y0 = cy - h / 2f, Y1 = cy + h / 2f, From = r.From };
        }

        static float R(float v) { return (float)Math.Round(v, 1); }

        static Rect Union(Rect a, Rect b)
        {
            return new Rect { X0 = Math.Min(a.X0, b.X0), X1 = Math.Max(a.X1, b.X1), Y0 = Math.Min(a.Y0, b.Y0), Y1 = Math.Max(a.Y1, b.Y1) };
        }

        static FullTasGoal Goal(Rect r)
        {
            var g = new FullTasGoal { kind = FullTasGoal.At, x = R((r.X0 + r.X1) / 2f), y = R((r.Y0 + r.Y1) / 2f), w = R(r.W), h = R(r.H), from = r.From };
            g.label = "at:" + string.Join(",", new[] { g.x, g.y, g.w, g.h }.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)));
            if (g.from != 0) g.label += ",from=" + string.Join("+", FullTasGoal.SideNames.Where((s, i) => (g.from & 1 << i) != 0));
            return g;
        }

        // ---- the course's own checkpoints and via boxes ----

        /// <summary>The checkpoint triggers the record touches (the ticks its respawn point moves), with their trigger boxes.</summary>
        static List<(int tick, Rect rect, string name)> Checkpoints(string trace, Path p)
        {
            var found = new List<(int, Rect, string)>();
            if (!p.Respawned.Any(b => b)) return found;
            string worldPath = System.IO.Path.ChangeExtension(trace, null) + ".world.json";
            List<Map.Marker> markers;
            using (JsonDocument world = JsonDocument.Parse(File.ReadAllText(worldPath), new JsonDocumentOptions { MaxDepth = 256 }))
                markers = Map.Markers(world.RootElement, new CapturePos(0f, 0f)).Where(m => m.kind == "checkpoint" && m.area != null && m.area.Length > 0).ToList();
            for (int i = 0; i < p.N; i++)
            {
                if (!p.Respawned[i]) continue;
                // The trigger the player's box overlaps now (grown by a tick's worth: the trigger fired inside the step).
                const float slack = 24f;
                foreach (Map.Marker m in markers)
                {
                    var r = new Rect
                    {
                        X0 = m.area.Min(v => v.Where((_, k) => k % 2 == 0).Min()), X1 = m.area.Max(v => v.Where((_, k) => k % 2 == 0).Max()),
                        Y0 = m.area.Min(v => v.Where((_, k) => k % 2 == 1).Min()), Y1 = m.area.Max(v => v.Where((_, k) => k % 2 == 1).Max()),
                    };
                    if (p.X1[i] + slack > r.X0 && p.X0[i] - slack < r.X1 && p.Y1[i] + slack > r.Y0 && p.Y0[i] - slack < r.Y1)
                    {
                        found.Add((i, r, m.name));
                        break;
                    }
                }
            }
            return found;
        }

        static List<(int tick, Rect rect)> ViaBoxes(Simulation sim, JsonElement header, Path p)
        {
            var found = new List<(int, Rect)>();
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            CapturePos o = sim.WorldOriginInCapture;
            int from = 0;
            foreach (OfflineSolver.Box box in Solve.ForcedPaths(Array.Empty<string>(), course, caps).Via)
            {
                var r = new Rect { X0 = box.X0 + o.x, X1 = box.X1 + o.x, Y0 = box.Y0 + o.y, Y1 = box.Y1 + o.y };
                // Passed as the solver passes it: the player's position inside the box, in order.
                int at = -1;
                for (int i = from; i < p.N && at < 0; i++)
                    if (p.X[i] >= r.X0 && p.X[i] <= r.X1 && p.Y[i] >= r.Y0 && p.Y[i] <= r.Y1) at = i;
                if (at < 0) continue;
                found.Add((at, r));
                from = at + 1;
            }
            return found;
        }

        // ---- the goals ----

        public static Result Build(string trace, IReadOnlyList<InputTick> route, WaypointOptions o)
        {
            JsonElement header = Solve.ReadHeader(trace);
            Simulation sim = Solve.Creator(trace, header)();
            Path p = Replay(sim, route, o.Reach);
            if (!p.Finished) throw new InvalidOperationException("the route doesn't finish offline on " + System.IO.Path.GetFileName(trace) + ": no waypoints to take from it");
            var result = new Result { FinishTick = p.N - 1 };
            int n = p.N - 1;

            // The record's last start-gate crossing: where the game timer starts (record timing's start goal).
            int gate = -1, gateCourse = 0;
            for (int i = 1; i < p.N; i++)
                if (p.Timing[i] && !p.Timing[i - 1]) { gate = i; gateCourse = p.TimingCourse[i]; }
            if (p.N > 0 && p.Timing[0] && gate < 0) { gate = 0; gateCourse = p.TimingCourse[0]; }
            result.GateTick = gate;
            bool startGoal = o.Record && gate > 0 && gateCourse != 0;

            var chosen = new List<Waypoint>();
            // Kept apart from each other, from the start and the finish and, with a start goal, from the gate crossing (a goal
            // touched on the crossing's tick would leave the start goal current after the course has started).
            var marks = new List<int> { 0, n };
            if (startGoal) marks.Add(gate);
            bool Clear(int t, int ticks) => marks.All(m => Math.Abs(m - t) >= ticks);

            if (o.Via)
                foreach (var (tick, rect) in ViaBoxes(sim, header, p))
                {
                    Rect? r = Fixed(p, rect, tick, o.MinTicks);
                    if (r == null) { result.Notes.Add($"via box at tick {tick} can't be told from the record's other passes: left out"); continue; }
                    chosen.Add(new Waypoint { Goal = Goal(r.Value), Why = "via box", Tick = tick, Forced = true });
                    marks.Add(tick);
                }
            if (o.Checkpoints)
                foreach (var (tick, rect, name) in Checkpoints(trace, p))
                {
                    if (!Clear(tick, 2)) { result.Notes.Add($"checkpoint {name} at tick {tick} is next to another goal: left out"); continue; }
                    // The trigger widened by the band across the motion there (a line past the checkpoint that misses its small
                    // trigger still counts), else the trigger alone.
                    Rect? band = Band(p, tick, o);
                    Rect? r = band == null ? null : Fixed(p, Union(rect, band.Value), tick, o.MinTicks);
                    r ??= Fixed(p, rect, tick, o.MinTicks);
                    if (r == null) { result.Notes.Add($"checkpoint {name} at tick {tick} can't be told from the record's other passes: left out"); continue; }
                    chosen.Add(new Waypoint { Goal = Goal(r.Value), Why = "checkpoint " + name, Tick = tick, Forced = true });
                    marks.Add(tick);
                }

            // The automatic waypoints: a slot per stretch boundary, each taking the best phase change near it, else the path
            // where the slot falls.
            int stretches = o.TicksPer > 0 ? (int)Math.Round((double)n / o.TicksPer) : o.Stretches;
            stretches = Math.Min(stretches, n / Math.Max(1, o.MinTicks));
            List<Phase> phases = Phases(p);
            var auto = new List<Waypoint>();
            if (stretches > 1)
            {
                double slot = (double)n / stretches;
                for (int j = 1; j < stretches; j++)
                {
                    double ideal = j * slot;
                    // A forced waypoint in the slot's reach serves it.
                    if (chosen.Any(w => Math.Abs(w.Tick - ideal) < slot / 2)) continue;
                    int lo = Math.Max(1, (int)Math.Ceiling(ideal - slot / 2)), hi = Math.Min(n - 1, (int)Math.Floor(ideal + slot / 2));
                    var picks = new List<(double score, int tick, string why)>();
                    foreach (Phase ph in phases)
                        if (ph.Tick >= lo && ph.Tick <= hi) picks.Add((ph.Priority - 3 * Math.Abs(ph.Tick - ideal) / slot, ph.Tick, ph.Kind));
                    for (int t = lo; t <= hi; t++) picks.Add((-3 * Math.Abs(t - ideal) / slot, t, "spacing"));
                    // A band the record passes once (no from=) before one it passes again: a from= side also turns away every
                    // other line that comes in another way (Course 7 ZIP's 8.12 line entered the record's revisited band from the
                    // right).
                    Waypoint pick = null, sided = null;
                    foreach (var (score, tick, why) in picks.OrderByDescending(q => q.score).Take(80))
                    {
                        if (!Clear(tick, o.MinTicks)) continue;
                        Rect? r = Band(p, tick, o);
                        if (r == null) continue;
                        var w = new Waypoint { Goal = Goal(r.Value), Why = why, Tick = tick };
                        if (r.Value.From == 0) { pick = w; break; }
                        sided ??= w;
                    }
                    pick ??= sided;
                    if (pick == null) continue;
                    auto.Add(pick);
                    marks.Add(pick.Tick);
                }
            }
            chosen.AddRange(auto);
            chosen = chosen.OrderBy(w => w.Tick).ToList();
            if (o.Stride > 1)
            {
                int k = 0;
                chosen = chosen.Where(w => w.Why == "via box" || k++ % o.Stride == 0).ToList();
            }

            FullTasGoal finish = Finish(sim, header);
            FullTasGoal start = startGoal ? new FullTasGoal { kind = FullTasGoal.Start, course = gateCourse, label = "start:" + gateCourse } : null;

            // The record replayed under the goals: a waypoint it misses (or reaches on the previous goal's heels) is dropped.
            for (int attempt = 0; ; attempt++)
            {
                List<FullTasGoal> goals = Assemble(chosen, start, gate, finish, out int startIndex);
                long[] reached = Check(trace, route, goals);
                int missed = Array.IndexOf(reached, -1L);
                int crowded = -1;
                for (int k = 1; k < goals.Count && missed < 0 && crowded < 0; k++)
                    if (goals[k].kind == FullTasGoal.At && reached[k] - reached[k - 1] < Math.Max(2, o.MinTicks / 2)
                        && chosen.FirstOrDefault(w => w.Goal == goals[k])?.Forced == false) crowded = k;
                int bad = missed >= 0 ? missed : crowded;
                if (bad < 0 || attempt > chosen.Count + 2)
                {
                    result.Goals = goals;
                    result.Waypoints = chosen;
                    result.Reached = reached;
                    if (bad >= 0) result.Notes.Add($"goal {bad + 1} ({goals[bad].label}) still {(missed >= 0 ? "missed" : "crowded")} after {attempt} drops");
                    if (startIndex >= 0 && reached[startIndex] >= 0 && reached[startIndex] != p.Physics[gate])
                        result.Notes.Add($"start:{gateCourse} fires at physics tick {reached[startIndex]}, not at the last gate crossing ({p.Physics[gate]})");
                    o.Log?.Invoke($"waypoints: {chosen.Count} over {n} ticks ({string.Join(", ", chosen.Select(w => w.Why + " @" + w.Tick))})"
                        + (result.Notes.Count > 0 ? "; " + string.Join("; ", result.Notes) : ""));
                    return result;
                }
                Waypoint drop = chosen.FirstOrDefault(w => w.Goal == goals[bad]);
                if (drop == null)
                {
                    // The start or finish goal missed: the waypoint before it spoils it (a touch on the gate's tick).
                    drop = chosen.LastOrDefault(w => goals.IndexOf(w.Goal) < bad);
                    if (drop == null) { attempt = chosen.Count + 3; continue; }
                }
                result.Notes.Add($"dropped {drop.Why} @{drop.Tick} ({drop.Goal.label}): {(missed >= 0 ? "the record misses goal " + (bad + 1) : "reached within " + (reached[bad] - reached[bad - 1]) + " ticks of the goal before")}");
                chosen.Remove(drop);
            }
        }

        static List<FullTasGoal> Assemble(List<Waypoint> chosen, FullTasGoal start, int gate, FullTasGoal finish, out int startIndex)
        {
            var goals = new List<FullTasGoal>();
            startIndex = -1;
            foreach (Waypoint w in chosen)
            {
                if (start != null && startIndex < 0 && w.Tick >= gate) { startIndex = goals.Count; goals.Add(start); }
                goals.Add(w.Goal);
            }
            if (start != null && startIndex < 0) { startIndex = goals.Count; goals.Add(start); }
            goals.Add(finish);
            return goals;
        }

        /// <summary>The course's finish as a goal: a transition's next course start, a bonus's finish rect (an area goal), else
        /// the course's finish.</summary>
        static FullTasGoal Finish(Simulation sim, JsonElement header)
        {
            if (header.TryGetProperty("finishStart", out JsonElement next))
                return new FullTasGoal { kind = FullTasGoal.Start, course = next.GetInt32(), label = "start:" + next.GetInt32() };
            if (header.TryGetProperty("finish", out JsonElement f))
            {
                float w = f[2].GetSingle(), h = f[3].GetSingle(), cx = f[0].GetSingle(), cy = f[1].GetSingle();
                return Goal(new Rect { X0 = cx - w / 2f, X1 = cx + w / 2f, Y0 = cy - h / 2f, Y1 = cy + h / 2f });
            }
            return new FullTasGoal { kind = FullTasGoal.Finish, course = sim.TargetCourse, label = "finish:" + sim.TargetCourse };
        }

        /// <summary>The trace's header without its own finish (a bonus's finish rect, a transition's next start): with goals, the
        /// last goal is the finish, and the header's would end the run on that tick before the goal check.</summary>
        internal static JsonElement GoalHeader(JsonElement header)
        {
            JsonObject copy = JsonNode.Parse(header.GetRawText()).AsObject();
            copy.Remove("finish");
            copy.Remove("transition");
            copy.Remove("finishStart");
            using JsonDocument doc = JsonDocument.Parse(copy.ToJsonString());
            return doc.RootElement.Clone();
        }

        /// <summary>The route replayed under the goals from the trace's start: each goal's physics tick, -1 where it isn't reached.</summary>
        public static long[] Check(string trace, IReadOnlyList<InputTick> route, IReadOnlyList<FullTasGoal> goals)
        {
            Simulation sim = Solve.Creator(trace, GoalHeader(Solve.ReadHeader(trace)))();
            var tracker = new FullTasTracker(goals.ToArray());
            SimSnapshot start = sim.Advance(new List<InputTick>());
            sim.UseGoals(tracker);
            sim.SetStart(start.WithGoal(tracker.Capture()), 0);
            RunResult r = sim.Run(route);
            var reached = new long[goals.Count];
            for (int k = 0; k < goals.Count; k++) reached[k] = r.GoalPhysicsTicks != null && k < r.Progress ? r.GoalPhysicsTicks[k] : -1;
            return reached;
        }

        // ---- the command ----

        /// <summary><c>waypoints &lt;trace&gt; [--route route.json] [waypoint options] [--out goals.json]</c>: the goals For
        /// returns, why each is there, and the record's tick at each under them.</summary>
        public static int Command(string[] args)
        {
            string trace = args[0];
            string routePath = Solve.Option(args, "--route", (string)null);
            List<InputTick> route;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(routePath ?? trace), new JsonDocumentOptions { MaxDepth = 256 }))
                route = Solve.ReadInputs(doc.RootElement);
            WaypointOptions o = WaypointOptions.Parse(args, Console.WriteLine);
            Result r = Build(trace, route, o);
            Console.WriteLine($"{System.IO.Path.GetFileName(routePath ?? trace)}: {r.FinishTick + 1} physics ticks, timer from tick {r.GateTick}, {r.Goals.Count} goals");
            for (int k = 0; k < r.Goals.Count; k++)
            {
                FullTasGoal g = r.Goals[k];
                Waypoint w = r.Waypoints.FirstOrDefault(x => x.Goal == g);
                long gap = k == 0 ? r.Reached[k] : r.Reached[k] - r.Reached[k - 1];
                Console.WriteLine($"  {k + 1,2}. {g.label,-44} reached {r.Reached[k],5} (+{gap,3})" + (w != null ? $"  {w.Why} @{w.Tick}" : ""));
            }
            foreach (string note in r.Notes) Console.WriteLine("  note: " + note);
            // --check <route.json>...: other routes of the course (another record, a farm route, an offline best) replayed on
            // the trace under the goals: an alternate line that reaches them all is one the waypoints let through.
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "--check") continue;
                List<InputTick> other;
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(args[i + 1]), new JsonDocumentOptions { MaxDepth = 256 }))
                    other = Solve.ReadInputs(doc.RootElement);
                long[] at = Check(trace, other, r.Goals);
                int got = at.TakeWhile(t => t >= 0).Count();
                Console.WriteLine($"  check {System.IO.Path.GetFileName(args[i + 1])}: {got}/{at.Length} goals"
                    + (got < at.Length ? $", misses goal {got + 1} ({r.Goals[got].label})" : $", finish at physics tick {at[at.Length - 1]}")
                    + $" (finish alone: {Check(trace, other, new[] { r.Goals[r.Goals.Count - 1] })[0]})");
                if (got < at.Length && r.Goals[got].kind == FullTasGoal.At && args.Contains("--why"))
                {
                    // Where the other line meets the missed rect: each pass, the tick and the sides it came in from.
                    FullTasGoal g = r.Goals[got];
                    Path q = Replay(Solve.Creator(trace, GoalHeader(Solve.ReadHeader(trace)))(), other, 0f);
                    var rect = new Rect { X0 = g.x - g.w / 2f, X1 = g.x + g.w / 2f, Y0 = g.y - g.h / 2f, Y1 = g.y + g.h / 2f };
                    var passes = new List<string>();
                    float near = float.PositiveInfinity; int nearAt = -1;
                    for (int t = 0; t < q.N; t++)
                    {
                        if (Overlaps(q, t, rect) && (t == 0 || !Overlaps(q, t - 1, rect))) passes.Add($"tick {t} from {Outside(q, t, rect)}");
                        float dx = Math.Max(0f, Math.Max(rect.X0 - q.X1[t], q.X0[t] - rect.X1)), dy = Math.Max(0f, Math.Max(rect.Y0 - q.Y1[t], q.Y0[t] - rect.Y1));
                        float d = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (d < near) { near = d; nearAt = t; }
                    }
                    Console.WriteLine($"    passes: {(passes.Count == 0 ? "none" : string.Join(", ", passes))}; nearest {near:0} at tick {nearAt} ({q.X[nearAt]:0},{q.Y[nearAt]:0}); goal {got} reached at {(got > 0 ? at[got - 1] : 0)}");
                }
            }
            string outPath = Solve.Option(args, "--out", (string)null);
            if (outPath != null)
                File.WriteAllText(outPath, JsonSerializer.Serialize(new
                {
                    goals = r.Goals.Select(g => g.label),
                    reached = r.Reached,
                    why = r.Goals.Select(g => r.Waypoints.FirstOrDefault(x => x.Goal == g)?.Why),
                }, new JsonSerializerOptions { WriteIndented = true }));
            return r.Reached.All(t => t >= 0) ? 0 : 1;
        }
    }
}
