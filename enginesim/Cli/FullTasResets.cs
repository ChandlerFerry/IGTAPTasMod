using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    // --reset-study (docs/fulltas.md, "Reset study"): a quick restart throws the player's momentum away. For each reset in the
    // best, the way on without it is searched from the input before it (no mutation adds a reset there) through the goal the
    // reset is in and ResetStudyAhead goals after it, and set against the best at each of those goals: arrival tick and speed
    // (|Velocity + momentum|) 12 ticks after the last one. A reset whose no-reset way is slower everywhere is a teleport that
    // saves time; one whose no-reset way arrives sooner or faster is a momentum killer. A no-reset way that makes the whole run
    // faster is kept (Merge), else the goals after it are searched again when it is ahead at the window's end (Repair).
    sealed partial class FullTas
    {
        bool _noResets;
        const int ResetStudyAhead = 2;

        void ResetStudy()
        {
            var table = new List<string>();
            var done = new HashSet<(int goal, int offset)>();
            while (!TimeUp)
            {
                Route best = _best;
                // Resets not studied yet, keyed by the goal they're in and their input's offset from that goal's start, so a
                // commit that moves the inputs doesn't study one twice.
                var next = Enumerable.Range(1, best.Inputs.Count - 1).Where(i => best.Inputs[i].reset)
                    .Select(i => (q: i, g: GoalsBy(best, i))).Where(r => r.g < best.Progress)
                    .Select(r => (r.q, r.g, offset: r.q - (r.g == 0 ? 0 : (int)best.GoalInput[r.g - 1])))
                    .FirstOrDefault(r => !done.Contains((r.g, r.offset)));
                if (next.q == 0) break;
                done.Add((next.g, next.offset));
                table.Add(StudyReset(best, next.q, next.g));
            }
            Log("reset study (goal: best tick / no-reset tick; speed after the last goal):");
            foreach (string row in table) Log("  " + row);
        }

        /// <summary>How many goals the route has reached by its input q.</summary>
        static int GoalsBy(Route r, int q)
        {
            int g = 0;
            while (g < r.Progress && r.GoalInput[g] <= q) g++;
            return g;
        }

        string StudyReset(Route best, int q, int reached)
        {
            int limit = Math.Min(best.Progress, reached + 1 + ResetStudyAhead);
            string where = $"reset at input {q} (in goal {reached + 1} {_labels[reached]}), window to goal {limit}";
            // Seeds: the best's own way on with its resets made plain ticks, and with the reset tick left out.
            List<InputTick> rest = best.Inputs.Skip(q).ToList();
            List<InputTick> plain = rest.Select(t => { t.reset = false; return t; }).ToList();
            var seeds = new List<List<InputTick>> { plain, plain.Skip(1).ToList() };
            List<List<InputTick>> found;
            _noResets = true;
            try { found = SearchWindow("noreset", best, q, limit, seeds, long.MaxValue, _windowMinutes, 0); }
            finally { _noResets = false; }
            var (bestTicks, bestSpeed) = Arrivals(best.Inputs.Take((int)Math.Min(best.GoalInput[limit - 1], best.Inputs.Count)).ToList(), reached, limit);
            if (found.Count == 0)
            {
                Log($"reset study: {where}: no way on without it reaches goal {limit}");
                return $"{where}: no reset-free way found; the reset stays (best {Ticks(bestTicks)}, speed {bestSpeed:0})";
            }
            List<InputTick> way = best.Inputs.Take(q).Concat(found[found.Count - 1]).ToList();
            var (ticks, speed) = Arrivals(way, reached, limit);
            long gain = ticks.Length == bestTicks.Length && ticks.Length > 0 ? bestTicks[bestTicks.Length - 1] - ticks[ticks.Length - 1] : long.MinValue;
            string verdict = gain == long.MinValue ? "the no-reset way doesn't replay to the window's end"
                : gain > 0 ? $"momentum killer: without it goal {limit} comes {gain} ticks sooner"
                : speed > bestSpeed * 1.25f && gain > -12 ? $"momentum killer: without it goal {limit} comes {-gain} ticks later but {speed / Math.Max(1f, bestSpeed):0.0}x as fast"
                : $"teleport: it saves {-gain} ticks to goal {limit}";
            string row = $"{where}: best {Ticks(bestTicks)} speed {bestSpeed:0}; no reset {Ticks(ticks)} speed {speed:0}; {verdict}";
            Log("reset study: " + row);
            if (gain > 0 || speed > bestSpeed * 1.25f) Merge($"the way on without the reset at input {q} (reset study)", best, q, limit, found);
            return row;
        }

        static string Ticks(long[] t) { return t.Length == 0 ? "-" : string.Join("/", t); }

        /// <summary>The goals reached..limit's physics ticks when inputs are played from the run's start, and the player's speed
        /// (|Velocity + momentum|) where the replay stops, a strict finish's 12 ticks after the last one.</summary>
        (long[] ticks, float speed) Arrivals(List<InputTick> inputs, int reached, int limit)
        {
            Route r = Evaluate(inputs, limit);
            if (r.Progress < limit) return (new long[0], 0f);
            Vector2 v = (Vector2)VelocityOf.GetValue(_eval.Player) + (Vector2)MomentumOf.GetValue(_eval.Player);
            return (Enumerable.Range(reached, limit - reached).Select(g => r.GoalPhysics[g]).ToArray(), v.magnitude);
        }
    }
}
