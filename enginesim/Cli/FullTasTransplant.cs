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
    // --transplant FILE (docs/fulltas.md, "Transplant"): an old route of this run (another engine's best, which dies on this one)
    // played again from the run's start and repaired where it fails, goal by goal: its inputs are kept and only the ticks just
    // before the failure are shifted, stretched or re-aimed within the controller's stick values. A 1x probe of the old route
    // (probes/<name>.json, its "ticks" and per-tick "rows") also gives where the player was on every tick then: the failure is
    // then the first tick the new run leaves that path, and a repair has to follow it further. Where no such repair reaches the
    // next goal, the old inputs are picked up again from the new route's or the best's state at a goal (any offset into the old
    // route). What it reaches faster than the best is handed to the solve's own merge and repair, the repaired route's goals are
    // filed as library pieces and every one of its goals seeds the search windows.
    sealed partial class FullTas
    {
        string _transplantFile;
        Reference _reference;
        /// <summary>
        /// Each goal where a transplant graft got there sooner than the best: the soonest tick and the graft's inputs up to it. A graft
        /// that later falls back on the best's state (Reanchor) or stops loses that lead; <see cref="Bridge"/> searches on from it.
        /// </summary>
        readonly Dictionary<int, (long tick, List<InputTick> inputs)> _leads = new Dictionary<int, (long, List<InputTick>)>();

        void NoteLeads(Graft g, Route r)
        {
            for (int p = 1; p <= Math.Min(r.Progress, _best.Progress); p++)
            {
                long t = r.GoalPhysics[p - 1];
                if (t >= _best.GoalPhysics[p - 1] || _leads.TryGetValue(p, out var known) && known.tick <= t) continue;
                _leads[p] = (t, g.Inputs.Take((int)Math.Min(r.GoalInput[p - 1], g.Inputs.Count)).ToList());
            }
        }

        /// <summary>
        /// From each goal where a graft was ahead of the best (the biggest lead first, while it still is ahead of the current best),
        /// the goals after it re-found by the solve's own repair windows from the graft's state (<see cref="Repair"/>, seeded with the
        /// best's inputs): the transplant's way through Course 1 and 2 (loose buys) or up to goal 12 kept, the rest searched.
        /// </summary>
        void Bridge()
        {
            var tried = new HashSet<int>();
            while (!TimeUp)
            {
                var lead = _leads.Where(l => !tried.Contains(l.Key) && l.Key < _goals && l.Key <= _best.Progress && l.Value.tick < _best.GoalPhysics[l.Key - 1])
                    .OrderByDescending(l => _best.GoalPhysics[l.Key - 1] - l.Value.tick).FirstOrDefault();
                if (lead.Key == 0) return;
                tried.Add(lead.Key);
                int p = lead.Key;
                Route run = Evaluate(lead.Value.inputs, p);
                if (run.Progress < p) { Log($"transplant bridge from goal {p}: its inputs no longer reach it"); continue; }
                Log($"transplant bridge from goal {p} ({_labels[p - 1]}): tick {run.GoalPhysics[p - 1]} against the best's {_best.GoalPhysics[p - 1]}; re-finding goals {p + 1}-{_goals}");
                Repair($"the transplant's way to goal {p}", _best, new Route { Inputs = run.Inputs, Progress = p, GoalInput = run.GoalInput, GoalPhysics = run.GoalPhysics, EndReason = run.EndReason });
            }
        }

        /// <summary>
        /// The old route's own run, from a 1x probe of it: the player's world position on each of the probe's physics ticks and the
        /// first tick each input played on. A run follows it while it stays within <see cref="PathTolerance"/> units of the probe's
        /// position a few ticks either way (<see cref="PathSlack"/>), its ticks lined up by the graft's <see cref="Graft.Shift"/>. It
        /// has left it only once it is off for <see cref="PathPersist"/> ticks in a row: a jump's and a dash's position writes (the
        /// +5 and the -20, pulled back by the frame's lag with interpolation on) put it 5-40 units off for a single tick and back.
        /// </summary>
        sealed class Reference
        {
            public float[] X, Y, VX, VY;
            public bool[] Right;
            public int[] FirstTick, InputAt;

            /// <summary>
            /// The old input played on the probe's tick nearest this state: position, then velocity (Velocity + momentum, 1/50 as much) and
            /// facing (20 units): where the old route stood as this state does, to pick its inputs up from there.
            /// </summary>
            public int NearestInput(WorldPos p, UnityEngine.Vector2 v, bool right, out float miss)
            {
                int best = -1;
                miss = float.MaxValue;
                for (int k = 0; k < X.Length; k++)
                {
                    if (InputAt[k] < 0) continue;
                    float dx = p.x - X[k], dy = p.y - Y[k], dvx = v.x - VX[k], dvy = v.y - VY[k];
                    float d = (float)Math.Sqrt(dx * dx + dy * dy) + .02f * (float)Math.Sqrt(dvx * dvx + dvy * dvy) + (right == Right[k] ? 0f : 20f);
                    if (d < miss) { miss = d; best = k; }
                }
                return best < 0 ? -1 : InputAt[best];
            }

            /// <summary>How far (x, y) is from the path around the probe's tick, NaN off its ends.</summary>
            public float Off(float x, float y, long tick)
            {
                float best = float.NaN;
                for (long k = tick - PathSlack; k <= tick + PathSlack; k++)
                {
                    if (k < 0 || k >= X.Length) continue;
                    float dx = x - X[k], dy = y - Y[k], d = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (!(d >= best)) best = d;
                }
                return best;
            }

            public static Reference Read(JsonElement rows)
            {
                var ticks = rows.EnumerateArray().Where(r => r.GetProperty("kind").GetString() == "tick").ToList();
                int count = ticks.Max(r => r.GetProperty("physics").GetInt32()) + 1, inputs = ticks.Max(r => r.GetProperty("input").GetInt32()) + 1;
                var reference = new Reference { X = new float[count], Y = new float[count], VX = new float[count], VY = new float[count], Right = new bool[count],
                    FirstTick = Enumerable.Repeat(-1, inputs).ToArray(), InputAt = Enumerable.Repeat(-1, count).ToArray() };
                float Get(JsonElement r, string name) => r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;
                foreach (JsonElement r in ticks)
                {
                    int t = r.GetProperty("physics").GetInt32(), input = r.GetProperty("input").GetInt32() - 1;
                    reference.X[t] = r.GetProperty("x").GetSingle();
                    reference.Y[t] = r.GetProperty("y").GetSingle();
                    reference.VX[t] = Get(r, "vx") + Get(r, "mx");
                    reference.VY[t] = Get(r, "vy") + Get(r, "my");
                    reference.Right[t] = r.TryGetProperty("facingRight", out JsonElement right) && right.ValueKind == JsonValueKind.True;
                    reference.InputAt[t] = input;
                    if (input >= 0 && reference.FirstTick[input] < 0) reference.FirstTick[input] = t;
                }
                return reference;
            }
        }
        const float PathTolerance = 6f;
        const int PathSlack = 3, PathPersist = 3;

        /// <summary>A transplanted route: its inputs and, for each, the old route's input it came from (-1: added by a repair or an anchor).</summary>
        sealed class Graft
        {
            public List<InputTick> Inputs;
            public List<int> From;
            public string How;
            /// <summary>This run's physics tick minus the probe's for the same moment of the old route (with a <see cref="Reference"/>).</summary>
            public int Shift;
            /// <summary>The first input the old path applies from (0; after an anchor, the anchor's end: the inputs before it are another route's).</summary>
            public int PathFrom;
        }

        /// <summary>
        /// How a stretch from a state played, higher is better: goals reached; then the last one's physics tick, sooner; then (no goal
        /// more than the base) how many ticks it followed the old route's path, alive at the horizon, nearest the next area goal, and
        /// how many inputs it lasted.
        /// </summary>
        struct Score : IComparable<Score>
        {
            public int Progress;
            public long LastTick;
            public bool Alive;
            public float Distance;
            public long Lasted;
            public int Gained;
            public int Followed, Shift;
            /// <summary>The input (from the stretch's start) on which it left the old route's path, -1: it didn't.</summary>
            public long Left;

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

            public override string ToString()
            {
                return $"{Progress} goals" + (Gained > 0 ? $" (last at tick {LastTick})" : "") + (Followed > 0 ? $", on the old path {Followed} ticks" : "") + (Alive ? ", alive" : ", dead")
                    + (float.IsNaN(Distance) ? "" : $", {Distance:0} from the next goal") + $", {Lasted} inputs";
            }
        }

        /// <summary>
        /// Whether a stretch played better than the base from the same state. With the old path, only more goals, the last one sooner,
        /// or more ticks on the path count: staying alive longer off it (a few idle ticks) isn't a repair.
        /// </summary>
        bool Beats(Score a, Score b)
        {
            if (_reference == null) return a.CompareTo(b) > 0;
            if (a.Progress != b.Progress) return a.Progress > b.Progress;
            if (a.Gained > 0 && a.LastTick != b.LastTick) return a.LastTick < b.LastTick;
            return a.Followed > b.Followed;
        }

        /// <summary>Worker threads, each with a simulation of its own (the game's statics are per thread), that play stretches from a cached prefix state.</summary>
        sealed class Lanes : IDisposable
        {
            readonly BlockingCollection<Action<Lane>> _jobs = new BlockingCollection<Action<Lane>>();
            readonly Thread[] _threads;

            public sealed class Lane
            {
                public Simulation Sim;
                public SimSnapshot Origin;
                readonly List<(ulong hash, int count, SimSnapshot state)> _cache = new List<(ulong, int, SimSnapshot)>();
                public int Goals;

                /// <summary>The state after the first count inputs (null: the run ends before them).</summary>
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
                _threads = new Thread[count];
                for (int i = 0; i < count; i++)
                {
                    _threads[i] = new Thread(() =>
                    {
                        Simulation sim = create();
                        var lane = new Lane { Sim = sim, Origin = sim.Start, Goals = goals };
                        foreach (Action<Lane> job in _jobs.GetConsumingEnumerable()) job(lane);
                    }, 64 << 20) { IsBackground = true, Name = "transplant-" + i };
                    _threads[i].Start();
                }
            }

            /// <summary>Runs every job on the lanes and returns their results in order.</summary>
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

            public void Dispose()
            {
                _jobs.CompleteAdding();
                foreach (Thread t in _threads) t.Join(10000);
            }
        }

        // Stick values a controller gives (README Rules 2): full, rest, and the partial tilts 0.3-0.8 (Humanize's Partials).
        static readonly float[] StickLadder = new[] { -1f }.Concat(Enumerable.Range(0, 11).Reverse().Select(i => -(float)Math.Round(.3 + .05 * i, 2)))
            .Concat(new[] { 0f }).Concat(Enumerable.Range(0, 11).Select(i => (float)Math.Round(.3 + .05 * i, 2))).Concat(new[] { 1f }).ToArray();
        // A repair's window before the failure (inputs), how far past it a candidate is played, and the beam's width.
        static readonly int[] RepairWindows = { 24, 60 };
        const int RepairHorizon = 250, RepairBeam = 6, MaxRepairsPerGoal = 40, AnchorBack = 40, AnchorAhead = 240, AnchorAheadNext = 420;

        /// <summary>
        /// Plays inputs[from..] from the state after inputs[0..from] on a lane, up to goal limit or horizon inputs past from; with a
        /// reference, counts the ticks it follows the old path. anchorOld (&gt;= 0) is the old input the stretch starts with, which
        /// lines the path up by itself (the shift is returned); else shift does.
        /// </summary>
        static Score Play(Lanes.Lane lane, List<InputTick> inputs, int from, int based, int limit, int horizon, Reference reference = null, int shift = 0, int anchorOld = -1)
        {
            SimSnapshot at = lane.At(inputs, from);
            if (at == null) return new Score { Progress = -1 };
            Simulation sim = lane.Sim;
            sim.SetStart(at, from);
            sim.Goals.Limit = limit;
            float nearest = float.NaN;
            List<InputTick> rest = inputs.GetRange(from, inputs.Count - from);
            int followed = 0, off = 0;
            long left = -1, offFrom = -1;
            bool first = true;
            RunResult r = sim.Run(rest, null, tick => tick < horizon, 400, _ =>
            {
                if (reference != null && left < 0)
                {
                    long t = sim.Engine.PhysicsTicks;
                    if (first && anchorOld >= 0 && anchorOld < reference.FirstTick.Length && reference.FirstTick[anchorOld] >= 0) shift = (int)(t - reference.FirstTick[anchorOld]);
                    first = false;
                    WorldPos p = sim.PlayerWorld;
                    float gap = reference.Off(p.x, p.y, t - shift);
                    if (gap > PathTolerance)
                    {
                        if (off++ == 0) offFrom = sim.Engine.RunTicks;
                        if (off == PathPersist) left = offFrom;
                    }
                    else
                    {
                        if (!float.IsNaN(gap)) followed += 1 + off;
                        off = 0;
                    }
                }
                if (sim.Progress != based) return;
                float d = OfflineSolver.GoalDistance(sim);
                if (!float.IsNaN(d) && !(d >= nearest)) nearest = d;
            });
            sim.Goals.Limit = lane.Goals;
            bool alive = r.EndReason == null || r.EndReason == "Solver run ended" || r.Progress >= limit || r.EndReason.StartsWith("Full-TAS", StringComparison.Ordinal);
            return new Score
            {
                Progress = r.Progress,
                Gained = r.Progress - based,
                LastTick = r.Progress > 0 ? r.GoalPhysicsTicks[r.Progress - 1] : 0,
                Alive = alive,
                Distance = nearest,
                Lasted = sim.Engine.RunTicks,
                Followed = followed,
                Left = left,
                Shift = shift,
            };
        }

        /// <summary>
        /// Where the whole graft fails: with a reference, the input on which it leaves the old path; else the input it ends on when it
        /// dies or is stopped, else the one nearest the next area goal.
        /// </summary>
        (Route route, int fail, string why) Diagnose(Graft g)
        {
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            int progress = 0;
            float nearest = float.NaN;
            long nearestAt = -1, left = -1, leftTick = -1, offFrom = -1, offTick = -1;
            int off = 0;
            RunResult r = _eval.Run(g.Inputs, null, null, 400, _ =>
            {
                if (_reference != null && left < 0 && _eval.Engine.RunTicks >= g.PathFrom)
                {
                    WorldPos p = _eval.PlayerWorld;
                    long t = _eval.Engine.PhysicsTicks;
                    if (_reference.Off(p.x, p.y, t - g.Shift) > PathTolerance)
                    {
                        if (off++ == 0) { offFrom = _eval.Engine.RunTicks; offTick = t; }
                        if (off == PathPersist) { left = offFrom; leftTick = offTick; }
                    }
                    else off = 0;
                }
                if (_eval.Progress != progress) { progress = _eval.Progress; nearest = float.NaN; nearestAt = -1; }
                float d = OfflineSolver.GoalDistance(_eval);
                if (!float.IsNaN(d) && !(d >= nearest)) { nearest = d; nearestAt = _eval.Engine.RunTicks; }
            });
            var route = new Route { Inputs = g.Inputs, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks, EndReason = r.EndReason };
            int end = (int)Math.Min(_eval.Engine.RunTicks, g.Inputs.Count - 1);
            bool died = r.EndReason != null && r.EndReason != "Solver run ended" && !r.EndReason.StartsWith("Full-TAS", StringComparison.Ordinal);
            int fail = died || nearestAt < 0 ? end : (int)Math.Min(nearestAt, g.Inputs.Count - 1);
            if (left >= 0 && left < fail)
                return (route, (int)left, $"leaves the old route's path on physics tick {leftTick} (then {r.EndReason})");
            return (route, fail, r.EndReason + (died ? "" : $" (nearest the next goal, {nearest:0} off, at input {nearestAt})"));
        }

        static InputTick Plain(InputTick t) { return new InputTick { x = t.x, y = t.y, held = t.held }; }

        static bool HasDash(InputTick t) { return t.dash || t.dashJump != 0 || t.springDash != 0 || t.lockX.HasValue || t.lockY.HasValue || t.turnX.HasValue || t.turnY.HasValue; }

        static InputTick WithDashOf(InputTick to, InputTick from)
        {
            to.dash = from.dash; to.dashJump = from.dashJump; to.springDash = from.springDash;
            to.lockX = from.lockX; to.lockY = from.lockY; to.turnX = from.turnX; to.turnY = from.turnY;
            return to;
        }

        static IEnumerable<float> Nudged(float v, int steps)
        {
            int at = Array.IndexOf(StickLadder, (float)Math.Round(v, 2));
            if (at < 0) yield break;
            for (int d = -steps; d <= steps; d++)
                if (d != 0 && at + d >= 0 && at + d < StickLadder.Length) yield return StickLadder[at + d];
        }

        /// <summary>The single local changes at input i: shift the inputs after it (insert or drop ticks), move its press, release or dash a tick, re-aim its stick.</summary>
        static IEnumerable<Graft> Mutations(Graft g, int i)
        {
            List<InputTick> s = g.Inputs;
            Graft Make(string how, Action<List<InputTick>, List<int>> edit)
            {
                var inputs = s.ToList();
                var from = g.From.ToList();
                edit(inputs, from);
                return new Graft { Inputs = inputs, From = from, How = how, Shift = g.Shift, PathFrom = g.PathFrom };
            }
            for (int d = 1; d <= 3; d++)
            {
                int n = d;
                yield return Make($"{n} tick(s) held at {i}", (a, f) => { a.InsertRange(i, Enumerable.Repeat(Plain(a[i - 1]), n)); f.InsertRange(i, Enumerable.Repeat(-1, n)); });
            }
            for (int d = 1; d <= 2; d++)
            {
                int n = d;
                if (i + n < s.Count && !Enumerable.Range(i, n).Any(k => s[k].reset))
                    yield return Make($"{n} tick(s) dropped at {i}", (a, f) => { a.RemoveRange(i, n); f.RemoveRange(i, n); });
            }
            InputTick t = s[i];
            if (t.press)
            {
                if (i > 0 && !s[i - 1].held) yield return Make($"press at {i} a tick earlier", (a, f) => { var p = a[i - 1]; p.press = true; p.held = true; p.release = false; a[i - 1] = p; var q = a[i]; q.press = false; a[i] = q; });
                if (i + 1 < s.Count) yield return Make($"press at {i} a tick later", (a, f) => { var q = a[i]; q.press = false; q.held = false; a[i] = q; var p = a[i + 1]; p.press = true; p.held = true; p.release = false; a[i + 1] = p; });
            }
            if (t.release)
            {
                if (i > 0 && s[i - 1].held && !s[i - 1].press) yield return Make($"release at {i} a tick earlier", (a, f) => { var p = a[i - 1]; p.release = true; p.held = false; a[i - 1] = p; var q = a[i]; q.release = false; a[i] = q; });
                if (i + 1 < s.Count && !s[i + 1].press) yield return Make($"release at {i} a tick later", (a, f) => { var q = a[i]; q.release = false; q.held = true; a[i] = q; var p = a[i + 1]; p.release = true; p.held = false; a[i + 1] = p; });
            }
            if (HasDash(t))
                foreach (int to in new[] { i - 1, i + 1 })
                    if (to > 0 && to < s.Count && !HasDash(s[to]))
                        yield return Make($"dash at {i} moved to {to}", (a, f) => { a[to] = WithDashOf(a[to], a[i]); a[i] = WithDashOf(a[i], new InputTick()); });
            foreach (int span in new[] { 1, 3 })
            {
                int end = i;
                while (end + 1 < s.Count && end + 1 < i + span && s[end + 1].x == t.x) end++;
                foreach (float x in Nudged(t.x, 2))
                    yield return Make($"x {t.x} -> {x} at {i}-{end}", (a, f) => { for (int k = i; k <= end; k++) { var p = a[k]; p.x = x; a[k] = p; } });
                end = i;
                while (end + 1 < s.Count && end + 1 < i + span && s[end + 1].y == t.y) end++;
                foreach (float y in Nudged(t.y, 1))
                    yield return Make($"y {t.y} -> {y} at {i}-{end}", (a, f) => { for (int k = i; k <= end; k++) { var p = a[k]; p.y = y; a[k] = p; } });
            }
        }

        /// <summary>A local repair before the failure: the best single change, else the best pair of changes (a beam of the best singles), that plays better than the graft does.</summary>
        Graft RepairLocally(Lanes lanes, Graft g, Route r, int fail)
        {
            int limit = Math.Min(_goals, r.Progress + 2);
            foreach (int window in RepairWindows)
            {
                int from = Math.Max(1, fail - window);
                int horizon = fail - from + RepairHorizon;
                Score base0 = lanes.Map(new Func<Lanes.Lane, Score>[] { l => Play(l, g.Inputs, from, r.Progress, limit, horizon, _reference, g.Shift) })[0];
                var singles = Enumerable.Range(from, Math.Max(0, Math.Min(fail + 3, g.Inputs.Count - 1) - from)).SelectMany(i => Mutations(g, i)).ToList();
                Score[] scores = lanes.Map(singles.Select(m => (Func<Lanes.Lane, Score>)(l => Play(l, m.Inputs, from, r.Progress, limit, horizon, _reference, g.Shift))).ToList());
                int best = Enumerable.Range(0, singles.Count).OrderByDescending(k => scores[k]).FirstOrDefault(-1);
                if (best >= 0 && Beats(scores[best], base0))
                {
                    Log($"transplant: {singles[best].How}: {scores[best]} (was {base0}; {singles.Count} changes tried in {window} inputs before the failure at {fail})");
                    return singles[best];
                }
                var beam = Enumerable.Range(0, singles.Count).Where(k => scores[k].Progress >= 0).OrderByDescending(k => scores[k]).Take(RepairBeam).Select(k => singles[k]).ToList();
                var pairs = beam.SelectMany(b => Enumerable.Range(from, Math.Max(0, Math.Min(fail + 3, b.Inputs.Count - 1) - from)).SelectMany(i => Mutations(b, i))
                    .Select(m => new Graft { Inputs = m.Inputs, From = m.From, How = b.How + "; " + m.How, Shift = g.Shift, PathFrom = g.PathFrom })).ToList();
                Score[] pairScores = lanes.Map(pairs.Select(m => (Func<Lanes.Lane, Score>)(l => Play(l, m.Inputs, from, r.Progress, limit, horizon, _reference, g.Shift))).ToList());
                int top = Enumerable.Range(0, pairs.Count).OrderByDescending(k => pairScores[k]).FirstOrDefault(-1);
                if (top >= 0 && Beats(pairScores[top], base0))
                {
                    Log($"transplant: {pairs[top].How}: {pairScores[top]} (was {base0}; {pairs.Count} pairs tried in {window} inputs before the failure at {fail})");
                    return pairs[top];
                }
                Log($"transplant: no change in {window} inputs before the failure at {fail} plays better than {base0} ({singles.Count} changes, {pairs.Count} pairs)");
            }
            return null;
        }

        /// <summary>The old route's input the graft's input i came from (the nearest one before it that has one), -1 when none.</summary>
        static int OldIndex(Graft g, int i)
        {
            for (int k = Math.Min(i, g.From.Count - 1); k >= 0; k--) if (g.From[k] >= 0) return g.From[k] + (i - k);
            return -1;
        }

        /// <summary>
        /// The old inputs picked up again at any offset (from AnchorBack before where the graft stands in them to AnchorAhead after) from
        /// the graft's own state at its last goal, the best's state at that goal, and the best's at the goal after it: the one that gets
        /// furthest soonest, if it gets further than the graft does.
        /// </summary>
        Graft Reanchor(Lanes lanes, List<InputTick> old, Graft g, Route r)
        {
            int p = r.Progress;
            if (p == 0) return null;
            int at = (int)Math.Min(r.GoalInput[p - 1], g.Inputs.Count);
            int j0 = OldIndex(g, at);
            if (j0 < 0) return null;
            var anchors = new List<(string how, List<InputTick> prefix, int reached, int lo, int hi)>
            {
                ($"the transplant's state at goal {p}", g.Inputs.GetRange(0, at), p, j0 - AnchorBack, j0 + AnchorAhead),
            };
            if (_best.Progress >= p)
                anchors.Add(($"the best's state at goal {p}", _best.Inputs.GetRange(0, (int)Math.Min(_best.GoalInput[p - 1], _best.Inputs.Count)), p, j0 - AnchorBack, j0 + AnchorAhead));
            if (_best.Progress > p)
                anchors.Add(($"the best's state at goal {p + 1}", _best.Inputs.GetRange(0, (int)Math.Min(_best.GoalInput[p], _best.Inputs.Count)), p + 1, j0, j0 + AnchorAheadNext));
            var tries = new List<Graft>();
            var jobs = new List<Func<Lanes.Lane, Score>>();
            foreach (var a in anchors)
                for (int j = Math.Max(0, a.lo); j < Math.Min(old.Count - 1, a.hi); j++)
                {
                    var inputs = a.prefix.Concat(old.Skip(j)).ToList();
                    var from = Enumerable.Repeat(-1, a.prefix.Count).Concat(Enumerable.Range(j, old.Count - j)).ToList();
                    int length = a.prefix.Count, reached = a.reached;
                    tries.Add(new Graft { Inputs = inputs, From = from, How = $"old inputs from {j} after {a.how}", PathFrom = a.prefix.Count });
                    int oldAt = j;
                    jobs.Add(l => Play(l, inputs, length, reached, Math.Min(_goals, reached + 2), RepairHorizon + 150, _reference, 0, oldAt));
                }
            Score[] scores = lanes.Map(jobs);
            // Each anchor has to gain a goal of its own: the best's state at goal p + 1 is past goal p already.
            int best = Enumerable.Range(0, tries.Count).Where(k => scores[k].Progress > p && scores[k].Gained > 0).OrderByDescending(k => scores[k]).FirstOrDefault(-1);
            if (best < 0)
            {
                Log($"transplant: none of {tries.Count} offsets into the old route gets past goal {p} from {string.Join(", ", anchors.Select(a => a.how))}");
                return null;
            }
            Log($"transplant: {tries[best].How}: {scores[best]} ({tries.Count} offsets tried)");
            tries[best].Shift = scores[best].Shift;
            return tries[best];
        }

        static readonly System.Reflection.FieldInfo VelocityOf = typeof(Movement).GetField("Velocity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            MomentumOf = typeof(Movement).GetField("momentum", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        // Splice points per goal: the best's inputs whose state is nearest the old path, how near they must be, and the offsets tried at each.
        const int SplicePoints = 8, SpliceOffsets = 4;
        const float SpliceMiss = 40f;

        /// <summary>
        /// For each goal k, the old inputs played from the best's states on its way to goal k (from goal k-1 on): with the probe's path,
        /// from the SplicePoints inputs where the best stands nearest where the old route stood (position, velocity, facing), picking the
        /// old inputs up there (a few offsets either way); without it, from the best's state at goal k-1 at offsets around where the graft
        /// was then. The fastest that reach goal k sooner than the best go to Merge (it keeps one if the whole run gets faster, else re-finds
        /// the goals after it); the old inputs from the best's state at goal k-1 also seed the solve's windows from there.
        /// </summary>
        void OfferSegments(Lanes lanes, List<InputTick> old, Graft g, Route chain)
        {
            // Goal by goal, the one whose splices gain the most first: a small gain early in the run means repairing every goal after it.
            var done = new HashSet<int>();
            for (bool first = true; !TimeUp; first = false)
            {
                var gains = Enumerable.Range(1, Math.Min(_best.Progress, _goals)).Where(k => !done.Contains(k))
                    .Select(k => (k, gain: Splices(lanes, old, g, chain, k, false, first))).ToList();
                var top = gains.Where(x => x.gain > 0).OrderByDescending(x => x.gain).FirstOrDefault();
                if (top.k == 0) break;
                done.Add(top.k);
                Splices(lanes, old, g, chain, top.k, true, false);
            }
        }

        /// <summary>
        /// The splices of the old route into the best's way to goal k (see <see cref="OfferSegments"/>): how many ticks the soonest
        /// gains there (0: none sooner); with apply, the soonest is followed along the old path and handed to Merge.
        /// </summary>
        long Splices(Lanes lanes, List<InputTick> old, Graft g, Route chain, int k, bool apply, bool seed)
        {
            {
                Route best = _best;
                int prefixLength = k == 1 ? 0 : (int)Math.Min(best.GoalInput[k - 2], best.Inputs.Count);
                int end = (int)Math.Min(best.GoalInput[k - 1], best.Inputs.Count);
                var tries = new List<List<InputTick>>();
                var starts = new List<int>();
                var olds = new List<int>();
                void Add(int at, int j)
                {
                    if (j < 0 || j >= old.Count - 1) return;
                    tries.Add(best.Inputs.Take(at).Concat(old.Skip(j)).ToList());
                    starts.Add(at);
                    olds.Add(j);
                }
                if (_reference != null)
                {
                    // The best's state after each of its inputs on the way to goal k.
                    var states = new Dictionary<long, (WorldPos p, UnityEngine.Vector2 v, bool right)>();
                    _eval.SetStart(_origin, 0);
                    _eval.Goals.Limit = _goals;
                    _eval.Run(best.Inputs, null, tick => tick <= end, 0, _ =>
                    {
                        long t = _eval.Engine.RunTicks;
                        if (t >= prefixLength && t <= end)
                            states[t] = (_eval.PlayerWorld, (UnityEngine.Vector2)VelocityOf.GetValue(_eval.Player) + (UnityEngine.Vector2)MomentumOf.GetValue(_eval.Player), _eval.Player.facingRight);
                    });
                    var near = states.Select(s => (at: (int)s.Key, j: _reference.NearestInput(s.Value.p, s.Value.v, s.Value.right, out float miss), miss))
                        .Where(s => s.j >= 0 && s.miss <= SpliceMiss).OrderBy(s => s.miss).Take(SplicePoints).ToList();
                    foreach (var s in near)
                        for (int d = -SpliceOffsets; d <= SpliceOffsets; d++) Add(s.at, s.j + 1 + d);
                    if (states.TryGetValue(prefixLength, out var there))
                    {
                        int j = _reference.NearestInput(there.p, there.v, there.right, out float miss);
                        if (seed && j >= 0 && miss <= SpliceMiss)
                            _seeds.Add(new Seed { Name = $"old route from input {j + 1} (goal {k - 1}, {miss:0} off)", Goals = _labels.Skip(k - 1).ToArray(), Inputs = old.Skip(j + 1).ToList() });
                    }
                }
                else
                {
                    int j0 = k == 1 ? 0 : chain.Progress >= k - 1 ? OldIndex(g, (int)Math.Min(chain.GoalInput[k - 2], g.Inputs.Count)) : -1;
                    if (j0 < 0) return 0;
                    for (int j = Math.Max(0, j0 - AnchorBack); j < j0 + AnchorBack; j++) Add(prefixLength, j);
                }
                if (tries.Count == 0) return 0;
                Score[] scores = lanes.Map(tries.Select((t, i) => (Func<Lanes.Lane, Score>)(l => Play(l, t, starts[i], k - 1, Math.Min(_goals, k + 1), RepairHorizon * 3, _reference, 0, olds[i]))).ToList());
                var faster = Enumerable.Range(0, tries.Count).Where(i => scores[i].Progress >= k && scores[i].LastTick < best.GoalPhysics[k - 1]).ToList();
                long bestAt = faster.Count == 0 ? -1 : faster.Min(i => scores[i].LastTick);
                Log($"transplant: goal {k} ({_labels[k - 1]}): {tries.Count} splices of the old route into the best's way there, {scores.Count(x => x.Progress >= k)} reach it, "
                    + (faster.Count == 0 ? "none sooner than the best's tick " + best.GoalPhysics[k - 1] : $"{faster.Count} sooner, the soonest at tick {bestAt} against the best's {best.GoalPhysics[k - 1]}"));
                if (faster.Count == 0) return 0;
                if (!apply) return best.GoalPhysics[k - 1] - bestAt;
                // Merge takes the last found first: soonest last. Its windows start at goal k-1 (the splice point can be later).
                var found = faster.OrderByDescending(i => scores[i].LastTick).TakeLast(5).Select(i => tries[i].Skip(prefixLength).ToList()).ToList();
                int limit = k;
                if (_reference != null)
                {
                    // The soonest splice follows the old path on as far as local repairs take it (the old route's own way through the
                    // goals after k): merged at the last goal it is still ahead of the best at, the goals after it are searched again.
                    int first = faster.OrderBy(i => scores[i].LastTick).First();
                    var graft = new Graft { Inputs = tries[first], From = Enumerable.Repeat(-1, starts[first]).Concat(Enumerable.Range(olds[first], old.Count - olds[first])).ToList(),
                        How = $"old inputs from {olds[first]} after the best's input {starts[first]}", Shift = scores[first].Shift, PathFrom = starts[first] };
                    (Route r0, int fail, string why) = Diagnose(graft);
                    Log($"transplant: goal {k}: following the old path from {graft.How}: {r0.Progress} goals; fails at input {fail}: {why}");
                    (Graft followed, Route chain2) = Follow(lanes, old, graft, r0, fail, false);
                    int ahead = Enumerable.Range(k, Math.Max(0, Math.Min(chain2.Progress, best.Progress) - k + 1)).Where(m => chain2.GoalPhysics[m - 1] < best.GoalPhysics[m - 1]).DefaultIfEmpty(k).Max();
                    Log($"transplant: goal {k}: the followed old path reaches goal {chain2.Progress}, ahead of the best through goal {ahead} (tick {chain2.GoalPhysics[ahead - 1]} against {best.GoalPhysics[ahead - 1]})");
                    if (ahead > k)
                    {
                        limit = ahead;
                        found = new List<List<InputTick>> { followed.Inputs.Skip(prefixLength).ToList() };
                    }
                }
                Merge($"goals {k}-{limit} from the old route (transplant)", best, prefixLength, limit, found);
                return best.GoalPhysics[k - 1] - bestAt;
            }
        }

        /// <summary>The shift (this run's physics tick minus the probe's) that lines the old route's first 120 ticks up with the probe's path best.</summary>
        int Align(List<InputTick> old)
        {
            if (_reference == null) return 0;
            var at = new List<(long tick, WorldPos p)>();
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            _eval.Run(old, null, tick => tick < 120, 0, _ => at.Add((_eval.Engine.PhysicsTicks, _eval.PlayerWorld)));
            int best = 0;
            double bestError = double.MaxValue;
            for (int shift = -6; shift <= 6; shift++)
            {
                double error = 0;
                foreach (var (tick, p) in at)
                {
                    long k = tick - shift;
                    if (k < 0 || k >= _reference.X.Length) { error += 1e6; continue; }
                    error += Math.Abs(p.x - _reference.X[k]) + Math.Abs(p.y - _reference.Y[k]);
                }
                if (error < bestError) { bestError = error; best = shift; }
            }
            var errors = at.Select(a => (a.tick, off: _reference.Off(a.p.x, a.p.y, a.tick - best), a.p)).ToList();
            var worst = errors.Where(e => !float.IsNaN(e.off)).OrderByDescending(e => e.off).FirstOrDefault();
            Log($"transplant: path lined up over {at.Count} ticks, worst off {worst.off:0.00} on tick {worst.tick} at ({worst.p.x:0.0}, {worst.p.y:0.0}); first ticks "
                + string.Join(" ", errors.Take(8).Select(e => $"{e.tick}:({e.p.x:0.0},{e.p.y:0.0})/{e.off:0.00}")));
            return best;
        }

        /// <summary>
        /// Repairs the graft where it fails, over and over, while it gets further (a local repair; with reanchor, the old inputs picked
        /// up again when none helps); returns the furthest graft and its replay from the run's start.
        /// </summary>
        (Graft, Route) Follow(Lanes lanes, List<InputTick> old, Graft g, Route r, int fail, bool reanchor)
        {
            int repairs = 0, atGoal = r.Progress;
            NoteLeads(g, r);
            while (!TimeUp && r.Progress < _goals)
            {
                Graft next = repairs < MaxRepairsPerGoal ? RepairLocally(lanes, g, r, fail) : null;
                if (next == null && reanchor) next = Reanchor(lanes, old, g, r);
                if (next == null) break;
                (Route nr, int nf, string nwhy) = Diagnose(next);
                // Accept only what replays at least as far as the graft did from the start (a lane's cached state is the same run).
                if (nr.Progress < r.Progress) { Log($"transplant: {next.How} replays from the start to goal {nr.Progress} only; stopped"); break; }
                g = next;
                repairs = nr.Progress > atGoal ? 0 : repairs + 1;
                atGoal = nr.Progress;
                r = nr;
                fail = nf;
                NoteLeads(g, r);
                Log($"transplant: now {r.Progress} of {_goals} goals" + (r.Progress > 0 ? $", goal {r.Progress} at tick {r.GoalPhysics[r.Progress - 1]}" + (_best.Progress >= r.Progress ? $" (best {_best.GoalPhysics[r.Progress - 1]})" : "") : "")
                    + $"; fails at input {fail}: {nwhy}");
            }
            return (g, r);
        }

        /// <summary>Runs the transplant of the old route in file, then hands what it found to the solve (see the class comment).</summary>
        void Transplant(string file)
        {
            List<InputTick> old;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 256 }))
            {
                JsonElement root = doc.RootElement;
                old = root.TryGetProperty("ticks", out JsonElement ticks) ? ticks.EnumerateArray().Select(InputTick.FromJson).ToList() : Solve.ReadInputs(root);
                if (root.TryGetProperty("rows", out JsonElement rows)) _reference = Reference.Read(rows);
            }
            var g = new Graft { Inputs = old.ToList(), From = Enumerable.Range(0, old.Count).ToList(), How = "as it is", Shift = Align(old) };
            if (_reference != null) Log($"transplant: the probe's path has {_reference.X.Length} physics ticks; this run's ticks are the probe's {(g.Shift >= 0 ? "+" : "")}{g.Shift}");
            using var lanes = new Lanes(Math.Max(1, _threads), _create, _goals);
            (Route r, int fail, string why) = Diagnose(g);
            Log($"transplant {Path.GetFileName(file)}: {old.Count} inputs, {r.Progress} of {_goals} goals as it is; fails at input {fail}: {why}");
            (g, r) = Follow(lanes, old, g, r, fail, true);
            // Each lead, as a run file beside the best (fulltas/<run>.lead-<goal>.json): a look at the way there, or a judge of it.
            foreach (var (p, lead) in _leads)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(_runFile), $"{_name}.lead-{p}.json"),
                    JsonSerializer.Serialize(new { name = _name, goals = _labels, inputs = lead.inputs.Select(t => t.ToJson()), leadGoal = p, leadTick = lead.tick }));
            string saved = Path.Combine(Path.GetDirectoryName(_runFile), _name + ".transplant.json");
            File.WriteAllText(saved, JsonSerializer.Serialize(new
            {
                name = _name, goals = _labels, inputs = g.Inputs.Select(t => t.ToJson()), goalInputTicks = r.GoalInput, goalPhysicsTicks = r.GoalPhysics,
                total = r.Progress == _goals ? r.Last : -1, from = g.From,
            }));
            Log($"transplant: {r.Progress} of {_goals} goals" + (r.Progress == _goals ? $", {Describe(r)}" : "") + $" -> fulltas/{Path.GetFileName(saved)}; per goal (transplant / best): "
                + string.Join(" ", Enumerable.Range(0, r.Progress).Select(k => $"{k + 1}:{r.GoalPhysics[k]}/{(_best.Progress > k ? _best.GoalPhysics[k].ToString(CultureInfo.InvariantCulture) : "-")}")));
            if (r.Progress > 0) HarvestRoute(saved);
            // Every goal it reaches seeds the windows from there on (docs/fulltas.md, "Run seeds").
            for (int k = 0; k < r.Progress; k++)
            {
                int start = k == 0 ? 0 : (int)Math.Min(r.GoalInput[k - 1], g.Inputs.Count);
                _seeds.Add(new Seed { Name = $"transplant from goal {k}", Goals = _labels.Skip(k).ToArray(), Inputs = g.Inputs.Skip(start).ToList() });
            }
            if (r.Progress == _goals && Better(r, _best)) Commit(r, "transplanted from " + Path.GetFileName(file));
            else
            {
                Bridge();
                OfferSegments(lanes, old, g, r);
            }
        }
    }
}
