using System;
using System.Collections.Generic;
using System.Linq;

namespace IGTAP.EngineSim.Cli
{
    // The economy lever (docs/fulltas.md, "Auto"): the plan change as a candidate goal list, evaluated by the loop. A strict
    // run's route buys only the box of its next goal (Simulation.StrictBuys), so the walk order between a row's boxes is
    // pinned by the goal order and the route zig-zags over the row (the per-goal model's `loops back` ticks on buy goals:
    // 1462 on FalseEnding-Any%). The candidate merges each walk's buys into one combined goal (`buy:buyreward2+buyclone2`:
    // the game's own grammar, "each of these boxes, any order"), so one walk may buy its boxes in spatial order; the walk's
    // boxes all stay named in the one goal, so every purchase is still the next goal's and the run stays strict.
    //
    // What does not work, measured: loose buys ("buys": "loose") bank one purchase per box (FullTasTracker._banked is a bit
    // per box), so a regrouped goal list under loose loses the extra levels and stops (the 390.38 route replayed under it
    // reached 140 of 300 goals); and a fixed grouped-by-box ORDER starves the clone buys that pay for the next levels
    // (tools/plan-fulltas.py's model: +2120 ticks on goals 98-181). Merged walk goals leave the order to the search and are
    // a pure relaxation: the run's own route replays under them at the same ticks (checked: 300 of 300, `fulltas --check`).
    //
    // Wiring (Integrator): an "economy" arm at goal k pulls the candidate whose First is k. Evaluate it like Ablate: a copy
    // of the trace header with fulltas.goals = candidate.Goals (strict buys; no `buys` change), the window from the best's
    // state at First-1 through Limit (candidate.Goals indexing), scored at the tick goal Limit is reached. A chunk goal is
    // reached when its last named box is bought, so Limit's tick is the walk's end under any buy order the search finds.

    /// <summary>A candidate goal list: the run's goals with each walk's buys merged into one combined goal.</summary>
    sealed class EconomyCandidate
    {
        public string Name;
        /// <summary>The full goal list (the run's goals with [First, Limit] replaced by combined goals); its length differs
        /// from the run's by the merges.</summary>
        public string[] Goals;
        /// <summary>Always false: merged walk goals run under strict buys (loose buys bank one level per box and lose the rest).</summary>
        public bool LooseBuys => false;
        /// <summary>The replaced span in <see cref="Goals"/> indexing, 1-based and inclusive.</summary>
        public int First, Limit;
        /// <summary>The walk model's estimate of the ticks the free in-walk order can recover (an upper bound).</summary>
        public long SavedTicks;
        public string Why;
    }

    static class EconomyLever
    {
        /// <summary>A same-box buy repeats after the shop's 0.2 s cooldown (12 ticks) plus the re-touch; the run and
        /// plan-fulltas.py's model (REPEAT) both price it at 22-27 ticks.</summary>
        public const long RepeatTicks = 22;
        /// <summary>A buy span's tick gap above this starts a new walk (the run's cross-box walks are 66+ ticks; its same-box
        /// repeats and in-walk steps 24-40).</summary>
        public const long WalkTicks = 40;
        /// <summary>A combined goal's boxes: the tracker's bought bitmask is an int (FullTasTracker._bought), so at most 32;
        /// 16 keeps a chunk to one walk.</summary>
        public const int ChunkBoxes = 16;

        public static bool IsBuy(string label) => label.StartsWith("buy:", StringComparison.Ordinal) || label.StartsWith("reward:", StringComparison.Ordinal);

        /// <summary>Every buy span worth merging, best estimate first. Chunks merge `buy:` goals only: a `reward:` goal's box
        /// comes from its bonus and is not part of a `buy:` combined goal.</summary>
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
                EconomyCandidate c = Merge(labels, goalPhysicsTicks, first, i - 1);
                if (c != null) all.Add(c);
            }
            return all.OrderByDescending(c => c.SavedTicks).ToList();
        }

        /// <summary>The span's buys merged per walk (chunk boundaries: a tick gap over <see cref="WalkTicks"/>, a reward
        /// goal, or the chunk's box cap). The run's own route replays under the merged goals unchanged: the chunks name the
        /// same boxes and their goals are reached when the walk's last box is bought.</summary>
        static EconomyCandidate Merge(IReadOnlyList<string> labels, IReadOnlyList<long> ticks, int first, int last)
        {
            var chunks = new List<List<int>>();
            var cur = new List<int>();
            for (int k = first; k <= last; k++)
            {
                long span = ticks[k] - (k == 0 ? 0 : ticks[k - 1]);
                bool reward = !labels[k].StartsWith("buy:", StringComparison.Ordinal);
                bool newWalk = cur.Count > 0 && (span > WalkTicks || reward || labels[k - 1].StartsWith("reward:", StringComparison.Ordinal)
                    || cur.Count >= ChunkBoxes);
                if (newWalk) { chunks.Add(cur); cur = new List<int>(); }
                cur.Add(k);
            }
            if (cur.Count > 0) chunks.Add(cur);

            // What the free in-walk order recovers: in a chunk of n buys over d boxes the run pays n-1 pairs of measured walk
            // or repeat, the free order pays d-1 walks and n-d repeats.
            long saved = 0;
            foreach (List<int> chunk in chunks)
            {
                int n = chunk.Count;
                if (n < 2) continue;
                var boxes = chunk.Select(k => labels[k]).ToList();
                int d = boxes.Distinct().Count();
                if (d < 2) continue;
                var cross = new List<long>();
                for (int i = 1; i < n; i++)
                    if (boxes[i] != boxes[i - 1]) cross.Add(ticks[chunk[i]] - ticks[chunk[i - 1]]);
                long crossWalk = Median(cross);
                long now = chunk.Sum(k => ticks[k] - (k == 0 ? 0 : ticks[k - 1]));
                long free = (d - 1) * crossWalk + (n - d) * RepeatTicks;
                saved += Math.Max(0, now - free);
            }
            if (saved <= 0) return null;

            // The candidate: the span's goals replaced by one combined goal per chunk ("buy:a+b" names each level once).
            var goals = new List<string>();
            for (int k = 0; k < first; k++) goals.Add(labels[k]);
            int mergedCount = 0, limit = first;
            foreach (List<int> chunk in chunks)
            {
                bool merged = chunk.Count >= 2 && chunk.Select(k => labels[k]).Distinct().Count() >= 2;
                if (merged)
                {
                    goals.Add("buy:" + string.Join("+", chunk.Select(k => labels[k].Substring(4))));
                    mergedCount++;
                }
                else foreach (int k in chunk) goals.Add(labels[k]);
                limit = goals.Count;
            }
            for (int k = last + 1; k < labels.Count; k++) goals.Add(labels[k]);
            string why = $"merges {last - first + 1} buys into {mergedCount}"
                + $" walk goals (`buy:a+b`, any order): about {saved} ticks of in-walk zig-zag to recover";
            return new EconomyCandidate
            {
                Name = $"merged walks {first + 1}-{last + 1}",
                Goals = goals.ToArray(),
                First = first + 1,
                Limit = limit,
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
