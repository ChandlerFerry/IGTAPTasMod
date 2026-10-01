using System;
using System.Collections.Generic;
using System.Linq;
using IGTAPTasMod;

namespace IGTAP.EngineSim
{
    /// <summary>
    /// Tech checks for the shop boxes, the landing tick, the dash's stored momentum, the quick restart's left-over wall jump lock and
    /// the slanted surfaces. Not checked: late-gate-touch (the tech is the course timer starting from a touch of the start gate at
    /// the last moment of a flying start; a timer start alone is every ordinary start, and what tells it apart is where the gate is
    /// and which side the dot came from, which the record does not hold - nor does the lab replay ever show the timer start).
    /// </summary>
    public static partial class TechChecks
    {
        // Momentum the landing tick keeps to matter: past the stick's own top speed (Velocity.x 460), so the 25% a tick later costs
        // 115 u/s or more. Momentum.y a landing jump keeps: a spring's drop, not the last of a decayed one.
        const float ShopLandingMomentumX = 460f, ShopLandingMomentumDown = 100f, ShopLandingKept = 0.9f;
        // endDash gives 95% of preDashMomentum back; a super's own momentum is 900, so storing pays past that.
        const float ShopStoredBack = 0.95f, ShopStoredMin = 900f, ShopStoredSlack = 1f;
        // A lock-carried first step runs at the full stick speed (a plain first step is 102.2).
        const float ShopRunSpeed = 460f;
        // A step that slides down a slanted overhang: moved at least this far down against a vertical velocity that is not down.
        const float ShopOverhangDrop = 2f;
        const int ShopOverhangSteps = 2;
        // Slope sliding: grounded on a slope face and creeping down it, the drop more than twice the sideways move (steeper than
        // about 63 degrees), for this many steps in a row.
        const float ShopSlopeDrop = 0.2f, ShopSlopeSteep = 2f;
        const int ShopSlopeSteps = 4;

        /// <summary>The first Input call of the step and the Sprite call after it: what getPlayerControlledInput did.</summary>
        static bool ShopInputSprite(TechStep step, out MoveState input, out MoveState sprite)
        {
            input = sprite = default;
            bool seen = false;
            foreach (TechEvent e in step.Calls)
            {
                if (e.Call == TechCall.Input)
                {
                    input = e.State;
                    seen = true;
                }
                else if (e.Call == TechCall.Sprite && seen)
                {
                    sprite = e.State;
                    return true;
                }
            }
            return false;
        }

        /// <summary>A jump went in on this step: the buffered press spent in getPlayerControlledInput (Velocity.y up), no wall kick.</summary>
        static bool ShopJumped(TechStep step, out MoveState input, out MoveState sprite)
        {
            input = sprite = default;
            foreach (TechEvent e in step.Calls) if (e.Call == TechCall.Kick) return false;
            return ShopInputSprite(step, out input, out sprite) && input.Buffer && !sprite.Buffer && sprite.Vy > input.Vy && sprite.Vy > 0f;
        }

        /// <summary>A jump off the ground (coyote included): no air jump or wall jump spent.</summary>
        static bool ShopGroundJump(TechStep step, out MoveState input)
        {
            return ShopJumped(step, out input, out MoveState sprite) && input.Ground && sprite.WallJumps == input.WallJumps && sprite.AirJumps == input.AirJumps;
        }

        /// <summary>A step of a dash (the freeze and the flight, a super's first step too) or the one that pressed it.</summary>
        static bool ShopDashStep(TechStep step)
        {
            if (step.DashPress || step.After.Cutscene == Movement.cutsceneModes.dash) return true;
            foreach (TechEvent e in step.Calls) if (e.Call == TechCall.Super) return true;
            return false;
        }

        static bool ShopHasCall(TechStep step, TechCall call)
        {
            foreach (TechEvent e in step.Calls) if (e.Call == call) return true;
            return false;
        }

