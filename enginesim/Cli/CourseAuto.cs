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
    static partial class CourseAuto
    {
        internal sealed class CourseRun
        {
            public string Trace, Label, Name, Timing, WaypointsFile, LogJson;
            public int Course;
            public string Caps;
            public JsonElement Header;
            public Func<Simulation> Plain;
            public OfflineSolver.ForcedPath Rules;
            public List<FullTasGoal> Goals;
            public Stopwatch Clock;
            public string Human;
        }

        sealed class CourseResult
        {
            public bool Finished;
            public float Time;
            public long RunTicks;
            public string EndReason;
            public bool OffPath;
            public string[] Directions;
            public Bus Bus;
        }

        sealed class Bus
        {
            public BusEdge Gate, Finish;
        }

        sealed class BusEdge
        {
            public long Tick, TimerTick;
            public float Before, At;
            public float Travel;
            public float After = float.NaN;
        }

        static readonly string[] Directions = { "avoid-forwards", "avoid-backwards" };

        public static int Run(string[] args)
        {
            string trace = Path.GetFullPath(args[0]);
            bool farm = args.Contains("--farm");
            JsonElement header = Solve.ReadHeader(trace);
            if (header.TryGetProperty("fulltas", out _))
                throw new ArgumentException("solve --auto takes a course trace; a Full-TAS trace runs `fulltas <trace> --auto`");
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
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
                if (run.WaypointsFile == null) return Difficulty.Ease(args);
            }
            List<string> optional = Solve.Optional(args, course, caps);
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
                int code = int.MaxValue;
                foreach (string block in run.Timing == "farm" ? new[] { "avoid-backwards", "avoid-forwards" } : new[] { "avoid-forwards", "avoid-backwards" })
                {
                    Console.WriteLine($"--auto: course {course} goes either way from its start; {block} for {minutes / 2:0.##} min");
                    code = Math.Min(code, Run(rest.Concat(new[] { "--optional", block, "--minutes", (minutes / 2).ToString(CultureInfo.InvariantCulture) }).ToArray()));
                }
                return code;
            }
            OfflineSolver.ForcedPath rules = Solve.ForcedPaths(args, course, caps);
            run.Rules = rules;
            run.Name = "course" + course + "_" + caps + (optional.Count == 0 ? "" : "-opt-" + string.Join("+", optional)) + (run.Timing == "farm" ? "-farm" : "");
            string runKey = run.Name + (run.Human == null ? "" : "." + run.Human);
            string dir = Path.Combine(Path.GetDirectoryName(trace), "course-auto");
            Directory.CreateDirectory(dir);
            if (run.LogJson != null) File.WriteAllText(run.LogJson, "");

            string startCaps = Solve.OnThread(() => PlayerCaps.Of(run.Plain()).Id);
            if (startCaps != caps)
                throw new InvalidOperationException($"{Path.GetFileName(trace)} is {caps}, but its start's player has {startCaps} unlocked: capture the category's start again");

            var candidates = new List<(string how, List<InputTick> inputs)> { (Path.GetFileName(trace), Solve.ReadInputs(header)) };
            foreach (string seed in Solve.Args(args, "--seed")) candidates.Add((Path.GetFileName(seed), global::IGTAP.EngineSim.Cli.Plan.ReadRoute(seed)));
            string runFile = Path.Combine(dir, "fulltas", runKey + ".json");
            if (!args.Contains("--fresh") && File.Exists(runFile)) candidates.Add((AutoRunDir + Path.GetFileName(runFile), global::IGTAP.EngineSim.Cli.Plan.ReadRoute(runFile)));
            if (run.Timing == "farm" && gated) return LoopAuto(run, args, candidates, dir);
            if (run.Human != null) candidates.AddRange(candidates.ToList().Select(c => (c.how + " (digital)", Digital(c.inputs))));
            var played = candidates.Select(c => (c.how, c.inputs, result: PlayCourse(run, c.inputs))).ToList();
            foreach (var p in played)
                Console.WriteLine($"{p.how}: " + (!p.result.Finished ? "no finish (" + p.result.EndReason + ")"
                    : $"record {p.result.Time:0.00}s, run {p.result.RunTicks * .02:0.00}s from the start ({p.result.RunTicks} ticks)" + (p.result.OffPath ? ", off the forced path (an avoid box or a missed via)" : "")));
            var finishing = played.Where(p => p.result.Finished && !p.result.OffPath).ToList();
            if (finishing.Count == 0 && run.Human != null)
                throw new InvalidOperationException($"No route a {run.Human} class plays to the finish yet: Improve reaches one first");
            if (finishing.Count == 0)
            {
                if (run.WaypointsFile != null)
                    throw new InvalidOperationException("--auto improves a complete route: neither the trace's route nor a --seed finishes the course on its forced path");
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
                int code = Solve.Run(plain.ToArray(), out List<InputTick> first);
                CourseResult firstResult = first == null ? null : PlayCourse(run, first);
                if (firstResult == null || !firstResult.Finished || firstResult.OffPath) return code;
                Console.WriteLine($"--auto: improving the first finish (record {firstResult.Time:0.00}s, run {firstResult.RunTicks * .02:0.00}s from the start)");
                finishing.Add(("first finish", first, firstResult));
                Directory.CreateDirectory(Path.GetDirectoryName(runFile));
                Solve.WriteShared(runFile, JsonSerializer.Serialize(new { inputs = first.Select(t => t.ToJson()) }));
            }
            double total = Solve.Option(args, "--minutes", 60.0);
            if (run.Human == null && run.WaypointsFile == null && !args.Contains("--focus") && !args.Contains("--no-whole"))
            {
                List<InputTick> whole = WholeRoute(run, args, args.Contains("--fresh") ? null : runFile);
                CourseResult wholeResult = whole == null ? null : PlayCourse(run, whole);
                if (wholeResult != null && wholeResult.Finished && !wholeResult.OffPath && !finishing.Any(p => p.inputs.SequenceEqual(whole))) finishing.Add((WholeHow, whole, wholeResult));
            }
            var best = run.Timing == "record" ? finishing.OrderBy(p => p.result.Time).ThenBy(p => p.result.RunTicks).First()
                : finishing.OrderBy(p => p.result.RunTicks).ThenBy(p => p.result.Time).First();
            // Write the starting best too: the plugin's judge only sees offline-best files, and polish writes only improvements.
            if (run.WaypointsFile == null && run.Human == null) WriteStartingBest(run, best.how, best.inputs, best.result);

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

        static (int from, int to) Span(string[] args, int count, int after)
        {
            string focus = Solve.Option(args, "--focus", (string)null);
            if (focus == null) return (after, count - 1);
            string[] ends = focus.Split('-');
            return (Math.Clamp(int.Parse(ends[0], CultureInfo.InvariantCulture) - 1, 0, count - 1), Math.Clamp(int.Parse(ends[^1], CultureInfo.InvariantCulture) - 1, 0, count - 1));
        }

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
                    // shortcut: 20M ticks a goal search and 3 of 20M for the rebuild; raise them if polishes stall short of the minutes.
                    var improver = new Improver(ctx, lib, search, facts, 1)
                        { SegmentBudget = Builder.Budgets[0], RebuildBudget = Builder.Budgets[0], SegmentSearches = 3, Log = Log };
                    improver.NewBest += (b, how) =>
                    {
                        if (how == "start") return;
                        best = b.Inputs;
                        gained = true;
                        kept(b.Inputs, how);
                    };
                    improver.RunSegment(plan, best, j, search.Stop);
                    // Collect between searches: server GC would let the 16 solvers pile up toward its hard limit.
                    GC.Collect();
                    Console.WriteLine("@fulltas " + JsonSerializer.Serialize(new { type = "pull", pull = ++pulls }));
                }
            }
            lib.Save();
            Log("polish: " + (search.Stop() ? "the minutes are up" : "converged: no goal of the span gains"));
        }

        static long CourseScore(CourseRun run, List<InputTick> inputs)
        {
            CourseResult c = PlayCourse(run, inputs);
            if (!c.Finished || c.OffPath) return long.MaxValue;
            return run.Timing == "record" ? (long)Math.Round(c.Time / .02f) * 1_000_000 + c.RunTicks : c.RunTicks;
        }

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

        internal static List<InputTick> FirstFinish(string trace, JsonElement header, OfflineSolver.ForcedPath rules, List<List<InputTick>> seeds,
            IReadOnlyList<string> seedPaths, double minutes)
        {
            trace = Path.GetFullPath(trace);
            if (header.TryGetProperty("fulltas", out _)) return null;
            CourseRun run = Run(trace, header, rules);
            if (seeds.Any(s => s.Count > 0 && PlayCourse(run, s) is { Finished: true, OffPath: false })) return null;
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

        static CourseRun Run(string trace, JsonElement header, OfflineSolver.ForcedPath rules)
        {
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            return new CourseRun { Trace = trace, Course = course, Caps = caps, Header = header, Label = course + "_" + caps, Timing = "farm",
                Plain = Solve.Creator(trace, header), Clock = Stopwatch.StartNew(), Rules = rules };
        }

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

        sealed class Seed
        {
            public string Name;
            public IGTAPTasMod.Caps Caps;
            public List<InputTick> Inputs;
            public SeedStart At;
        }

        sealed class SeedStart
        {
            public float X, Y;
            public bool FacingRight;
        }

        const float MaxWalk = 1500f, MaxWalkMiss = 8f, MaxWalkRise = 4f, CellWalkRise = 96f;
        const int MaxWalkTicks = 240, MaxWalks = 3, StandTicks = 7, CellWalksPerCheck = 4;

        static Seed ReadSeed(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 256 });
            JsonElement root = doc.RootElement;
            return new Seed
            {
                Name = root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : Path.GetFileName(path),
                Caps = !root.TryGetProperty("capabilities", out JsonElement caps) || caps.ValueKind != JsonValueKind.String
                    || !IGTAPTasMod.Caps.TryParse(caps.GetString(), out IGTAPTasMod.Caps seedCaps) ? default : seedCaps,
                Inputs = Solve.ReadInputs(root),
                At = root.TryGetProperty("start", out JsonElement start) && start.ValueKind == JsonValueKind.Object
                    ? new SeedStart { X = start.GetProperty("x").GetSingle(), Y = start.GetProperty("y").GetSingle(), FacingRight = start.GetProperty("facingRight").GetBoolean() }
                    : root.TryGetProperty("trace", out JsonElement trace) && trace.ValueKind == JsonValueKind.String ? StartOf(trace.GetString()) : null,
            };
        }

        static SeedStart StartOf(string trace)
        {
            if (!File.Exists(trace)) return null;
            JsonElement start = Solve.ReadHeader(trace).GetProperty("start");
            JsonElement position = start.GetProperty("position"), origin = start.GetProperty("origin");
            JsonElement facing = start.GetProperty("movement").EnumerateArray().First(f => f[1].GetString() == "facingRight")[2];
            return new SeedStart
            {
                X = position.GetProperty("x").GetSingle() - origin.GetProperty("x").GetSingle(),
                Y = position.GetProperty("y").GetSingle() - origin.GetProperty("y").GetSingle(),
                FacingRight = facing.GetBoolean(),
            };
        }

        static List<(string how, List<InputTick> inputs)> WalkedStarts(Simulation eval, Seed s)
        {
            var found = new List<(float miss, string how, List<InputTick> inputs)>();
            SeedStart to = s.At;
            eval.Restore(eval.Start);
            float dx = to.X - eval.PlayerWorld.x;
            if (Math.Abs(dx) > MaxWalk) return new List<(string, List<InputTick>)>();
            float dir = dx < 0f ? -1f : 1f, face = to.FacingRight ? 1f : -1f;
            float? Stand(int n, float last, float turn)
            {
                var walk = Enumerable.Range(0, n).Select(i => new InputTick { x = i == n - 1 ? dir * last : dir }).ToList();
                int still = eval.Still(walk, 300);
                if (still < 0) return null;
                if (turn > 0f)
                {
                    walk.AddRange(Enumerable.Repeat(new InputTick(), still - walk.Count));
                    walk.Add(new InputTick { x = face * turn });
                    still = eval.Still(walk, 300);
                    if (still < 0) return null;
                }
                WorldPos p = eval.PlayerWorld;
                if (Math.Abs(p.y - to.Y) > MaxWalkRise) return null;
                float miss = Math.Abs(p.x - to.X);
                if (miss <= MaxWalkMiss && eval.Player.facingRight == to.FacingRight)
                {
                    walk.AddRange(Enumerable.Repeat(new InputTick(), still - walk.Count + StandTicks));
                    walk.AddRange(s.Inputs);
                    found.Add((miss, $"{s.Name}, after walking {n} ticks to its start ({miss:0.0} off) and standing still", walk));
                }
                return (p.x - to.X) * dir;
            }
            float tap = face == dir ? 0f : 1f;
            int best = -1, blocked = 0;
            float bestMiss = float.MaxValue;
            float? before = null;
            for (int n = 0; n <= MaxWalkTicks && blocked < 5; n++)
            {
                float? past = Stand(n, 1f, tap);
                if (past.HasValue && Math.Abs(past.Value) < bestMiss) { bestMiss = Math.Abs(past.Value); best = n; }
                if (past > MaxWalkMiss) break;
                blocked = past.HasValue && past == before ? blocked + 1 : 0;
                before = past;
            }
            if (best >= 0)
                foreach (int n in new[] { best - 1, best, best + 1 }.Where(n => n > 0))
                    for (int last = 1; last <= 10; last++)
                        for (int turn = (int)tap; turn <= 10 * tap; turn++) Stand(n, last / 10f, turn / 10f);
            return found.OrderBy(f => f.miss).GroupBy(f => f.miss).Select(g => g.First()).Take(MaxWalks).Select(f => (f.how, f.inputs)).ToList();
        }

        static List<InputTick> Reach(CourseRun run, string dir, string name, List<FullTasGoal> goals, List<InputTick> carried, OfflineSolver.ForcedPath path,
            double minutes, bool tickTimed)
        {
            Solve.CourseRuns[name] = path;
            var (ctx, lib, facts, search) = V2(WriteCourseTrace(run, dir, carried, name, goals), tickTimed, minutes);
            var plan = new RunPlan(ctx.HeaderIds, ctx.HeaderGoals.Select(g => g.label).ToArray());
            return Solve.OnThread(() =>
            {
                var builder = new Builder(ctx, lib, search, facts, searchMisses: true)
                    { MaxBudget = Builder.Budgets[0], Log = m => Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] {m}") };
                for (Built b = builder.Harvest(plan, carried, "carried"); ; )
                {
                    b = builder.Build(b.Plan, b);
                    if (b.Complete) return b.Inputs;
                    if (b.Failure?.Kind != "unreachable" || b.Failure.Goal == b.Plan.Count - 1) return null;
                    Console.WriteLine($"[{run.Clock.Elapsed:hh\\:mm\\:ss}] no way to waypoint {b.Failure.Goal + 1} ({b.Failure.Label}) in {run.Caps}: dropped");
                    b = builder.Harvest(b.Plan.RemoveAt(b.Failure.Goal), b.Inputs, "carried", b);
                }
            });
        }

        static (RunContext ctx, SegmentLibrary lib, SegmentFacts facts, SegmentSearch search) V2(string synthetic, bool tickTimed, double minutes)
        {
            RunContext ctx = RunContext.Open(synthetic);
            Func<Simulation> make = ctx.Make;
            if (tickTimed) ctx.Make = () => { Simulation sim = make(); sim.TickTimed = true; return sim; };
            DateTime deadline = DateTime.UtcNow.AddMinutes(minutes);
            return (ctx, SegmentLibrary.Load(RunFiles.FileOf(ctx, ".segments.json"), Array.Empty<string>()), new SegmentFacts(RunFiles.FileOf(ctx, ".failures.json")),
                new SegmentSearch(ctx) { Stop = () => DateTime.UtcNow > deadline, ArchiveBudget = 1L << 26, StateBudget = 1L << 27 });
        }

        static string WriteCourseTrace(CourseRun run, string dir, List<InputTick> inputs, string name = null, List<FullTasGoal> goals = null)
        {
            var head = JsonNode.Parse(CourseWaypoints.GoalHeader(run.Header).GetRawText()).AsObject();
            string[] ids = IGTAPTasMod.FullTasGoalIds.Derived((goals ?? run.Goals).Select(g => g.label).ToList());
            foreach (string key in new[] { "ticks", "result", "inputs" }) head.Remove(key);
            head["courseAuto"] = true;
            head["inputs"] = JsonSerializer.SerializeToNode(inputs.Select(t => t.ToJson()));
            head["fulltas"] = new JsonObject
            {
                ["name"] = name ?? run.Name,
                // Loose buys: a category route may brush any box (RULES.md 38), so no buy is a stray one.
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

        static CourseResult PlayCourse(CourseRun run, List<InputTick> inputs)
        {
            return Solve.OnThread(() =>
            {
                Simulation sim = run.Plain();
                sim.Engine.HumanClass = run.Human != null;
                OfflineSolver.ForcedPath rules = run.Rules ?? new OfflineSolver.ForcedPath();
                int via = 0;
                bool avoided = false;
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

        static List<(string name, List<OfflineSolver.Box> boxes)> DirectionBlocks(int course, string caps)
        {
            List<Solve.OptionalBlock> blocks = Solve.OptionalBlocks(course, caps, new OfflineSolver.ForcedPath());
            var found = IGTAPTasMod.Categories.Directions.Select(d => (d.name, boxes: blocks.FirstOrDefault(b => b.Name == d.block)?.Boxes.Avoid)).ToList();
            return found.All(d => d.boxes != null && d.boxes.Count > 0) ? found : null;
        }

        static List<Collider2D> GateColliders(Simulation sim, int course, bool start)
        {
            IEnumerable<MonoBehaviour> gates = start ? sim.World.All<startGate>()
                : sim.World.All<endGate>().Where(g => DistanceField.EndOfCourse == null || (bool)DistanceField.EndOfCourse.GetValue(g));
            return gates.Where(g => g.GetComponentInParent<courseScript>()?.courseNumber == course)
                .Select(g => (Collider2D)g.GetComponent<BoxCollider2D>()).Where(c => c != null).ToList();
        }

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

        static Bus MeasureBus(List<long> ticks, List<float> toStart, List<float> toEnd, List<bool> timing, List<(float x, float y)> body)
        {
            int n = ticks.Count;
            if (n < 2) return null;
            float Travel(int i) => (float)Math.Sqrt(Math.Pow(body[i].x - body[i - 1].x, 2) + Math.Pow(body[i].y - body[i - 1].y, 2));
            var bus = new Bus { Finish = new BusEdge { Tick = ticks[n - 1], Before = toEnd[n - 2], At = toEnd[n - 1], Travel = Travel(n - 1) } };
            // Take the timer's last start; the Stay starts it, so don't walk back over overlaps that lapsed mid-step.
            int t = -1;
            for (int i = 1; i < n; i++) if (timing[i] && !timing[i - 1]) t = i;
            if (t < 2) return bus;
            int e = t - 1;
            bus.Gate = new BusEdge { Tick = ticks[e], TimerTick = ticks[t], Before = toStart[e - 1], At = toStart[e], Travel = Travel(e),
                After = t + 1 < n ? toStart[t + 1] : float.NaN };
            return bus;
        }

        const string AutoRunDir = "course-auto/fulltas/";

        static (string file, string text) RecordBestFile(CourseRun run, List<InputTick> inputs, CourseResult c)
        {
            string file = Path.Combine(Path.GetDirectoryName(run.Trace), "offline-best", run.Label + "-" + c.Time.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
            return (file, JsonSerializer.Serialize(new { course = run.Course, capabilities = run.Caps, time = c.Time, runTicks = c.RunTicks, directions = c.Directions,
                ticks = inputs.Select(t => t.ToJson()) }));
        }

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
            bool guided = DistanceField.Enabled;
            try
            {
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

        static void WriteStartingBest(CourseRun run, string how, List<InputTick> inputs, CourseResult c)
        {
            if (how.StartsWith(AutoRunDir, StringComparison.Ordinal) || how == WholeHow) return;
            var (file, text) = RecordBestFile(run, inputs, c);
            foreach (string folder in new[] { Path.GetDirectoryName(file), Path.Combine(Path.GetDirectoryName(file), "judged") })
                if (File.Exists(Path.Combine(folder, Path.GetFileName(file))) && File.ReadAllText(Path.Combine(folder, Path.GetFileName(file))) == text) return;
            WriteRecordBest(run, inputs, c, "start: " + how);
        }

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
