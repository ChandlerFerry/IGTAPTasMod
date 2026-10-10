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
    // A course's farm as a steady-state loop (README ### Rules 30): the laps that repeat finish to finish, by a quick restart
    // after the finish or by dropping through the end gate into the next lap, timed in live time (ticks and held frames). A
    // farm route is a lead (from the course start through its last lead finish) and the loop (the laps after it, through the
    // last one's finish: one, or more when what a lap carries into the next (a buffered jump, a coyote count) alternates). Its
    // time is the loop's cycle once the loop repeats exactly, a lap's share of it: the lead and LoopRepeats loops played back
    // to back, every loop finishing on its own last input, the last two cycles equal.
    sealed partial class FullTas
    {
        /// <summary>Loops played after the lead to find the steady cycle.</summary>
        internal const int LoopRepeats = 4;
        /// <summary>The most laps a loop is made of.</summary>
        const int MaxLoopLaps = 3;
        /// <summary>Run's extra ticks for a whole route: its cap counts physics ticks, a dash's no-input ones too, so a route of
        /// several laps of dashes outruns inputs + a few (a loop's 4th lap was cut off). A run still ends at its last goal, a
        /// death, or 20 idle ticks after its inputs.</summary>
        const int AnyTicks = 1000000;

        /// <summary>A farm loop's steady-state check (<see cref="MeasureLoop"/>).</summary>
        sealed class LoopCycle
        {
            /// <summary>The loop repeats exactly: every loop finished on its own last input and the last two cycles are equal.</summary>
            public bool Steady;
            /// <summary>The last loop's cycle: physics ticks and held frames from the finish before it to its own.</summary>
            public long Ticks, Held;
            /// <summary>Laps in a loop.</summary>
            public int Laps = 1;
            public long Live { get { return MacroEngine.LiveUnits(Ticks, Held); } }
            /// <summary>The farm time: a lap's share of the cycle, in live seconds.</summary>
            public double Lap { get { return MacroEngine.LiveSeconds(Ticks, Held) / Laps; } }
            /// <summary>Loops played before the cycle settled (0: the first loop is already the steady one).</summary>
            public int Settled;
            /// <summary>Each loop's cycle in live units, -1 from the first that didn't finish on its own last input.</summary>
            public readonly List<long> Cycles = new List<long>();
            /// <summary>The last loop's finish near miss (<see cref="BusEdge"/>), null when it didn't finish.</summary>
            public BusEdge Finish;
            /// <summary>Why the first loop that didn't finish on its own last input didn't; null when all did.</summary>
            public string Broke;

            public override string ToString()
            {
                string cycles = string.Join(", ", Cycles.Select(c => c < 0 ? "-" : (c / 1400.0).ToString("0.0000", CultureInfo.InvariantCulture)));
                string laps = Laps == 1 ? "" : $" a lap of {Laps}";
                return Steady ? $"steady {Lap.ToString("0.0000", CultureInfo.InvariantCulture)}s{laps} ({Ticks} ticks, {Held} held a loop) after {Settled} loops (cycles {cycles})"
                    : $"not steady{laps} (cycles {cycles}{(Broke == null ? "" : "; " + Broke)})";
            }
        }

        /// <summary>A farm loop: the lead (the course start through its <see cref="LeadLaps"/>th finish) and the loop's laps.</summary>
        sealed class LoopRoute
        {
            public List<InputTick> Lead, Loop;
            public int LeadLaps, LoopLaps = 1;
            /// <summary>The route's first lap (the course start through its first finish) and that finish's physics tick.</summary>
            public List<InputTick> FirstLap;
            public long FirstTicks;
            public List<InputTick> Inputs { get { return Lead.Concat(Loop ?? new List<InputTick>()).ToList(); } }
        }

        /// <summary>A course run under <paramref name="finishes"/> finish goals: a finish doesn't end it, the last goal does.</summary>
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

        /// <summary>
        /// A route's laps: its inputs cut at each finish (each lap through its finish's input, the idle ticks a finish after the
        /// inputs ran out came on included) and its first finish's physics tick; no cuts when it doesn't finish.
        /// </summary>
        static (List<InputTick> inputs, List<int> cuts, long first) Laps(CourseRun run, List<InputTick> inputs)
        {
            const int MaxLaps = 16;
            List<InputTick> idled = inputs.Concat(Enumerable.Repeat(new InputTick(), 60)).ToList();
            RunResult r = Solve.OnThread(() => LoopSim(run, MaxLaps).Run(idled, null, null, AnyTicks));
            return (idled, Enumerable.Range(0, r.Progress).Select(k => (int)r.GoalInputTicks[k]).ToList(), r.Progress == 0 ? 0 : r.GoalPhysicsTicks[0]);
        }

        /// <summary>A route cut into laps as a farm loop of its last <paramref name="laps"/> laps (the laps before them the lead).</summary>
        static LoopRoute AsLoop((List<InputTick> inputs, List<int> cuts, long first) l, int laps)
        {
            int n = l.cuts.Count, from = l.cuts[n - 1 - laps], to = l.cuts[n - 1];
            return new LoopRoute { Lead = l.inputs.GetRange(0, from), LeadLaps = n - laps, Loop = l.inputs.GetRange(from, to - from), LoopLaps = laps,
                FirstLap = l.inputs.GetRange(0, l.cuts[0]), FirstTicks = l.first };
        }

        /// <summary>The loop's steady-state check: the lead, then the loop LoopRepeats times (<see cref="LoopCycle"/>).</summary>
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
                // Where each finish leaves the quick restart: a lap that doesn't repeat usually restarts somewhere else (Simulation.LoopRespawn).
                var restarts = new List<Vector2>();
                // The player's fields one input into each loop: what a lap carries through the restart into the next.
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
                    // The loop's last finish, on its own last input, and the finish the loop started from.
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

        /// <summary>The player's value fields (numbers, flags, vectors), by name, for comparing two loops' starts.</summary>
        static Dictionary<string, string> Fields(Movement m)
        {
            var fields = new Dictionary<string, string>();
            foreach (System.Reflection.FieldInfo f in typeof(Movement).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(Vector2) || f.FieldType == typeof(Vector3))
                    fields[f.Name] = Convert.ToString(f.GetValue(m), CultureInfo.InvariantCulture);
            return fields;
        }

        /// <summary>The route with its loop settled into the lead: the loops before the steady one played as lead laps, so the
        /// loop after the lead is the steady cycle itself.</summary>
        static LoopRoute SettleLoop(LoopRoute route, LoopCycle cycle)
        {
            if (cycle.Settled == 0) return route;
            var lead = new List<InputTick>(route.Lead);
            for (int i = 0; i < cycle.Settled; i++) lead.AddRange(route.Loop);
            return new LoopRoute { Lead = lead, LeadLaps = route.LeadLaps + cycle.Settled * route.LoopLaps, Loop = route.Loop, LoopLaps = route.LoopLaps,
                FirstLap = route.FirstLap, FirstTicks = route.FirstTicks };
        }

        /// <summary>
        /// A loop lap from where the lead leaves the player (after its last finish): the course solve's search (OfflineSolver),
        /// from that state with the run's forced path, for <paramref name="minutes"/>; the fastest lap that repeats as a loop of
        /// itself and the fastest lap at all when that one doesn't (each cut at its finish, null: none). Seeded with the lead's first lap played after the two ways a
        /// farm loops: a quick restart (on the tick after the finish, then a few idle ticks for what the restart carries, a
        /// buffered jump among them, to land), and the drop through the end gate (idle until the player lands at the start).
        /// </summary>
        static (List<InputTick> steady, List<InputTick> fastest) SearchLoopLap(CourseRun run, LoopRoute route, double minutes, int threads)
        {
            List<InputTick> lead = route.Lead;
            int laps = route.LeadLaps;
            Simulation Create() => AfterLead(run, route);
            List<InputTick> lap = route.FirstLap;
            List<InputTick> Idle(int n) => Enumerable.Repeat(new InputTick(), n).ToList();
            var seeds = new List<List<InputTick>>();
            if (route.Loop != null) foreach (int k in new[] { 0, 1, 2 }) seeds.Add(Idle(k).Concat(route.Loop).ToList());
            // A lap never starts on its quick restart: a restart input fires at once, inside the stretch of no-input ticks a
            // finish may come in (a dash, a spring), and cuts the lap before off short of its finish (Course 9 WJ_DS_DJ_BS: the
            // record's finish comes 7 ticks after its last input). An idle input first waits that stretch out.
            foreach (int k in new[] { 0, 10, 20, 30, 40 }) seeds.Add(new[] { new InputTick(), new InputTick { reset = true } }.Concat(Idle(k)).Concat(lap).ToList());
            foreach (int k in new[] { 20, 30, 40, 50 }) seeds.Add(Idle(k).Concat(lap).ToList());
            // The restart a human farms with: after the finish a tick of the stick to face the first lap's way, a quick restart, the
            // fall to the floor under the respawn point (its landing gives the air dash and ground coyote back), and a second quick
            // restart on the first lap's own first input, which then plays on the respawn's tick as it does from the category start
            // (a restart while standing). Course 6 WJ_DS_DJ_BS's 0.94 repeats so at 1.5357 s a lap, where the restarts above leave
            // it no air dash or coyote for its opening. The falls cover a 5-tick cycle: the 280 fps frame grid repeats every 5 ticks,
            // and a frame-timed tech (its pauselift) holds only on its own phase.
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
            // When the finish leaves the respawn point at another checkpoint than the course start's (the last one the lap touched:
            // Course 9 WJ_DS_DJ_BS's, 2600 u right of the start), the restart there, the way back until the start's checkpoint takes
            // the respawn point and the landing (WayBack), then the restart on the first lap's first input: from it the lap plays as
            // from the category start. Every phase of the 5-tick grid, as above.
            if (lap.Count > 0 && Solve.OnThread(() => WayBack(run, Create)) is List<InputTick> back)
                for (int w = 0; w < 5; w++)
                {
                    InputTick restart = lap[0];
                    restart.reset = true;
                    seeds.Add(back.Concat(Idle(w)).Append(restart).Concat(lap.Skip(1)).ToList());
                }
            // How each seed plays from the lead's finish, for the log: a search with no finishing seed rarely finds a first finish.
            List<string> played = Solve.OnThread(() =>
            {
                Simulation sim = Create();
                return seeds.Select(s => { RunResult r = sim.Run(s, null, null, 60); return r.Finished ? $"finish at tick {r.PhysicsTicks}" : r.EndReason; }).ToList();
            });
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap seeds: {string.Join("; ", played)}");
            // No seed finishes from the lead's finish (Course 9 WJ_DS_DJ_BS: the restart puts the player at the last checkpoint,
            // near the end, and a lap first goes back to the start gate): the unguided search seldom finds a first finish (none in
            // 1.4M runs), so it gets 2 minutes and the waypoint repair (RepairLoopLap), which has the course's goals to reach, the rest.
            if (!played.Any(p => p.StartsWith("finish", StringComparison.Ordinal))) minutes = Math.Min(minutes, 2.0);
            // Ranked by live time to the lap's finish (ticks and held frames), the farm's own measure, not by the course timer.
            var solver = new OfflineSolver(Create, run.Caps, seeds, null, run.Rules) { Farm = true };
            List<InputTick> found = null;
            long foundLive = long.MaxValue;
            var guard = new object();
            solver.Improved += (time, live, inputs, how) =>
            {
                lock (guard) if (live < foundLive) (found, foundLive) = (inputs, live);
                Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: finish at {live / 1400.0:0.0000}s live from the course start ({time:0.00}s on the course timer) via {how}");
            };
            // Every finishing lap is a candidate, not only the fastest: a quick restart keeps the lap's history (lastHyperY,
            // heightOfLastJumpStart: CourseStart.cs), so the fastest lap from the lead's finish often dies on its second play (Course 6
            // WJ_DS_DJ_BS's 1.5557 s lap did, and no loop was found), where a slower one repeats. The fastest PoolSize by the course
            // timer wait to be checked, fastest first. ponytail: ordered by the lap's course timer, not its live time (not reported).
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
            // Each new fastest lap is checked as a loop of itself as it comes: the fastest one that repeats is kept (a faster lap that
            // doesn't, carrying a buffered jump into the next, can't replace it), and once one repeats the search ends after
            // SteadyQuietSeconds without a faster lap. It is the farm's whole-lap search, as the whole-route search is a record's: the
            // --auto windows after it only trim the loop it found (Course 6 WJ_DS_DJ_BS: a 2.78 s loop where this search had found a
            // 1.56 s lap within a minute), so it keeps going while it finds faster laps.
            var clock = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            Thread[] workers = solver.Start(threads);
            List<InputTick> steady = null, seen = null;
            long steadyLive = long.MaxValue;
            bool fastestSteady = false;
            var checkedLaps = new HashSet<string>();
            int pooled = 0;
            // The lap as a loop of itself, kept when it repeats faster than the steady lap so far; whether it was a new one.
            bool Check(List<InputTick> f, string what)
            {
                List<InputTick> cut = CutAtFinish(run, route, f);
                if (cut == null || !checkedLaps.Add(System.Text.Json.JsonSerializer.Serialize(cut.Select(t => t.ToJson())))) return false;
                LoopCycle c = MeasureLoop(run, new LoopRoute { Lead = lead, LeadLaps = laps, Loop = cut, FirstLap = route.FirstLap, FirstTicks = route.FirstTicks });
                bool faster = c.Steady && c.Live < steadyLive;
                if (what != null || faster) Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: {what ?? "a finishing lap"} as a loop of itself: {c}");
                if (!faster) return true;
                // Its pauses at their shortest that still repeat (the pool's laps come as the search ran them, never through its own
                // DropIdlePauses): a pause's frames are live time every lap.
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
                // The pool's fastest laps for the rest of the beat.
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

        /// <summary>
        /// From the state <paramref name="create"/> starts in (a lead's finish): a quick restart, the stick held toward the course
        /// start until the start's checkpoint is the respawn point, and on to the landing after it: those inputs (the landing's tick
        /// the last), or null when 600 ticks don't get there. Built before <paramref name="create"/>'s simulation, the category start's
        /// own tells where the start's checkpoint is (the last Simulation built on a thread owns its engine hooks).
        /// </summary>
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

        /// <summary>Finishing laps waiting for the lap search's steady check.</summary>
        const int PoolSize = 256;

        /// <summary>The lap search ends this long after its last faster lap once it has a lap that repeats.</summary>
        const double SteadyQuietSeconds = 300;

        /// <summary>The course run after the route's lead, without goals: a course solve's from where the lead leaves the player.</summary>
        static Simulation AfterLead(CourseRun run, LoopRoute route)
        {
            Simulation sim = LoopSim(run, route.LeadLaps + 1);
            SimSnapshot at = sim.Advance(route.Lead);
            // A lead whose last finish comes in a stretch of ticks that take no input after its last one (Course 7 WJ_DS_DJ_BS's
            // record, 64 ticks on): Advance stops where the next input is asked, before them, and every lap searched from there
            // "finished" on the lead's own finish. The state at that finish instead, its next input the lap's first.
            if (sim.Progress < route.LeadLaps)
                at = sim.AdvanceToGoal(route.Lead.Concat(Enumerable.Repeat(new InputTick(), 60)).ToList(), route.LeadLaps, out _) ?? at;
            sim.SetStart(at, route.Lead.Count);
            sim.UseGoals(null);
            sim.TargetCourse = run.Course;
            return sim;
        }

        /// <summary>A lap played after the route's lead, cut at its finish (a finish in the idle ticks after its inputs takes those
        /// ticks as inputs), or null when it doesn't finish. Played on from the lead, as <see cref="MeasureLoop"/> plays it, and cut
        /// where that run's finish counts it: from a lead state inside a no-input stretch (<see cref="AfterLead"/>) the lap run's own
        /// finish tick is one input off (Course 7 WJ_DS_DJ_BS: every lap "finished on input 660, its last is 661").</summary>
        static List<InputTick> CutAtFinish(CourseRun run, LoopRoute route, List<InputTick> lap)
        {
            List<InputTick> idled = lap.Concat(Enumerable.Repeat(new InputTick(), 60)).ToList();
            RunResult r = Solve.OnThread(() => LoopSim(run, route.LeadLaps + 1).Run(route.Lead.Concat(idled).ToList(), null, null, AnyTicks));
            if (r.Progress <= route.LeadLaps) return null;
            int n = (int)(r.GoalInputTicks[route.LeadLaps] - route.Lead.Count);
            return n > 0 && n <= idled.Count ? idled.GetRange(0, n) : null;
        }

        /// <summary>
        /// A loop lap repaired where the lap search found none: the lap before (the route's loop, else its first lap after a quick
        /// restart) played after the lead, and the reach windows (FullTas --unsolved) take it on from where it breaks, through the
        /// first lap's waypoints to the finish. The finish clears the course reset point, so the restart lands on the course's
        /// checkpoint, which needn't be the course start (Course 3: 1 u right of it): a first lap through spikes on grace breaks
        /// there, and the lap search has no seed that finishes. The lap cut at its finish, or null when the reach doesn't get there.
        /// </summary>
        static List<InputTick> RepairLoopLap(CourseRun run, LoopRoute route, WaypointOptions options, string dir, double minutes, int threads)
        {
            List<InputTick> lap = route.Loop ?? new[] { new InputTick(), new InputTick { reset = true } }.Concat(route.FirstLap).ToList();
            List<FullTasGoal> goals = Enumerable.Range(0, route.LeadLaps).Select(_ => FinishGoal(run.Course))
                .Concat(CourseWaypoints.Build(run.Trace, route.FirstLap, options).Goals.Where(g => g.kind == FullTasGoal.At))
                .Append(FinishGoal(run.Course)).ToList();
            string name = run.Name + "-lap";
            string synthetic = WriteCourseTrace(run, dir, route.Lead.Concat(lap).ToList(), name, goals);
            Solve.CourseRuns[name] = run.Rules;
            TimerGoal = route.LeadLaps;
            LoopTiming = true;
            TickTiming = false;
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: repairing the lap before from where it breaks, through {goals.Count - route.LeadLaps - 1} "
                + $"waypoints to the finish, for {minutes:0.##} min");
            var repair = new FullTas(new[] { synthetic, "--unsolved", "--fresh", "--no-submit", "--threads", threads.ToString(CultureInfo.InvariantCulture),
                "--minutes", minutes.ToString("0.##", CultureInfo.InvariantCulture) }) { _course = run, _reachOnly = true };
            repair.Search();
            Route best = repair._best;
            if (best.Progress < goals.Count)
            {
                Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: the repair reached {best.Progress} of {goals.Count} goals");
                return null;
            }
            // The reach may redo the lead's inputs: the lap is replayed after the route's own lead, null if it needs the redone one.
            return CutAtFinish(run, route, best.Inputs.Skip(route.Lead.Count).ToList());
        }

        /// <summary>
        /// The farm loop the search starts from: the fastest steady cycle, settled (<see cref="SettleLoop"/>), of the candidates
        /// and the loop laps searched after the best's lead (or the fastest first lap), for <paramref name="lapMinutes"/>; a lap
        /// the search doesn't find with no loop yet is repaired (<see cref="RepairLoopLap"/>). Null when no steady loop came of it.
        /// </summary>
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
                // The fewest laps that loop: a loop of more laps only when what a lap carries into the next alternates.
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
            // Laps searched one after another, from the best loop's lead (the last Improve's) or the fastest first lap: each search
            // keeps its fastest lap that repeats as a loop of itself, and the next searches after its fastest lap at all when that one
            // doesn't repeat. A quick restart carries the lap's history (CourseStart.cs), so the fastest lap from the lead's finish is
            // tuned to the lead's; one searched after it is tuned to a lap's, and the loops ending with it (the newest lap, the newest
            // two, ...) are checked, so laps whose carried state alternates still make a loop. It runs every Improve, as a record's
            // whole-route search does: the --auto windows only trim the loop they start from.
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
                List<InputTick> lap = fastest ?? (best == null && steady == null ? RepairLoopLap(run, at, options, dir, left, threads) : null);
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

        /// <summary>
        /// `solve --auto --farm` on a gated course (README ### Rules 30): the farm loop to start from (<see cref="StartLoop"/>: a
        /// saved loop, else loop laps searched for half the minutes each), its goals (a finish per lead lap,
        /// then per loop lap the first lap's waypoints it passes and its finish), timed from the lead's last finish
        /// (<see cref="TimerGoal"/>, <see cref="LoopTiming"/>), and the Full-TAS --auto loop over them.
        /// </summary>
        static int LoopAuto(CourseRun run, string[] args, List<(string how, List<InputTick> inputs)> candidates, string dir)
        {
            run.Loop = true;
            // A farm lap's cut boxes hold on the course only (ForcedPath.OnCourseOnly), not on the way back to its start.
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
            TimerGoal = route.LeadLaps;
            LoopTiming = true;
            TickTiming = false;
            run.LoopLaps = route.LoopLaps;
            // The loop the run starts from is a kept best too: the plugin judges only offline-best files, and the loop writes only what
            // it improves on (LoopCommitted). A loop the lap search just found or a seed's (a library route) reaches the judge here; the
            // last --auto best was written when kept, and a read-only run (--waypoints-out) writes nothing.
            if (run.WaypointsFile == null) WriteStartingLoop(run, route, cycle, candidates.Any(c => c.how == how) ? "start: " + how : how);
            List<FullTasGoal> lap = LoopLapWaypoints(run, route, options);
            run.Goals = LoopGoals(run, route, lap);
            string synthetic = WriteCourseTrace(run, dir, route.Inputs);
            Solve.CourseRuns[run.Name] = run.Rules;
            Console.WriteLine($"Course {run.Course} ({run.Caps}) --auto, farm loop timing, from {how}: {cycle}; {route.LeadLaps} lead laps, "
                + $"{run.Goals.Count} goals ({run.Goals.Count(g => g.kind == FullTasGoal.At)} waypoints on the loop): {string.Join(" ", run.Goals.Select(g => g.label))}");
            // The lap search's minutes come out of the run's.
            string[] rest = args.ToArray();
            int at = Array.IndexOf(rest, "--minutes");
            if (at >= 0 && at + 1 < rest.Length)
                rest[at + 1] = Math.Max(0.25, minutes - run.Clock.Elapsed.TotalMinutes).ToString("0.##", CultureInfo.InvariantCulture);
            var loop = new FullTas(FullArgs(synthetic, rest, run.Goals).ToArray());
            loop._course = run;
            loop._committed = loop.CourseCommitted;
            if (run.WaypointsFile != null)
            {
                loop.Open();
                loop.WriteLoopWaypoints(loop._best, loop._best.Progress == loop._goals ? MeasureLoop(run, loop.RouteLoop(loop._best)) : cycle);
                return 0;
            }
            return loop.Search();
        }

        /// <summary>
        /// The area waypoints of the loop's first lap, placed on that lap's own path (from the lead's finish, a quick restart's
        /// landing or the drop through the end gate, to the next finish) and checked on the whole route under the lead's
        /// finishes; the first lap's from the course start when that fails (a loop lap that starts where the first lap did).
        /// </summary>
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

        /// <summary>A finish per lead lap, then per loop lap the waypoints it passes in order (one a lap misses, or reaches on
        /// the tick after the goal before, dropped from every lap) and its finish.</summary>
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

        /// <summary>The loop route of a complete run: its inputs through the lead's last finish, then through the loop's.</summary>
        LoopRoute RouteLoop(Route r)
        {
            List<InputTick> idled = r.Inputs.Concat(Enumerable.Repeat(new InputTick(), 60)).ToList();
            int a = (int)r.GoalInput[TimerGoal - 1], b = (int)r.GoalInput[_goals - 1];
            return new LoopRoute { Lead = idled.GetRange(0, a), LeadLaps = TimerGoal, Loop = idled.GetRange(a, b - a), LoopLaps = _course.LoopLaps };
        }

        /// <summary>The last complete route whose steady-state check passed in <see cref="LoopKeeps"/> (or the best's, measured there).</summary>
        (Route route, LoopCycle cycle) _loopChecked;

        LoopCycle LoopCycleOf(Route r)
        {
            if (_loopChecked.route != r) _loopChecked = (r, MeasureLoop(_course, RouteLoop(r)));
            return _loopChecked.cycle;
        }

        /// <summary>Commit's farm check, before a route becomes the best: the goals rank a loop's first pass, so a complete route is
        /// kept only when its loop repeats exactly and its steady cycle beats the best's (README ### Rules 30, 52).</summary>
        bool LoopKeeps(Route r, string where)
        {
            if (r.Progress < _goals) return true;
            LoopCycle best = _best.Progress == _goals ? LoopCycleOf(_best) : null;
            LoopCycle c = MeasureLoop(_course, RouteLoop(r));
            if (!c.Steady)
            {
                Log($"farm loop check ({where}): {Describe(r)} doesn't repeat exactly ({c}); not kept");
                return false;
            }
            if (best != null && best.Steady && c.Live * best.Laps >= best.Live * c.Laps)
            {
                Log($"farm loop check ({where}): {Describe(r)} repeats at {c.Lap:0.0000}s a lap, no faster than the best's {best.Lap:0.0000}s; not kept");
                return false;
            }
            _loopChecked = (r, c);
            return true;
        }

        /// <summary>A kept farm loop best: its steady-state check (<see cref="LoopKeeps"/> passed it), its waypoints, written for
        /// the plugin to judge as the farm route (<see cref="WriteLoopBest"/>).</summary>
        void LoopCommitted(Route r, string where)
        {
            LoopCycle c = LoopCycleOf(r);
            LoopRoute route = RouteLoop(r);
            WriteLoopWaypoints(r, c);
            if (c.Live != r.Live)
                Log($"farm loop check: the loop's steady cycle is {c.Live / 1400.0:0.0000}s, its first {r.LiveSeconds:0.0000}s");
            WriteLoopBest(_course, route, c, "auto: " + where);
        }

        /// <summary>A steady farm loop written to offline-best as <c>&lt;course&gt;_&lt;caps&gt;-loop-&lt;lap seconds&gt;.json</c> (the
        /// inputs, where the loop starts, its laps and cycle), and the `offline best farm loop` line the plugin reads.</summary>
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

        /// <summary>The offline-best file of a steady farm loop: its path, text and lap seconds (the plugin reads the loop's laps and cycle from it).</summary>
        static (string file, string text, string seconds) LoopBestFile(CourseRun run, LoopRoute route, LoopCycle c)
        {
            string seconds = c.Lap.ToString("0.0000", CultureInfo.InvariantCulture);
            string file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(run.Trace), "offline-best", run.Label + "-loop-" + seconds + ".json");
            string text = System.Text.Json.JsonSerializer.Serialize(new { course = run.Course, capabilities = run.Caps, loopFrom = route.Lead.Count,
                loopLaps = c.Laps, loopTicks = c.Ticks, loopHeld = c.Held, loopLive = c.Live, ticks = route.Inputs.Select(t => t.ToJson()) });
            return (file, text, seconds);
        }

        /// <summary>The farm loop a run starts from, written like a kept one (<see cref="WriteLoopBest"/>) unless it is the last --auto
        /// best's or its file is already there, waiting or judged. The plugin judges a loop file only when its lap beats the farm route's.</summary>
        static void WriteStartingLoop(CourseRun run, LoopRoute route, LoopCycle c, string how)
        {
            if (how.StartsWith("start: " + AutoRunDir, StringComparison.Ordinal)) return;
            var (file, text, _) = LoopBestFile(run, route, c);
            string dir = System.IO.Path.GetDirectoryName(file);
            foreach (string folder in new[] { dir, System.IO.Path.Combine(dir, "judged") })
                if (System.IO.File.Exists(System.IO.Path.Combine(folder, System.IO.Path.GetFileName(file))) && System.IO.File.ReadAllText(System.IO.Path.Combine(folder, System.IO.Path.GetFileName(file))) == text) return;
            WriteLoopBest(run, route, c, how);
        }

        /// <summary>The waypoints of a farm loop (WriteWaypoints' list, the lead's finishes first) with its steady-state check:
        /// `loop` (a lap's share of the cycle, steady or not, its laps, the loops before it settled, each loop's cycle) and the
        /// loop finish's near miss.</summary>
        void WriteLoopWaypoints(Route r, LoopCycle c)
        {
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                timing = "farm",
                route = c.Steady ? c.Lap.ToString("0.0000", CultureInfo.InvariantCulture) + "s a lap, looped" : Describe(r) + " (not a steady loop)",
                goals = _labels,
                rects = _course.Goals.Select(g => g.kind == FullTasGoal.At ? new[] { g.x, g.y, g.w, g.h } : null),
                world = _course.Goals.Select(g =>
                {
                    if (g.kind != FullTasGoal.At) return null;
                    WorldPos at = _eval.ToWorld(new CapturePos(g.x, g.y));
                    return new[] { at.x, at.y, g.w, g.h };
                }),
                splits = Enumerable.Range(0, _goals).Select(k => k < r.Progress ? r.GoalPhysics[k] : -1),
                timerGoal = TimerGoal,
                loop = new { steady = c.Steady, laps = c.Laps, ticks = c.Ticks, held = c.Held, live = c.Live, lap = Math.Round(c.Lap, 4), settled = c.Settled, cycles = c.Cycles },
                bus = c.Finish == null ? null : new { gate = (object)null, finish = BusJson(c.Finish) },
            });
            Console.WriteLine("waypoints " + json);
            if (_course.WaypointsFile != null) Solve.WriteShared(_course.WaypointsFile, json);
        }
    }
}
