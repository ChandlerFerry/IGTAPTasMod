using System;
using System.Collections.Generic;
using System.Linq;
using IGTAPTasMod;

namespace IGTAP.EngineSim
{
    public static partial class TechChecks
    {
        const float MotionStandingH = 70f;

        const float MotionClimbRise = 13f;

        const float MotionPumpGrowth = 1.05f;

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

        static MoveState MotionBefore(List<TechStep> steps, int i) =>
            i > 0 ? steps[i - 1].After : steps[i].Calls.Count > 0 ? steps[i].Calls[0].State : steps[i].After;

        static bool MotionClimbStep(List<TechStep> steps, int i)
        {
            TechStep step = steps[i];
            foreach (TechEvent e in step.Calls)
                if (e.Call == TechCall.Respawn) return false;
            return MotionSteppedUp(step) && step.After.Vy < 60f && step.After.Y - MotionBefore(steps, i).Y >= MotionClimbRise;
        }

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

        static bool MotionStalled(List<TechStep> steps, int k)
        {
            if (k < 1) return false;
            MoveState a = steps[k].After, before = steps[k - 1].After;
            return a.Ground && a.ColliderH >= MotionStandingH && !a.Dash && Math.Abs(a.Vx) >= 200f && Math.Sign(a.MoveX) == Math.Sign(a.Vx)
                && Math.Abs(a.X - before.X) < 1.5f;
        }

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

        [TechCheck("updiag-wall-climb")]
        static int MotionUpdiagWallClimb(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            bool turned = false;
            for (int i = 0; i <= to; i++)
            {
                MoveState pre = i > 0 ? steps[i - 1].After : steps[0].Calls.Count > 0 ? steps[0].Calls[0].State : steps[0].After;
                MoveState now = steps[i].After;
                if (!now.Dash) { turned = false; continue; }
                if (!pre.Dash || steps[i].DashPress)
                {
                    turned = pre.Wall && now.DashY > 0f && now.DashX != 0f && now.Vx != 0f && Math.Sign(now.DashX) != Math.Sign(now.Vx);
                    continue;
                }
                if (i >= from && turned && pre.AirDashes == 0 && now.AirDashes > 0 && now.Vy > 0f && !now.Ground) return i;
            }
            return -1;
        }
    }
}
