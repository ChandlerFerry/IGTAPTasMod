using System;
using System.Collections.Generic;
using System.Linq;
using IGTAPTasMod;

namespace IGTAP.EngineSim
{
    // Tech checks for spikes and springs. A spike kills on its second Stay (spikeScript: counter past graceFrames 1, a dash on any
    // Stay) and its first overlapping tick is only an Enter, so a hurtbox may touch one on two ticks and has to be clear on the
    // third: the "SpikeGrace" span is such a touch the run lives through. A spring hit (Movement.hitSpring) refills the jumps and
    // dashes, ends a dash in progress and adds the spring's momentum; SpringScript's cooldown is 0.15 s (8 steps).
    // Not checked: corner-jump (stands with only the back ground ray on a platform end over a spike pit that the hurtbox does not
    // touch: the record has neither the rays nor the spikes under the dot, so a standing jump cannot be told from any other).
    public static partial class TechChecks
    {
        /// <summary>
        /// Edge spike jump: a ground jump off the edge of a spike strip inside its grace. The dot stands or runs on the ground (coyote
        /// full, no landing) with its hurtbox in a spike on the step before the jump, jumps (the buffered press spent, Velocity.y up,
        /// no air or wall jump used) and lives: the jump lifts it out before the third tick that would kill it. Landing in the spike
        /// with the buffered jump leaving on the landing step (the one spike jump) is not it: the dot was in the air.
        /// </summary>
        [TechCheck("edge-spike-jump")]
        static int HazardEdgeSpikeJump(TechSteps run, int from, int to) => HazardEdgeJump(run, from, to, false);

        /// <summary>
        /// Edge spike jump next to a wall: the edge spike jump, and within 16 steps of it the dot is held sideways by a solid (its x
        /// does not move with Velocity.x at 100 or more, or it is on a wall): the jump goes up a wall at the strip's end.
        /// </summary>
        [TechCheck("edge-spike-jump-wall")]
        static int HazardEdgeSpikeJumpWall(TechSteps run, int from, int to) => HazardEdgeJump(run, from, to, true);

        static int HazardEdgeJump(TechSteps run, int from, int to, bool wall)
        {
            List<TechStep> steps = run.Steps;
            for (int j = Math.Max(from, 1); j <= to; j++)
            {
                if (!HazardGroundJump(steps[j])) continue;
                TechStep before = steps[j - 1];
                if (!before.Spike || !before.After.Ground || before.After.Coyote < run.MaxCoyote) continue;
                if (Dies(steps, j, 3)) continue;
                if (wall && !HazardHeldSideways(steps, j)) continue;
                return j;
            }
            return -1;
        }

        /// <summary>A ground jump went in on this step: the press spent from the buffer (getPlayerControlledInput), Velocity.y up, no
        /// air or wall jump used and no wall kick.</summary>
        static bool HazardGroundJump(TechStep step)
        {
            MoveState input = default;
            bool seen = false;
            foreach (TechEvent e in step.Calls)
            {
                if (e.Call == TechCall.Input)
                {
                    input = e.State;
                    seen = true;
                }
                else if (e.Call == TechCall.Kick) return false;
                else if (e.Call == TechCall.Sprite && seen)
                {
                    MoveState m = e.State;
                    return input.Buffer && !m.Buffer && input.Ground && m.Vy > 0f && m.AirJumps == input.AirJumps && m.WallJumps == input.WallJumps;
                }
            }
            return false;
        }

        /// <summary>Within 16 steps after step j a step where the dot's x does not move although Velocity.x is 100 or more (or it is
        /// on a wall): a solid beside it.</summary>
        static bool HazardHeldSideways(List<TechStep> steps, int j)
        {
            for (int k = j + 1; k <= Math.Min(steps.Count - 1, j + 16); k++)
            {
                MoveState a = steps[k].After, b = steps[k - 1].After;
                if (Math.Abs(a.X - b.X) < 0.5f && (a.Wall || Math.Abs(a.Vx) >= 100f)) return true;
            }
            return false;
        }

