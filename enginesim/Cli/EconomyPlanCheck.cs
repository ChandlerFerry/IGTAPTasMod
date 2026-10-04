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
        sealed record ToySnap(double Cash, int Clones, int Mult, int Targets, double Earned);

        sealed class Toy : IEconomy<ToySnap>
        {
            double _cash, _earned;
            int _clones, _mult = 1, _targets;
            /// <summary>The target's price, and whether the clone box is offered (a toy whose only income is laps).</summary>
            public double Cost = 2000;
            public bool Clones = true;
            /// <summary>Ticks played (Idle's and Buy's), for the stand checks.</summary>
            public int Played;
            public ToySnap Capture() => new ToySnap(_cash, _clones, _mult, _targets, _earned);
            public void Restore(ToySnap s) => (_cash, _clones, _mult, _targets, _earned) = (s.Cash, s.Clones, s.Mult, s.Targets, s.Earned);
            public void Idle(int ticks, string place) { Played += ticks; if (place != "c1") Gain(_clones * ticks); }
            void Gain(double cash) { _cash += cash; _earned += cash; }
            public bool Buy(string box)
            {
                Idle(1, box);
                double cost = box == "clone" ? 30 : box == "target" ? Cost : 0;
                if (_cash < cost || box == "bonus" && _mult == 2) return false;
                _cash -= cost;
                if (box == "clone") _clones++;
                else if (box == "bonus") _mult = 2;
                else _targets++;
                return true;
            }
            public void Finish(string course) { Gain(10 * _mult); }
            public double[] Holds() => new[] { _cash };
            public string Key() => _clones + "," + _mult + "," + _targets;
            public double[] Earned() => new[] { _earned };
            public double[] Price(string box, int levels = 1) => box == "bonus" && _mult == 2 ? null : new[] { box == "clone" ? 30.0 * levels : box == "target" ? Cost : 0.0 };
        }

        static readonly Dictionary<(string, string), int> Walk = new Dictionary<(string, string), int>
        {
            [("c1", "clone")] = 20, [("clone", "clone")] = 20, [("c1", "bonus")] = 150, [("clone", "bonus")] = 150,
            [("c1", "target")] = 300, [("clone", "target")] = 300, [("bonus", "target")] = 300,
        };

        static int Ticks(string a, string b) => a == b && a != "clone" ? 0 : Walk.TryGetValue((a, b), out int t) || Walk.TryGetValue((b, a), out t) ? t : 300;

        static IEnumerable<PlanOption> Options(Toy toy, string place, bool stand = false)
        {
            yield return new PlanOption(new Step("lap", "lap:1", Ticks(place, "c1") + 100), "c1", Array.Empty<string>(), "1", false, new[] { "start:1", "finish:1" });
            if (toy.Clones)
                yield return new PlanOption(new Step("buy", "buy:clone", Ticks(place, "clone")), "clone", new[] { "clone" }, null, false, new[] { "buy:clone" });
            if (toy.Capture().Mult == 1)
                yield return new PlanOption(new Step("reward", "reward:bonus", Ticks(place, "bonus")), "bonus", new[] { "bonus" }, null, false, new[] { "reward:bonus" });
            yield return new PlanOption(new Step("target", "buy:target", Ticks(place, "target")), "target", new[] { "target" }, null, true, new[] { "buy:target" }, Stand: stand);
        }

        /// <summary>The beam's estimate on the toy: the target's price until it's bought, and the walk to it.</summary>
        static (double[] price, int ticks) Remaining(Toy toy, string place) => toy.Capture().Targets > 0 ? (new[] { 0.0 }, 0) : (new[] { toy.Cost }, Ticks(place, "target"));

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

            // Brute force: every order of up to 12 steps, each step played as the planner plays it (stand: the target stands at its
            // box for its last Cash, as the beam's does).
            (long best, string order, int orders) Brute(bool stand)
            {
                long most = long.MaxValue;
                string first = null;
                int count = 0;
                void Explore(ToySnap at, string place, long tick, int depth, List<string> order)
                {
                    if (depth == 12 || tick >= most) return;
                    toy.Restore(at);
                    foreach (PlanOption o in Options(toy, place, stand).ToList())
                    {
                        toy.Restore(at);
                        long? end = EconomyPlan.Play(toy, tick, o);
                        if (end == null) continue;
                        order.Add(o.Step.Arg);
                        count++;
                        if (o.Target)
                        {
                            if (end.Value < most) { most = end.Value; first = string.Join(", ", order); }
                        }
                        else Explore(toy.Capture(), o.End, end.Value, depth + 1, order);
                        order.RemoveAt(order.Count - 1);
                    }
                }
                Explore(new Toy().Capture(), "c1", 0, 0, new List<string>());
                return (most, first, count);
            }
            var (best, bestOrder, orders) = Brute(false);
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
            // A capped search finishes its richest states by standing at the target, each asked from its own state: the stand is
            // offered only while the target isn't bought, so one asked from the last completion's state (bought) is never offered.
            toy.Restore(new Toy().Capture());
            PlanOption Stand(string p) => toy.Capture().Targets > 0 ? null
                : new PlanOption(new Step("target", "buy:target", Ticks(p, "target")), "target", new[] { "target" }, null, true, new[] { "buy:target" }, Stand: true);
            PlanResult capped = EconomyPlan.Search(toy, 0, "c1", p => Options(toy, p), 60, 60, stand: Stand);
            toy.Restore(new Toy().Capture());
            (t, at, plays) = (0, "c1", true);
            foreach (Step s in capped.Steps)
            {
                PlanOption o = Options(toy, at).FirstOrDefault(x => x.Step.Arg == s.Arg && !x.Target) ?? Stand(at);
                long? end = o == null ? null : EconomyPlan.Play(toy, t, o);
                if (end == null) { plays = false; break; }
                (t, at) = (end.Value, o.End);
            }
            Check(capped.Done && !capped.Proven && capped.Completed > 1 && plays && t == capped.Tick,
                $"capped at 60 states: {capped.Completed} states finished by standing at the target, the best on tick {capped.Tick} (played again: {t}), not proven");

            // The beam (--mode beam): a plan played again reaches its tick; never proven.
            (bool plays, long tick) Replay(PlanResult r, bool stand)
            {
                toy.Restore(new Toy().Capture());
                long tick = 0;
                string place = "c1";
                foreach (Step s in r.Steps)
                {
                    PlanOption o = Options(toy, place, stand).FirstOrDefault(x => x.Step.Arg == s.Arg);
                    long? end = o == null ? null : EconomyPlan.Play(toy, tick, o);
                    if (end == null) return (false, tick);
                    (tick, place) = (end.Value, o.End);
                }
                return (true, tick);
            }
            PlanResult Beam(int width, bool stand)
            {
                toy.Restore(new Toy().Capture());
                return EconomyPlan.Search(toy, 0, "c1", p => Options(toy, p, stand), 1000000, 60, width: width, remaining: p => Remaining(toy, p));
            }
            // Wide, on the same steps as the search above: the brute force's optimum.
            PlanResult wide = Beam(1000, false);
            var again = Replay(wide, false);
            Check(wide.Done && !wide.Proven && wide.Tick == best && again.plays && again.tick == wide.Tick,
                $"beam 1000: tick {wide.Tick} ({wide.Nodes} let in, {wide.Pruned} cut, {wide.Prechecked} pre-checked), the brute force's {best}, played again {again.tick}, not proven");
            // With the target's stand (the beam's default on the game): the brute force over the same steps.
            var (standBest, standOrder, _) = Brute(true);
            PlanResult wideStand = Beam(1000, true);
            again = Replay(wideStand, true);
            Check(wideStand.Done && wideStand.Tick == standBest && again.plays && again.tick == wideStand.Tick,
                $"beam 1000 with the target's stand: tick {wideStand.Tick} ({string.Join(", ", wideStand.Steps.Select(x => x.Arg))}), the brute force's {standBest} ({standOrder}), played again {again.tick}");
            // Width 1: still a plan that plays, no sooner than the optimum.
            PlanResult narrow = Beam(1, true);
            again = Replay(narrow, true);
            Check(narrow.Done && narrow.Tick >= standBest && again.plays && again.tick == narrow.Tick && narrow.Pruned > 0,
                $"beam 1: tick {narrow.Tick} ({narrow.Steps.Length} steps, {narrow.Steps.Count(x => x.Kind == "lap")} of them laps), {narrow.Pruned} cut by the width, played again {again.tick}, the optimum {standBest}");
            // Income only from laps (no clone box) and a target of 100: the optimum walks to the bonus first, 150 ticks earning
            // nothing. The beam keeps such a state (ranked on its fallback income), so it still finds it.
            (toy.Cost, toy.Clones) = (100, false);
            var (poorBest, poorOrder, _) = Brute(true);
            PlanResult poor = Beam(1000, true);
            again = Replay(poor, true);
            Check(poorOrder != null && poorOrder.StartsWith("reward:bonus", StringComparison.Ordinal) && poor.Done && poor.Tick == poorBest && again.plays && again.tick == poor.Tick,
                $"laps only, target 100: beam 1000 tick {poor.Tick} ({string.Join(", ", poor.Steps.Select(x => x.Arg))}; {poor.NoIncome} estimates without income), the brute force's {poorBest} ({poorOrder})");
            (toy.Cost, toy.Clones) = (2000, true);

            // Area 2's steps (Play): all:box buys every level paid for now, a goal each; atoms:N saves its N levels' price before the
            // touch; a patient stand gives up once nothing it lacks comes in (no clones: no income), long before StandCap.
            toy.Restore(new ToySnap(100, 0, 1, 0, 100));
            var all = new PlanOption(new Step("all", "all:clone", 0), "clone", new[] { "clone" }, null, false, new[] { "buy:clone" }, All: true);
            long? played = EconomyPlan.Play(toy, 0, all, out int extra);
            Check(played == 3 && extra == 2 && toy.Capture().Clones == 3, $"all:clone from 100 Cash: {1 + extra} levels on tick {played}, {toy.Capture().Clones} clones (3, tick 3)");
            toy.Restore(new ToySnap(0, 2, 1, 0, 0));
            var save = new PlanOption(new Step("atoms", "atoms:3", 0), "clone", new[] { "clone" }, null, false, new[] { "buy:clone" }, Stand: true, Levels: 3);
            played = EconomyPlan.Play(toy, 0, save, out _);
            Check(played == 46 && toy.Capture().Clones == 3 && toy.Capture().Cash == 62, $"atoms:3 on 2 a tick: touched on tick {played} with 3 levels' 90 saved ({toy.Capture().Cash} left; 46, 62)");
            toy.Restore(new ToySnap(0, 0, 1, 0, 0));
            toy.Played = 0;
            var stand = new PlanOption(new Step("target", "buy:target", 0), "target", new[] { "target" }, null, true, new[] { "buy:target" }, Stand: true, Patient: true);
            played = EconomyPlan.Play(toy, 0, stand);
            Check(played == null && toy.Played <= 1000, $"a patient stand with no income: {(played == null ? "gave up" : "bought on " + played)} after {toy.Played} ticks (at most 1000)");
            Console.WriteLine(fails == 0 ? "plan-selftest: PASS" : $"plan-selftest: {fails} FAIL");
            return fails == 0 ? 0 : 1;
        }
    }
}
