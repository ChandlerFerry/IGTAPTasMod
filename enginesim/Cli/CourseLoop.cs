using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityEngine;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using FullTasTracker = IGTAPTasMod.FullTasTracker;
using MacroEngine = IGTAPTasMod.MacroEngine;

namespace IGTAP.EngineSim.Cli
{
    static partial class CourseAuto
    {
        internal const int LoopRepeats = 4;
        const int MaxLoopLaps = 3;
        // Raised tick cap: dash ticks take no input but count, so multi-lap dash routes outrun inputs + a few.
        const int AnyTicks = 1000000;

        sealed class LoopCycle
        {
            public bool Steady;
            public long Ticks, Held;
            public int Laps = 1;
            public long Live { get { return MacroEngine.LiveUnits(Ticks, Held); } }
            public double Lap { get { return MacroEngine.LiveSeconds(Ticks, Held) / Laps; } }
            public int Settled;
            public readonly List<long> Cycles = new List<long>();
            public BusEdge Finish;
            public string Broke;

            public override string ToString()
            {
                string cycles = string.Join(", ", Cycles.Select(c => c < 0 ? "-" : (c / 1400.0).ToString("0.0000", CultureInfo.InvariantCulture)));
                string laps = Laps == 1 ? "" : $" a lap of {Laps}";
                return Steady ? $"steady {Lap.ToString("0.0000", CultureInfo.InvariantCulture)}s{laps} ({Ticks} ticks, {Held} held a loop) after {Settled} loops (cycles {cycles})"
                    : $"not steady{laps} (cycles {cycles}{(Broke == null ? "" : "; " + Broke)})";
            }
        }

        sealed class LoopRoute
        {
            public List<InputTick> Lead, Loop;
            public int LeadLaps, LoopLaps = 1;
            public List<InputTick> FirstLap;
            public long FirstTicks;
            public List<InputTick> Inputs { get { return Lead.Concat(Loop ?? new List<InputTick>()).ToList(); } }
        }

        static Simulation LoopSim(CourseRun run, int finishes)
        {
            Simulation sim = Solve.Creator(run.Trace, CourseWaypoints.GoalHeader(run.Header))();
            FullTasGoal[] goals = Enumerable.Range(0, finishes).Select(_ => FinishGoal(run.Course)).ToArray();
            var tracker = new FullTasTracker(goals);
            SimSnapshot start = sim.Advance(new List<InputTick>());
            sim.UseGoals(tracker);
            sim.SetStart(start.WithGoal(tracker.Capture()), 0);
            return sim;
        }

        static (List<InputTick> inputs, List<int> cuts, long first) Laps(CourseRun run, List<InputTick> inputs)
        {
            const int MaxLaps = 16;
            List<InputTick> idled = inputs.Concat(Enumerable.Repeat(new InputTick(), 60)).ToList();
            RunResult r = Solve.OnThread(() => LoopSim(run, MaxLaps).Run(idled, null, null, AnyTicks));
            return (idled, Enumerable.Range(0, r.Progress).Select(k => (int)r.GoalInputTicks[k]).ToList(), r.Progress == 0 ? 0 : r.GoalPhysicsTicks[0]);
        }

        static LoopRoute AsLoop((List<InputTick> inputs, List<int> cuts, long first) l, int laps)
        {
            int n = l.cuts.Count, from = l.cuts[n - 1 - laps], to = l.cuts[n - 1];
            return new LoopRoute { Lead = l.inputs.GetRange(0, from), LeadLaps = n - laps, Loop = l.inputs.GetRange(from, to - from), LoopLaps = laps,
                FirstLap = l.inputs.GetRange(0, l.cuts[0]), FirstTicks = l.first };
        }