        /// <summary>
        /// Spike fall: the ground rays find no floor over a spike patch flush with it (spikes are triggers, not ground), so the dot
        /// that was standing or running (coyote full on a step within 12 before) starts to fall with no jump, dash or spring, drops at
        /// most 20 u, touches the spikes (the first step of a graze the run lives through) and a step-up lifts it out within 3
        /// steps: it fell into the patch and stepped onto the far floor inside the grace.
        /// </summary>
        [TechCheck("spike-fall")]
        static int HazardSpikeFall(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            foreach (Span span in run.Spans)
            {
                int g = span.End;
                if (span.Tech != "SpikeGrace" || g < from || g > to) continue;
                if (g > 0 && steps[g - 1].Spike) continue;
                int stood = -1;
                for (int j = g - 1; j >= 0 && g - j <= 12; j--)
                {
                    TechStep s = steps[j];
                    if (s.After.Ground && s.After.Coyote >= run.MaxCoyote)
                    {
                        stood = j;
                        break;
                    }
                    if (s.JumpPress || s.DashPress || s.After.Dash || s.After.Vy > 0f || (s.Events & (StepEvents.Landed | StepEvents.SpringHit)) != 0) break;
                }
                if (stood < 0 || steps[stood].After.Y - steps[g].After.Y > 20f) continue;
                for (int k = g; k <= Math.Min(steps.Count - 1, g + 3); k++)
                    if ((steps[k].Events & StepEvents.SteppedUp) != 0) return g;
            }
            return -1;
        }

        /// <summary>
        /// Spike super: a super jump (CheckForSuperJump, the SuperJump / ReversedSuper / HighSuper spans) that leaves a spike inside
        /// its grace: a spike graze the run lives through on the step before the super or on the super's own step (a dashing dot
        /// may touch a spike on one step only: the next Stay kills it).
        /// </summary>
        [TechCheck("spike-super")]
        static int HazardSpikeSuper(TechSteps run, int from, int to)
        {
            foreach (Span super in run.Spans)
            {
                int s = super.End;
                if (!HazardIsSuper(super) || s < from || s > to) continue;
                foreach (Span grace in run.Spans)
                    if (grace.Tech == "SpikeGrace" && grace.End >= s - 1 && grace.End <= s) return s;
            }
            return -1;
        }

        static bool HazardIsSuper(Span span) => span.Tech == "SuperJump" || span.Tech == "ReversedSuper" || span.Tech == "HighSuper";

        /// <summary>
        /// Spike wallbounce: a bounce off a wall (the Superbounce or WallBounce span: SpecialWallJump) with a spike graze the run
        /// lives through from 2 steps before the kick to 3 after it: the bounce's first ticks brush the spikes inside their grace.
        /// </summary>
        [TechCheck("spike-wallbounce")]
        static int HazardSpikeWallbounce(TechSteps run, int from, int to)
        {
            foreach (Span bounce in run.Spans)
            {
                int k = bounce.End;
                if ((bounce.Tech != "Superbounce" && bounce.Tech != "WallBounce") || k < from || k > to) continue;
                foreach (Span grace in run.Spans)
                    if (grace.Tech == "SpikeGrace" && grace.End >= k - 2 && grace.End <= k + 3) return k;
            }
            return -1;
        }

        /// <summary>
        /// Super in switching spikes: a dash's block swap (Swapped, on the step the dash was pressed) turns spikes on around the dot:
        /// its hurtbox touches a spike on that very step, not on the step before, and the super on that step or the next leaves
        /// them inside their grace (a SuperJump / ReversedSuper / HighSuper span, the run lives). A spike the dash itself runs into
        /// on its first step looks the same when the blocks are unlocked.
        /// </summary>
        [TechCheck("super-switching-spikes")]
        static int HazardSuperSwitchingSpikes(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            foreach (Span super in run.Spans)
            {
                int s = super.End;
                if (!HazardIsSuper(super) || s < from || s > to) continue;
                for (int k = Math.Max(s - 1, 0); k <= s; k++)
                {
                    TechStep swap = steps[k];
                    if (swap.Swapped && swap.DashPress && swap.Spike && (k == 0 || !steps[k - 1].Spike) && !Dies(steps, k, 3)) return s;
                }
            }
            return -1;
        }

