using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    // Route repair by rejoin (the old stack's transplant, FullTasTransplant.cs, deleted in 0c35faa): a route a fidelity fix broke keeps
    // its inputs. Its replay here is followed against its own path where it was found (a --ticks-out of it there) and the goal ticks
    // it recorded; at the first break only the inputs just before it change (a tick held or dropped, a press, release or dash moved a
    // tick, the stick re-aimed: one change, else a pair of the best), or, when none plays better, the goal it misses is searched from a
    // little before the break and the old inputs from where the route reached that goal are appended (-4..+4 inputs). Then the next
    // break, while it gets further. What comes out is only a starting route: the improver keeps it only as its straight replay plays it.
    sealed class Rejoin
    {
        const float PathTolerance = 6f;
        const int PathSlack = 3, PathPersist = 3, GoalSlack = 8;
        static readonly int[] Windows = { 24, 60 };
        const int Horizon = 250, Beam = 6, MaxRepairsPerGoal = 40, SpliceOffsets = 4, SearchBack = 120, MaxFinds = 24;
        // A path patch: from PatchBack inputs before the break to an `at:` box Box wide on the path PathAhead ticks past it, cut at up to
        // Cuts places it comes within PathTolerance of the path, the old inputs from each (-CutOffsets..+CutOffsets).
        const int PatchBack = 40, PathAhead = 30, Cuts = 8, CutOffsets = 2;
        const float Box = 12f;

        // Stick values a controller gives (README Rules 2): full, rest and the partial tilts 0.3-0.8; a human class's digital stick only.
        static readonly float[] Ladder = new[] { -1f }.Concat(Enumerable.Range(0, 11).Reverse().Select(i => -(float)Math.Round(.3 + .05 * i, 2)))
            .Concat(new[] { 0f }).Concat(Enumerable.Range(0, 11).Select(i => (float)Math.Round(.3 + .05 * i, 2))).Concat(new[] { 1f }).ToArray();
        static readonly float[] Digital = { -1f, 0f, 1f };

        // The route's own run where it was found: the player's world place per physics tick and the first tick each input count played
        // on (a `fulltas --check --ticks-out` row's "input").
        public sealed class Path
        {
            public float[] X, Y;
            public long[] InputAt;
            public Dictionary<long, long> FirstTick = new Dictionary<long, long>();

            // Kept through the last goal it reached on the route's recorded tick: a path taken on an engine near the route's own (the
            // old frame grid's) parts from it somewhere after that.
            public static Path Load(string file, long[] goalPhysics)
            {
                var rows = new List<(long t, long input, int goal, float x, float y)>();
                foreach (string line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using JsonDocument d = JsonDocument.Parse(line);
                    JsonElement e = d.RootElement;
                    if (e.TryGetProperty("kind", out _) || !e.TryGetProperty("x", out JsonElement x)) continue;
                    rows.Add((e.GetProperty("physics").GetInt64(), e.GetProperty("input").GetInt64(), e.TryGetProperty("goal", out JsonElement g) ? g.GetInt32() : 0, x.GetSingle(), e.GetProperty("y").GetSingle()));
                }
                return Of(rows, goalPhysics);
            }

            public static Path Of(List<(long t, long input, int goal, float x, float y)> rows, long[] goalPhysics)
            {
                long end = rows.Count == 0 ? 0 : rows[^1].t;
                if (goalPhysics != null)
                {
                    end = 0;
                    for (int i = 1; i < rows.Count; i++)
                        if (rows[i].goal > rows[i - 1].goal && rows[i].goal - 1 < goalPhysics.Length && goalPhysics[rows[i].goal - 1] == rows[i].t) end = rows[i].t;
                }
                var path = new Path { X = new float[end + 1], Y = new float[end + 1], InputAt = new long[end + 1] };
                Array.Fill(path.X, float.NaN);
                foreach (var r in rows)
                {
                    if (r.t > end) break;
                    (path.X[r.t], path.Y[r.t], path.InputAt[r.t]) = (r.x, r.y, r.input);
                    path.FirstTick.TryAdd(r.input, r.t);
                }
                return path;
            }

            // How far (x, y) is from the path at the k-th tick of the input count `input`, a few ticks either way; NaN off its ends.
            public float Off(float x, float y, long input, long k)
            {
                if (!FirstTick.TryGetValue(input, out long t0)) return float.NaN;
                float best = float.NaN;
                for (long t = t0 + k - PathSlack; t <= t0 + k + PathSlack; t++)
                {
                    if (t < 0 || t >= X.Length || float.IsNaN(X[t])) continue;
                    float dx = x - X[t], dy = y - Y[t], d = MathF.Sqrt(dx * dx + dy * dy);
                    if (!(d >= best)) best = d;
                }
                return best;
            }
        }

        // A repaired route: its inputs and, for each, the old input it is (-1: a tick put in or a searched patch's).
        sealed class Graft
        {
            public List<InputTick> Inputs;
            public List<int> Map;
            public string How;
        }

        // How a stretch played, higher is better: goals; then the last one sooner; then ticks on the old path, alive, nearer the next
        // goal, inputs lasted.
        struct Score : IComparable<Score>
        {
            public int Progress, Gained, Followed;
            public long LastTick, Lasted;
            public bool Alive;
            public float Distance;

            public int CompareTo(Score o)
            {
                if (Progress != o.Progress) return Progress.CompareTo(o.Progress);
                if (Gained > 0 && LastTick != o.LastTick) return o.LastTick.CompareTo(LastTick);
                if (Followed != o.Followed) return Followed.CompareTo(o.Followed);
                if (Alive != o.Alive) return Alive.CompareTo(o.Alive);
                float a = float.IsNaN(Distance) ? float.MaxValue : Distance, b = float.IsNaN(o.Distance) ? float.MaxValue : o.Distance;
                if (Math.Abs(a - b) > 0.5f) return b.CompareTo(a);
                return Lasted.CompareTo(o.Lasted);
            }

            public override string ToString() => $"{Progress} goals" + (Gained > 0 ? $" (last at tick {LastTick})" : "") + (Followed > 0 ? $", on the old path {Followed} ticks" : "")
                + (Alive ? ", alive" : ", dead") + (float.IsNaN(Distance) ? "" : $", {Distance:0} from the next goal") + $", {Lasted} inputs";
        }

        // With the old path only more goals, the last one sooner or more ticks on the path count: alive longer off it isn't a repair.
        bool Beats(Score a, Score b)
        {
            if (_path == null) return a.CompareTo(b) > 0;
            if (a.Progress != b.Progress) return a.Progress > b.Progress;
            if (a.Gained > 0 && a.LastTick != b.LastTick) return a.LastTick < b.LastTick;
            return a.Followed > b.Followed && (a.Alive || !b.Alive);
        }

        // Follows a replay along the old path tick by tick, lined up by input: an old input's k-th tick against the path's k-th tick of it.
        // It has left the path once it is off for PathPersist ticks in a row (a jump's or dash's position write puts it off for one).
        sealed class Follower
        {
            readonly Path _path;
            readonly List<int> _map;
            readonly int _from;
            long _v = -1, _vStart;
            int _off, _offFrom;
            public int Followed, Left = -1;

            public Follower(Path path, List<int> map, int from) { (_path, _map, _from) = (path, map, from); }

            public void Step(Simulation sim)
            {
                if (_path == null || Left >= 0) return;
                long t = sim.Engine.PhysicsTicks, v = _from + sim.Engine.RunTicks;
                if (v != _v) { _v = v; _vStart = t; }
                int i = (int)v - 1, j = i >= 0 && i < _map.Count ? _map[i] : -1;
                if (j < 0) { _off = 0; return; }
                WorldPos p = sim.PlayerWorld;
                float gap = _path.Off(p.x, p.y, j + 1, t - _vStart);
                if (gap > PathTolerance)
                {
                    if (_off++ == 0) _offFrom = i;
                    if (_off == PathPersist) Left = _offFrom;
                }
                else
                {
                    if (!float.IsNaN(gap)) Followed += 1 + _off;
                    _off = 0;
                }
            }
        }

        // Worker threads, each with a simulation of its own (the game's statics are per thread), playing stretches from a cached prefix.
        sealed class Lanes : IDisposable
        {
            readonly BlockingCollection<Action<Lane>> _jobs = new BlockingCollection<Action<Lane>>();

            public sealed class Lane
            {
                public Simulation Sim;
                public SimSnapshot Origin;
                public int Goals;
                readonly List<(ulong hash, int count, SimSnapshot state)> _cache = new List<(ulong, int, SimSnapshot)>();

                public SimSnapshot At(List<InputTick> inputs, int count)
                {
                    if (count == 0) return Origin;
                    ulong hash = SnapshotIndex.Hash(inputs, count);
                    foreach (var c in _cache) if (c.hash == hash && c.count == count) return c.state;
                    Sim.SetStart(Origin, 0);
                    Sim.Goals.Limit = Goals;
                    SimSnapshot s = Sim.Advance(inputs.GetRange(0, count));
                    _cache.Add((hash, count, s));
                    if (_cache.Count > 6) _cache.RemoveAt(0);
                    return s;
                }
            }

            public Lanes(int count, Func<Simulation> create, int goals)
            {
                for (int i = 0; i < count; i++)
                    new Thread(() =>
                    {
                        Simulation sim = create();
                        var lane = new Lane { Sim = sim, Origin = sim.Start, Goals = goals };
                        foreach (Action<Lane> job in _jobs.GetConsumingEnumerable()) job(lane);
                    }, 64 << 20) { IsBackground = true, Name = "rejoin-" + i }.Start();
            }

            public T[] Map<T>(IReadOnlyList<Func<Lane, T>> work)
            {
                var results = new T[work.Count];
                if (work.Count == 0) return results;
                using var done = new CountdownEvent(work.Count);
                for (int i = 0; i < work.Count; i++)
                {
                    int k = i;
                    _jobs.Add(lane =>
                    {
                        try { results[k] = work[k](lane); }
                        catch (Exception) { results[k] = default; }
                        finally { done.Signal(); }
                    });
                }
                done.Wait();
                return results;
            }

            public T One<T>(Func<Lane, T> work) => Map(new[] { work })[0];

            public void Dispose() => _jobs.CompleteAdding();
        }

        sealed class Diag
        {
            // Fail: the first break (off the old path too); Hard: where its goals stop coming (a goal missed, a death). Progress: the most
            // goals it reached (a death takes back the area goals after its checkpoint), GoalInput/GoalPhysics where it first reached
            // each; Reached and Last: the straight replay's goals and its last goal's tick.
            public int Progress, Fail, Hard, Reached;
            public long Last;
            public long[] GoalInput, GoalPhysics;
            public string Why, HardWhy;
        }

        readonly RunContext _run;
        readonly RunPlan _plan;
        readonly FullTasGoal[] _goals;
        readonly List<InputTick> _old;
        readonly long[] _oldInput, _oldPhysics;
        readonly Path _path;
        readonly SegmentSearch _search;
        readonly int _threads;
        readonly float[] _ladder;
        public Action<string> Log = _ => { };
        public Func<bool> Stop = () => false;
        public TimeSpan Budget = TimeSpan.FromMinutes(30);
        readonly Stopwatch _clock = new Stopwatch();
        bool TimeUp => Stop() || _clock.Elapsed > Budget;

        // oldInput: per plan goal, the input count where the route reached it (its run file's goals' "input"), -1 unknown.
        public Rejoin(RunContext run, RunPlan plan, List<InputTick> old, long[] oldInput, long[] oldPhysics, Path path, SegmentSearch search, int threads)
        {
            (_run, _plan, _old, _oldInput, _oldPhysics, _path, _search, _threads) = (run, plan, old, oldInput, oldPhysics, path, search, Math.Max(1, threads));
            _goals = run.GoalsOf(plan);
            _ladder = Glitches.Human(run.Class) ? Digital : Ladder;
        }

        // The route file's recorded goal ticks by the plan's goal ids (-1: not reached, or no such goal).
        public static (long[] input, long[] physics) Recorded(JsonElement file, RunPlan plan)
        {
            long[] input = Enumerable.Repeat(-1L, plan.Count).ToArray(), physics = Enumerable.Repeat(-1L, plan.Count).ToArray();
            if (!file.TryGetProperty("goals", out JsonElement goals)) return (input, physics);
            foreach (JsonElement g in goals.EnumerateArray())
            {
                int at = g.TryGetProperty("id", out JsonElement id) ? Array.IndexOf(plan.Ids, id.GetString()) : -1;
                if (at < 0) continue;
                if (g.TryGetProperty("input", out JsonElement i)) input[at] = i.GetInt64();
                if (g.TryGetProperty("physics", out JsonElement p)) physics[at] = p.GetInt64();
            }
            return (input, physics);
        }

        // With a prefix: another route's way to a goal, then the old inputs from oldFrom (where the route reached that goal), repaired.
        public List<InputTick> Repair(List<InputTick> prefix = null, int oldFrom = 0)
        {
            _clock.Start();
            using var lanes = new Lanes(_threads, () => { Simulation sim = _run.Make(); sim.UseGoals(sim.Goals.For(_goals)); return sim; }, _goals.Length);
            var g = prefix == null ? new Graft { Inputs = _old.ToList(), Map = Enumerable.Range(0, _old.Count).ToList(), How = "the route as it is" }
                : new Graft { Inputs = prefix.Concat(_old.Skip(oldFrom)).ToList(), Map = Enumerable.Repeat(-1, prefix.Count).Concat(Enumerable.Range(oldFrom, _old.Count - oldFrom)).ToList(),
                    How = $"another route's {prefix.Count} inputs, then the old inputs from {oldFrom}" };
            Diag d = lanes.One(l => Diagnose(l, g));
            Log($"rejoin: the route reaches {d.Progress} of {_goals.Length} goals here; breaks at input {d.Fail}: {d.Why}");
            (Graft best, Diag bestD) = (g, d);
            int repairs = 0, atGoal = d.Progress;
            var searched = new HashSet<(int, int)>();
            var patched = new HashSet<(int, ulong)>();
            // A break whose local changes (or path patch) were tried with the same inputs up to it isn't tried again.
            var tried = new HashSet<(int, ulong)>();
            Graft Local(int fail) => tried.Add((fail, SnapshotIndex.Hash(g.Inputs, Math.Min(g.Inputs.Count, fail + 3)))) ? RepairLocally(lanes, g, d, fail) : null;
            while (!TimeUp && d.Progress < _goals.Length)
            {
                Graft next = null;
                if (repairs < MaxRepairsPerGoal) next = Local(d.Fail) ?? (d.Hard != d.Fail && !TimeUp ? Local(d.Hard) : null);
                if (next == null && !TimeUp && _path != null && patched.Add((d.Fail, SnapshotIndex.Hash(g.Inputs, Math.Min(g.Inputs.Count, d.Fail + 3)))))
                    next = PathPatch(lanes, g, d);
                if (next == null && !TimeUp && searched.Add((d.Progress, d.Hard))) next = SearchPatch(lanes, g, d);
                if (next == null) break;
                Diag nd = lanes.One(l => Diagnose(l, next));
                if (nd.Progress < d.Progress) { Log($"rejoin: {next.How} replays from the start to goal {nd.Progress} only; stopped"); break; }
                repairs = nd.Progress > atGoal ? 0 : repairs + 1;
                atGoal = nd.Progress;
                (g, d) = (next, nd);
                if (d.Reached > bestD.Reached || d.Reached == bestD.Reached && d.Last < bestD.Last) (best, bestD) = (g, d);
                Log($"rejoin: now {d.Reached} of {_goals.Length} goals" + (d.Reached > 0 ? $", goal {d.Reached} at tick {d.Last}" : "") + (d.Progress > d.Reached ? $" ({d.Progress} before a death)" : "")
                    + $" ({_clock.Elapsed:hh\\:mm\\:ss}); breaks at input {d.Fail}: {d.Why}");
            }
            Log($"rejoin: done, {bestD.Reached} of {_goals.Length} goals" + (bestD.Reached > 0 ? $", the last at tick {bestD.Last}" : "") + $" in {_clock.Elapsed:hh\\:mm\\:ss}");
            return best.Inputs;
        }

        bool Died(string reason) => reason != null && FullTasTracker.Unexplained(reason) != "Solver run ended" && !reason.StartsWith("Full-TAS", StringComparison.Ordinal);

        // Where the replay from the run's start breaks: the input on which it leaves the old path, where it passes the input the route
        // reached its next goal on without reaching it, or else where it dies (or is nearest the next goal).
        Diag Diagnose(Lanes.Lane lane, Graft g)
        {
            Simulation sim = lane.Sim;
            sim.SetStart(lane.Origin, 0);
            sim.Goals.Limit = _goals.Length;
            var f = new Follower(_path, g.Map, 0);
            int progress = 0, missAt = -1, missed = -1;
            float nearest = float.NaN;
            long nearestAt = -1;
            long[] goalInput = new long[_goals.Length], goalPhysics = new long[_goals.Length];
            RunResult r = sim.Run(g.Inputs, null, null, 400, _ =>
            {
                f.Step(sim);
                for (; progress < sim.Progress; progress++) (goalInput[progress], goalPhysics[progress], nearest, nearestAt) = (sim.Engine.RunTicks, sim.Engine.PhysicsTicks, float.NaN, -1);
                float dist = OfflineSolver.GoalDistance(sim);
                if (!float.IsNaN(dist) && !(dist >= nearest)) { nearest = dist; nearestAt = sim.Engine.RunTicks; }
                int i = (int)sim.Engine.RunTicks - 1, j = i >= 0 && i < g.Map.Count ? g.Map[i] : -1;
                if (missAt < 0 && progress < _goals.Length && _oldInput[progress] > 0 && j >= _oldInput[progress] - 1 + GoalSlack)
                    (missAt, missed) = (Math.Max(0, i - (int)(j - (_oldInput[progress] - 1))), progress);
            });
            int end = (int)Math.Min(sim.Engine.RunTicks, g.Inputs.Count - 1);
            bool died = Died(r.EndReason);
            int fail = died || nearestAt < 0 ? end : (int)Math.Min(nearestAt, g.Inputs.Count - 1);
            string why = r.EndReason + (died ? "" : $" (nearest the next goal, {nearest:0} off, at input {nearestAt})");
            if (missAt >= 0 && missAt < fail) (fail, why) = (missAt, $"passes the input the route reached goal {missed + 1} {_plan.Labels[missed]} on without it");
            var diag = new Diag { Progress = progress, GoalInput = goalInput, GoalPhysics = goalPhysics, Hard = fail, HardWhy = why, Fail = fail, Why = why,
                Reached = r.Progress, Last = r.Progress > 0 ? r.GoalPhysicsTicks[r.Progress - 1] : 0 };
            if (f.Left >= 0 && f.Left < fail) (diag.Fail, diag.Why) = (f.Left, $"leaves the old path (then {why})");
            return diag;
        }

        Score Play(Lanes.Lane lane, Graft g, int from, int based, int limit, int horizon)
        {
            SimSnapshot at = lane.At(g.Inputs, from);
            if (at == null) return new Score { Progress = -1 };
            Simulation sim = lane.Sim;
            sim.SetStart(at, from);
            sim.Goals.Limit = limit;
            var f = new Follower(_path, g.Map, from);
            float nearest = float.NaN;
            int most = at.Goal?.Next ?? 0;
            long last = 0;
            RunResult r = sim.Run(g.Inputs.GetRange(from, g.Inputs.Count - from), null, tick => tick < horizon, 400, _ =>
            {
                f.Step(sim);
                if (sim.Progress > most) (most, last) = (sim.Progress, sim.Engine.PhysicsTicks);
                if (sim.Progress != based) return;
                float d = OfflineSolver.GoalDistance(sim);
                if (!float.IsNaN(d) && !(d >= nearest)) nearest = d;
            });
            sim.Goals.Limit = lane.Goals;
            return new Score
            {
                Progress = most, Gained = most - based, LastTick = last,
                Alive = !Died(r.EndReason) || most >= limit, Distance = nearest, Lasted = sim.Engine.RunTicks, Followed = f.Followed,
            };
        }

        static InputTick Plain(InputTick t) => new InputTick { x = t.x, y = t.y, held = t.held };

        static bool HasDash(InputTick t) => t.dash || t.dashJump != 0 || t.springDash != 0 || t.lockX.HasValue || t.lockY.HasValue || t.turnX.HasValue || t.turnY.HasValue;

        static InputTick WithDashOf(InputTick to, InputTick from)
        {
            (to.dash, to.dashJump, to.springDash, to.lockX, to.lockY, to.turnX, to.turnY) = (from.dash, from.dashJump, from.springDash, from.lockX, from.lockY, from.turnX, from.turnY);
            return to;
        }

        IEnumerable<float> Nudged(float v, int steps)
        {
            int at = Array.IndexOf(_ladder, (float)Math.Round(v, 2));
            if (at < 0) yield break;
            for (int d = -steps; d <= steps; d++)
                if (d != 0 && at + d >= 0 && at + d < _ladder.Length) yield return _ladder[at + d];
        }

        // The single local changes at input i: ticks held or dropped there, its press, release or dash a tick earlier or later, its stick re-aimed.
        IEnumerable<Graft> Mutations(Graft g, int i)
        {
            List<InputTick> s = g.Inputs;
            Graft Make(string how, Action<List<InputTick>, List<int>> edit)
            {
                var inputs = s.ToList();
                var map = g.Map.ToList();
                edit(inputs, map);
                return new Graft { Inputs = inputs, Map = map, How = how };
            }
            for (int n = 1; n <= 3; n++)
            {
                int k = n;
                yield return Make($"{k} tick(s) held at {i}", (a, m) => { a.InsertRange(i, Enumerable.Repeat(Plain(a[i - 1]), k)); m.InsertRange(i, Enumerable.Repeat(-1, k)); });
            }
            for (int n = 1; n <= 2; n++)
            {
                int k = n;
                if (i + k < s.Count && !Enumerable.Range(i, k).Any(x => s[x].reset))
                    yield return Make($"{k} tick(s) dropped at {i}", (a, m) => { a.RemoveRange(i, k); m.RemoveRange(i, k); });
            }
            InputTick t = s[i];
            if (t.press)
            {
                if (i > 0 && !s[i - 1].held) yield return Make($"press at {i} a tick earlier", (a, _) => { var p = a[i - 1]; p.press = true; p.held = true; p.release = false; a[i - 1] = p; var q = a[i]; q.press = false; a[i] = q; });
                if (i + 1 < s.Count) yield return Make($"press at {i} a tick later", (a, _) => { var q = a[i]; q.press = false; q.held = false; a[i] = q; var p = a[i + 1]; p.press = true; p.held = true; p.release = false; a[i + 1] = p; });
            }
            if (t.release)
            {
                if (i > 0 && s[i - 1].held && !s[i - 1].press) yield return Make($"release at {i} a tick earlier", (a, _) => { var p = a[i - 1]; p.release = true; p.held = false; a[i - 1] = p; var q = a[i]; q.release = false; a[i] = q; });
                if (i + 1 < s.Count && !s[i + 1].press) yield return Make($"release at {i} a tick later", (a, _) => { var q = a[i]; q.release = false; q.held = true; a[i] = q; var p = a[i + 1]; p.release = true; p.held = false; a[i + 1] = p; });
            }
            if (HasDash(t))
                foreach (int to in new[] { i - 1, i + 1 })
                    if (to > 0 && to < s.Count && !HasDash(s[to]))
                        yield return Make($"dash at {i} moved to {to}", (a, _) => { a[to] = WithDashOf(a[to], a[i]); a[i] = WithDashOf(a[i], new InputTick()); });
            foreach (int span in new[] { 1, 3 })
            {
                int end = i;
                while (end + 1 < s.Count && end + 1 < i + span && s[end + 1].x == t.x) end++;
                int ex = end;
                foreach (float x in Nudged(t.x, 2))
                    yield return Make($"x {t.x} -> {x} at {i}-{ex}", (a, _) => { for (int k = i; k <= ex; k++) { var p = a[k]; p.x = x; a[k] = p; } });
                end = i;
                while (end + 1 < s.Count && end + 1 < i + span && s[end + 1].y == t.y) end++;
                int ey = end;
                foreach (float y in Nudged(t.y, 1))
                    yield return Make($"y {t.y} -> {y} at {i}-{ey}", (a, _) => { for (int k = i; k <= ey; k++) { var p = a[k]; p.y = y; a[k] = p; } });
            }
        }

        // The best single change before the break, else the best pair (a beam of the best singles), that plays better than the graft.
        Graft RepairLocally(Lanes lanes, Graft g, Diag d, int fail)
        {
            int limit = Math.Min(_goals.Length, d.Progress + 2);
            foreach (int window in Windows)
            {
                if (TimeUp) return null;
                int from = Math.Max(1, fail - window), horizon = fail - from + Horizon;
                Score base0 = lanes.One(l => Play(l, g, from, d.Progress, limit, horizon));
                IEnumerable<int> At(Graft x) => Enumerable.Range(from, Math.Max(0, Math.Min(fail + 3, x.Inputs.Count - 1) - from));
                var singles = At(g).SelectMany(i => Mutations(g, i)).ToList();
                Score[] scores = lanes.Map(singles.Select(m => (Func<Lanes.Lane, Score>)(l => Play(l, m, from, d.Progress, limit, horizon))).ToList());
                int best = Enumerable.Range(0, singles.Count).OrderByDescending(k => scores[k]).FirstOrDefault(-1);
                if (best >= 0 && Beats(scores[best], base0))
                {
                    Log($"rejoin: {singles[best].How}: {scores[best]} (was {base0}; {singles.Count} changes in {window} inputs before the break at {fail})");
                    return singles[best];
                }
                if (TimeUp) return null;
                var beam = Enumerable.Range(0, singles.Count).Where(k => scores[k].Progress >= 0).OrderByDescending(k => scores[k]).Take(Beam).Select(k => singles[k]).ToList();
                var pairs = beam.SelectMany(b => At(b).SelectMany(i => Mutations(b, i)).Select(m => { m.How = b.How + "; " + m.How; return m; })).ToList();
                Score[] pairScores = lanes.Map(pairs.Select(m => (Func<Lanes.Lane, Score>)(l => Play(l, m, from, d.Progress, limit, horizon))).ToList());
                int top = Enumerable.Range(0, pairs.Count).OrderByDescending(k => pairScores[k]).FirstOrDefault(-1);
                if (top >= 0 && Beats(pairScores[top], base0))
                {
                    Log($"rejoin: {pairs[top].How}: {pairScores[top]} (was {base0}; {pairs.Count} pairs in {window} inputs before the break at {fail})");
                    return pairs[top];
                }
                Log($"rejoin: no change in {window} inputs before the break at {fail} plays better than {base0} ({singles.Count} changes, {pairs.Count} pairs)");
            }
            return null;
        }

        // A short patch from just before the break back onto the old path a little past it: the solver aims at an `at:` box on the path
        // (a goal put in before the next one), each find is cut where it comes within PathTolerance of the path, and the old inputs are
        // played on from that place's input.
        Graft PathPatch(Lanes lanes, Graft g, Diag d)
        {
            int b = d.Fail, m = Math.Min(b, g.Map.Count - 1), s = Math.Max(0, b - PatchBack);
            while (m >= 0 && g.Map[m] < 0) m--;
            // A break before the old inputs start (another route's prefix): the path from where they start.
            if (m < 0) { m = b; while (m < g.Map.Count && g.Map[m] < 0) m++; }
            if (m >= g.Map.Count || !_path.FirstTick.TryGetValue(g.Map[m] + 1, out long t0)) return null;
            if (m > b) t0--;
            // The box goes in before the first goal the graft reaches after the patch's start (the run can leave the path before a goal
            // it still reaches).
            int p = 0;
            while (p < d.Progress && d.GoalInput[p] <= s) p++;
            long end = _path.X.Length - 1;
            if (p < _goals.Length && _oldPhysics[p] > 0) end = Math.Min(end, _oldPhysics[p] - 2);
            foreach (long target in new[] { PathAhead, 3 * PathAhead }.Select(a => Math.Min(t0 + a, end)).Distinct())
            {
                if (TimeUp || target <= t0 || float.IsNaN(_path.X[target])) return null;
                CapturePos c = lanes.One(l => l.Sim.ToCapture(new WorldPos(_path.X[target], _path.Y[target])));
                var box = new FullTasGoal { label = $"at:{c.x:0.#},{c.y:0.#},{Box},{Box}", kind = FullTasGoal.At, x = c.x, y = c.y, w = Box, h = Box };
                FullTasGoal[] goals = _goals.Take(p).Append(box).Concat(_goals.Skip(p)).ToArray();
                string[] ids = _plan.Ids.Take(p).Append("rejoin").Concat(_plan.Ids.Skip(p)).ToArray();
                int seedEnd = g.Map.IndexOf((int)_path.InputAt[target], Math.Min(s, g.Map.Count));
                var seeds = seedEnd > s ? new List<List<InputTick>> { g.Inputs.GetRange(s, seedEnd - s) } : new List<List<InputTick>>();
                Log($"rejoin: from input {s} back onto the old path at tick {target} ({box.label}; {Builder.Budgets[0] / 1_000_000}M ticks)");
                // The prefix replayed on the search's own simulations: their tracker has the box as goal p.
                List<List<InputTick>> finds = _search.Find(null, g.Inputs.Take(s).ToList(), goals, ids, p, seeds, Builder.Budgets[0], out string nearest);
                GC.Collect();
                if (_search.Stopped) return null;
                if (finds.Count == 0) { Log($"rejoin: the old path at tick {target} not reached ({nearest})"); continue; }
                List<Graft> tries = PathSplices(lanes, g, s, finds, t0, end);
                if (tries.Count == 0) { Log($"rejoin: none of the {finds.Count} finds comes within {PathTolerance} of the old path"); continue; }
                int limit = Math.Min(_goals.Length, p + 2), horizon = b - s + 2 * Horizon;
                Score[] scores = lanes.Map(tries.Select(x => (Func<Lanes.Lane, Score>)(l => Play(l, x, s, p, limit, horizon))).ToList());
                Score base0 = lanes.One(l => Play(l, g, s, p, limit, horizon));
                int top = Enumerable.Range(0, tries.Count).OrderByDescending(k => scores[k]).First();
                if (!Beats(scores[top], base0)) { Log($"rejoin: back onto the old path: {scores[top]}, not better than {base0} ({tries.Count} splices)"); continue; }
                Log($"rejoin: {tries[top].How}: {scores[top]} (was {base0}; {tries.Count} splices)");
                return tries[top];
            }
            return null;
        }

        // Each find (from input s) cut at up to Cuts places it comes within PathTolerance of the old path's ticks t0+1..end, and the old
        // inputs from that place's input after it.
        List<Graft> PathSplices(Lanes lanes, Graft g, int s, List<List<InputTick>> finds, long t0, long end)
        {
            List<List<InputTick>> picked = finds.OrderBy(f => f.Count).Take(MaxFinds).Select(f => f.Concat(Enumerable.Repeat(new InputTick(), 20)).ToList()).ToList();
            List<(int k, long t)>[] places = lanes.Map(picked.Select(f => (Func<Lanes.Lane, List<(int, long)>>)(l =>
            {
                var at = new List<(int, long)>();
                Simulation sim = l.Sim;
                sim.SetStart(l.At(g.Inputs, s), s);
                sim.Goals.Limit = _goals.Length;
                sim.Run(f, null, null, 0, _ =>
                {
                    WorldPos q = sim.PlayerWorld;
                    long bestT = -1;
                    float bestD = PathTolerance;
                    for (long t = t0 + 1; t <= end; t++)
                    {
                        if (float.IsNaN(_path.X[t])) continue;
                        float dx = q.x - _path.X[t], dy = q.y - _path.Y[t], dd = MathF.Sqrt(dx * dx + dy * dy);
                        if (dd <= bestD) (bestD, bestT) = (dd, t);
                    }
                    int k = (int)sim.Engine.RunTicks;
                    if (bestT >= 0 && (at.Count == 0 || at[^1].Item1 != k)) at.Add((k, bestT));
                });
                return at;
            })).ToList());
            var tries = new List<Graft>();
            for (int n = 0; n < picked.Count; n++)
            {
                if (places[n] == null || places[n].Count == 0) continue;
                int every = Math.Max(1, places[n].Count / Cuts);
                for (int i = 0; i < places[n].Count; i += every)
                {
                    var (k, t) = places[n][i];
                    for (int dd = -CutOffsets; dd <= CutOffsets; dd++)
                    {
                        int from = (int)_path.InputAt[t] + dd;
                        if (k > picked[n].Count || from < 0 || from >= _old.Count) continue;
                        tries.Add(new Graft
                        {
                            Inputs = g.Inputs.Take(s).Concat(picked[n].Take(k)).Concat(_old.Skip(from)).ToList(),
                            Map = g.Map.Take(s).Concat(Enumerable.Repeat(-1, k)).Concat(Enumerable.Range(from, _old.Count - from)).ToList(),
                            How = $"from input {s} back onto the old path at tick {t} ({k} inputs), the old inputs from {from} after it",
                        });
                    }
                }
            }
            return tries;
        }

        // The goal the graft misses, searched from a little before the break (the old way there the seed), and the old inputs from where
        // the route reached that goal appended to each find (-4..+4 inputs): the one that plays furthest along them, if better than the graft.
        Graft SearchPatch(Lanes lanes, Graft g, Diag d)
        {
            int p = d.Progress;
            if (p >= _goals.Length || _oldInput[p] <= 0) return null;
            int oldAt = (int)_oldInput[p];
            int s = Math.Max(p == 0 ? 0 : (int)Math.Min(d.GoalInput[p - 1], g.Inputs.Count), Math.Max(0, d.Hard - SearchBack));
            int seedEnd = g.Map.IndexOf(oldAt - 1, Math.Min(s, g.Map.Count));
            var seeds = seedEnd >= s ? new List<List<InputTick>> { g.Inputs.GetRange(s, seedEnd + 1 - s) } : new List<List<InputTick>>();
            byte[] bytes = lanes.One(l =>
            {
                SimSnapshot at = l.At(g.Inputs, s);
                if (at == null) return null;
                l.Sim.Restore(at);
                try { return new SnapshotStore(l.Sim).Save(at); }
                catch (NotSupportedException) { return null; }
            });
            List<List<InputTick>> finds = new List<List<InputTick>>();
            foreach (long budget in Builder.Budgets.Take(2))
            {
                if (TimeUp) return null;
                Log($"rejoin: searching goal {p + 1} {_plan.Labels[p]} from input {s} ({budget / 1_000_000}M ticks, {seeds.Count} seeds)");
                finds = _search.Find(bytes, g.Inputs.Take(s).ToList(), _goals, _plan.Ids, p, seeds, budget, out string nearest);
                GC.Collect();
                if (_search.Stopped) return null;
                if (finds.Count > 0) break;
                Log($"rejoin: goal {p + 1} not found ({nearest})");
            }
            if (finds.Count == 0) return null;
            // Each find cut where it reaches the goal, as the builder plays it.
            List<InputTick>[] cut = lanes.Map(finds.OrderBy(f => f.Count).Take(MaxFinds).Select(f => (Func<Lanes.Lane, List<InputTick>>)(l =>
            {
                SimSnapshot at = l.At(g.Inputs, s);
                var list = f.Concat(Enumerable.Repeat(new InputTick(), 20)).ToList();
                l.Sim.SetStart(at, s);
                SimSnapshot reached = l.Sim.AdvanceToGoal(list, p + 1, out int consumed);
                return reached == null ? null : list.GetRange(0, consumed);
            })).ToList());
            var tries = new List<Graft>();
            foreach (List<InputTick> f in cut.Where(c => c != null).Distinct())
                for (int dd = -SpliceOffsets; dd <= SpliceOffsets; dd++)
                {
                    int from = oldAt + dd;
                    if (from < 0 || from >= _old.Count) continue;
                    tries.Add(new Graft
                    {
                        Inputs = g.Inputs.Take(s).Concat(f).Concat(_old.Skip(from)).ToList(),
                        Map = g.Map.Take(s).Concat(Enumerable.Repeat(-1, f.Count)).Concat(Enumerable.Range(from, _old.Count - from)).ToList(),
                        How = $"goal {p + 1} searched from input {s} ({f.Count} inputs), the old inputs from {from} after it",
                    });
                }
            // With the old path: also each find cut where it comes back onto it.
            int m = Math.Min(s, g.Map.Count) - 1;
            while (m >= 0 && g.Map[m] < 0) m--;
            if (_path != null && m >= 0 && _path.FirstTick.TryGetValue(g.Map[m] + 1, out long t0)) tries.AddRange(PathSplices(lanes, g, s, finds, t0, _path.X.Length - 1));
            if (tries.Count == 0) { Log($"rejoin: none of goal {p + 1}'s {finds.Count} finds replays from the graft"); return null; }
            int limit = Math.Min(_goals.Length, p + 3);
            int next = p + 1 < _goals.Length && _oldInput[p + 1] > oldAt ? (int)(_oldInput[p + 1] - oldAt) : 0;
            Score[] scores = lanes.Map(tries.Select(t => (Func<Lanes.Lane, Score>)(l => Play(l, t, s, p, limit, t.Inputs.Count - _old.Count + oldAt - s + next + Horizon))).ToList());
            Score base0 = lanes.One(l => Play(l, g, s, p, limit, d.Hard - s + next + Horizon));
            int best = Enumerable.Range(0, tries.Count).OrderByDescending(k => scores[k]).First();
            if (!Beats(scores[best], base0) || scores[best].Progress <= p) { Log($"rejoin: goal {p + 1}'s finds, the old inputs after: {scores[best]}, not better than {base0}"); return null; }
            Log($"rejoin: {tries[best].How}: {scores[best]} (was {base0}; {tries.Count} splices)");
            return tries[best];
        }
    }
}