        static LoopCycle MeasureLoop(CourseRun run, LoopRoute route)
        {
            var inputs = new List<InputTick>(route.Lead);
            for (int i = 0; i < LoopRepeats; i++) inputs.AddRange(route.Loop);
            int p = route.LoopLaps;
            return Solve.OnThread(() =>
            {
                Simulation sim = LoopSim(run, route.LeadLaps + LoopRepeats * p);
                Collider2D[] hitbox = sim.Player.GetComponents<BoxCollider2D>();
                List<Collider2D> ends = GateColliders(sim, run.Course, false);
                var ticks = new List<long>();
                var toEnd = new List<float>();
                var body = new List<(float x, float y)>();
                var restarts = new List<Vector2>();
                var carried = new List<Dictionary<string, string>>();
                RunResult r = sim.Run(inputs, null, null, AnyTicks, _ =>
                {
                    while (restarts.Count < sim.Progress) restarts.Add(sim.RestartPoint);
                    long into = sim.Engine.RunTicks - route.Lead.Count - 1;
                    if (into >= 0 && into % route.Loop.Count == 0 && carried.Count == into / route.Loop.Count) carried.Add(Fields(sim.Player));
                    ticks.Add(sim.Engine.PhysicsTicks);
                    toEnd.Add(Nearest(sim, hitbox, ends));
                    var b = sim.Physics.PlayerBody();
                    ScenePos origin = sim.WorldOriginInScene;
                    body.Add((b.px - origin.x, b.py - origin.y));
                });
                var c = new LoopCycle { Laps = p };
                for (int k = 1; k <= LoopRepeats; k++)
                {
                    int g = route.LeadLaps - 1 + k * p, s = g - p;
                    bool on = (k == 1 || c.Cycles[k - 2] >= 0) && g < r.Progress && r.GoalInputTicks[g] == route.Lead.Count + (long)k * route.Loop.Count;
                    c.Cycles.Add(on ? MacroEngine.LiveUnits(r.GoalPhysicsTicks[g] - r.GoalPhysicsTicks[s], r.GoalHeldFrames[g] - r.GoalHeldFrames[s]) : -1);
                    if (!on)
                    {
                        long own = route.Lead.Count + (long)k * route.Loop.Count;
                        c.Broke ??= (g >= r.Progress ? $"loop {k}: {r.EndReason} at goal {r.Progress + 1}"
                            : $"loop {k} finished on input {r.GoalInputTicks[g]}, its last is {own}")
                            + $"; restarts after each finish at {string.Join(" ", restarts.Select(v => $"({v.x:0},{v.y:0})"))}"
                            + (carried.Count < 2 ? "" : "; loop 2 starts unlike loop 1 in " + string.Join(", ", carried[0].Keys
                                .Where(f => carried[1].TryGetValue(f, out string v) && v != carried[0][f]).Select(f => $"{f} {carried[0][f]} -> {carried[1][f]}")));
                        continue;
                    }
                    c.Ticks = r.GoalPhysicsTicks[g] - r.GoalPhysicsTicks[s];
                    c.Held = r.GoalHeldFrames[g] - r.GoalHeldFrames[s];
                    int at = ticks.IndexOf(r.GoalPhysicsTicks[g]);
                    c.Finish = at < 1 ? null : new BusEdge { Tick = ticks[at], Before = toEnd[at - 1], At = toEnd[at],
                        Travel = (float)Math.Sqrt(Math.Pow(body[at].x - body[at - 1].x, 2) + Math.Pow(body[at].y - body[at - 1].y, 2)) };
                }
                long last = c.Cycles[LoopRepeats - 1];
                c.Steady = last >= 0 && c.Cycles[LoopRepeats - 2] == last;
                if (c.Steady)
                {
                    c.Settled = LoopRepeats - 1;
                    while (c.Settled > 0 && c.Cycles[c.Settled - 1] == last) c.Settled--;
                }
                return c;
            });
        }

        static Dictionary<string, string> Fields(Movement m)
        {
            var fields = new Dictionary<string, string>();
            foreach (System.Reflection.FieldInfo f in typeof(Movement).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(Vector2) || f.FieldType == typeof(Vector3))
                    fields[f.Name] = Convert.ToString(f.GetValue(m), CultureInfo.InvariantCulture);
            return fields;
        }

