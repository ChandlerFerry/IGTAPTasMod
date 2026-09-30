using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using Caps = IGTAPTasMod.Caps;

namespace IGTAP.EngineSim.Cli
{
    // --parts goals, record-break ladders (docs/fulltas.md "Parts" item 5, Payout breaks): K laps of a course back to back, each
    // breaking the record the one before set. The course's fastest lap from the quick restart, T ticks on the course timer, is
    // played T+K-1, ..., T+1, then T: every finish replaces the clone path and pays each clone in flight at once
    // (clonesScript.updateClonePath). On build 25630570, while the course's spawn timer keeps Start's reserve, the slots refill one
    // a rendered frame after a break, so the pool runs in step and the next break, a lap later, pays about C(1 - o/T') more
    // payouts (o: the restart's overhead); once it lost it, a break only brings Cash forward. K laps cost K(K-1)/2 ticks over K
    // laps at T.
    sealed partial class FullTas
    {
        /// <summary>A ladder has at most LadderKmax laps; a hold-back slows an earlier lap by at most MaxHoldBack ticks; a pull plays at
        /// most MaxLadderEdits ladders.</summary>
        const int LadderKmax = 8, MaxHoldBack = 24, MaxLadderEdits = 64;

        /// <summary>A lap exactly some ticks slower on the course timer: its inputs (from where it was played through its finish goal's
        /// input), its course time, its finish (physics tick), its gate touch (lap input) and the lap inputs its held ticks go in
        /// before.</summary>
        sealed class SlowLap
        {
            public List<InputTick> Lap;
            public long Time, Tick;
            public int Gate;
            public List<int> At;
        }

        /// <summary>Slowed laps by (the inputs that lead to the state it is played from, the lap, k); null: none is exactly k slower.</summary>
        readonly ConcurrentDictionary<ulong, SlowLap> _slowLaps = new ConcurrentDictionary<ulong, SlowLap>();

        /// <summary>
        /// <paramref name="lap"/> played from <paramref name="at"/> (input <paramref name="a"/>, reached by the inputs hashed as
        /// <paramref name="prefix"/>; <paramref name="before"/> the input before it) to goal <paramref name="finishGoal"/> (its gate
        /// the goal before), exactly <paramref name="k"/> ticks slower on the course timer, legal as played: held ticks that hold the
        /// input before them one tick longer (<see cref="InputTick.Coast"/>: no new press, a held jump stays held) after the gate
        /// touch. All k at one point, of every point the one exactly +k that finishes soonest; when no point gives it, a smaller
        /// exact one with the rest held at a second point (so on: k points at most). Null when none is.
        /// </summary>
        SlowLap SlowOne(Simulation sim, SimSnapshot at, int a, ulong prefix, InputTick before, List<InputTick> lap, int finishGoal, int k)
        {
            ulong key = Mix(SnapshotIndex.Hash(new List<InputTick> { before }.Concat(lap).ToList(), lap.Count + 1, prefix), (ulong)k);
            if (_slowLaps.TryGetValue(key, out SlowLap have)) return have;
            SlowLap Play(List<InputTick> v, List<int> points)
            {
                sim.SetStart(at, a);
                sim.Goals.Limit = finishGoal;
                RunResult rr = sim.Run(v, null, null, 400);
                if (rr.Progress < finishGoal) return null;
                int end = (int)rr.GoalInputTicks[finishGoal - 1] - a;
                var cut = new List<InputTick>(v);
                // The finish can come in the idle ticks the run plays after the inputs (a dash carries it over the line).
                while (cut.Count < end) cut.Add(new InputTick());
                return new SlowLap { Lap = cut.GetRange(0, end), Time = PathTicks(sim.LastCourseTime), Tick = rr.GoalPhysicsTicks[finishGoal - 1],
                    Gate = Math.Max(0, (int)rr.GoalInputTicks[finishGoal - 2] - a), At = points };
            }
            SlowLap r = Play(lap, new List<int>());
            if (r != null && k > 0)
            {
                long want = r.Time + k;
                // n held ticks at one point of `from` (itself exactly want - n).
                SlowLap Exact(SlowLap from, int n)
                {
                    SlowLap pick = null;
                    for (int p = from.Gate; p < from.Lap.Count; p++)
                    {
                        var v = new List<InputTick>(from.Lap.Count + n);
                        v.AddRange(from.Lap.Take(p));
                        v.AddRange(Enumerable.Repeat((p == 0 ? before : from.Lap[p - 1]).Coast(), n));
                        v.AddRange(from.Lap.Skip(p));
                        if (Play(v, from.At.Append(p).ToList()) is SlowLap s && s.Time == want && (pick == null || s.Tick < pick.Tick)) pick = s;
                    }
                    return pick;
                }
                SlowLap one = Exact(r, k);
                for (int j = k - 1; j >= 1 && one == null; j--)
                    if (SlowOne(sim, at, a, prefix, before, lap, finishGoal, j) is SlowLap m) one = Exact(m, k - j);
                r = one;
            }
            _slowLaps[key] = r;
            return r;
        }

