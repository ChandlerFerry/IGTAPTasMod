using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    static class TimeLossFloor
    {
        const string Usage = "usage: timeloss floor <trace.json> <route.json> [--json]";
        const float Cell = 8f;
        const int SpeedWindow = 10;
        const float TeleportSlack = 20f;

        struct Sample
        {
            public long Tick;
            public int Input;
            public float X, Y, Speed;
            public string Caps;
        }

        static void Sustained(List<Sample> s, float dt, Dictionary<string, (float speed, string how)> top, string how)
        {
            float sum = 0f;
            var steps = new Queue<float>();
            for (int i = 1; i < s.Count; i++)
            {
                float dx = s[i].X - s[i - 1].X, dy = s[i].Y - s[i - 1].Y, step = (float)Math.Sqrt(dx * dx + dy * dy);
                if (Jumped(s[i - 1], s[i], dt) || s[i].Caps != s[i - 1].Caps) { steps.Clear(); sum = 0f; continue; }
                steps.Enqueue(step);
                sum += step;
                if (steps.Count > SpeedWindow) sum -= steps.Dequeue();
                if (steps.Count < SpeedWindow) continue;
                float v = sum / SpeedWindow;
                if (!top.TryGetValue(s[i].Caps, out var known) || v > known.speed) top[s[i].Caps] = (v, $"{how}, tick {s[i].Tick}");
            }
        }

        static bool Jumped(Sample a, Sample b, float dt)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, step = (float)Math.Sqrt(dx * dx + dy * dy);
            return step > 2f * Math.Max(a.Speed, b.Speed) * dt + TeleportSlack;
        }

        static InputTick Stick(float x, float y, bool held, bool press = false, bool release = false)
        {
            return new InputTick { x = x, y = y, held = held || press, press = press, release = release && !held && !press };
        }

        static IEnumerable<(List<InputTick> moves, int links)> SpeedMoves(bool omni)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                var dash = new List<InputTick> { new InputTick { x = side, dash = true } };
                dash.AddRange(Enumerable.Repeat(Stick(side, 0f, false), 40));
                yield return (dash, 0);
                foreach (int jump in new[] { 3, 5, 7 })
                {
                    var super = new List<InputTick> { new InputTick { x = side, dash = true, dashJump = jump, lockX = side } };
                    super.AddRange(Enumerable.Repeat(Stick(side, 0f, true), 6));
                    super.Add(Stick(side, 0f, false, release: true));
                    super.AddRange(Enumerable.Repeat(Stick(side, 0f, false), 40));
                    yield return (super, 0);
                }
                if (!omni) continue;
                foreach (int links in new[] { 2, 3, 4, 5, 6 })
                    foreach (int gap in new[] { 3, 7 })
                        foreach (int jump in new[] { 3, 6 })
                        {
                            var t = new List<InputTick>();
                            for (int l = 0; l < links; l++)
                            {
                                InputTick d = Stick(0f, -1f, false);
                                d.dash = true;
                                d.dashJump = jump;
                                d.lockX = side;
                                t.Add(d);
                                for (int i = 0; i < 3; i++) t.Add(Stick(side, 0f, true));
                                t.Add(Stick(side, 0f, false, release: true));
                                for (int i = 0; i < gap; i++) t.Add(Stick(side, 0f, false));
                            }
                            t.AddRange(Enumerable.Repeat(Stick(side, 0f, false), 20));
                            yield return (t, links);
                        }
            }
        }

        internal sealed class Row
        {
            public int Index, From, To;
            public string Kind, Next, Caps;
            public long FromPhysics, ToPhysics, Ticks, Timed, Floor, TimedFloor;
            public float Distance, VTop;
            public long Slack => Ticks - Floor;
            public long TimedSlack => Timed - TimedFloor;
        }

        internal sealed class Result
        {
            public string Route;
            public bool Finished;
            public float Time;
            public long Ticks;
            public Dictionary<string, (float speed, string how)> Speeds;
            public List<Row> Rows;
        }

        internal static int Run(string[] args)
        {
            if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) || args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            Result r = Measure(args[0], args[1]);
            Console.WriteLine(args.Contains("--json") ? JsonSerializer.Serialize(Json(r)) : Table(r));
            return 0;
        }

        internal static Result Measure(string tracePath, string routePath)
        {
            var (header, labels, inputs) = TechsUsed.Load(tracePath, routePath, "timeloss floor");
            if (labels != null) throw new ArgumentException("timeloss floor: a course trace, not a Full-TAS run's");
            var worker = new Worker<Simulation>(() => TechsUsed.Create(tracePath, header, null), "timeloss floor");
            Result result = null;
            float cell = DistanceField.CellSize;
            try
            {
                DistanceField.CellSize = Cell;
                worker.Do(sim => result = Measure(sim, Path.GetFileName(routePath), inputs));
            }
            finally
            {
                DistanceField.CellSize = cell;
                worker.Stop();
            }
            return result;
        }

        static Result Measure(Simulation sim, string name, List<InputTick> inputs)
        {
            List<Difficulty.Anchor> anchors = Difficulty.Anchors(sim, inputs, null, true, out TechSteps record, out RunResult run, out _);
            int segments = anchors.Count - 1;
            float dt = sim.World.FixedDeltaTime;
            long timerFrom = sim.TimedFromRunStart ? 0 : -1;
            for (int i = 0; i < record.Steps.Count && !sim.TimedFromRunStart; i++)
                if (record.Steps[i].Timing && (i == 0 || !record.Steps[i - 1].Timing)) timerFrom = record.Steps[i].Physics;
            long Timed(long physics) => timerFrom < 0 ? 0 : Math.Max(0, physics - timerFrom);
            var via = new List<OfflineSolver.Box>();
            for (int i = 1; i < anchors.Count - (run.Finished ? 1 : 0); i++)
            {
                MoveState m = anchors[i].Move;
                via.Add(new OfflineSolver.Box(new WorldPos(m.X - Cell / 2f, m.Y - Cell / 2f), new WorldPos(m.X + Cell / 2f, m.Y + Cell / 2f)));
            }
            DistanceField field = DistanceField.For(sim);
            field.SetVia(via);
            int SegmentAt(long physics)
            {
                int i = 0;
                while (i + 1 < segments && anchors[i + 1].Physics <= physics) i++;
                return i;
            }

            bool gates = sim.Goals == null && !sim.TimedFromRunStart;
            Profile(sim, inputs, anchors, field, SegmentAt, gates);
            (List<Sample> path, float[] dist, float[] gate) = Profile(sim, inputs, anchors, field, SegmentAt, gates);

            var speeds = new Dictionary<string, (float speed, string how)>();
            Sustained(path, dt, speeds, "the route's path");
            for (int a = 0; a < segments; a++) Probe(sim, inputs, anchors[a], dt, speeds);

            var rows = new List<Row>();
            for (int i = 0; i < segments; i++)
            {
                Difficulty.Anchor from = anchors[i], to = anchors[i + 1];
                var row = new Row
                {
                    Index = i, From = from.Tick, To = to.Tick, Kind = from.Kind, Next = to.Kind, FromPhysics = from.Physics, ToPhysics = to.Physics,
                    Ticks = to.Physics - from.Physics, Timed = Timed(to.Physics) - Timed(from.Physics),
                };
                rows.Add(row);
                int start = path.FindIndex(x => x.Tick == from.Physics);
                if (row.Ticks <= 0 || start < 0) continue;
                MoveState target = to.Move;
                float Dist(int k)
                {
                    if (!float.IsNaN(dist[k])) return dist[k];
                    float dx = path[k].X - target.X, dy = path[k].Y - target.Y;
                    return MathF.Sqrt(dx * dx + dy * dy);
                }
                int crossing = timerFrom > from.Physics && timerFrom < to.Physics ? path.FindIndex(x => x.Tick == timerFrom) : -1;
                float Walk(int k) => k < crossing && !float.IsNaN(gate[k]) ? gate[k] + Dist(crossing) : Dist(k);
                row.Caps = path[start].Caps;
                row.VTop = speeds.TryGetValue(row.Caps, out var v) ? Math.Max(1f, v.speed) : 1f;
                row.Distance = Walk(start);
                double best = row.Distance / row.VTop;
                for (int k = start + 1; k < path.Count && path[k].Tick < to.Physics; k++)
                    if (Jumped(path[k - 1], path[k], dt)) best = Math.Min(best, 1 + Walk(k) / row.VTop);
                row.Floor = Math.Max(1, (long)Math.Ceiling(best));
                row.TimedFloor = row.Timed == 0 ? 0 : crossing < 0 ? row.Floor : Math.Max(1, (long)Math.Ceiling(Dist(crossing) / row.VTop));
            }
            return new Result
            {
                Route = name, Finished = run.Finished, Time = run.Finished ? run.Time : 0f, Ticks = anchors[segments].Physics, Speeds = speeds, Rows = rows,
            };
        }

        static (List<Sample> path, float[] dist, float[] gate) Profile(Simulation sim, List<InputTick> inputs, List<Difficulty.Anchor> anchors,
            DistanceField field, Func<long, int> segmentAt, bool gates)
        {
            var path = new List<Sample>();
            var dist = new List<float>();
            var gate = new List<float>();
            long end = anchors[anchors.Count - 1].Physics;
            float Finite(float d) => float.IsInfinity(d) ? float.NaN : d;
            void Add(long tick)
            {
                Movement m = sim.Player;
                WorldPos w = sim.PlayerWorld;
                CapturePos c = sim.PlayerAtCapture;
                path.Add(new Sample
                {
                    Tick = tick, Input = (int)sim.Engine.RunTicks, X = w.x, Y = w.y, Speed = (m.Velocity + m.momentum).magnitude, Caps = PlayerCaps.Of(sim).Id,
                });
                dist.Add(Finite(field.Distance(c, segmentAt(tick))));
                gate.Add(gates ? Finite(field.StartGateDistance(c)) : float.NaN);
            }
            sim.Restore(sim.Start);
            Add(anchors[0].Physics);
            sim.Run(inputs, null, tick => tick < anchors[anchors.Count - 1].Tick, 400, _ =>
            {
                if (sim.Engine.PhysicsTicks <= end) Add(sim.Engine.PhysicsTicks);
            });
            return (path, dist.ToArray(), gate.ToArray());
        }

        static void Probe(Simulation sim, List<InputTick> inputs, Difficulty.Anchor at, float dt, Dictionary<string, (float speed, string how)> speeds)
        {
            if (at.State == null) return;
            sim.Restore(at.State);
            string caps = PlayerCaps.Of(sim).Id;
            foreach (var (moves, links) in SpeedMoves(caps.Contains("om", StringComparison.OrdinalIgnoreCase)))
            {
                var s = new List<Sample>();
                List<InputTick> probe = inputs.Take(at.Tick).Concat(moves).ToList();
                sim.Run(probe, at.State, null, 20, _ =>
                {
                    WorldPos w = sim.PlayerWorld;
                    s.Add(new Sample { Tick = sim.Engine.PhysicsTicks, X = w.x, Y = w.y, Speed = (sim.Player.Velocity + sim.Player.momentum).magnitude, Caps = caps });
                });
                Sustained(s, dt, speeds, $"speed probe from input tick {at.Tick}" + (links > 0 ? $", a {links}-link bounce chain" : ""));
            }
        }

        internal static IEnumerable<Row> Ranked(Result r) => r.Rows.OrderByDescending(x => x.Timed > 0).ThenByDescending(x => x.Timed > 0 ? x.TimedSlack : x.Slack)
            .ThenBy(x => x.Index);

        internal static string Header(Result r)
        {
            return $"{r.Route}: " + (r.Finished ? $"finished, {r.Time.ToString("0.00", CultureInfo.InvariantCulture)} s on the course timer" : "not finished")
                + $", {r.Ticks} physics ticks, {r.Rows.Count} segments; top path speed (u/tick over {SpeedWindow} ticks): "
                + string.Join("; ", r.Speeds.OrderBy(v => v.Key).Select(v => $"{v.Key} {v.Value.speed.ToString("0.0", CultureInfo.InvariantCulture)} ({v.Value.how})"));
        }

        static string Table(Result r)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header(r));
            sb.AppendLine("floor = walkable distance to the next anchor (the finish; a start gate first while the timer is off) / top path speed;"
                + " slack = ticks - floor; timed: the course timer's share of both; timed segments first");
            sb.AppendLine(" seg  inputs     anchor -> next      ticks  floor  slack  timed  floor  slack    dist  v/tick");
            foreach (Row x in Ranked(r))
                sb.AppendLine($" {x.Index,3}  {x.From,4}-{x.To,-4}  {x.Kind,-7} -> {x.Next,-7}  {x.Ticks,5}  {x.Floor,5}  {x.Slack,5}  {x.Timed,5}  {x.TimedFloor,5}  {x.TimedSlack,5}"
                    + $"  {x.Distance,6:0}  {x.VTop,6:0.0}");
            return sb.ToString().TrimEnd();
        }

        internal static object Json(Row x) => new
        {
            index = x.Index, from = x.From, to = x.To, anchor = x.Kind, next = x.Next, fromPhysics = x.FromPhysics, toPhysics = x.ToPhysics, ticks = x.Ticks,
            timed = x.Timed, floor = x.Floor, slack = x.Slack, timedFloor = x.TimedFloor, timedSlack = x.TimedSlack, distance = Math.Round(x.Distance, 1),
            vtop = Math.Round(x.VTop, 2), caps = x.Caps,
        };

        static object Json(Result r) => new
        {
            route = r.Route, finished = r.Finished, time = r.Time, ticks = r.Ticks,
            speeds = r.Speeds.ToDictionary(v => v.Key, v => new { speed = Math.Round(v.Value.speed, 2), how = v.Value.how }),
            segments = Ranked(r).Select(Json),
        };
    }
}
