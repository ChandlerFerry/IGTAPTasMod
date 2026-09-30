using System;
using System.Collections.Generic;
using System.Linq;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    // --parts goals, payout breaks (techs.json record-break-payout, docs/fulltas.md "Auto"): a course finish that beats the
    // course's best time replaces the clone path, and every clone on the old path pays at once (clonesScript.updateClonePath,
    // DestroyClone). A lap that only ties the best time pays nothing extra; played after the laps that set that time, each made
    // one tick slower, it breaks the record and brings the Cash of the clones in flight forward by up to one best time.
    sealed partial class FullTas
    {
        /// <summary>A payout break slows at most this many laps (each costs at least a tick), and a pull plays at most this many.</summary>
        const int MaxPayoutScuffs = 40, MaxPayoutEdits = 48;

        /// <summary>A finish of the best: goals K (start:N) and K+1 (finish:N), its own course time and the course's best time just
        /// before and after it, in physics ticks.</summary>
        sealed class Finish
        {
            public int K, Course;
            public long Time, Before, After;
        }

        /// <summary>Each payout edit's (by key) best time it breaks, in ticks: the most its clones' Cash comes forward by.</summary>
        readonly Dictionary<string, long> _payoutReach = new Dictionary<string, long>();

        /// <summary>
        /// Whether a payout break that didn't play through spliced is worth re-finding as an investment: behind the best by less
        /// than the best time it breaks where its old way on stops (a dropped lap already counted). The clones' Cash comes forward
        /// by at most one best time, so a deficit past that can't be paid back (FalseEnding-Any%'s held-back Course 1 record lap,
        /// 142 ticks behind a 70-tick best time, took 4 minutes on 16 threads to re-find no faster).
        /// </summary>
        bool PayoutMayPay(GoalEdit e, Route partial, Route best)
        {
            int p = partial.Progress;
            return p > 0 && e.OldOf[p - 1] >= 0 && _payoutReach.TryGetValue(e.Key, out long reach)
                && partial.GoalPhysics[p - 1] - best.GoalPhysics[e.OldOf[p - 1]] < reach;
        }

        static long PathTicks(float seconds) { return (long)Math.Round(seconds / 0.02); }

        /// <summary>Every lap of the best (walked in or not), timed in one replay.</summary>
        List<Finish> Finishes(Route best)
        {
            var finishOf = new int[_goals];
            for (int k = 0; k + 1 < _goals; k++)
            {
                FullTasGoal s = _runGoals.Goals[k], f = _runGoals.Goals[k + 1];
                if (s.kind == FullTasGoal.Start && f.kind == FullTasGoal.Finish && s.course == f.course) finishOf[k + 1] = s.course;
            }
            var finishes = new List<Finish>();
            if (finishOf.All(c => c == 0)) return finishes;
            courseScript[] courses = finishOf.Where(c => c != 0).Distinct().Select(c => _eval.Courses.First(x => x.courseNumber == c)).ToArray();
            long Best(courseScript c) => PathTicks((float)BestPathTime.GetValue(c));
            _eval.Restore(_origin);
            long[] last = courses.Select(Best).ToArray();
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            int progress = 0;
            _eval.Run(best.Inputs, null, null, 1000000, _ =>
            {
                for (; progress < _eval.Progress && progress < _goals; progress++)
                {
                    if (finishOf[progress] == 0) continue;
                    int i = Array.FindIndex(courses, c => c.courseNumber == finishOf[progress]);
                    finishes.Add(new Finish { K = progress - 1, Course = finishOf[progress], Time = PathTicks(_eval.LastCourseTime), Before = last[i], After = Best(courses[i]) });
                }
                for (int i = 0; i < courses.Length; i++) last[i] = Best(courses[i]);
            });
            return finishes;
        }

        /// <summary>
        /// Payout breaks: a restart lap f played at its course's best time, with every earlier lap that set or tied that time (back
        /// to the last reset of the best time: a prestige) played at least one tick slower (<see cref="ScuffOf"/>), so f's finish
        /// breaks the record and the clones on the path pay there. A lap f slower than the best time plays the lap that set it
        /// again (both from a quick restart): the record held back to f (FalseEnding-Any%: Course 1's 1.40 s lap at goals 22-23,
        /// then 1.62 s laps to buy:buywj). Offered at the last lap of each run of back-to-back laps, and at the one or two before
        /// it with the laps after it dropped (the payout buys what they earned), and at the last with the last lap of one of the
        /// next three runs of laps dropped. The way on from a break that doesn't play through spliced is re-found as an
        /// investment (<see cref="InvestRepair"/>) when it may pay (<see cref="PayoutMayPay"/>).
        /// </summary>
        List<GoalEdit> PayoutEdits(Route best, List<RunLap> laps)
        {
            var edits = new List<GoalEdit>();
            List<Finish> finishes = Finishes(best);
            var specs = new List<(RunLap f, List<RunLap> drop, List<Finish> slow, RunLap from)>();
            List<List<RunLap>> blocks = Blocks(laps);
            foreach (List<RunLap> block in blocks)
                for (int m = 0; m <= Math.Min(2, block.Count - 1); m++)
                {
                    RunLap f = block[block.Count - 1 - m];
                    Finish ff = finishes.FirstOrDefault(x => x.K == f.K);
                    if (ff == null || ff.Time < ff.Before) continue;
                    long t = ff.Before;
                    var slow = new List<Finish>();
                    Finish next = ff;
                    foreach (Finish j in finishes.Where(x => x.Course == f.Course && x.K < f.K).Reverse())
                    {
                        if (next.Before > j.After) break;
                        if (j.Time < t) { slow = null; break; }
                        if (j.Time == t) slow.Add(j);
                        next = j;
                    }
                    if (slow == null || slow.Count == 0 || slow.Count > MaxPayoutScuffs) continue;
                    slow.Reverse();
                    RunLap from = null;
                    if (ff.Time != t && (from = laps.FirstOrDefault(l => l.K == slow[0].K)) == null) continue;
                    specs.Add((f, block.Skip(block.Count - m).ToList(), slow, from));
                    if (m == 0)
                        foreach (List<RunLap> later in blocks.Where(b => b[0].K > f.K).Take(3))
                            specs.Add((f, new List<RunLap> { later[later.Count - 1] }, slow, from));
                }
            // ponytail: fewest slowed laps first, cheapest to try; rank by the clones in flight at f if the cap cuts good ones.
            specs = specs.OrderBy(s => s.slow.Count).ThenBy(s => s.drop.Count).Take(MaxPayoutEdits).ToList();
            if (specs.Count == 0) return edits;
            int[] need = specs.SelectMany(s => s.slow.Select(j => j.K)).Distinct().OrderBy(k => k).ToArray();
            var scuffs = new List<InputTick>[need.Length];
            int chunks = Math.Min(_threads, need.Length);
            // Each thread slows a contiguous stretch of the laps, advancing its own simulation along the best from one to the next.
            RunOnThreads(chunks, (sim, origin, tracker, c) =>
            {
                sim.UseGoals(tracker.For(_runGoals.Goals));
                SimSnapshot at = origin;
                int pos = 0;
                for (int i = c * need.Length / chunks; i < (c + 1) * need.Length / chunks; i++)
                {
                    Finish j = finishes.First(x => x.K == need[i]);
                    int gate = (int)Math.Min(best.GoalInput[j.K], best.Inputs.Count);
                    sim.SetStart(at, pos);
                    sim.Goals.Limit = _goals;
                    if ((at = sim.Advance(best.Inputs.GetRange(pos, gate - pos))) == null) return;
                    pos = gate;
                    scuffs[i] = ScuffOf(sim, at, best, j);
                }
            });
            foreach (var (f, drop, slow, from) in specs)
            {
                if (slow.Any(j => scuffs[Array.IndexOf(need, j.K)] == null))
                {
                    Log($"auto goals: no payout break at goals {f.K + 1}-{f.K + 2} (Course {f.Course}): no lap one tick slower at goals "
                        + string.Join(", ", slow.Where(j => scuffs[Array.IndexOf(need, j.K)] == null).Select(j => $"{j.K + 1}-{j.K + 2}")));
                    continue;
                }
                var ops = slow.Select(j => ((int)best.GoalInput[j.K], (int)Math.Min(best.GoalInput[j.K + 1], best.Inputs.Count), scuffs[Array.IndexOf(need, j.K)])).ToList();
                ops.AddRange(drop.Select(d => (d.A, d.B, new List<InputTick>())));
                if (from != null)
                {
                    // f's own quick restart, then the record lap's inputs after its restart through its finish.
                    var lap = new List<InputTick> { best.Inputs[f.A] };
                    lap.AddRange(best.Inputs.GetRange(from.A + 1, (int)Math.Min(best.GoalInput[from.K + 1], best.Inputs.Count) - from.A - 1));
                    ops.Add((f.A, (int)Math.Min(best.GoalInput[f.K + 1], best.Inputs.Count), lap));
                }
                string text = $"Payout break: the Course {f.Course} lap at goals {f.K + 1}-{f.K + 2}"
                    + (from == null ? "" : $" plays the record lap of goals {from.K + 1}-{from.K + 2} again and")
                    + $" beats its {slow[0].Time * 0.02:0.00}s best time, "
                    + $"{Plural(slow.Count, "lap")} before it a tick slower (goals {string.Join(", ", slow.Select(j => $"{j.K + 1}-{j.K + 2}"))})"
                    + (drop.Count == 0 ? "" : $"; dropped {Plural(drop.Count, $"Course {drop[0].Course} lap")} ({Goals(drop)})");
                GoalEdit e = Spliced(best, ops, drop.SelectMany(d => new[] { d.K, d.K + 1 }), "payout", $"payout:{f.K}:{string.Join(",", drop.Select(d => d.K))}:{from?.K ?? -1}", text,
                    slow[0].K + 1, drop.Count == 0 ? f.K + 2 : drop.Max(d => d.K) + 2);
                _payoutReach[e.Key] = slow[0].Time;
                edits.Add(e);
            }
            return edits;
        }

        /// <summary>
        /// Lap j of the best (from its gate goal's input to its finish goal's) at least one tick slower on the course timer: one
        /// tick that holds the input before it (<see cref="InputTick.Coast"/>, no new press) put in at each point after the gate,
        /// and of those whose lap is slower, the one that finishes soonest (then the least slower). From <paramref name="at"/>, the
        /// state at the gate goal's input. Null when none is.
        /// </summary>
        List<InputTick> ScuffOf(Simulation sim, SimSnapshot at, Route best, Finish j)
        {
            int gate = (int)Math.Min(best.GoalInput[j.K], best.Inputs.Count), fin = (int)Math.Min(best.GoalInput[j.K + 1], best.Inputs.Count);
            if (gate == 0) return null;
            List<InputTick> lap = best.Inputs.GetRange(gate, fin - gate);
            (List<InputTick> lap, long tick, long time)? pick = null;
            for (int p = 0; p < lap.Count; p++)
            {
                var v = new List<InputTick>(lap.Count + 1);
                v.AddRange(lap.Take(p));
                v.Add((p == 0 ? best.Inputs[gate - 1] : lap[p - 1]).Coast());
                v.AddRange(lap.Skip(p));
                sim.SetStart(at, gate);
                sim.Goals.Limit = j.K + 2;
                RunResult rr = sim.Run(v, null, null, 400);
                if (rr.Progress < j.K + 2) continue;
                long time = PathTicks(sim.LastCourseTime), tick = rr.GoalPhysicsTicks[j.K + 1];
                if (time <= j.Time || pick != null && (tick > pick.Value.tick || tick == pick.Value.tick && time >= pick.Value.time)) continue;
                int end = (int)rr.GoalInputTicks[j.K + 1] - gate;
                while (v.Count < end) v.Add(new InputTick());
                pick = (v.GetRange(0, end), tick, time);
            }
            return pick?.lap;
        }
    }
}
