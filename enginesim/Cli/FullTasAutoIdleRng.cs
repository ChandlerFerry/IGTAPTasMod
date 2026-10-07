using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    // --parts idle, the RNG under the box (docs/fulltas.md, "Parts" item 7): a wait for Cash is time the player can spend drawing
    // the RNG. The clones' spawn rolls (Economy.RollCount) draw it, and so do the player's own sounds: a jump 2 (playSFX's clip and
    // pitch), a landing's footstep 2 (Audio.PlayAudio with pitch variance; 5 after 0.8 s in the air), a quick restart, a dash, a
    // wall slide. On build 25640326 an on-screen course with a full pool rolls on every frame its timer is past due (SpawnClone
    // runs, and a spawn only takes the interval off it: 2 draws a frame, more on a success, 6 frames a physics tick), and the
    // pool banks the timer, about 1.5 frames a lap, until it rolls on every frame (Courses 1 and 2 within about a minute); a roll
    // on a frame with no free slot is drawn and thrown away. So any extra draw moves which rolls
    // fall on the frames that spawn, from then on; before, only a roll between a hop's jump and landing did. Each hop schedule
    // is a lottery ticket (its class: the RNG's state where the wait ends); each class is played whole with the later waits
    // re-timed to their Cash (Retune), and the earliest end is kept.
    sealed partial class FullTas
    {
        /// <summary>Retune adds at most this many idle ticks to a wait that no longer reaches its goals; a site whose goals are lost
        /// takes back at most MaxMerge sites before it.</summary>
        const int RetuneCap = 60, MaxMerge = 3;
        /// <summary>The RNG classes played whole per wait, at most.</summary>
        const int MaxTickets = 40, HopSamples = 300, HopApart = 20;
        /// <summary>--rng-study --census: RngTickets sorts its schedules into classes and logs them, and plays nothing whole.</summary>
        bool _rngCensus;

        /// <summary>
        /// <paramref name="inputs"/> (<paramref name="b"/>'s from its goal <paramref name="start"/> on, 0-based, moved by the count
        /// difference) with every wait from there re-timed to its Cash, on <paramref name="sim"/> from <paramref name="origin"/>. A
        /// site is a goal (a buy, a lap's start) with a quick restart since the goal before, its wait the idle ticks before that restart (the restart puts
        /// the player back as the run had it, so only the clock and the Cash move). Each site takes the cut whose touch of the goal
        /// before the next site is soonest (every goal up to it reached), or, when none reaches them, the fewest idle ticks added
        /// (up to RetuneCap); a site whose goals are lost either way takes the site before back and checks that one's cut through
        /// them too. Returns the whole run replayed, or null when a goal is lost. <paramref name="goals"/>: the goal count of the
        /// simulation's goals when they aren't the run's (a goals edit's).
        /// </summary>
        Route Retune(Simulation sim, SimSnapshot origin, Route b, List<InputTick> inputs, int start, int goals = 0) => Retune(sim, origin, b, inputs, start, out _, goals);

        /// <param name="lost">When null is returned: which goal is lost, at which wait, what was tried, and why the best try ended.</param>
        Route Retune(Simulation sim, SimSnapshot origin, Route b, List<InputTick> inputs, int start, out string lost, int goals = 0)
        {
            lost = null;
            int count = goals > 0 ? goals : _goals;
            inputs = new List<InputTick>(inputs);
            int shift = inputs.Count - b.Inputs.Count;
            int Old(int g) => g < 0 ? 0 : (int)b.GoalInput[g];
            // b's last restart since the goal before: a restart's input moves only with the cuts before it, while a touch can come
            // on another input as the Cash does.
            int LastReset(int g) { for (int i = Math.Min(Old(g), b.Inputs.Count) - 1; i > Old(g - 1); i--) if (b.Inputs[i].reset) return i; return -1; }
            var sites = Enumerable.Range(start, count - start).Where(g => LastReset(g) > 0).Append(count).ToList();
            int at = start == 0 ? 0 : Old(start - 1) + shift;
            sim.SetStart(origin, 0);
            sim.Goals.Limit = count;
            SimSnapshot snap = at == 0 ? origin : sim.Advance(inputs.GetRange(0, at));
            (int k, int at, SimSnapshot snap, List<InputTick> inputs, int shift, int extra)? before = null;
            int extra = 0;
            for (int k = 0; k < sites.Count && snap != null;)
            {
                // The try that reached the most goals (n idle ticks cut, n < 0: added), for the message when the site fails.
                (int n, int progress, string reason) best = (0, -1, null);
                int g = sites[k], limit = g == count ? count : sites[Math.Min(k + 1 + extra, sites.Count - 1)];
                int reset = g < count && LastReset(g) + shift > at ? LastReset(g) + shift : -1, idle = 0;
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
                    if (rr.Progress > best.progress) best = (n, rr.Progress, rr.EndReason);
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
                    if (before == null || before.Value.extra >= MaxMerge)
                    {
                        int p = Math.Min(best.progress, limit - 1);
                        string why = StrayBox(best.reason) != null ? "stray buy: needs a goal fit (move, insert or drop a goal), waits can't drop it"
                            : reset <= 0 ? $"no wait site before this goal (no quick restart since input {at}): only a wait at an earlier site can fix it"
                            : p > g && LastReset(p) <= 0 ? $"no wait site before this goal (its walk has no quick restart since goal {p}): only a wait at an earlier site can fix it, and this one needs more than {RetuneCap} idle ticks"
                            : $"needs more than RetuneCap ({RetuneCap}) idle ticks here";
                        lost = $"goal {p + 1} ({sim.Goals.Goals[p].label}) is lost at "
                            + (reset > 0 ? $"the wait before the quick restart at input {reset} (goal {g + 1}'s site; {idle} idle ticks cut to {RetuneCap} added tried"
                                : $"goal {g + 1}'s site (no wait to retime: the route as it is tried") + (extra > 0 ? $"; through goal {limit}, {extra} later site(s) taken back" : "")
                            + $"); the try reaching the most ({(best.n < 0 ? -best.n + " added" : best.n + " cut")}) reaches {best.progress} goals: {best.reason}; {why}";
                        return null;
                    }
                    (k, at, snap, inputs, shift, extra) = before.Value;
                    extra++;
                    before = null;
                    continue;
                }
                before = (k, at, snap, new List<InputTick>(inputs), shift, extra);
                if (pick > 0) inputs.RemoveRange(reset - pick, pick);
                else if (pick < 0) inputs.InsertRange(reset, Enumerable.Repeat(new InputTick { held = inputs[reset - 1].held && !inputs[reset - 1].release }, -pick));
                shift -= pick;
                if (limit == count) break;
                sim.SetStart(snap, at);
                sim.Goals.Limit = count;
                snap = sim.Advance(inputs.GetRange(at, (int)touch - at));
                at = (int)touch;
                k += 1 + extra;
                extra = 0;
            }
            if (snap == null)
            {
                lost = $"the replay to input {at} (the last goal touched) ends early";
                return null;
            }
            sim.SetStart(origin, 0);
            sim.Goals.Limit = count;
            RunResult whole = sim.Run(inputs, null, null, 1000000);
            return new Route { Inputs = inputs, Progress = whole.Progress, GoalInput = whole.GoalInputTicks, GoalPhysics = whole.GoalPhysicsTicks, GoalHeld = whole.GoalHeldFrames, EndReason = whole.EndReason };
        }

        /// <summary>Hop schedules equal when their hops are.</summary>
        sealed class ScheduleEquality : IEqualityComparer<(int at, int hold)[]>
        {
            public bool Equals((int at, int hold)[] a, (int at, int hold)[] b) { return a.SequenceEqual(b); }
            public int GetHashCode((int at, int hold)[] a) { return a.Aggregate(17, (h, t) => h * 31 + t.at * 7 + t.hold); }
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
        (Route, string) RngTickets(Route current, int g, List<InputTick> run, int ws, int we, SimSnapshot state, int stateAt, Zone zone, long touchBy, bool fixTail = false, int retuneFrom = -1, bool census = false)
        {
            // Hops go on released idle ticks (a jump let go on the first one keeps its release).
            if (ws < we && run[ws].release) ws++;
            if (we - ws < 2 || run.Skip(ws).Take(we - ws).Any(t => t.held)) return (null, null);
            int w = we - ws;
            // Under a box a hop bonks its underside whatever the hold (about 20 ticks up and down). With a roll on every frame
            // nearly every schedule is its own class, so the MaxTickets played are taken in turns from the single hops (every
            // tick) and HopSamples random sets of hops HopApart ticks apart (the same for the same goal), not all singles first.
            var singles = Enumerable.Range(0, Math.Max(0, w - 1)).Select(a => new[] { (a, 1) }).ToList();
            var sets = new List<(int at, int hold)[]>();
            var random = new Random(g);
            for (int n = 0; n < HopSamples && w >= 2 * HopApart; n++)
            {
                var hops = new List<(int, int)>();
                for (int a = random.Next(HopApart); a + 2 <= w; a += HopApart + random.Next(w / 2)) hops.Add((a, 1));
                if (hops.Count > 1) sets.Add(hops.ToArray());
            }
            var schedules = new List<(int at, int hold)[]> { new (int, int)[0] };
            for (int i = 0; i < Math.Max(singles.Count, sets.Count); i++)
            {
                if (i < singles.Count) schedules.Add(singles[i * (singles.Count % 7 == 0 ? 1 : 7) % singles.Count]);
                if (i < sets.Count) schedules.Add(sets[i]);
            }
            schedules = schedules.Distinct(new ScheduleEquality()).ToList();
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
                if (!(census || _rngCensus) && tickets.Count >= MaxTickets || TimeUp) break;
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
                    // A hop that buys the box would take its levels (a buy-max box's come in one a tick while the player is in
                    // the trigger) away from the jump in: the box stays unbought until the wait ends.
                    ok = _eval.Player.onGround && (census || zone.Near(x) && _eval.Progress <= g);
                    rng = UnityEngine.Random.state;
                    return false;
                }, 0);
                if (!ok || !seen.Add((rng.s0, rng.s1, rng.s2, rng.s3, (float)Math.Round(x)))) continue;
                tickets.Add((list, hops.Length == 0 ? "no hop" : string.Join(" and ", hops.Select(h => $"a {h.hold}-tick jump {h.at} ticks in"))));
            }
            if (census || _rngCensus)
            {
                Log($"rng study: goal {g + 1} ({_labels[g]}): {w} idle ticks under the box at inputs {ws}-{we - 1}: {schedules.Count} hop schedules, {tickets.Count} RNG classes");
                return (null, null);
            }
            if (tickets.Count == 0) return (null, null);
            var results = new Route[tickets.Count];
            Solve.Workers(_threads, tickets.Count, () => { Simulation sim = _create(); return (sim, sim.Start); },
                (s, i) => results[i] = Retune(s.sim, s.Start, current, tickets[i].inputs, retuneFrom < 0 ? g + 1 : retuneFrom))();
            string fixedHow = "";
            if (fixTail && !results.Any(r => r != null && r.Progress == _goals))
            {
                // The way on from the new touch re-found (TailFix) from the plain wait's, then every class's wait with that way on.
                Route fixedRun = TailFix(current, tickets[0].inputs, g);
                if (fixedRun != null)
                {
                    int at = (int)fixedRun.GoalInput[Math.Max(g, (retuneFrom < 0 ? g + 1 : retuneFrom) - 1)];
                    results[0] = fixedRun;
                    Solve.Workers(_threads, tickets.Count - 1, () => { Simulation sim = _create(); return (sim, sim.Start); },
                        (s, i) => results[i + 1] = Retune(s.sim, s.Start, fixedRun, tickets[i + 1].inputs.Take(at).Concat(fixedRun.Inputs.Skip(at)).ToList(), retuneFrom < 0 ? g + 1 : retuneFrom))();
                    fixedHow = ", the way on re-found";
                }
            }
            int best = -1;
            for (int i = 0; i < results.Length; i++)
                if (results[i] != null && results[i].Progress == _goals && results[i].GoalPhysics[g] <= touchBy && (best < 0 || results[i].Live < results[best].Live)) best = i;
            int done = results.Count(r => r != null && r.Progress == _goals && r.GoalPhysics[g] <= touchBy), faster = results.Count(r => r != null && r.Progress == _goals && r.GoalPhysics[g] <= touchBy && r.Live < current.Live);
            Log($"auto: idle rng: goal {g + 1} ({_labels[g]}): {w} idle ticks at inputs {ws}-{we - 1}, {schedules.Count} hop schedules, {tickets.Count} RNG classes, "
                + $"{done} complete re-timed, {faster} faster than {Describe(current)}" + (best < 0 ? "" : $"; best {Describe(results[best])} ({tickets[best].how})")
                + (done > 0 ? "" : $"; the plain wait as it is: {Describe(Evaluate(tickets[0].inputs))} ({Evaluate(tickets[0].inputs).EndReason})"));
            return best < 0 ? (null, null) : (results[best], $"{tickets[best].how} in {w} idle ticks under the box{fixedHow}, the waits after re-timed");
        }

        /// <summary>
        /// The run re-timed from each site on (Retune from every buy with a quick restart since the goal before, on every thread):
        /// the fastest when it beats the run, else null. A re-timing from one site on falls on the clones' rolls another way than
        /// from the next, so each start is its own ticket.
        /// </summary>
        Route RetuneSweep(Route best)
        {
            double c0 = CpuSeconds();
            // A goal reached in the no-input ticks after the last input (a dash) has its input tick at the inputs' end.
            var starts = Enumerable.Range(1, _goals - 1).Where(g => InIdleReach(g + 1)).Where(g =>
            {
                int from = (int)best.GoalInput[g - 1] + 1, to = (int)Math.Min(best.GoalInput[g], best.Inputs.Count);
                return Enumerable.Range(from, Math.Max(0, to - from)).Any(i => best.Inputs[i].reset);
            }).ToList();
            var results = new Route[starts.Count];
            Solve.Workers(_threads, starts.Count, () => { Simulation sim = _create(); return (sim, sim.Start); },
                (s, i) => results[i] = Retune(s.sim, s.Start, best, best.Inputs, starts[i]))();
            int pick = -1;
            for (int i = 0; i < results.Length; i++)
                if (results[i] != null && results[i].Progress == _goals && LiveAhead(results[i], best) && (pick < 0 || LiveAhead(results[i], results[pick]))) pick = i;
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
                if (!InIdleReach(k + 1)) continue;
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
                if (results[i] != null && results[i].Progress == _goals && LiveAhead(results[i], best) && (pick < 0 || LiveAhead(results[i], results[pick]))) pick = i;
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
        (Route, string) WalkIn(Route current, int g, int from, int buy, SimSnapshot at, List<InputTick> lead = null, string leadHow = null)
        {
            if (at == null || buy <= from) return (null, null);
            lead ??= new List<InputTick>();
            bool held = lead.Count > 0 ? lead[lead.Count - 1].held : from > 0 && current.Inputs[from - 1].held;
            SimSnapshot afterLead = at;
            if (lead.Count > 0)
            {
                _eval.SetStart(at, from);
                _eval.Goals.Limit = _goals;
                afterLead = _eval.Advance(lead);
                if (afterLead == null) return (null, null);
            }
            Pose[] start = PoseTrace(afterLead, from + lead.Count, new List<InputTick> { new InputTick { release = held } });
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
                _eval.SetStart(afterLead, from + lead.Count);
                _eval.Goals.Limit = _goals;
                bool seen = false;
                // Past the list: a run stops with its inputs, and its last tick reports nothing then.
                _eval.Run(list, null, tick =>
                {
                    if (tick < list.Count) return true;
                    rest = new Pose { ground = _eval.Player.onGround, x = _eval.PlayerWorld.x, y = _eval.PlayerWorld.y };
                    seen = true;
                    return false;
                });
                if (!seen) break;
                if (rest.ground && zone.Under(rest.x) && rest.y < zone.y0) stand = run;
                else if (step.x < 0f && rest.x < zone.x0 || step.x > 0f && rest.x > zone.x1) break;
            }
            if (stand < 0)
            {
                if (leadHow != null) Log($"auto: stand window: goal {g + 1} ({_labels[g]}): no rest under the trigger (x {zone.x0:0}-{zone.x1:0}) from ({start[1].x:0},{start[1].y:0})");
                return (null, null);
            }
            // The jump in holds the stick the run held into the box: the way on after the buy starts from that motion.
            float stick = current.Inputs[Math.Max(from, buy - 1)].x;
            List<InputTick> Jump(int w)
            {
                List<InputTick> list = Walk(stand, w);
                list.Add(new InputTick { held = true, press = true, x = stick });
                for (int i = 0; i < JumpFall; i++) list.Add(new InputTick { held = true, x = stick });
                return list;
            }
            (long input, long physics) Play(List<InputTick> list)
            {
                _eval.SetStart(afterLead, from + lead.Count);
                _eval.Goals.Limit = g + 1;
                RunResult rr = _eval.Run(list, null, null, 0);
                return rr.Progress >= g + 1 ? (rr.GoalInputTicks[g], rr.GoalPhysicsTicks[g]) : (-1, -1);
            }
            int low = FirstWait(w => Play(Jump(w)).input, buy - from + WaitSlack);
            if (low < 0)
            {
                if (leadHow != null) Log($"auto: stand window: goal {g + 1} ({_labels[g]}): at rest under the trigger after {stand} ticks on, no jump touches");
                return (null, null);
            }
            var (touch, physics) = Play(Jump(low));
            // A buy-max box's levels (the goals after g with its label) come in one a physics tick while the player is in the
            // trigger: the jump in is kept on through as many of them as it reaches (the most first), and the run's inputs go on
            // from the last one's old touch.
            int levels = 0;
            while (g + levels + 1 < _goals && _labels[g + levels + 1] == _labels[g] && current.GoalInput[g + levels + 1] - current.GoalInput[g] <= levels + 1) levels++;
            for (; levels > 0; levels--)
            {
                List<InputTick> hold = Jump(low).Concat(Enumerable.Repeat(new InputTick { held = true, x = stick }, levels)).ToList();
                _eval.SetStart(afterLead, from + lead.Count);
                _eval.Goals.Limit = g + 1 + levels;
                RunResult rr = _eval.Run(hold, null, null, 0);
                if (rr.Progress < g + 1 + levels) continue;
                touch = rr.GoalInputTicks[g + levels];
                buy = (int)Math.Min(current.GoalInput[g + levels], current.Inputs.Count);
                break;
            }
            List<InputTick> tail = current.Inputs.Skip(buy).ToList();
            if (tail.Count > 0)
            {
                InputTick t0 = tail[0];
                if (!t0.held && !t0.release) { t0.release = true; tail[0] = t0; }
                else if (t0.held && !t0.press) return (null, null);
            }
            int on = from + lead.Count;
            List<InputTick> jumpIn = Jump(low).Concat(Enumerable.Repeat(new InputTick { held = true, x = stick }, levels)).ToList();
            List<InputTick> whole = current.Inputs.Take(from).Concat(lead).Concat(jumpIn.Take((int)touch - on)).Concat(tail).ToList();
            int ws = on + stand;
            var (r, how) = RngTickets(current, g, whole, ws, ws + low, afterLead, on, zone, physics, leadHow != null, g + levels + 1);
            return r == null ? (null, null) : (r, $"{leadHow ?? ""}{(leadHow == null ? "" : ", ")}walked in ({stand} ticks), {how}");
        }

        /// <summary>
        /// <paramref name="whole"/> (goal g reached, then a run of <paramref name="current"/>'s inputs that no longer follows on) with
        /// its way on re-found: up to 3 rounds of a window from the last goal it reaches through the two after it (the solver,
        /// seeded with the run's own way there, --stand-minutes), spliced with the run's inputs after them and re-timed. Returns
        /// the complete run, or null.
        /// </summary>
        Route TailFix(Route current, List<InputTick> whole, int g)
        {
            Route r = Evaluate(whole);
            for (int round = 0; round < 3 && r.Progress < _goals && r.Progress > g && !TimeUp; round++)
            {
                int p = r.Progress, touch = (int)Math.Min(r.GoalInput[p - 1], r.Inputs.Count), limit = Math.Min(_goals, p + 2);
                List<InputTick> prefix = r.Inputs.GetRange(0, touch);
                int oldFrom = (int)current.GoalInput[p - 1], oldTo = (int)Math.Min(current.GoalInput[limit - 1], current.Inputs.Count);
                var seed = current.Inputs.GetRange(oldFrom, Math.Min(current.Inputs.Count, oldTo + 20) - oldFrom);
                Simulation Create()
                {
                    Simulation sim = _create();
                    SimSnapshot s0 = sim.Advance(prefix) ?? throw new InvalidOperationException("the prefix ended before its last input");
                    sim.SetStart(s0, touch);
                    sim.Goals.Limit = limit;
                    return sim;
                }
                _eval.SetStart(_origin, 0);
                _eval.Goals.Limit = _goals;
                _eval.Restore(_eval.Advance(prefix));
                string caps = WindowCaps(_runGoals.Goals.Skip(p).Take(limit - p));
                List<InputTick> found = null;
                long foundAt = long.MaxValue;
                RunResult through = null;
                // On a thread of its own: a simulation made on this one would take over _eval's game hooks.
                Solve.OnThread(() =>
                {
                    var solver = new OfflineSolver(Create, caps, new List<List<InputTick>> { seed });
                    var guard = new object();
                    solver.Improved += (time, runTicks, inputs, how) => { lock (guard) if (runTicks < foundAt) (found, foundAt) = (inputs, runTicks); };
                    DateTime stop = DateTime.UtcNow.AddMinutes(_standMinutes);
                    Thread[] threads = solver.Start(_threads);
                    while (DateTime.UtcNow < stop && !TimeUp) Thread.Sleep(250);
                    solver.Stop();
                    foreach (Thread t in threads) t.Join(60000);
                    if (found != null) through = Create().Run(found, null, null);
                });
                if (found == null || through == null || through.Progress < limit)
                {
                    Log($"auto: idle tail: goals {p + 1}-{limit} from the touch of goal {p} (input {touch}): no way through in {_standMinutes:0.#} min");
                    return null;
                }
                List<InputTick> candidate = prefix.Concat(found.Take((int)(through.GoalInputTicks[limit - 1] - touch))).Concat(current.Inputs.Skip(oldTo)).ToList();
                Log($"auto: idle tail: goals {p + 1}-{limit} re-found from the touch of goal {p}: goal {limit} at tick {through.GoalPhysicsTicks[limit - 1]} against {current.GoalPhysics[limit - 1]}");
                r = limit < _goals ? Retune(_eval, _origin, current, candidate, limit) ?? Evaluate(candidate) : Evaluate(candidate);
            }
            return r.Progress == _goals ? r : null;
        }

        /// <summary>
        /// Goal g's wait before its last quick restart spent under the box: the idle ticks before that restart (the route waits
        /// there, then restarts and dashes in: its way on after the buy follows from that dash) start with a walk toward the
        /// trigger until the player rests under it, and the rest of them stand there; the restart and the approach after it stay.
        /// The standing ticks go to RngTickets, re-timed from goal g on (the wait's length is the restart's re-timing). Returns the
        /// fastest run and how, or null.
        /// </summary>
        (Route, string) UnderBoxWait(Route current, int g, int from, int buy, Func<int, SimSnapshot> snapshot)
        {
            int last = -1;
            for (int i = buy - 1; i > from; i--) if (current.Inputs[i].reset) { last = i; break; }
            if (last < 0) return (null, null);
            int s = last;
            while (s > from && Idle(current.Inputs[s - 1])) s--;
            if (last - s < IdleWaitRun) return (null, null);
            SimSnapshot at = snapshot(s);
            if (at == null) return (null, null);
            Pose[] start = PoseTrace(at, s, new List<InputTick> { new InputTick() });
            Zone zone = BoxZone(_runGoals.Goals[g]);
            if (float.IsNaN(zone.x0)) return (null, null);
            var step = new InputTick { x = zone.Middle < start[1].x ? -1f : 1f };
            bool held = s > 0 && current.Inputs[s - 1].held;
            List<InputTick> Walk(int run)
            {
                var list = Enumerable.Repeat(step, run).Concat(Enumerable.Repeat(new InputTick(), last - s - run)).ToList();
                if (held && list.Count > 0) { InputTick t = list[0]; t.release = true; list[0] = t; }
                return list;
            }
            int stand = -1;
            for (int run = 0; run + RestTicks <= last - s && stand < 0; run++)
            {
                Pose rest = default;
                bool seen = false;
                List<InputTick> list = Walk(run).Take(run + RestTicks).ToList();
                _eval.SetStart(at, s);
                _eval.Goals.Limit = _goals;
                // Past the list: a run stops with its inputs, and its last tick reports nothing then.
                _eval.Run(list, null, tick =>
                {
                    if (tick < list.Count) return true;
                    rest = new Pose { ground = _eval.Player.onGround, x = _eval.PlayerWorld.x, y = _eval.PlayerWorld.y };
                    seen = true;
                    return false;
                });
                if (!seen) break;
                if (rest.ground && zone.Under(rest.x) && rest.y < zone.y0) stand = run;
                else if (step.x < 0f && rest.x < zone.x0 || step.x > 0f && rest.x > zone.x1) break;
            }
            if (stand < 0) return (null, null);
            List<InputTick> whole = current.Inputs.Take(s).Concat(Walk(stand)).Concat(current.Inputs.Skip(last)).ToList();
            var (r, how) = RngTickets(current, g, whole, s + stand, last, at, s, zone, long.MaxValue, false, g);
            return r == null ? (null, null) : (r, $"the wait before the restart under the box ({stand} ticks walked, {last - s - stand} standing), {how}");
        }

        /// <summary>A buy's stretch this long or longer (inputs) gets a stand window when no walk-in beats the run; its budget in
        /// minutes (--stand-minutes).</summary>
        const int StandWindowMin = 120;
        double _standMinutes = 1.0;

        /// <summary>
        /// Goal g's buy reached by a search: a window from the touch of the goal before (input <paramref name="from"/>, <paramref name="at"/>
        /// its state) whose only goal is a stand under the box, a line at the height the run last stood at within reach of the
        /// trigger, as wide as the trigger; scored at the earliest arrival (the solver's, seeded with the run's own way). Then idle
        /// ticks there and a jump straight up that touches as the Cash comes (the fewest), the run's inputs from its old touch
        /// after, and the idle ticks go to RngTickets. For a buy no walk reaches (a climb, a far box). Returns the fastest
        /// re-timed run and how, or null.
        /// </summary>
        (Route, string) StandWindow(Route current, int g, int from, int buy, SimSnapshot at)
        {
            if (at == null || buy <= from) { Log($"auto: stand window: goal {g + 1} ({_labels[g]}): no stretch ({from}..{buy})"); return (null, null); }
            double c0 = CpuSeconds();
            Pose[] poses = PoseTrace(at, from, current.Inputs.GetRange(from, buy - from));
            Zone zone = BoxZone(_runGoals.Goals[g]);
            if (float.IsNaN(zone.x0)) { Log($"auto: stand window: goal {g + 1} ({_labels[g]}): no box trigger"); return (null, null); }
            // The run's own wait for the box when it stands within 200 units of the trigger (the statue's: 32 to its right): walked
            // in from there, with no search.
            int waitAt = -1;
            for (int i = from; i < buy;)
            {
                if (!Idle(current.Inputs[i])) { i++; continue; }
                int j = i;
                while (j < buy && Idle(current.Inputs[j])) j++;
                if (j - i >= IdleWaitRun && poses[i - from + 1].ground && Math.Abs(poses[i - from + 1].x - zone.Middle) < 200f && (waitAt < 0 || j - i > 0)) waitAt = i;
                i = j;
            }
            if (waitAt > from)
            {
                var (own, ownHow) = WalkIn(current, g, from, buy, at, current.Inputs.GetRange(from, waitAt - from), $"the run's own way to its wait at input {waitAt}");
                if (own != null) return (own, ownHow);
            }
            // The floor under the box: where the run stood nearest the trigger below it (its last such spot), else straight down
            // from the trigger (a raycast past triggers and the player).
            int stood = Enumerable.Range(1, buy - from).Where(t => poses[t].ground && poses[t].y < zone.y0 && poses[t].y > zone.y0 - 400f)
                .OrderBy(t => Math.Abs(poses[t].x - zone.Middle)).ThenByDescending(t => t).FirstOrDefault();
            float lineY = stood > 0 ? poses[stood].y : float.NaN, lineH = 4f;
            if (stood == 0)
            {
                ScenePos o = _eval.ToScene(new WorldPos(zone.Middle, zone.y0 - 1f));
                var ray = new UnityEngine.Vector2(o.x, o.y);
                for (int i = 0; i < 8; i++)
                {
                    UnityEngine.RaycastHit2D hit = UnityEngine.Physics2D.Raycast(ray, UnityEngine.Vector2.down, 1000f);
                    if (!hit) break;
                    if (!hit.collider.isTrigger && hit.collider.GetComponent<Movement>() == null)
                    {
                        // A line as tall as the player standing on it.
                        lineY = _eval.ToWorld(new ScenePos(o.x, hit.point.y)).y + zone.playerTall / 2f;
                        lineH = zone.playerTall;
                        break;
                    }
                    ray = hit.point + UnityEngine.Vector2.down * 0.5f;
                }
            }
            if (float.IsNaN(lineY)) { Log($"auto: stand window: goal {g + 1} ({_labels[g]}): no floor under the trigger"); return (null, null); }
            var line = new FullTasGoal { label = $"at:{zone.Middle},{lineY},{zone.x1 - zone.x0},{lineH}", kind = FullTasGoal.At, x = zone.Middle, y = lineY, w = zone.x1 - zone.x0, h = lineH };
            List<InputTick> prefix = current.Inputs.GetRange(0, from);
            Simulation Create()
            {
                Simulation sim = _create();
                SimSnapshot s0 = sim.Advance(prefix) ?? throw new InvalidOperationException("the prefix ended before its last input");
                sim.SetStart(s0, from);
                // The run's goals up to g, then the line in place of the buy (a snapshot carries its goal count).
                sim.UseGoals(new IGTAPTasMod.FullTasTracker(_runGoals.Goals.Take(g).Append(line).ToArray()));
                return sim;
            }
            _eval.Restore(at);
            string caps = WindowCaps(Enumerable.Empty<FullTasGoal>());
            List<InputTick> found = null;
            long foundAt = long.MaxValue;
            RunResult arrival = null;
            // On a thread of its own: a simulation made on this one would take over _eval's game hooks.
            Solve.OnThread(() =>
            {
                var solver = new OfflineSolver(Create, caps, new List<List<InputTick>> { current.Inputs.GetRange(from, buy - from) });
                var guard = new object();
                solver.Improved += (time, runTicks, inputs, how) => { lock (guard) if (runTicks < foundAt) (found, foundAt) = (inputs, runTicks); };
                DateTime stop = DateTime.UtcNow.AddMinutes(_standMinutes);
                Thread[] threads = solver.Start(_threads);
                while (DateTime.UtcNow < stop && !TimeUp) Thread.Sleep(250);
                solver.Stop();
                foreach (Thread t in threads) t.Join(60000);
                if (found != null) arrival = Create().Run(found, null, null);
            });
            if (found == null) { Log($"auto: stand window: goal {g + 1} ({_labels[g]}): no arrival at {line.label} in {_standMinutes:0.#} min"); return (null, null); }
            if (arrival.Progress < g + 1) { Log($"auto: stand window: goal {g + 1} ({_labels[g]}): the arrival doesn't replay ({arrival.EndReason})"); return (null, null); }
            List<InputTick> reach = found.Take((int)(arrival.GoalInputTicks[g] - from)).ToList();
            string arrived = $"a window to a stand under the box ({reach.Count} inputs, arrival at tick {arrival.GoalPhysicsTicks[g]} against the run's touch at {current.GoalPhysics[g]}, {CpuSeconds() - c0:0} CPU s)";
            Log($"auto: stand window: goal {g + 1} ({_labels[g]}): {arrived}");
            // The arrival can come in a dash: idle ticks to let it end (a dash's ticks take no input) before the walk.
            bool heldAt = reach.Count > 0 && reach[reach.Count - 1].held;
            reach.AddRange(Enumerable.Range(0, RestTicks).Select(i => new InputTick { release = i == 0 && heldAt }));
            return WalkIn(current, g, from, buy, at, reach, arrived);
        }

        /// <summary>--rng-study: the best's waits for Cash and what draws the RNG in them, then the best re-timed (Retune); then stops.</summary>
        void RngStudy()
        {
            Route best = _best;
            // --base-check FILE: the best's goal ticks as another build's --check of it printed them ("goal k label tick (...) input
            // i" lines). A route made on that build that strays on this one is re-timed from the start (Retune) with its old goal
            // inputs for the waits' places, and kept when complete.
            string baseCheck = Solve.Args(_args, "--base-check").FirstOrDefault();
            if (baseCheck != null)
            {
                var lines = System.IO.File.ReadAllLines(baseCheck).Where(l => l.StartsWith("goal ") && l.Contains(" input ")).ToList();
                var input = new long[_goals];
                var physics = new long[_goals];
                foreach (string l in lines)
                {
                    string[] w = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    int k = int.Parse(w[1]) - 1;
                    physics[k] = long.Parse(w[3]);
                    input[k] = long.Parse(w[w.Length - 1]);
                }
                var old = new Route { Inputs = best.Inputs, Progress = lines.Count, GoalInput = input, GoalPhysics = physics };
                double r0 = CpuSeconds();
                Route fixedRun = Retune(_eval, _origin, old, best.Inputs, 0, out string retuneLost);
                Log($"rng study: {Path.GetFileName(baseCheck)}'s route re-timed on this build: {(fixedRun == null ? retuneLost : Describe(fixedRun))} (as it is: {Describe(best)}, {best.Progress}/{_goals} goals) in {CpuSeconds() - r0:0} CPU s");
                if (fixedRun == null || fixedRun.Progress < _goals) return;
                Commit(fixedRun, "re-timed on this build");
                best = fixedRun;
            }
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
            // --census: every wait standing within reach of its box (in the goals the best reaches), its hop schedules sorted
            // into RNG classes (no replays of the whole run).
            bool census = _args.Contains("--census");
            if (census)
            {
                // Every buy's walk-in too: its idle ticks under the box, the classes of their hops.
                _rngCensus = true;
                for (int g = 1; g < best.Progress; g++)
                {
                    if (_runGoals.Goals[g].kind != FullTasGoal.Buy) continue;
                    int from = (int)best.GoalInput[g - 1];
                    _eval.SetStart(_origin, 0);
                    _eval.Goals.Limit = _goals;
                    WalkIn(best, g, from, (int)best.GoalInput[g], _eval.Advance(best.Inputs.GetRange(0, from)));
                }
                _rngCensus = false;
            }
            for (int g = 0; g < best.Progress; g++)
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
                        if (census && near > 0)
                        {
                            int ws = j;
                            while (ws > i && poses[ws].ground && zone.Near(poses[ws].x) && poses[ws].vx == 0f) ws--;
                            _eval.SetStart(_origin, 0);
                            _eval.Goals.Limit = _goals;
                            SimSnapshot atWs = _eval.Advance(best.Inputs.GetRange(0, ws));
                            BoxZone(_runGoals.Goals[g]);
                            RngTickets(best, g, best.Inputs, ws, j, atWs, ws, zone, best.GoalPhysics[g], census: true);
                            // --census-extend N,...: the same stand N idle ticks longer (a longer wait, for the class count only).
                            foreach (int n in Solve.Args(_args, "--census-extend").SelectMany(a => a.Split(',')).Select(int.Parse))
                            {
                                var longer = best.Inputs.Take(j).Concat(Enumerable.Repeat(new InputTick(), n)).Concat(best.Inputs.Skip(j)).ToList();
                                RngTickets(best, g, longer, ws, j + n, atWs, ws, zone, long.MaxValue, census: true);
                            }
                        }
                    }
                    i = j;
                }
            }
            foreach (int k in Solve.Args(_args, "--under").SelectMany(a => a.Split(',')).Select(int.Parse))
            {
                int g = k - 1, from = (int)best.GoalInput[g - 1];
                SimSnapshot Snap(int i) { _eval.SetStart(_origin, 0); _eval.Goals.Limit = _goals; return _eval.Advance(best.Inputs.GetRange(0, i)); }
                var (w, how) = UnderBoxWait(best, g, from, (int)best.GoalInput[g], Snap);
                Log($"rng study: under the box at goal {k}: {(w == null ? "nothing complete" : Describe(w) + ", " + how)}");
                if (w != null && Ahead(w, _best)) Commit(w, $"under the box at goal {k}");
            }
            foreach (int k in Solve.Args(_args, "--stand").SelectMany(a => a.Split(',')).Select(int.Parse))
            {
                int g = k - 1, from = (int)best.GoalInput[g - 1];
                _eval.SetStart(_origin, 0);
                _eval.Goals.Limit = _goals;
                var (w, how) = StandWindow(best, g, from, (int)best.GoalInput[g], _eval.Advance(best.Inputs.GetRange(0, from)));
                Log($"rng study: stand window at goal {k}: {(w == null ? "nothing complete" : Describe(w) + ", " + how)}");
                if (w != null && Ahead(w, _best)) Commit(w, $"stand window at goal {k}");
            }
            double c0 = CpuSeconds();
            Route re = Retune(_eval, _origin, best, best.Inputs, 0, out string reLost);
            Log($"rng study: the best re-timed: {(re == null ? reLost : Describe(re))} against {Describe(best)} in {CpuSeconds() - c0:0.0} CPU s");
        }
    }
}