        /// <summary>Where a ladder goes: in place of a run of back-to-back laps (<see cref="Block"/>: a whole run, any K, or its last
        /// <see cref="OnlyK"/> laps, K = those), or put in before old goal
        /// <see cref="First"/> (0-based) at input A: right after an unlock buy, as inserted record laps go, or at the last quick
        /// restart before one (the best's restart there, which then puts the player back where the best's walk to the box starts).
        /// Its first lap's quick restart (<see cref="Restart"/>) is at input A. <see cref="Clones"/>: the course's clones there, and
        /// whether its spawn timer still holds Start's reserve (<see cref="Reserve"/>). <see cref="Ladders"/>: K and the K laps, slowest
        /// first, with the earlier laps held back for it and the run they are held back in (null: none).</summary>
        sealed class LadderSpot
        {
            public int Course, A, First, OnlyK, Clones;
            public bool Reserve;
            public InputTick Restart;
            public List<RunLap> Block;
            public long Before, T = -1;
            public string How, Why;
            public readonly List<(int k, List<SlowLap> rungs, List<(Finish j, SlowLap v)> hold, Route basis)> Ladders = new List<(int, List<SlowLap>, List<(Finish, SlowLap)>, Route)>();
        }

        /// <summary>A course's spawn timer at or past this still holds Start's 10000 reserve (clonesScript.Start): after a break the
        /// slots refill one a rendered frame, so the next break a lap later pays nearly every clone again. The first off-screen tick
        /// resets it to 0 (SpawnClone), and nothing in a run gives it back: a break then only brings Cash forward (about half the
        /// clones' pay at once, repaid over one path).</summary>
        const float ReserveKept = 1000f;

        /// <summary>The run with a ladder's earlier laps held back (<see cref="Retune"/> after each), by the best and the held laps.</summary>
        readonly ConcurrentDictionary<ulong, Route> _heldBack = new ConcurrentDictionary<ulong, Route>();

        /// <summary>The run's goals with a ladder of k laps at the spot (the block's goals out).</summary>
        string[] LadderLabels(LadderSpot s, int k)
        {
            int skip = s.Block == null ? s.First : s.Block[s.Block.Count - 1].K + 2;
            return _labels.Take(s.First).Concat(Enumerable.Range(0, 2 * k).Select(i => i % 2 == 0 ? $"start:{s.Course}" : $"finish:{s.Course}")).Concat(_labels.Skip(skip)).ToArray();
        }

