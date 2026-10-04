using System;
using System.Collections.Generic;
using System.Linq;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// plan-selftest: <see cref="EconomyPlan.Search{TSnap}"/> on a toy economy, without the game. One course (`c1`) pays 10 a lap
    /// (100 ticks); a clone box costs 30 and each clone pays 1 a tick while the player is off the course's screen (anywhere but
    /// `c1`); a free bonus box 150 ticks away doubles the lap reward; the target box costs 2000. The planner's schedule must
    /// reach the target on the tick a brute force over every order of up to 12 steps finds first, and dominance must have
    /// dropped at least one state on the way. Prints each PASS or FAIL; exits 1 on a FAIL.
    /// </summary>
    static class EconomyPlanCheck
    {
        sealed record ToySnap(double Cash, int Clones, int Mult, int Targets);

        sealed class Toy : IEconomy<ToySnap>
        {
            double _cash;
            int _clones, _mult = 1, _targets;
            public ToySnap Capture() => new ToySnap(_cash, _clones, _mult, _targets);
            public void Restore(ToySnap s) => (_cash, _clones, _mult, _targets) = (s.Cash, s.Clones, s.Mult, s.Targets);
            public void Idle(int ticks, string place) { if (place != "c1") _cash += _clones * ticks; }
            public bool Buy(string box)
            {
                Idle(1, box);
                double cost = box == "clone" ? 30 : box == "target" ? 2000 : 0;
                if (_cash < cost || box == "bonus" && _mult == 2) return false;
                _cash -= cost;
                if (box == "clone") _clones++;
                else if (box == "bonus") _mult = 2;
                else _targets++;
                return true;
            }
            public void Finish(string course) { _cash += 10 * _mult; }
            public double[] Holds() => new[] { _cash };
            public string Key() => _clones + "," + _mult + "," + _targets;
        }

        static readonly Dictionary<(string, string), int> Walk = new Dictionary<(string, string), int>
        {
            [("c1", "clone")] = 20, [("clone", "clone")] = 20, [("c1", "bonus")] = 150, [("clone", "bonus")] = 150,
            [("c1", "target")] = 300, [("clone", "target")] = 300, [("bonus", "target")] = 300,
        };

        static int Ticks(string a, string b) => a == b && a != "clone" ? 0 : Walk.TryGetValue((a, b), out int t) || Walk.TryGetValue((b, a), out t) ? t : 300;

        static IEnumerable<PlanOption> Options(Toy toy, string place)
        {
            yield return new PlanOption(new Step("lap", "lap:1", Ticks(place, "c1") + 100), "c1", Array.Empty<string>(), "1", false, new[] { "start:1", "finish:1" });
            yield return new PlanOption(new Step("buy", "buy:clone", Ticks(place, "clone")), "clone", new[] { "clone" }, null, false, new[] { "buy:clone" });
            if (toy.Capture().Mult == 1)
                yield return new PlanOption(new Step("reward", "reward:bonus", Ticks(place, "bonus")), "bonus", new[] { "bonus" }, null, false, new[] { "reward:bonus" });
            yield return new PlanOption(new Step("target", "buy:target", Ticks(place, "target")), "target", new[] { "target" }, null, true, new[] { "buy:target" });
        }

        internal static int Run()
        {
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
                if (!ok) fails++;
            }
            var toy = new Toy();
            PlanResult plan = EconomyPlan.Search(toy, 0, "c1", p => Options(toy, p), 1000000, 60);
            Console.WriteLine($"planner: {(plan.Done ? "tick " + plan.Tick : "not done")}, {plan.States} states, {plan.Dropped} dominated dropped: "
                + string.Join(", ", plan.Steps.Select(s => s.Arg)));

            // Brute force: every order of up to 12 steps, each step played as the planner plays it.
            long best = long.MaxValue;
            string bestOrder = null;
            int orders = 0;
            void Explore(ToySnap at, string place, long tick, int depth, List<string> order)
            {
                if (depth == 12 || tick >= best) return;
                toy.Restore(at);
                foreach (PlanOption o in Options(toy, place).ToList())
                {
                    toy.Restore(at);
                    long? end = EconomyPlan.Play(toy, tick, o);
                    if (end == null) continue;
                    order.Add(o.Step.Arg);
                    orders++;
                    if (o.Target)
                    {
                        if (end.Value < best) { best = end.Value; bestOrder = string.Join(", ", order); }
                    }
                    else Explore(toy.Capture(), o.End, end.Value, depth + 1, order);
                    order.RemoveAt(order.Count - 1);
                }
            }
            Explore(new Toy().Capture(), "c1", 0, 0, new List<string>());
            Console.WriteLine($"brute force: {(bestOrder == null ? "none" : "tick " + best)} over {orders} step orders: {bestOrder}");
            Check(plan.Done && bestOrder != null && plan.Tick == best, $"the planner's target tick {plan.Tick} is the brute force's {best}");
            Check(plan.Steps.Length <= 12, $"the plan has {plan.Steps.Length} steps (the brute force's reach: 12)");

            // The planner's order, played again, reaches the target on its tick.
            toy.Restore(new Toy().Capture());
            long t = 0;
            string at = "c1";
            bool plays = true;
            foreach (Step s in plan.Steps)
            {
                PlanOption o = Options(toy, at).FirstOrDefault(x => x.Step.Arg == s.Arg);
                long? end = o == null ? null : EconomyPlan.Play(toy, t, o);
                if (end == null) { plays = false; break; }
                (t, at) = (end.Value, o.End);
            }
            Check(plays && t == plan.Tick, $"the planner's order played again reaches the target on tick {t}");
            Check(plan.Dropped > 0, $"dominance dropped {plan.Dropped} states (same place and levels, no later, no less Cash)");
            Check(plan.Proven, "the plan is proven (the queue reached the target before any cap)");

            // A prefix (--compare) plays exactly, then the search finishes from there: never earlier than the free plan.
            toy.Restore(new Toy().Capture());
            PlanResult forced = EconomyPlan.Search(toy, 0, "c1", p => Options(toy, p), 1000000, 60, new[] { "reward:bonus", "lap:1" });
            Check(forced.Done && forced.Steps.Take(2).Select(s => s.Arg).SequenceEqual(new[] { "reward:bonus", "lap:1" }) && forced.Tick >= plan.Tick,
                $"--compare reward:bonus,lap:1: tick {forced.Tick} ({string.Join(", ", forced.Steps.Select(s => s.Arg))}), the free plan's {plan.Tick}");
            Console.WriteLine(fails == 0 ? "plan-selftest: PASS" : $"plan-selftest: {fails} FAIL");
            return fails == 0 ? 0 : 1;
        }
    }
}
