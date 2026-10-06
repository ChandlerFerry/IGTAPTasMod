using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    // The join (docs/fulltas.md, "Auto: the join"): a window find whose own arrival breaks the best's tail keeps the part of it
    // before that. Wherever inside the window the find is in a goal-mode cell (OfflineSolver.GoalKey: place, resources, momentum,
    // goals reached) the best reaches later, moving alike, the find's inputs to there and the best's from its first arrival there
    // are one candidate, replayed from the start, as the plain solve's splices join an earlier arrival onto the best's inputs.
    // The states at such a seam are alike, not equal, which the merge's exact joins (RouteSync, TryStitch) never take.
    sealed partial class FullTas
    {
        internal readonly record struct JoinCell(int Input, long Key, long Physics, float VX, float VY, bool Dash);

        /// <summary>A join keeps at least this many physics ticks; two states join only this alike (u/s each way, the same dash).</summary>
        const long JoinGain = 2;
        const float JoinSpeed = 300f;
        /// <summary>Candidates a find gives, this many inputs apart at least, and a merge replays (a Full-TAS run's are whole-run
        /// replays). Spread over the find, not its largest gains alone: on tree27to10 the seams at a find's end (107-118 ticks
        /// ahead) all broke and the ones 24-100 inputs before it (78-80 ahead) finished, states alike at both.</summary>
        const int JoinsPerFind = 4, JoinSpacing = 24, CourseJoins = 12, RunJoins = 4;

        internal static Func<int, bool> RecordCells(Simulation sim, List<JoinCell> into, int from, int to)
        {
            colouredBlockSwapper swapper = sim.World.All<colouredBlockSwapper>().FirstOrDefault();
            return tick =>
            {
                if (tick > from && tick < to && !sim.Engine.InputsExhausted)
                {
                    Vector2 v = sim.Player.Velocity;
                    into.Add(new JoinCell(tick, OfflineSolver.GoalKey(sim, swapper), sim.Engine.PhysicsTicks, v.x, v.y, sim.Player.dashActive));
                }
                return true;
            };
        }

        internal static Dictionary<long, JoinCell> FirstArrivals(List<JoinCell> cells)
        {
            var first = new Dictionary<long, JoinCell>();
            foreach (JoinCell c in cells) first.TryAdd(c.Key, c);
            return first;
        }

        /// <summary>A find's joins onto a run (its first arrivals): (gain, the find's input, the run's input), largest gain first, at
        /// most <paramref name="count"/>, JoinSpacing inputs apart in the find.</summary>
        internal static List<(long gain, int at, int from)> JoinCandidates(List<JoinCell> find, Dictionary<long, JoinCell> best, int count)
        {
            var all = new List<(long gain, int at, int from)>();
            foreach (JoinCell c in find)
                if (best.TryGetValue(c.Key, out JoinCell b) && b.Physics - c.Physics >= JoinGain && b.Dash == c.Dash
                    && Math.Abs(b.VX - c.VX) <= JoinSpeed && Math.Abs(b.VY - c.VY) <= JoinSpeed)
                    all.Add((b.Physics - c.Physics, c.Input, b.Input));
            var picked = new List<(long gain, int at, int from)>();
            foreach (var x in all.OrderByDescending(x => x.gain).ThenByDescending(x => x.at))
                if (picked.Count < count && picked.All(p => Math.Abs(p.at - x.at) >= JoinSpacing)) picked.Add(x);
            return picked;
        }

        /// <summary>The find's inputs to <paramref name="at"/> and the run's from <paramref name="from"/>, the seam pressed and released
        /// as a splice's (OfflineSolver.Seam); null when the seam can't be made or it makes two quick restarts in a row (README ### Rules 34).</summary>
        internal static List<InputTick> Joined(List<InputTick> find, int at, List<InputTick> run, int from, int floor)
        {
            if (at <= 0 || at > find.Count || from >= run.Count) return null;
            var t = new List<InputTick>(at + run.Count - from);
            t.AddRange(find.Take(at));
            t.AddRange(run.Skip(from));
            if (!OfflineSolver.Seam(t, at, floor)) return null;
            for (int i = Math.Max(1, at - 1); i <= at; i++) if (t[i].reset && t[i - 1].reset) return null;
            return t;
        }

        Route _joinFor;
        Dictionary<long, JoinCell> _joinFirst;
        readonly HashSet<ulong> _joinTried = new HashSet<ulong>();

        /// <summary>
        /// The merge's join of a window's finds (each the best's prefix and the find's inputs, with its cells inside the window) onto
        /// the best: the candidates (JoinsPerFind a find, the finds in turn), at most CourseJoins (RunJoins on a Full-TAS run), none
        /// tried on this best before; each replayed from the start and kept when it is ahead of the best. The one that reached the
        /// most goals and missed one gets the wait fix (its Cash comes on the best's ticks). True: one was kept.
        /// </summary>
        bool JoinFinds(string where, Route best, int prefixLength, List<(List<InputTick> inputs, List<JoinCell> cells)> finds)
        {
            if (finds.Count == 0 || best.Progress < _goals) return false;
            if (!ReferenceEquals(_joinFor, best))
            {
                var cells = new List<JoinCell>();
                Evaluate(best.Inputs, -1, RecordCells(_eval, cells, 0, best.Inputs.Count));
                _joinFor = best;
                _joinFirst = FirstArrivals(cells);
                _joinTried.Clear();
            }
            // Each find's best candidate first, then each one's second, and so on.
            var candidates = finds.SelectMany(f => JoinCandidates(f.cells, _joinFirst, JoinsPerFind).Select((c, rank) => (rank, c.gain, c.at, c.from, f.inputs)))
                .OrderBy(c => c.rank).ThenByDescending(c => c.gain).ToList();
            int replayed = 0;
            Route closest = null;
            int closestAt = 0;
            bool kept = false;
            long gain = 0;
            foreach (var c in candidates)
            {
                if (replayed == (_course != null ? CourseJoins : RunJoins)) break;
                List<InputTick> inputs = Joined(c.inputs, c.at, best.Inputs, c.from, prefixLength);
                if (inputs == null) continue;
                ulong hash = SnapshotIndex.Hash(inputs, inputs.Count);
                if (!_joinTried.Add(hash) || _lib != null && _lib.Knows(hash)) continue;
                replayed++;
                Route r = Evaluate(inputs);
                if (r.Progress == _goals && Ahead(r, _best))
                {
                    Log($"auto join: {where}: its input {c.at} onto the best's {c.from} ({c.gain} ticks ahead there) -> {Describe(r)}");
                    Commit(r, where + ", joined onto the best");
                    (kept, gain) = (true, c.gain);
                    break;
                }
                if (r.Progress < _goals && (closest == null || r.Progress > closest.Progress)) (closest, closestAt) = (r, c.at);
            }
            if (!kept && closest != null)
            {
                Route waited = WaitFix(closest, best, closestAt, where + ", joined onto the best", prefixLength);
                if (waited != null && Ahead(waited, _best))
                {
                    Commit(waited, where + ", joined onto the best, waiting for the Cash");
                    kept = true;
                }
            }
            if (candidates.Count > 0)
            {
                Log($"auto join: {where}: {candidates.Count} joins of {finds.Count} finds, {replayed} replayed: " + (kept ? "kept" : "none ahead of the best"));
                AutoWrite(new { type = "join", where, finds = finds.Count, candidates = candidates.Count, replayed, kept, gain });
            }
            return kept;
        }

        /// <summary>
        /// join-check TRACE BEST FIND [--from N] [--count N]: the joins of FIND onto BEST on a course trace's plain simulation, each
        /// replayed to the finish (a replay check of the join, no search): its inputs, the best's, the gain at the seam and the finish.
        /// </summary>
        public static int JoinCheck(string[] args)
        {
            JsonElement header = Solve.ReadHeader(args[0]);
            Simulation sim = Solve.Creator(args[0], header)();
            List<InputTick> best = Plan.ReadRoute(args[1]), find = Plan.ReadRoute(args[2]);
            int from = Solve.Option(args, "--from", 0), count = Solve.Option(args, "--count", 12);
            RunResult Play(List<InputTick> inputs, List<JoinCell> cells, int after) => sim.Run(inputs, null, cells == null ? null : RecordCells(sim, cells, after, inputs.Count), 1000000);
            // The best's cells from its start, as the merge records them; the find's after --from (a window's start).
            var bestCells = new List<JoinCell>();
            RunResult b = Play(best, bestCells, 0);
            var findCells = new List<JoinCell>();
            RunResult f = Play(find, findCells, from);
            Console.WriteLine($"best: {(b.Finished ? $"{b.Time:0.00}s" : "no finish (" + b.EndReason + ")")}, find: {(f.Finished ? $"{f.Time:0.00}s" : "no finish (" + f.EndReason + ")")}");
            Dictionary<long, JoinCell> first = FirstArrivals(bestCells);
            foreach (var (gain, at, onto) in JoinCandidates(findCells, first, count))
            {
                List<InputTick> joined = Joined(find, at, best, onto, from);
                RunResult r = joined == null ? null : Play(joined, null, 0);
                JoinCell fc = findCells.First(c => c.Input == at), bc = first[fc.Key];
                Console.WriteLine($"find {at} onto best {onto} ({gain} ticks ahead, v {fc.VX:0},{fc.VY:0} against {bc.VX:0},{bc.VY:0}): "
                    + (r == null ? "no seam" : r.Finished ? $"finish {r.Time:0.00}s" : "no finish (" + r.EndReason + ")"));
            }
            return 0;
        }
    }
}
