using System;
using System.Collections.Generic;
using System.Linq;
using IGTAPTasMod;

namespace IGTAP.EngineSim
{
    /// <summary>
    /// Tech checks for how the dot moves along the ground and through tight spaces: corner boosts, ground bounce pumping, the step-up
    /// techs (Movement.Update's step-up teleports the dot onto a ground top 13-44 u above its feet), the roof squeeze, gap stick and
    /// edge stop. Each reads the player's own state (Movement's fields, recorded per step) and never the geometry around it: a step-up
    /// is told from a landing's tiny correction by how far it lifted the dot, a squeeze by the dot stalling against something its
    /// standing hitbox does not fit and the jump's short hitbox carrying it on.
    /// </summary>
    public static partial class TechChecks
    {
        /// <summary>The standing hitbox is 72 u tall, a jump's or dash's 54: anything under this is the short one.</summary>
        const float MotionStandingH = 70f;

        /// <summary>A step-up that is a climb lifts the dot at least this much (a landing's correction is under 11).</summary>
        const float MotionClimbRise = 13f;

        /// <summary>
        /// Pixel perfect corner boost: a hyper off a floor's corner chained into the ultra bonus. The flat hyper dash slides past the
        /// floor's edge with onGround held on the coyote ticks (Coyote under its maximum while Ground is still set), and the jump
        /// pressed there is the hyper (CheckForSuperJump: buffered press spent, canHyper) whose ultra check passes and adds
        /// preDashMomentum.x to its momentum: the overlay's <c>UltraBonus</c> span, on a jump taken with the floor already gone. A
        /// plain ultra bonus jumps with the coyote full.
        /// </summary>
        [TechCheck("corner-boost")]
        static int MotionCornerBoost(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            foreach (Span span in run.Spans)
            {
                if (span.Tech != "UltraBonus" || span.End < from || span.End > to) continue;
                MoveState sup = default;
                bool open = false;
                foreach (TechEvent e in steps[span.End].Calls)
                {
                    if (e.Call == TechCall.Super)
                    {
                        sup = e.State;
                        open = true;
                    }
                    else if (e.Call == TechCall.Input) open = false;
                    else if (e.Call == TechCall.Sprite && open)
                    {
                        open = false;
                        if (sup.Buffer && !e.State.Buffer && sup.CanHyper && sup.Ground && sup.Coyote < run.MaxCoyote) return span.End;
                    }
                }
            }
            return -1;
        }

        /// <summary>Each ground bounce has to beat the last one's momentum by this much (the pump's +11 % a cycle, with room).</summary>
        const float MotionPumpGrowth = 1.05f;

        /// <summary>
        /// Ground bounce momentum pump: chained ground bounces (the overlay's <c>DashBounce</c>, <c>WaveBounce</c> and
        /// <c>SuperGroundBounce</c> spans: a bounce jump inside DashBounceFrames after a straight-down dash landed) where the sideways
        /// momentum right after a bounce is above the previous bounce's by 5 % or more, and nothing but the bounce itself raised it in
        /// between (no step between them holds more momentum.x than the first bounce left). One bounce and a coast decays; the pump is
        /// the second bounce beating the first.
        /// </summary>
        [TechCheck("ground-bounce-pump")]
        static int MotionGroundBouncePump(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            var bounces = new SortedSet<int>();
            foreach (Span span in run.Spans)
                if ((span.Tech == "DashBounce" || span.Tech == "WaveBounce" || span.Tech == "SuperGroundBounce") && span.End >= from && span.End <= to)
                    bounces.Add(span.End);
            int previous = -1;
            foreach (int bounce in bounces)
            {
                if (previous >= 0 && MotionPumped(steps, previous, bounce)) return bounce;
                previous = bounce;
            }
            return -1;
        }

        static bool MotionPumped(List<TechStep> steps, int first, int second)
        {
            float a = steps[first].After.Mx, b = steps[second].After.Mx;
            if (a == 0f || Math.Sign(a) != Math.Sign(b) || Math.Abs(b) < MotionPumpGrowth * Math.Abs(a)) return false;
            for (int k = first + 1; k < second; k++)
                if (Math.Abs(steps[k].After.Mx) > Math.Abs(a) + 1f) return false;
            return true;
        }

