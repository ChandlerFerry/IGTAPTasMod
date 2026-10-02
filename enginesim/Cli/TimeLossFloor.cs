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
    /// <summary>
    /// <c>timeloss floor &lt;trace.json&gt; &lt;route.json&gt; [--json]</c> (enginesim/README.md "Time loss"): per segment of a course route
    /// (<see cref="Difficulty.Anchors(Simulation, List{InputTick}, SimSnapshot, bool)"/> on its own inputs), a floor on its physics ticks
    /// as <c>fulltas --auto</c>'s per-goal model bounds a goal (FullTasAuto.cs <c>BuildModel</c>): the walkable distance
    /// (<see cref="DistanceField"/>, 8 u cells, through the air as through a corridor) from where the segment starts, or lands after a
    /// teleport in it (a tick more), to the next anchor's place (the last segment of a finished route: the finish; while a course's timer
    /// is off, by a start gate and on from where the route crossed it) over the top path speed with the segment's unlocks (the fastest
    /// <see cref="FullTas.SpeedWindow"/>-tick mean of the route's path and of fulltas's speed probes from every anchor). A model, not a
    /// proof: a peak above that mean beats it. Slack = the route's ticks - the floor, and the course timer's share of both (the timed
    /// floor: from where the timer starts in the segment); the timed segments by timed slack first, then the untimed ones by slack.
    /// </summary>
    static class TimeLossFloor
    {
        const string Usage = "usage: timeloss floor <trace.json> <route.json> [--json]";
        /// <summary>The field cell (plan's --field-cell default, fulltas --auto's bounds).</summary>
        const float Cell = 8f;

        /// <summary>One segment: anchors <see cref="Index"/> and the next, their input ticks and physics ticks, its ticks and the course
        /// timer's share of them, its floor and the timer's share of that (from where the timer starts in it, else its start), the
        /// distance and top speed the floor comes from.</summary>
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
            /// <summary>The course timer at the finish (0: not finished) and the run's physics ticks.</summary>
            public float Time;
            public long Ticks;
            public Dictionary<string, (float speed, string how)> Speeds;
            /// <summary>In route order.</summary>
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

        /// <summary>The route's segments with their floors, measured on a simulation of its own thread (the game's statics are per thread).</summary>
        internal static Result Measure(string tracePath, string routePath)
        {
            var (header, labels, inputs) = TechsUsed.Load(tracePath, routePath, "timeloss floor");
            if (labels != null) throw new ArgumentException("timeloss floor: a course trace; a Full-TAS run's per-goal bounds are fulltas --auto --minutes 0's");
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
            // The course timer's ticks up to a physics tick: from the last step it turns on in (a re-crossed start gate restarts it), as split counts them.
            long timerFrom = sim.TimedFromRunStart ? 0 : -1;
            for (int i = 0; i < record.Steps.Count && !sim.TimedFromRunStart; i++)
                if (record.Steps[i].Timing && (i == 0 || !record.Steps[i - 1].Timing)) timerFrom = record.Steps[i].Physics;
            long Timed(long physics) => timerFrom < 0 ? 0 : Math.Max(0, physics - timerFrom);
            // Segment i heads for anchor i + 1's place: field waypoint i, the finish itself (past the waypoints) for a finished route's last.
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

            // The field's grid covers every place asked about so far and coarsens as it grows: a first pass grows it over the whole
            // route, so the measured pass has one grid from start to end (as fulltas --auto's model does).
            bool gates = sim.Goals == null && !sim.TimedFromRunStart;
            Profile(sim, inputs, anchors, field, SegmentAt, gates);
            (List<FullTas.Sample> path, float[] dist, float[] gate) = Profile(sim, inputs, anchors, field, SegmentAt, gates);

            var speeds = new Dictionary<string, (float speed, string how)>();
            FullTas.Sustained(path, dt, speeds, "the route's path");
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
                // A course timer that starts in the segment starts at a start gate: the walk before it goes there first, then on from
                // where the route crossed it.
                int crossing = timerFrom > from.Physics && timerFrom < to.Physics ? path.FindIndex(x => x.Tick == timerFrom) : -1;
                float Walk(int k) => k < crossing && !float.IsNaN(gate[k]) ? gate[k] + Dist(crossing) : Dist(k);
                row.Caps = path[start].Caps;
                row.VTop = speeds.TryGetValue(row.Caps, out var v) ? Math.Max(1f, v.speed) : 1f;
                row.Distance = Walk(start);
                // Where the segment can be started from: its first tick, and after each teleport in it (a quick restart costs a tick).
                double best = row.Distance / row.VTop;
                for (int k = start + 1; k < path.Count && path[k].Tick < to.Physics; k++)
                    if (FullTas.Jumped(path[k - 1], path[k], dt)) best = Math.Min(best, 1 + Walk(k) / row.VTop);
                row.Floor = Math.Max(1, (long)Math.Ceiling(best));
                row.TimedFloor = row.Timed == 0 ? 0 : crossing < 0 ? row.Floor : Math.Max(1, (long)Math.Ceiling(Dist(crossing) / row.VTop));
            }
            return new Result
            {
                Route = name, Finished = run.Finished, Time = run.Finished ? run.Time : 0f, Ticks = anchors[segments].Physics, Speeds = speeds, Rows = rows,
            };
        }

        /// <summary>The route replayed: the player at the start and after every physics tick up to its last anchor (world place, speed
        /// |Velocity + momentum|, unlocks), and each one's walkable distance to its segment's target and, with <paramref name="gates"/>, to
        /// the course's start gates (NaN where the field has none).</summary>
        static (List<FullTas.Sample> path, float[] dist, float[] gate) Profile(Simulation sim, List<InputTick> inputs, List<Difficulty.Anchor> anchors,
            DistanceField field, Func<long, int> segmentAt, bool gates)
        {
            var path = new List<FullTas.Sample>();
            var dist = new List<float>();
            var gate = new List<float>();
            long end = anchors[anchors.Count - 1].Physics;
            float Finite(float d) => float.IsInfinity(d) ? float.NaN : d;
            void Add(long tick)
            {
                Movement m = sim.Player;
                WorldPos w = sim.PlayerWorld;
                CapturePos c = sim.PlayerAtCapture;
                path.Add(new FullTas.Sample
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

        /// <summary>fulltas --auto's speed probes (<see cref="FullTas.SpeedMoves"/>) from the route's state at an anchor, into the top speeds.</summary>
        static void Probe(Simulation sim, List<InputTick> inputs, Difficulty.Anchor at, float dt, Dictionary<string, (float speed, string how)> speeds)
        {
            if (at.State == null) return;
            sim.Restore(at.State);
            string caps = PlayerCaps.Of(sim).Id;
            foreach (var (moves, links) in FullTas.SpeedMoves(caps.Contains("om", StringComparison.OrdinalIgnoreCase)))
            {
                var s = new List<FullTas.Sample>();
                List<InputTick> probe = inputs.Take(at.Tick).Concat(moves).ToList();
                sim.Run(probe, at.State, null, 20, _ =>
                {
                    WorldPos w = sim.PlayerWorld;
                    s.Add(new FullTas.Sample { Tick = sim.Engine.PhysicsTicks, X = w.x, Y = w.y, Speed = (sim.Player.Velocity + sim.Player.momentum).magnitude, Caps = caps });
                });
                FullTas.Sustained(s, dt, speeds, $"speed probe from input tick {at.Tick}" + (links > 0 ? $", a {links}-link bounce chain" : ""));
            }
        }

        /// <summary>The timed segments by timed slack, most first, then the untimed ones by slack.</summary>
        internal static IEnumerable<Row> Ranked(Result r) => r.Rows.OrderByDescending(x => x.Timed > 0).ThenByDescending(x => x.Timed > 0 ? x.TimedSlack : x.Slack)
            .ThenBy(x => x.Index);

        internal static string Header(Result r)
        {
            return $"{r.Route}: " + (r.Finished ? $"finished, {r.Time.ToString("0.00", CultureInfo.InvariantCulture)} s on the course timer" : "not finished")
                + $", {r.Ticks} physics ticks, {r.Rows.Count} segments; top path speed (u/tick over {FullTas.SpeedWindow} ticks): "
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