        /// <summary>
        /// Ladders: at every run of back-to-back laps of a course (replaced by the ladder: fewer laps drop, more add; or its last K
        /// laps, the laps before them kept), right after
        /// every unlock buy for each course the run laps after it, and at the last quick restart before every unlock buy, K = 2 ..
        /// LadderKmax laps from the quick restart, the first T+K-1 ticks on the course timer, each one tick faster, the last T: T is
        /// the fastest lap from the restart at the spot (<see cref="LapWays"/> on the lap's own time: saved routes, the best's own
        /// laps). Each lap is built where the one before leaves the player (a lap from a finish's restart can play otherwise than
        /// from the spot: Course 3's dash comes back later): the fastest lap there, slowed to its exact time (<see cref="SlowOne"/>).
        /// The first must beat the course's best time there: when T+K-1 doesn't, the earlier laps that set it (back to the last
        /// reset, as payout breaks walk it) are held back to exactly T+K. The waits from the ladder's first quick restart on are
        /// re-timed to their Cash once it plays through (<see cref="GoalEdit.Retune"/>: laps in place of the wait before it); one
        /// that doesn't play through spliced is re-found as an investment when it may pay (<see cref="PayoutMayPay"/>, K best
        /// times). The held-back laps are played first, the waits after each re-timed (<see cref="Retune"/>), and the ladder spliced
        /// into that run (<see cref="GoalEdit.Base"/>): a lap held back far before the ladder (Course 1's record lap at goals 22-23 for
        /// a ladder at the end of Course 1) changes the Cash of every buy between. With <paramref name="gate"/> only where the course
        /// has clones, and where its spawn timer has lost the reserve (<see cref="ReserveKept"/>) only right before a Cash-bound buy
        /// (the model's spare under its gap), where Cash brought forward is time. <paramref name="course"/> and
        /// <paramref name="onlyK"/> narrow it (--ladder-study); <paramref name="spots"/> gets every spot looked at.
        /// </summary>
        List<GoalEdit> LadderEdits(Route best, List<RunLap> laps, int course = 0, int onlyK = 0, List<LadderSpot> spots = null, bool gate = true)
        {
            var edits = new List<GoalEdit>();
            LoadCourseRoutes();
            spots ??= new List<LadderSpot>();
            foreach (List<RunLap> block in Blocks(laps))
                if ((course == 0 || block[0].Course == course) && block[block.Count - 1].K + 2 <= best.Progress)
                    // The whole run of laps, and its last K laps (the laps before them kept).
                    foreach (int m in Enumerable.Range(0, block.Count - 1).Where(m => m == 0 || m >= block.Count - LadderKmax).DefaultIfEmpty(0))
                    {
                        List<RunLap> tail = block.Skip(m).ToList();
                        spots.Add(new LadderSpot { Course = tail[0].Course, A = tail[0].A, First = tail[0].K, Restart = best.Inputs[tail[0].A], Block = tail, OnlyK = m == 0 ? 0 : tail.Count });
                    }
            int[] unlocks = Enumerable.Range(1, Math.Min(_goals - 2, best.Progress - 1)).Where(k => _runGoals.Goals[k].kind == FullTasGoal.Buy && !FullTasGoal.IsEconomy(_labels[k])).ToArray();
            foreach (int k in unlocks)
            {
                int r = (int)Math.Min(best.GoalInput[k], best.Inputs.Count) - 1;
                while (r > best.GoalInput[k - 1] && !best.Inputs[r].reset) r--;
                foreach (int c in laps.Select(l => l.Course).Distinct().Where(c => course == 0 || c == course))
                {
                    if (laps.Any(l => l.K > k && l.Course == c))
                        spots.Add(new LadderSpot { Course = c, A = (int)Math.Min(best.GoalInput[k], best.Inputs.Count), First = k + 1, Restart = new InputTick { reset = true } });
                    if (best.Inputs[r].reset) spots.Add(new LadderSpot { Course = c, A = r, First = k, Restart = best.Inputs[r] });
                }
            }
            if (spots.Count == 0) return edits;
            Dictionary<int, List<(string name, List<InputTick> inputs)>> own = spots.Select(s => s.Course).Distinct().ToDictionary(c => c, c => OwnLaps(best, c));
            List<Finish> finishes = Finishes(best);
            // The earlier laps of the course since the last reset of its best time, latest first (as payout breaks walk them).
            List<Finish> Earlier(LadderSpot s)
            {
                var chain = new List<Finish>();
                long cur = s.Before;
                foreach (Finish j in finishes.Where(x => x.Course == s.Course && x.K < s.First).Reverse())
                {
                    if (cur > j.After) break;
                    chain.Add(j);
                    cur = j.Before;
                }
                return chain;
            }
            ulong start = SnapshotIndex.Hash(best.Inputs, 0), bestKey = SnapshotIndex.Hash(best.Inputs, best.Inputs.Count);
            RunOnThreads(spots.Count, (sim, origin, tracker, i) =>
            {
                LadderSpot s = spots[i];
                Caps has = default;
                List<Finish> earlier = null;
                var gates = new Dictionary<int, SimSnapshot>();
                int raised = 0;
                for (int k = 2; k <= LadderKmax && s.Why == null; k++)
                {
                    if (s.OnlyK > 0 && k != s.OnlyK) continue;
                    string[] labels = LadderLabels(s, k);
                    sim.UseGoals(tracker.For(GoalsOf(labels)));
                    sim.SetStart(origin, 0);
                    sim.Goals.Limit = labels.Length;
                    SimSnapshot at = sim.Advance(best.Inputs.GetRange(0, s.A));
                    if (at == null) { s.Why = $"the best ends before input {s.A}"; break; }
                    var tried = new List<string>();
                    if (s.T < 0)
                    {
                        sim.Restore(at);
                        has = PlayerCaps.Of(sim.Player);
                        s.Before = PathTicks((float)BestPathTime.GetValue(sim.Courses.First(c => c.courseNumber == s.Course)));
                        object[] clones = sim.Shop.Economy?.CloneStates().FirstOrDefault(x => (int)x[0] == s.Course);
                        s.Clones = clones == null ? 0 : (int)clones[1];
                        s.Reserve = clones != null && (float)clones[5] >= ReserveKept;
                        var ways = LapWays(sim, at, s.A, s.Restart, s.First + 2, s.Course, has, own[s.Course], float.MaxValue, tried, true);
                        if (ways == null) { s.Why = $"no lap plays from the restart ({has.Label}): " + string.Join("; ", tried); break; }
                        s.T = PathTicks(ways[0].path);
                        s.How = ways[0].how.Split(", best time")[0];
                        earlier = Earlier(s);
                    }
                    // The laps, slowest first, each the fastest lap from where the one before leaves the player, slowed to its time.
                    var rungs = new List<SlowLap>();
                    SimSnapshot state = at;
                    int a = s.A;
                    ulong prefix = SnapshotIndex.Hash(best.Inputs, s.A, start);
                    InputTick restart = s.Restart, before = s.A > 0 ? best.Inputs[s.A - 1] : new InputTick();
                    bool raise = false;
                    for (int r = 0; r < k; r++)
                    {
                        int fin = s.First + 2 * r + 2;
                        long target = s.T + k - 1 - r;
                        var ways = LapWays(sim, state, a, restart, fin, s.Course, has, own[s.Course], float.MaxValue, tried, true);
                        long time = ways == null ? long.MaxValue : PathTicks(ways[0].path);
                        // A lap from a finish's restart slower than its rung (Course 3's 190-tick lap needs the dash the restart after a
                        // finish hasn't given back: 194): the ladder starts again on a T that every lap makes, a few times at most.
                        if (ways != null && time > target && raised < 3)
                        {
                            s.T += time - target;
                            raised++;
                            raise = true;
                            break;
                        }
                        SlowLap v = time > target ? null : SlowOne(sim, state, a, prefix, before, ways[0].lap, fin, (int)(target - time));
                        if (v == null)
                        {
                            s.Why = $"{k} laps: no lap {r + 1} at {target} ticks (" + (ways == null ? "no lap plays there" : $"the fastest there is {time}") + ")";
                            break;
                        }
                        rungs.Add(v);
                        sim.SetStart(state, a);
                        sim.Goals.Limit = labels.Length;
                        state = sim.Advance(v.Lap);
                        if (state == null) { s.Why = $"{k} laps: lap {r + 1} doesn't replay"; break; }
                        prefix = SnapshotIndex.Hash(v.Lap, v.Lap.Count, prefix);
                        a += v.Lap.Count;
                        before = v.Lap[v.Lap.Count - 1];
                        restart = new InputTick { reset = true };
                    }
                    if (raise) { k--; continue; }
                    if (rungs.Count < k) break;
                    // The first lap must break the best time there: the earlier laps that hold it at T+K-1 or less go back to T+K.
                    var hold = new List<(Finish, SlowLap)>();
                    if (s.Before < s.T + k)
                    {
                        if (earlier.Count == 0 || earlier[earlier.Count - 1].Before < s.T + k) { s.Why = $"{k} laps: the best time there comes from before the laps it could hold back"; break; }
                        foreach (Finish j in earlier.Where(j => j.Time < s.T + k))
                        {
                            int gate = (int)Math.Min(best.GoalInput[j.K], best.Inputs.Count), end = (int)Math.Min(best.GoalInput[j.K + 1], best.Inputs.Count);
                            if (!gates.TryGetValue(j.K, out SimSnapshot g))
                            {
                                sim.SetStart(origin, 0);
                                sim.Goals.Limit = labels.Length;
                                gates[j.K] = g = sim.Advance(best.Inputs.GetRange(0, gate));
                            }
                            SlowLap v = g == null || s.T + k - j.Time > MaxHoldBack ? null
                                : SlowOne(sim, g, gate, SnapshotIndex.Hash(best.Inputs, gate, start), best.Inputs[gate - 1], best.Inputs.GetRange(gate, end - gate), j.K + 2, (int)(s.T + k - j.Time));
                            if (v == null) { hold = null; break; }
                            hold.Add((j, v));
                        }
                        if (hold == null) { s.Why = $"{k} laps: an earlier lap can't be held back to {s.T + k}"; break; }
                    }
                    // The run with them held back, each followed by the waits after it re-timed (a best short of the end: up to where it stops).
                    Route basis = null;
                    if (hold.Count > 0)
                    {
                        ulong key = hold.Aggregate(bestKey, (h, x) => Mix(Mix(h, (ulong)x.Item1.K), SnapshotIndex.Hash(x.Item2.Lap, x.Item2.Lap.Count)));
                        if (!_heldBack.TryGetValue(key, out basis))
                        {
                            sim.UseGoals(tracker.For(_runGoals.Goals));
                            basis = best;
                            foreach (var (j, v) in hold.OrderBy(h => h.Item1.K))
                            {
                                var inputs = basis.Inputs.Take((int)basis.GoalInput[j.K]).Concat(v.Lap).Concat(basis.Inputs.Skip((int)Math.Min(basis.GoalInput[j.K + 1], basis.Inputs.Count))).ToList();
                                basis = Retune(sim, origin, basis, inputs, j.K + 2, best.Progress);
                                if (basis == null || basis.Progress < best.Progress) { basis = null; break; }
                            }
                            _heldBack[key] = basis;
                        }
                        // One that loses a goal (a stray buy) is spliced with the ladder into the best instead, its goals fitted as it plays (StrayFit).
                    }
                    if (onlyK == 0 || k == onlyK) s.Ladders.Add((k, rungs, hold, basis));
                }
            });
            List<GoalRow> rows = _model?.Count == _goals ? _model : null;
            bool CashBound(int g) => rows != null && g < _goals && _runGoals.Goals[g].kind == FullTasGoal.Buy && rows[g].Spare < rows[g].Gap;
            var made = new List<(GoalEdit e, LadderSpot s)>();
            foreach (LadderSpot s in spots)
                foreach (var (k, rungs, hold, basis) in s.Ladders)
                {
                    if (gate && (s.Clones == 0 || !s.Reserve && !CashBound(s.Block == null ? s.First : s.Block[s.Block.Count - 1].K + 2))) continue;
                    long t = rungs[k - 1].Time;
                    Route b = basis ?? best;
                    var ops = basis != null ? new List<(int, int, List<InputTick>)>()
                        : hold.Select(h => ((int)best.GoalInput[h.j.K], (int)Math.Min(best.GoalInput[h.j.K + 1], best.Inputs.Count), h.v.Lap)).ToList();
                    // The spot's quick restart in that run: right after the buy, or the last one before goal First.
                    int g0 = s.First == 0 ? 0 : (int)Math.Min(b.GoalInput[s.First - 1], b.Inputs.Count), a = g0;
                    if (s.Block != null || s.A != (int)Math.Min(best.GoalInput[s.First - 1], best.Inputs.Count))
                        for (a = (int)Math.Min(b.GoalInput[s.First], b.Inputs.Count) - 1; a > g0 && !b.Inputs[a].reset; a--) { }
                    int end = s.Block == null ? a : (int)Math.Min(b.GoalInput[s.Block[s.Block.Count - 1].K + 1], b.Inputs.Count);
                    ops.Add((a, end, rungs.SelectMany(r => r.Lap).ToList()));
                    IEnumerable<int> cut = s.Block == null ? Enumerable.Empty<int>() : s.Block.SelectMany(l => new[] { l.K, l.K + 1 });
                    string text = $"Ladder: {k} Course {s.Course} laps {Where(s, true)}, {t + k - 1}..{t} ticks on the course timer against its {s.Before} ({s.How})"
                        + (hold.Count == 0 ? "" : $"; held back {Plural(hold.Count, "lap")} to {t + k} (goals {string.Join(", ", hold.Select(h => $"{h.j.K + 1}-{h.j.K + 2}"))})");
                    GoalEdit e = Spliced(b, ops, cut, "ladder", $"ladder:{s.Course}:{s.First}:{s.A}:{k}", text,
                        hold.Count == 0 ? s.First + 1 : hold.Min(h => h.j.K) + 1, s.First + 2 * k);
                    // The ladder's 2K goals go where the block's were (or before old goal First); the waits from its first quick restart on
                    // are re-timed.
                    e.OldOf = e.OldOf.Take(s.First).Concat(Enumerable.Repeat(-1, 2 * k)).Concat(e.OldOf.Skip(s.First)).ToArray();
                    e.Labels = LadderLabels(s, k);
                    e.Retune = s.First;
                    e.Base = basis;
                    _payoutReach[e.Key] = k * (t + k);
                    made.Add((e, s));
                }
            // ponytail: the most clones first (the reserve kept before a lost one), capped; rank by the modelled Cash per break if the
            // cap cuts good ones.
            return made.OrderByDescending(x => x.s.Reserve).ThenByDescending(x => x.s.Clones).ThenBy(x => x.e.Labels.Length).Take(MaxLadderEdits).Select(x => x.e).ToList();
        }