        /// <summary>The state the step started from: the step before's end (the first step's first call).</summary>
        static MoveState MotionBefore(List<TechStep> steps, int i) =>
            i > 0 ? steps[i - 1].After : steps[i].Calls.Count > 0 ? steps[i].Calls[0].State : steps[i].After;

        /// <summary>
        /// A step on which the dot climbed by a step-up: a step-up in the step's frames (not an inwall climb, whose wall rays look
        /// against the facing, and not after a respawn), the dot not rising on its own (Velocity.y under 60, Movement's own gate is
        /// 10) and up at least <see cref="MotionClimbRise"/> u over the step before. A landing at speed is a step-up too, but it only
        /// corrects the dot's sunk position (10 u at most in the recorded runs).
        /// </summary>
        static bool MotionClimbStep(List<TechStep> steps, int i)
        {
            TechStep step = steps[i];
            foreach (TechEvent e in step.Calls)
                if (e.Call == TechCall.Respawn) return false;
            return MotionSteppedUp(step) && step.After.Vy < 60f && step.After.Y - MotionBefore(steps, i).Y >= MotionClimbRise;
        }

        /// <summary>
        /// Fast steps (blocks and slopes are one mechanism, the description of one is "same as" the other): the dot steps up many
        /// times in a row, each climb within 12 steps of the last, three or more. A climb is a step-up that lifts the dot (see
        /// <see cref="MotionClimbStep"/>). Reported on the third.
        /// </summary>
        [TechCheck("fast-steps-slopes", "fast-steps-blocks")]
        static int MotionFastSteps(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            int last = -1, chain = 0;
            for (int i = from; i <= to; i++)
            {
                if (!MotionClimbStep(steps, i)) continue;
                chain = last >= 0 && i - last <= 12 ? chain + 1 : 1;
                last = i;
                if (chain >= 3) return i;
            }
            return -1;
        }

        /// <summary>
        /// Fast steps blocks clip: the step-up is a teleport, so a step-up puts the hitbox across a solid and the push-out lifts the
        /// dot onto it, which is a second step-up the next tick without moving on. Two climbs at most two steps apart that lift the dot
        /// 60 u or more together (one step-up lifts 44 at most), stay within 5 u sideways, and no jump or dash press in them: the
        /// dot went up by teleports alone, in place, not by running up stairs.
        /// </summary>
        [TechCheck("fast-steps-blocks-clip")]
        static int MotionFastStepsClip(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            int last = -1;
            for (int b = from; b <= to; b++)
            {
                if (!MotionClimbStep(steps, b)) continue;
                int a = last;
                last = b;
                if (a < 0 || b - a > 2) continue;
                if (steps[b].After.Y - MotionBefore(steps, a).Y < 60f || Math.Abs(steps[b].After.X - steps[a].After.X) > 5f) continue;
                bool pressed = false;
                for (int k = a; k <= b; k++) pressed |= steps[k].JumpPress || steps[k].DashPress;
                if (!pressed) return b;
            }
            return -1;
        }

        /// <summary>
        /// Fast steps slopes glitch: the step-up teleports the dot into the ceiling over the step and the ceiling pushes it back down,
        /// so it steps up again every tick, and it lives: five step-up steps in a row (Movement kills a squish past
        /// SquishGraceFrames 4, five squished ticks) with the jump's short hitbox on (the jump spam: the collider is the 54 tall one,
        /// whose overlap stays under squishDistance) and no death within the next three steps. Reported on the fifth. The record's
        /// Squished stays 0 here (that is the point), so it cannot be read.
        /// </summary>
        [TechCheck("fast-steps-slopes-glitch")]
        static int MotionFastStepsGlitch(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            int chain = 0;
            for (int i = from; i <= to; i++)
            {
                chain = MotionSteppedUp(steps[i]) && steps[i].After.ColliderH < MotionStandingH ? chain + 1 : 0;
                if (chain >= 5 && !Dies(steps, i, 3)) return i;
            }
            return -1;
        }

        /// <summary>A step-up (not an inwall climb) happened in the step's frames.</summary>
        static bool MotionSteppedUp(TechStep step)
        {
            bool up = false;
            foreach (TechEvent e in step.Calls)
            {
                if (e.Call != TechCall.StepUp) continue;
                float rays = e.State.RaysX;
                if (rays != 0f && (rays > 0f) != e.State.Right) return false;
                up = true;
            }
            return up;
        }

