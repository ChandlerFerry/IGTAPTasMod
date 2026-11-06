using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace IGTAP.EngineSim.Cli
{
    sealed class Polish
    {
        const float Tie = .0001f;
        const int StateEvery = 2;
        int _keepFrom, _keepTo = int.MaxValue;

        public sealed class Result
        {
            public List<InputTick> Inputs;
            public float Time;
            public long RunTicks;
            public long Ticks;
            public int Sweeps, Kept;
            public List<string> Changes = new List<string>();
        }

        readonly Func<Simulation> _create;
        readonly OfflineSolver.ForcedPath _path;
        readonly Action<string> _log;
        readonly int _radius, _slack;
        readonly List<Worker<Judge>> _workers = new List<Worker<Judge>>();
        long _ticks;
        public bool Wide = true;
        public bool Sync = true;
        public bool Farm;

        public bool Lead;

        public int LeadTicks = 4;

        Result _floor;

        bool Better(float time, long runTicks, float bestTime, long bestRunTicks)
        {
            if (!Farm) return OfflineSolver.Beats(time, runTicks, bestTime, bestRunTicks);
            return runTicks < bestRunTicks || (runTicks == bestRunTicks && time < bestTime - Tie);
        }

        public Polish(Func<Simulation> create, OfflineSolver.ForcedPath path, int threads, Action<string> log, int radius = Humanizer.Radius, int slack = DefaultSlack)
        {
            _create = create;
            _path = path ?? new OfflineSolver.ForcedPath();
            _log = log;
            _radius = radius;
            _slack = slack;
            for (int i = 0; i < Math.Max(1, threads); i++) _workers.Add(new Worker<Judge>(() => new Judge(this), "polish"));
        }
        public const int DefaultSlack = 30;

        public static Result Once(Func<Simulation> create, OfflineSolver.ForcedPath path, int threads, Action<string> log, List<InputTick> seed,
            int from = 0, int to = int.MaxValue)
        {
            var polish = new Polish(create, path, threads, log);
            try { return polish.Run(seed, from: from, to: to); }
            finally { polish.Stop(); }
        }

        public void Stop() { foreach (Worker<Judge> w in _workers) w.Stop(); }

        List<InputTick> _route;
        float _time;
        long _runTicks;
        int _version;
        bool _fromStart;
        public Action<List<InputTick>, float, long> Swept;

        public Result Run(List<InputTick> seed, int maxSweeps = 50, int from = 0, int to = int.MaxValue)
        {
            _keepFrom = Math.Max(0, from - StateEvery);
            _keepTo = to;
            _floor = null;
            Result best = Sweeps(seed, maxSweeps, from, to, Lead);
            if (!Lead || from != 0 || to <= 0 || seed.Count == 0 || seed[0].reset) return best;
            _floor = best;
            for (int d = 1; d <= LeadTicks; d++)
            {
                List<InputTick> shifted = Enumerable.Repeat(Idle(seed[0]), d).Concat(seed).ToList();
                RunResult r = Check(shifted);
                if (!r.Finished)
                {
                    _log?.Invoke($"polish: {d} idle ticks in front of the seed: {r.EndReason}, no restart");
                    continue;
                }
                _log?.Invoke($"polish: restart with {d} idle ticks in front of the seed");
                Result other = Sweeps(shifted, maxSweeps, from, to, false);
                if (!Better(other.Time, other.RunTicks, best.Time, best.RunTicks))
                {
                    _log?.Invoke($"polish: the restart ends {other.Time:0.00}s ({other.RunTicks} physics ticks), not better than {best.Time:0.00}s ({best.RunTicks})");
                    continue;
                }
                other.Changes.Insert(0, $"{d} idle ticks in front of the seed");
                best = _floor = other;
            }
            _floor = null;
            best.Ticks = Interlocked.Read(ref _ticks);
            return best;
        }

        Result Sweeps(List<InputTick> seed, int maxSweeps, int from, int to, bool lead)
        {
            var result = new Result();
            _fromStart = false;
            RunResult start = Check(seed);
            if (!start.Finished) throw new InvalidOperationException("The seed doesn't finish offline (" + start.EndReason + ").");
            Set(seed.Take(start.FinishTick + 1).ToList(), start);
            _log?.Invoke($"polish: seed {_time:0.00}s ({_runTicks} physics ticks, {_route.Count} inputs)");
            var same = Enumerable.Range(1, 4).Select(k => (_route, _route.Count * k / 5, "restore check")).ToList();
            var checks = Test(same, sync: false);
            if (checks.Any(o => o == null || o.Value.runTicks != _runTicks || Math.Abs(o.Value.time - _time) > Tie))
            {
                _fromStart = true;
                _log?.Invoke("polish: the seed replays differently from its recorded states (" + string.Join(", ", checks.Select((o, k) =>
                    $"input {same[k].Item2}: {(o == null ? "no finish" : o.Value.time.ToString("0.00", CultureInfo.InvariantCulture) + "s/" + o.Value.runTicks)}"))
                    + "): every candidate replays from the start");
            }
            bool plateau = false;
            for (int sweep = 1; sweep <= maxSweeps; sweep++)
            {
                result.Sweeps = sweep;
                int kept = 0, tried = 0, finished = 0;
                bool Keep(List<(List<InputTick> inputs, int first, string what)> cands)
                {
                    if (cands.Count == 0) return false;
                    var outcomes = Test(cands);
                    tried += cands.Count;
                    finished += outcomes.Count(o => o != null);
                    var better = Enumerable.Range(0, cands.Count)
                        .Where(k => outcomes[k] is var (t, rt, _) && Better(t, rt, _time, _runTicks))
                        .OrderBy(k => Farm ? outcomes[k].Value.runTicks : 0L).ThenBy(k => outcomes[k].Value.time).ThenBy(k => outcomes[k].Value.runTicks).Take(4);
                    foreach (int best in better)
                    {
                        RunResult check = Check(cands[best].inputs);
                        if (!check.Finished || !Better(check.Time, check.PhysicsTicks, _time, _runTicks))
                        {
                            _log?.Invoke($"polish: {cands[best].what} replays from the start as {(check.Finished ? check.Time.ToString("0.00", CultureInfo.InvariantCulture) + "s/" + check.PhysicsTicks : check.EndReason)}: not kept");
                            continue;
                        }
                        string change = $"{cands[best].what}: {_time:0.00}s/{_runTicks} -> {check.Time:0.00}s/{check.PhysicsTicks} ({Interlocked.Read(ref _ticks)} ticks spent)";
                        result.Changes.Add(change);
                        _log?.Invoke("polish: " + change);
                        Set(cands[best].inputs.Take(check.FinishTick + 1).ToList(), check);
                        kept++;
                        return true;
                    }
                    return false;
                }
                if (!plateau)
                {
                    if (lead && from == 0) Keep(Leads(_route, 1, _radius));
                    List<Humanizer.Event> events = Humanizer.Events(_route, _route);
                    for (int i = 0; i < events.Count; i++)
                    {
                        Humanizer.Event e = events[i];
                        if (e.Tick < from || e.Tick >= to) continue;
                        if (!Keep(Candidates(_route, e, _radius))) continue;
                        events = Humanizer.Events(_route, _route);
                        i = events.FindIndex(x => x.Tick >= e.Tick) - 1;
                        if (i < -1) i = events.Count;
                    }
                    for (int i = Math.Max(1, from); i < Math.Min(to, _route.Count); i++)
                        if (_route[i].reset) Keep(Resets(_route, i));
                }
                else
                {
                    if (lead && from == 0) Keep(Leads(_route, _radius + 1, WideRadius));
                    for (int t = Math.Max(1, from); t < Math.Min(to, _route.Count); t += PlateauSlot)
                        Keep(Plateau(_route, t, Math.Min(t + PlateauSlot, Math.Min(to, _route.Count))));
                }
                result.Kept += kept;
                _log?.Invoke($"polish: {(plateau ? "plateau " : "")}sweep {sweep} kept {kept} ({tried} candidates, {finished} finished no slower), {_time:0.00}s ({_runTicks} physics ticks), "
                    + $"{Interlocked.Read(ref _ticks)} ticks");
                if (kept == 0)
                {
                    if (plateau || !Wide) break;
                    plateau = true;
                    continue;
                }
                plateau = false;
                if (_floor == null || Better(_time, _runTicks, _floor.Time, _floor.RunTicks)) Swept?.Invoke(_route, _time, _runTicks);
            }
            RunResult end = Check(_route);
            if (!end.Finished || Math.Abs(end.Time - _time) > Tie || end.PhysicsTicks != _runTicks)
                throw new InvalidOperationException($"polished route replays as {(end.Finished ? end.Time.ToString("0.00", CultureInfo.InvariantCulture) : end.EndReason)}");
            result.Inputs = _route;
            result.Time = _time;
            result.RunTicks = _runTicks;
            result.Ticks = Interlocked.Read(ref _ticks);
            return result;
        }

        long[] _goalTicks;

        void Set(List<InputTick> route, RunResult r)
        {
            _route = route;
            _time = r.Time;
            _runTicks = r.PhysicsTicks;
            _goalTicks = r.GoalPhysicsTicks != null && r.Progress > 0 ? (long[])r.GoalPhysicsTicks.Clone() : null;
            _version++;
        }

        RunResult Check(List<InputTick> route)
        {
            RunResult r = null;
            _workers[0].Do(j => r = j.Full(route));
            return r;
        }

        internal static List<(List<InputTick> inputs, int first, string what)> Candidates(List<InputTick> route, Humanizer.Event e, int radius)
        {
            var c = new List<(List<InputTick>, int, string)>();
            string name = $"{e.Kind} {e.Tick}";
            Shifts(route, e, 1, radius, c);
            switch (e.Kind)
            {
                case Humanizer.Kind.StickX:
                case Humanizer.Kind.StickY:
                    foreach (float v in Humanizer.Grid(e).Where(v => v != e.Value))
                        c.Add((Humanizer.SetStick(route, e.Kind == Humanizer.Kind.StickY, e.Tick, e.End, v), e.Tick, $"{name} value {Humanizer.Num(v)}"));
                    break;
                case Humanizer.Kind.Dash:
                    for (int j = 0; j <= 10; j++)
                    {
                        if (j == route[e.Tick].dashJump) continue;
                        var s = new List<InputTick>(route);
                        InputTick k = s[e.Tick];
                        k.dashJump = j;
                        s[e.Tick] = k;
                        c.Add((s, e.Tick, $"{name} jump frame {j}"));
                    }
                    c.Add((Humanizer.DropDash(route, e.Tick), e.Tick, $"{name} dropped"));
                    break;
                case Humanizer.Kind.Press:
                    c.Add((Humanizer.DropPress(route, e.Tick), e.Tick, $"{name} dropped"));
                    break;
            }
            return c;
        }

        // Never put a restart next to another: the gate refuses two quick restarts in a row (RULES.md 34).
        static List<(List<InputTick> inputs, int first, string what)> Resets(List<InputTick> route, int r)
        {
            var c = new List<(List<InputTick>, int, string)>();
            var dropped = new List<InputTick>(route);
            InputTick k = dropped[r]; k.reset = false; dropped[r] = k;
            c.Add((dropped, r, $"Reset {r} dropped"));
            for (int d = -3; d <= 3; d++)
            {
                int q = r + d;
                if (d == 0 || q < 1 || q + 1 >= route.Count || dropped[q - 1].reset || dropped[q + 1].reset || dropped[q].reset) continue;
                var moved = new List<InputTick>(dropped);
                InputTick m = moved[q]; m.reset = true; moved[q] = m;
                c.Add((moved, Math.Min(r, q), $"Reset {r} {(d > 0 ? "+" : "")}{d}"));
            }
            return c;
        }

        static void Shifts(List<InputTick> route, Humanizer.Event e, int lo, int hi, List<(List<InputTick>, int, string)> c)
        {
            // A stick starting at tick 0 has no earlier value to shift against (Humanizer.Shift reads the tick before).
            if (e.Start) return;
            string name = $"{e.Kind} {e.Tick}";
            for (int d = -hi; d <= hi; d++)
            {
                if (Math.Abs(d) < lo) continue;
                List<InputTick> s = Humanizer.Shift(route, e, d, out int first);
                if (s != null) c.Add((s, first, $"{name} {(d > 0 ? "+" : "")}{d}"));
                if (d > 0 && e.Tick > 0)
                {
                    InputTick before = route[e.Tick - 1];
                    var hold = new InputTick { x = before.x, y = before.y, held = before.held && !before.release };
                    var later = new List<InputTick>(route);
                    later.InsertRange(e.Tick, Enumerable.Repeat(hold, d));
                    c.Add((later, e.Tick, $"{name} and after +{d}"));
                }
                else if (d < 0 && e.Tick + d > 0)
                {
                    bool clean = true;
                    for (int j = e.Tick + d; j < e.Tick; j++) if (route[j].press || route[j].release || route[j].dash || route[j].reset) clean = false;
                    if (!clean || route[e.Tick + d - 1].held != route[e.Tick - 1].held) continue;
                    var earlier = new List<InputTick>(route);
                    earlier.RemoveRange(e.Tick + d, -d);
                    c.Add((earlier, e.Tick + d, $"{name} and after {d}"));
                }
            }
        }

        internal static List<(List<InputTick> inputs, int first, string what)> Leads(List<InputTick> route, int lo, int hi)
        {
            var c = new List<(List<InputTick>, int, string)>();
            if (route.Count == 0 || route[0].reset) return c;
            for (int d = lo; d <= hi; d++)
            {
                var later = new List<InputTick>(route);
                later.InsertRange(0, Enumerable.Repeat(Idle(route[0]), d));
                c.Add((later, 0, $"Lead +{d}"));
                if (d < route.Count && Enumerable.Range(0, d).All(j => route[j].Equals(Idle(route[d]))))
                    c.Add((route.GetRange(d, route.Count - d), 0, $"Lead -{d}"));
            }
            return c;
        }

        static InputTick Idle(InputTick next) { return new InputTick { held = next.held && !next.press }; }

        const int PlateauSlot = 8, WideRadius = 16, PairSpan = 8;
        static readonly int[] PairShifts = { -2, -1, 1, 2 }, BandTicks = { 2, 6 }, PressHolds = { 2, 5, 10, 16, 24, 40 };
        static readonly (float x, float y)[] DashAims = { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f), (1f, 1f), (-1f, 1f), (1f, -1f), (-1f, -1f) };

        List<InputTick> _eventsOf;
        List<Humanizer.Event> _events;

        List<(List<InputTick> inputs, int first, string what)> Plateau(List<InputTick> route, int from, int to)
        {
            var c = new List<(List<InputTick>, int, string)>();
            if (!ReferenceEquals(route, _eventsOf)) (_eventsOf, _events) = (route, Humanizer.Events(route, route));
            List<Humanizer.Event> events = _events;
            int lo = 0, hi = events.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (events[mid].Tick < from) lo = mid + 1; else hi = mid; }
            for (int i = lo; i < events.Count && events[i].Tick < to; i++)
            {
                Humanizer.Event e = events[i];
                if (e.Start) continue;
                Shifts(route, e, _radius + 1, WideRadius, c);
                for (int j = i + 1; j < events.Count && j <= i + 3 && events[j].Tick - e.Tick <= PairSpan; j++)
                {
                    Humanizer.Event e2 = events[j];
                    foreach (int d1 in PairShifts)
                    {
                        List<InputTick> s1 = Humanizer.Shift(route, e, d1, out int f1);
                        if (s1 == null) continue;
                        Humanizer.Event moved = Humanizer.EventAt(s1, e2.Kind, e2.Tick);
                        if (moved == null || moved.Start) continue;
                        foreach (int d2 in PairShifts)
                        {
                            List<InputTick> s2 = Humanizer.Shift(s1, moved, d2, out int f2);
                            if (s2 != null) c.Add((s2, Math.Min(f1, f2), $"{e.Kind} {e.Tick} {(d1 > 0 ? "+" : "")}{d1} with {e2.Kind} {e2.Tick} {(d2 > 0 ? "+" : "")}{d2}"));
                        }
                    }
                }
            }
            for (int t = from; t < to; t++)
            {
                InputTick k = route[t];
                if (t % 2 == 0)
                    foreach (bool y in new[] { false, true })
                        foreach (float v in new[] { -1f, 0f, 1f })
                        {
                            if ((y ? k.y : k.x) == v) continue;
                            foreach (int n in BandTicks)
                                if (t + n <= route.Count)
                                    c.Add((Humanizer.SetStick(route, y, t, t + n, v), t, $"Stick{(y ? "Y" : "X")} band {t}-{t + n - 1} value {Humanizer.Num(v)}"));
                        }
                foreach (int n in PressHolds)
                {
                    if (t + n >= route.Count) break;
                    bool free = !route[t - 1].held;
                    for (int j = t; j <= t + n && free; j++) free = !route[j].held && !route[j].press && !route[j].release && !route[j].dash && !route[j].reset;
                    if (!free) break;
                    var s = new List<InputTick>(route);
                    for (int j = t; j < t + n; j++) { InputTick h = s[j]; h.held = true; h.press = j == t; s[j] = h; }
                    InputTick r = s[t + n]; r.release = true; s[t + n] = r;
                    c.Add((s, t, $"Press added {t} held {n}"));
                }
                if (t % 2 == 0 && !k.dash && !k.ArmsLock && !k.reset)
                {
                    bool near = false;
                    for (int j = Math.Max(0, t - 3); j <= Math.Min(route.Count - 1, t + 3); j++) near |= route[j].dash || route[j].ArmsLock;
                    if (!near)
                        foreach (var (ax, ay) in DashAims)
                        {
                            var s = new List<InputTick>(route);
                            InputTick d = s[t]; d.dash = true; d.dashJump = 0; d.x = ax; d.y = ay; s[t] = d;
                            c.Add((s, t, $"Dash added {t} aim {Humanizer.Num(ax)},{Humanizer.Num(ay)}"));
                        }
                }
            }
            return c;
        }

        (float time, long runTicks, int finish)?[] Test(List<(List<InputTick> inputs, int first, string what)> cands, bool sync = true)
        {
            var outcomes = new (float, long, int)?[cands.Count];
            int next = -1, version = _version;
            List<InputTick> route = _route;
            float time = _time;
            long runTicks = _runTicks;
            long[] goals = _goalTicks;
            using var done = new CountdownEvent(_workers.Count);
            foreach (Worker<Judge> w in _workers)
                w.Post(j =>
                {
                    try
                    {
                        j.Record(route, version);
                        for (int i; (i = Interlocked.Increment(ref next)) < cands.Count;)
                            outcomes[i] = j.Try(cands[i].inputs, cands[i].first, time, runTicks, goals, sync && Sync);
                    }
                    finally { done.Signal(); }
                });
            done.Wait();
            return outcomes;
        }

        sealed class Judge
        {
            readonly Polish _owner;
            readonly Simulation _sim;
            SimSnapshot[] _states = Array.Empty<SimSnapshot>();
            List<InputTick> _recorded = new List<InputTick>();
            int[] _fp = Array.Empty<int>(), _progress = Array.Empty<int>(), _via = Array.Empty<int>();
            long[] _phys = Array.Empty<long>();
            float[] _clock = Array.Empty<float>();
            int _version = -1;
            int _waypoints;
            bool _avoided;
            int _syncFrom = int.MaxValue, _syncShift;
            long? _synced;
            float _syncedClock;

            public Judge(Polish owner)
            {
                _owner = owner;
                _sim = owner._create();
            }

            public RunResult Full(List<InputTick> route) { return Replay(route, null, float.PositiveInfinity, long.MaxValue, null, null); }

            public void Record(List<InputTick> route, int version)
            {
                if (version == _version) return;
                var states = new SimSnapshot[route.Count];
                int n = route.Count + 1;
                _fp = new int[n]; _progress = new int[n]; _via = new int[n]; _phys = Enumerable.Repeat(-1L, n).ToArray(); _clock = new float[n];
                _recorded = route;
                _syncFrom = int.MaxValue;
                Replay(route, null, float.PositiveInfinity, long.MaxValue, states, null);
                _states = states;
                _version = version;
            }

            const int SyncLookback = 12;

            public (float, long, int)? Try(List<InputTick> inputs, int first, float time, long runTicks, long[] goals, bool sync)
            {
                int s = _owner._fromStart ? 0 : Math.Min(first, _states.Length - 1);
                while (s > 0 && _states[s] == null) s--;
                int shift = inputs.Count - _recorded.Count, last = inputs.Count - 1;
                while (last >= 0 && last - shift >= 0 && last - shift < _recorded.Count && inputs[last].Equals(_recorded[last - shift])) last--;
                _syncShift = shift;
                _syncFrom = sync && last - shift >= 0 ? Math.Max(first, last) + SyncLookback + 1 : int.MaxValue;
                _synced = null;
                RunResult r = Replay(inputs, s > 0 ? _states[s] : null, time + Tie, runTicks, null, goals);
                _syncFrom = int.MaxValue;
                if (_synced is long ahead)
                {
                    long ticks = runTicks + ahead;
                    float end = time + _syncedClock;
                    return _owner.Better(end, ticks, time, runTicks) ? (end, ticks, _recorded.Count - 1 + shift) : null;
                }
                bool valid = r.Finished && _waypoints == _owner._path.Via.Count && !_avoided;
                return valid ? (r.Time, r.PhysicsTicks, r.FinishTick) : null;
            }

            RunResult Replay(List<InputTick> inputs, SimSnapshot from, float limit, long limitTicks, SimSnapshot[] states, long[] goals)
            {
                if (from != null && from.Tick >= inputs.Count) from = null;
                OfflineSolver.ForcedPath path = _owner._path;
                _waypoints = from?.Waypoints ?? 0;
                _avoided = false;
                Action<int> onStep = path.IsEmpty ? null : _ =>
                {
                    WorldPos p = _sim.PlayerWorld;
                    if (_waypoints < path.Via.Count && path.Via[_waypoints].Contains(p)) _waypoints++;
                    // Past the last goal nothing counts (RULES.md 32); a strict run plays 12 ticks on (StrictFinishTicks).
                    if (_sim.Goals != null && _sim.Goals.Done) return;
                    foreach (OfflineSolver.Box box in path.Avoid) if (box.Cuts(_sim.PlayerStepFrom, p) && box.Live(_sim.Progress)) _avoided = true;
                };
                RunResult r = _sim.Run(inputs, from, tick =>
                {
                    if (_avoided) return false;
                    if (states != null && tick < states.Length && tick % StateEvery == 0 && tick >= _owner._keepFrom && tick <= _owner._keepTo)
                    {
                        SimSnapshot state = _sim.Capture(tick);
                        state.Waypoints = _waypoints;
                        states[tick] = state;
                    }
                    if (states != null && tick < _fp.Length)
                    {
                        _fp[tick] = _sim.MovementFingerprint();
                        _phys[tick] = _sim.Engine.PhysicsTicks;
                        _progress[tick] = _sim.Progress;
                        _via[tick] = _waypoints;
                        _clock[tick] = _sim.Elapsed(_sim.TimingCourse());
                    }
                    else if (tick >= _syncFrom)
                    {
                        int u = tick - _syncShift;
                        if (u >= 0 && u < _fp.Length && _phys[u] >= 0 && _progress[u] == _sim.Progress && _via[u] == _waypoints
                            && _fp[u] == _sim.MovementFingerprint())
                        {
                            float clock = _sim.Elapsed(_sim.TimingCourse());
                            if ((clock < 0f) == (_clock[u] < 0f))
                            {
                                _synced = _sim.Engine.PhysicsTicks - _phys[u];
                                _syncedClock = clock < 0f ? 0f : clock - _clock[u];
                                return false;
                            }
                        }
                    }
                    if (goals != null && _owner._slack >= 0 && _sim.Progress < goals.Length && goals[_sim.Progress] >= 0
                        && _sim.Engine.PhysicsTicks > goals[_sim.Progress] + _owner._slack) return false;
                    if (_sim.Goals != null || _sim.TimedFromRunStart || _owner.Farm) return _sim.Engine.PhysicsTicks <= limitTicks;
                    return !(_sim.Elapsed(_sim.TimingCourse()) > limit);
                }, extraTicks: 1000000, onStep: onStep);
                Interlocked.Add(ref _owner._ticks, r.Stepped);
                return r;
            }
        }

        public static int Command(string[] args)
        {
            string trace = args[0], routePath = args[1];
            JsonElement header = Solve.ReadHeader(trace);
            int threads = Solve.Option(args, "--threads", Environment.ProcessorCount);
            List<InputTick> seed = Plan.ReadRoute(routePath);
            int course = 0, from = 0, to = int.MaxValue;
            string caps, label, fulltas = null;
            Func<Simulation> create;
            OfflineSolver.ForcedPath forced;
            if (header.TryGetProperty("fulltas", out _))
            {
                Plan.Window w = Plan.Window.Open(trace, header, args.Concat(new[] { "--route", routePath }).ToArray());
                if (args.Contains("--from-goal")) { from = w.Prefix.Count; to = w.Prefix.Count + w.OldWindow.Count; }
                create = w.Full;
                forced = new OfflineSolver.ForcedPath();
                caps = w.Caps;
                fulltas = w.Name;
                label = "fulltas-" + w.Name;
            }
            else
            {
                course = header.GetProperty("course").GetInt32();
                caps = header.GetProperty("capabilities").GetString();
                forced = Solve.ForcedPaths(args, course, caps);
                create = Solve.Creator(trace, header);
                label = course + "_" + caps;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var polish = new Polish(create, forced, threads, line => Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {line}"),
                Solve.Option(args, "--radius", Humanizer.Radius), Solve.Option(args, "--slack", DefaultSlack))
            { Wide = !args.Contains("--no-wide"), Sync = !args.Contains("--no-sync"), Farm = args.Contains("--farm"),
                Lead = fulltas == null && !args.Contains("--no-lead"), LeadTicks = Solve.Option(args, "--lead-ticks", 4) };
            string Named(float time, long runTicks) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(routePath)),
                Path.GetFileNameWithoutExtension(routePath) + "-polished-" + (polish.Farm ? "farm-" + (runTicks * .02).ToString("0.00", CultureInfo.InvariantCulture)
                    : time.ToString("0.00", CultureInfo.InvariantCulture)) + ".json");
            if (!args.Contains("--out"))
                polish.Swept = (inputs, time, runTicks) =>
                {
                    string file = Named(time, runTicks);
                    Solve.WriteShared(file, JsonSerializer.Serialize(new
                    {
                        course, capabilities = caps, fulltas, time, runTicks, source = Path.GetFileName(routePath),
                        ticks = inputs.Select(t => t.ToJson()),
                    }));
                    Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] polish: checkpoint {time:0.00}s ({runTicks} physics ticks) -> {file}");
                };
            try
            {
                Result r = polish.Run(seed, from: from, to: to);
                string outFile = Solve.Option(args, "--out", Named(r.Time, r.RunTicks));
                Solve.WriteShared(outFile, JsonSerializer.Serialize(new
                {
                    course, capabilities = caps, fulltas, time = r.Time, runTicks = r.RunTicks, source = Path.GetFileName(routePath),
                    polish = new { ticks = r.Ticks, sweeps = r.Sweeps, kept = r.Kept, changes = r.Changes },
                    ticks = r.Inputs.Select(t => t.ToJson()),
                }));
                Console.WriteLine($"polished {label}{(polish.Farm ? " (farm)" : "")}: {r.Time:0.00}s ({r.RunTicks} physics ticks, farm {r.RunTicks * .02:0.00}s) after {r.Sweeps} sweeps, {r.Kept} kept, "
                    + $"{r.Ticks} ticks, {clock.Elapsed.TotalSeconds:0.0}s -> {outFile}");
            }
            finally { polish.Stop(); }
            return 0;
        }
    }
}