        string Where(LadderSpot s, bool edit)
        {
            if (s.Block != null) return edit ? $"in place of the {Plural(s.Block.Count, "lap")} at {Goals(s.Block)}" : $"{Plural(s.Block.Count, "lap")} at {Goals(s.Block)}";
            return s.A == (int)Math.Min(_best.GoalInput[s.First - 1], _best.Inputs.Count) ? $"after goal {s.First} ({_labels[s.First - 1]})"
                : $"at the quick restart before goal {s.First + 1} ({_labels[s.First]})";
        }

        /// <summary>
        /// --ladder-study C --k K: the ladders of Course C (every spot, K laps, or every K with 0: each lap's course time, finish and where its held ticks
        /// go, the laps held back), each played as a goals pull plays it (the wait fix, then the waits re-timed), with the course time
        /// and what each break paid in the whole run (Cash the tick its finish came, less the lap's reward), against the best's own
        /// laps there. Then stops.
        /// </summary>
        void LadderStudy(int course, int k)
        {
            Route best = _best;
            // A best short of the end (an engine change broke it) is studied up to where it stops: its goals after that never come.
            if (best.Progress < _goals)
            {
                int n = best.Progress;
                long[] Out(long[] a) => a == null ? null : Enumerable.Range(0, _goals).Select(g => g < n && g < a.Length ? a[g] : long.MaxValue).ToArray();
                best = new Route { Inputs = best.Inputs, Progress = best.Progress, GoalInput = Out(best.GoalInput), GoalPhysics = Out(best.GoalPhysics), GoalHeld = Out(best.GoalHeld), EndReason = best.EndReason };
            }
            var spots = new List<LadderSpot>();
            double c0 = CpuSeconds();
            List<GoalEdit> edits = LadderEdits(best, Laps(best), course, k, spots, false);
            Log($"ladder study: Course {course}, {(k == 0 ? $"2-{LadderKmax}" : k.ToString(CultureInfo.InvariantCulture))} laps: {spots.Count} spots, {edits.Count} ladders in {CpuSeconds() - c0:0} CPU s; best {Describe(best)}");
            foreach (LadderSpot s in spots)
            {
                Log($"ladder study: {Where(s, false)}, restart at input {s.A}, best time there {s.Before} ticks, {s.Clones} clones" + (s.Clones == 0 ? "" : s.Reserve ? " (reserve kept)" : " (reserve lost)")
                    + (s.T < 0 ? "" : $", fastest lap {s.T} ({s.How})") + (s.Why == null ? "" : $"; stops: {s.Why}"));
                foreach (var (n, rungs, hold, basis) in s.Ladders)
                {
                    long tick0 = rungs[0].Tick;
                    for (int r = 0; r < rungs.Count; r++)
                        Log($"ladder study:   lap {r + 1}: course time {rungs[r].Time} ({rungs[r].Time * 0.02:0.00}s, +{rungs[r].Time - rungs[rungs.Count - 1].Time}), {rungs[r].Lap.Count} inputs, finish {rungs[r].Tick - tick0:+0;-0} ticks after lap 1's"
                            + (rungs[r].At.Count == 0 ? "" : $", held ticks before lap inputs {string.Join(", ", rungs[r].At)} (the gate at {rungs[r].Gate})"));
                    foreach (var (j, v) in hold)
                        Log($"ladder study:   held back: goals {j.K + 1}-{j.K + 2}, {j.Time} -> {v.Time} ticks, held ticks before lap inputs {string.Join(", ", v.At)}");
                    if (basis != null) Log($"ladder study:   the run with them held back, its waits re-timed: {Describe(basis)}, {basis.Last - best.Last:+0;-0} ticks");
                }
            }
            var results = new (Route route, Route partial, string why)[edits.Count];
            var pays = new string[edits.Count];
            RunOnThreads(edits.Count, (sim, origin, tracker, i) =>
            {
                GoalEdit e = edits[i];
                sim.UseGoals(tracker.For(GoalsOf(e.Labels)));
                if (best.Progress == _goals) results[i] = PlayedRetuned(sim, origin, e, best);
                else
                {
                    sim.SetStart(origin, 0);
                    sim.Goals.Limit = e.Labels.Length;
                    RunResult rr = sim.Run(e.Inputs, null, null, 1000000);
                    results[i] = (null, new Route { Inputs = e.Inputs, Progress = rr.Progress, GoalInput = rr.GoalInputTicks, GoalPhysics = rr.GoalPhysicsTicks, GoalHeld = rr.GoalHeldFrames },
                        $"played as spliced, the best reaching goal {best.Progress}: {rr.EndReason}");
                }
                int at = Array.IndexOf(e.OldOf, -1);
                pays[i] = BreakPays(sim, origin, results[i].route ?? results[i].partial, e.Labels, course, at, at + e.OldOf.Count(o => o < 0));
            });
            foreach (LadderSpot s in spots.Where(s => s.Block != null))
                Log($"ladder study: the best's laps at {Goals(s.Block)}: {BreakPays(_eval, _origin, best, _labels, course, s.Block[0].K, s.Block[s.Block.Count - 1].K + 2)}");
            for (int i = 0; i < edits.Count; i++)
            {
                (Route r, Route p, string why) = results[i];
                Log($"ladder study: {edits[i].Text}: " + (r == null ? $"{why ?? "not played"} (reached goal {p?.Progress ?? 0} of {edits[i].Labels.Length})"
                    : $"{Describe(r)}, {r.Last - best.Last:+0;-0} ticks, {r.LiveSeconds - best.LiveSeconds:+0.00;-0.00} s live{(why == null ? "" : ", " + why)}") + $"; laps {pays[i]}");
            }
            // A held-back run (the best's own goals) that beats the best is a best of its own: saved as any (the run file, offline-best).
            Route won = spots.SelectMany(s => s.Ladders).Select(l => l.basis).Where(b => b != null && b.Progress == _goals && b.Live < best.Live).OrderBy(b => b.Live).FirstOrDefault();
            int fastest = Enumerable.Range(0, edits.Count).Where(i => results[i].route != null).OrderBy(i => results[i].route.Live).DefaultIfEmpty(-1).First();
            if (fastest >= 0 && results[fastest].route.Live < Math.Min(best.Live, won?.Live ?? long.MaxValue))
            {
                // Its goals (the ladder's, fitted to its strays) become the run's, the old list its lineage, as an adopted goals edit.
                GoalEdit e = edits[fastest];
                string[] from = _labels;
                SwitchGoals(e.Labels);
                _goalLineage.Add(from);
                _best = StandIn(e, best, false);
                _goalsEdit = e.Text;
                Commit(results[fastest].route, "ladder study: " + e.Text);
            }
            else if (won != null) Commit(won, "ladder study: earlier laps held back, the waits re-timed");
        }