        static LoopRoute SettleLoop(LoopRoute route, LoopCycle cycle)
        {
            if (cycle.Settled == 0) return route;
            var lead = new List<InputTick>(route.Lead);
            for (int i = 0; i < cycle.Settled; i++) lead.AddRange(route.Loop);
            return new LoopRoute { Lead = lead, LeadLaps = route.LeadLaps + cycle.Settled * route.LoopLaps, Loop = route.Loop, LoopLaps = route.LoopLaps,
                FirstLap = route.FirstLap, FirstTicks = route.FirstTicks };
        }

        static (List<InputTick> steady, List<InputTick> fastest) SearchLoopLap(CourseRun run, LoopRoute route, double minutes, int threads)
        {
            List<InputTick> lead = route.Lead;
            int laps = route.LeadLaps;
            Simulation Create() => AfterLead(run, route);
            List<InputTick> lap = route.FirstLap;
            List<InputTick> Idle(int n) => Enumerable.Repeat(new InputTick(), n).ToList();
            var seeds = new List<List<InputTick>>();
            if (route.Loop != null) foreach (int k in new[] { 0, 1, 2 }) seeds.Add(Idle(k).Concat(route.Loop).ToList());
            // A lap never starts on its quick restart: it fires inside the finish's no-input ticks and cuts the prior lap short.
            foreach (int k in new[] { 0, 10, 20, 30, 40 }) seeds.Add(new[] { new InputTick(), new InputTick { reset = true } }.Concat(Idle(k)).Concat(lap).ToList());
            foreach (int k in new[] { 20, 30, 40, 50 }) seeds.Add(Idle(k).Concat(lap).ToList());
            float face = Math.Sign(lap.Select(t => t.x).FirstOrDefault(x => x != 0f));
            if (lap.Count > 0)
                foreach (int a in new[] { 1, 2 })
                    for (int b = 2; b <= 12; b++)
                    {
                        InputTick restart = lap[0];
                        restart.reset = true;
                        seeds.Add(Enumerable.Repeat(new InputTick { x = face == 0f ? 1f : face }, a).Append(new InputTick { reset = true })
                            .Concat(Idle(b)).Append(restart).Concat(lap.Skip(1)).ToList());
                    }
            if (lap.Count > 0 && Solve.OnThread(() => WayBack(run, Create)) is List<InputTick> back)
                for (int w = 0; w < 5; w++)
                {
                    InputTick restart = lap[0];
                    restart.reset = true;
                    seeds.Add(back.Concat(Idle(w)).Append(restart).Concat(lap.Skip(1)).ToList());
                }
            List<string> played = Solve.OnThread(() =>
            {
                Simulation sim = Create();
                return seeds.Select(s => { RunResult r = sim.Run(s, null, null, 60); return r.Finished ? $"finish at tick {r.PhysicsTicks}" : r.EndReason; }).ToList();
            });
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap seeds: {string.Join("; ", played)}");
            if (!played.Any(p => p.StartsWith("finish", StringComparison.Ordinal))) minutes = Math.Min(minutes, 2.0);
            var solver = new OfflineSolver(Create, run.Caps, seeds, null, run.Rules) { Farm = true };
            List<InputTick> found = null;
            long foundLive = long.MaxValue;
            var guard = new object();
            solver.Improved += (time, live, inputs, how) =>
            {
                lock (guard) if (live < foundLive) (found, foundLive) = (inputs, live);
                Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: finish at {live / 1400.0:0.0000}s live from the course start ({time:0.00}s on the course timer) via {how}");
            };
            // Check many finishing laps: a quick restart keeps lap history (CourseStart.cs), so the fastest lap can die on replay.
            var pool = new List<(float time, List<InputTick> inputs)>();
            solver.FinishedRun = (inputs, time) =>
            {
                lock (pool)
                {
                    if (pool.Count >= PoolSize && time >= pool[pool.Count - 1].time) return;
                    int at = pool.FindIndex(p => p.time > time);
                    pool.Insert(at < 0 ? pool.Count : at, (time, inputs));
                    if (pool.Count > PoolSize) pool.RemoveAt(pool.Count - 1);
                }
            };
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: searching Course {run.Course} ({run.Caps}) from the lead's finish {laps} "
                + $"({lead.Count} inputs) for {minutes:0.##} min on {threads} threads");
            var clock = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            Thread[] workers = solver.Start(threads);
            List<InputTick> steady = null, seen = null;
            long steadyLive = long.MaxValue;
            bool fastestSteady = false;
            var checkedLaps = new HashSet<string>();
            int pooled = 0;
            bool Check(List<InputTick> f, string what)
            {
                List<InputTick> cut = CutAtFinish(run, route, f);
                if (cut == null || !checkedLaps.Add(System.Text.Json.JsonSerializer.Serialize(cut.Select(t => t.ToJson())))) return false;
                LoopCycle c = MeasureLoop(run, new LoopRoute { Lead = lead, LeadLaps = laps, Loop = cut, FirstLap = route.FirstLap, FirstTicks = route.FirstTicks });
                bool faster = c.Steady && c.Live < steadyLive;
                if (what != null || faster) Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: {what ?? "a finishing lap"} as a loop of itself: {c}");
                if (!faster) return true;
                long live0 = c.Live;
                LoopRoute Of(List<InputTick> l) => new LoopRoute { Lead = lead, LeadLaps = laps, Loop = l, FirstLap = route.FirstLap, FirstTicks = route.FirstTicks };
                cut = OfflineSolver.DropIdlePauses(cut, l => { LoopCycle d = MeasureLoop(run, Of(l)); if (!d.Steady || d.Live > c.Live) return false; c = d; return true; });
                if (c.Live < live0) Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: its pauses shortened: {c}");
                (steady, steadyLive) = (cut, c.Live);
                return true;
            }
            while (clock.Elapsed.TotalMinutes < minutes && !(steady != null && quiet.Elapsed.TotalSeconds >= SteadyQuietSeconds))
            {
                Thread.Sleep(250);
                List<InputTick> f;
                long live;
                lock (guard) (f, live) = (found, foundLive);
                if (f != null && f != seen)
                {
                    seen = f;
                    quiet.Restart();
                    long before = steadyLive;
                    Check(f, $"the {live / 1400.0:0.0000}s lap");
                    fastestSteady = steadyLive < before;
                }
                var beat = Stopwatch.StartNew();
                while (beat.ElapsedMilliseconds < 250)
                {
                    List<InputTick> next;
                    lock (pool)
                    {
                        if (pool.Count == 0) break;
                        next = pool[0].inputs;
                        pool.RemoveAt(0);
                    }
                    long before = steadyLive;
                    if (Check(next, null)) pooled++;
                    if (steadyLive < before) quiet.Restart();
                }
            }
            solver.Stop();
            foreach (Thread t in workers) t.Join(60000);
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: {Interlocked.Read(ref solver.Runs)} runs, {Interlocked.Read(ref solver.Finishes)} finishes, {pooled} finishing laps checked, {solver.GuideStatus()}");
            return (steady, found == null || fastestSteady ? null : CutAtFinish(run, route, found));
        }