        /// <summary>
        /// Jump-through buy: one jump's arc buys two shop boxes on the way. The arc is a jump (a buffered press spent, no wall kick)
        /// and the airborne steps after it up to the landing, a wall cling, a dash, a spring, a respawn or a death; the steps whose
        /// buy trigger held the dot (<see cref="TechStep.Boxes"/>, the active upgrade boxes by id) add their boxes, and it is the
        /// step that makes two different ones. A dash in the arc ends it (a dash pass is dash-through-buy) and a hop per box is two arcs.
        /// </summary>
        [TechCheck("jump-through-buy")]
        static int ShopJumpThroughBuy(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool arc = false;
            for (int i = 0; i <= to && i < steps.Count; i++)
            {
                TechStep step = steps[i];
                MoveState after = step.After;
                bool free = !ShopDashStep(step) && !after.Ground && !after.Wall && after.Cutscene == Movement.cutsceneModes.none;
                if (!free || (step.Events & (StepEvents.SpringHit | StepEvents.Landed)) != 0 || step.Died || ShopHasCall(step, TechCall.Respawn))
                {
                    arc = false;
                    seen.Clear();
                }
                if (free && ShopJumped(step, out _, out _))
                {
                    arc = true;
                    seen.Clear();
                }
                if (!arc || step.Boxes == null) continue;
                foreach (string box in step.Boxes)
                    if (seen.Add(box) && seen.Count >= 2 && i >= from) return i;
            }
            return -1;
        }

        /// <summary>
        /// Dash-through buy: one dash pass buys two shop boxes. The steps of a dash in a row (<see cref="ShopDashStep"/>) add the
        /// boxes whose buy trigger held the dot; it is the step that makes two different ones.
        /// </summary>
        [TechCheck("dash-through-buy")]
        static int ShopDashThroughBuy(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i <= to && i < steps.Count; i++)
            {
                TechStep step = steps[i];
                if (!ShopDashStep(step))
                {
                    seen.Clear();
                    continue;
                }
                if (step.Boxes == null) continue;
                foreach (string box in step.Boxes)
                    if (seen.Add(box) && seen.Count >= 2 && i >= from) return i;
            }
            return -1;
        }

        /// <summary>A jump leaves on the landing step: the ground rays touched down on it (<see cref="StepEvents.Landed"/>) and the
        /// buffered press spent on the same step. A jump a step later comes after the ground friction.</summary>
        static bool ShopLandingJump(TechStep step, out MoveState input)
        {
            input = default;
            return (step.Events & StepEvents.Landed) != 0 && ShopGroundJump(step, out input);
        }

        /// <summary>
        /// Landing-tick jump: a jump on the landing step with momentum.x past the stick's own speed, and it comes through the step
        /// (90% or more kept, the air's decay): ground friction (x0.75 on metal, x0.85 on mossy) skips the landing tick.
        /// </summary>
        [TechCheck("landing-tick-jump")]
        static int ShopLandingTickJump(TechSteps run, int from, int to)
        {
            for (int i = from; i <= to; i++)
            {
                TechStep step = run.Steps[i];
                if (!ShopLandingJump(step, out MoveState input)) continue;
                float before = Math.Abs(input.Mx), now = Math.Abs(step.After.Mx);
                if (before >= ShopLandingMomentumX && now >= ShopLandingKept * before && Math.Sign(step.After.Mx) == Math.Sign(input.Mx)) return i;
            }
            return -1;
        }

        /// <summary>
        /// Landing jump keeps downward momentum: a jump on the landing step with momentum.y down of 100 or more that the step leaves
        /// standing (90% or more of it): OnCollisionStay2D zeroes it on the floor contact but not while recentlyJumped.
        /// </summary>
        [TechCheck("landing-jump-momentum-carry")]
        static int ShopLandingJumpMomentumCarry(TechSteps run, int from, int to)
        {
            for (int i = from; i <= to; i++)
            {
                TechStep step = run.Steps[i];
                if (!ShopLandingJump(step, out MoveState input)) continue;
                if (input.My <= -ShopLandingMomentumDown && step.After.My <= ShopLandingKept * input.My) return i;
            }
            return -1;
        }

