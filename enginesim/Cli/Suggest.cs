using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// "Optimize my route so far": the player after a route's first N input ticks is the target, and local edits of those
    /// ticks (<see cref="Humanizer"/>'s: each input shifted, each stick run at every other value, each jump press or dash
    /// dropped) that bring the player there after fewer input ticks are ranked by the ticks they save.
    /// </summary>
    static class Suggest
    {
        const int StateEvery = 2, Top = 20;
        static readonly float[] XValues = new[] { -1f, 0f, 1f }.Concat(Humanizer.Partials).Concat(Humanizer.Partials.Select(v => -v)).ToArray();
        static readonly float[] YValues = { -1f, 0f, 1f };

        struct State
        {
            public Vector2 Pos, Vel;
            public bool Ground;
        }

        /// <summary>Reached: within the radius of the target's position, at least its speed less 0.5 along its velocity (unless
        /// that is below 1), and grounded if it was. Speed: along the target's velocity (0 when it is below 1).</summary>
        sealed class Target
        {
            public State At;
            public float Radius;

            public bool Reached(State s, out float speed)
            {
                float v = At.Vel.magnitude;
                speed = v >= 1f ? Vector2.Dot(s.Vel, At.Vel / v) : 0f;
                return (s.Pos - At.Pos).sqrMagnitude <= Radius * Radius && (v < 1f || speed >= v - .5f) && (!At.Ground || s.Ground);
            }
        }

        sealed class Judge
        {
            readonly Simulation _sim;
            readonly Rigidbody2D _body;
            SimSnapshot[] _states;
            /// <summary>The route's player after each of its first N input ticks (index: input ticks done; null where no step ended one).</summary>
            public State?[] Path;

            public Judge(Func<Simulation> create)
            {
                _sim = create();
                _body = _sim.Player.GetComponent<Rigidbody2D>();
            }

            State Now()
            {
                WorldPos p = _sim.PlayerWorld;
                return new State { Pos = new Vector2(p.x, p.y), Vel = _sim.Physics.GetVelocity(_body), Ground = _sim.Player.onGround };
            }

            /// <summary>The first <paramref name="n"/> of <paramref name="inputs"/> (one more after them: the player after an input
            /// differs when none follows, as the engine stops, and every edit's arrival before n has one).</summary>
            public void Record(List<InputTick> inputs, int n)
            {
                if (Path != null) return;
                var path = new State?[n + 1];
                var states = new SimSnapshot[n];
                RunResult r = _sim.Run(inputs, null, tick =>
                {
                    if (tick < states.Length && tick % StateEvery == 0) states[tick] = _sim.Capture(tick);
                    if (tick < path.Length) path[tick] = Now();
                    return tick < n;
                }, extraTicks: 1000000);   // a dash's freeze ticks take no input
                if (path[n] == null)
                    throw new InvalidOperationException($"the route doesn't reach input tick {n} offline ("
                        + (r.Finished ? $"it finishes at input tick {r.FinishTick}: polish improves a finished route" : r.EndReason ?? "it stops") + ")");
                _states = states;
                Path = path;
            }

            /// <summary>The first input tick before <paramref name="limit"/> after which the inputs' player has reached the target, and
            /// its speed and position there (-1: none), replayed from the route's last recorded state at or before <paramref name="first"/>
            /// (the first input that differs from the route's) or from the start.</summary>
            public (int at, float speed, Vector2 pos) Arrival(List<InputTick> inputs, int first, Target target, int limit, bool fromStart = false)
            {
                int s = fromStart ? 0 : Math.Min(first, _states.Length - 1);
                while (s > 0 && _states[s] == null) s--;
                (int, float, Vector2) arrival = (-1, 0f, default);
                _sim.Run(inputs, s > 0 ? _states[s] : null, tick =>
                {
                    if (tick >= limit) return false;
                    State now = Now();
                    if (!target.Reached(now, out float v)) return true;
                    arrival = (tick, v, now.Pos);
                    return false;
                }, extraTicks: 1000000);
                return arrival;
            }
        }

        sealed class Edit
        {
            public List<InputTick> Inputs;
            public int First, At;
            public float Speed;
            public Vector2 Pos;
            public string Kind, What;
        }

        /// <summary>Every input of <paramref name="prefix"/> shifted 1..<paramref name="maxShift"/> ticks either way, every stick run at
        /// every other value, every jump press (with its hold) and dash dropped, as <see cref="Humanizer"/> keeps them real inputs.</summary>
        static List<Edit> Edits(List<InputTick> prefix, int maxShift)
        {
            var edits = new List<Edit>();
            List<Humanizer.Event> events = Humanizer.Events(prefix, prefix);
            // A stick at rest from the start is no input to Events; its run takes the other values too (a first tilt from tick 0).
            foreach (bool y in new[] { false, true })
            {
                int end = 0;
                while (end < prefix.Count && (y ? prefix[end].y : prefix[end].x) == 0f) end++;
                if (end > 0) events.Add(new Humanizer.Event { Kind = y ? Humanizer.Kind.StickY : Humanizer.Kind.StickX, Tick = 0, End = end, Start = true });
            }
            foreach (Humanizer.Event e in events)
            {
                bool y = e.Kind == Humanizer.Kind.StickY, stick = y || e.Kind == Humanizer.Kind.StickX;
                string name = stick ? $"stick {(y ? "y" : "x")} {Humanizer.Num(e.Value)} at" : e.Kind == Humanizer.Kind.Press ? "jump press"
                    : e.Kind == Humanizer.Kind.Release ? "jump release" : "dash";
                if (!e.Start)
                    for (int d = -maxShift; d <= maxShift; d++)
                        if (d != 0 && Humanizer.Shift(prefix, e, d, out int first) is List<InputTick> shifted)
                            edits.Add(new Edit { Inputs = shifted, First = first, Kind = "shift", What = $"{name} {e.Tick} -> {e.Tick + d}" });
                if (stick)
                    foreach (float v in (y ? YValues : XValues).Where(v => v != e.Value))
                        edits.Add(new Edit
                        {
                            Inputs = Humanizer.SetStick(prefix, y, e.Tick, e.End, v), First = e.Tick, Kind = "value",
                            What = $"stick {(y ? "y" : "x")} {(e.End - e.Tick == 1 ? "tick " + e.Tick : $"ticks {e.Tick}-{e.End - 1}")} {Humanizer.Num(e.Value)} -> {Humanizer.Num(v)}",
                        });
                else if (e.Kind == Humanizer.Kind.Press)
                    edits.Add(new Edit { Inputs = Humanizer.DropPress(prefix, e.Tick), First = e.Tick, Kind = "drop", What = $"{name} {e.Tick} dropped" });
                else if (e.Kind == Humanizer.Kind.Dash)
                    edits.Add(new Edit { Inputs = Humanizer.DropDash(prefix, e.Tick), First = e.Tick, Kind = "drop", What = $"{name} {e.Tick} dropped" });
            }
            return edits;
        }

        public static int Run(string[] args)
        {
            if (args.Length < 2 || !args.Contains("--until")) throw new ArgumentException("suggest <start-trace.json> <route.json> --until N [--out file] [--threads T] [--radius R] [--max-shift S]");
            string trace = args[0], routePath = args[1];
            int until = Solve.Option(args, "--until", -1), threads = Solve.Option(args, "--threads", Environment.ProcessorCount),
                maxShift = Solve.Option(args, "--max-shift", Humanizer.Radius);
            float radius = Solve.Option(args, "--radius", 4f);
            string outPath = Solve.Option(args, "--out",
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(routePath)), Path.GetFileNameWithoutExtension(routePath) + "-suggest.json"));
            List<InputTick> route = Plan.ReadRoute(routePath);
            if (until < 1 || until > route.Count) throw new ArgumentException($"--until must be 1..{route.Count} (the route's input ticks)");
            var clock = Stopwatch.StartNew();
            List<InputTick> prefix = route.Take(until).ToList();
            List<InputTick> played = prefix.Append(until < route.Count ? route[until] : route[until - 1].Coast()).ToList();
            List<Edit> edits = Edits(prefix, maxShift);
            Func<Simulation> create = Solve.Creator(trace, Solve.ReadHeader(trace));
            var workers = Enumerable.Range(0, Math.Max(1, Math.Min(threads, edits.Count))).Select(_ => new Worker<Judge>(() => new Judge(create), "suggest")).ToList();
            try
            {
                State?[] path = null;
                workers[0].Do(j => { j.Record(played, until); path = j.Path; });
                var target = new Target { At = path[until].Value, Radius = radius };
                // The route's own first arrival (at the latest the target itself): an edit counts only if it arrives sooner, so it
                // changes an input before then.
                int baseline = Enumerable.Range(0, until + 1).First(t => path[t] is State s && target.Reached(s, out _));
                List<Edit> tests = edits.Where(e => e.First < baseline - 1).ToList();
                int next = -1;
                Exception failure = null;
                using (var done = new CountdownEvent(workers.Count))
                {
                    foreach (Worker<Judge> w in workers)
                        w.Post(j =>
                        {
                            try
                            {
                                j.Record(played, until);
                                for (int i; (i = Interlocked.Increment(ref next)) < tests.Count;)
                                    (tests[i].At, tests[i].Speed, tests[i].Pos) = j.Arrival(tests[i].Inputs, tests[i].First, target, baseline);
                            }
                            catch (Exception ex) { failure = ex; }
                            finally { done.Signal(); }
                        });
                    done.Wait();
                }
                if (failure != null) throw new InvalidOperationException("suggest replay failed", failure);
                List<Edit> found = tests.Where(e => e.At >= 0).ToList();
                if (baseline < until)
                {
                    target.Reached(path[baseline].Value, out float speed);
                    found.Add(new Edit { Inputs = prefix, First = baseline, At = baseline, Speed = speed, Pos = path[baseline].Value.Pos, Kind = "cut", What = $"ticks {baseline}-{until - 1} cut (the route is there after {baseline} already)" });
                }
                // Ranked; the same ticks, or the same first change arriving the same (another stick value that plays alike), once;
                // each checked from the start (one that arrives otherwise from there isn't kept).
                var kept = new List<Edit>();
                foreach (Edit f in found.OrderBy(f => f.At).ThenByDescending(f => f.Speed))
                {
                    if (kept.Count == Top) break;
                    if (kept.Any(k => k.At == f.At && (k.First == f.First && k.Speed == f.Speed && k.Pos == f.Pos
                        || k.Inputs.Take(f.At).SequenceEqual(f.Inputs.Take(f.At))))) continue;
                    int check = -1;
                    workers[0].Do(j => check = j.Arrival(f.Inputs, 0, target, f.At + 1, fromStart: true).at);
                    if (check != f.At) { Console.Error.WriteLine($"{f.What}: arrives after {f.At} from a recorded state, {check} from the start: not kept"); continue; }
                    kept.Add(f);
                }
                State t = target.At;
                Solve.WriteShared(outPath, JsonSerializer.Serialize(new
                {
                    until,
                    target = new { x = t.Pos.x, y = t.Pos.y, vx = t.Vel.x, vy = t.Vel.y, grounded = t.Ground },
                    tried = tests.Count,
                    suggestions = kept.Select(k => new
                    {
                        description = k.What, kind = k.Kind, saved = until - k.At, arrival = k.At, speed = k.Speed, @from = k.First,
                        ticks = k.Inputs.Take(k.At).Concat(route.Skip(until)).Select(x => x.ToJson()),
                    }),
                }));
                Console.WriteLine($"suggest: until {until}, target ({t.Pos.x:0.0}, {t.Pos.y:0.0}) v ({t.Vel.x:0.0}, {t.Vel.y:0.0}){(t.Ground ? " grounded" : "")}, "
                    + $"{tests.Count} tried, {found.Count} arrive sooner, {kept.Count} kept"
                    + (kept.Count > 0 ? $", best: {kept[0].What} saves {until - kept[0].At}" : "")
                    + $" ({clock.Elapsed.TotalSeconds:0.0}s, {workers.Count} threads) -> {outPath}");
            }
            finally
            {
                foreach (Worker<Judge> w in workers) w.Stop();
            }
            return 0;
        }
    }
}
