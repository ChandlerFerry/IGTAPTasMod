using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim.Cli.Runs
{
    // What pays a currency from a state, by the economy drive (Simulation.EconomyIdle/Finish/Buy, what plan-check holds to the full
    // simulation): each course's lap and each economy box in reach played once, then Window ticks idle, against Window ticks idle
    // alone. A producer's gain is the currency that adds (a lap's pay and what its record's clones pay, a box's new income); its
    // value is that gain per tick it costs (the lap, or the wait to afford the box, and the walk there).
    static class Income
    {
        internal const int Window = 1000;
        const int LapGuess = 1500;
        // ponytail: walks costed as a straight line at this speed (u a tick); a distance field if the ranking misorders.
        const float Speed = 8f;
        static readonly FieldInfo BestPathTime = typeof(courseScript).GetField("bestPathTime", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly globalStats.Currencies[] Currencies = (globalStats.Currencies[])Enum.GetValues(typeof(globalStats.Currencies));

        internal sealed record Producer(string Label, double Gain, double Ticks)
        {
            public double Value => Gain / Math.Max(1, Ticks);
        }

        // The currency the boxes are shortest of at the state, and by how much (null: none short, or a box not on).
        internal static (globalStats.Currencies currency, double by)? Short(Simulation sim, SimSnapshot s, string[] boxes)
        {
            sim.Restore(s);
            double[] need = sim.Shop.Need(boxes);
            if (need == null) return null;
            var shortest = Enumerable.Range(0, Math.Min(need.Length, Currencies.Length)).Select(k => (k, by: need[k] - sim.Shop.Holds(Currencies[k])))
                .Where(x => need[x.k] > 0).OrderByDescending(x => x.by).FirstOrDefault((k: -1, by: 0));
            return shortest.k < 0 || shortest.by <= 0 ? null : (Currencies[shortest.k], shortest.by);
        }

        // What idling Window ticks at the state pays of the currency.
        internal static double Idle(Simulation sim, SimSnapshot s, globalStats.Currencies c)
        {
            sim.RestoreForDrive(s);
            double from = sim.Shop.Gained(c);
            try { sim.EconomyIdle(Window, sim.Player.transform.position); }
            catch (InvalidOperationException) { return 0; }
            double gained = sim.Shop.Gained(c) - from;
            sim.Restore(s);
            return gained;
        }

        // Every producer of the currency at the state, the best value first.
        internal static List<Producer> Of(Simulation sim, SimSnapshot s, globalStats.Currencies c)
        {
            sim.RestoreForDrive(s);
            Vector2 at = sim.Player.transform.position;
            double[] Gained() => Currencies.Select(x => sim.Shop.Gained(x)).ToArray();
            double[] g0 = Gained();
            sim.EconomyIdle(Window, at);
            double[] idle = Gained().Zip(g0, (a, b) => a - b).ToArray();
            float Walk(Vector2 to) => Vector2.Distance(at, to) / Speed;
            // C gained over the window after the move, beyond what idling pays; 0 when the drive refuses it (an unmodeled event).
            double Gain(Action before, Func<bool> move)
            {
                sim.RestoreForDrive(s);
                try
                {
                    before?.Invoke();
                    double from = sim.Shop.Gained(c);
                    if (!move()) return 0;
                    sim.EconomyIdle(Window, at);
                    return sim.Shop.Gained(c) - from - idle[(int)c];
                }
                catch (InvalidOperationException) { return 0; }
            }
            var found = new List<Producer>();
            foreach (courseScript course in sim.Courses.Where(x => x.courseNumber > 0).GroupBy(x => x.courseNumber).Select(g => g.First()))
            {
                // A course never finished: its first lap is a record, on a path of LapGuess ticks (a sum of physics ticks, as the timer's).
                float best = (float)BestPathTime.GetValue(course), time = 0f;
                if (best < 1e7f) time = best;
                else for (int i = 0; i < LapGuess; i++) time += 0.02f;
                double gain = Gain(null, () => { sim.EconomyFinish(course.courseNumber, time); return true; });
                if (gain > 0) found.Add(new Producer("record:" + course.courseNumber, gain, time / 0.02f + Walk(course.transform.position)));
            }
            sim.Restore(s);
            foreach (var (token, path, sibling) in FullTasGoal.Boxes)
            {
                string id = FullTasGoal.BoxId(path, sibling);
                if (!FullTasGoal.IsEconomy("buy:" + token) || !sim.Shop.HasBox(id)) continue;
                sim.Restore(s);
                double[] need = sim.Shop.Need(new[] { id });
                upgradeBox box = sim.Shop.Box(id);
                if (need == null || box == null || !(sim.TouchSpot(box) is Vector2 spot)) continue;
                // Affordable now, or within the window at the income idling pays.
                double wait = 0;
                for (int k = 0; k < need.Length && k < Currencies.Length; k++)
                {
                    double gap = need[k] - sim.Shop.Holds(Currencies[k]);
                    if (gap > 0) wait = Math.Max(wait, idle[k] > 0 ? gap / (idle[k] / Window) : double.PositiveInfinity);
                }
                if (wait > Window) continue;
                int w = (int)Math.Ceiling(wait);
                double gain = Gain(w == 0 ? null : () => sim.EconomyIdle(w, at), () => sim.EconomyBuy(id, spot));
                if (gain > 0) found.Add(new Producer("buy:" + token, gain, w + Walk(spot) + 1));
            }
            sim.Restore(s);
            return found.OrderByDescending(p => p.Value).ThenBy(p => p.Label, StringComparer.Ordinal).ToList();
        }
    }
}