        // Build before create's simulation: the last Simulation built on a thread owns its engine hooks.
        static List<InputTick> WayBack(CourseRun run, Func<Simulation> create)
        {
            Simulation start = LoopSim(run, 1);
            Vector2 home = start.Player.respawnPoint;
            float homeX = start.PlayerWorld.x;
            Simulation sim = create();
            sim.Run(new List<InputTick> { new InputTick(), new InputTick { reset = true }, new InputTick() }, null, null, 0);
            float dir = Math.Sign(homeX - sim.PlayerWorld.x);
            var walk = new List<InputTick> { new InputTick(), new InputTick { reset = true } };
            walk.AddRange(Enumerable.Repeat(new InputTick { x = dir == 0f ? 1f : dir }, 600));
            int touched = -1, landed = -1;
            sim.Run(walk, null, null, 0, _ =>
            {
                if (landed >= 0) return;
                int t = (int)sim.Engine.RunTicks;
                if (touched < 0 && t > 0 && sim.Player.respawnPoint == home) touched = t;
                if (touched >= 0 && sim.Player.onGround && sim.Player.airDashesLeft == sim.Player.maxAirDashes) landed = t;
            });
            return landed < 0 ? null : walk.GetRange(0, Math.Min(walk.Count, landed + 1));
        }