        /// <summary>
        /// Spring clip: a downward spring's momentum carries the dot through a spike strip a plain fall dies in. A spike graze the
        /// run lives through while falling faster than a plain fall can (27 u a step or more: the terminal fall is 26.3) with the
        /// spring's momentum still pushing down (momentum.y -20 or less), no dash, and the dot has been falling since a hit of a
        /// spring pointing down (SpringY below -0.5) with no ground touched in between.
        /// </summary>
        [TechCheck("spring-clip")]
        static int HazardSpringClip(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            foreach (Span span in run.Spans)
            {
                int g = span.End;
                if (span.Tech != "SpikeGrace" || g < from || g > to || g == 0) continue;
                MoveState now = steps[g].After, was = steps[g - 1].After;
                if (was.Y - now.Y < 27f || now.My > -20f || now.Dash || was.Dash) continue;
                for (int j = g - 1; j >= 0; j--)
                {
                    if ((steps[j].Events & StepEvents.SpringHit) != 0 && steps[j].SpringY < -0.5f) return g;
                    if ((steps[j].Events & StepEvents.Landed) != 0 || steps[j].After.Ground) break;
                }
            }
            return -1;
        }

        /// <summary>
        /// Spring dash stack: a dash that carries one spring's momentum into a second spring. A spring hit lands in a dash (the step
        /// before the hit has the dash active): hitSpring's endDash gives back 95% of preDashMomentum on the axes endDash checks
        /// (Movement 2030-2039: x when its sign matches the dash's and the dash is not upward, or a straight-down dash; y when its
        /// sign matches the dash's), at least 100 u of it, and that momentum is a spring's: the dash started after an earlier spring
        /// hit with no ground touched since.
        /// </summary>
        [TechCheck("spring-dash-stack")]
        static int HazardSpringDashStack(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            for (int i = Math.Max(from, 1); i <= to; i++)
            {
                if ((steps[i].Events & StepEvents.SpringHit) == 0) continue;
                MoveState dash = steps[i - 1].After;
                if (!dash.Dash || dash.Cutscene != Movement.cutsceneModes.dash) continue;
                float backX = 0f, backY = 0f;
                if ((HazardSign(dash.PreDashX) == HazardSign(dash.DashX) && dash.DashY <= 0f) || dash.DashY == -1f) backX = 0.95f * dash.PreDashX;
                if (HazardSign(dash.PreDashY) == HazardSign(dash.DashY) && dash.DashY != 0f) backY = 0.95f * dash.PreDashY;
                if (Math.Sqrt(backX * backX + backY * backY) < 100.0) continue;
                int start = -1;
                for (int j = i - 1; j >= 0 && steps[j].After.Dash; j--)
                    if (steps[j].After.SinceDash == 0f)
                    {
                        start = j;
                        break;
                    }
                if (start < 0) continue;
                for (int j = start - 1; j >= 0; j--)
                {
                    if ((steps[j].Events & StepEvents.SpringHit) != 0) return i;
                    if ((steps[j].Events & StepEvents.Landed) != 0 || steps[j].After.Ground) break;
                }
            }
            return -1;
        }

        /// <summary>Mathf.Sign: 1 for zero.</summary>
        static float HazardSign(float x) => x < 0f ? -1f : 1f;

        /// <summary>
        /// Spring double hit: one spring hits the dot twice in one pass. SpringScript's cooldown is 0.15 s (OnTriggerStay2D hits again
        /// as soon as it ends if the dot is still inside), so two hits of a spring with the same up 7-9 steps apart, the dot within one
        /// 80x80 trigger's reach (125 u in x, 155 in y) of where the first one hit it. Returns the second hit.
        /// </summary>
        [TechCheck("spring-double-hit")]
        static int HazardSpringDoubleHit(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            int first = -1;
            for (int i = 0; i < steps.Count && i <= to; i++)
            {
                if ((steps[i].Events & StepEvents.SpringHit) == 0) continue;
                int a = first;
                first = i;
                if (a < 0 || i < from || i - a < 7 || i - a > 9) continue;
                TechStep one = steps[a], two = steps[i];
                if (Math.Abs(one.SpringX - two.SpringX) > 0.01f || Math.Abs(one.SpringY - two.SpringY) > 0.01f) continue;
                if (Math.Abs(one.After.X - two.After.X) > 125f || Math.Abs(one.After.Y - two.After.Y) > 155f) continue;
                return i;
            }
            return -1;
        }
    }
}
