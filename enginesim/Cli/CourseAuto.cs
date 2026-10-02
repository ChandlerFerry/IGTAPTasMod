using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using UnityEngine;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    // solve <course trace> --auto (enginesim/README.md, "Course --auto"): the Full-TAS --auto loop on a course. The course becomes a
    // Full-TAS run whose goals are its waypoints (CourseWaypoints.Build: area goals along the best's path, the course's checkpoints,
    // in record timing the start-gate crossing) and its finish, so the windows, library, tail windows, combine, scheduler,
    // cost-to-go and macros all work goal to goal. Waypoints are checkpoints, not cut points: a window runs through them from the
    // state the route has there, and is scored on arrival. The run lives in <traces>/course-auto/ (its trace, world, run file,
    // archive, refs); every kept best is replayed on the plain course simulation and written to <traces>/offline-best/ as solve
    // writes it, so the plugin judges it (record, then farm) as any solve's.
    sealed partial class FullTas
    {
        sealed class CourseRun
        {
            public string Trace, Label, Name, Timing, WaypointsFile, LogJson;
            public int Course;
            public string Caps;
            public JsonElement Header;
            public Func<Simulation> Plain;
            public OfflineSolver.ForcedPath Rules;
            public List<FullTasGoal> Goals;
            public Stopwatch Clock;
            /// <summary>A gated course's farm: a steady-state loop (CourseLoop.cs), timed finish to finish, of this many laps.</summary>
            public bool Loop;
            public int LoopLaps = 1;
        }

        /// <summary>A route played on the plain course simulation (the game's own finish and timer).</summary>
        sealed class CourseResult
        {
            public bool Finished;
            public float Time;
            public long RunTicks;
            public string EndReason;
            /// <summary>Entered one of the run's avoid boxes or missed a via box: off the course's forced path.</summary>
            public bool OffPath;
            /// <summary>The record's near misses at the start gate and the finish (a gated course's finished run), else null.</summary>
            public Bus Bus;
        }

        /// <summary>
        /// How near a run came to its gates a tick sooner, in the simulation's own distances: Collider2D.Distance between the
        /// player's hurtbox (the 40×54 box; the body box excludes the gates' Default layer and never touches them) and the
        /// gate's trigger, the gap between them, negative when they overlap (a trigger's touch is tested where the bodies stand
        /// after the step, PhysicsWorld.CollectTouching). Gate: the start gate's Enter tick (startGate starts the timer on the
        /// Stay, so the touch that started it entered the tick before the timer) against the tick before it. Finish: the end
        /// gate's finishing tick against the tick before it. docs/course-timer.md.
        /// </summary>
        sealed class Bus
        {
            public BusEdge Gate, Finish;
        }

        sealed class BusEdge
        {
            /// <summary>The physics tick from the run's start the trigger was first overlapped on; the game timer's start
            /// (<see cref="TimerTick"/>, the gate only) is the tick it ran on.</summary>
            public long Tick, TimerTick;
            /// <summary>The gap the tick before (positive: the hurtbox stopped short) and on the tick (negative: its overlap).
            /// The start gate's opportunity is a shallow <see cref="At"/> (a hair less and the timer starts a tick later); the
            /// finish's a small <see cref="Before"/> (a hair further and it finishes a tick sooner).</summary>
            public float Before, At;
            /// <summary>How far the player's body moved between the two ticks.</summary>
            public float Travel;
        }

        /// <summary>The direction blocks (forced-paths.json): a course with both lets a run go either way from the start.</summary>
        static readonly string[] Directions = { "avoid-forwards", "avoid-backwards" };

        public static int CourseAuto(string[] args)
        {
            string trace = Path.GetFullPath(args[0]);
            bool farm = args.Contains("--farm");
            JsonElement header = Solve.ReadHeader(trace);
            if (header.TryGetProperty("fulltas", out _))
                throw new ArgumentException("solve --auto takes a course trace; a Full-TAS trace runs `fulltas <trace> --auto`");
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            // Timed from the start already (a bonus's reward box, a transition's finish): its record is its farm.
            bool gated = !header.TryGetProperty("finish", out _) && !header.TryGetProperty("finishStart", out _);
            var run = new CourseRun
            {
                Trace = trace, Course = course, Caps = caps, Header = header, Label = course + "_" + caps,
                Timing = farm || !gated ? "farm" : "record", Plain = Solve.Creator(trace, header), Clock = Stopwatch.StartNew(),
                WaypointsFile = Solve.Option(args, "--waypoints-out", (string)null), LogJson = Solve.Option(args, "--log-json", (string)null),
            };
            List<string> optional = Solve.Optional(args, course, caps);
            // Both directions open (neither direction block picked): the waypoints follow one route, so one loop would only ever
            // improve the direction its best goes. Each direction gets half the minutes, its own run and waypoints: farm forwards
            // first, record backwards first. A picked block, or --focus on the studio's list, keeps the one run.
            var blocks = Solve.OptionalBlocks(course, caps, new OfflineSolver.ForcedPath()).Select(b => b.Name).ToList();
            if (Directions.All(blocks.Contains) && !Directions.Any(optional.Contains) && run.WaypointsFile == null && !args.Contains("--focus"))
            {
                double minutes = Solve.Option(args, "--minutes", 60.0);
                var rest = new List<string>();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--minutes") { i++; continue; }
                    rest.Add(args[i]);
                }
                int code = 0;
                foreach (string block in run.Timing == "farm" ? new[] { "avoid-backwards", "avoid-forwards" } : new[] { "avoid-forwards", "avoid-backwards" })
                {
                    Console.WriteLine($"--auto: course {course} goes either way from its start; {block} for {minutes / 2:0.##} min");
                    code = Math.Max(code, CourseAuto(rest.Concat(new[] { "--optional", block, "--minutes", (minutes / 2).ToString(CultureInfo.InvariantCulture) }).ToArray()));
                }
                return code;
            }
            OfflineSolver.ForcedPath rules = Solve.ForcedPaths(args, course, caps);
            run.Rules = rules;
            run.Name = "course" + course + "_" + caps + (optional.Count == 0 ? "" : "-opt-" + string.Join("+", optional)) + (run.Timing == "farm" ? "-farm" : "");
            string dir = Path.Combine(Path.GetDirectoryName(trace), "course-auto");
            Directory.CreateDirectory(dir);
            if (run.LogJson != null) File.WriteAllText(run.LogJson, "");

            // The category's unlocks: the start's player has them (the capture set them); a trace captured with others isn't this category.
            string startCaps = Solve.OnThread(() => PlayerCaps.Of(run.Plain()).Id);
            if (startCaps != caps)
                throw new InvalidOperationException($"{Path.GetFileName(trace)} is {caps}, but its start's player has {startCaps} unlocked: capture the category's start again");

            // The loop starts from the fastest of the trace's route, every --seed and the last --auto best (on this timing).
            var candidates = new List<(string how, List<InputTick> inputs)> { (Path.GetFileName(trace), Solve.ReadInputs(header)) };
            foreach (string seed in Solve.Args(args, "--seed")) candidates.Add((Path.GetFileName(seed), global::IGTAP.EngineSim.Cli.Plan.ReadRoute(seed)));
            string runFile = Path.Combine(dir, "fulltas", run.Name + ".json");
            if (!args.Contains("--fresh") && File.Exists(runFile)) candidates.Add((AutoRunDir + Path.GetFileName(runFile), global::IGTAP.EngineSim.Cli.Plan.ReadRoute(runFile)));
            // A gated course's farm is its steady-state loop, finish to finish (README ### Rules 30).
            if (run.Timing == "farm" && gated) return LoopAuto(run, args, candidates, dir);
            var played = candidates.Select(c => (c.how, c.inputs, result: PlayCourse(run, c.inputs))).ToList();
            foreach (var p in played)
                Console.WriteLine($"{p.how}: " + (!p.result.Finished ? "no finish (" + p.result.EndReason + ")"
                    : $"record {p.result.Time:0.00}s, run {p.result.RunTicks * .02:0.00}s from the start ({p.result.RunTicks} ticks)" + (p.result.OffPath ? ", off the forced path (an avoid box or a missed via)" : "")));
            // The waypoints follow the best, so it has to keep the run's rules: one through an avoid box (the direction a picked
            // block shuts) would set every window's goals where the windows are cut.
            var finishing = played.Where(p => p.result.Finished && !p.result.OffPath).ToList();
            if (finishing.Count == 0)
            {
                if (run.WaypointsFile != null)
                    throw new InvalidOperationException("--auto improves a complete route: neither the trace's route nor a --seed finishes the course on its forced path");
                // Only a narrower category's routes (the seeds) and none finishes here (an added OM or ORB changes what the same
                // inputs do), or none keeps the forced path: nothing to improve yet, so the plain solve finds a first finish, those
                // routes still its seeds.
                Console.WriteLine("--auto: neither the trace's route nor a --seed finishes the course on its forced path; solving for a first finish instead");
                var plain = new List<string>();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--auto" || args[i] == "--farm") continue;
                    if (args[i] == "--focus") { i++; continue; }
                    plain.Add(args[i]);
                }
                if (!plain.Contains("--stall-minutes"))
                    plain.AddRange(new[] { "--stall-minutes", Math.Min(5.0, Solve.Option(args, "--minutes", 60.0) / 2).ToString(CultureInfo.InvariantCulture) });
                return Solve.Run(plain.ToArray());
            }
            var best = run.Timing == "record" ? finishing.OrderBy(p => p.result.Time).ThenBy(p => p.result.RunTicks).First()
                : finishing.OrderBy(p => p.result.RunTicks).ThenBy(p => p.result.Time).First();
            // The best the loop starts from is a kept best too: the loop writes only what it improves on (CourseCommitted), so a seed
            // that already beats the saved route (a library route, a hand TAS) would never reach the plugin's judge, which sees only
            // offline-best files. A read-only run (--waypoints-out) writes nothing.
            if (run.WaypointsFile == null) WriteStartingBest(run, best.how, best.inputs, best.result);

            // The goals: the waypoints along the best (the start gate among them in record timing), then the finish.
            WaypointOptions options = WaypointOptions.Parse(args, line => Console.WriteLine(line));
            options.Record = run.Timing == "record" && gated;
            run.Goals = CourseWaypoints.Build(trace, best.inputs, options).Goals;
            TimerGoal = 0;
            LoopTiming = false;
            if (options.Record)
            {
                TimerGoal = run.Goals.FindIndex(g => g.kind == FullTasGoal.Start && g.course == course) + 1;
                if (TimerGoal == 0) throw new InvalidOperationException("record timing needs the start-gate goal (start:" + course + ") among the waypoints");
            }
            string synthetic = WriteCourseTrace(run, dir, best.inputs);
            Solve.CourseRuns[run.Name] = rules;
            Console.WriteLine($"Course {course} ({caps}) --auto, {run.Timing} timing, from {best.how} (record {best.result.Time:0.00}s, run {best.result.RunTicks * .02:0.00}s from the start): "
                + $"{run.Goals.Count} goals ({run.Goals.Count(g => g.kind == FullTasGoal.At)} waypoints): {string.Join(" ", run.Goals.Select(g => g.label))}; "
                + $"avoid {(rules.Avoid.Count == 0 ? "-" : string.Join(", ", rules.Avoid))}");

            var fullArgs = FullArgs(synthetic, args, run.Goals.Count);
            // The other finishing candidates are the loop's references (combine splices their leading goal runs into the best).
            foreach (var p in finishing.Where(p => p.inputs != best.inputs))
            {
                string refFile = Path.Combine(dir, run.Name + ".ref-" + finishing.IndexOf(p) + ".json");
                File.WriteAllText(refFile, JsonSerializer.Serialize(new { inputs = p.inputs.Select(t => t.ToJson()) }));
                fullArgs.Add("--ref");
                fullArgs.Add(refFile);
            }
            var loop = new FullTas(fullArgs.ToArray());
            loop._course = run;
            loop._committed = loop.CourseCommitted;
            if (run.WaypointsFile != null)
            {
                loop.Open();
                loop.WriteWaypoints(loop._best, best.result);
                return 0;
            }
            return loop.Search();
        }

        /// <summary>
        /// The Full-TAS --auto loop's arguments for a course run: its synthetic trace, then the solve's own options it takes. A
        /// run timed from a later goal (<see cref="TimerGoal"/>: a record's start gate, a farm loop's lead finish) is scored on
        /// arrival ticks that are its time only once that goal is in a window's prefix, so its windows start after that goal
        /// unless --focus says otherwise (combine, which compares the run's time, still splices anywhere).
        /// </summary>
        static List<string> FullArgs(string synthetic, string[] args, int goals)
        {
            var fullArgs = new List<string> { synthetic, "--auto", "--no-submit" };
            if (TimerGoal > 0 && !args.Contains("--focus")) fullArgs.AddRange(new[] { "--focus", (TimerGoal + 1) + "-" + goals });
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--seed" || a == "--waypoints-out" || a == "--log-json" || a == "--optional" || a == "--no-optional" || a == "--via" || a == "--avoid"
                    || a == "--stall-minutes" || a == "--rng" || a == "--tech-cache" || a == "--tech-dir" || a == "--guide-weight" || a == "--path-name")
                {
                    i++;
                    continue;
                }
                if (a == "--auto" || a == "--farm" || a == "--no-submit" || a == "--no-humanize" || a == "--no-tech-grafts" || a == "--guide" || a == "--plan") continue;
                fullArgs.Add(a);
            }
            return fullArgs;
        }

        CourseRun _course;

        /// <summary>The course trace as a Full-TAS run's: its header with the goals added and the best as its inputs, and its world.</summary>
        static string WriteCourseTrace(CourseRun run, string dir, List<InputTick> inputs)
        {
            // The run ends at its goals (GoalHeader drops a bonus's reward box and a transition's next gate: its last goal is the finish).
            var head = JsonNode.Parse(CourseWaypoints.GoalHeader(run.Header).GetRawText()).AsObject();
            foreach (string key in new[] { "ticks", "result", "inputs" }) head.Remove(key);
            // A course's run: the simulation starts it from the category's canonical start (CourseStart.cs) as a course trace's.
            head["courseAuto"] = true;
            head["inputs"] = JsonSerializer.SerializeToNode(inputs.Select(t => t.ToJson()));
            head["fulltas"] = new JsonObject
            {
                ["name"] = run.Name,
                // A category route may walk through any box (bonus5's 3.56 brushes GLOBALWatt): no buy is a stray one here.
                ["buys"] = "loose",
                ["goals"] = new JsonArray(run.Goals.Select(g =>
                {
                    var o = new JsonObject { ["label"] = g.label, ["kind"] = g.kind };
                    if (g.kind == FullTasGoal.Start || g.kind == FullTasGoal.Finish) o["course"] = g.course;
                    if (g.kind == FullTasGoal.At) o["rect"] = new JsonArray(g.x, g.y, g.w, g.h);
                    if (g.from != 0) o["from"] = g.from;
                    return (JsonNode)o;
                }).ToArray()),
            };
            string file = Path.Combine(dir, "fulltas-" + run.Name + ".json");
            File.WriteAllText(file, head.ToJsonString());
            string world = Path.ChangeExtension(run.Trace, null) + ".world.json", copy = Path.ChangeExtension(file, null) + ".world.json";
            if (!File.Exists(copy) || new FileInfo(copy).Length != new FileInfo(world).Length || File.GetLastWriteTimeUtc(copy) < File.GetLastWriteTimeUtc(world))
                File.Copy(world, copy, true);
            return file;
        }

        /// <summary>The route on a new plain course simulation (its own thread): the game's finish, timer and physics ticks, and
        /// whether it keeps the run's forced path.</summary>
        static CourseResult PlayCourse(CourseRun run, List<InputTick> inputs)
        {
            return Solve.OnThread(() =>
            {
                Simulation sim = run.Plain();
                OfflineSolver.ForcedPath rules = run.Rules ?? new OfflineSolver.ForcedPath();
                int via = 0;
                bool avoided = false;
                // A gated course's gates: its start gates and the end gates that finish it (a bonus's reward box and a transition's
                // next course have none of these).
                bool gated = !run.Header.TryGetProperty("finish", out _) && !run.Header.TryGetProperty("finishStart", out _);
                Collider2D[] hitbox = gated ? sim.Player.GetComponents<BoxCollider2D>() : null;
                List<Collider2D> starts = gated ? GateColliders(sim, run.Course, true) : null, ends = gated ? GateColliders(sim, run.Course, false) : null;
                var ticks = new List<long>();
                var toStart = new List<float>();
                var toEnd = new List<float>();
                var timing = new List<bool>();
                var body = new List<(float x, float y)>();
                Action<int> onStep = _ =>
                {
                    if (!rules.IsEmpty)
                    {
                        WorldPos p = sim.PlayerWorld;
                        if (via < rules.Via.Count && rules.Via[via].Contains(p)) via++;
                        foreach (OfflineSolver.Box box in rules.Avoid) if (box.Contains(p)) avoided = true;
                    }
                    if (!gated) return;
                    ticks.Add(sim.Engine.PhysicsTicks);
                    toStart.Add(Nearest(sim, hitbox, starts));
                    toEnd.Add(Nearest(sim, hitbox, ends));
                    timing.Add(sim.TimingCourse()?.courseNumber == run.Course);
                    var b = sim.Physics.PlayerBody();
                    ScenePos origin = sim.WorldOriginInScene;
                    body.Add((b.px - origin.x, b.py - origin.y));
                };
                RunResult r = sim.Run(inputs, null, null, 1000000, onStep);
                return new CourseResult { Finished = r.Finished, Time = r.Time, RunTicks = r.PhysicsTicks, EndReason = r.EndReason,
                    OffPath = avoided || via < rules.Via.Count, Bus = gated && r.Finished ? MeasureBus(ticks, toStart, toEnd, timing, body) : null };
            });
        }

        /// <summary>The course's start gates (start) or the end gates that finish it.</summary>
        static List<Collider2D> GateColliders(Simulation sim, int course, bool start)
        {
            IEnumerable<MonoBehaviour> gates = start ? sim.World.All<startGate>()
                : sim.World.All<endGate>().Where(g => DistanceField.EndOfCourse == null || (bool)DistanceField.EndOfCourse.GetValue(g));
            return gates.Where(g => g.GetComponentInParent<courseScript>()?.courseNumber == course)
                .Select(g => (Collider2D)g.GetComponent<BoxCollider2D>()).Where(c => c != null).ToList();
        }

        /// <summary>The smallest gap between the player's enabled boxes that can touch the triggers (the hurtbox) and the live
        /// triggers (+inf: none live).</summary>
        static float Nearest(Simulation sim, Collider2D[] hitbox, List<Collider2D> triggers)
        {
            float best = float.PositiveInfinity;
            foreach (Collider2D a in hitbox)
            {
                if (!a.enabled) continue;
                foreach (Collider2D b in triggers)
                {
                    if (!b.enabled || !b.gameObject.activeInHierarchy || !sim.Physics.CanTouch(a, b)) continue;
                    ColliderDistance2D d = a.Distance(b);
                    if (d.isValid) best = Math.Min(best, d.distance);
                }
            }
            return best;
        }

        /// <summary>The run's gate and finish margins from its per-tick gaps (the run finished on its last tick).</summary>
        static Bus MeasureBus(List<long> ticks, List<float> toStart, List<float> toEnd, List<bool> timing, List<(float x, float y)> body)
        {
            int n = ticks.Count;
            if (n < 2) return null;
            float Travel(int i) => (float)Math.Sqrt(Math.Pow(body[i].x - body[i - 1].x, 2) + Math.Pow(body[i].y - body[i - 1].y, 2));
            var bus = new Bus { Finish = new BusEdge { Tick = ticks[n - 1], Before = toEnd[n - 2], At = toEnd[n - 1], Travel = Travel(n - 1) } };
            // The timer's last start before the finish (an earlier start was a restart's). startGate starts it on the Stay, so
            // the touch entered the tick before; walking back over overlapping ticks would cross a touch that lapsed at a step's
            // start (a write pulled the hurtbox out and the step moved it back in: Enter again).
            int t = -1;
            for (int i = 1; i < n; i++) if (timing[i] && !timing[i - 1]) t = i;
            if (t < 2) return bus;
            int e = t - 1;
            bus.Gate = new BusEdge { Tick = ticks[e], TimerTick = ticks[t], Before = toStart[e - 1], At = toStart[e], Travel = Travel(e) };
            return bus;
        }

        /// <summary>A kept best: replayed on the course's own simulation, written to offline-best as solve writes it (the plugin
        /// judges it for the record and the farm), and its waypoint splits.</summary>
        void CourseCommitted(Route r, string where)
        {
            if (r.Progress < _goals) return;
            if (_course.Loop)
            {
                LoopCommitted(r, where);
                return;
            }
            CourseResult c = PlayCourse(_course, r.Inputs);
            WriteWaypoints(r, c);
            if (!c.Finished)
            {
                Log($"course check: the course's own simulation doesn't finish {Describe(r)} ({c.EndReason}); not written");
                return;
            }
            long timer = (long)Math.Round(c.Time / _dt), want = _course.Timing == "record" ? timer : c.RunTicks;
            if (want != r.Last)
                Log($"course check: DISAGREES: the course's simulation times it {(_course.Timing == "record" ? $"{timer} ticks on the game timer" : $"{c.RunTicks} physics ticks")}, the loop {r.Last}");
            WriteRecordBest(_course, r.Inputs, c, "auto: " + where);
        }

        /// <summary>Where a run keeps its last --auto best (under the traces folder); a candidate by that path is that best.</summary>
        const string AutoRunDir = "course-auto/fulltas/";

        /// <summary>The offline-best file of a finishing route as `solve` writes it (the game timer's time, `runTicks`): path and text.</summary>
        static (string file, string text) RecordBestFile(CourseRun run, List<InputTick> inputs, CourseResult c)
        {
            string file = Path.Combine(Path.GetDirectoryName(run.Trace), "offline-best", run.Label + "-" + c.Time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
            return (file, JsonSerializer.Serialize(new { course = run.Course, capabilities = run.Caps, time = c.Time, runTicks = c.RunTicks, ticks = inputs.Select(t => t.ToJson()) }));
        }

        /// <summary>A finishing route written to offline-best as `solve` writes it, and the `offline best` line the plugin reads.</summary>
        static void WriteRecordBest(CourseRun run, List<InputTick> inputs, CourseResult c, string how)
        {
            var (file, text) = RecordBestFile(run, inputs, c);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            Solve.WriteShared(file, text);
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] offline best {c.Time:0.00}s via {how} ({inputs.Count} ticks, run {c.RunTicks * .02:0.00}s from the start) -> {Path.GetFileName(file)}");
            if (run.LogJson != null)
                File.AppendAllText(run.LogJson, JsonSerializer.Serialize(new { wall = run.Clock.Elapsed.TotalSeconds, time = c.Time, runTicks = c.RunTicks, ticks = 0, how = how.StartsWith("auto", StringComparison.Ordinal) ? "auto" : "start" }) + "\n");
        }

        /// <summary>
        /// The route a run starts from, written like a kept best. Not the last --auto best (the loop wrote it when it kept it), and
        /// not a route whose file is already there, waiting or judged (the same inputs and time: a rerun writes nothing twice).
        /// The plugin judges a file only when it ties or beats the saved route, so a slower start is moved to judged\ unjudged.
        /// </summary>
        static void WriteStartingBest(CourseRun run, string how, List<InputTick> inputs, CourseResult c)
        {
            if (how.StartsWith(AutoRunDir, StringComparison.Ordinal)) return;
            var (file, text) = RecordBestFile(run, inputs, c);
            foreach (string folder in new[] { Path.GetDirectoryName(file), Path.Combine(Path.GetDirectoryName(file), "judged") })
                if (File.Exists(Path.Combine(folder, Path.GetFileName(file))) && File.ReadAllText(Path.Combine(folder, Path.GetFileName(file))) == text) return;
            WriteRecordBest(run, inputs, c, "start: " + how);
        }

        /// <summary>The waypoints with the route's splits (--waypoints FILE and the `waypoints` line on stdout, for the studio).</summary>
        void WriteWaypoints(Route r, CourseResult c)
        {
            string json = JsonSerializer.Serialize(new
            {
                timing = _course.Timing,
                route = Describe(r),
                goals = _labels,
                rects = _course.Goals.Select(g => g.kind == FullTasGoal.At ? new[] { g.x, g.y, g.w, g.h } : null),
                // The same rects in the world frame (world = capture - the capture's FloatingOrigin): courses.json's, the map's.
                world = _course.Goals.Select(g =>
                {
                    if (g.kind != FullTasGoal.At) return null;
                    WorldPos at = _eval.ToWorld(new CapturePos(g.x, g.y));
                    return new[] { at.x, at.y, g.w, g.h };
                }),
                splits = Enumerable.Range(0, _goals).Select(k => k < r.Progress ? r.GoalPhysics[k] : -1),
                timerGoal = TimerGoal,
                record = c.Finished ? (long?)Math.Round(c.Time / _dt) : null,
                farm = c.Finished ? (long?)c.RunTicks : null,
                bus = c.Bus == null ? null : new { gate = BusJson(c.Bus.Gate), finish = BusJson(c.Bus.Finish) },
            });
            Console.WriteLine("waypoints " + json);
            if (_course.WaypointsFile != null) Solve.WriteShared(_course.WaypointsFile, json);
        }

        static object BusJson(BusEdge e)
        {
            static float? Finite(float v) => float.IsFinite(v) ? (float)Math.Round(v, 3) : null;
            return e == null ? null : new { tick = e.Tick, timerTick = e.TimerTick == 0 ? (long?)null : e.TimerTick, before = Finite(e.Before), at = Finite(e.At), travel = Finite(e.Travel) };
        }
    }
}