        /// <summary>
        /// Roof squeeze: the dot runs into something its standing hitbox (72 tall) does not fit under and stalls (on the ground,
        /// pushing along its Velocity.x of 200 or more, x not moving), then a jump press shrinks the hitbox to the 54 tall one and
        /// the dot goes on under it: from the next step on it stays within 14 u of the stall's height (a jump would rise 16 a step;
        /// the roof stops it) and covers 5 u a step or more. Reported on the jump.
        /// </summary>
        [TechCheck("roof-squeeze")]
        static int MotionRoofSqueeze(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            for (int j = Math.Max(from, 2); j <= to; j++)
            {
                if (!steps[j].JumpPress || steps[j].After.ColliderH >= MotionStandingH || steps[j - 1].After.ColliderH < MotionStandingH) continue;
                if (!MotionStalled(steps, j - 1)) continue;
                MoveState stall = steps[j - 1].After;
                int dir = Math.Sign(stall.Vx), end = Math.Min(to, j + 3);
                if (end - j < 2) continue;
                bool flat = true;
                for (int k = j; k <= end && flat; k++) flat = steps[k].After.Y - stall.Y <= 14f;
                if (flat && (steps[end].After.X - stall.X) * dir >= 5f * (end - j + 1)) return j;
            }
            return -1;
        }

        /// <summary>Standing on the ground with the full hitbox, pushing along at Velocity.x 200 or more and not getting anywhere.</summary>
        static bool MotionStalled(List<TechStep> steps, int k)
        {
            if (k < 1) return false;
            MoveState a = steps[k].After, before = steps[k - 1].After;
            return a.Ground && a.ColliderH >= MotionStandingH && !a.Dash && Math.Abs(a.Vx) >= 200f && Math.Sign(a.MoveX) == Math.Sign(a.Vx)
                && Math.Abs(a.X - before.X) < 1.5f;
        }

        /// <summary>
        /// Gap stick: in a shaft too narrow for the hitbox's turn, turning every tick swings the hitbox into the walls and a ground ray
        /// starts inside one, which grounds the dot every tick (Velocity.y set to 0, one tick of gravity left) with no floor under it.
        /// Eight steps in a row on which the dot is grounded with the full hitbox, faces the other way than the step before, stays
        /// within 6 u sideways and still sinks 0.1-2 u (a dot on a floor does not sink: only the 4 steps after a step-up settle).
        /// </summary>
        [TechCheck("gap-stick")]
        static int MotionGapStick(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            int chain = 0;
            for (int i = Math.Max(from, 1); i <= to; i++)
            {
                MoveState a = steps[i].After, before = steps[i - 1].After;
                float dy = a.Y - before.Y;
                chain = a.Ground && a.ColliderH >= MotionStandingH && !a.Dash && a.Right != before.Right && dy > -2f && dy < -0.1f
                    && Math.Abs(a.X - before.X) < 6f ? chain + 1 : 0;
                if (chain >= 8) return i;
            }
            return -1;
        }

        /// <summary>
        /// Edge stop: turning into a wall's bottom edge on the way up. The turn swings the hitbox (offset x 5) into the wall, the
        /// overlap is shallower upward than sideways, so the depenetration pushes the dot down out of the wall's bottom face and the
        /// contact is a ceiling: Velocity.y and momentum.y are zeroed. On a step where the dot faces the other way, is still rising
        /// at 300 or more (the step before too) and ends lower than the step before, with Velocity.y at 50 or less on the next
        /// step: the jump stopped dead. A plain ceiling bonk does not turn the dot nor push it down. Reported on the step with
        /// Velocity.y zeroed.
        /// </summary>
        [TechCheck("edge-stop")]
        static int MotionEdgeStop(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            for (int k = Math.Max(from, 1); k + 1 <= to; k++)
            {
                MoveState before = steps[k - 1].After, now = steps[k].After, next = steps[k + 1].After;
                if (before.Vy >= 300f && now.Vy >= 300f && now.Right != before.Right && now.Y - before.Y < -1f && next.Vy <= 50f && !now.Dash && !before.Dash)
                    return k + 1;
            }
            return -1;
        }
    }
}