        /// <summary>The edit played (<see cref="Played"/>) and, when it plays through, the waits from goal <see cref="GoalEdit.Retune"/>
        /// on re-timed to their Cash (<see cref="Retune"/>): the laps it adds come in place of those waits.</summary>
        (Route route, Route partial, string why) PlayedRetuned(Simulation sim, SimSnapshot origin, GoalEdit e, Route best)
        {
            var played = Played(sim, origin, e, e.Base ?? best);
            if (e.Retune == 0 || played.route == null) return played;
            Route r = played.route, t = Retune(sim, origin, r, r.Inputs, e.Retune, e.Labels.Length);
            if (t == null || t.Progress < e.Labels.Length || t.Live >= r.Live) return played;
            return (t, null, (played.why == null ? "" : played.why + ", ") + $"the waits re-timed ({t.Last - r.Last:+0;-0} ticks)");
        }

        /// <summary>Each finish of <paramref name="course"/> among goals [<paramref name="from"/>, <paramref name="until"/>) (0-based) of
        /// route <paramref name="r"/>: its course time (ticks) and what it paid on its tick: the Cash earned that tick
        /// (Economy.Earned: the lap's reward and the clones that paid) less the course's reward.</summary>
        static string BreakPays(Simulation sim, SimSnapshot origin, Route r, string[] labels, int course, int from, int until)
        {
            until = Math.Min(until, r?.Progress ?? 0);
            if (until <= from || sim.Shop.Economy == null) return "none reached";
            courseScript c = sim.Courses.First(x => x.courseNumber == course);
            var paid = new List<string>();
            double last = 0;
            int progress = 0;
            sim.SetStart(origin, 0);
            sim.Goals.Limit = until;
            sim.Run(r.Inputs, null, null, 400, _ =>
            {
                double now = sim.Shop.Economy.Earned;
                for (; progress < sim.Progress; progress++)
                    if (progress >= from && progress < until && labels[progress] == $"finish:{course}")
                        paid.Add($"{PathTicks(sim.LastCourseTime)}/{(now - last - c.reward).ToString("0.###e0", CultureInfo.InvariantCulture)}");
                last = now;
            });
            return "time/paid " + string.Join(" ", paid);
        }
    }
}