        const int PoolSize = 256;

        const double SteadyQuietSeconds = 300;

        static Simulation AfterLead(CourseRun run, LoopRoute route)
        {
            Simulation sim = LoopSim(run, route.LeadLaps + 1);
            SimSnapshot at = sim.Advance(route.Lead);
            // Lead finishing in no-input ticks: Advance stops before them; take the state at the finish, next input the lap's first.
            if (sim.Progress < route.LeadLaps)
                at = sim.AdvanceToGoal(route.Lead.Concat(Enumerable.Repeat(new InputTick(), 60)).ToList(), route.LeadLaps, out _, inLock: true) ?? at;
            sim.SetStart(at, route.Lead.Count);
            sim.UseGoals(null);
            sim.TargetCourse = run.Course;
            return sim;
        }

        // From a lead state inside a no-input stretch, the lap run's finish tick is one input off; cut where that run counts it.
        static List<InputTick> CutAtFinish(CourseRun run, LoopRoute route, List<InputTick> lap)
        {
            List<InputTick> idled = lap.Concat(Enumerable.Repeat(new InputTick(), 60)).ToList();
            RunResult r = Solve.OnThread(() => LoopSim(run, route.LeadLaps + 1).Run(route.Lead.Concat(idled).ToList(), null, null, AnyTicks));
            if (r.Progress <= route.LeadLaps) return null;
            int n = (int)(r.GoalInputTicks[route.LeadLaps] - route.Lead.Count);
            return n > 0 && n <= idled.Count ? idled.GetRange(0, n) : null;
        }

