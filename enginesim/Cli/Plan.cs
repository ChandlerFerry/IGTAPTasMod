using IGTAPTasMod;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// plan &lt;trace&gt; [--threads N] [--width W] [--max-width W] [--minutes M] [--cell C] [--cap K] [--labels L] [--first]
    /// [--out file] [--no-polish] [--no-tech-grafts] [--via/--avoid/--optional as solve]: the contact-node planner
    /// (<see cref="Planner"/>) from the trace's start, then the timing polish (<see cref="Polish"/>) on its best; the route is
    /// written as a best file `solve --seed` takes (default &lt;traces&gt;\plan\&lt;label&gt;-&lt;time&gt;.json).
    /// </summary>
    static class Plan
    {
        public static int Run(string[] args)
        {
            string trace = args[0];
            JsonElement header = Solve.ReadHeader(trace);
            int threads = Solve.Option(args, "--threads", Environment.ProcessorCount);
            var options = new Planner.Options
            {
                Threads = threads,
                Width = Solve.Option(args, "--width", Math.Max(48, 2 * threads)),
                MaxWidth = Solve.Option(args, "--max-width", 768),
                Cell = Solve.Option(args, "--cell", 16f),
                Cap = Solve.Option(args, "--cap", 8),
                Labels = Solve.Option(args, "--labels", 2),
                Seconds = Solve.Option(args, "--minutes", 5.0) * 60.0,
                FirstOnly = args.Contains("--first"),
                GuideRadius = Solve.Option(args, "--guide-radius", 160f),
                GuideSpeed = Solve.Option(args, "--guide-speed", 10f),
                GuideAccel = Solve.Option(args, "--guide-accel", 60f),
                FollowTicks = Solve.Option(args, "--follow-ticks", 16),
                FollowRadius = Solve.Option(args, "--follow-radius", 48f),
                FollowSpeed = Solve.Option(args, "--follow-speed", 200f),
                MidAir = !args.Contains("--no-midair"),
                Chains = !args.Contains("--no-chains"),
                GuideAhead = Solve.Option(args, "--guide-ahead", 60),
            };
            options.Guides.AddRange(Solve.Args(args, "--guide-route").Select(ReadRoute));
            Field(args);
            var clock = Stopwatch.StartNew();
            void Log(string line) => Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {line}");

            TechGrafts mined = args.Contains("--no-tech-grafts") ? null : TechGrafts.Load(Path.GetDirectoryName(Path.GetFullPath(trace)),
                techDir: Solve.Option(args, "--tech-dir", (string)null), cacheFile: Solve.Option(args, "--tech-cache", (string)null));
            // A Full-TAS trace plans windows of its run's goals (Windows).
            if (header.TryGetProperty("fulltas", out _))
            {
                if (Solve.Option(args, "--profile", (string)null) is string fprofile)
                {
                    Window pw = Window.Open(trace, header, args);
                    return Profile(pw.Create, pw.Path, fprofile);
                }
                return Windows(trace, header, args, options, mined, threads, Log, clock);
            }
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            OfflineSolver.ForcedPath forced = Solve.ForcedPaths(args, course, caps);
            Func<Simulation> create = Solve.Creator(trace, header);
            if (Solve.Option(args, "--profile", (string)null) is string profile) return Profile(create, forced, profile);
            string label = course + "_" + caps;
            Log($"Planning course {course} ({caps}) from {Path.GetFileName(trace)} on {threads} threads, width {options.Width}..{options.MaxWidth}, cell {options.Cell}, "
                + $"cap {options.Cap}, {options.Guides.Count} guide routes (follow {options.FollowTicks} ticks), field cell {DistanceField.CellSize} clearance "
                + $"{DistanceField.Clearance.x},{DistanceField.Clearance.y}, {(mined == null ? "no tech grafts" : mined.Patterns.Count + " tech patterns")}"
                + (forced.IsEmpty ? "" : $", forced path via {forced.Via.Count} avoid {forced.Avoid.Count}"));
            Planner.Route route = Plan1(create, caps, forced, mined, options, Log, out Planner planner);
            if (route == null)
            {
                Log($"No finish: {planner.Ticks} ticks simulated, {planner.Expanded} nodes, {clock.Elapsed.TotalSeconds:0.0}s");
                return 1;
            }
            List<InputTick> inputs = route.Inputs;
            float time = route.Time;
            long runTicks = route.RunTicks, polishTicks = 0;
            if (!args.Contains("--no-polish"))
            {
                Polish.Result p = Polish.Once(create, forced, threads, Log, inputs);
                (polishTicks, inputs, time, runTicks) = (p.Ticks, p.Inputs, p.Time, p.RunTicks);
            }
            string outFile = Solve.Option(args, "--out", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(trace)), "plan",
                label + "-" + time.ToString("0.00", CultureInfo.InvariantCulture) + ".json"));
            Write(outFile, course, caps, null, time, runTicks, new { ticks = planner.Ticks, route.Wall, width = route.Width, found = route.Time, foundRunTicks = route.RunTicks, polishTicks }, inputs);
            Log($"Planned {time:0.00}s ({runTicks} physics ticks, {inputs.Count} inputs): planner {planner.Ticks} ticks, first found {route.Time:0.00}s at {route.Wall:0.0}s, "
                + $"polish {polishTicks} ticks, wall {clock.Elapsed.TotalSeconds:0.0}s -> {outFile}");
            return 0;
        }

        static void Write(string outFile, int course, string caps, string fulltas, float time, long runTicks, object planner, List<InputTick> inputs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
            Solve.WriteShared(outFile, JsonSerializer.Serialize(new
            {
                course, capabilities = caps, fulltas, time, runTicks, planner, ticks = inputs.Select(t => t.ToJson()),
            }));
        }

        /// <summary>
        /// A Full-TAS plan: goals --from-goal+1..--to-goal of --route planned from the route's state at goal --from-goal (the
        /// route's window its exact guide unless --no-guide; --guide-probe files and, past the first window, the route itself
        /// as trails), and the route's rest after its goal --to-goal spliced on after each window finish (fastest first). The
        /// first splice that finishes faster than the route is polished (the window's inputs) and written. Where none follows
        /// on, --chain K plans on: the best window finish kept, the next goal planned from there (the route as a trail), up to K
        /// more windows. Without a faster whole run, the best window is written with the prefix alone.
        /// </summary>
        static int Windows(string trace, JsonElement header, string[] args, Planner.Options options, TechGrafts mined, int threads, Action<string> log, Stopwatch clock)
        {
            var userGuides = options.Guides.ToList();
            var trails = new List<Planner.Trail>();
            trails.AddRange(Solve.Args(args, "--guide-probe").Select(ReadProbe));
            options.TrailFollowSpeed = Solve.Option(args, "--trail-follow-speed", options.TrailFollowSpeed);
            // --drop-reset N: the route's quick restart at input N dropped, the window planned from there (the momentum kept) to the
            // route's next goal (or --to-goal), the route itself a trail.
            int drop = Solve.Option(args, "--drop-reset", -1);
            Window w;
            List<InputTick> original;
            long[] originalGoals;
            long originalPhysics;
            if (drop < 0)
            {
                w = Window.Open(trace, header, args);
                original = w.Route;
                originalGoals = w.RouteGoalInputs;
                originalPhysics = w.RoutePhysics;
            }
            else
            {
                original = ReadRoute(Solve.Option(args, "--route", (string)null) ?? throw new ArgumentException("--drop-reset needs --route"));
                if (drop >= original.Count || !original[drop].reset) throw new ArgumentException($"input {drop} of the route is no quick restart");
                Window whole = Window.Build(trace, header, original, 0, 1, partial: false);
                originalGoals = whole.RouteGoalInputs;
                originalPhysics = whole.RoutePhysics;
                int fromGoal = originalGoals.Count(g => g >= 0 && g <= drop);
                var dropped = new List<InputTick>(original);
                InputTick k = dropped[drop]; k.reset = false; dropped[drop] = k;
                w = Window.Build(trace, header, dropped, fromGoal, Math.Max(fromGoal + 1, Solve.Option(args, "--to-goal", fromGoal + 1)), partial: true, startInput: drop);
            }
            Planner.Trail self = null;
            int chain = Solve.Option(args, "--chain", 0), firstFrom = w.From;
            int polishStart = drop >= 0 ? drop : firstFrom == 0 ? 0 : (int)originalGoals[firstFrom - 1];
            float momentumWorth = Solve.Option(args, "--momentum-worth", 60f);
            options.MomentumSlack = Solve.Option(args, "--momentum-slack", 30);
            long plannerTicks = 0;
            for (int step = 0; ; step++)
            {
                options.Guides = userGuides.ToList();
                if (w.OldWindow != null && !args.Contains("--no-guide")) options.Guides.Add(w.OldWindow);
                options.Trails = trails.ToList();
                if (step > 0 || drop >= 0) options.Trails.Add(self ??= w.TrailOf(original, "route"));
                options.FromGoal = w.From;
                log($"Planning {w.Name} goals {w.From + 1}-{w.To} ({w.Labels[w.From]} .. {w.Labels[w.To - 1]}) from input {w.Prefix.Count}"
                    + (w.OldWindow != null ? $", the route's window {w.OldWindowPhysics} physics ticks" : "") + $" ({w.Caps}) from {Path.GetFileName(trace)} on {threads} threads, "
                    + $"width {options.Width}..{options.MaxWidth}, cap {options.Cap}, {options.Guides.Count} guide routes, {options.Trails.Count} trails, "
                    + $"{(mined == null ? "no tech grafts" : mined.Patterns.Count + " tech patterns")}, forced path avoid {w.Path.Avoid.Count}");
                var finishes = new List<Planner.Route>();
                var planner = new Planner(w.Create, w.Caps, w.Path, mined, options);
                planner.Found += r =>
                {
                    lock (finishes) finishes.Add(r);
                    log($"plan finish {r.Time:0.00}s ({r.RunTicks} physics ticks, {r.Inputs.Count} inputs) via {r.How}, width {r.Width}, {r.Ticks} ticks, {planner.Expanded} nodes, {r.Wall:0.0}s");
                };
                planner.Run(log);
                plannerTicks += planner.Ticks;
                if (finishes.Count == 0)
                {
                    log($"No finish: {planner.Ticks} ticks simulated, {planner.Expanded} nodes, {clock.Elapsed.TotalSeconds:0.0}s");
                    return 1;
                }
                // The route's rest after its goal w.To, on each window finish cut at its own goal w.To: by arrival less the momentum
                // it carries (--momentum-worth u/s per physics tick), so a later arrival with more speed is tried before a first touch.
                List<InputTick> tail = original.Skip((int)originalGoals[w.To - 1]).ToList();
                List<InputTick> best = null;
                List<InputTick> faster = null;
                RunResult fasterRun = null;
                int fasterThrough = 0;
                foreach (Planner.Route f in finishes.OrderBy(f => f.RunTicks - f.Speed / momentumWorth).ThenBy(f => f.Time).Take(24))
                {
                    List<InputTick> through = w.Through(f.Inputs);
                    if (through == null) continue;
                    RunResult whole = w.Evaluate(through.Concat(tail).ToList());
                    // A chain goes on only from a finish the player lives through (not one reached while dying).
                    if (whole.Progress > w.To || !(whole.EndReason ?? "").StartsWith("Player death", StringComparison.Ordinal)) best ??= through;
                    if (!whole.Finished || whole.PhysicsTicks >= (fasterRun?.PhysicsTicks ?? originalPhysics)) continue;
                    log($"window finish {f.RunTicks - w.StartPhysics} physics ticks (speed {f.Speed:0} at its goal): the route's rest follows on, whole run "
                        + $"{whole.Time:0.00}s ({whole.PhysicsTicks} physics ticks; the route's {originalPhysics})");
                    faster = through.Concat(tail).Take(whole.FinishTick + 1).ToList();
                    fasterRun = whole;
                    fasterThrough = through.Count;
                }
                if (faster != null) return Finish(trace, args, w, faster, firstFrom, polishStart, fasterThrough, plannerTicks, threads, log, clock);
                Planner.Route fastest = finishes.OrderBy(f => f.RunTicks).First();
                log($"window best {fastest.RunTicks - w.StartPhysics} physics ticks{(w.OldWindow != null ? $" (the route's: {w.OldWindowPhysics})" : "")}: the route's rest after goal {w.To} "
                    + $"doesn't follow on from any of {finishes.Count} finishes (speeds at their goal {string.Join(" ", finishes.Select(f => f.Speed.ToString("0", CultureInfo.InvariantCulture)))})");
                if (best == null || step >= chain || w.To >= w.Labels.Length)
                {
                    List<InputTick> alone = best ?? w.Prefix.Concat(fastest.Inputs).ToList();
                    string file = Solve.Option(args, "--out", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(trace)), "plan",
                        $"fulltas-{w.Name}-goals-{firstFrom + 1}-{w.To}-window.json"));
                    Write(file, 0, w.Caps, w.Name, fastest.Time, fastest.RunTicks, new { ticks = plannerTicks }, alone);
                    log($"Planned no faster whole run: the prefix and best window ({alone.Count} inputs) -> {file}, planner {plannerTicks} ticks, wall {clock.Elapsed.TotalSeconds:0.0}s");
                    return 1;
                }
                // Chain: the best window kept, the next goal planned from there.
                w = Window.Build(trace, header, best.Concat(tail).ToList(), w.To, w.To + 1, partial: true);
            }
        }

        /// <summary>Polishes the planned part (input ticks <paramref name="start"/> to <paramref name="planned"/>) of a whole run
        /// that finishes faster, and writes it.</summary>
        static int Finish(string trace, string[] args, Window w, List<InputTick> inputs, int from, int start, int planned, long plannerTicks, int threads, Action<string> log, Stopwatch clock)
        {
            RunResult r = w.Evaluate(inputs);
            float time = r.Time;
            long runTicks = r.PhysicsTicks, polishTicks = 0;
            if (!args.Contains("--no-polish"))
            {
                Polish.Result p = Polish.Once(w.Full, new OfflineSolver.ForcedPath(), threads, log, inputs, start, planned);
                (polishTicks, inputs, time, runTicks) = (p.Ticks, p.Inputs, p.Time, p.RunTicks);
            }
            string outFile = Solve.Option(args, "--out", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(trace)), "plan",
                $"fulltas-{w.Name}-goals-{from + 1}-{w.To}-{time.ToString("0.00", CultureInfo.InvariantCulture)}.json"));
            Write(outFile, 0, w.Caps, w.Name, time, runTicks, new { ticks = plannerTicks, polishTicks }, inputs);
            log($"Planned {time:0.00}s ({runTicks} physics ticks, {inputs.Count} inputs): planner {plannerTicks} ticks, polish {polishTicks} ticks, "
                + $"wall {clock.Elapsed.TotalSeconds:0.0}s -> {outFile}");
            return 0;
        }

        /// <summary>A 1x probe (tools/probe.py, probes/&lt;name&gt;.json): its inputs ("ticks") and per physics tick ("rows" of kind
        /// "tick") its place in the world frame, Velocity + momentum and the input tick it had played.</summary>
        internal static Planner.Trail ReadProbe(string file)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 256 });
            JsonElement root = doc.RootElement;
            var rows = root.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("kind").GetString() == "tick").ToList();
            int count = rows.Max(r => r.GetProperty("physics").GetInt32()) + 1;
            var trail = new Planner.Trail
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Inputs = root.GetProperty("ticks").EnumerateArray().Select(InputTick.FromJson).ToList(),
                At = new WorldPos[count], V = new Vector2[count], Input = Enumerable.Repeat(-1, count).ToArray(),
            };
            float F(JsonElement r, string name) => r.GetProperty(name).GetSingle();
            foreach (JsonElement r in rows)
            {
                int p = r.GetProperty("physics").GetInt32();
                trail.At[p] = new WorldPos(F(r, "x"), F(r, "y"));
                trail.V[p] = new Vector2(F(r, "vx") + F(r, "mx"), F(r, "vy") + F(r, "my"));
                trail.Input[p] = r.GetProperty("input").GetInt32();
            }
            return trail;
        }

        internal static List<InputTick> ReadRoute(string file)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 256 });
            return doc.RootElement.TryGetProperty("ticks", out JsonElement ticks) && ticks.ValueKind == JsonValueKind.Array && !doc.RootElement.TryGetProperty("inputs", out _)
                ? ticks.EnumerateArray().Select(InputTick.FromJson).ToList() : Solve.ReadInputs(doc.RootElement);
        }

        /// <summary>
        /// A Full-TAS trace's run (its header's goals) and a route of it (--route, a run file's "inputs" or a best's "ticks"),
        /// cut at goal --from-goal (0: the start) and --to-goal (default the next): the route's prefix, its window, its rest,
        /// and simulations from the start (<see cref="Full"/>) and from the window's start (<see cref="Create"/>, the prefix
        /// replayed on each, the goals limited to the window's last), with the run's avoid boxes for the window's goals.
        /// </summary>
        internal sealed class Window
        {
            public string Name, Caps;
            public string[] Labels;
            public int From, To;
            /// <summary>The whole route and its goals' input ticks (the input count when each was reached).</summary>
            public List<InputTick> Route;
            public long[] RouteGoalInputs;
            /// <summary>OldWindow and Tail are null for a route that doesn't reach goal To (a chained window).</summary>
            public List<InputTick> Prefix, OldWindow, Tail;
            public long StartPhysics, OldWindowPhysics, RoutePhysics;
            public Func<Simulation> Full, Create;
            public OfflineSolver.ForcedPath Path;

            public static Window Open(string trace, JsonElement header, string[] args)
            {
                string routeFile = Solve.Option(args, "--route", (string)null) ?? throw new ArgumentException("a Full-TAS plan needs --route <run file or best>");
                int from = Solve.Option(args, "--from-goal", 0);
                return Build(trace, header, ReadRoute(routeFile), from, Solve.Option(args, "--to-goal", from + 1), partial: false);
            }

            /// <param name="partial">The route may stop short of goal <paramref name="to"/> (it must reach goal <paramref name="from"/>).</param>
            /// <param name="startInput">The window starts at this input tick (goal <paramref name="from"/> reached before it) instead of
            /// at goal <paramref name="from"/>'s.</param>
            public static Window Build(string trace, JsonElement header, List<InputTick> route, int from, int to, bool partial, int startInput = -1)
            {
                JsonElement fulltas = header.GetProperty("fulltas");
                var w = new Window
                {
                    Name = fulltas.GetProperty("name").GetString(),
                    Labels = fulltas.GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray(),
                    Route = route,
                    From = from,
                };
                w.To = Math.Min(w.Labels.Length, to);
                if (w.From < 0 || w.To <= w.From) throw new ArgumentException($"goals {w.From}..{w.To} of {w.Labels.Length}");
                Func<Simulation> make = Solve.Creator(trace, header);
                int goals = w.Labels.Length;
                w.Full = () => { Simulation sim = make(); sim.StrictBuys = true; sim.Goals.Limit = goals; return sim; };
                RunResult r = null;
                Caps caps = default;
                long startPhysics = 0;
                Solve.OnThread(() =>
                {
                    Simulation sim = w.Full();
                    r = sim.Run(route, null, null, 1000000);
                    if (r.Progress < w.From) return;
                    int cut = startInput >= 0 ? startInput : w.From == 0 ? 0 : (int)r.GoalInputTicks[w.From - 1];
                    SimSnapshot at = cut == 0 ? sim.Start : sim.Advance(route.GetRange(0, cut));
                    sim.Restore(at);
                    caps = PlayerCaps.Of(sim.Player);
                    startPhysics = sim.Engine.PhysicsTicks;
                });
                if (r.Progress < (partial ? w.From : w.To)) throw new InvalidOperationException($"the route reaches goal {r.Progress} of {goals} ({r.EndReason}), not goal {(partial ? w.From : w.To)}");
                int start = startInput >= 0 ? startInput : w.From == 0 ? 0 : (int)r.GoalInputTicks[w.From - 1];
                w.RouteGoalInputs = (long[])r.GoalInputTicks.Clone();
                w.Prefix = route.GetRange(0, start);
                if (r.Progress >= w.To)
                {
                    int end = (int)r.GoalInputTicks[w.To - 1];
                    w.OldWindow = route.GetRange(start, Math.Min(route.Count, end + 1) - start);
                    w.Tail = route.Skip(end).ToList();
                    w.OldWindowPhysics = r.GoalPhysicsTicks[w.To - 1] - startPhysics;
                }
                w.StartPhysics = startPhysics;
                w.RoutePhysics = r.Finished ? r.PhysicsTicks : long.MaxValue;
                w.Caps = caps.Id;
                w.Path = Solve.FullTasPaths(w.Name, w.Labels.Skip(w.From).Take(w.To - w.From).ToList(), w.Labels);
                List<InputTick> prefix = w.Prefix;
                int limit = w.To;
                w.Create = () =>
                {
                    Simulation sim = make();
                    sim.StrictBuys = true;
                    sim.Goals.Limit = limit;
                    if (prefix.Count == 0) return sim;
                    SimSnapshot at = sim.Advance(prefix) ?? throw new InvalidOperationException("the prefix ended before its last input");
                    sim.SetStart(at, prefix.Count);
                    return sim;
                };
                return w;
            }

            /// <summary>The whole run from the start (its own thread and world).</summary>
            public RunResult Evaluate(List<InputTick> inputs)
            {
                return Solve.OnThread(() => Full().Run(inputs, null, null, 1000000));
            }

            /// <summary>The prefix and a window's inputs (from the window's start) cut where they reach goal To: the inputs a
            /// route's rest after its own goal To follows; null when they don't reach it from the start.</summary>
            public List<InputTick> Through(List<InputTick> window)
            {
                List<InputTick> all = Prefix.Concat(window).ToList();
                RunResult r = Evaluate(all);
                return r.Progress >= To ? all.Take((int)r.GoalInputTicks[To - 1]).ToList() : null;
            }

            /// <summary>A route of this run as a trail (<see cref="Planner.Trail"/>): its replay from the start, per physics tick.</summary>
            public Planner.Trail TrailOf(List<InputTick> route, string name)
            {
                var at = new List<WorldPos>();
                var v = new List<Vector2>();
                var input = new List<int>();
                Solve.OnThread(() =>
                {
                    Simulation sim = Full();
                    sim.Run(route, null, null, 1000000, _ =>
                    {
                        int p = (int)sim.Engine.PhysicsTicks;
                        while (at.Count <= p) { at.Add(default); v.Add(default); input.Add(-1); }
                        at[p] = sim.PlayerWorld;
                        v[p] = sim.Player.Velocity + sim.Player.momentum;
                        input[p] = (int)sim.Engine.RunTicks;
                    });
                });
                return new Planner.Trail { Name = name, Inputs = route, At = at.ToArray(), V = v.ToArray(), Input = input.ToArray() };
            }
        }

        /// <summary>The field the planner's nodes rank by without guide routes: a fine one (--field-cell, default 8) that can keep
        /// the hitbox's room (--clearance x,y; default 0: none; 18,32 keeps a 41x72 hitbox), so a gap the player can't pass isn't
        /// a way to the target.</summary>
        internal static void Field(string[] args)
        {
            DistanceField.CellSize = Solve.Option(args, "--field-cell", 8f);
            string[] c = Solve.Option(args, "--clearance", "0").Split(',');
            DistanceField.Clearance = c.Length == 2
                ? new Vector2(float.Parse(c[0], CultureInfo.InvariantCulture), float.Parse(c[1], CultureInfo.InvariantCulture)) : Vector2.zero;
        }

        /// <summary>--profile &lt;route.json&gt;: the guide's distance along a route every 10 input ticks (the start gate's until the
        /// timer runs), to see where it misleads.</summary>
        static int Profile(Func<Simulation> create, OfflineSolver.ForcedPath forced, string routePath)
        {
            List<InputTick> route = ReadRoute(routePath);
            Simulation sim = create();
            DistanceField field = DistanceField.For(sim);
            if (sim.Goals == null) field.SetVia(forced.Via);
            field.SetAvoid(forced.Avoid);
            RunResult r = sim.Run(route, null, tick =>
            {
                if (tick % 10 != 0) return true;
                bool timing = sim.TimingCourse() != null;
                CapturePos at = sim.PlayerAtCapture;
                float d = sim.Goals == null && !sim.TimedFromRunStart && !timing ? field.StartGateDistance(at) : field.Distance(at);
                Console.WriteLine($"tick {tick} physics {sim.Engine.PhysicsTicks}: world {sim.PlayerWorld.x:0},{sim.PlayerWorld.y:0} "
                    + $"{(timing ? "timing" : "untimed")} progress {sim.Progress} distance {d:0}");
                return true;
            });
            Console.WriteLine($"finished {r.Finished} time {r.Time:0.00} physics {r.PhysicsTicks}");
            return 0;
        }

        /// <summary>Runs the planner and checks its best replays from the start as found; null: no finish.</summary>
        internal static Planner.Route Plan1(Func<Simulation> create, string caps, OfflineSolver.ForcedPath forced, TechGrafts mined, Planner.Options options,
            Action<string> log, out Planner planner)
        {
            planner = new Planner(create, caps, forced, mined, options);
            Planner p = planner;
            planner.Found += r => log($"plan finish {r.Time:0.00}s ({r.RunTicks} physics ticks, {r.Inputs.Count} inputs) via {r.How}, width {r.Width}, "
                + $"{r.Ticks} ticks, {p.Expanded} nodes, {r.Wall:0.0}s");
            Planner.Route route = planner.Run(log);
            if (route == null) return null;
            Planner.Route checkedRoute = null;
            Solve.OnThread(() =>
            {
                Simulation sim = create();
                RunResult r = sim.Run(route.Inputs);
                if (r.Finished && Math.Abs(r.Time - route.Time) < .0001f && r.PhysicsTicks == route.RunTicks) checkedRoute = route;
                else log($"plan best {route.Time:0.00}s replays from the start as {(r.Finished ? r.Time.ToString("0.00", CultureInfo.InvariantCulture) + "s" : r.EndReason)}");
            });
            return checkedRoute;
        }
    }
}
