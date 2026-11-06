using System;
using System.Collections.Generic;
using System.Linq;
using IGTAPTasMod;

namespace IGTAP.EngineSim
{
    public static partial class TechChecks
    {
        const int SuperFlipTicks = 4, SuperDashSteps = 12, SuperFlightSteps = 60, SuperSquishSteps = 10;

        enum SuperSource { Floor, Clip, Respawn, Start }

        enum SuperInwallKind { Plain, Into, Reverse }

        static MoveState SuperFirst(TechSteps run)
        {
            TechStep step = run.Steps[0];
            return step.Calls.Count > 0 ? step.Calls[0].State : step.After;
        }

        static MoveState SuperBefore(TechSteps run, int i) => i > 0 ? run.Steps[i - 1].After : SuperFirst(run);

        static bool SuperFresh(TechSteps run, MoveState m) => m.Ground && m.Coyote >= run.MaxCoyote;

        static bool SuperHasRespawn(TechStep step)
        {
            foreach (TechEvent e in step.Calls) if (e.Call == TechCall.Respawn) return true;
            return false;
        }

        static List<(int step, MoveState at)> SuperJumps(TechSteps run)
        {
            var jumps = new List<(int step, MoveState at)>();
            for (int i = 0; i < run.Steps.Count; i++)
            {
                bool open = false;
                MoveState super = default;
                foreach (TechEvent e in run.Steps[i].Calls)
                {
                    switch (e.Call)
                    {
                        case TechCall.Input:
                            open = false;
                            break;
                        case TechCall.Super:
                            open = true;
                            super = e.State;
                            break;
                        case TechCall.Sprite:
                            if (open && super.Buffer && !e.State.Buffer) jumps.Add((i, super));
                            open = false;
                            break;
                    }
                }
            }
            return jumps;
        }

        static int SuperDashStart(TechSteps run, int e)
        {
            for (int j = e; j >= 0 && e - j <= SuperDashSteps; j--)
            {
                MoveState a = run.Steps[j].After;
                if (a.Dash && a.SinceDash == 0f && !SuperBefore(run, j).Dash) return j;
            }
            return -1;
        }

        static bool SuperReversed(MoveState at) => at.MoveX != 0f && at.DashX != 0f && Math.Sign(at.MoveX) != Math.Sign(at.DashX);

        static SuperSource SuperGroundSource(TechSteps run, int e, MoveState at, out int landing)
        {
            List<TechStep> steps = run.Steps;
            MoveState first = SuperFirst(run);
            landing = -1;
            int j = e;
            if (!SuperFresh(run, at))
            {
                j = -1;
                for (int k = e - 1; k >= 0; k--)
                {
                    if (SuperFresh(run, steps[k].After)) { j = k; break; }
                    if (SuperHasRespawn(steps[k])) return SuperSource.Respawn;
                }
                if (j < 0) return SuperFresh(run, first) ? SuperSource.Floor : SuperSource.Start;
            }
            int c = j;
            while (c > 0 && steps[c - 1].After.Ground) c--;
            landing = c;
            if (c == 0 && first.Ground) return SuperSource.Floor;
            if (SuperBefore(run, c).Vy <= 0f) return SuperSource.Floor;
            for (int k = Math.Max(0, c - SuperFlipTicks); k < c; k++)
                if (steps[k].After.Right != SuperBefore(run, k).Right) return SuperSource.Clip;
            return SuperSource.Floor;
        }

        [TechCheck("checkpoint-midair-super")]
        static int SuperCheckpointMidairSuper(TechSteps run, int from, int to)
        {
            foreach ((int e, MoveState at) in SuperJumps(run))
            {
                if (e < from || e > to || at.CanHyper) continue;
                SuperSource source = SuperGroundSource(run, e, at, out _);
                if (source == SuperSource.Respawn) return e;
                if (source != SuperSource.Start) continue;
                MoveState first = SuperFirst(run);
                if (first.Ground && first.Vx == 0f && first.Mx == 0f && first.Vy <= 0f) return e;
            }
            return -1;
        }

        [TechCheck("checkpoint-midair-walljump", "wall-coyote-restart")]
        static int SuperRestartWallJump(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            foreach (Span span in run.Spans)
            {
                int e = span.End;
                if (span.Tech != "StoredWallJump" || e < from || e > to) continue;
                if (SuperHasRespawn(steps[e])) return e;
                for (int k = e - 1; k >= 0; k--)
                {
                    MoveState a = steps[k].After;
                    if (a.Wall && a.WallCoyote >= run.MaxWallCoyote) break;
                    if (SuperHasRespawn(steps[k])) return e;
                }
            }
            return -1;
        }

