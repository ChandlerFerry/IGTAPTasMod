using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace IGTAP.EngineSim.Cli
{
    sealed class Humanizer
    {
        const float Tie = .0001f;
        public const int Radius = 6;
        const int StateEvery = 4, Rounds = 6;
        static readonly float[] Partials = Enumerable.Range(0, 11).Select(i => (float)Math.Round(.3 + .05 * i, 2)).ToArray();

        public enum Kind { StickX, StickY, Press, Release, Dash }

        public sealed class Event
        {
            public Kind Kind;
            public int Tick, End;
            public float Value;
            public string Best;
            public int Earliest, Latest;
            public bool EarlyFailed, LateFailed, Start;
            public List<(float value, bool works)> Values;

            public bool Partial => Kind == Kind.StickX && Value != 0f && Math.Abs(Value) != 1f;
            public int Window => Latest - Earliest + 1;
            public (float lo, float hi)? Band
            {
                get
                {
                    if (Values == null) return null;
                    var sorted = Values.OrderBy(v => v.value).ToList();
                    int at = sorted.FindIndex(v => v.value == Value);
                    int lo = at, hi = at;
                    while (lo > 0 && sorted[lo - 1].works) lo--;
                    while (hi < sorted.Count - 1 && sorted[hi + 1].works) hi++;
                    return (sorted[lo].value, sorted[hi].value);
                }
            }
            public bool AnyValue => Values == null || Values.All(v => v.works);
            public bool Exact => !Start && Window == 1 || Partial && Band is (float lo, float hi) && lo == hi;
            public bool Free => !Exact && !EarlyFailed && !LateFailed && AnyValue;
            public string Class => Exact ? "exact" : Free ? "free" : Start ? "band" : "window " + Window;
        }

        public sealed class Result
        {
            public List<InputTick> Inputs;
            public float Time;
            public long RunTicks;
            public List<string> Changes = new List<string>();
            public List<Event> Before, After;
        }

        readonly Judge _main;
        readonly List<Worker> _workers = new List<Worker>();
        float _time;
        long _runTicks;

        public Humanizer(Func<Simulation> create, OfflineSolver.ForcedPath path, int threads)
        {
            _main = new Judge(create(), path);
            for (int i = 1; i < threads; i++) _workers.Add(new Worker(create, path));
        }

        public void Stop()
        {
            foreach (Worker w in _workers) w.Stop();
        }

        public Result Humanize(List<InputTick> best)
        {
            RunResult start = _main.Record(best);
            if (!_main.Valid) throw new InvalidOperationException("The best does not finish offline (" + (start.EndReason ?? "forced path") + ").");
            _time = start.Time;
            _runTicks = start.PhysicsTicks;
            var result = new Result { Time = _time, RunTicks = _runTicks };
            List<InputTick> original = best.Take(start.FinishTick + 1).ToList();
            List<InputTick> route = Simplify(original, result.Changes);
            RunResult end = _main.Record(route);
            result.Inputs = route;
            result.RunTicks = end.PhysicsTicks;
            result.Before = Tolerance(original, original);
            result.After = Tolerance(route, original);
            return result;
        }

        List<InputTick> Simplify(List<InputTick> route, List<string> changes)
        {
            for (int round = 0; round < Rounds; round++)
            {
                int before = changes.Count;
                route = Snap(route, changes);
                route = Widen(route, changes);
                route = Merge(route, false, changes);
                route = Merge(route, true, changes);
                route = DropPresses(route, changes);
                route = EarlyPresses(route, changes);
                if (changes.Count == before) break;
            }
            return route;
        }

        bool Accept(ref List<InputTick> route, List<InputTick> candidate, int first)
        {
            if (candidate == null || !_main.Accepts(candidate, first, _time, _runTicks)) return false;
            RunResult r = _main.Record(candidate);
            route = candidate.Take(r.FinishTick + 1).ToList();
            return true;
        }

        List<InputTick> Snap(List<InputTick> route, List<string> changes)
        {
            foreach (var (start, end, value) in Runs(route, false))
            {
                if (end > route.Count || value == 0f || Math.Abs(value) == 1f) continue;
                foreach (float to in new[] { Math.Sign(value), 0f })
                    if (Accept(ref route, SetStick(route, false, start, end, to), start))
                    {
                        changes.Add($"{Ticks(start, end)}: x {Num(value)} -> {Num(to)}");
                        break;
                    }
            }
            return route;
        }

        List<InputTick> Widen(List<InputTick> route, List<string> changes)
        {
            foreach (var (start, end, value) in Runs(route, false))
            {
                if (end > route.Count || value == 0f || Math.Abs(value) == 1f) continue;
                float sign = Math.Sign(value);
                var band = new List<float>();
                int at = Array.IndexOf(Partials, (float)Math.Round(Math.Abs(value), 2));
                if (at < 0) continue;
                for (int i = at; i >= 0 && (i == at || _main.Accepts(SetStick(route, false, start, end, sign * Partials[i]), start, _time, _runTicks)); i--) band.Insert(0, Partials[i]);
                for (int i = at + 1; i < Partials.Length && _main.Accepts(SetStick(route, false, start, end, sign * Partials[i]), start, _time, _runTicks); i++) band.Add(Partials[i]);
                float middle = sign * band[band.Count / 2];
                if (middle != value && Accept(ref route, SetStick(route, false, start, end, middle), start))
                    changes.Add($"{Ticks(start, end)}: x {Num(value)} -> {Num(middle)} (works {Num(sign * band[0])}..{Num(sign * band[band.Count - 1])})");
            }
            return route;
        }

        List<InputTick> Merge(List<InputTick> route, bool y, List<string> changes)
        {
            string axis = y ? "y" : "x";
            for (int k = 0; ; k++)
            {
                var runs = Runs(route, y);
                if (k >= runs.Count) return route;
                var (start, end, value) = runs[k];
                bool gap = k > 0 && k + 1 < runs.Count && runs[k - 1].value == runs[k + 1].value;
                var options = new List<float>();
                if (y && value == 0f && !gap) continue;
                if (y) options.Add(0f);
                if (k > 0) options.Add(runs[k - 1].value);
                if (k + 1 < runs.Count) options.Add(runs[k + 1].value);
                foreach (float to in options.Distinct().Where(v => v != value))
                    if (Accept(ref route, SetStick(route, y, start, end, to), start))
                    {
                        changes.Add($"{Ticks(start, end)}: {axis} {Num(value)} -> {Num(to)}");
                        k = Math.Max(-1, k - 2);
                        break;
                    }
            }
        }

        List<InputTick> DropPresses(List<InputTick> route, List<string> changes)
        {
            for (int p = 0; p < route.Count; p++)
            {
                if (route[p].press && Accept(ref route, DropPress(route, p), p)) changes.Add($"tick {p}: jump press dropped");
                else if (p < route.Count && route[p].dash && Accept(ref route, DropDash(route, p), p)) changes.Add($"tick {p}: dash dropped");
            }
            return route;
        }

        List<InputTick> EarlyPresses(List<InputTick> route, List<string> changes)
        {
            for (int p = 0; p < route.Count; p++)
            {
                foreach (bool dash in new[] { false, true })
                {
                    if (p >= route.Count || !(dash ? route[p].dash : route[p].press)) continue;
                    int q = p;
                    while (q > 0 && Accept(ref route, dash ? MoveDash(route, q, q - 1) : MovePress(route, q, q - 1), q - 1)) q--;
                    if (q < p) changes.Add($"{(dash ? "dash" : "jump press")} {p} -> {q}");
                }
            }
            return route;
        }

        List<Event> Tolerance(List<InputTick> route, List<InputTick> original)
        {
            var events = Events(route, original);
            var tests = new List<(Event e, int shift, float? value, List<InputTick> inputs, int first)>();
            foreach (Event e in events)
            {
                if (!e.Start)
                    for (int d = 1; d <= Radius; d++)
                    {
                        tests.Add((e, -d, null, Shift(route, e, -d, out int first), first));
                        tests.Add((e, d, null, Shift(route, e, d, out first), first));
                    }
                if (e.Kind == Kind.StickX || e.Kind == Kind.StickY)
                    foreach (float v in Grid(e).Where(v => v != e.Value))
                        tests.Add((e, 0, v, SetStick(route, e.Kind == Kind.StickY, e.Tick, e.End, v), e.Tick));
            }
            bool[] works = Test(route, tests.Select(t => (t.inputs, t.first)).ToList());
            var outcome = new Dictionary<(Event, int), bool?>();
            for (int i = 0; i < tests.Count; i++)
            {
                var t = tests[i];
                if (t.value is float v) (t.e.Values ??= new List<(float, bool)> { (t.e.Value, true) }).Add((v, works[i]));
                else outcome[(t.e, t.shift)] = t.inputs == null ? null : works[i];
            }
            foreach (Event e in events)
            {
                e.Earliest = e.Latest = e.Tick;
                if (e.Start) continue;
                for (int d = 1; d <= Radius; d++)
                {
                    bool? ok = outcome[(e, -d)];
                    if (ok != true) { e.EarlyFailed = ok == false; break; }
                    e.Earliest = e.Tick - d;
                }
                for (int d = 1; d <= Radius; d++)
                {
                    bool? ok = outcome[(e, d)];
                    if (ok != true) { e.LateFailed = ok == false; break; }
                    e.Latest = e.Tick + d;
                }
            }
            return events;
        }

        static IEnumerable<float> Grid(Event e)
        {
            if (e.Kind == Kind.StickY) return new[] { -1f, 0f, 1f };
            if (e.Value == 0f) return new[] { -1f }.Concat(Partials.Where((_, i) => i % 2 == 0).Reverse().Select(v => -v)).Append(0f)
                .Concat(Partials.Where((_, i) => i % 2 == 0)).Append(1f);
            float sign = Math.Sign(e.Value);
            return new[] { -sign, 0f }.Concat(Partials.Select(v => sign * v)).Append(sign);
        }

        bool[] Test(List<InputTick> route, List<(List<InputTick> inputs, int first)> tests)
        {
            var works = new bool[tests.Count];
            int next = -1;
            Exception failure = null;
            void Work(Judge judge)
            {
                try
                {
                    judge.Record(route);
                    for (int i; (i = Interlocked.Increment(ref next)) < tests.Count;)
                        works[i] = tests[i].inputs != null && judge.Accepts(tests[i].inputs, tests[i].first, _time, _runTicks);
                }
                catch (Exception ex) { failure = ex; }
            }
            using var done = new CountdownEvent(_workers.Count + 1);
            foreach (Worker w in _workers) w.Post(judge => { Work(judge); done.Signal(); });
            Work(_main);
            done.Signal();
            done.Wait();
            if (failure != null) throw new InvalidOperationException("Tolerance run failed", failure);
            return works;
        }

        static List<Event> Events(List<InputTick> route, List<InputTick> original)
        {
            var events = new List<Event>();
            foreach (bool y in new[] { false, true })
                foreach (var (start, end, value) in Runs(route, y))
                    if (start > 0 || value != 0f)
                        events.Add(new Event { Kind = y ? Kind.StickY : Kind.StickX, Tick = start, End = end, Value = value, Start = start == 0 });
            for (int i = 0; i < route.Count; i++)
            {
                if (route[i].press) events.Add(new Event { Kind = Kind.Press, Tick = i });
                if (IsRelease(route, i)) events.Add(new Event { Kind = Kind.Release, Tick = i });
                if (route[i].dash) events.Add(new Event { Kind = Kind.Dash, Tick = i, Value = route[i].dashJump });
            }
            foreach (Event e in events) e.Best = e.Tick < original.Count ? Describe(original[e.Tick]) : "-";
            return events.OrderBy(e => e.Tick).ThenBy(e => e.Kind).ToList();
        }

        static List<InputTick> Shift(List<InputTick> route, Event e, int d, out int first)
        {
            int q = e.Tick + d;
            first = Math.Min(e.Tick, q);
            if (q < 0 || q >= route.Count) return null;
            switch (e.Kind)
            {
                case Kind.StickX:
                case Kind.StickY:
                {
                    bool y = e.Kind == Kind.StickY;
                    float previous = Axis(route[e.Tick - 1], y);
                    if (d < 0)
                    {
                        int previousStart = e.Tick - 1;
                        while (previousStart > 0 && Axis(route[previousStart - 1], y) == previous) previousStart--;
                        return q > previousStart ? SetStick(route, y, q, e.Tick, e.Value) : null;
                    }
                    return q < e.End ? SetStick(route, y, e.Tick, q, previous) : null;
                }
                case Kind.Press: return MovePress(route, e.Tick, q);
                case Kind.Release: return MoveRelease(route, e.Tick, q);
                default: return MoveDash(route, e.Tick, q);
            }
        }

        static float Axis(InputTick k, bool y) { return y ? k.y : k.x; }

        static List<(int start, int end, float value)> Runs(List<InputTick> route, bool y)
        {
            var runs = new List<(int, int, float)>();
            for (int i = 0; i < route.Count;)
            {
                int j = i + 1;
                while (j < route.Count && Axis(route[j], y) == Axis(route[i], y)) j++;
                runs.Add((i, j, Axis(route[i], y)));
                i = j;
            }
            return runs;
        }

        static List<InputTick> SetStick(List<InputTick> route, bool y, int from, int to, float value)
        {
            var c = new List<InputTick>(route);
            for (int i = from; i < to; i++)
            {
                InputTick k = c[i];
                if (y) k.y = value; else k.x = value;
                c[i] = k;
            }
            return c;
        }

        static bool HeldBefore(List<InputTick> route, int i) { return i > 0 && route[i - 1].held; }

        static bool IsRelease(List<InputTick> route, int i)
        {
            return !route[i].press && (route[i].release || HeldBefore(route, i) && !route[i].held);
        }

        static List<InputTick> Held(List<InputTick> c, int from, int to, bool held)
        {
            for (int j = from; j < to; j++) { InputTick k = c[j]; k.held = held; c[j] = k; }
            return c;
        }

        static List<InputTick> MovePress(List<InputTick> route, int p, int q)
        {
            if (q < 0 || q >= route.Count || route[p].release) return null;
            bool before = HeldBefore(route, p);
            var c = new List<InputTick>(route);
            if (q < p)
            {
                if (HeldBefore(route, q) != before) return null;
                for (int j = q; j < p; j++) if (route[j].press || route[j].release || route[j].dash || route[j].held != before) return null;
                Held(c, q, p, before || route[p].held);
            }
            else
            {
                for (int j = p + 1; j <= q; j++) if (route[j].press || route[j].release || route[j].held != route[p].held) return null;
                Held(c, p, q, before);
            }
            InputTick a = c[p]; a.press = false; c[p] = a;
            InputTick b = c[q]; b.press = true; c[q] = b;
            return c;
        }

        static List<InputTick> MoveRelease(List<InputTick> route, int r, int q)
        {
            if (q < 1 || q >= route.Count || route[r].held) return null;
            var c = new List<InputTick>(route);
            if (q < r)
            {
                for (int j = q; j < r; j++) if (route[j].press || route[j].release || !route[j].held) return null;
                if (!route[q - 1].held) return null;
                Held(c, q, r, false);
            }
            else
            {
                for (int j = r + 1; j <= q; j++) if (route[j].press || route[j].release || route[j].held) return null;
                Held(c, r, q, true);
            }
            if (route[r].release)
            {
                InputTick a = c[r]; a.release = false; c[r] = a;
                InputTick b = c[q]; b.release = true; c[q] = b;
            }
            return c;
        }

        static List<InputTick> DropPress(List<InputTick> route, int p)
        {
            var c = new List<InputTick>(route);
            InputTick a = c[p]; a.press = false; c[p] = a;
            if (HeldBefore(route, p) || route[p].release) return c;
            int e = p;
            while (e < route.Count && route[e].held && (e == p || !route[e].press)) e++;
            Held(c, p, e, false);
            if (e < route.Count && route[e].release && !route[e].press && !route[e].held)
            {
                InputTick k = c[e]; k.release = false; c[e] = k;
            }
            return c;
        }

        static List<InputTick> MoveDash(List<InputTick> route, int d, int q)
        {
            if (q < 0 || q >= route.Count || route[d].springDash != 0) return null;
            for (int j = Math.Min(d, q); j <= Math.Max(d, q); j++)
                if (j != d && (route[j].dash || route[j].ArmsLock)) return null;
            var c = new List<InputTick>(route);
            InputTick from = c[d], to = c[q];
            to.dash = true; to.dashJump = from.dashJump; to.lockX = from.lockX; to.lockY = from.lockY; to.turnX = from.turnX;
            from.dash = false; from.dashJump = 0; from.lockX = from.lockY = null; from.turnX = null;
            c[d] = from;
            c[q] = to;
            return c;
        }

        static List<InputTick> DropDash(List<InputTick> route, int d)
        {
            var c = new List<InputTick>(route);
            InputTick k = c[d];
            k.dash = false;
            k.dashJump = 0;
            k.turnX = null;
            c[d] = k;
            return c;
        }

        static string Ticks(int start, int end) { return end - start == 1 ? "tick " + start : $"ticks {start}-{end - 1}"; }

        internal static string Num(float v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        static string Describe(InputTick k)
        {
            var parts = new List<string> { "x " + Num(k.x) };
            if (k.y != 0f) parts.Add("y " + Num(k.y));
            if (k.press) parts.Add("press");
            if (k.release) parts.Add("release");
            parts.Add(k.held ? "held" : "up");
            if (k.dash) parts.Add("dash" + (k.dashJump > 0 ? "+jump@" + k.dashJump : "") + (k.turnX.HasValue ? " turn " + Num(k.turnX.Value) : ""));
            return string.Join(" ", parts);
        }

        sealed class Judge
        {
            readonly Simulation _sim;
            readonly OfflineSolver.ForcedPath _path;
            SimSnapshot[] _states = Array.Empty<SimSnapshot>();
            List<InputTick> _recorded;
            RunResult _recordedResult;
            bool _recordedValid;
            int _waypoints;
            bool _avoided;

            public Judge(Simulation sim, OfflineSolver.ForcedPath path)
            {
                _sim = sim;
                _path = path ?? new OfflineSolver.ForcedPath();
            }

            public bool Valid { get; private set; }

            public RunResult Record(List<InputTick> route)
            {
                if (ReferenceEquals(route, _recorded)) { Valid = _recordedValid; return _recordedResult; }
                var states = new SimSnapshot[route.Count];
                RunResult r = Replay(route, null, float.PositiveInfinity, states);
                _states = states;
                _recorded = route;
                _recordedResult = r;
                _recordedValid = Valid;
                return r;
            }

            public bool Accepts(List<InputTick> inputs, int first, float time, long runTicks)
            {
                int s = Math.Min(first, _states.Length - 1);
                while (s > 0 && _states[s] == null) s--;
                RunResult r = Replay(inputs, s > 0 ? _states[s] : null, time + Tie, null);
                return Valid && Math.Abs(r.Time - time) <= Tie && r.PhysicsTicks <= runTicks;
            }

            RunResult Replay(List<InputTick> inputs, SimSnapshot from, float limit, SimSnapshot[] states)
            {
                if (from != null && from.Tick >= inputs.Count) from = null;
                _waypoints = from?.Waypoints ?? 0;
                _avoided = false;
                Action<int> onStep = _path.IsEmpty ? null : _ =>
                {
                    UnityEngine.Vector3 p = _sim.PlayerWorld;
                    if (_waypoints < _path.Via.Count && _path.Via[_waypoints].Contains(p)) _waypoints++;
                    foreach (OfflineSolver.Box box in _path.Avoid) if (box.Contains(p)) _avoided = true;
                };
                RunResult r = _sim.Run(inputs, from, tick =>
                {
                    if (_avoided) return false;
                    if (states != null && tick < states.Length && tick % StateEvery == 0)
                    {
                        SimSnapshot state = _sim.Capture(tick);
                        state.Waypoints = _waypoints;
                        states[tick] = state;
                    }
                    return !(_sim.Elapsed(_sim.TimingCourse()) > limit);
                }, onStep: onStep);
                Valid = r.Finished && _waypoints == _path.Via.Count && !_avoided;
                return r;
            }
        }

        sealed class Worker
        {
            readonly BlockingCollection<Action<Judge>> _jobs = new BlockingCollection<Action<Judge>>();

            public Worker(Func<Simulation> create, OfflineSolver.ForcedPath path)
            {
                new Thread(() =>
                {
                    var judge = new Judge(create(), path);
                    foreach (Action<Judge> job in _jobs.GetConsumingEnumerable()) job(judge);
                }) { IsBackground = true, Name = "humanize" }.Start();
            }

            public void Post(Action<Judge> job) { _jobs.Add(job); }

            public void Stop() { _jobs.CompleteAdding(); }
        }
    }

    static class Humanize
    {
        public static string OutputPath(string bestPath, string extension)
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bestPath)), Path.GetFileNameWithoutExtension(bestPath) + "-human" + extension);
        }

        public static int Run(string[] args)
        {
            string trace = args[0], bestPath = args[1];
            int threads = Solve.Option(args, "--threads", Environment.ProcessorCount);
            JsonElement header = Solve.ReadHeader(trace);
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            OfflineSolver.ForcedPath forced = Solve.ForcedPaths(args, course, caps);
            List<InputTick> best;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(bestPath), new JsonDocumentOptions { MaxDepth = 256 }))
                best = Solve.ReadInputs(doc.RootElement);
            var humanizer = new Humanizer(Solve.Creator(trace, header), forced, threads);
            try
            {
                Humanizer.Result result = humanizer.Humanize(best);
                string report = Write(bestPath, course, caps, result);
                Console.WriteLine(report);
                Console.WriteLine("-> " + OutputPath(bestPath, ".json"));
                Console.WriteLine("-> " + OutputPath(bestPath, ".md"));
            }
            finally { humanizer.Stop(); }
            return 0;
        }

        public static string Write(string bestPath, int course, string caps, Humanizer.Result r)
        {
            int exact = r.After.Count(e => e.Exact), exactBefore = r.Before.Count(e => e.Exact);
            Solve.WriteShared(OutputPath(bestPath, ".json"), JsonSerializer.Serialize(new
            {
                course, capabilities = caps, time = r.Time, runTicks = r.RunTicks, human = true, source = Path.GetFileName(bestPath),
                exact, exactBefore, changes = r.Changes,
                events = r.After.Select(e => new
                {
                    tick = e.Tick, input = Input(e), @class = e.Class, earliest = e.Earliest, latest = e.Latest,
                    band = e.Band is (float lo, float hi) && !e.AnyValue ? new[] { lo, hi } : null,
                }),
                ticks = r.Inputs.Select(t => t.ToJson()),
            }));
            string report = Report(Path.GetFileName(bestPath), course, caps, r, exact, exactBefore);
            Solve.WriteShared(OutputPath(bestPath, ".md"), report);
            return report;
        }

        static string Report(string source, int course, string caps, Humanizer.Result r, int exact, int exactBefore)
        {
            var s = new StringBuilder();
            string time = r.Time.ToString("0.00", CultureInfo.InvariantCulture);
            s.AppendLine($"# {course}_{caps} {time}: human version of `{source}`");
            s.AppendLine();
            s.AppendLine($"Same time ({time}), {r.RunTicks} physics ticks from the run start, {r.Inputs.Count} input ticks. "
                + $"Exact inputs: {exactBefore} in the best, {exact} here. Not a record: play it from the studio, never submit it.");
            s.AppendLine();
            s.AppendLine("## Changes from the best");
            s.AppendLine();
            if (r.Changes.Count == 0) s.AppendLine("None: every input of the best is needed as it is.");
            foreach (string c in r.Changes) s.AppendLine("- " + c);
            s.AppendLine();
            s.AppendLine("## Inputs");
            s.AppendLine();
            s.AppendLine($"Timing: the ticks the input works on (tried up to {Humanizer.Radius} ticks either side, the rest unchanged). Value: the stick values that work.");
            s.AppendLine();
            s.AppendLine("| Tick | Input | Best on that tick | Timing | Value | Class |");
            s.AppendLine("|---|---|---|---|---|---|");
            foreach (Humanizer.Event e in r.After)
                s.AppendLine($"| {e.Tick} | {Input(e)} | {e.Best} | {Timing(e)} | {Value(e)} | {e.Class} |");
            var hard = r.After.Where(e => e.Exact).ToList();
            if (hard.Count > 0)
            {
                s.AppendLine();
                s.AppendLine("## Exact inputs");
                s.AppendLine();
                foreach (Humanizer.Event e in hard)
                    s.AppendLine($"- tick {e.Tick}: {Input(e)}" + (e.Window == 1 && !e.Start ? " on this tick exactly" : "")
                        + (e.Partial && e.Band is (float lo, float hi) && lo == hi ? ", only at " + Humanizer.Num(e.Value) : ""));
            }
            return s.ToString();
        }

        static string Input(Humanizer.Event e)
        {
            int ticks = e.End - e.Tick;
            string span = (e.Kind == Humanizer.Kind.StickX || e.Kind == Humanizer.Kind.StickY) && ticks <= 3 ? $" for {ticks} tick{(ticks == 1 ? "" : "s")}" : "";
            switch (e.Kind)
            {
                case Humanizer.Kind.StickX:
                    return (e.Value == 1f ? "full right" : e.Value == -1f ? "full left" : e.Value == 0f ? "x neutral" : "x " + Humanizer.Num(e.Value)) + span;
                case Humanizer.Kind.StickY:
                    return (e.Value > 0f ? "up" : e.Value < 0f ? "down" : "y neutral") + span;
                case Humanizer.Kind.Press: return "press jump";
                case Humanizer.Kind.Release: return "release jump";
                default: return "press dash" + (e.Value > 0f ? $" (jump on dash frame {e.Value:0})" : "");
            }
        }

        static string Timing(Humanizer.Event e)
        {
            if (e.Start) return "from the start";
            string range = e.Window == 1 ? "this tick only" : $"{e.Earliest}-{e.Latest}";
            if (!e.EarlyFailed || !e.LateFailed) range += " (" + (!e.EarlyFailed && !e.LateFailed ? "no limit found" : !e.EarlyFailed ? "no earlier limit found" : "no later limit found") + ")";
            return range;
        }

        static string Value(Humanizer.Event e)
        {
            if (e.Values == null) return "";
            if (e.AnyValue) return "any";
            var (lo, hi) = e.Band.Value;
            return lo == hi ? "only " + Humanizer.Num(lo) : Humanizer.Num(lo) + ".." + Humanizer.Num(hi);
        }

        public sealed class Background
        {
            readonly Func<Simulation> _create;
            readonly OfflineSolver.ForcedPath _path;
            readonly int _course;
            readonly string _caps;
            readonly Action<string> _log;
            (string file, List<InputTick> inputs)? _next;
            bool _busy, _stopping;
            readonly Thread _thread;

            public Background(Func<Simulation> create, OfflineSolver.ForcedPath path, int course, string caps, Action<string> log)
            {
                _create = create;
                _path = path;
                _course = course;
                _caps = caps;
                _log = log;
                _thread = new Thread(Loop) { IsBackground = true, Name = "humanize" };
                _thread.Start();
            }

            public void Set(string file, List<InputTick> inputs)
            {
                lock (this) { _next = (file, inputs); Monitor.PulseAll(this); }
            }

            public void Finish()
            {
                lock (this)
                {
                    _stopping = true;
                    Monitor.PulseAll(this);
                    while (_next != null || _busy) Monitor.Wait(this);
                }
            }

            void Loop()
            {
                Humanizer humanizer = null;
                while (true)
                {
                    (string file, List<InputTick> inputs) job;
                    lock (this)
                    {
                        _busy = false;
                        Monitor.PulseAll(this);
                        while (_next == null && !_stopping) Monitor.Wait(this);
                        if (_next == null) return;
                        job = _next.Value;
                        _next = null;
                        _busy = true;
                    }
                    try
                    {
                        humanizer ??= new Humanizer(_create, _path, 1);
                        Humanizer.Result r = humanizer.Humanize(job.inputs);
                        Write(job.file, _course, _caps, r);
                        _log($"human version of {Path.GetFileName(job.file)}: {r.Changes.Count} changes, exact inputs {r.Before.Count(e => e.Exact)} -> "
                            + $"{r.After.Count(e => e.Exact)} -> {Path.GetFileName(OutputPath(job.file, ".json"))}");
                    }
                    catch (Exception ex) { _log($"human version of {Path.GetFileName(job.file)} failed: {ex.GetBaseException().Message}"); }
                }
            }
        }
    }
}