        /// <summary>
        /// Dash momentum storage: a dash ends on its own (the step opens in getPlayerControlledInput with the cutscene gone: a super
        /// never gets there) and endDash gave 95% of preDashMomentum.x back, 900 or more (what a super's own momentum is): the
        /// momentum the dash zeroed came back whole, not worn by the ground's friction or lost to a spring's lock or a wall. The
        /// check looks at the momentum itself, so a dash that was ended any other way (momentum back at 0) is not it.
        /// </summary>
        [TechCheck("dash-momentum-storage")]
        static int ShopDashMomentumStorage(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            for (int i = Math.Max(from, 1); i <= to; i++)
            {
                TechStep step = steps[i];
                MoveState dashing = steps[i - 1].After;
                if (!dashing.Dash || dashing.Cutscene != Movement.cutsceneModes.dash) continue;
                if (step.Calls.Count == 0 || step.Calls[0].Call != TechCall.Input) continue;
                MoveState input = step.Calls[0].State;
                if (input.Dash || input.Cutscene == Movement.cutsceneModes.dash) continue;
                if (Math.Abs(input.Mx) >= ShopStoredMin && Math.Abs(input.Mx - ShopStoredBack * dashing.PreDashX) <= ShopStoredSlack) return i;
            }
            return -1;
        }

        /// <summary>
        /// Momentum after quick restart, the wall jump's lock: the respawn zeroes Velocity, yet the first input of the new life runs
        /// at the full stick speed (460, not the first step's 102) because wallJumpMovementLockRemaining and wallJumpDirection
        /// outlive it. The two other cases of the tech (a zip mover's history, a ground bounce's window) are not checked.
        /// </summary>
        [TechCheck("momentum-after-quick-restart")]
        static int ShopMomentumAfterQuickRestart(TechSteps run, int from, int to)
        {
            for (int i = from; i <= to; i++)
            {
                TechStep step = run.Steps[i];
                int respawn = step.Calls.FindIndex(call => call.Call == TechCall.Respawn);
                if (respawn < 0) continue;
                MoveState input = default, sprite = default;
                bool seen = false;
                for (int k = respawn + 1; k < step.Calls.Count; k++)
                {
                    TechEvent e = step.Calls[k];
                    if (e.Call == TechCall.Input && !seen)
                    {
                        input = e.State;
                        seen = true;
                    }
                    else if (e.Call == TechCall.Sprite && seen)
                    {
                        sprite = e.State;
                        if (Math.Abs(input.Vx) < 1f && Math.Abs(sprite.Vx) >= ShopRunSpeed - 1f) return i;
                        break;
                    }
                }
            }
            return -1;
        }

        /// <summary>
        /// Overhang slide: the dot touches a slanted surface that faces down (<see cref="TechStep.Overhang"/>) in the air and moves
        /// down by 2 u or more a step while its Velocity.y + momentum.y is not down: Box2D took the into-surface part of the
        /// sideways speed and left the part along the surface, which points down. Two such steps in a row.
        /// </summary>
        [TechCheck("overhang-slide")]
        static int ShopOverhangSlide(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            int slid = 0;
            for (int i = 1; i <= to && i < steps.Count; i++)
            {
                TechStep step = steps[i];
                MoveState after = step.After;
                if (step.Overhang != 0f && !after.Ground && after.Y - steps[i - 1].After.Y <= -ShopOverhangDrop && after.Vy + after.My >= 0f) slid++;
                else slid = 0;
                if (slid >= ShopOverhangSteps && i >= from) return i;
            }
            return -1;
        }

        /// <summary>
        /// Slope sliding: the dot is grounded on a slope face (<see cref="TechStep.Slope"/>) too steep to hold it and creeps down it,
        /// 0.2 u or more a step and more than twice as far down as sideways, four steps in a row.
        /// </summary>
        [TechCheck("slope-sliding")]
        static int ShopSlopeSliding(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            int slid = 0;
            for (int i = 1; i <= to && i < steps.Count; i++)
            {
                TechStep step = steps[i];
                MoveState after = step.After, before = steps[i - 1].After;
                float drop = before.Y - after.Y, side = Math.Abs(after.X - before.X);
                if (step.Slope != 0f && after.Ground && drop >= ShopSlopeDrop && drop > ShopSlopeSteep * side) slid++;
                else slid = 0;
                if (slid >= ShopSlopeSteps && i >= from) return i;
            }
            return -1;
        }
    }
}
