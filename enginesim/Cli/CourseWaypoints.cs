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
    sealed class WaypointOptions
    {
        public int Stretches = 6;
        public bool Checkpoints = true;
        public bool Record = true;
        public int Stride = 1;
        public Action<string> Log;

        public static WaypointOptions Parse(string[] args, Action<string> log = null)
        {
            return new WaypointOptions
            {
                Stretches = Solve.Option(args, "--waypoints", 6),
                Checkpoints = !args.Contains("--no-checkpoint-waypoints"),
                Record = !args.Contains("--farm"),
                Stride = Math.Max(1, Solve.Option(args, "--waypoint-stride", 1)),
                Log = log,
            };
        }
    }

    static class CourseWaypoints
    {
        const int MinTicks = 16;
        const float Reach = 360f;


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
            public long[] Reached;
            public int FinishTick, GateTick;
            public List<string> Notes = new List<string>();
        }

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

        const int DetourTicks = 150;
        const float DetourReach = 300f;

        static bool[] Detours(Path p)
        {
            var inside = new bool[p.N];
            for (int a = 0; a < p.N; a++)
            {
                float far = 0f;
                int turn = a;
                for (int b = a + 1; b < p.N && b - a <= DetourTicks; b++)
                {
                    if (Math.Abs(p.X[b] - p.X[b - 1]) + Math.Abs(p.Y[b] - p.Y[b - 1]) > 200f) break;
                    float d = Math.Abs(p.X[b] - p.X[a]);
                    if (d > far) { far = d; turn = b; }
                    if (far < DetourReach || d > far / 4f) continue;
                    bool wall = false;
                    for (int t = Math.Max(0, turn - 2); t <= Math.Min(p.N - 1, turn + 2); t++) wall |= p.Wall[t];
                    if (!wall) for (int t = a + 1; t < b; t++) inside[t] = true;
                    break;
                }
            }
            return inside;
        }

        struct Rect
        {
            public float X0, X1, Y0, Y1;
            public int From;
            public float W => X1 - X0;
            public float H => Y1 - Y0;
        }

        static bool Overlaps(Path p, int i, Rect r) { return p.X1[i] > r.X0 && p.X0[i] < r.X1 && p.Y1[i] > r.Y0 && p.Y0[i] < r.Y1; }

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
            for (int i = entries[mine].start + 1; i <= entries[mine].end; i++)
                if (Overlaps(p, i, r) && !Overlaps(p, i - 1, r)) others |= Outside(p, i, r);
            int allowed = sides & ~others;
            return allowed == 0 ? -1 : allowed;
        }

        static Rect? Band(Path p, int t, WaypointOptions o)
        {
            int a = Math.Max(0, t - 4), b = Math.Min(p.N - 1, t + 4);
            float dx = p.X[b] - p.X[a], dy = p.Y[b] - p.Y[a];
            if (dx * dx + dy * dy < 64f) { dx = p.VX[t]; dy = p.VY[t]; }
            if (dx * dx + dy * dy < 1f) return null;
            bool across = Math.Abs(dx) >= Math.Abs(dy);
            float step = 0f;
            for (int i = Math.Max(1, t - 3); i <= Math.Min(p.N - 1, t + 3); i++)
                step = Math.Max(step, across ? Math.Abs(p.X[i] - p.X[i - 1]) : Math.Abs(p.Y[i] - p.Y[i - 1]));
            float size = across ? p.X1[t] - p.X0[t] : p.Y1[t] - p.Y0[t];
            float thick = Math.Max(40f, step - size + 16f);
            float half = (across ? p.Y1[t] - p.Y0[t] : p.X1[t] - p.X0[t]) / 2f;
            float neg = Math.Clamp(across ? p.Down[t] : p.Left[t], half, Reach), pos = Math.Clamp(across ? p.Up[t] : p.Right[t], half, Reach);
            for (int shrink = 0; shrink < 3; shrink++)
            {
                Rect r = across
                    ? new Rect { X0 = p.X[t] - thick / 2f, X1 = p.X[t] + thick / 2f, Y0 = p.Y[t] - neg, Y1 = p.Y[t] + pos }
                    : new Rect { X0 = p.X[t] - neg, X1 = p.X[t] + pos, Y0 = p.Y[t] - thick / 2f, Y1 = p.Y[t] + thick / 2f };
                r = Rounded(r);
                int sides = Sides(p, r, t, MinTicks);
                if (sides >= 0) { r.From = sides; return r; }
                neg = Math.Max(half, neg / 2f);
                pos = Math.Max(half, pos / 2f);
            }
            return null;
        }

        static Rect? Fixed(Path p, Rect r, int t, int minTicks)
        {
            r = Rounded(r);
            int sides = Sides(p, r, t, minTicks);
            if (sides < 0) return null;
            r.From = sides;
            return r;
        }

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
                int at = -1;
                for (int i = from; i < p.N && at < 0; i++)
                    if (p.X[i] >= r.X0 && p.X[i] <= r.X1 && p.Y[i] >= r.Y0 && p.Y[i] <= r.Y1) at = i;
                if (at < 0) continue;
                found.Add((at, r));
                from = at + 1;
            }
            return found;
        }

        public static Result Build(string trace, IReadOnlyList<InputTick> route, WaypointOptions o)
        {
            JsonElement header = Solve.ReadHeader(trace);
            return Build(trace, header, Solve.Creator(trace, header)(), route, o, goals => Check(trace, route, goals));
        }

        internal static Result Build(string trace, JsonElement header, Simulation sim, IReadOnlyList<InputTick> route, WaypointOptions o,
            Func<List<FullTasGoal>, long[]> check)
        {
            Path p = Replay(sim, route, Reach);
            if (!p.Finished) throw new InvalidOperationException("the route doesn't finish offline on " + System.IO.Path.GetFileName(trace) + ": no waypoints to take from it");
            var result = new Result { FinishTick = p.N - 1 };
            int n = p.N - 1;

            int gate = -1, gateCourse = 0;
            for (int i = 1; i < p.N; i++)
                if (p.Timing[i] && !p.Timing[i - 1]) { gate = i; gateCourse = p.TimingCourse[i]; }
            if (p.N > 0 && p.Timing[0] && gate < 0) { gate = 0; gateCourse = p.TimingCourse[0]; }
            result.GateTick = gate;
            bool startGoal = o.Record && gate > 0 && gateCourse != 0;

            var chosen = new List<Waypoint>();
            bool[] detour = Detours(p);
            // Keep goals off the gate-crossing tick, or the start goal stays current after the course has started.
            var marks = new List<int> { 0, n };
            if (startGoal) marks.Add(gate);
            bool Clear(int t, int ticks) => marks.All(m => Math.Abs(m - t) >= ticks);

            foreach (var (tick, rect) in ViaBoxes(sim, header, p))
            {
                Rect? r = Fixed(p, rect, tick, MinTicks);
                if (r == null) { result.Notes.Add($"via box at tick {tick} can't be told from the record's other passes: left out"); continue; }
                chosen.Add(new Waypoint { Goal = Goal(r.Value), Why = "via box", Tick = tick, Forced = true });
                marks.Add(tick);
            }
            if (o.Checkpoints)
                foreach (var (tick, rect, name) in Checkpoints(trace, p))
                {
                    if (!Clear(tick, 2)) { result.Notes.Add($"checkpoint {name} at tick {tick} is next to another goal: left out"); continue; }
                    if (detour[tick]) { result.Notes.Add($"checkpoint {name} at tick {tick} is inside a detour: left out"); continue; }
                    Rect? band = Band(p, tick, o);
                    Rect? r = band == null ? null : Fixed(p, Union(rect, band.Value), tick, MinTicks);
                    r ??= Fixed(p, rect, tick, MinTicks);
                    if (r == null) { result.Notes.Add($"checkpoint {name} at tick {tick} can't be told from the record's other passes: left out"); continue; }
                    chosen.Add(new Waypoint { Goal = Goal(r.Value), Why = "checkpoint " + name, Tick = tick, Forced = true });
                    marks.Add(tick);
                }

            int stretches = Math.Min(o.Stretches, n / MinTicks);
            List<Phase> phases = Phases(p);
            var auto = new List<Waypoint>();
            if (stretches > 1)
            {
                double slot = (double)n / stretches;
                for (int j = 1; j < stretches; j++)
                {
                    double ideal = j * slot;
                    if (chosen.Any(w => Math.Abs(w.Tick - ideal) < slot / 2)) continue;
                    int lo = Math.Max(1, (int)Math.Ceiling(ideal - slot / 2)), hi = Math.Min(n - 1, (int)Math.Floor(ideal + slot / 2));
                    var picks = new List<(double score, int tick, string why)>();
                    foreach (Phase ph in phases)
                        if (ph.Tick >= lo && ph.Tick <= hi) picks.Add((ph.Priority - 3 * Math.Abs(ph.Tick - ideal) / slot, ph.Tick, ph.Kind));
                    for (int t = lo; t <= hi; t++) picks.Add((-3 * Math.Abs(t - ideal) / slot, t, "spacing"));
                    Waypoint pick = null, sided = null;
                    foreach (var (score, tick, why) in picks.OrderByDescending(q => q.score).Take(80))
                    {
                        if (!Clear(tick, MinTicks) || detour[tick]) continue;
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

            for (int attempt = 0; ; attempt++)
            {
                List<FullTasGoal> goals = Assemble(chosen, start, gate, finish, out int startIndex);
                long[] reached = check(goals);
                int missed = Array.IndexOf(reached, -1L);
                int crowded = -1;
                for (int k = 1; k < goals.Count && missed < 0 && crowded < 0; k++)
                    if (goals[k].kind == FullTasGoal.At && reached[k] - reached[k - 1] < Math.Max(2, MinTicks / 2)
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

        internal static FullTasGoal Finish(Simulation sim, JsonElement header)
        {
            if (header.TryGetProperty("finishStart", out JsonElement next))
                return new FullTasGoal { kind = FullTasGoal.Start, course = next.GetInt32(), label = "start:" + next.GetInt32() };
            if (header.TryGetProperty("finish", out JsonElement f))
            {
                float w = f[2].GetSingle(), h = f[3].GetSingle(), cx = f[0].GetSingle(), cy = f[1].GetSingle();
                return Goal(new Rect { X0 = cx - w / 2f, X1 = cx + w / 2f, Y0 = cy - h / 2f, Y1 = cy + h / 2f });
            }
            return new FullTasGoal { kind = FullTasGoal.Farm, course = sim.TargetCourse, label = "farm:" + sim.TargetCourse };
        }

        // Drop the header's own finish: with goals the last goal is the finish, and the header's would end the run early.
        internal static JsonElement GoalHeader(JsonElement header)
        {
            JsonObject copy = JsonNode.Parse(header.GetRawText()).AsObject();
            copy.Remove("finish");
            copy.Remove("transition");
            copy.Remove("finishStart");
            using JsonDocument doc = JsonDocument.Parse(copy.ToJsonString());
            return doc.RootElement.Clone();
        }

        internal static long[] Check(string trace, IReadOnlyList<InputTick> route, IReadOnlyList<FullTasGoal> goals)
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

    }
}
