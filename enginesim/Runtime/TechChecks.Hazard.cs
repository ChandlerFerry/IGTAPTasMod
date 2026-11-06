using System;
using System.Collections.Generic;
using System.Linq;
using IGTAPTasMod;

namespace IGTAP.EngineSim
{
    public static partial class TechChecks
    {
        [TechCheck("edge-spike-jump")]
        static int HazardEdgeSpikeJump(TechSteps run, int from, int to) => HazardEdgeJump(run, from, to, false);

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

        [TechCheck("corner-jump")]
        static int HazardCornerJump(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            for (int j = Math.Max(from, 2); j <= to; j++)
            {
                if (!HazardGroundJump(steps[j])) continue;
                TechStep stand = steps[j - 1], before = steps[j - 2];
                if (!stand.SpikeUnder || !before.SpikeUnder || Math.Abs(stand.After.X - before.After.X) >= 0.5f) continue;
                if (Dies(steps, j, 3)) continue;
                return j;
            }
            return -1;
        }

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

        static bool HazardHeldSideways(List<TechStep> steps, int j)
        {
            for (int k = j + 1; k <= Math.Min(steps.Count - 1, j + 16); k++)
            {
                MoveState a = steps[k].After, b = steps[k - 1].After;
                if (Math.Abs(a.X - b.X) < 0.5f && (a.Wall || Math.Abs(a.Vx) >= 100f)) return true;
            }
            return false;
        }

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

        [TechCheck("spike-super")]
        static int HazardSpikeSuper(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            foreach (Span super in run.Spans)
            {
                int s = super.End;
                if (!HazardIsSuper(super) || s < from || s > to) continue;
                foreach (Span grace in run.Spans)
                    if (grace.Tech == "SpikeGrace" && grace.End >= s - 1 && grace.End <= s && !HazardSwapGraze(steps, grace.End)) return s;
            }
            return -1;
        }

        static bool HazardSwapGraze(List<TechStep> steps, int k) =>
            steps[k].Swapped && steps[k].DashPress && steps[k].Spike && (k == 0 || !steps[k - 1].Spike);

        static bool HazardIsSuper(Span span) => span.Tech == "SuperJump" || span.Tech == "ReversedSuper" || span.Tech == "HighSuper";

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

        [TechCheck("super-switching-spikes")]
        static int HazardSuperSwitchingSpikes(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            foreach (Span super in run.Spans)
            {
                int s = super.End;
                if (!HazardIsSuper(super) || s < from || s > to) continue;
                for (int k = Math.Max(s - 1, 0); k <= s; k++)
                    if (HazardSwapGraze(steps, k) && !Dies(steps, k, 3)) return s;
            }
            return -1;
        }

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

        // Mathf.Sign returns 1 for zero.
        static float HazardSign(float x) => x < 0f ? -1f : 1f;

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
