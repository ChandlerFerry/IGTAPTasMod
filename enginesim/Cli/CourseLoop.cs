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

            public override string ToString()
            {
                string cycles = string.Join(", ", Cycles.Select(c => c < 0 ? "-" : (c / 1400.0).ToString("0.0000", CultureInfo.InvariantCulture)));
                string laps = Laps == 1 ? "" : $" a lap of {Laps}";
                return Steady ? $"steady {Lap.ToString("0.0000", CultureInfo.InvariantCulture)}s{laps} ({Ticks} ticks, {Held} held a loop) after {Settled} loops (cycles {cycles})"
                    : $"not steady{laps} (cycles {cycles})";
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
            RunResult r = Solve.OnThread(() => LoopSim(run, MaxLaps).Run(idled, null, null, 0));
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
                RunResult r = sim.Run(inputs, null, null, 0, _ =>
                {
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
                    if (!on) continue;
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
        /// from that state with the run's forced path, for <paramref name="minutes"/>; the lap with the least live time to
        /// its finish, cut at the finish, or null when none finished. Seeded with the lead's first lap played after the two ways a
        /// farm loops: a quick restart (on the tick after the finish, then a few idle ticks for what the restart carries, a
        /// buffered jump among them, to land), and the drop through the end gate (idle until the player lands at the start).
        /// </summary>
        static List<InputTick> SearchLoopLap(CourseRun run, LoopRoute route, double minutes, int threads)
        {
            List<InputTick> lead = route.Lead;
            int laps = route.LeadLaps;
            Simulation Create()
            {
                Simulation sim = LoopSim(run, laps + 1);
                SimSnapshot at = sim.Advance(lead);
                sim.SetStart(at, lead.Count);
                sim.UseGoals(null);
                sim.TargetCourse = run.Course;
                return sim;
            }
            List<InputTick> lap = route.FirstLap;
            List<InputTick> Idle(int n) => Enumerable.Repeat(new InputTick(), n).ToList();
            var seeds = new List<List<InputTick>>();
            if (route.Loop != null) foreach (int k in new[] { 0, 1, 2 }) seeds.Add(Idle(k).Concat(route.Loop).ToList());
            foreach (int k in new[] { 0, 10, 20, 30, 40 }) seeds.Add(new[] { new InputTick { reset = true } }.Concat(Idle(k)).Concat(lap).ToList());
            foreach (int k in new[] { 20, 30, 40, 50 }) seeds.Add(Idle(k).Concat(lap).ToList());
            // How each seed plays from the lead's finish, for the log: a search with no finishing seed rarely finds a first finish.
            List<string> played = Solve.OnThread(() =>
            {
                Simulation sim = Create();
                return seeds.Select(s => { RunResult r = sim.Run(s, null, null, 60); return r.Finished ? $"finish at tick {r.PhysicsTicks}" : r.EndReason; }).ToList();
            });
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap seeds: {string.Join("; ", played)}");
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
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: searching Course {run.Course} ({run.Caps}) from the lead's finish {laps} "
                + $"({lead.Count} inputs) for {minutes:0.##} min on {threads} threads");
            var clock = Stopwatch.StartNew();
            Thread[] workers = solver.Start(threads);
            while (clock.Elapsed.TotalMinutes < minutes) Thread.Sleep(250);
            solver.Stop();
            foreach (Thread t in workers) t.Join(60000);
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] loop lap: {Interlocked.Read(ref solver.Runs)} runs, {Interlocked.Read(ref solver.Finishes)} finishes, {solver.GuideStatus()}");
            if (found == null) return null;
            // A finish in the idle ticks after the solver's inputs needs those ticks as inputs: the loop is cut at its finish.
            List<InputTick> idled = found.Concat(Enumerable.Repeat(new InputTick(), 60)).ToList();
            RunResult rr = Solve.OnThread(() => Create().Run(idled, null, null, 0));
            return rr.Finished ? idled.GetRange(0, rr.FinishTick + 1) : null;
        }

        /// <summary>
        /// The farm loop the search starts from: the candidate with the fastest steady cycle, settled (<see cref="SettleLoop"/>).
        /// Without one, a loop lap is searched after the fastest lead, and while a found loop doesn't repeat exactly, after the
        /// lead and that loop once more (a few times at most). Null when no steady loop came of it.
        /// </summary>
        static (LoopRoute route, LoopCycle cycle, string how)? StartLoop(CourseRun run, List<(string how, List<InputTick> inputs)> candidates, double lapMinutes,
            int threads, bool search)
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
            if (best != null || !search || lead == null) return best;
            // Laps searched one after another from the first lead lap: after each, the loops ending with it (the newest lap, the
            // newest two, ...) are checked, so laps whose carried state alternates still make a loop.
            LoopRoute at = lead.Value.route;
            var found = new List<List<InputTick>>();
            for (int attempt = 0; attempt < 2 * MaxLoopLaps && best == null; attempt++)
            {
                List<InputTick> lap = SearchLoopLap(run, at, lapMinutes, threads);
                if (lap == null) break;
                found.Add(lap);
                for (int p = 1; p <= found.Count && p <= MaxLoopLaps && best == null; p++)
                {
                    var lead2 = new List<InputTick>(lead.Value.route.Lead);
                    foreach (List<InputTick> l in found.Take(found.Count - p)) lead2.AddRange(l);
                    var r = new LoopRoute { Lead = lead2, LeadLaps = 1 + found.Count - p, Loop = found.Skip(found.Count - p).SelectMany(l => l).ToList(), LoopLaps = p,
                        FirstLap = at.FirstLap, FirstTicks = at.FirstTicks };
                    LoopCycle c = MeasureLoop(run, r);
                    Console.WriteLine($"loop of the last {p} laps searched, after {r.LeadLaps} lead laps: {c}");
                    Consider(r, c, "loop lap search from " + lead.Value.how);
                }
                at = new LoopRoute { Lead = at.Lead.Concat(lap).ToList(), LeadLaps = at.LeadLaps + 1, Loop = lap, FirstLap = at.FirstLap, FirstTicks = at.FirstTicks };
            }
            return best;
        }

        static FullTasGoal FinishGoal(int course) { return new FullTasGoal { kind = FullTasGoal.Finish, course = course, label = "finish:" + course }; }

        /// <summary>
        /// `solve --auto --farm` on a gated course (README ### Rules 30): the farm loop to start from (<see cref="StartLoop"/>: a
        /// saved loop, else loop laps searched for a third of the minutes, 5 at most, each), its goals (a finish per lead lap,
        /// then per loop lap the first lap's waypoints it passes and its finish), timed from the lead's last finish
        /// (<see cref="TimerGoal"/>, <see cref="LoopTiming"/>), and the Full-TAS --auto loop over them.
        /// </summary>
        static int LoopAuto(CourseRun run, string[] args, List<(string how, List<InputTick> inputs)> candidates, string dir)
        {
            run.Loop = true;
            double minutes = Solve.Option(args, "--minutes", 60.0);
            int threads = Solve.Option(args, "--threads", Math.Max(1, Environment.ProcessorCount - 4));
            var start = StartLoop(run, candidates, Math.Min(5.0, Math.Max(0.25, minutes / 3)), threads, run.WaypointsFile == null);
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
            run.LoopLaps = route.LoopLaps;
            // The loop the run starts from is a kept best too: the plugin judges only offline-best files, and the loop writes only what
            // it improves on (LoopCommitted). A loop the lap search just found or a seed's (a library route) reaches the judge here; the
            // last --auto best was written when kept, and a read-only run (--waypoints-out) writes nothing.
            if (run.WaypointsFile == null) WriteStartingLoop(run, route, cycle, candidates.Any(c => c.how == how) ? "start: " + how : how);
            WaypointOptions options = WaypointOptions.Parse(args, line => Console.WriteLine(line));
            options.Record = false;
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
            var loop = new FullTas(FullArgs(synthetic, rest, run.Goals.Count).ToArray());
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
                Simulation sim = LoopSim(run, route.LeadLaps + 1);
                SimSnapshot at = sim.Advance(route.Lead);
                sim.SetStart(at, route.Lead.Count);
                sim.UseGoals(null);
                sim.TargetCourse = run.Course;
                return CourseWaypoints.Build(run.Trace, run.Header, sim, route.Loop, options,
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
                    if (kept.Count == 0) throw new InvalidOperationException("the farm loop doesn't replay under its finish goals");
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

        /// <summary>A kept farm loop best: its steady-state check, its waypoints, and when it repeats exactly, written for the
        /// plugin to judge as the farm route (<see cref="WriteLoopBest"/>).</summary>
        void LoopCommitted(Route r, string where)
        {
            LoopRoute route = RouteLoop(r);
            LoopCycle c = MeasureLoop(_course, route);
            WriteLoopWaypoints(r, c);
            if (!c.Steady)
            {
                Log($"farm loop check: {Describe(r)} doesn't repeat exactly ({c}); not written");
                return;
            }
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
