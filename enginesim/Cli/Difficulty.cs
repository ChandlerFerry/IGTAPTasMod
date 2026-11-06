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
    static partial class Difficulty
    {
        const string Usage = "usage: difficulty <trace.json> <route.json> [--json] [--radius R] [--pos U] [--vel F] [--raw] [--threads T]"
            + " | difficulty <trace.json> --candidates <dir> [same options]";
        public const int Apart = 3;
        const int Merge = 3, Sync = 3;
        const int Late = 25;
        const float MinSpeed = 1f;

        sealed class Settings
        {
            public int Radius = Humanizer.Radius, Threads = Environment.ProcessorCount;
            public float Pos = 4f, Vel = .05f;
            public bool Raw;
            public const string Kind = "windows-3";
            public object Json => new { radius = Radius, sync = Sync, pos = Pos, vel = Vel, raw = Raw, measure = Kind };
        }

        internal struct Anchor
        {
            public string Kind;
            public int Tick;
            public long Physics;
            public SimSnapshot State;
            public MoveState Move;
            public int Progress;
        }

        internal static bool SameState(MoveState sample, MoveState route, float pos, float vel)
        {
            if (sample.AirJumps != route.AirJumps || sample.AirDashes != route.AirDashes || sample.WallJumps != route.WallJumps || sample.Buffer != route.Buffer) return false;
            float dx = sample.X - route.X, dy = sample.Y - route.Y, dvx = sample.Vx - route.Vx, dvy = sample.Vy - route.Vy;
            float speed = Math.Max(vel * MathF.Sqrt(route.Vx * route.Vx + route.Vy * route.Vy), MinSpeed);
            return dx * dx + dy * dy <= pos * pos && dvx * dvx + dvy * dvy <= speed * speed;
        }

        sealed class Line
        {
            public string Name;
            public List<InputTick> Inputs;
            public bool Human;
            public bool FullTas, Finished;
            public int Goals;
            public float Live;
            public int[] GoalTicks;
            public List<Anchor> Anchors;
            public long[] Physics;
        }

        enum Limit { Open, Fail, Blocked }

        sealed class Timed
        {
            public Humanizer.Event Event;
            public bool DashJump;
            public int Tick, Earliest, Latest;
            public long Physics;
            public Limit Early = Limit.Open, Late = Limit.Open;
            public int Window => Latest - Earliest + 1;
            public bool FramePerfect => Window == 1;
            public bool Done(int sign) => (sign < 0 ? Early : Late) != Limit.Open;
        }

        sealed class Pair
        {
            public int First, Second, Gap, Spread;
            public bool Forced;
        }

        sealed class Result
        {
            public Line Line;
            public Settings Settings;
            public List<Timed> Inputs;
            public List<Pair> Pairs;
            public int Runs;
            public double Seconds;
            public int FramePerfect => Inputs.Count(t => t.FramePerfect);
            public int BackToBack => Pairs.Count(p => p.Forced);
        }

        internal readonly struct Tally
        {
            public readonly int Inputs, FramePerfect, BackToBack;

            public Tally(int inputs, int framePerfect, int backToBack) { Inputs = inputs; FramePerfect = framePerfect; BackToBack = backToBack; }

            public object Json => new { inputs = Inputs, framePerfect = FramePerfect, backToBack = BackToBack };

            public override string ToString() => $"{FramePerfect} frame-perfect, {BackToBack} back-to-back of {Inputs} inputs";
        }

        internal sealed class Meter : IDisposable
        {
            readonly Pool _pool;
            readonly Settings _settings;

            public Meter(string tracePath, JsonElement header, string[] labels, int threads)
            {
                _settings = new Settings { Threads = threads };
                _pool = new Pool(tracePath, header, labels, threads);
            }

            public Tally Measure(string name, List<InputTick> inputs)
            {
                var (own, human) = _pool.Routes(new List<string> { name }, new List<List<InputTick>> { inputs }, false)[0];
                Result r = Difficulty.Measure(_pool, name, own, human, _settings);
                return new Tally(r.Inputs.Count, r.FramePerfect, r.BackToBack);
            }

            public void Dispose() => _pool.Dispose();
        }

        public static int Run(string[] args)
        {
            string dir = Solve.Option(args, "--candidates", (string)null);
            if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) || dir == null && args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            var s = new Settings
            {
                Radius = Solve.Option(args, "--radius", Humanizer.Radius), Pos = Solve.Option(args, "--pos", 4f), Vel = Solve.Option(args, "--vel", .05f),
                Threads = Solve.Option(args, "--threads", Environment.ProcessorCount), Raw = args.Contains("--raw"),
            };
            if (s.Radius < 1 || s.Threads < 1) throw new ArgumentException(Usage);
            return dir != null ? Candidates(args[0], dir, s) : One(args[0], args[1], s, args.Contains("--json"));
        }

        static int One(string tracePath, string routePath, Settings s, bool json)
        {
            var (header, labels, inputs) = TechsUsed.Load(tracePath, routePath, "difficulty");
            using var pool = new Pool(tracePath, header, labels, s.Threads);
            string name = Path.GetFileName(routePath);
            var (own, human) = pool.Routes(new List<string> { name }, new List<List<InputTick>> { inputs }, s.Raw)[0];
            Result r = Measure(pool, name, own, human, s);
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
            foreach (var group in routes.GroupBy(r => r.labels == null ? "" : string.Join("\n", r.labels)))
            {
                var list = group.ToList();
                using var pool = new Pool(tracePath, list[0].header, list[0].labels, s.Threads);
                var played = pool.Routes(list.Select(r => r.name).ToList(), list.Select(r => r.inputs).ToList(), s.Raw);
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        Result r = Measure(pool, list[i].name, played[i].own, played[i].human, s);
                        Solve.WriteShared(Path.Combine(outDir, list[i].name), JsonSerializer.Serialize(Json(r)));
                        Console.WriteLine($"{list[i].name}: {r.FramePerfect} frame-perfect, {r.BackToBack} back-to-back of {r.Inputs.Count} inputs ({r.Seconds:0.0} s)");
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

        static bool Expected(Exception ex)
        {
            return ex is IOException || ex is JsonException || ex is InvalidOperationException || ex is KeyNotFoundException || ex is FormatException
                || ex is ArgumentException || ex is UnauthorizedAccessException;
        }

        static bool Fresh(string sidecar, string candidate, Settings s)
        {
            if (!File.Exists(sidecar) || File.GetLastWriteTimeUtc(sidecar) <= File.GetLastWriteTimeUtc(candidate)) return false;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sidecar));
                JsonElement p = doc.RootElement.GetProperty("params");
                bool Near(string name, float v) => Math.Abs(p.GetProperty(name).GetDouble() - v) < 1e-6;
                return p.TryGetProperty("measure", out JsonElement kind) && kind.GetString() == Settings.Kind && p.GetProperty("radius").GetInt32() == s.Radius
                    && p.GetProperty("sync").GetInt32() == Sync && Near("pos", s.Pos) && Near("vel", s.Vel) && p.GetProperty("raw").GetBoolean() == s.Raw;
            }
            catch (Exception ex) when (ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException || ex is FormatException)
            {
                return false;
            }
        }

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
            var r = new Result { Line = line, Settings = s, Inputs = Inputs(line) };
            for (int d = 1; d <= s.Radius; d++)
            {
                var jobs = new List<(Timed input, int shift)>();
                foreach (Timed t in r.Inputs)
                    foreach (int sign in new[] { -1, 1 })
                        if (!t.Done(sign)) jobs.Add((t, sign * d));
                if (jobs.Count == 0) break;
                Limit?[] outcome = pool.Run(line, jobs, s);
                r.Runs += jobs.Count;
                for (int i = 0; i < jobs.Count; i++)
                {
                    var (t, shift) = jobs[i];
                    if (outcome[i] is Limit limit)
                    {
                        if (shift < 0) t.Early = limit;
                        else t.Late = limit;
                    }
                    else if (shift < 0) t.Earliest = t.Tick + shift;
                    else t.Latest = t.Tick + shift;
                }
            }
            r.Pairs = Pairs(r.Inputs);
            r.Seconds = watch.Elapsed.TotalSeconds;
            return r;
        }

        static List<Timed> Inputs(Line line)
        {
            int end = line.Anchors[line.Anchors.Count - 1].Tick;
            var inputs = new List<Timed>();
            foreach (Humanizer.Event e in Humanizer.Events(line.Inputs, line.Inputs))
            {
                if (e.Start || e.Tick >= end) continue;
                inputs.Add(new Timed { Event = e, Tick = e.Tick });
                if (e.Kind == Humanizer.Kind.Dash && e.Value > 0f) inputs.Add(new Timed { Event = e, DashJump = true, Tick = e.Tick + (int)e.Value });
            }
            foreach (Timed t in inputs)
            {
                t.Earliest = t.Latest = t.Tick;
                t.Physics = line.Physics[Math.Min(t.Tick, line.Physics.Length - 1)];
            }
            return inputs.OrderBy(t => t.Tick).ThenBy(t => t.Event.Kind).ThenBy(t => t.DashJump).ToList();
        }

        static List<InputTick> Move(List<InputTick> inputs, Timed t, int d, out int lo, out int hi)
        {
            lo = Math.Min(t.Tick, t.Tick + d);
            hi = Math.Max(t.Tick, t.Tick + d);
            if (t.DashJump)
            {
                int frame = (int)t.Event.Value + d;
                if (frame < 1) return null;
                lo = t.Event.Tick;
                var moved = new List<InputTick>(inputs);
                InputTick k = moved[t.Event.Tick];
                k.dashJump = frame;
                moved[t.Event.Tick] = k;
                return moved;
            }
            switch (t.Event.Kind)
            {
                case Humanizer.Kind.Press: return MovePress(inputs, t.Tick, d, ref hi);
                case Humanizer.Kind.Release: return MoveRelease(inputs, t.Tick, t.Tick + d);
                case Humanizer.Kind.Dash: return MoveDash(inputs, t.Tick, t.Tick + d);
                default: return Humanizer.Shift(inputs, t.Event, d, out _);
            }
        }

        static bool Released(List<InputTick> c, int i) => !c[i].press && (c[i].release || i > 0 && c[i - 1].held && !c[i].held);

        static void Held(List<InputTick> c, int from, int to, bool held)
        {
            for (int i = from; i < to; i++)
            {
                InputTick k = c[i];
                k.held = held;
                c[i] = k;
            }
        }

        static void Press(List<InputTick> c, int i, bool press)
        {
            InputTick k = c[i];
            k.press = press;
            if (press) k.held = true;
            c[i] = k;
        }

        static List<InputTick> MovePress(List<InputTick> inputs, int p, int d, ref int hi)
        {
            int q = p + d;
            if (q < 0 || q >= inputs.Count) return null;
            List<InputTick> c;
            if (d < 0)
            {
                for (int i = q; i < p; i++)
                    if (inputs[i].press || inputs[i].held || Released(inputs, i)) return null;
                c = new List<InputTick>(inputs);
                Press(c, p, false);
                Held(c, q, p, true);
                Press(c, q, true);
                return c;
            }
            int end = p + 1;
            while (end < inputs.Count && !inputs[end].press && !Released(inputs, end)) end++;
            if (q >= end)
            {
                if (end == inputs.Count || inputs[end].press) return null;
                c = MoveRelease(inputs, end, end + d);
                if (c == null) return null;
                hi = Math.Max(hi, end + d);
            }
            else c = new List<InputTick>(inputs);
            Press(c, p, false);
            Held(c, p, q, false);
            Press(c, q, true);
            return c;
        }

        static List<InputTick> MoveRelease(List<InputTick> inputs, int r, int q)
        {
            if (q < 1 || q >= inputs.Count) return null;
            for (int i = Math.Min(r, q); i <= Math.Max(r, q); i++)
                if (inputs[i].press) return null;
            var c = new List<InputTick>(inputs);
            bool flag = c[r].release;
            InputTick k = c[r];
            k.release = false;
            c[r] = k;
            if (q < r) Held(c, q, r, false);
            else
            {
                Held(c, r, q, true);
                Held(c, q, q + 1, false);
            }
            k = c[q];
            k.release = flag;
            c[q] = k;
            return c;
        }

        static List<InputTick> MoveDash(List<InputTick> inputs, int t, int q)
        {
            if (q < 0 || q >= inputs.Count) return null;
            for (int i = Math.Min(t, q); i <= Math.Max(t, q); i++)
                if (i != t && inputs[i].dash) return null;
            var c = new List<InputTick>(inputs);
            InputTick from = c[t], to = c[q];
            to.dash = true; to.dashJump = from.dashJump; to.lockX = from.lockX; to.lockY = from.lockY; to.turnX = from.turnX; to.turnY = from.turnY;
            to.pause = from.pause; to.pauseTick = from.pauseTick; to.pauseFrame = from.pauseFrame;
            from.dash = false; from.dashJump = 0; from.lockX = from.lockY = null; from.turnX = from.turnY = null; from.pause = from.pauseTick = from.pauseFrame = 0;
            c[t] = from;
            c[q] = to;
            return c;
        }

        static bool? Holds(Simulation sim, SimSnapshot[] states, Line line, Timed t, int d, Settings s)
        {
            List<InputTick> inputs = Move(line.Inputs, t, d, out int lo, out int hi);
            if (inputs == null) return null;
            int from = line.Anchors.FindLastIndex(a => a.Tick <= lo), to = line.Anchors.FindIndex(a => a.Tick > hi);
            if (from < 0) return null;
            if (to < 0) to = line.Anchors.Count - 1;
            Anchor next = line.Anchors[to];
            int at = next.Tick, slack = Sync + Math.Abs(d);
            int goal = Array.FindIndex(line.GoalTicks, g => g >= at);
            bool match = !(to == line.Anchors.Count - 1 && goal >= 0);
            int limit = goal < 0 ? at + slack : line.GoalTicks[goal] + Late + Math.Abs(d);
            bool works = false;
            RunResult run = sim.Run(inputs, states[from], tick =>
            {
                works = match && Math.Abs(tick - at) <= slack && sim.Progress == next.Progress
                        && SameState(MoveState.Of(sim, sim.Player) with { Buffer = next.Move.Buffer }, next.Move, s.Pos, s.Vel)
                    || line.FullTas && goal >= 0 && sim.Progress > goal;
                return !works && tick < limit;
            }, Late + slack);
            return works || goal >= 0 && (line.FullTas ? run.Progress > goal : run.Finished);
        }

        static List<Pair> Pairs(List<Timed> inputs)
        {
            var pairs = new List<Pair>();
            if (inputs.Count == 0) return pairs;
            int lo = inputs.Min(t => t.Earliest), n = inputs.Max(t => t.Latest) - lo + 1;
            var covered = new bool[n];
            var endsFrom = Enumerable.Repeat(int.MaxValue, n).ToArray();
            foreach (Timed t in inputs)
            {
                endsFrom[t.Earliest - lo] = Math.Min(endsFrom[t.Earliest - lo], t.Latest - lo);
                for (int i = t.Earliest; i <= t.Latest; i++) covered[i - lo] = true;
            }
            var after = new int[n];
            int least = int.MaxValue;
            for (int p = n - 1; p >= 0; p--)
            {
                after[p] = least;
                least = Math.Min(least, endsFrom[p]);
            }
            int lastFrom = inputs.Max(t => t.Earliest) - lo;
            var cost = Enumerable.Repeat(int.MaxValue, n).ToArray();
            var prev = new int[n];
            for (int p = 0; p <= Math.Min(least, n - 1); p++)
                if (covered[p]) { cost[p] = 0; prev[p] = -1; }
            for (int p = 0; p < n; p++)
            {
                if (cost[p] == int.MaxValue) continue;
                for (int q = p + 1, to = Math.Min(after[p], n - 1); q <= to; q++)
                {
                    if (!covered[q]) continue;
                    int c = cost[p] + (q - p < Apart ? 1 : 0);
                    if (c < cost[q]) { cost[q] = c; prev[q] = p; }
                }
            }
            int end = -1;
            for (int p = lastFrom; p < n; p++)
                if (cost[p] != int.MaxValue && (end < 0 || cost[p] < cost[end])) end = p;
            var points = new List<int>();
            for (int p = end; p >= 0; p = prev[p]) points.Add(p + lo);
            points.Reverse();
            var placed = new int[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                Timed t = inputs[i];
                placed[i] = points.Where(x => x >= t.Earliest && x <= t.Latest).OrderBy(x => Math.Abs(x - t.Tick)).ThenBy(x => x).First();
            }
            var forced = new HashSet<(int, int)>();
            for (int k = 1; k < points.Count; k++)
            {
                if (points[k] - points[k - 1] >= Apart) continue;
                int a = Enumerable.Range(0, inputs.Count).Where(i => placed[i] == points[k - 1]).OrderBy(i => inputs[i].Window).ThenBy(i => inputs[i].Tick).First();
                int b = Enumerable.Range(0, inputs.Count).Where(i => placed[i] == points[k]).OrderBy(i => inputs[i].Window).ThenBy(i => inputs[i].Tick).First();
                forced.Add((a, b));
                pairs.Add(new Pair { First = a, Second = b, Gap = Math.Abs(inputs[b].Tick - inputs[a].Tick), Spread = points[k] - points[k - 1], Forced = true });
            }
            List<IGrouping<int, int>> ticks = Enumerable.Range(0, inputs.Count).GroupBy(i => inputs[i].Tick).OrderBy(g => g.Key).ToList();
            for (int g = 1; g < ticks.Count; g++)
            {
                int gap = ticks[g].Key - ticks[g - 1].Key, a = ticks[g - 1].First(), b = ticks[g].First();
                if (gap < Apart && !forced.Contains((a, b)))
                    pairs.Add(new Pair { First = a, Second = b, Gap = gap, Spread = Math.Abs(placed[b] - placed[a]) });
            }
            return pairs.OrderBy(p => Math.Min(inputs[p.First].Tick, inputs[p.Second].Tick)).ThenBy(p => p.First).ToList();
        }

        static List<InputTick> Canonical(Simulation sim, List<InputTick> route)
        {
            route = route.Select(c =>
            {
                if (!c.dash) { c.turnX = c.turnY = null; c.dashJump = 0; }
                if (c.pause == 0) c.pauseTick = c.pauseFrame = 0;
                return c;
            }).ToList();
            bool fullTas = sim.Goals != null;
            int extra = fullTas ? 1000000 : 400;
            RunResult run = sim.Run(route, null, null, extra);
            int end = fullTas ? run.Progress > 0 ? (int)(run.GoalInputTicks[run.Progress - 1] - sim.InputBase) : -1
                : run.Finished ? run.FinishTick + 1 : -1;
            if (end < 1) return route;
            List<InputTick> cut = route.Take(end).ToList();
            while (cut.Count > 1)
            {
                List<InputTick> shorter = cut.GetRange(0, cut.Count - 1);
                if (!Alike(sim.Run(shorter, null, null, extra), run, fullTas)) break;
                cut = shorter;
            }
            return cut;
        }

        static bool Alike(RunResult a, RunResult b, bool fullTas)
        {
            if (!fullTas) return a.Finished == b.Finished && a.Time == b.Time && a.PhysicsTicks == b.PhysicsTicks;
            if (a.Progress != b.Progress) return false;
            for (int g = 0; g < a.Progress; g++)
                if (a.GoalInputTicks[g] != b.GoalInputTicks[g] || a.GoalPhysicsTicks[g] != b.GoalPhysicsTicks[g] || a.GoalHeldFrames[g] != b.GoalHeldFrames[g]) return false;
            return true;
        }

        static Line Trace(Simulation sim, string name, List<InputTick> inputs)
        {
            List<Anchor> anchors = Anchors(sim, inputs, null, false, out _, out RunResult run, out int[] goalTicks);
            bool fullTas = sim.Goals != null;
            int progress = goalTicks.Length;
            int end = anchors[anchors.Count - 1].Tick;
            var physics = new long[end + 1];
            physics[0] = anchors[0].Physics;
            sim.Run(inputs, null, tick =>
            {
                physics[tick] = sim.Engine.PhysicsTicks;
                return tick < end;
            });
            return new Line
            {
                Name = name, Inputs = inputs, FullTas = fullTas, Goals = progress, GoalTicks = goalTicks, Anchors = anchors, Physics = physics,
                Finished = fullTas ? progress == sim.Goals.Limit : run.Finished,
                Live = !fullTas ? run.Time : progress == 0 ? 0f : MacroEngine.LiveSeconds(run.GoalPhysicsTicks[progress - 1], run.GoalHeldFrames[progress - 1]),
            };
        }

        internal static List<Anchor> Anchors(Simulation sim, List<InputTick> route, SimSnapshot from = null, bool states = true)
        {
            return Anchors(sim, route, from, states, out _, out _, out _);
        }

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
            for (int i = 1; i < anchors.Count; i++)
                if (!seen[i])
                {
                    TechStep last = steps.Last(st => st.Tick <= anchors[i].Tick);
                    anchors[i] = new Anchor { Kind = anchors[i].Kind, Tick = anchors[i].Tick, Physics = last.Physics, Move = last.After, Progress = run.Progress };
                }
            return anchors;
        }

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

        static bool Settled(List<TechStep> steps, int i, Func<TechStep, bool> so)
        {
            return i > 0 && so(steps[i]) && so(steps[i - 1]) && (i < 2 || !so(steps[i - 2]));
        }

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

            public List<(List<InputTick> own, List<InputTick> human)> Routes(List<string> names, List<List<InputTick>> routes, bool raw)
            {
                var played = new (List<InputTick> own, List<InputTick> human)[routes.Count];
                Each(routes.Count, (lane, i) =>
                {
                    List<InputTick> own = Canonical(lane.Sim, routes[i]);
                    played[i] = (own, null);
                    if (raw) return;
                    bool strict = lane.Sim.StrictBuys;
                    lane.Sim.StrictBuys = false;
                    try { played[i].human = new Humanizer(() => lane.Sim, null, 1).Simplified(own); }
                    catch (InvalidOperationException ex) { Console.Error.WriteLine($"difficulty: {names[i]}: {ex.Message} Measured on its own inputs."); }
                    finally { lane.Sim.StrictBuys = strict; }
                });
                return played.ToList();
            }

            public Limit?[] Run(Line line, List<(Timed input, int shift)> jobs, Settings s)
            {
                var outcome = new Limit?[jobs.Count];
                Each(jobs.Count, (lane, i) =>
                {
                    Prepare(lane, line);
                    bool? holds = Holds(lane.Sim, lane.States, line, jobs[i].input, jobs[i].shift, s);
                    outcome[i] = holds == null ? Limit.Blocked : holds.Value ? null : Limit.Fail;
                });
                return outcome;
            }

            public RunResult[] Play(List<List<InputTick>> routes, bool human)
            {
                var results = new RunResult[routes.Count];
                Each(routes.Count, (lane, i) =>
                {
                    lane.Sim.Engine.HumanClass = human;
                    try { results[i] = lane.Sim.Run(routes[i], null, null, 400); }
                    finally { lane.Sim.Engine.HumanClass = false; }
                });
                return results;
            }

            public List<Anchor>[] AnchorsOf(List<List<InputTick>> routes)
            {
                var anchors = new List<Anchor>[routes.Count];
                Each(routes.Count, (lane, i) => anchors[i] = Difficulty.Anchors(lane.Sim, routes[i], null, false));
                return anchors;
            }

            public GlitchReport Report(List<InputTick> inputs, HumanWeights weights)
            {
                GlitchReport report = null;
                _lanes[0].Do(lane =>
                {
                    var record = new TechSteps(lane.Sim);
                    record.Attach();
                    record.Clear();
                    try { record.End(lane.Sim.Run(inputs, null, null, 400, _ => record.Step())); }
                    finally { record.Detach(); }
                    report = Glitches.Classify(record, weights);
                });
                return report;
            }

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

        static string Text(Timed t)
        {
            Humanizer.Event e = t.Event;
            if (t.DashJump) return $"jump on dash frame {e.Value:0}";
            if (e.Kind == Humanizer.Kind.Dash) return "press dash";
            return Humanize.Input(e);
        }

        static string KindOf(Timed t) => t.DashJump ? "dash-jump" : t.Event.Kind switch
        {
            Humanizer.Kind.StickX => "stick-x",
            Humanizer.Kind.StickY => "stick-y",
            Humanizer.Kind.Press => "press",
            Humanizer.Kind.Release => "release",
            _ => "dash",
        };

        static string LimitText(Limit l) => l == Limit.Fail ? "fail" : l == Limit.Blocked ? "blocked" : "open";

        static object Json(Result r)
        {
            Line line = r.Line;
            return new
            {
                route = line.Name, finished = line.Finished, goals = line.Goals, live = line.Live, human = line.Human, @params = r.Settings.Json,
                inputs = r.Inputs.Count, framePerfect = r.FramePerfect, backToBack = r.BackToBack,
                events = r.Inputs.Select((t, i) => new
                {
                    index = i, tick = t.Tick, physics = t.Physics, kind = KindOf(t), input = Text(t), earliest = t.Earliest, latest = t.Latest,
                    window = t.Window, early = LimitText(t.Early), late = LimitText(t.Late), framePerfect = t.FramePerfect,
                }),
                pairs = r.Pairs.Select(p => new { first = p.First, second = p.Second, gap = p.Gap, spread = p.Spread, forced = p.Forced }),
            };
        }

        static string Table(Result r)
        {
            Line line = r.Line;
            Settings s = r.Settings;
            var sb = new StringBuilder();
            sb.AppendLine($"{line.Name}: {(line.Finished ? "finished" : "not finished")}, "
                + (line.FullTas ? $"{line.Goals} goals, {line.Live:0.0000} s live" : $"{line.Live:0.0000} s course time")
                + (line.Human ? ", humanize's version" : ", its own inputs"));
            sb.AppendLine($"each input moved alone up to {s.Radius} ticks either way; it holds when the run reaches the next anchor in its state "
                + $"({s.Pos} u, {s.Vel * 100f} % of the speed, {Sync} ticks and the move early or late) or its next goal");
            sb.AppendLine(" tick  physics  input                              works on        limits (early/late)");
            foreach (Timed t in r.Inputs)
            {
                string window = t.FramePerfect ? $"{t.Tick} only" : $"{t.Earliest}-{t.Latest}";
                sb.AppendLine($"{t.Tick,5}  {t.Physics,7}  {Text(t),-34} {window,-15} {LimitText(t.Early)}/{LimitText(t.Late)}{(t.FramePerfect ? "  FRAME-PERFECT" : "")}");
            }
            if (r.Pairs.Count > 0)
            {
                sb.AppendLine($"inputs fewer than {Apart} ticks apart (at best: placed by the timing with the fewest back-to-back pairs):");
                foreach (Pair p in r.Pairs)
                {
                    Timed a = r.Inputs[p.First], b = r.Inputs[p.Second];
                    sb.AppendLine($"  ticks {a.Tick}-{b.Tick}: {Text(a)} -> {Text(b)}, {p.Gap} apart, "
                        + (p.Spread == 0 ? "on one tick" : $"{p.Spread} apart") + " at best" + (p.Forced ? "  BACK-TO-BACK" : ""));
                }
            }
            sb.AppendLine($"{r.FramePerfect} frame-perfect and {r.BackToBack} back-to-back of {r.Inputs.Count} inputs");
            sb.Append($"{r.Runs} runs in {r.Seconds:0.0} s on {s.Threads} threads");
            return sb.ToString();
        }
    }
}
