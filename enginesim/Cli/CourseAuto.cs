using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using IGTAP.EngineSim.Cli.Runs;
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
            /// <summary>--class human-major|human-nmg with --waypoints-out: the record as a human class plays it
            /// (MacroEngine.HumanClass: digital stick, two jump buttons); null: by the game timer.</summary>
            public string Human;
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
            /// <summary>A two-way course's directions the route keeps (Categories.Directions: the direction blocks it never cuts), else null.</summary>
            public string[] Directions;
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
            /// <summary>The start gate only: the gap on the tick after the timer's start (NaN: none). A shallow <see cref="At"/>
            /// delays the start a tick only if the hurtbox still overlaps the gate then (≤ 0); a route that leaves the gate there
            /// (a facing flip, a wall jump) has no such tick to give, and its near miss is false.</summary>
            public float After = float.NaN;
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
            string cls = Solve.Option(args, "--class", "major");
            if (cls != "major")
            {
                if (!Glitches.Human(Glitches.Parse(cls))) throw new ArgumentException("solve --auto --class: human-major or human-nmg (a course keeps nmg and major routes by time)");
                if (run.Timing == "farm") throw new ArgumentException("--class " + cls + " improves the record: a human class has no farm");
                run.Human = cls;
                // A human class trades time for ease, which the loop's windows can't (they rank finds by time): ease searches
                // on the difficulty itself, from every seed (the class's record among them), and writes its lowest score.
                if (run.WaypointsFile == null) return Difficulty.Ease(args);
            }
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
                    if (args[i] == "--whole-minutes")
                    {
                        rest.AddRange(new[] { "--whole-minutes", (Solve.Option(args, "--whole-minutes", 0.0) / 2).ToString(CultureInfo.InvariantCulture) });
                        i++;
                        continue;
                    }
                    rest.Add(args[i]);
                }
                // Each way is a try: the run fails only when neither way gets a route (Course 3's farm loops one way).
                int code = int.MaxValue;
                foreach (string block in run.Timing == "farm" ? new[] { "avoid-backwards", "avoid-forwards" } : new[] { "avoid-forwards", "avoid-backwards" })
                {
                    Console.WriteLine($"--auto: course {course} goes either way from its start; {block} for {minutes / 2:0.##} min");
                    code = Math.Min(code, CourseAuto(rest.Concat(new[] { "--optional", block, "--minutes", (minutes / 2).ToString(CultureInfo.InvariantCulture) }).ToArray()));
                }
                return code;
            }
            OfflineSolver.ForcedPath rules = Solve.ForcedPaths(args, course, caps);
            run.Rules = rules;
            run.Name = "course" + course + "_" + caps + (optional.Count == 0 ? "" : "-opt-" + string.Join("+", optional)) + (run.Timing == "farm" ? "-farm" : "");
            // A class's own run file (FullTas.Suffix): fulltas/<name>.<class>.json.
            string runKey = run.Name + (run.Human == null ? "" : "." + run.Human);
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
            string runFile = Path.Combine(dir, "fulltas", runKey + ".json");
            if (!args.Contains("--fresh") && File.Exists(runFile)) candidates.Add((AutoRunDir + Path.GetFileName(runFile), global::IGTAP.EngineSim.Cli.Plan.ReadRoute(runFile)));
            // A gated course's farm is its steady-state loop, finish to finish (README ### Rules 30).
            if (run.Timing == "farm" && gated) return LoopAuto(run, args, candidates, dir);
            // A human class holds digital sticks: each candidate also as its part tilts snap to them.
            if (run.Human != null) candidates.AddRange(candidates.ToList().Select(c => (c.how + " (digital)", Digital(c.inputs))));
            var played = candidates.Select(c => (c.how, c.inputs, result: PlayCourse(run, c.inputs))).ToList();
            foreach (var p in played)
                Console.WriteLine($"{p.how}: " + (!p.result.Finished ? "no finish (" + p.result.EndReason + ")"
                    : $"record {p.result.Time:0.00}s, run {p.result.RunTicks * .02:0.00}s from the start ({p.result.RunTicks} ticks)" + (p.result.OffPath ? ", off the forced path (an avoid box or a missed via)" : "")));
            // The waypoints follow the best, so it has to keep the run's rules: one through an avoid box (the direction a picked
            // block shuts) would set every window's goals where the windows are cut.
            var finishing = played.Where(p => p.result.Finished && !p.result.OffPath).ToList();
            if (finishing.Count == 0 && run.Human != null)
                throw new InvalidOperationException($"No route a {run.Human} class plays to the finish yet: Improve reaches one first");
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
            double total = Solve.Option(args, "--minutes", 60.0);
            // The whole-route search first, as a target with no route of its own gets it on every Improve (Solver.cs CourseSolveArgs):
            // the loop's windows judge a find at a waypoint and lose most of them at the merge, where the plain solve joins an earlier
            // arrival at a state cell onto the best's own inputs and judges the whole course (enginesim/README.md, "Course --auto").
            if (run.Human == null && run.WaypointsFile == null && !args.Contains("--focus") && !args.Contains("--no-whole"))
            {
                List<InputTick> whole = WholeRoute(run, args, args.Contains("--fresh") ? null : runFile);
                CourseResult wholeResult = whole == null ? null : PlayCourse(run, whole);
                if (wholeResult != null && wholeResult.Finished && !wholeResult.OffPath && !finishing.Any(p => p.inputs.SequenceEqual(whole))) finishing.Add((WholeHow, whole, wholeResult));
            }
            var best = run.Timing == "record" ? finishing.OrderBy(p => p.result.Time).ThenBy(p => p.result.RunTicks).First()
                : finishing.OrderBy(p => p.result.RunTicks).ThenBy(p => p.result.Time).First();
            // The best the polish starts from is a kept best too: the polish writes only what it improves on, so a seed that already
            // beats the saved route (a library route, a hand TAS) would never reach the plugin's judge, which sees only offline-best
            // files. A read-only run (--waypoints-out) writes nothing.
            if (run.WaypointsFile == null && run.Human == null) WriteStartingBest(run, best.how, best.inputs, best.result);

            // The goals: the waypoints along the best (the start gate among them in record timing), then the finish.
            WaypointOptions options = WaypointOptions.Parse(args, line => Console.WriteLine(line));
            options.Record = run.Timing == "record" && gated;
            run.Goals = CourseWaypoints.Build(trace, best.inputs, options).Goals;
            int timerGoal = options.Record ? run.Goals.FindIndex(g => g.kind == FullTasGoal.Start && g.course == course) + 1 : 0;
            if (options.Record && timerGoal == 0) throw new InvalidOperationException("record timing needs the start-gate goal (start:" + course + ") among the waypoints");
            if (run.WaypointsFile != null)
            {
                WriteWaypoints(run, best.inputs, best.result, timerGoal);
                return 0;
            }
            string synthetic = WriteCourseTrace(run, dir, best.inputs);
            Solve.CourseRuns[run.Name] = rules;
            Console.WriteLine($"Course {course} ({caps}) --auto, {run.Timing} timing, from {best.how} (record {best.result.Time:0.00}s, run {best.result.RunTicks * .02:0.00}s from the start): "
                + $"{run.Goals.Count} goals ({run.Goals.Count(g => g.kind == FullTasGoal.At)} waypoints): {string.Join(" ", run.Goals.Select(g => g.label))}; "
                + $"avoid {(rules.Avoid.Count == 0 ? "-" : string.Join(", ", rules.Avoid))}");
            (int from, int to) = Span(args, run.Goals.Count, timerGoal);
            Polish(run, synthetic, from, to, total - run.Clock.Elapsed.TotalMinutes, true, inputs => CourseScore(run, inputs), (inputs, how) => KeepRecord(run, runFile, inputs, how, timerGoal));
            return 0;
        }

        /// <summary>--focus a-b: exactly those goals (from 1, the studio's waypoint list); else the goals after <paramref name="after"/>
        /// (a record's start gate: its timer starts there; a farm loop's lead), a bonus's or transition's all. 0-based, inclusive.</summary>
        static (int from, int to) Span(string[] args, int count, int after)
        {
            string focus = Solve.Option(args, "--focus", (string)null);
            if (focus == null) return (after, count - 1);
            string[] ends = focus.Split('-');
            return (Math.Clamp(int.Parse(ends[0], CultureInfo.InvariantCulture) - 1, 0, count - 1), Math.Clamp(int.Parse(ends[^1], CultureInfo.InvariantCulture) - 1, 0, count - 1));
        }

        /// <summary>
        /// Improve's polish on v2 (what --focus drives): each goal from <paramref name="from"/> to <paramref name="to"/> searched again
        /// from the best's own state there and the goals after it rebuilt (Improver.RunSegment), kept when the whole route scores
        /// lower (<paramref name="score"/>, RunContext.Score); pass after pass until one gains nothing or the minutes are up. A goal is
        /// searched once per best; the start gate never (it only marks where the timer starts). <paramref name="kept"/> gets each new best.
        /// </summary>
        static void Polish(CourseRun run, string synthetic, int from, int to, double minutes, bool tickTimed, Func<List<InputTick>, long> score,
            Action<List<InputTick>, string> kept)
        {
            var (ctx, lib, facts, search) = V2(synthetic, tickTimed, minutes);
            ctx.Score = score;
            var plan = new RunPlan(ctx.HeaderIds, ctx.HeaderGoals.Select(g => g.label).ToArray());
            List<InputTick> best = Solve.ReadInputs(ctx.Header);
            var tried = new HashSet<(int, ulong)>();
            int pulls = 0;
            void Log(string m) => Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] {m}");
            Log($"polish: goals {from + 1}-{to + 1} of {plan.Count} for {minutes:0.##} min");
            for (bool gained = true; gained && !search.Stop();)
            {
                gained = false;
                for (int j = from; j <= to && !search.Stop(); j++)
                {
                    if (plan.Labels[j] == "start:" + run.Course || !tried.Add((j, SnapshotIndex.Hash(best, best.Count)))) continue;
                    // ponytail: a 20M-tick search per goal and 3 of 20M for the rebuild after it (the Full-TAS improver's searched
                    // candidates); raise them if polishes stall short of the minutes.
                    var improver = new Improver(ctx, lib, search, facts, 1)
                        { SegmentBudget = Runs.Builder.Budgets[0], RebuildBudget = Runs.Builder.Budgets[0], SegmentSearches = 3, Log = Log };
                    improver.NewBest += (b, how) =>
                    {
                        if (how == "start") return;
                        best = b.Inputs;
                        gained = true;
                        kept(b.Inputs, how);
                    };
                    improver.RunSegment(plan, best, j, search.Stop);
                    // Each search's 16 solvers are garbage now: server GC would let them pile up toward its hard limit.
                    GC.Collect();
                    Console.WriteLine("@fulltas " + JsonSerializer.Serialize(new { type = "pull", pull = ++pulls }));
                }
            }
            lib.Save();
            Log("polish: " + (search.Stop() ? "the minutes are up" : "converged: no goal of the span gains"));
        }

        /// <summary>A course route's score (RunContext.Score): a record's game timer in ticks (README ### Rules 27-29), ties by its
        /// ticks from the run's start; a bonus's or transition's ticks from its start (31); long.MaxValue without the course's finish
        /// on its forced path.</summary>
        static long CourseScore(CourseRun run, List<InputTick> inputs)
        {
            CourseResult c = PlayCourse(run, inputs);
            if (!c.Finished || c.OffPath) return long.MaxValue;
            return run.Timing == "record" ? (long)Math.Round(c.Time / .02f) * 1_000_000 + c.RunTicks : c.RunTicks;
        }

        /// <summary>A kept record best: replayed on the course's own simulation, its idle pauses dropped where the course time holds,
        /// written to offline-best as solve writes it (the plugin judges it), as the next Improve's candidate, and its waypoints.</summary>
        static void KeepRecord(CourseRun run, string runFile, List<InputTick> found, string how, int timerGoal)
        {
            CourseResult c = PlayCourse(run, found);
            List<InputTick> inputs = OfflineSolver.DropIdlePauses(found, without =>
            {
                CourseResult bare = PlayCourse(run, without);
                if (!bare.Finished || bare.OffPath || OfflineSolver.Beats(c.Time, c.RunTicks, bare.Time, bare.RunTicks)) return false;
                c = bare;
                return true;
            });
            WriteRecordBest(run, inputs, c, "auto: " + how);
            Directory.CreateDirectory(Path.GetDirectoryName(runFile));
            Solve.WriteShared(runFile, JsonSerializer.Serialize(new { inputs = inputs.Select(t => t.ToJson()) }));
            WriteWaypoints(run, inputs, c, timerGoal);
        }

        /// <summary>The route's sticks as a human class holds them: a part tilt snapped to full (half or more) or to rest. The
        /// vertical is the game's own snap already (MacroEngine.ControllerAxis).</summary>
        internal static List<InputTick> Digital(List<InputTick> inputs)
        {
            static float Snap(float v) => Math.Abs(v) >= .5f ? Math.Sign(v) : 0f;
            static float? SnapOr(float? v) => v.HasValue ? Snap(v.Value) : (float?)null;
            return inputs.Select(t =>
            {
                t.x = Snap(t.x);
                t.lockX = SnapOr(t.lockX);
                t.lockY = SnapOr(t.lockY);
                t.turnX = SnapOr(t.turnX);
                t.turnY = SnapOr(t.turnY);
                return t;
            }).ToList();
        }

        /// <summary>
        /// A first finish for a target no seed finishes in its own category, from its seeds of other categories: the fastest that
        /// finishes in its own category's trace (with more unlocks: an omnidash route) gives the way, as waypoints along its path
        /// (CourseWaypoints, carried to this trace's frame), and the v2 builder (<see cref="Reach"/>, the target's forced path)
        /// takes its inputs on from where they break here, waypoint to waypoint, to the finish. A waypoint no search gets to is
        /// dropped and the build goes on from the one before. A long way is short hops then: tree27to10 `WJ_DS_DJ_BS`, whose
        /// `ZIP_OM` and `ORB_ZIP_OM` routes climb the same corridor (up the left side to y 6400, along the top, past the
        /// checkpoint at (25844, 6264), right at y 5800), had no finish from the plain solve in an hour: aimed at the finish, the
        /// search spreads over the open floor to the right. This is only a first finish, the baseline. The route, or null; at most <paramref name="minutes"/>.
        /// </summary>
        internal static List<InputTick> FirstFinish(string trace, JsonElement header, OfflineSolver.ForcedPath rules, List<List<InputTick>> seeds,
            IReadOnlyList<string> seedPaths, double minutes)
        {
            trace = Path.GetFullPath(trace);
            if (header.TryGetProperty("fulltas", out _)) return null;
            CourseRun run = Run(trace, header, rules);
            if (seeds.Any(s => s.Count > 0 && PlayCourse(run, s) is { Finished: true, OffPath: false })) return null;
            // The guide: the seed of another category that finishes fastest in that category's own trace.
            string dir = Path.GetDirectoryName(trace);
            (string trace, List<InputTick> inputs, long ticks, string name)? guide = null;
            var traces = new Dictionary<string, string>();
            foreach (string seed in seedPaths)
            {
                string name;
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(seed), new JsonDocumentOptions { MaxDepth = 256 }))
                    name = doc.RootElement.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                string label = name == null ? null : name.Split(' ')[0];
                if (label == null || label.IndexOf('_') < 0) continue;
                string caps = label.Substring(label.IndexOf('_') + 1).Replace("_", "").ToLowerInvariant();
                if (caps == run.Caps) continue;
                if (!traces.TryGetValue(caps, out string own)) traces[caps] = own = CategoryTrace(dir, run.Course, caps);
                if (own == null) continue;
                List<InputTick> inputs = global::IGTAP.EngineSim.Cli.Plan.ReadRoute(seed);
                CourseResult c = PlayCourse(Run(own, Solve.ReadHeader(own), null), inputs);
                if (c.Finished && (guide == null || c.RunTicks < guide.Value.ticks)) guide = (own, inputs, c.RunTicks, name);
            }
            if (guide == null) return null;
            var (finish, origin) = Ends(run);
            CapturePos from = Solve.OnThread(() => Run(guide.Value.trace, Solve.ReadHeader(guide.Value.trace), null).Plain().WorldOriginInCapture);
            var options = new WaypointOptions { Stretches = 12, Record = false };
            List<FullTasGoal> goals = CourseWaypoints.Build(guide.Value.trace, guide.Value.inputs, options).Goals.Where(g => g.kind == FullTasGoal.At).SkipLast(1)
                .Select(g => AtGoal(g.x - from.x + origin.x, g.y - from.y + origin.y, g.w, g.h, g.from)).Append(finish).ToList();
            string autoDir = Path.Combine(dir, "course-auto");
            Directory.CreateDirectory(autoDir);
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] no seed finishes {run.Caps}: reaching the finish along {guide.Value.name}'s way ({goals.Count - 1} waypoints)");
            List<InputTick> found = Reach(run, autoDir, "course" + run.Course + "_" + run.Caps + "-first", goals, guide.Value.inputs, rules, minutes - run.Clock.Elapsed.TotalMinutes, true);
            if (found == null) return null;
            CourseResult r = PlayCourse(run, found);
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] first finish: " + (r.Finished && !r.OffPath ? $"{r.RunTicks * .02:0.00}s ({r.RunTicks} ticks)"
                : r.Finished ? "off the forced path" : "no finish on the course (" + r.EndReason + ")"));
            return r.Finished && !r.OffPath ? found : null;
        }

        /// <summary>run-selftest --course-reach &lt;trace&gt; &lt;best&gt;: the best's first half carried, the rest reached through its
        /// waypoints to the finish (a fresh library); null when the reached route finishes on the forced path, else why not.</summary>
        internal static string ReachSelfTest(string trace, List<InputTick> best, double minutes)
        {
            trace = Path.GetFullPath(trace);
            JsonElement header = Solve.ReadHeader(trace);
            CourseRun run = Run(trace, header, Solve.ForcedPaths(new[] { trace }, header.GetProperty("course").GetInt32(), header.GetProperty("capabilities").GetString()));
            string dir = Path.Combine(Path.GetDirectoryName(trace), "course-auto"), name = "course" + run.Course + "_" + run.Caps + "-reachtest";
            Directory.CreateDirectory(Path.Combine(dir, "fulltas"));
            foreach (string ext in new[] { ".segments.json", ".failures.json" }) File.Delete(Path.Combine(dir, "fulltas", name + ext));
            List<FullTasGoal> goals = CourseWaypoints.Build(trace, best, new WaypointOptions { Record = false }).Goals;
            List<InputTick> found = Reach(run, dir, name, goals, best.Take(best.Count / 2).ToList(), run.Rules, minutes, true);
            if (found == null) return "no route reached the finish";
            CourseResult c = PlayCourse(run, found);
            return c.Finished && !c.OffPath ? null : c.Finished ? "the route leaves the forced path" : "the route doesn't finish on the course: " + c.EndReason;
        }

        /// <summary>A course run of <paramref name="trace"/> for the reach windows and PlayCourse (no name yet).</summary>
        static CourseRun Run(string trace, JsonElement header, OfflineSolver.ForcedPath rules)
        {
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            return new CourseRun { Trace = trace, Course = course, Caps = caps, Header = header, Label = course + "_" + caps, Timing = "farm",
                Plain = Solve.Creator(trace, header), Clock = Stopwatch.StartNew(), Rules = rules };
        }

        /// <summary>The run's finish goal and the world origin in its capture frame.</summary>
        static (FullTasGoal finish, CapturePos origin) Ends(CourseRun run)
        {
            return Solve.OnThread(() => { Simulation sim = run.Plain(); return (CourseWaypoints.Finish(sim, run.Header), sim.WorldOriginInCapture); });
        }

        static FullTasGoal AtGoal(float x, float y, float w, float h, int from = 0)
        {
            var g = new FullTasGoal { kind = FullTasGoal.At, x = x, y = y, w = w, h = h, from = from };
            g.label = FormattableString.Invariant($"at:{x:0.#},{y:0.#},{w:0.#},{h:0.#}");
            return g;
        }

        /// <summary>The newest trace of this course in another category beside it (optimize-c..., start-c..., map-c...), or null.</summary>
        static string CategoryTrace(string dir, int course, string caps)
        {
            foreach (string file in Directory.GetFiles(dir, "*-" + caps + ".json").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (!File.Exists(Path.ChangeExtension(file, null) + ".world.json")) continue;
                JsonElement h = Solve.ReadHeader(file);
                if (h.TryGetProperty("fulltas", out _) || !h.TryGetProperty("course", out JsonElement c) || c.GetInt32() != course
                    || h.GetProperty("capabilities").GetString() != caps) continue;
                return file;
            }
            return null;
        }

        /// <summary>
        /// The plain solve's walks (--walk-seed): each call, the solver's explored cells nearest each walker's start (as a reach
        /// window's CellWalks), walked there and the walker played on from there, offered to the solver as seeds (Offer): it
        /// judges each on the course and explores on from where it goes, finished or not. Played exactly they seldom finish: by
        /// the atom room Course 9 · WJ_DS_DJ_BS stands a unit above tree0to9's start, and the wall bounce at its tick 44 misses.
        /// Null when no walk seed has a start under these unlocks.
        /// </summary>
        internal static Action<OfflineSolver> PlainWalks(Func<Simulation> create, IReadOnlyList<string> walkSeeds, string caps, Action<string> log)
        {
            IGTAPTasMod.Caps runCaps = IGTAPTasMod.Caps.Parse(caps);
            List<Seed> walkers = walkSeeds.Select(ReadSeed).Where(s => s.At != null && s.Caps.IsSubsetOf(runCaps)).ToList();
            if (walkers.Count == 0) return null;
            Simulation eval = create();
            SimSnapshot origin = eval.Start;
            var tried = new HashSet<long>();
            return solver =>
            {
                foreach (Seed s in walkers)
                {
                    eval.SetStart(origin, 0);
                    var spot = eval.ToCapture(new WorldPos(s.At.X, s.At.Y)).AsVector2();
                    foreach (List<InputTick> cell in solver.Near(spot, _ => true, MaxWalk, CellWalkRise, tried, CellWalksPerCheck))
                    {
                        eval.SetStart(origin, 0);
                        SimSnapshot there = eval.Advance(cell);
                        if (there == null) continue;
                        eval.SetStart(there, cell.Count);
                        var walks = WalkedStarts(eval, s);
                        foreach (var (_, walk) in walks) solver.Offer(cell.Concat(walk).ToList());
                        if (walks.Count > 0) log($"{s.Name}: {walks.Count} walks to its start from an explored cell {cell.Count} inputs in");
                    }
                }
            };
        }

        /// <summary>
        /// A course run's goals reached on v2 (FirstFinish, RepairLoopLap): <paramref name="carried"/> harvested goal by goal, the
        /// rest built (searched) from where it breaks, under <paramref name="path"/>; a waypoint no search reaches is dropped and the
        /// rest built again. The route through every goal left, or null: the finish out of reach, or the minutes up.
        /// </summary>
        static List<InputTick> Reach(CourseRun run, string dir, string name, List<FullTasGoal> goals, List<InputTick> carried, OfflineSolver.ForcedPath path,
            double minutes, bool tickTimed)
        {
            Solve.CourseRuns[name] = path;
            var (ctx, lib, facts, search) = V2(WriteCourseTrace(run, dir, carried, name, goals), tickTimed, minutes);
            var plan = new RunPlan(ctx.HeaderIds, ctx.HeaderGoals.Select(g => g.label).ToArray());
            return Solve.OnThread(() =>
            {
                var builder = new Runs.Builder(ctx, lib, search, facts, searchMisses: true) { Log = m => Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] {m}") };
                for (Runs.Built b = builder.Harvest(plan, carried, "carried"); ; )
                {
                    b = builder.Build(b.Plan, b);
                    if (b.Complete) return b.Inputs;
                    if (b.Failure?.Kind != "unreachable" || b.Failure.Goal == b.Plan.Count - 1) return null;
                    Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] no way to waypoint {b.Failure.Goal + 1} ({b.Failure.Label}) in {run.Caps}: dropped");
                    b = builder.Harvest(b.Plan.RemoveAt(b.Failure.Goal), b.Inputs, "carried", b);
                }
            });
        }

        /// <summary>A course run's synthetic Full-TAS trace as a v2 run: its context (a record, bonus or transition timed in physics
        /// ticks, <see cref="Simulation.TickTimed"/>), its segment library and failures beside it, and a search that stops after
        /// <paramref name="minutes"/>.</summary>
        static (RunContext ctx, SegmentLibrary lib, SegmentFacts facts, SegmentSearch search) V2(string synthetic, bool tickTimed, double minutes)
        {
            RunContext ctx = RunContext.Open(synthetic);
            Func<Simulation> make = ctx.Make;
            if (tickTimed) ctx.Make = () => { Simulation sim = make(); sim.TickTimed = true; return sim; };
            DateTime deadline = DateTime.UtcNow.AddMinutes(minutes);
            return (ctx, SegmentLibrary.Load(RunFiles.FileOf(ctx, ".segments.json"), Array.Empty<string>()), new SegmentFacts(RunFiles.FileOf(ctx, ".failures.json")),
                // A course's goals are seconds apart: an eighth of a Full-TAS search's memory (16 workers in 3 GB, not 24).
                new SegmentSearch(ctx) { Stop = () => DateTime.UtcNow > deadline, ArchiveBudget = 1L << 26, StateBudget = 1L << 27 });
        }

        CourseRun _course;

        /// <summary>The course trace as a Full-TAS run's: its header with the goals added and the best as its inputs, and its world
        /// (another run's <paramref name="name"/> and <paramref name="goals"/>: a farm loop's lap repair).</summary>
        static string WriteCourseTrace(CourseRun run, string dir, List<InputTick> inputs, string name = null, List<FullTasGoal> goals = null)
        {
            // The run ends at its goals (GoalHeader drops a bonus's reward box and a transition's next gate: its last goal is the finish).
            var head = JsonNode.Parse(CourseWaypoints.GoalHeader(run.Header).GetRawText()).AsObject();
            string[] ids = IGTAPTasMod.FullTasGoalIds.Derived((goals ?? run.Goals).Select(g => g.label).ToList());
            foreach (string key in new[] { "ticks", "result", "inputs" }) head.Remove(key);
            // A course's run: the simulation starts it from the category's canonical start (CourseStart.cs) as a course trace's.
            head["courseAuto"] = true;
            head["inputs"] = JsonSerializer.SerializeToNode(inputs.Select(t => t.ToJson()));
            head["fulltas"] = new JsonObject
            {
                ["name"] = name ?? run.Name,
                // A category route may walk through any box (bonus5's 3.56 brushes GLOBALWatt): no buy is a stray one here.
                ["buys"] = "loose",
                ["goals"] = new JsonArray((goals ?? run.Goals).Select((g, i) =>
                {
                    var o = new JsonObject { ["id"] = ids[i], ["label"] = g.label, ["kind"] = g.kind };
                    if (g.IsCourse) o["course"] = g.course;
                    if (g.kind == FullTasGoal.At) o["rect"] = new JsonArray(g.x, g.y, g.w, g.h);
                    if (g.from != 0) o["from"] = g.from;
                    return (JsonNode)o;
                }).ToArray()),
            };
            string file = Path.Combine(dir, "fulltas-" + (name ?? run.Name) + ".json");
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
                sim.Engine.HumanClass = run.Human != null;
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
                List<(string name, List<OfflineSolver.Box> boxes)> directions = DirectionBlocks(run.Course, run.Caps);
                var cut = new bool[directions?.Count ?? 0];
                Action<int> onStep = _ =>
                {
                    if (!rules.IsEmpty)
                    {
                        WorldPos p = sim.PlayerWorld;
                        if (via < rules.Via.Count && rules.Via[via].Contains(p)) via++;
                        foreach (OfflineSolver.Box box in rules.Avoid) if (box.Cuts(sim.PlayerStepFrom, p)) avoided = true;
                    }
                    for (int k = 0; k < cut.Length; k++)
                        if (!cut[k] && directions[k].boxes.Any(b => b.Cuts(sim.PlayerStepFrom, sim.PlayerWorld))) cut[k] = true;
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
                    Directions = directions?.Where((d, k) => !cut[k]).Select(d => d.name).ToArray(),
                    OffPath = avoided || via < rules.Via.Count, Bus = gated && r.Finished ? MeasureBus(ticks, toStart, toEnd, timing, body) : null };
            });
        }

        /// <summary>A two-way course's direction blocks (Categories.Directions) with their avoid boxes; null when the course doesn't have both.</summary>
        static List<(string name, List<OfflineSolver.Box> boxes)> DirectionBlocks(int course, string caps)
        {
            List<Solve.OptionalBlock> blocks = Solve.OptionalBlocks(course, caps, new OfflineSolver.ForcedPath());
            var found = IGTAPTasMod.Categories.Directions.Select(d => (d.name, boxes: blocks.FirstOrDefault(b => b.Name == d.block)?.Boxes.Avoid)).ToList();
            return found.All(d => d.boxes != null && d.boxes.Count > 0) ? found : null;
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
            bus.Gate = new BusEdge { Tick = ticks[e], TimerTick = ticks[t], Before = toStart[e - 1], At = toStart[e], Travel = Travel(e),
                After = t + 1 < n ? toStart[t + 1] : float.NaN };
            return bus;
        }

        /// <summary>Where a run keeps its last --auto best (under the traces folder); a candidate by that path is that best.</summary>
        const string AutoRunDir = "course-auto/fulltas/";

        /// <summary>The offline-best file of a finishing route as `solve` writes it (the game timer's time, `runTicks`): path and text.</summary>
        static (string file, string text) RecordBestFile(CourseRun run, List<InputTick> inputs, CourseResult c)
        {
            string file = Path.Combine(Path.GetDirectoryName(run.Trace), "offline-best", run.Label + "-" + c.Time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
            return (file, JsonSerializer.Serialize(new { course = run.Course, capabilities = run.Caps, time = c.Time, runTicks = c.RunTicks, directions = c.Directions,
                ticks = inputs.Select(t => t.ToJson()) }));
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


        const string WholeHow = "the whole-route search";

        /// <summary>
        /// The whole-route search (Solve.Run, the plain solve) on the course: seeded with the trace's route, every --seed (another
        /// category's or tree level's route that dies here gives its cells too) and the loop's run file; for --whole-minutes (half the
        /// minutes), in rounds that each end after 5 minutes without a faster route. No archive read or written, so each Improve
        /// searches as a target's first does and the target's archive is left as it is. Its bests go to offline-best as a plain
        /// solve writes them, judged after the session with the loop's. Returns its best (null: none, or it failed).
        /// </summary>
        static List<InputTick> WholeRoute(CourseRun run, string[] args, string runFile)
        {
            double total = Solve.Option(args, "--minutes", 60.0), budget = Solve.Option(args, "--whole-minutes", total / 2);
            if (budget <= 0) return null;
            var plain = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--focus" || a == "--class" || a == "--minutes" || a == "--whole-minutes" || a == "--stall-minutes" || a == "--log-json") { i++; continue; }
                if (a == "--auto" || a == "--farm" || a == "--other-ways" || a == "--no-whole") continue;
                plain.Add(a);
            }
            plain.AddRange(new[] { "--no-archive", "--no-seed-files", "--no-submit", "--no-first-finish" });
            if (runFile != null && File.Exists(runFile)) plain.AddRange(new[] { "--seed", runFile });
            Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] --auto: the whole-route search first, for {budget:0.##} min at most");
            // Solve.Run turns the distance field on for a guided rule; the loop's windows read it in their goal distance.
            bool guided = DistanceField.Enabled;
            try
            {
                // In rounds over the whole budget: a round that stalls (5 min without a faster route) ends, and the next searches
                // afresh (new archive and RNG) from its best, rather than handing the rest to the --auto windows: on Course 6
                // WJ_DS_DJ_BS the windows found nothing, where a fresh search from 1.20 found 1.10 in 2 min and from 1.06, 0.94.
                var clock = Stopwatch.StartNew();
                string bestFile = Path.Combine(Path.GetDirectoryName(run.Trace), "course-auto", run.Name + ".whole-best.json");
                List<InputTick> best = null;
                for (int round = 1; budget - clock.Elapsed.TotalMinutes >= 1; round++)
                {
                    double left = budget - clock.Elapsed.TotalMinutes;
                    var args1 = plain.Concat(new[] { "--minutes", left.ToString(CultureInfo.InvariantCulture),
                        "--stall-minutes", Math.Min(5.0, left).ToString(CultureInfo.InvariantCulture) }).ToList();
                    if (best != null)
                    {
                        File.WriteAllText(bestFile, JsonSerializer.Serialize(new { ticks = best.Select(t => t.ToJson()) }));
                        args1.AddRange(new[] { "--seed", bestFile });
                        Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] --auto: whole-route round {round}, {left:0.##} min left, from its best");
                    }
                    Solve.Run(args1.ToArray(), out List<InputTick> found);
                    best = found ?? best;
                }
                return best;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] --auto: the whole-route search failed ({ex.GetBaseException().Message}); the loop goes on without it");
                return null;
            }
            finally
            {
                DistanceField.Enabled = guided;
                GC.Collect();
            }
        }

        /// <summary>
        /// The route a run starts from, written like a kept best. Not the last --auto best (the loop wrote it when it kept it), nor
        /// the whole-route search's (it wrote its own), and not a route whose file is already there, waiting or judged (the same
        /// inputs and time: a rerun writes nothing twice). The plugin judges a file only when it ties or beats the saved route, so a
        /// slower start is moved to judged\ unjudged.
        /// </summary>
        static void WriteStartingBest(CourseRun run, string how, List<InputTick> inputs, CourseResult c)
        {
            if (how.StartsWith(AutoRunDir, StringComparison.Ordinal) || how == WholeHow) return;
            var (file, text) = RecordBestFile(run, inputs, c);
            foreach (string folder in new[] { Path.GetDirectoryName(file), Path.Combine(Path.GetDirectoryName(file), "judged") })
                if (File.Exists(Path.Combine(folder, Path.GetFileName(file))) && File.ReadAllText(Path.Combine(folder, Path.GetFileName(file))) == text) return;
            WriteRecordBest(run, inputs, c, "start: " + how);
        }

        /// <summary>The waypoints with the route's splits (--waypoints-out FILE and the `waypoints` line on stdout, for the studio):
        /// the goals, their rects (capture frame) and the same in the world frame (capture less the capture's FloatingOrigin:
        /// courses.json's, the map's), each goal's physics tick, the goal the timer counts from (1-based, 0: the start).</summary>
        static void WriteWaypoints(CourseRun run, List<InputTick> inputs, CourseResult c, int timerGoal)
        {
            var (rects, world) = Rects(run);
            WaypointsLine(run, new
            {
                timing = run.Timing,
                route = c.Finished ? (run.Timing == "record" ? c.Time : c.RunTicks * .02f).ToString("0.00", CultureInfo.InvariantCulture) + "s" : "no finish",
                goals = run.Goals.Select(g => g.label),
                rects,
                world,
                splits = CourseWaypoints.Check(run.Trace, inputs, run.Goals),
                timerGoal,
                record = c.Finished ? (long?)Math.Round(c.Time / .02f) : null,
                farm = c.Finished ? (long?)c.RunTicks : null,
                bus = c.Bus == null ? null : new { gate = BusJson(c.Bus.Gate), finish = BusJson(c.Bus.Finish) },
            });
        }

        static (float[][] rects, float[][] world) Rects(CourseRun run)
        {
            CapturePos origin = Solve.OnThread(() => run.Plain().WorldOriginInCapture);
            return (run.Goals.Select(g => g.kind == FullTasGoal.At ? new[] { g.x, g.y, g.w, g.h } : null).ToArray(),
                run.Goals.Select(g => g.kind == FullTasGoal.At ? new[] { g.x - origin.x, g.y - origin.y, g.w, g.h } : null).ToArray());
        }

        static void WaypointsLine(CourseRun run, object waypoints)
        {
            string json = JsonSerializer.Serialize(waypoints);
            Console.WriteLine("waypoints " + json);
            if (run.WaypointsFile != null) Solve.WriteShared(run.WaypointsFile, json);
        }

        static object BusJson(BusEdge e)
        {
            static float? Finite(float v) => float.IsFinite(v) ? (float)Math.Round(v, 3) : null;
            return e == null ? null : new { tick = e.Tick, timerTick = e.TimerTick == 0 ? (long?)null : e.TimerTick, before = Finite(e.Before), at = Finite(e.At),
                travel = Finite(e.Travel), after = Finite(e.After) };
        }
    }
}
