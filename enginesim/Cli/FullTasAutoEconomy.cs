using System;
using System.Collections.Generic;
using System.Linq;

namespace IGTAP.EngineSim.Cli
{
    // The economy lever (docs/fulltas.md, "Auto"): the plan change as a candidate goal list, evaluated by the loop. A Full-TAS
    // route buys one level per touch and walks the box row between buys; when the goals alternate boxes the walk doubles back
    // over the row (the per-goal model's "loops back" ticks). This lever rewrites a buy span's goal list so every box's buys
    // are contiguous (one visit per box per walk: the grouped-buys plan of fulltas-plan --group-buys), keeping the goal count,
    // so every goal index outside the span is unchanged and a window at [First, Limit] measures the plan change against the
    // best under the run's own goals. It never edits the run's goals: the loop evaluates the candidate (as Ablations do) and
    // SearchDesign decides what is integrated.
    //
    // Wiring (SearchDesign): a "economy" arm at goal k = the candidate whose First is k. Evaluate it exactly like Ablate:
    // a copy of the trace header with fulltas.goals replaced by candidate.Goals (Solve.Creator), the window from the best's
    // state at First-1 through Limit, scored at the tick goal Limit is reached; the outcome string can carry Candidate.Why.
    // Candidates are cheap to compute; one pull can try the best few.

    /// <summary>A candidate goal list: the run's goals with one buy span reordered (same goals, grouped by box).</summary>
    sealed class EconomyCandidate
    {
        public string Name;
        /// <summary>The full goal list (the run's own goals with [First, Limit] reordered); same length as the run's.</summary>
        public string[] Goals;
        /// <summary>The replaced span, 1-based and inclusive (a buy run: consecutive buy:/reward: goals).</summary>
        public int First, Limit;
        /// <summary>The model's estimate of the ticks the span saves against the run's own order.</summary>
        public long SavedTicks;
        public string Why;
    }

    static class EconomyLever
    {
        /// <summary>A same-box buy repeats after the shop's 0.2 s cooldown (12 ticks) plus the re-touch; the run and
        /// plan-fulltas.py's model (REPEAT) both price it at 22-27 ticks.</summary>
        public const long RepeatTicks = 22;

        public static bool IsBuy(string label) => label.StartsWith("buy:", StringComparison.Ordinal) || label.StartsWith("reward:", StringComparison.Ordinal);

        /// <summary>Every buy span worth grouping, best estimate first. Spans of one box or fewer than three buys are skipped:
        /// there is no second walk to remove.</summary>
        public static List<EconomyCandidate> Candidates(IReadOnlyList<string> labels, IReadOnlyList<long> goalPhysicsTicks)
        {
            if (labels == null || goalPhysicsTicks == null || labels.Count != goalPhysicsTicks.Count)
                throw new ArgumentException("labels and goalPhysicsTicks must have the same length");
            var all = new List<EconomyCandidate>();
            for (int i = 0; i < labels.Count;)
            {
                if (!IsBuy(labels[i])) { i++; continue; }
                int first = i;
                while (i < labels.Count && IsBuy(labels[i])) i++;
                EconomyCandidate c = Group(labels, goalPhysicsTicks, first, i - 1);
                if (c != null) all.Add(c);
            }
            return all.OrderByDescending(c => c.SavedTicks).ToList();
        }

        /// <summary>The span's own order against every box's buys contiguous (boxes in the order the run first touches them:
        /// the first and last walk of the span are kept where they are, so the walk in and out of the span is unchanged).</summary>
        static EconomyCandidate Group(IReadOnlyList<string> labels, IReadOnlyList<long> ticks, int first, int last)
        {
            int n = last - first + 1;
            if (n < 3) return null;
            // Per-goal span ticks; goal 1's span starts at the run's start.
            long[] span = new long[n];
            for (int k = first; k <= last; k++)
                span[k - first] = ticks[k] - (k == 0 ? 0 : ticks[k - 1]);
            // The walk structure the run pays now: consecutive buys of one box cost the cooldown repeat, consecutive buys of
            // different boxes cost a walk (their measured span).
            var order = new List<string>();
            var count = new Dictionary<string, int>();
            for (int k = first; k <= last; k++)
            {
                string box = labels[k];
                if (!count.ContainsKey(box)) { count[box] = 0; order.Add(box); }
                count[box]++;
            }
            if (order.Count < 2) return null;

            long now = 0;
            for (int k = first; k <= last; k++) now += ticks[k] - (k == 0 ? 0 : ticks[k - 1]);
            // Grouped: one walk per box after the first (the median cross-box walk of the span), every later level of a box a
            // cooldown repeat. The walks into the first box and out of the last are already in `now` and unchanged by grouping.
            var cross = new List<long>();
            for (int k = first + 1; k <= last; k++)
                if (labels[k] != labels[k - 1]) cross.Add(ticks[k] - ticks[k - 1]);
            if (cross.Count == 0) return null;
            long crossWalk = Median(cross);
            long grouped = 0;
            foreach (string box in order)
            {
                int c = count[box];
                grouped += (c - 1) * RepeatTicks;
            }
            grouped += (order.Count - 1) * crossWalk;
            long saved = now - grouped;
            if (saved <= 0) return null;

            // The candidate: the span's goals grouped by box (each box's buys in the run's own order).
            string[] goals = labels.ToArray();
            var groupedLabels = order.SelectMany(box => Enumerable.Repeat(box, count[box])).ToArray();
            for (int k = 0; k < n; k++) goals[first + k] = groupedLabels[k];
            string why = $"buys {n} boxes' levels in {order.Count} visits against the run's {cross.Count + 1} walks"
                + $" (a walk {crossWalk} ticks, a repeat {RepeatTicks}): about {saved} ticks of walking";
            return new EconomyCandidate
            {
                Name = $"grouped-buys {first + 1}-{last + 1}",
                Goals = goals,
                First = first + 1,
                Limit = last + 1,
                SavedTicks = saved,
                Why = why,
            };
        }

        static long Median(List<long> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s[s.Count / 2];
        }
    }
}
