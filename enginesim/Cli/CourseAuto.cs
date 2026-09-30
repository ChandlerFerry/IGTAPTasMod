using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
            public List<FullTasGoal> Goals;
            public Stopwatch Clock;
        }

        /// <summary>A route played on the plain course simulation (the game's own finish and timer).</summary>
        sealed class CourseResult
        {
            public bool Finished;
            public float Time;
            public long RunTicks;
            public string EndReason;
        }

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
            OfflineSolver.ForcedPath rules = Solve.ForcedPaths(args, course, caps);
            run.Name = "course" + course + "_" + caps + (optional.Count == 0 ? "" : "-opt-" + string.Join("+", optional)) + (run.Timing == "farm" ? "-farm" : "");
            string dir = Path.Combine(Path.GetDirectoryName(trace), "course-auto");
            Directory.CreateDirectory(dir);
            if (run.LogJson != null) File.WriteAllText(run.LogJson, "");

            // The category's unlocks: the start's player has them (the capture set them); a trace captured with others isn't this category.
            string startCaps = Solve.OnThread(() => PlayerCaps.Of(run.Plain().Player).Id);
            if (startCaps != caps)
                throw new InvalidOperationException($"{Path.GetFileName(trace)} is {caps}, but its start's player has {startCaps} unlocked: capture the category's start again");

            // The loop starts from the fastest of the trace's route, every --seed and the last --auto best (on this timing).
            var candidates = new List<(string how, List<InputTick> inputs)> { (Path.GetFileName(trace), Solve.ReadInputs(header)) };
            foreach (string seed in Solve.Args(args, "--seed")) candidates.Add((Path.GetFileName(seed), global::IGTAP.EngineSim.Cli.Plan.ReadRoute(seed)));
            string runFile = Path.Combine(dir, "fulltas", run.Name + ".json");
            if (!args.Contains("--fresh") && File.Exists(runFile)) candidates.Add(("course-auto/fulltas/" + Path.GetFileName(runFile), global::IGTAP.EngineSim.Cli.Plan.ReadRoute(runFile)));
            var played = candidates.Select(c => (c.how, c.inputs, result: PlayCourse(run, c.inputs))).ToList();
            foreach (var p in played)
                Console.WriteLine($"{p.how}: " + (p.result.Finished ? $"record {p.result.Time:0.00}s, farm {p.result.RunTicks * .02:0.00}s ({p.result.RunTicks} ticks)" : "no finish (" + p.result.EndReason + ")"));
            var finishing = played.Where(p => p.result.Finished).ToList();
            if (finishing.Count == 0)
            {
                if (run.WaypointsFile != null)
                    throw new InvalidOperationException("--auto improves a complete route: neither the trace's route nor a --seed finishes the course");
                // Only a narrower category's routes (the seeds) and none finishes here (an added OM or ORB changes what the same
                // inputs do): nothing to improve yet, so the plain solve finds a first finish, those routes still its seeds.
                Console.WriteLine("--auto: neither the trace's route nor a --seed finishes the course; solving for a first finish instead");
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

            // The goals: the waypoints along the best (the start gate among them in record timing), then the finish.
            WaypointOptions options = WaypointOptions.Parse(args, line => Console.WriteLine(line));
            options.Record = run.Timing == "record" && gated;
            run.Goals = CourseWaypoints.Build(trace, best.inputs, options).Goals;
            TimerGoal = 0;
            if (options.Record)
            {
                TimerGoal = run.Goals.FindIndex(g => g.kind == FullTasGoal.Start && g.course == course) + 1;
                if (TimerGoal == 0) throw new InvalidOperationException("record timing needs the start-gate goal (start:" + course + ") among the waypoints");
            }
            string synthetic = WriteCourseTrace(run, dir, best.inputs);
            Solve.CourseRuns[run.Name] = rules;
            Console.WriteLine($"Course {course} ({caps}) --auto, {run.Timing} timing, from {best.how} (record {best.result.Time:0.00}s, farm {best.result.RunTicks * .02:0.00}s): "
                + $"{run.Goals.Count} goals ({run.Goals.Count(g => g.kind == FullTasGoal.At)} waypoints): {string.Join(" ", run.Goals.Select(g => g.label))}; "
                + $"avoid {(rules.Avoid.Count == 0 ? "-" : string.Join(", ", rules.Avoid))}");

            var fullArgs = new List<string> { synthetic, "--auto", "--no-submit" };
            // Record timing: a window is scored on its arrival's physics tick, which only matches the game timer once the gate
            // crossing is in its prefix; windows from the quick restart would chase the farm instead. So the loop's windows start
            // after the gate unless --focus says otherwise (combine, which compares the timer, still splices anywhere).
            if (TimerGoal > 0 && !args.Contains("--focus")) fullArgs.AddRange(new[] { "--focus", (TimerGoal + 1) + "-" + run.Goals.Count });
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

        /// <summary>The route on a new plain course simulation (its own thread): the game's finish, timer and physics ticks.</summary>
        static CourseResult PlayCourse(CourseRun run, List<InputTick> inputs)
        {
            return Solve.OnThread(() =>
            {
                RunResult r = run.Plain().Run(inputs, null, null, 1000000);
                return new CourseResult { Finished = r.Finished, Time = r.Time, RunTicks = r.PhysicsTicks, EndReason = r.EndReason };
            });
        }

        /// <summary>A kept best: replayed on the course's own simulation, written to offline-best as solve writes it (the plugin
        /// judges it for the record and the farm), and its waypoint splits.</summary>
        void CourseCommitted(Route r, string where)
        {
            if (r.Progress < _goals) return;
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
            string outDir = Path.Combine(Path.GetDirectoryName(_course.Trace), "offline-best");
            Directory.CreateDirectory(outDir);
            string file = Path.Combine(outDir, _course.Label + "-" + c.Time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
            Solve.WriteShared(file, JsonSerializer.Serialize(new { course = _course.Course, capabilities = _course.Caps, time = c.Time, runTicks = c.RunTicks, ticks = r.Inputs.Select(t => t.ToJson()) }));
            Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] offline best {c.Time:0.00}s via auto: {where} ({r.Inputs.Count} ticks, run {c.RunTicks * .02:0.00}s from the start) -> {Path.GetFileName(file)}");
            if (_course.LogJson != null)
                File.AppendAllText(_course.LogJson, JsonSerializer.Serialize(new { wall = _course.Clock.Elapsed.TotalSeconds, time = c.Time, runTicks = c.RunTicks, ticks = 0, how = "auto" }) + "\n");
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
            });
            Console.WriteLine("waypoints " + json);
            if (_course.WaypointsFile != null) Solve.WriteShared(_course.WaypointsFile, json);
        }
    }
}