        static bool SuperInwall(TechSteps run, int e, MoveState at, out SuperInwallKind kind, out int landing)
        {
            kind = SuperInwallKind.Plain;
            landing = -1;
            if (at.CanHyper || at.DashY > 0f || at.DashX == 0f) return false;
            if (SuperGroundSource(run, e, at, out landing) != SuperSource.Clip) return false;
            int wall = SuperBefore(run, landing).Right ? 1 : -1;
            kind = SuperReversed(at) ? SuperInwallKind.Reverse : Math.Sign(at.DashX) == wall ? SuperInwallKind.Into : SuperInwallKind.Plain;
            return true;
        }

        static int SuperInwallSuper(TechSteps run, int from, int to, SuperInwallKind want)
        {
            foreach ((int e, MoveState at) in SuperJumps(run))
            {
                if (e >= from && e <= to && SuperInwall(run, e, at, out SuperInwallKind kind, out _) && kind == want) return e;
            }
            return -1;
        }

        [TechCheck("inwall-super")]
        static int SuperInwallPlain(TechSteps run, int from, int to) => SuperInwallSuper(run, from, to, SuperInwallKind.Plain);

        [TechCheck("inwall-reverse-super")]
        static int SuperInwallReverse(TechSteps run, int from, int to) => SuperInwallSuper(run, from, to, SuperInwallKind.Reverse);

        [TechCheck("inwall-super-into-wall")]
        static int SuperInwallIntoWall(TechSteps run, int from, int to) => SuperInwallSuper(run, from, to, SuperInwallKind.Into);

        static int SuperStoredBoost(TechSteps run, int from, int to, bool hyper)
        {
            List<(int step, MoveState at)> jumps = SuperJumps(run);
            for (int e = from; e <= to; e++)
                foreach (TechEvent kick in run.Steps[e].Calls)
                {
                    if (kick.Call != TechCall.Kick || kick.Value >= 1f || kick.State.Wall || kick.State.WallCoyote <= 0f) continue;
                    int dash = Math.Max(0, e - (int)Math.Round(kick.State.SinceDash));
                    int n = jumps.FindLastIndex(j => j.step < e);
                    if (n >= 0 && jumps[n].step >= dash && jumps[n].at.CanHyper == hyper) return e;
                }
            return -1;
        }

        [TechCheck("stored-superbounce")]
        static int SuperStoredSuperbounce(TechSteps run, int from, int to) => SuperStoredBoost(run, from, to, false);

        [TechCheck("hyper-stored-superbounce")]
        static int SuperHyperStoredSuperbounce(TechSteps run, int from, int to) => SuperStoredBoost(run, from, to, true);

        [TechCheck("reverse-super-pauselift")]
        static int SuperReversePauselift(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            List<(int step, MoveState at)> jumps = SuperJumps(run);
            foreach (Span span in run.Spans)
            {
                int p = span.End;
                if (span.Tech != "Pauselift" || p < from || p > to) continue;
                int dash = SuperDashStart(run, p);
                if (dash < 0) continue;
                int n = jumps.FindLastIndex(j => j.step < dash);
                if (n < 0) continue;
                (int k, MoveState at) = jumps[n];
                if (at.CanHyper || !SuperReversed(at) || Math.Sign(at.MoveX) != Math.Sign(steps[dash].After.DashX) || dash - k > SuperFlightSteps) continue;
                bool landed = false;
                for (int m = k + 1; m <= p && !landed; m++)
                    landed = (steps[m].Events & StepEvents.Landed) != 0 || SuperFresh(run, steps[m].After);
                if (!landed) return p;
            }
            return -1;
        }

        [TechCheck("reverse-super-switching-block")]
        static int SuperReverseSwitchingBlock(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            foreach ((int e, MoveState at) in SuperJumps(run))
            {
                if (e < from || e > to || at.CanHyper || at.DashY > 0f || !SuperReversed(at)) continue;
                int dash = SuperDashStart(run, e);
                if (dash < 0 || !SuperFresh(run, steps[dash].After) || !at.Ground || SuperFresh(run, at)) continue;
                for (int k = dash; k <= e; k++) if (steps[k].Swapped) return e;
            }
            return -1;
        }

        [TechCheck("super-switching-blocks")]
        static int SuperSwitchingBlocks(TechSteps run, int from, int to)
        {
            List<TechStep> steps = run.Steps;
            foreach ((int e, MoveState at) in SuperJumps(run))
            {
                if (e < from || e > to || at.CanHyper || at.DashY > 0f) continue;
                int dash = SuperDashStart(run, e);
                if (dash < 0) continue;
                int swap = -1;
                for (int k = dash; k <= e && swap < 0; k++) if (steps[k].Swapped) swap = k;
                if (swap < 0 || SuperBefore(run, swap).Squished > 0) continue;
                int end = Math.Min(steps.Count - 1, dash + SuperSquishSteps);
                bool squished = false, died = false;
                for (int k = dash; k <= end; k++)
                {
                    died |= steps[k].Died;
                    squished |= k >= swap && steps[k].After.Squished > 0;
                }
                if (squished && !died) return e;
            }
            return -1;
        }
    }
}
