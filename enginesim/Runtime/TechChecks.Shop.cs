using System;
using System.Collections.Generic;
using System.Linq;
using IGTAPTasMod;

namespace IGTAP.EngineSim
{
    public static partial class TechChecks
    {
        const float ShopLandingMomentumX = 460f, ShopLandingMomentumDown = 100f, ShopLandingKept = 0.9f;
        const float ShopStoredBack = 0.95f, ShopStoredMin = 900f, ShopStoredSlack = 1f;
        const float ShopRunSpeed = 460f;
        const float ShopOverhangDrop = 2f;
        const int ShopOverhangSteps = 2;
        const float ShopSlopeDrop = 0.2f, ShopSlopeSteep = 2f;
        const int ShopSlopeSteps = 4;

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

        static bool ShopJumped(TechStep step, out MoveState input, out MoveState sprite)
        {
            input = sprite = default;
            foreach (TechEvent e in step.Calls) if (e.Call == TechCall.Kick) return false;
            return ShopInputSprite(step, out input, out sprite) && input.Buffer && !sprite.Buffer && sprite.Vy > input.Vy && sprite.Vy > 0f;
        }

        static bool ShopGroundJump(TechStep step, out MoveState input)
        {
            return ShopJumped(step, out input, out MoveState sprite) && input.Ground && sprite.WallJumps == input.WallJumps && sprite.AirJumps == input.AirJumps;
        }

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

        static bool ShopLandingJump(TechStep step, out MoveState input)
        {
            input = default;
            return (step.Events & StepEvents.Landed) != 0 && ShopGroundJump(step, out input);
        }

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

        [TechCheck("inslope-wallbounce")]
        static int ShopInslopeWallbounce(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            for (int k = Math.Max(from, 1); k <= to; k++)
            {
                TechStep step = steps[k];
                if (!step.Calls.Any(e => e.Call == TechCall.Kick) || step.After.Vx == 0f) continue;
                float side = -Math.Sign(step.After.Vx);
                if (steps[k - 1].Slope == side || step.Slope == side) return k;
            }
            return -1;
        }
    }
}
