using System;
using System.Collections.Generic;
using System.Linq;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    // --parts idle, the RNG under the box (docs/fulltas.md, "Parts" item 7): a wait for Cash is time the player can spend drawing
    // the RNG. The clones' spawn rolls (Economy.RollCount) draw it, and so do the player's own sounds: a jump 2 (playSFX's clip and
    // pitch), a landing's footstep 2 (Audio.PlayAudio with pitch variance; 5 after 0.8 s in the air), a quick restart, a dash, a
    // wall slide. So hops under the box in a wait set how many draws come before each later clone roll, and with them every roll
    // after: a lottery ticket per RNG class. Each class is played whole with the later waits re-timed to their Cash (Retune),
    // and the earliest end is kept.
    sealed partial class FullTas
    {
        /// <summary>Retune adds at most this many idle ticks to a wait that no longer reaches its goals; a site whose goals are lost
        /// takes back at most MaxMerge sites before it.</summary>
        const int RetuneCap = 60, MaxMerge = 3;
        /// <summary>The RNG classes played whole per wait, at most.</summary>
        const int MaxTickets = 40, HopSamples = 300, HopApart = 20;

        /// <summary>
        /// <paramref name="inputs"/> (<paramref name="b"/>'s from its goal <paramref name="start"/> on, 0-based, moved by the count
        /// difference) with every wait from there re-timed to its Cash, on <paramref name="sim"/> from <paramref name="origin"/>. A
        /// site is a goal (a buy, a lap's start) with a quick restart since the goal before, its wait the idle ticks before that restart (the restart puts
        /// the player back as the run had it, so only the clock and the Cash move). Each site takes the cut whose touch of the goal
        /// before the next site is soonest (every goal up to it reached), or, when none reaches them, the fewest idle ticks added
        /// (up to RetuneCap); a site whose goals are lost either way takes the site before back and checks that one's cut through
        /// them too. Returns the whole run replayed, or null when a goal is lost.
        /// </summary>
        Route Retune(Simulation sim, SimSnapshot origin, Route b, List<InputTick> inputs, int start)
        {
            inputs = new List<InputTick>(inputs);
            int shift = inputs.Count - b.Inputs.Count;
            int Old(int g) => g < 0 ? 0 : (int)b.GoalInput[g];
            // b's last restart since the goal before: a restart's input moves only with the cuts before it, while a touch can come
            // on another input as the Cash does.
            int LastReset(int g) { for (int i = Old(g) - 1; i > Old(g - 1); i--) if (b.Inputs[i].reset) return i; return -1; }
            var sites = Enumerable.Range(start, _goals - start).Where(g => LastReset(g) > 0).Append(_goals).ToList();
            int at = start == 0 ? 0 : Old(start - 1) + shift;
            sim.SetStart(origin, 0);
            sim.Goals.Limit = _goals;
            SimSnapshot snap = at == 0 ? origin : sim.Advance(inputs.GetRange(0, at));
            (int k, int at, SimSnapshot snap, List<InputTick> inputs, int shift, int extra)? before = null;
            int extra = 0;
            for (int k = 0; k < sites.Count && snap != null;)
            {
                int g = sites[k], limit = g == _goals ? _goals : sites[Math.Min(k + 1 + extra, sites.Count - 1)];
                int reset = g < _goals && LastReset(g) + shift > at ? LastReset(g) + shift : -1, idle = 0;
                if (reset > 0) while (reset - idle - 1 >= at && Idle(inputs[reset - idle - 1])) idle++;
                // Two quick restarts on ticks in a row are refused (README ### Rules 34): one idle tick stays between them.
                if (idle > 0 && reset - idle - 1 >= 0 && inputs[reset - idle - 1].reset) idle--;
                // The touch of goal limit - 1 (input, physics tick) with n idle ticks cut before the restart (n < 0: added), or -1 when
                // a goal up to it is missed.
                (long input, long physics) Reach(int n)
                {
                    int stop = Math.Min(inputs.Count, Old(limit - 1) + shift + 200);
                    List<InputTick> list = inputs.GetRange(at, stop - at);
                    if (n > 0) list.RemoveRange(reset - n - at, n);
                    else if (n < 0) list.InsertRange(reset - at, Enumerable.Repeat(new InputTick { held = inputs[reset - 1].held && !inputs[reset - 1].release }, -n));
                    sim.SetStart(snap, at);
                    sim.Goals.Limit = limit;
                    // Extra ticks: a run cut where its inputs end can stop short of a goal its inputs reach (a reward's).
                    RunResult rr = sim.Run(list, null, null);
                    return rr.Progress >= limit && (rr.GoalInputTicks[limit - 1] < at + list.Count || stop == inputs.Count) ? (rr.GoalInputTicks[limit - 1], rr.GoalPhysicsTicks[limit - 1]) : (-1, -1);
                }
                int pick = 0;
                var (touch, soonest) = Reach(0);
                for (int n = 1; n <= idle; n++)
                {
                    var (i, t) = Reach(n);
                    if (i >= 0 && (touch < 0 || t < soonest)) (pick, touch, soonest) = (n, i, t);
                }
                for (int m = 1; reset > 0 && m <= RetuneCap && touch < 0; m++) (pick, (touch, soonest)) = (-m, Reach(-m));
                if (touch < 0)
                {
                    if (before == null || before.Value.extra >= MaxMerge) return null;
                    (k, at, snap, inputs, shift, extra) = before.Value;
                    extra++;
                    before = null;
                    continue;
                }
                before = (k, at, snap, new List<InputTick>(inputs), shift, extra);
                if (pick > 0) inputs.RemoveRange(reset - pick, pick);
                else if (pick < 0) inputs.InsertRange(reset, Enumerable.Repeat(new InputTick { held = inputs[reset - 1].held && !inputs[reset - 1].release }, -pick));
                shift -= pick;
                if (limit == _goals) break;
                sim.SetStart(snap, at);
                sim.Goals.Limit = _goals;
                snap = sim.Advance(inputs.GetRange(at, (int)touch - at));
                at = (int)touch;
                k += 1 + extra;
                extra = 0;
            }
            if (snap == null) return null;
            sim.SetStart(origin, 0);
            sim.Goals.Limit = _goals;
            RunResult whole = sim.Run(inputs, null, null, 1000000);
            return new Route { Inputs = inputs, Progress = whole.Progress, GoalInput = whole.GoalInputTicks, GoalPhysics = whole.GoalPhysicsTicks, GoalHeld = whole.GoalHeldFrames, EndReason = whole.EndReason };
        }

        /// <summary>
        /// Goal g's wait for Cash spent drawing the RNG: <paramref name="run"/> (a whole run of <paramref name="current"/>'s goals)
        /// has idle ticks at <paramref name="ws"/>..<paramref name="we"/> where the player stands within reach of the box's trigger
        /// (<paramref name="state"/>: the run's state after <paramref name="stateAt"/> inputs, at or before ws). Hops go there
        /// (each a sound's draws on its own ticks); the ones that leave the player on the ground within reach of the trigger at we
        /// are sorted into RNG classes (the RNG's state there), and each class (up to MaxTickets, the plain wait first) is played
        /// whole with Retune from goal g + 1 on every thread. A class counts only when it still buys the box by physics tick
        /// <paramref name="touchBy"/> (the plain wait's: the jump in comes as the Cash does). Returns the fastest run and its hops,
        /// or null when none completes.
        /// </summary>
        (Route, string) RngTickets(Route current, int g, List<InputTick> run, int ws, int we, SimSnapshot state, int stateAt, Zone zone, long touchBy)
        {
            // Hops go on released idle ticks (a jump let go on the first one keeps its release).
            if (ws < we && run[ws].release) ws++;
            if (we - ws < 2 || run.Skip(ws).Take(we - ws).Any(t => t.held)) return (null, null);
            int w = we - ws;
            // Under a box a hop bonks its underside whatever the hold (about 20 ticks up and down), and a hop's draws only make
            // another class where a clone roll falls between them and the next hop's: every single hop, then HopSamples random
            // sets of hops HopApart ticks apart (the same for the same goal).
            var schedules = new List<(int at, int hold)[]> { new (int, int)[0] };
            for (int a = 0; a + 2 <= w; a++) schedules.Add(new[] { (a, 1) });
            var random = new Random(g);
            for (int n = 0; n < HopSamples && w >= 2 * HopApart; n++)
            {
                var hops = new List<(int, int)>();
                for (int a = random.Next(HopApart); a + 2 <= w; a += HopApart + random.Next(w / 2)) hops.Add((a, 1));
                if (hops.Count > 1) schedules.Add(hops.ToArray());
            }
            List<InputTick> Hopped((int at, int hold)[] hops)
            {
                var list = new List<InputTick>(run);
                foreach (var (a, hold) in hops)
                    for (int i = 0; i <= hold && ws + a + i < we; i++)
                        list[ws + a + i] = i == 0 ? new InputTick { held = true, press = true } : i < hold ? new InputTick { held = true } : new InputTick { release = true };
                return list;
            }
            // The classes: the RNG's state at we (and where the player is), with the player on the ground within reach.
            var seen = new HashSet<(int, int, int, int, float)>();
            var tickets = new List<(List<InputTick> inputs, string how)>();
            foreach (var hops in schedules)
            {
                if (tickets.Count >= MaxTickets || TimeUp) break;
                List<InputTick> list = Hopped(hops);
                bool ok = false;
                UnityEngine.Random.State rng = default;
                float x = 0f;
                _eval.SetStart(state, stateAt);
                _eval.Goals.Limit = _goals;
                int count = we - stateAt;
                _eval.Run(list.GetRange(stateAt, count + 1), null, tick =>
                {
                    if (tick != count) return true;
                    x = _eval.PlayerWorld.x;
                    ok = _eval.Player.onGround && zone.Near(x);
                    rng = UnityEngine.Random.state;
                    return false;
                }, 0);
                if (!ok || !seen.Add((rng.s0, rng.s1, rng.s2, rng.s3, (float)Math.Round(x)))) continue;
                tickets.Add((list, hops.Length == 0 ? "no hop" : string.Join(" and ", hops.Select(h => $"a {h.hold}-tick jump {h.at} ticks in"))));
            }
            if (tickets.Count == 0) return (null, null);
            var results = new Route[tickets.Count];
            Solve.Workers(_threads, tickets.Count, () => { Simulation sim = _create(); return (sim, sim.Start); },
                (s, i) => results[i] = Retune(s.sim, s.Start, current, tickets[i].inputs, g + 1))();
            int best = -1;
            for (int i = 0; i < results.Length; i++)
                if (results[i] != null && results[i].Progress == _goals && results[i].GoalPhysics[g] <= touchBy && (best < 0 || results[i].Live < results[best].Live)) best = i;
            int done = results.Count(r => r != null && r.Progress == _goals && r.GoalPhysics[g] <= touchBy), faster = results.Count(r => r != null && r.Progress == _goals && r.GoalPhysics[g] <= touchBy && r.Live < current.Live);
            Log($"auto: idle rng: goal {g + 1} ({_labels[g]}): {w} idle ticks at inputs {ws}-{we - 1}, {schedules.Count} hop schedules, {tickets.Count} RNG classes, "
                + $"{done} complete re-timed, {faster} faster than {Describe(current)}" + (best < 0 ? "" : $"; best {Describe(results[best])} ({tickets[best].how})"));
            return best < 0 ? (null, null) : (results[best], $"{tickets[best].how} in {w} idle ticks under the box, the waits after re-timed");
        }

        /// <summary>
        /// The run re-timed from each site on (Retune from every buy with a quick restart since the goal before, on every thread):
        /// the fastest when it beats the run, else null. A re-timing from one site on falls on the clones' rolls another way than
        /// from the next, so each start is its own ticket.
        /// </summary>
        Route RetuneSweep(Route best)
        {
            double c0 = CpuSeconds();
            var starts = Enumerable.Range(1, _goals - 1).Where(g => Enumerable.Range((int)best.GoalInput[g - 1] + 1, Math.Max(0, (int)(best.GoalInput[g] - best.GoalInput[g - 1]) - 1)).Any(i => best.Inputs[i].reset)).ToList();
            var results = new Route[starts.Count];
            Solve.Workers(_threads, starts.Count, () => { Simulation sim = _create(); return (sim, sim.Start); },
                (s, i) => results[i] = Retune(s.sim, s.Start, best, best.Inputs, starts[i]))();
            int pick = -1;
            for (int i = 0; i < results.Length; i++)
                if (results[i] != null && results[i].Progress == _goals && results[i].Live < best.Live && (pick < 0 || results[i].Live < results[pick].Live)) pick = i;
            Log($"auto: idle re-timing: {starts.Count} starts, {results.Count(r => r != null && r.Progress == _goals)} complete, {results.Count(r => r != null && r.Progress == _goals && r.Live < best.Live)} faster than {Describe(best)}"
                + (pick < 0 ? "" : $"; best from goal {starts[pick] + 1} ({_labels[starts[pick]]}): {Describe(results[pick])}") + $" in {CpuSeconds() - c0:0} CPU s");
            return pick < 0 ? null : results[pick];
        }

        /// <summary>
        /// Filler before a quick restart made standing: where a goal's stretch has more than one restart, everything before the last
        /// one (dashes away, restarts, hops that only burn ticks for the Cash or the RNG) becomes idle ticks, as many, and the run is
        /// re-timed from that goal on (Retune, which cuts them to what the Cash needs). The last restart puts the player back as
        /// before, so the lap or buy after it plays as it did. Every such stretch is a ticket, on every thread; the fastest when it
        /// beats the run, else null.
        /// </summary>
        Route Defiller(Route best)
        {
            double c0 = CpuSeconds();
            var tickets = new List<(int k, int resets, List<InputTick> inputs)>();
            for (int k = 1; k < _goals; k++)
            {
                int a = (int)best.GoalInput[k - 1], b = (int)Math.Min(best.GoalInput[k], best.Inputs.Count);
                List<int> resets = Enumerable.Range(a, Math.Max(0, b - a)).Where(i => best.Inputs[i].reset).ToList();
                if (resets.Count < 2 || resets[0] == a) continue;
                int last = resets[resets.Count - 1];
                if (best.Inputs[last].held && !best.Inputs[last].press) continue;
                var list = new List<InputTick>(best.Inputs);
                bool held = a > 0 && best.Inputs[a - 1].held;
                for (int i = a; i < last; i++) list[i] = new InputTick { release = held && i == a };
                tickets.Add((k, resets.Count - 1, list));
            }
            if (tickets.Count == 0) return null;
            var results = new Route[tickets.Count];
            Solve.Workers(_threads, tickets.Count, () => { Simulation sim = _create(); return (sim, sim.Start); },
                (s, i) => results[i] = Retune(s.sim, s.Start, best, tickets[i].inputs, tickets[i].k))();
            int pick = -1;
            for (int i = 0; i < results.Length; i++)
                if (results[i] != null && results[i].Progress == _goals && results[i].Live < best.Live && (pick < 0 || results[i].Live < results[pick].Live)) pick = i;
            Log($"auto: idle filler: {tickets.Count} stretches with filler before their last quick restart, {results.Count(r => r != null && r.Progress == _goals)} complete standing, "
                + $"{results.Count(r => r != null && r.Progress == _goals && r.Live < best.Live)} faster than {Describe(best)}"
                + (pick < 0 ? "" : $"; best at goal {tickets[pick].k + 1} ({_labels[tickets[pick].k]}, {tickets[pick].resets} restarts dropped): {Describe(results[pick])}") + $" in {CpuSeconds() - c0:0} CPU s");
            return pick < 0 ? null : results[pick];
        }

        /// <summary>The longest walk to a box's trigger WalkIn looks at, in ticks.</summary>
        const int MaxWalkIn = 150;

        /// <summary>
        /// Goal g's buy walked to: from the touch of the goal before (input <paramref name="from"/>, <paramref name="at"/> its state),
        /// the stick toward the box's trigger until 12 idle ticks leave the player at rest under it, then idle ticks and a jump
        /// straight up that touches as the Cash comes (the fewest that touch), then the run's inputs from its old touch. What the run
        /// did in between (restarts and dashes to a far box, loops, jumps) goes, and the idle ticks under the box are the RNG's
        /// (RngTickets). Returns the fastest re-timed run and how, or null.
        /// </summary>
        (Route, string) WalkIn(Route current, int g, int from, int buy, SimSnapshot at)
        {
            if (at == null || buy <= from) return (null, null);
            bool held = from > 0 && current.Inputs[from - 1].held;
            Pose[] start = PoseTrace(at, from, new List<InputTick> { new InputTick { release = held } });
            Zone zone = BoxZone(_runGoals.Goals[g]);
            if (float.IsNaN(zone.x0)) return (null, null);
            var step = new InputTick { x = zone.Middle < start[1].x ? -1f : 1f };
            List<InputTick> Walk(int run, int idle)
            {
                var list = Enumerable.Repeat(step, run).Concat(Enumerable.Repeat(new InputTick(), idle)).ToList();
                if (held && list.Count > 0) { InputTick t = list[0]; t.release = true; list[0] = t; }
                return list;
            }
            int stand = -1;
            for (int run = 0; run <= MaxWalkIn && stand < 0; run++)
            {
                Pose rest = default;
                List<InputTick> list = Walk(run, RestTicks);
                _eval.SetStart(at, from);
                _eval.Goals.Limit = _goals;
                _eval.Run(list, null, tick =>
                {
                    if (tick == list.Count) rest = new Pose { ground = _eval.Player.onGround, x = _eval.PlayerWorld.x };
                    return true;
                }, 0);
                if (rest.ground && zone.Under(rest.x)) stand = run;
                else if (step.x < 0f && rest.x < zone.x0 || step.x > 0f && rest.x > zone.x1) break;
            }
            if (stand < 0) return (null, null);
            List<InputTick> Jump(int w)
            {
                List<InputTick> list = Walk(stand, w);
                list.Add(new InputTick { held = true, press = true });
                for (int i = 0; i < JumpFall; i++) list.Add(new InputTick { held = true });
                return list;
            }
            (long input, long physics) Play(List<InputTick> list)
            {
                _eval.SetStart(at, from);
                _eval.Goals.Limit = g + 1;
                RunResult rr = _eval.Run(list, null, null, 0);
                return rr.Progress >= g + 1 ? (rr.GoalInputTicks[g], rr.GoalPhysicsTicks[g]) : (-1, -1);
            }
            int low = FirstWait(w => Play(Jump(w)).input, buy - from + WaitSlack);
            if (low < 0) return (null, null);
            var (touch, physics) = Play(Jump(low));
            List<InputTick> tail = current.Inputs.Skip(buy).ToList();
            if (tail.Count > 0)
            {
                InputTick t0 = tail[0];
                if (!t0.held && !t0.release) { t0.release = true; tail[0] = t0; }
                else if (t0.held && !t0.press) return (null, null);
            }
            List<InputTick> whole = current.Inputs.Take(from).Concat(Jump(low).Take((int)touch - from)).Concat(tail).ToList();
            var (r, how) = RngTickets(current, g, whole, from + stand, from + stand + low, at, from, zone, physics);
            return r == null ? (null, null) : (r, $"walked in ({stand} ticks), {how}");
        }

        /// <summary>--rng-study: the best's waits for Cash and what draws the RNG in them, then the best re-timed (Retune); then stops.</summary>
        void RngStudy()
        {
            Route best = _best;
            // Every draw of the best by input and caller, and the clone rolls.
            var draws = new List<(int input, string caller)>();
            var rolls = new List<int>();
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            UnityEngine.Random.Drawn = caller => draws.Add(((int)_eval.Engine.RunTicks, caller));
            if (_eval.Shop.Economy != null) _eval.Shop.Economy.Spawned = (course, id, onScreen, divisor, i, j, pay) => rolls.Add((int)_eval.Engine.RunTicks);
            Pose[] poses = PoseTrace(_origin, 0, best.Inputs);
            UnityEngine.Random.Drawn = null;
            if (_eval.Shop.Economy != null) _eval.Shop.Economy.Spawned = null;
            Log($"rng study: {draws.Count} draws, {rolls.Count} clone rolls; by caller: "
                + string.Join(", ", draws.GroupBy(d => d.caller).OrderByDescending(x => x.Count()).Select(x => $"{x.Key} {x.Count()}")));
            for (int g = 0; g < _goals; g++)
            {
                if (_runGoals.Goals[g].kind != FullTasGoal.Buy) continue;
                int from = g == 0 ? 0 : (int)best.GoalInput[g - 1], buy = (int)best.GoalInput[g];
                for (int i = from; i < buy;)
                {
                    if (!Idle(best.Inputs[i])) { i++; continue; }
                    int j = i;
                    while (j < buy && Idle(best.Inputs[j])) j++;
                    if (j - i >= IdleWaitRun)
                    {
                        int ground = Enumerable.Range(i + 1, j - i).Count(t => poses[t].ground);
                        Zone zone = BoxZone(_runGoals.Goals[g]);
                        int near = Enumerable.Range(i + 1, j - i).Count(t => poses[t].ground && zone.Near(poses[t].x) && poses[t].vx == 0f);
                        var inWait = draws.Where(d => d.input >= i && d.input < j).GroupBy(d => d.caller).Select(x => $"{x.Key} {x.Count()}");
                        Log($"rng study: goal {g + 1} ({_labels[g]}): wait inputs {i}-{j - 1} ({j - i}), {ground} on the ground, {near} at rest within reach of the trigger (x {zone.x0:0}-{zone.x1:0}, y {zone.y0:0}), ends at ({poses[j].x:0},{poses[j].y:0})"
                            + $"{(j < best.Inputs.Count && best.Inputs[j].reset ? ", then a quick restart" : "")}; clone rolls in it {rolls.Count(t => t >= i && t < j)}; draws: {string.Join(", ", inWait)}");
                    }
                    i = j;
                }
            }
            double c0 = CpuSeconds();
            Route re = Retune(_eval, _origin, best, best.Inputs, 0);
            Log($"rng study: the best re-timed: {(re == null ? "a goal is lost" : Describe(re))} against {Describe(best)} in {CpuSeconds() - c0:0.0} CPU s");
        }
    }
}