        static List<InputTick> RepairLoopLap(CourseRun run, LoopRoute route, WaypointOptions options, string dir, double minutes)
        {
            List<InputTick> lap = route.Loop ?? new[] { new InputTick(), new InputTick { reset = true } }.Concat(route.FirstLap).ToList();
            List<FullTasGoal> goals = Enumerable.Range(0, route.LeadLaps).Select(_ => FinishGoal(run.Course))
                .Concat(CourseWaypoints.Build(run.Trace, route.FirstLap, options).Goals.Where(g => g.kind == FullTasGoal.At))
                .Append(FinishGoal(run.Course)).ToList();
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: repairing the lap before from where it breaks, through {goals.Count - route.LeadLaps - 1} "
                + $"waypoints to the finish, for {minutes:0.##} min");
            List<InputTick> best = Reach(run, dir, run.Name + "-lap", goals, route.Lead.Concat(lap).ToList(), run.Rules, minutes, false);
            if (best == null)
            {
                Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: the repair reached no finish");
                return null;
            }
            return CutAtFinish(run, route, best.Skip(route.Lead.Count).ToList());
        }

        static (LoopRoute route, LoopCycle cycle, string how)? StartLoop(CourseRun run, List<(string how, List<InputTick> inputs)> candidates, double lapMinutes,
            int threads, bool search, WaypointOptions options, string dir)
        {
            (LoopRoute route, LoopCycle cycle, string how)? best = null;
            (LoopRoute route, string how)? lead = null;
            void Consider(LoopRoute r, LoopCycle c, string how)
            {
                if (c.Steady && (best == null || c.Lap < best.Value.cycle.Lap - 1e-9)) best = (SettleLoop(r, c), c, how);
            }
            foreach (var (how, inputs) in candidates)
            {
                var laps = Laps(run, inputs);
                if (laps.cuts.Count == 0) continue;
                if (lead == null || laps.first < lead.Value.route.FirstTicks)
                {
                    List<InputTick> first = laps.inputs.GetRange(0, laps.cuts[0]);
                    lead = (new LoopRoute { Lead = first, LeadLaps = 1, FirstLap = first, FirstTicks = laps.first }, how);
                }
                for (int p = 1; p <= Math.Min(MaxLoopLaps, laps.cuts.Count - 1); p++)
                {
                    LoopRoute r = AsLoop(laps, p);
                    LoopCycle c = MeasureLoop(run, r);
                    Console.WriteLine($"{how}: farm loop after {r.LeadLaps} lead laps, {c}");
                    if (!c.Steady) continue;
                    Consider(r, c, how);
                    break;
                }
            }
            if (!search || lead == null) return best;
            LoopRoute start = best?.route ?? lead.Value.route, at = start;
            string from = "loop lap search from " + (best?.how ?? lead.Value.how);
            var found = new List<List<InputTick>>();
            var clock = Stopwatch.StartNew();
            for (int attempt = 0; attempt < 2 * MaxLoopLaps; attempt++)
            {
                double left = lapMinutes - clock.Elapsed.TotalMinutes;
                if (left < .25) break;
                var (steady, fastest) = SearchLoopLap(run, at, attempt == 0 ? left / 2 : left, threads);
                if (steady != null)
                {
                    var r = new LoopRoute { Lead = at.Lead, LeadLaps = at.LeadLaps, Loop = steady, FirstLap = at.FirstLap, FirstTicks = at.FirstTicks };
                    Consider(r, MeasureLoop(run, r), from);
                }
                List<InputTick> lap = fastest ?? (best == null && steady == null ? RepairLoopLap(run, at, options, dir, left) : null);
                if (lap == null) break;
                found.Add(lap);
                for (int p = 1; p <= found.Count && p <= MaxLoopLaps; p++)
                {
                    var lead2 = new List<InputTick>(start.Lead);
                    foreach (List<InputTick> l in found.Take(found.Count - p)) lead2.AddRange(l);
                    var r = new LoopRoute { Lead = lead2, LeadLaps = start.LeadLaps + found.Count - p, Loop = found.Skip(found.Count - p).SelectMany(l => l).ToList(), LoopLaps = p,
                        FirstLap = start.FirstLap, FirstTicks = start.FirstTicks };
                    LoopCycle c = MeasureLoop(run, r);
                    Console.WriteLine($"loop of the last {p} laps searched, after {r.LeadLaps} lead laps: {c}");
                    Consider(r, c, from);
                }
                at = new LoopRoute { Lead = at.Lead.Concat(lap).ToList(), LeadLaps = at.LeadLaps + 1, Loop = lap, FirstLap = at.FirstLap, FirstTicks = at.FirstTicks };
            }
            return best;
        }

        static FullTasGoal FinishGoal(int course) { return new FullTasGoal { kind = FullTasGoal.Farm, course = course, label = "farm:" + course }; }

        static int LoopAuto(CourseRun run, string[] args, List<(string how, List<InputTick> inputs)> candidates, string dir)
        {
            run.Rules.OnCourseOnly = true;
            double minutes = Solve.Option(args, "--minutes", 60.0);
            int threads = Solve.Option(args, "--threads", Math.Max(1, Environment.ProcessorCount - 4));
            WaypointOptions options = WaypointOptions.Parse(args, line => Console.WriteLine(line));
            options.Record = false;
            var start = StartLoop(run, candidates, Math.Max(0.25, minutes / 2), threads, run.WaypointsFile == null, options, dir);
            if (start == null)
            {
                if (run.WaypointsFile != null)
                    throw new InvalidOperationException("No farm loop yet: Improve farm first searches a lap from the finish back to the finish.");
                Console.WriteLine("--auto: no farm loop that repeats exactly (a lap from the finish back to the finish)");
                return 1;
            }
            var (route, cycle, how) = start.Value;
            if (run.WaypointsFile == null) WriteStartingLoop(run, route, cycle, candidates.Any(c => c.how == how) ? "start: " + how : how);
            List<FullTasGoal> lap = LoopLapWaypoints(run, route, options);
            run.Goals = LoopGoals(run, route, lap);
            if (run.WaypointsFile != null)
            {
                WriteLoopWaypoints(run, route, cycle);
                return 0;
            }
            string synthetic = WriteCourseTrace(run, dir, route.Inputs);
            Solve.CourseRuns[run.Name] = run.Rules;
            Console.WriteLine($"Course {run.Course} ({run.Caps}) --auto, farm loop timing, from {how}: {cycle}; {route.LeadLaps} lead laps, "
                + $"{run.Goals.Count} goals ({run.Goals.Count(g => g.kind == FullTasGoal.At)} waypoints on the loop): {string.Join(" ", run.Goals.Select(g => g.label))}");
            (int from, int to) = Span(args, run.Goals.Count, route.LeadLaps);
            int leadLaps = route.LeadLaps, loopLaps = route.LoopLaps;
            string runFile = System.IO.Path.Combine(dir, "fulltas", run.Name + ".json");
            Polish(run, synthetic, from, to, minutes - run.Clock.Elapsed.TotalMinutes, false, inputs => LoopOf(run, inputs, leadLaps, loopLaps).cycle is { Steady: true } c ? c.Live : long.MaxValue,
                (inputs, kept) =>
                {
                    var (r, c) = LoopOf(run, inputs, leadLaps, loopLaps);
                    WriteLoopBest(run, r, c, "auto: " + kept);
                    Solve.WriteShared(runFile, System.Text.Json.JsonSerializer.Serialize(new { inputs = r.Inputs.Select(t => t.ToJson()) }));
                    WriteLoopWaypoints(run, r, c);
                });
            return 0;
        }

        static (LoopRoute route, LoopCycle cycle) LoopOf(CourseRun run, List<InputTick> inputs, int leadLaps, int loopLaps)
        {
            var l = Laps(run, inputs);
            if (l.cuts.Count < leadLaps + loopLaps) return (null, new LoopCycle());
            LoopRoute r = AsLoop((l.inputs, l.cuts.Take(leadLaps + loopLaps).ToList(), l.first), loopLaps);
            return (r, MeasureLoop(run, r));
        }

        static List<FullTasGoal> LoopLapWaypoints(CourseRun run, LoopRoute route, WaypointOptions options)
        {
            List<FullTasGoal> leadFinishes = Enumerable.Range(0, route.LeadLaps).Select(_ => FinishGoal(run.Course)).ToList();
            List<InputTick> inputs = route.Inputs;
            try
            {
                return CourseWaypoints.Build(run.Trace, run.Header, AfterLead(run, route), route.Loop, options,
                    goals => CourseWaypoints.Check(run.Trace, inputs, leadFinishes.Concat(goals).ToList()).Skip(route.LeadLaps).ToArray())
                    .Goals.Where(g => g.kind == FullTasGoal.At).ToList();
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"loop waypoints: the loop's own lap gave none ({ex.Message}); the first lap's instead");
                return CourseWaypoints.Build(run.Trace, route.FirstLap, options).Goals.Where(g => g.kind == FullTasGoal.At).ToList();
            }
        }

        static List<FullTasGoal> LoopGoals(CourseRun run, LoopRoute route, List<FullTasGoal> lap)
        {
            var kept = new List<FullTasGoal>(lap);
            List<InputTick> inputs = route.Inputs;
            while (true)
            {
                List<FullTasGoal> goals = Enumerable.Range(0, route.LeadLaps).Select(_ => FinishGoal(run.Course))
                    .Concat(Enumerable.Range(0, route.LoopLaps).SelectMany(_ => kept.Append(FinishGoal(run.Course)))).ToList();
                long[] reached = CourseWaypoints.Check(run.Trace, inputs, goals);
                int bad = Enumerable.Range(route.LeadLaps, goals.Count - route.LeadLaps)
                    .Where(k => goals[k].kind == FullTasGoal.At && (reached[k] < 0 || reached[k] - reached[k - 1] < 2)).DefaultIfEmpty(-1).First();
                if (bad < 0 && reached[goals.Count - 1] >= 0) return goals;
                if (bad < 0)
                {
                    if (kept.Count == 0) throw new InvalidOperationException("the farm loop doesn't replay under its finish goals (finishes at ticks "
                        + string.Join(", ", reached) + " of " + inputs.Count + " inputs)");
                    kept.Clear();
                    continue;
                }
                kept.Remove(goals[bad]);
            }
        }

        static void WriteLoopBest(CourseRun run, LoopRoute route, LoopCycle c, string how)
        {
            var (file, text, seconds) = LoopBestFile(run, route, c);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            Solve.WriteShared(file, text);
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] offline best farm loop {seconds}s via {how} ({c.Ticks} ticks, {c.Held} held frames a loop of "
                + $"{c.Laps} lap{(c.Laps == 1 ? "" : "s")}, {route.LeadLaps} lead laps) -> {System.IO.Path.GetFileName(file)}");
            if (run.LogJson != null)
                System.IO.File.AppendAllText(run.LogJson, System.Text.Json.JsonSerializer.Serialize(new { wall = run.Clock.Elapsed.TotalSeconds, loop = c.Lap,
                    loopLaps = c.Laps, loopTicks = c.Ticks, loopHeld = c.Held, how }) + "\n");
        }

        static (string file, string text, string seconds) LoopBestFile(CourseRun run, LoopRoute route, LoopCycle c)
        {
            string seconds = c.Lap.ToString("0.0000", CultureInfo.InvariantCulture);
            string file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(run.Trace), "offline-best", run.Label + "-loop-" + seconds + ".json");
            string text = System.Text.Json.JsonSerializer.Serialize(new { course = run.Course, capabilities = run.Caps, loopFrom = route.Lead.Count,
                loopLaps = c.Laps, loopTicks = c.Ticks, loopHeld = c.Held, loopLive = c.Live, ticks = route.Inputs.Select(t => t.ToJson()) });
            return (file, text, seconds);
        }

        static void WriteStartingLoop(CourseRun run, LoopRoute route, LoopCycle c, string how)
        {
            if (how.StartsWith("start: " + AutoRunDir, StringComparison.Ordinal)) return;
            var (file, text, _) = LoopBestFile(run, route, c);
            string dir = System.IO.Path.GetDirectoryName(file);
            foreach (string folder in new[] { dir, System.IO.Path.Combine(dir, "judged") })
                if (System.IO.File.Exists(System.IO.Path.Combine(folder, System.IO.Path.GetFileName(file))) && System.IO.File.ReadAllText(System.IO.Path.Combine(folder, System.IO.Path.GetFileName(file))) == text) return;
            WriteLoopBest(run, route, c, how);
        }

        static void WriteLoopWaypoints(CourseRun run, LoopRoute route, LoopCycle c)
        {
            var (rects, world) = Rects(run);
            WaypointsLine(run, new
            {
                timing = "farm",
                route = c.Steady ? c.Lap.ToString("0.0000", CultureInfo.InvariantCulture) + "s a lap, looped" : "not a steady loop",
                goals = run.Goals.Select(g => g.label),
                rects,
                world,
                splits = CourseWaypoints.Check(run.Trace, route.Inputs, run.Goals),
                timerGoal = route.LeadLaps,
                loop = new { steady = c.Steady, laps = c.Laps, ticks = c.Ticks, held = c.Held, live = c.Live, lap = Math.Round(c.Lap, 4), settled = c.Settled, cycles = c.Cycles },
                bus = c.Finish == null ? null : new { gate = (object)null, finish = BusJson(c.Finish) },
            });
        }
    }
}
