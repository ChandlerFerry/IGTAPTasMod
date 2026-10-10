using System;
using System.Collections.Generic;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    // Idles (docs/superpowers/specs/2026-10-06-fulltas-idles-design.md): a buy goal's wait for its Cash made explicit. Not a goal:
    // an annotation of the buy it waits for, so the goal list, its numbers and the gate are unchanged.
    sealed partial class FullTas
    {
        /// <summary>How far below a box's trigger (world units) the player still counts as at it: a box is bought from below, by
        /// a jump from the ground under it.</summary>
        // ponytail: a fixed reach; take it from the jump's height under each box if a raised box reads as never reached.
        const float IdleReachBelow = 160f;

        internal sealed class IdleSpan
        {
            /// <summary>The buy goal it waits for (1-based, the run's numbering).</summary>
            public int Goal;
            /// <summary>The physics tick and held frames when the player was first at the box, and when its Cash was there.</summary>
            public long ArrivePhysics, ArriveHeld, AffordPhysics, AffordHeld;
        }

        /// <summary>
        /// The route's idles: for each buy goal k reached, from goal k-1 on, the first tick the player is at the box (within its
        /// trigger's x-range plus the player's half width, below its top by up to IdleReachBelow) and the first tick the run holds
        /// what goal k's boxes cost; an idle when the first comes IdleWaitRun ticks or more before the second. One replay, on a
        /// simulation of its own (its own thread: the engine's statics are per thread), so the loop's evaluator is left as it is.
        /// </summary>
        internal List<IdleSpan> Idles(Route r)
        {
            var idles = new List<IdleSpan>();
            if (r.Progress == 0 || TickTiming) return idles;
            return Solve.OnThread(() =>
            {
                Simulation sim = _create();
                sim.SetStart(sim.Start, 0);
                sim.Goals.Limit = r.Progress;
                int goal = -1;
                Zone zone = default;
                long arriveP = -1, arriveH = -1, affordP = -1, affordH = -1;
                sim.Run(r.Inputs, null, _ =>
                {
                    int next = sim.Progress;
                    if (next != goal)
                    {
                        // The goal before was just reached: close its stretch, and start the next one's.
                        if (goal >= 0 && arriveP >= 0 && affordP >= 0 && affordP - arriveP >= IdleWaitRun)
                            idles.Add(new IdleSpan { Goal = goal + 1, ArrivePhysics = arriveP, ArriveHeld = arriveH, AffordPhysics = affordP, AffordHeld = affordH });
                        goal = next;
                        arriveP = arriveH = affordP = affordH = -1;
                        FullTasGoal g = next < r.Progress ? _runGoals.Goals[next] : null;
                        zone = g != null && g.kind == FullTasGoal.Buy && g.boxes != null ? BoxZone(g, sim) : new Zone { x0 = float.NaN };
                    }
                    if (float.IsNaN(zone.x0)) return true;
                    long physics = sim.Engine.PhysicsTicks, held = sim.Engine.HeldFrames;
                    if (arriveP < 0)
                    {
                        WorldPos at = sim.PlayerWorld;
                        if (zone.Near(at.x) && at.y <= zone.y1 && at.y >= zone.y0 - IdleReachBelow) { arriveP = physics; arriveH = held; }
                    }
                    if (affordP < 0 && sim.Shop.Affordable(_runGoals.Goals[goal].boxes) == true) { affordP = physics; affordH = held; }
                    return true;
                }, 1000000);
                // The last goal's own stretch closes on its tick, which the run ends 12 ticks after.
                if (goal >= 0 && goal < r.Progress && arriveP >= 0 && affordP >= 0 && affordP - arriveP >= IdleWaitRun)
                    idles.Add(new IdleSpan { Goal = goal + 1, ArrivePhysics = arriveP, ArriveHeld = arriveH, AffordPhysics = affordP, AffordHeld = affordH });
                return idles;
            });
        }

        /// <summary>
        /// The ticks the route stands idle waiting for its buys, counted from its inputs: the idle-input runs of IdleWaitRun ticks or
        /// more in each buy goal's stretch. What ranks two routes of the same live time (<see cref="Ahead"/>): the one that waits
        /// more stands where the other burns ticks (filler, a slower stretch), so its wait is visible and its stretches faster.
        /// </summary>
        long IdleTicks(Route r)
        {
            long total = 0;
            for (int k = 1; k <= r.Progress && k <= _runGoals.Goals.Length; k++)
            {
                if (_runGoals.Goals[k - 1].kind != FullTasGoal.Buy) continue;
                int from = k == 1 ? 0 : (int)Math.Max(0, Math.Min(r.GoalInput[k - 2], r.Inputs.Count));
                int to = (int)Math.Max(0, Math.Min(r.GoalInput[k - 1], r.Inputs.Count));
                for (int i = from; i < to;)
                {
                    if (!Idle(r.Inputs[i])) { i++; continue; }
                    int j = i;
                    while (j < to && Idle(r.Inputs[j])) j++;
                    if (j - i >= IdleWaitRun) total += j - i;
                    i = j;
                }
            }
            return total;
        }
    }
}
