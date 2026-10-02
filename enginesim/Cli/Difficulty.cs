using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// <c>difficulty &lt;trace.json&gt; &lt;route.json&gt; [--json] [--samples N] [--sigma S] [--chain G] [--pos U] [--vel F] [--raw] [--threads T]</c>:
    /// the chance one human attempt at a route works (docs/fulltas.md "Difficulty"). The route played is humanize's version of it
    /// (<see cref="Humanizer.Simplified"/>: a search's stray inputs are no part of it; <c>--raw</c>, or a route that doesn't finish
    /// offline: its own inputs). It is cut at its anchors (<see cref="Anchors(List{TechStep}, int[], int, bool)"/>), where a
    /// player re-syncs (the start, standing, a wall cling, a respawn, a goal, a dash's freeze); each segment is played
    /// <c>--samples</c> times (more while few work) from the route's own state at its first anchor, every input event in it moved by
    /// a rounded normal of <c>--sigma</c> input ticks and a partial stick a controller step off half the time. A sample works when it
    /// reaches the next anchor's state, or the route's own inputs from there still reach the next goal. Bottlenecks less than
    /// <c>--chain</c> ticks apart are measured again as one span; P is the product of the chains and the other segments.
    /// <c>difficulty &lt;trace.json&gt; --candidates &lt;dir&gt;</c>: every *.json in dir into dir/difficulty/&lt;name&gt; (a sidecar newer than
    /// its candidate, made with the same parameters, is kept), an <c>@difficulty</c> progress line after each.
    /// </summary>
    static class Difficulty
    {
        const string Usage = "usage: difficulty <trace.json> <route.json> [--json] [--samples N] [--sigma S] [--chain G] [--pos U] [--vel F] [--raw] [--threads T]"
            + " | difficulty <trace.json> --candidates <dir> [same options]";
        /// <summary>A segment that works less often is a bottleneck.</summary>
        const double Bottleneck = .9;
        /// <summary>A span with fewer working samples is sampled --samples more at a time, up to this many times --samples: a chain
        /// of narrow segments works too rarely for one batch to see.</summary>
        const int MinWorks = 20, Rounds = 16;
        /// <summary>Anchors fewer input ticks apart are one; a sample may reach its next anchor this many ticks early or late.</summary>
        const int Merge = 3, Sync = 3;
        /// <summary>Input ticks after the route's own that a sample may reach its next goal on the route's inputs.</summary>
        const int Late = 25;
        /// <summary>The least velocity difference (u/s) that matches: the --vel share of a standing player's 0 is nothing.</summary>
        const float MinSpeed = 1f;
        /// <summary>The controller's x values on one side: rest (what the game makes of a tilt under 0.3), the partials, full tilt.</summary>
        static readonly float[] Tilts = new[] { 0f }.Concat(Humanizer.Partials).Append(1f).ToArray();

        sealed class Settings
        {
            public int Samples = 300, Chain = 15, Threads = Environment.ProcessorCount;
            public float Sigma = 1f, Pos = 4f, Vel = .05f;
            /// <summary>--raw: the route's own inputs, not humanize's version of them.</summary>
            public bool Raw;
            public object Json => new { samples = Samples, sigma = Sigma, chain = Chain, pos = Pos, vel = Vel, raw = Raw };
        }

        /// <summary>A point of a route where a player re-syncs (<see cref="Anchors"/>): its kind (<c>start</c>, <c>land</c>, <c>wall</c>,
        /// <c>dash</c> (a dash's freeze starts), <c>respawn</c>, <c>goal</c>, <c>finish</c> (a course's), <c>end</c> (an unfinished route's
        /// last input tick)), the input tick next there and the run's physics ticks so far, the simulation's state there (null when
        /// not asked for, and at a course's finish, where the run stops), the player then and the goals reached.</summary>
        internal struct Anchor
        {
            public string Kind;
            public int Tick;
            public long Physics;
            public SimSnapshot State;
            public MoveState Move;
            public int Progress;
        }

        /// <summary>The same resources (air jumps, air dashes, wall jumps), the position within <paramref name="pos"/> u and the
        /// velocity within <paramref name="vel"/> of <paramref name="route"/>'s speed (at least <see cref="MinSpeed"/>). The goals
        /// reached are the caller's to compare.</summary>
        internal static bool SameState(MoveState sample, MoveState route, float pos, float vel)
        {
            if (sample.AirJumps != route.AirJumps || sample.AirDashes != route.AirDashes || sample.WallJumps != route.WallJumps) return false;
            float dx = sample.X - route.X, dy = sample.Y - route.Y, dvx = sample.Vx - route.Vx, dvy = sample.Vy - route.Vy;
            float speed = Math.Max(vel * MathF.Sqrt(route.Vx * route.Vx + route.Vy * route.Vy), MinSpeed);
            return dx * dx + dy * dy <= pos * pos && dvx * dvx + dvy * dvy <= speed * speed;
        }

        /// <summary>A route replayed once: its anchors, the player at every input tick, its input events, techs and goals.</summary>
        sealed class Line
        {
            public string Name;
            public List<InputTick> Inputs;
            /// <summary>The inputs are humanize's version of the route's.</summary>
            public bool Human;
            public bool FullTas, Finished;
            public int Goals;
            public float Live;
            /// <summary>The input tick of each goal reached (a course: its finish).</summary>
            public int[] GoalTicks;
            public List<Anchor> Anchors;
            public List<Humanizer.Event> Events;
            public GlitchReport Report;
        }

        /// <summary>The inputs from one anchor to a later one: a segment (the next anchor), or a chain of them.</summary>
        sealed class Span
        {
            public int From, To;
            public List<Humanizer.Event> Events;
            public int Samples, Works;
            public double P = 1;
            public int? Chain;
            public Random Rng;
            /// <summary>The distinct samples so far, by where each event went.</summary>
            public readonly Dictionary<string, Job> Known = new Dictionary<string, Job>();
        }

        /// <summary>One distinct sample of a span: how many of the span's samples play these inputs, and whether they work.</summary>
        sealed class Job
        {
            public Span Span;
            public List<InputTick> Inputs;
            public int Count;
            public bool Works;
        }

        sealed class Result
        {
            public Line Line;
            public Settings Settings;
            public List<Span> Segments = new List<Span>(), Chains = new List<Span>();
            public double P;
            public int Runs;
            public double Seconds;
        }

        public static int Run(string[] args)
        {
            string dir = Solve.Option(args, "--candidates", (string)null);
            if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) || dir == null && args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            var s = new Settings
            {
                Samples = Solve.Option(args, "--samples", 300), Sigma = Solve.Option(args, "--sigma", 1f), Chain = Solve.Option(args, "--chain", 15),
                Pos = Solve.Option(args, "--pos", 4f), Vel = Solve.Option(args, "--vel", .05f), Threads = Solve.Option(args, "--threads", Environment.ProcessorCount),
                Raw = args.Contains("--raw"),
            };
            if (s.Samples < 1 || s.Sigma < 0f || s.Threads < 1) throw new ArgumentException(Usage);
            return dir != null ? Candidates(args[0], dir, s) : One(args[0], args[1], s, args.Contains("--json"));
        }

        static int One(string tracePath, string routePath, Settings s, bool json)
        {
            var (header, labels, inputs) = TechsUsed.Load(tracePath, routePath, "difficulty");
            using var pool = new Pool(tracePath, header, labels, s.Threads);
            string name = Path.GetFileName(routePath);
            List<InputTick> human = s.Raw ? null : pool.Human(new List<string> { name }, new List<List<InputTick>> { inputs })[0];
            Result r = Measure(pool, name, inputs, human, s);
            Console.WriteLine(json ? JsonSerializer.Serialize(Json(r)) : Table(r));
            return 0;
        }

        static int Candidates(string tracePath, string dir, Settings s)
        {
            var watch = Stopwatch.StartNew();
            string[] files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            string outDir = Path.Combine(dir, "difficulty");
            Directory.CreateDirectory(outDir);
            int done = 0, measured = 0, kept = 0, failed = 0;
            void Progress(string file) => Console.WriteLine("@difficulty " + JsonSerializer.Serialize(file == null
                ? (object)new { type = "progress", done, of = files.Length } : new { type = "progress", done, of = files.Length, file }));
            void Failed(string name, Exception ex)
            {
                Console.Error.WriteLine($"difficulty: {name}: {ex.GetBaseException().Message}");
                failed++;
                done++;
                Progress(name);
            }
            Progress(null);
            var routes = new List<(string name, JsonElement header, string[] labels, List<InputTick> inputs)>();
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                try
                {
                    if (!Fresh(Path.Combine(outDir, name), file, s))
                    {
                        var (header, labels, inputs) = TechsUsed.Load(tracePath, file, "difficulty");
                        routes.Add((name, header, labels, inputs));
                        continue;
                    }
                    kept++;
                    done++;
                    Progress(name);
                }
                catch (Exception ex) when (Expected(ex)) { Failed(name, ex); }
            }
            // One pool per goal list (a route's own adopted goals play on a simulation of their own); humanize's versions first, a
            // route a lane, then each route's samples on every lane.
            foreach (var group in routes.GroupBy(r => r.labels == null ? "" : string.Join("\n", r.labels)))
            {
                var list = group.ToList();
                using var pool = new Pool(tracePath, list[0].header, list[0].labels, s.Threads);
                List<List<InputTick>> human = s.Raw ? list.Select(_ => (List<InputTick>)null).ToList()
                    : pool.Human(list.Select(r => r.name).ToList(), list.Select(r => r.inputs).ToList());
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        Result r = Measure(pool, list[i].name, list[i].inputs, human[i], s);
                        Solve.WriteShared(Path.Combine(outDir, list[i].name), JsonSerializer.Serialize(Json(r)));
                        Console.WriteLine($"{list[i].name}: P {G4(r.P).ToString(CultureInfo.InvariantCulture)}, {Attempts(r.P)} attempts, "
                            + $"{r.Segments.Count(x => x.P < Bottleneck)} bottlenecks, {r.Chains.Count} chains ({r.Seconds:0.0} s)");
                        measured++;
                        done++;
                        Progress(list[i].name);
                    }
                    catch (Exception ex) when (Expected(ex)) { Failed(list[i].name, ex); }
                }
            }
            Console.WriteLine($"difficulty: {measured} measured, {kept} kept, {failed} failed in {watch.Elapsed.TotalSeconds:0.0} s");
            return 0;
        }

        /// <summary>A candidate that can't be read or measured: logged, the batch goes on.</summary>
        static bool Expected(Exception ex)
        {
            return ex is IOException || ex is JsonException || ex is InvalidOperationException || ex is KeyNotFoundException || ex is FormatException
                || ex is ArgumentException || ex is UnauthorizedAccessException;
        }

        /// <summary>The sidecar is newer than its candidate and was made with these parameters.</summary>
        static bool Fresh(string sidecar, string candidate, Settings s)
        {
            if (!File.Exists(sidecar) || File.GetLastWriteTimeUtc(sidecar) <= File.GetLastWriteTimeUtc(candidate)) return false;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sidecar));
                JsonElement p = doc.RootElement.GetProperty("params");
                bool Near(string name, float v) => Math.Abs(p.GetProperty(name).GetDouble() - v) < 1e-6;
                return p.GetProperty("samples").GetInt32() == s.Samples && p.GetProperty("chain").GetInt32() == s.Chain
                    && Near("sigma", s.Sigma) && Near("pos", s.Pos) && Near("vel", s.Vel) && p.GetProperty("raw").GetBoolean() == s.Raw;
            }
            catch (Exception ex) when (ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException || ex is FormatException)
            {
                return false;
            }
        }

        /// <summary>The route measured: humanize's version (<paramref name="human"/>, null for none), unless it doesn't finish under
        /// the run's strict buys where the route's own inputs do (humanize plays it without them, <see cref="Pool.Human"/>); then those.</summary>
        static Result Measure(Pool pool, string name, List<InputTick> own, List<InputTick> human, Settings s)
        {
            var watch = Stopwatch.StartNew();
            Line line = pool.Trace(name, human ?? own);
            line.Human = human != null;
            if (human != null && !line.Finished)
            {
                Line raw = pool.Trace(name, own);
                if (raw.Finished)
                {
                    Console.Error.WriteLine($"difficulty: {name}: humanize's version doesn't finish under strict buys. Measured on its own inputs.");
                    line = raw;
                }
            }
            var r = new Result { Line = line, Settings = s };
            int Tick(int anchor) => line.Anchors[anchor].Tick;
            List<Humanizer.Event> Within(int from, int to) => line.Events.Where(e => e.Tick >= Tick(from) && e.Tick < Tick(to)).ToList();

            for (int a = 0; a + 1 < line.Anchors.Count; a++) r.Segments.Add(new Span { From = a, To = a + 1, Events = Within(a, a + 1) });
            r.Runs += Measure(pool, line, r.Segments, s);

            // Bottlenecks closer than the chain gap leave no time to reset: one span from the first's anchor to the last's next.
            List<int> bottlenecks = Enumerable.Range(0, r.Segments.Count).Where(i => r.Segments[i].P < Bottleneck).ToList();
            for (int i = 0; i < bottlenecks.Count;)
            {
                int j = i;
                while (j + 1 < bottlenecks.Count && Tick(r.Segments[bottlenecks[j + 1]].From) - Tick(r.Segments[bottlenecks[j]].To) < s.Chain) j++;
                if (j > i)
                {
                    var chain = new Span { From = r.Segments[bottlenecks[i]].From, To = r.Segments[bottlenecks[j]].To };
                    chain.Events = Within(chain.From, chain.To);
                    for (int k = chain.From; k < chain.To; k++) r.Segments[k].Chain = r.Chains.Count;
                    r.Chains.Add(chain);
                }
                i = j + 1;
            }
            r.Runs += Measure(pool, line, r.Chains, s);
            r.P = r.Chains.Aggregate(1.0, (p, c) => p * c.P) * r.Segments.Where(x => x.Chain == null).Aggregate(1.0, (p, x) => p * x.P);
            r.Seconds = watch.Elapsed.TotalSeconds;
            return r;
        }

        /// <summary>Samples every span on the pool, --samples at a time while it has fewer than <see cref="MinWorks"/> working (up to
        /// <see cref="Rounds"/> times); returns the runs made (one per distinct sample).</summary>
        static int Measure(Pool pool, Line line, List<Span> spans, Settings s)
        {
            int runs = 0;
            List<Span> open = spans.Where(x => x.Events.Count > 0).ToList();
            foreach (Span span in open) span.Rng = new Random(Seed(line.Name, line.Anchors[span.From].Tick, line.Anchors[span.To].Tick));
            while (open.Count > 0)
            {
                List<Job> jobs = open.SelectMany(span => Sample(line, span, s)).ToList();
                pool.Run(line, jobs, s);
                runs += jobs.Count;
                foreach (Span span in open)
                {
                    span.Works = span.Known.Values.Where(j => j.Works).Sum(j => j.Count);
                    span.P = (double)span.Works / span.Samples;
                }
                open = open.Where(x => x.Works < MinWorks && x.Samples < s.Samples * Rounds).ToList();
            }
            return runs;
        }

        /// <summary>--samples more of the span's samples; returns the new distinct ones. Each event is moved by a rounded normal (kept
        /// between the span's anchor and <see cref="Sync"/> ticks past its end, never across another input), a partial stick a
        /// controller step off half the time. Seeded by the route and the span, so a measure repeats exactly; sigma 0 is the route
        /// itself.</summary>
        static List<Job> Sample(Line line, Span span, Settings s)
        {
            var jobs = new List<Job>();
            int lo = line.Anchors[span.From].Tick, to = line.Anchors[span.To].Tick, hi = Math.Min(to + Sync - 1, line.Inputs.Count - 1);
            Random rng = span.Rng;
            var key = new StringBuilder();
            for (int n = 0; n < s.Samples; n++, span.Samples++)
            {
                List<InputTick> inputs = line.Inputs;
                key.Clear();
                foreach (Humanizer.Event e in span.Events)
                {
                    int shift = (int)Math.Round(s.Sigma * Normal(rng), MidpointRounding.AwayFromZero);
                    bool snap = rng.NextDouble() < .5, up = rng.NextDouble() < .5;
                    float value = s.Sigma > 0f && snap && e.Partial ? Neighbour(e.Value, up) : e.Value;
                    inputs = Apply(inputs, e, shift, value, lo, hi, out int at);
                    key.Append(at).Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                }
                if (!span.Known.TryGetValue(key.ToString(), out Job job))
                {
                    job = new Job { Span = span, Inputs = inputs };
                    span.Known.Add(key.ToString(), job);
                    jobs.Add(job);
                }
                job.Count++;
            }
            return jobs;
        }

        /// <summary>The event at its new value, then moved by shift, or as near it as it goes inside lo..hi without crossing an input
        /// (humanize's moves refuse that); at is where it ends up.</summary>
        static List<InputTick> Apply(List<InputTick> inputs, Humanizer.Event e, int shift, float value, int lo, int hi, out int at)
        {
            at = e.Tick;
            Humanizer.Event now = Humanizer.EventAt(inputs, e.Kind, e.Tick);
            if (now == null) return inputs;
            if (value != now.Value)
            {
                inputs = Humanizer.SetStick(inputs, false, now.Tick, now.End, value);
                // A run that took its neighbour's value is no change any more.
                if ((now = Humanizer.EventAt(inputs, e.Kind, e.Tick)) == null) return inputs;
            }
            if (now.Start) return inputs;
            for (int d = shift; d != 0; d -= Math.Sign(d))
            {
                if (e.Tick + d < lo || e.Tick + d > hi) continue;
                List<InputTick> moved = Humanizer.Shift(inputs, now, d, out _);
                if (moved == null) continue;
                at = e.Tick + d;
                return moved;
            }
            return inputs;
        }

        /// <summary>A partial x a controller step up or down: the next partial, past the last full tilt, under the first rest.</summary>
        static float Neighbour(float value, bool up)
        {
            float size = Math.Abs(value);
            int at = 0;
            for (int i = 1; i < Tilts.Length; i++)
                if (Math.Abs(Tilts[i] - size) < Math.Abs(Tilts[at] - size)) at = i;
            return Math.Sign(value) * Tilts[Math.Clamp(at + (up ? 1 : -1), 0, Tilts.Length - 1)];
        }

        static double Normal(Random rng)
        {
            return Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());
        }

        /// <summary>FNV-1a of the route's name and the span's ticks (string.GetHashCode differs per process).</summary>
        static int Seed(string name, int from, int to)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in name + "/" + from + "-" + to) h = (h ^ c) * 1099511628211UL;
            return (int)(h ^ (h >> 32));
        }

        /// <summary>
        /// The sample from the route's state at the span's anchor. It works when it passes the next anchor's state
        /// (<see cref="SameState"/>, the same goals) within <see cref="Sync"/> ticks of the route's tick there, or its run, the
        /// route's own inputs on, reaches the next goal (a course: the finish) at most <see cref="Late"/> ticks after the route.
        /// The route's last anchor is its last goal: reaching it is all. A death ends the run.
        /// </summary>
        static bool Works(Simulation sim, SimSnapshot from, Line line, Span span, List<InputTick> inputs, Settings s)
        {
            Anchor next = line.Anchors[span.To];
            int to = next.Tick;
            int goal = Array.FindIndex(line.GoalTicks, t => t >= to);
            bool match = !(span.To == line.Anchors.Count - 1 && goal >= 0);
            int limit = goal < 0 ? to + Sync : line.GoalTicks[goal] + Late;
            bool works = false;
            RunResult run = sim.Run(inputs, from, tick =>
            {
                works = match && Math.Abs(tick - to) <= Sync && sim.Progress == next.Progress
                        && SameState(MoveState.Of(sim, sim.Player), next.Move, s.Pos, s.Vel)
                    || line.FullTas && goal >= 0 && sim.Progress > goal;
                return !works && tick < limit;
            }, Late + Sync);
            return works || goal >= 0 && (line.FullTas ? run.Progress > goal : run.Finished);
        }

        /// <summary>The route replayed for its anchors, techs and goals
        /// (<see cref="Anchors(Simulation, List{InputTick}, SimSnapshot, bool, out TechSteps, out RunResult, out int[])"/>).</summary>
        static Line Trace(Simulation sim, string name, List<InputTick> inputs)
        {
            List<Anchor> anchors = Anchors(sim, inputs, null, false, out TechSteps record, out RunResult run, out int[] goalTicks);
            bool fullTas = sim.Goals != null;
            int progress = goalTicks.Length;
            GlitchReport report = Glitches.Classify(record);
            int end = anchors[anchors.Count - 1].Tick;
            return new Line
            {
                Name = name, Inputs = inputs, FullTas = fullTas, Goals = progress, GoalTicks = goalTicks, Anchors = anchors,
                Finished = fullTas ? progress == sim.Goals.Limit : run.Finished,
                Live = !fullTas ? run.Time : progress == 0 ? 0f : MacroEngine.LiveSeconds(run.GoalPhysicsTicks[progress - 1], run.GoalHeldFrames[progress - 1]),
                // Nothing after a Full-TAS route's last goal is part of it (README ### Rules 32).
                Report = fullTas && progress > 0 ? report.Within(-1, run.GoalPhysicsTicks[progress - 1]) : report,
                Events = Humanizer.Events(inputs, inputs).Where(e => e.Tick < end).ToList(),
            };
        }

        /// <summary>The route's anchors from <paramref name="from"/> (null: the simulation's start), as
        /// <see cref="Anchors(Simulation, List{InputTick}, SimSnapshot, bool, out TechSteps, out RunResult, out int[])"/>.</summary>
        internal static List<Anchor> Anchors(Simulation sim, List<InputTick> route, SimSnapshot from = null, bool states = true)
        {
            return Anchors(sim, route, from, states, out _, out _, out _);
        }

        /// <summary>
        /// The route's anchors from <paramref name="from"/> (null: the simulation's start) to its end
        /// (<see cref="Anchors(List{TechStep}, int[], int, bool)"/>). The route plays twice: with the tech record
        /// (<paramref name="record"/>, <paramref name="run"/>) for the kinds and goals, then without its hooks, as a sample plays, for
        /// each anchor's player and, with <paramref name="states"/>, its state. <paramref name="goalTicks"/>: the input tick of every
        /// goal reached (a course: its finish), goals before <paramref name="from"/> included.
        /// </summary>
        internal static List<Anchor> Anchors(Simulation sim, List<InputTick> route, SimSnapshot from, bool states, out TechSteps record, out RunResult run,
            out int[] goalTicks)
        {
            bool fullTas = sim.Goals != null;
            int extra = fullTas ? 1000000 : 400, first = from?.Tick ?? 0;
            sim.Restore(from ?? sim.Start);
            var start = new Anchor { Kind = "start", Tick = first, State = states ? from ?? sim.Start : null, Move = MoveState.Of(sim, sim.Player), Progress = sim.Progress };
            var rec = new TechSteps(sim);
            rec.Attach();
            rec.Clear();
            run = sim.Run(route, from, null, extra, _ => rec.Step());
            rec.End(run);
            rec.Detach();
            record = rec;

            List<TechStep> steps = rec.Steps;
            goalTicks = fullTas ? run.GoalInputTicks.Take(run.Progress).Select(t => (int)(t - sim.InputBase)).ToArray()
                : run.Finished ? new[] { steps[steps.Count - 1].Tick } : Array.Empty<int>();
            List<Anchor> anchors = Anchors(steps, goalTicks, first, fullTas);
            start.Physics = steps.Count > 0 ? steps[0].Physics - 1 : 0;
            anchors[0] = start;
            if (anchors.Count == 1) return anchors;
            int end = anchors[anchors.Count - 1].Tick;

            var index = new Dictionary<int, int>();
            for (int i = 1; i < anchors.Count; i++) index[anchors[i].Tick] = i;
            var seen = new bool[anchors.Count];
            sim.Run(route, from, tick =>
            {
                if (index.TryGetValue(tick, out int i))
                {
                    anchors[i] = new Anchor
                    {
                        Kind = anchors[i].Kind, Tick = tick, Physics = sim.Engine.PhysicsTicks, State = states ? sim.Capture(tick) : null,
                        Move = MoveState.Of(sim, sim.Player), Progress = sim.Progress,
                    };
                    seen[i] = true;
                }
                return tick < end;
            }, extra);
            // A course's finish stops the run before its input tick is next: the player after the finishing step.
            for (int i = 1; i < anchors.Count; i++)
                if (!seen[i])
                {
                    TechStep last = steps.Last(st => st.Tick <= anchors[i].Tick);
                    anchors[i] = new Anchor { Kind = anchors[i].Kind, Tick = anchors[i].Tick, Physics = last.Physics, Move = last.After, Progress = run.Progress };
                }
            return anchors;
        }

        /// <summary>
        /// The anchors of a recorded run (<see cref="TechSteps"/>, from input tick <paramref name="first"/>) with its goals' input ticks
        /// (a course: its finish; goals before <paramref name="first"/> may be listed), Kind and Tick only: the start, every step that
        /// respawns, starts a dash's freeze, or is the second on the ground or on a wall off the ground (settled there, not passing
        /// through), every goal, and the end (the last goal; with none, the last step's input tick). Anchors fewer than
        /// <see cref="Merge"/> input ticks apart are one (the earlier; a goal first at a tick; the start and the end kept).
        /// </summary>
        internal static List<Anchor> Anchors(List<TechStep> steps, int[] goalTicks, int first, bool fullTas)
        {
            int end = goalTicks.Length > 0 ? goalTicks[goalTicks.Length - 1] : steps.Count > 0 ? steps[steps.Count - 1].Tick : first;
            var anchors = new List<Anchor> { new Anchor { Kind = "start", Tick = first } };
            if (end <= first) return anchors;
            var found = new List<Anchor>();
            for (int i = 0; i < steps.Count; i++)
            {
                TechStep st = steps[i];
                string kind = st.Calls.Exists(c => c.Call == TechCall.Respawn) ? "respawn"
                    : st.After.Dash && st.After.SinceDash == 0f ? "freeze"
                    : Settled(steps, i, x => x.After.Ground) ? "land"
                    : Settled(steps, i, x => x.After.Wall && !x.After.Ground) ? "wall" : null;
                if (kind != null) found.Add(new Anchor { Tick = st.Tick, Kind = kind });
            }
            foreach (int g in goalTicks) found.Add(new Anchor { Tick = g, Kind = fullTas ? "goal" : "finish" });
            foreach (Anchor a in found.OrderBy(a => a.Tick).ThenBy(a => a.Kind == "goal" ? 0 : 1))
                if (a.Tick - anchors[anchors.Count - 1].Tick >= Merge && a.Tick <= end - Merge) anchors.Add(a);
            anchors.Add(new Anchor { Tick = end, Kind = goalTicks.Length == 0 ? "end" : fullTas ? "goal" : "finish" });
            return anchors;
        }

        /// <summary>The second step in a row the player is so: settled there, not passing through.</summary>
        static bool Settled(List<TechStep> steps, int i, Func<TechStep, bool> so)
        {
            return i > 0 && so(steps[i]) && so(steps[i - 1]) && (i < 2 || !so(steps[i - 2]));
        }

        /// <summary>The samples' workers, each with a simulation of its own (the game's statics are per thread) and the states at the
        /// anchors of the route it last played, from one straight replay of it.</summary>
        sealed class Pool : IDisposable
        {
            sealed class Lane
            {
                public Simulation Sim;
                public Line Line;
                public SimSnapshot[] States;
            }

            readonly Worker<Lane>[] _lanes;
            public readonly string[] Labels;

            public Pool(string tracePath, JsonElement header, string[] labels, int threads)
            {
                Labels = labels;
                _lanes = Enumerable.Range(0, threads).Select(i => new Worker<Lane>(() => new Lane { Sim = TechsUsed.Create(tracePath, header, labels) }, "difficulty " + i)).ToArray();
            }

            public Line Trace(string name, List<InputTick> inputs)
            {
                Line line = null;
                _lanes[0].Do(lane => line = Difficulty.Trace(lane.Sim, name, inputs));
                return line;
            }

            /// <summary>Humanize's version of each route (its greedy passes, no slack report), a route a lane, played as the
            /// <c>humanize</c> command plays it (a Full-TAS run without strict buys: it finishes at its last goal, not the strict
            /// finish's 12 ticks later, which a run touching its last goal after its last input never reaches: it stops 20 idle
            /// steps after that input); null, said on stderr, where it doesn't finish.</summary>
            public List<List<InputTick>> Human(List<string> names, List<List<InputTick>> routes)
            {
                var human = new List<InputTick>[routes.Count];
                Each(routes.Count, (lane, i) =>
                {
                    bool strict = lane.Sim.StrictBuys;
                    lane.Sim.StrictBuys = false;
                    try { human[i] = new Humanizer(() => lane.Sim, null, 1).Simplified(routes[i]); }
                    catch (InvalidOperationException ex) { Console.Error.WriteLine($"difficulty: {names[i]}: {ex.Message} Measured on its own inputs."); }
                    finally { lane.Sim.StrictBuys = strict; }
                });
                return human.ToList();
            }

            public void Run(Line line, List<Job> jobs, Settings s)
            {
                Each(jobs.Count, (lane, i) =>
                {
                    Prepare(lane, line);
                    jobs[i].Works = Works(lane.Sim, lane.States[jobs[i].Span.From], line, jobs[i].Span, jobs[i].Inputs, s);
                });
            }

            /// <summary>work(lane, i) for every i below count, on every lane.</summary>
            void Each(int count, Action<Lane, int> work)
            {
                if (count == 0) return;
                int next = -1;
                Exception failure = null;
                using var done = new CountdownEvent(_lanes.Length);
                foreach (Worker<Lane> w in _lanes)
                    w.Post(lane =>
                    {
                        try
                        {
                            for (int i; (i = Interlocked.Increment(ref next)) < count;) work(lane, i);
                        }
                        catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
                        finally { done.Signal(); }
                    });
                done.Wait();
                if (failure != null) throw new InvalidOperationException(failure.GetBaseException().Message, failure);
            }

            /// <summary>The lane's states at the route's anchors (the start's is the simulation's own), unless it has them.</summary>
            static void Prepare(Lane lane, Line line)
            {
                if (lane.Line == line) return;
                var index = new Dictionary<int, int>();
                for (int i = 1; i + 1 < line.Anchors.Count; i++) index[line.Anchors[i].Tick] = i;
                var states = new SimSnapshot[line.Anchors.Count];
                if (index.Count > 0)
                {
                    int last = line.Anchors[line.Anchors.Count - 2].Tick;
                    Simulation sim = lane.Sim;
                    sim.Run(line.Inputs, null, tick =>
                    {
                        if (index.TryGetValue(tick, out int i)) states[i] = sim.Capture(tick);
                        return tick < last;
                    });
                    foreach (var (tick, i) in index.Select(kv => (kv.Key, kv.Value)))
                        if (states[i] == null) throw new InvalidOperationException($"{line.Name}: the replay never has input tick {tick} next");
                }
                lane.States = states;
                lane.Line = line;
            }

            public void Dispose()
            {
                foreach (Worker<Lane> w in _lanes) w.Stop();
            }
        }

        static double Round(double p) => Math.Round(p, 4);

        static double G4(double v) => double.Parse(v.ToString("G4", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        static string Attempts(double p) => p > 0 ? (1 / p).ToString("0.0", CultureInfo.InvariantCulture) : "inf";

        /// <summary>The techs and spike-grace ticks of the run's physics ticks after one anchor up to the next.</summary>
        static (string[] techs, int grace) Techs(Line line, Span span)
        {
            long from = line.Anchors[span.From].Physics, to = line.Anchors[span.To].Physics;
            GlitchReport within = line.Report.Within(from, to);
            return (within.Spans.Select(x => x.Tech).Distinct().ToArray(), within.GraceTicks);
        }

        static List<int> Spacing(Result r)
        {
            List<Span> b = r.Segments.Where(x => x.P < Bottleneck).ToList();
            return Enumerable.Range(1, Math.Max(0, b.Count - 1)).Select(i => r.Line.Anchors[b[i].From].Tick - r.Line.Anchors[b[i - 1].To].Tick).ToList();
        }

        static Span Tightest(Result r) => r.Segments.Count == 0 ? null : r.Segments.Aggregate((a, x) => x.P < a.P ? x : a);

        /// <summary>The sidecar and <c>--json</c>'s object (docs/fulltas.md "Difficulty").</summary>
        static object Json(Result r)
        {
            Line line = r.Line;
            Span tightest = Tightest(r);
            return new
            {
                route = line.Name, finished = line.Finished, goals = line.Goals, live = line.Live, human = line.Human, @params = r.Settings.Json,
                P = G4(r.P), attempts = r.P > 0 ? Math.Round(1 / r.P, 1) : (double?)null,
                bottlenecks = r.Segments.Count(x => x.P < Bottleneck), chains = r.Chains.Count,
                tightest = tightest == null ? null : new { segment = r.Segments.IndexOf(tightest), p = Round(tightest.P) },
                spacing = Spacing(r),
                segments = r.Segments.Select((x, i) =>
                {
                    var (techs, grace) = Techs(line, x);
                    Anchor a = line.Anchors[x.From], b = line.Anchors[x.To];
                    return new
                    {
                        index = i, from = a.Tick, to = b.Tick, fromPhysics = a.Physics, toPhysics = b.Physics, anchor = a.Kind, next = b.Kind,
                        events = x.Events.Count, samples = x.Samples, p = Round(x.P), bottleneck = x.P < Bottleneck, chain = x.Chain, techs, grace,
                    };
                }),
                chainList = r.Chains.Select((c, i) => new
                {
                    index = i, segments = Enumerable.Range(c.From, c.To - c.From), from = line.Anchors[c.From].Tick, to = line.Anchors[c.To].Tick,
                    events = c.Events.Count, samples = c.Samples, p = Round(c.P),
                }),
            };
        }

        static string Table(Result r)
        {
            Line line = r.Line;
            Settings s = r.Settings;
            string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.AppendLine($"{line.Name}: {(line.Finished ? "finished" : "not finished")}, "
                + (line.FullTas ? $"{line.Goals} goals, {line.Live:0.0000} s live" : $"{line.Live:0.0000} s course time")
                + (line.Human ? ", humanize's version" : ", its own inputs"));
            sb.AppendLine($"{s.Samples} samples a span, sigma {s.Sigma} ticks, chain gap {s.Chain} ticks, pos {s.Pos} u, vel {s.Vel * 100f} %");
            sb.AppendLine(" seg  input ticks  physics      anchor -> next      events samples  p      chain  techs");
            for (int i = 0; i < r.Segments.Count; i++)
            {
                Span x = r.Segments[i];
                Anchor a = line.Anchors[x.From], b = line.Anchors[x.To];
                var (techs, grace) = Techs(line, x);
                string tech = string.Join(", ", techs.Concat(grace > 0 ? new[] { "grace " + grace } : Array.Empty<string>()));
                sb.AppendLine($"{i,4}{(x.P < Bottleneck ? '*' : ' ')} {a.Tick + "-" + b.Tick,-12} {a.Physics + "-" + b.Physics,-12} {a.Kind,-7}-> {b.Kind,-8} "
                    + $"{x.Events.Count,6} {x.Samples,7}  {F(x.P)}  {(x.Chain == null ? "-" : x.Chain.ToString()),-5}  {tech}");
            }
            for (int i = 0; i < r.Chains.Count; i++)
            {
                Span c = r.Chains[i];
                sb.AppendLine($"chain {i}: segments {c.From}-{c.To - 1}, input ticks {line.Anchors[c.From].Tick}-{line.Anchors[c.To].Tick}, {c.Events.Count} events, "
                    + $"{c.Samples} samples, p {F(c.P)} (their product {F(r.Segments.Where(x => x.Chain == i).Aggregate(1.0, (p, x) => p * x.P))})");
            }
            Span tightest = Tightest(r);
            List<int> spacing = Spacing(r);
            sb.AppendLine($"P {G4(r.P).ToString(CultureInfo.InvariantCulture)}: {Attempts(r.P)} attempts; {r.Segments.Count(x => x.P < Bottleneck)} bottlenecks (p < {Bottleneck}), "
                + $"{r.Chains.Count} chains; tightest " + (tightest == null ? "-" : $"segment {r.Segments.IndexOf(tightest)} (p {F(tightest.P)})")
                + "; spacing " + (spacing.Count == 0 ? "-" : string.Join(", ", spacing)) + " ticks");
            sb.Append($"{r.Runs} runs in {r.Seconds:0.0} s on {s.Threads} threads");
            return sb.ToString();
        }
    }
}
