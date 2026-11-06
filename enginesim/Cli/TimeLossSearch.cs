using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace IGTAP.EngineSim.Cli
{
    static class TimeLossSearch
    {
        const string Usage = "usage: timeloss search <trace.json> <route.json> [--segment k | --window a-b] [--depth 1|2|3] [--radius R] [--beam B] [--threads T]";
        const float Pos = 4f, Vel = .05f, Tick = .02f;
        const int Late = 2;
        const int LateStart = 50;
        const int Splice = 64;
        static readonly int[] PressHolds = { 1, 2, 3, 4, 6, 8, 11, 15, 24, 40 };

        sealed class Settings
        {
            public int Depth = 2, Radius = Humanizer.Radius, Beam = 8, Threads = Environment.ProcessorCount;
            public int? Segment;
            public (int from, int to)? Window;
        }

        struct Arrival
        {
            public long Physics;
            public float Clock;
            public int Fingerprint;
        }

        sealed class Cand
        {
            public List<InputTick> Inputs;
            public string What;
            public int Edits;
            public Arrival? At;
            public float Near;
            public long Gain;
            public int Segment;
            public string Mode;
            public RunResult Whole;
        }

        sealed class Leg
        {
            public int From, To, Lo, Hi;
            public string Mode;
            public Arrival? Base;
            public int Tried, Arrived, Ties;
            public Cand Best, Survivor;
            public List<Cand> Gaining = new List<Cand>();
        }

        sealed class Result
        {
            public string Name;
            public List<Difficulty.Anchor> Anchors;
            public RunResult Route;
            public List<Leg> Legs = new List<Leg>();
            public Cand Best;
            public Settings Settings;
            public long Runs;
            public double Seconds;
        }

        internal sealed class Row
        {
            public int Segment, From, To;
            public string Anchor, Next;
            public long Gain, SegmentGain, FreeGain;
            public string Change, SegmentChange, FreeChange;
            public float Time;
            public long RunTicks;
        }

        internal static int Run(string[] args)
        {
            if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) || args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            Settings s = Parse(args);
            var clock = Stopwatch.StartNew();
            Result r = Search(args[0], args[1], s, line => Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {line}"));
            Console.WriteLine(Table(r));
            if (r.Best != null)
            {
                string file = Write(args[0], args[1], r);
                Console.WriteLine($"best: {r.Best.What} (segment {r.Best.Segment}, {r.Best.Mode}): {Seconds(r.Route)} -> "
                    + $"{r.Best.Whole.Time:0.00}s ({r.Best.Whole.PhysicsTicks} physics ticks) -> {file}");
            }
            else Console.WriteLine("best: no edit found beats the route at the finish");
            Console.WriteLine($"{r.Runs} runs in {r.Seconds:0.0} s on {s.Threads} threads");
            return 0;
        }

        internal static List<Row> Rows(string tracePath, string routePath, int threads, int depth = 2, int radius = Humanizer.Radius)
        {
            Result r = Search(tracePath, routePath, new Settings { Threads = threads, Depth = depth, Radius = radius }, null);
            var rows = new List<Row>();
            for (int k = 0; k + 1 < r.Anchors.Count; k++)
            {
                Leg next = r.Legs.FirstOrDefault(l => l.From == k && l.Mode == "next"), free = r.Legs.FirstOrDefault(l => l.From == k && l.Mode == "free");
                if (next == null) continue;
                Cand survivor = new[] { next.Survivor, free?.Survivor }.Where(c => c != null)
                    .OrderBy(c => c.Whole.Time).ThenBy(c => c.Whole.PhysicsTicks).FirstOrDefault();
                rows.Add(new Row
                {
                    Segment = k, From = r.Anchors[k].Tick, To = r.Anchors[k + 1].Tick, Anchor = r.Anchors[k].Kind, Next = r.Anchors[k + 1].Kind,
                    Gain = survivor == null ? 0 : FinishGain(survivor.Whole, r.Route), Change = survivor?.What,
                    Time = survivor?.Whole.Time ?? r.Route.Time, RunTicks = survivor?.Whole.PhysicsTicks ?? r.Route.PhysicsTicks,
                    SegmentGain = Math.Max(0, next.Best?.Gain ?? 0), SegmentChange = next.Best?.Gain > 0 ? next.Best.What : null,
                    FreeGain = Math.Max(0, free?.Best?.Gain ?? 0), FreeChange = free?.Best?.Gain > 0 ? free.Best.What : null,
                });
            }
            return rows;
        }

        static Settings Parse(string[] args)
        {
            var s = new Settings
            {
                Depth = Solve.Option(args, "--depth", 2), Radius = Solve.Option(args, "--radius", Humanizer.Radius), Beam = Solve.Option(args, "--beam", 8),
                Threads = Solve.Option(args, "--threads", Environment.ProcessorCount),
            };
            int segment = Solve.Option(args, "--segment", -1);
            if (segment >= 0) s.Segment = segment;
            string window = Solve.Option(args, "--window", (string)null);
            if (window != null)
            {
                string[] ends = window.Split('-');
                if (ends.Length != 2 || !int.TryParse(ends[0], out int a) || !int.TryParse(ends[1], out int b) || a < 0 || b <= a || s.Segment != null)
                    throw new ArgumentException(Usage);
                s.Window = (a, b);
            }
            if (s.Depth < 1 || s.Depth > 3 || s.Radius < 1 || s.Beam < 1 || s.Threads < 1) throw new ArgumentException(Usage);
            return s;
        }

        static Result Search(string tracePath, string routePath, Settings s, Action<string> log)
        {
            var watch = Stopwatch.StartNew();
            var (header, labels, inputs) = TechsUsed.Load(tracePath, routePath, "timeloss");
            if (s.Window is var (wa, wb) && wb > inputs.Count) throw new ArgumentException($"--window {wa}-{wb}: the route has {inputs.Count} input ticks");
            using var pool = new Pool(tracePath, header, labels, inputs, s.Window?.from, s.Threads);
            var r = new Result { Name = Path.GetFileName(routePath), Settings = s, Anchors = pool.Anchors(), Route = pool.Whole(new[] { inputs })[0] };
            log?.Invoke($"{r.Name}: {Seconds(r.Route)}, {r.Anchors.Count} anchors, depth {s.Depth}, radius {s.Radius}, beam {s.Beam}");
            if (s.Segment is int only && (only < 0 || only + 1 >= r.Anchors.Count))
                throw new ArgumentException($"--segment {only}: the route has segments 0-{r.Anchors.Count - 2}");
            var legs = new List<(int from, int end, string mode)>();
            if (s.Window is var (a, b))
            {
                int from = r.Anchors.FindIndex(x => x.Tick == a), end = r.Anchors.FindIndex(x => x.Tick >= b);
                if (from < 0 || end < 0) throw new ArgumentException($"--window {a}-{b}: the route's anchors run from input tick 0 to {r.Anchors[r.Anchors.Count - 1].Tick}");
                legs.Add((from, end, "window"));
            }
            else
                for (int k = 0; k + 1 < r.Anchors.Count; k++)
                {
                    if (s.Segment is int one && one != k) continue;
                    legs.Add((k, k + 1, "next"));
                    if (k + 2 < r.Anchors.Count) legs.Add((k, k + 2, "free"));
                }
            foreach (var (from, end, mode) in legs)
            {
                int to = r.Anchors[end].Kind == Timer && end + 1 < r.Anchors.Count ? end + 1 : end;
                Leg leg = Search(pool, inputs, r.Anchors, from, to, r.Anchors[from].Tick, s.Window?.to ?? r.Anchors[end].Tick, mode, s);
                r.Legs.Add(leg);
                log?.Invoke($"segment {from} {mode} ({r.Anchors[from].Kind} -> {r.Anchors[to].Kind}, edits in input ticks {leg.Lo}-{leg.Hi - 1}): {leg.Tried} candidates, "
                    + (leg.Base == null ? "the route itself doesn't arrive from its state there"
                        : (leg.Best?.Gain > 0 ? $"best +{leg.Best.Gain} ({leg.Best.What}), {leg.Gaining.Count} gaining" : "no gain")
                            + $", {leg.Arrived} arrive by {Late} ticks late, {leg.Ties} of them ties"));
            }
            List<Cand> splice = r.Legs.SelectMany(l => l.Gaining.OrderByDescending(c => c.Gain).ThenBy(c => c.Edits).Take(Splice)).ToList();
            RunResult[] whole = pool.Whole(splice.Select(c => c.Inputs).ToList());
            for (int i = 0; i < splice.Count; i++) splice[i].Whole = whole[i];
            foreach (Leg leg in r.Legs)
                leg.Survivor = leg.Gaining.Where(c => c.Whole != null && Beats(c.Whole, r.Route))
                    .OrderBy(c => c.Whole.Time).ThenBy(c => c.Whole.PhysicsTicks).ThenBy(c => c.Edits).FirstOrDefault();
            r.Best = r.Legs.Select(l => l.Survivor).Where(c => c != null).OrderBy(c => c.Whole.Time).ThenBy(c => c.Whole.PhysicsTicks).ThenBy(c => c.Edits).FirstOrDefault();
            log?.Invoke($"spliced {splice.Count} gaining candidates into the whole route: {splice.Count(c => Beats(c.Whole, r.Route))} beat it at the finish");
            r.Runs = pool.Runs;
            r.Seconds = watch.Elapsed.TotalSeconds;
            return r;
        }

        static Leg Search(Pool pool, List<InputTick> route, List<Difficulty.Anchor> anchors, int from, int to, int lo, int hi, string mode, Settings s)
        {
            var leg = new Leg { From = from, To = to, Lo = lo, Hi = hi, Mode = mode };
            leg.Base = pool.Play(new List<List<InputTick>> { route }, from, to, 2 * anchors[to].Physics - anchors[from].Physics + LateStart, float.PositiveInfinity).at[0];
            if (leg.Base == null) return leg;
            Arrival own = leg.Base.Value;
            bool timed = own.Clock >= 0f;
            long limit = own.Physics + Late + (timed ? LateStart : 0);
            float clockLimit = timed ? own.Clock + (Late + .5f) * Tick : float.PositiveInfinity;
            var seen = new Seen();
            seen.Add(route);
            List<Cand> level = Singles(route, lo, hi, s.Radius).Where(c => seen.Add(c.inputs))
                .Select(c => new Cand { Inputs = c.inputs, What = c.what, Edits = 1, Segment = from, Mode = mode }).ToList();
            for (int depth = 1; level.Count > 0; depth++)
            {
                var (at, near) = pool.Play(level.Select(c => c.Inputs).ToList(), from, to, limit, clockLimit);
                leg.Tried += level.Count;
                for (int i = 0; i < level.Count; i++)
                {
                    Cand c = level[i];
                    c.At = at[i];
                    c.Near = near[i];
                    if (at[i] is not Arrival a || !Comparable(a, own)) continue;
                    c.Gain = Gain(a, own);
                    leg.Arrived++;
                    if (c.Gain > 0) leg.Gaining.Add(c);
                    else if (c.Gain == 0 && !(a.Physics == own.Physics && a.Fingerprint == own.Fingerprint)) leg.Ties++;
                    if (leg.Best == null || c.Gain > leg.Best.Gain) leg.Best = c;
                }
                if (depth == s.Depth) break;
                List<Cand> beam = level.Where(c => c.At is Arrival a && Comparable(a, own) && !(a.Physics == own.Physics && a.Fingerprint == own.Fingerprint))
                    .OrderByDescending(c => c.Gain).ThenBy(c => c.At.Value.Physics)
                    .GroupBy(c => (c.At.Value.Physics, c.At.Value.Fingerprint)).Select(g => g.First()).Take(s.Beam).ToList();
                beam.AddRange(level.Where(c => c.At == null).OrderBy(c => c.Near).Take(s.Beam - beam.Count));
                level = beam.SelectMany(b => Singles(b.Inputs, lo, hi + b.Inputs.Count - route.Count, s.Radius).Where(c => seen.Add(c.inputs))
                    .Select(c => new Cand { Inputs = c.inputs, What = b.What + " + " + c.what, Edits = b.Edits + 1, Segment = from, Mode = mode })).ToList();
            }
            return leg;
        }

        static List<(List<InputTick> inputs, string what)> Singles(List<InputTick> route, int lo, int hi, int radius)
        {
            var c = new List<(List<InputTick>, string)>();
            foreach (Humanizer.Event e in Humanizer.Events(route, route))
            {
                if (e.Tick < lo || e.Tick >= hi) continue;
                foreach (var (inputs, first, what) in Polish.Candidates(route, e, radius))
                    if (first >= lo) c.Add((inputs, what));
                if (e.Kind != Humanizer.Kind.Release) continue;
                for (int q = lo; q < Math.Min(route.Count, hi + radius); q++)
                {
                    int d = q - e.Tick;
                    if (Math.Abs(d) <= radius) continue;
                    List<InputTick> moved = Humanizer.Shift(route, e, d, out int first);
                    if (moved != null && first >= lo) c.Add((moved, $"Release {e.Tick} moved to {q}"));
                }
            }
            for (int t = Math.Max(1, lo); t < hi; t++)
                foreach (int n in PressHolds)
                {
                    if (t + n >= route.Count) break;
                    bool up = !route[t - 1].held;
                    for (int j = t; j <= t + n && up; j++) up = !route[j].held && !route[j].press && !route[j].release && !route[j].dash && !route[j].reset;
                    if (!up) break;
                    var s = new List<InputTick>(route);
                    for (int j = t; j < t + n; j++) { InputTick h = s[j]; h.held = true; h.press = j == t; s[j] = h; }
                    InputTick r = s[t + n]; r.release = true; s[t + n] = r;
                    c.Add((s, $"Press added {t} held {n}"));
                }
            return c;
        }

        static bool Comparable(Arrival a, Arrival b) => (a.Clock < 0f) == (b.Clock < 0f);

        static long Gain(Arrival a, Arrival b) => a.Clock >= 0f ? (long)Math.Round((b.Clock - a.Clock) / Tick) : b.Physics - a.Physics;

        static bool Beats(RunResult r, RunResult route) =>
            r.Finished && (!route.Finished || OfflineSolver.Beats(r.Time, r.PhysicsTicks, route.Time, route.PhysicsTicks));

        static long FinishGain(RunResult r, RunResult route)
        {
            long timer = (long)Math.Round((route.Time - r.Time) / Tick);
            return timer != 0 ? timer : route.PhysicsTicks - r.PhysicsTicks;
        }

        static string Seconds(RunResult r) => r.Finished ? $"{r.Time:0.00}s ({r.PhysicsTicks} physics ticks)" : "not finished (" + r.EndReason + ")";

        sealed class Seen
        {
            readonly Dictionary<int, List<List<InputTick>>> _by = new Dictionary<int, List<List<InputTick>>>();

            public bool Add(List<InputTick> inputs)
            {
                var h = new HashCode();
                h.Add(inputs.Count);
                foreach (InputTick t in inputs) h.Add(t);
                int key = h.ToHashCode();
                if (!_by.TryGetValue(key, out List<List<InputTick>> same)) _by[key] = same = new List<List<InputTick>>();
                else if (same.Exists(x => x.SequenceEqual(inputs))) return false;
                same.Add(inputs);
                return true;
            }
        }

        const string Timer = "timer";

        static List<Difficulty.Anchor> Anchors(Simulation sim, List<InputTick> route, int? window)
        {
            List<Difficulty.Anchor> anchors = Difficulty.Anchors(sim, route, null, true, out TechSteps record, out RunResult run, out _);
            var extra = new Dictionary<int, string>();
            List<TechStep> steps = record.Steps;
            int start = sim.Goals == null && run.Finished ? steps.FindLastIndex(st => st.Timing) : -1;
            while (start > 0 && steps[start - 1].Timing) start--;
            if (start >= 0) extra[steps[start].Tick] = Timer;
            if (window is int w) extra[w] = "window";
            int last = anchors[anchors.Count - 1].Tick;
            foreach (int tick in extra.Keys.ToList())
                if (tick >= last || anchors.Exists(a => a.Tick == tick)) extra.Remove(tick);
            if (extra.Count == 0) return anchors;
            int end = extra.Keys.Max();
            sim.Run(route, null, tick =>
            {
                if (extra.TryGetValue(tick, out string kind))
                    anchors.Add(new Difficulty.Anchor
                    {
                        Kind = kind, Tick = tick, Physics = sim.Engine.PhysicsTicks, State = sim.Capture(tick), Move = MoveState.Of(sim, sim.Player), Progress = sim.Progress,
                    });
                return tick < end;
            }, Pool.Extra(sim));
            return anchors.OrderBy(a => a.Tick).ToList();
        }

        sealed class Pool : IDisposable
        {
            sealed class Lane
            {
                public Simulation Sim;
                public List<Difficulty.Anchor> Anchors;
            }

            readonly Worker<Lane>[] _lanes;
            long _runs;
            public long Runs => Interlocked.Read(ref _runs);

            public Pool(string tracePath, JsonElement header, string[] labels, List<InputTick> route, int? window, int threads)
            {
                _lanes = Enumerable.Range(0, threads).Select(i => new Worker<Lane>(() =>
                {
                    Simulation sim = TechsUsed.Create(tracePath, header, labels);
                    return new Lane { Sim = sim, Anchors = TimeLossSearch.Anchors(sim, route, window) };
                }, "timeloss " + i)).ToArray();
            }

            public List<Difficulty.Anchor> Anchors()
            {
                List<Difficulty.Anchor> anchors = null;
                _lanes[0].Do(lane => anchors = lane.Anchors);
                return anchors;
            }

            public RunResult[] Whole(IReadOnlyList<List<InputTick>> routes)
            {
                var r = new RunResult[routes.Count];
                Each(routes.Count, (lane, i) => r[i] = lane.Sim.Run(routes[i], null, null, Extra(lane.Sim)));
                return r;
            }

            public (Arrival?[] at, float[] near) Play(List<List<InputTick>> routes, int from, int to, long limit, float clockLimit)
            {
                var r = new Arrival?[routes.Count];
                var near = new float[routes.Count];
                Each(routes.Count, (lane, i) => r[i] = Arrive(lane, routes[i], from, to, limit, clockLimit, out near[i]));
                return (r, near);
            }

            static Arrival? Arrive(Lane lane, List<InputTick> inputs, int from, int to, long limit, float clockLimit, out float near)
            {
                Simulation sim = lane.Sim;
                Difficulty.Anchor target = lane.Anchors[to];
                bool finish = target.Kind == "finish", goal = target.Kind == "goal";
                Arrival? at = null;
                float nearest = float.PositiveInfinity;
                RunResult run = sim.Run(inputs, lane.Anchors[from].State, tick =>
                {
                    MoveState m = MoveState.Of(sim, sim.Player);
                    nearest = Math.Min(nearest, MathF.Sqrt((m.X - target.Move.X) * (m.X - target.Move.X) + (m.Y - target.Move.Y) * (m.Y - target.Move.Y)));
                    bool there = goal ? sim.Progress >= target.Progress : !finish && sim.Progress == target.Progress && Difficulty.SameState(m, target.Move, Pos, Vel);
                    if (there)
                    {
                        at = new Arrival { Physics = sim.Engine.PhysicsTicks, Clock = sim.Elapsed(sim.TimingCourse()), Fingerprint = sim.MovementFingerprint() };
                        return false;
                    }
                    return sim.Engine.PhysicsTicks < limit && !(sim.Elapsed(sim.TimingCourse()) > clockLimit);
                }, Extra(sim));
                if (finish && run.Finished)
                {
                    nearest = 0f;
                    if (run.PhysicsTicks <= limit && !(run.Time > clockLimit))
                        at = new Arrival { Physics = run.PhysicsTicks, Clock = run.Time, Fingerprint = sim.MovementFingerprint() };
                }
                near = nearest;
                return at;
            }

            internal static int Extra(Simulation sim) => sim.Goals != null ? 1000000 : 400;

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
                            for (int i; (i = Interlocked.Increment(ref next)) < count;)
                            {
                                work(lane, i);
                                Interlocked.Increment(ref _runs);
                            }
                        }
                        catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
                        finally { done.Signal(); }
                    });
                done.Wait();
                if (failure != null) throw new InvalidOperationException(failure.GetBaseException().Message, failure);
            }

            public void Dispose()
            {
                foreach (Worker<Lane> w in _lanes) w.Stop();
            }
        }

        static string Table(Result r)
        {
            var sb = new StringBuilder();
            Settings s = r.Settings;
            sb.AppendLine($"{r.Name}: {Seconds(r.Route)}; depth {s.Depth}, radius {s.Radius}, beam {s.Beam}");
            sb.AppendLine(" seg  anchor -> next      input ticks  physics      mode    edits in  tried   ties  arrives  gain  edits");
            foreach (Leg leg in r.Legs)
            {
                Difficulty.Anchor a = r.Anchors[leg.From], b = r.Anchors[leg.To];
                string gain = leg.Base == null ? "-" : leg.Best?.Gain > 0 ? "+" + leg.Best.Gain : "0";
                sb.AppendLine($"{leg.From,4}  {a.Kind,-7}-> {b.Kind,-8} {a.Tick + "-" + b.Tick,-12} {a.Physics + "-" + b.Physics,-12} {leg.Mode,-7} {leg.Lo + "-" + (leg.Hi - 1),-8} "
                    + $"{leg.Tried,6} {leg.Ties,6}  {(leg.Base == null ? "never" : leg.Base.Value.Physics.ToString(CultureInfo.InvariantCulture)),7}  {gain,4}  "
                    + (leg.Base == null ? "the route doesn't arrive from its own state" : leg.Best?.Gain > 0 ? leg.Best.What : "-"));
            }
            sb.AppendLine("whole route, each leg's gaining candidates spliced and played from the start:");
            sb.AppendLine(" seg  mode     spliced  beat  best finish                  edits");
            foreach (Leg leg in r.Legs.Where(l => l.Gaining.Count > 0))
            {
                List<Cand> played = leg.Gaining.Where(c => c.Whole != null).ToList();
                Cand fastest = played.Where(c => c.Whole.Finished).OrderBy(c => c.Whole.Time).ThenBy(c => c.Whole.PhysicsTicks).FirstOrDefault();
                string best = fastest == null ? "none finishes" : $"{fastest.Whole.Time:0.00}s ({fastest.Whole.PhysicsTicks} ticks)";
                sb.AppendLine($"{leg.From,4}  {leg.Mode,-7} {played.Count,7}  {played.Count(c => Beats(c.Whole, r.Route)),4}  {best,-28} "
                    + (leg.Survivor ?? fastest)?.What);
            }
            return sb.ToString().TrimEnd();
        }

        static string Write(string tracePath, string routePath, Result r)
        {
            JsonElement header = Solve.ReadHeader(tracePath);
            string file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(routePath)), Path.GetFileNameWithoutExtension(routePath) + "-timeloss.json");
            Cand best = r.Best;
            Solve.WriteShared(file, JsonSerializer.Serialize(new
            {
                course = header.TryGetProperty("course", out JsonElement course) ? course.GetInt32() : (int?)null,
                capabilities = header.TryGetProperty("capabilities", out JsonElement caps) ? caps.GetString() : null,
                time = best.Whole.Time, runTicks = best.Whole.PhysicsTicks, source = Path.GetFileName(routePath),
                timeloss = new { segment = best.Segment, mode = best.Mode, edits = best.What, gain = FinishGain(best.Whole, r.Route) },
                ticks = best.Inputs.Take(best.Whole.FinishTick + 1).Select(t => t.ToJson()),
            }));
            return file;
        }
    }
}
