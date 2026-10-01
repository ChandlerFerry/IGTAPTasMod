using System;
using System.Collections.Generic;
using System.Linq;
using IGTAPTasMod;

namespace IGTAP.EngineSim
{
    // Tech checks for the super family: each tech is a super (CheckForSuperJump: the jump pressed in a dash while onGround holds), a
    // stored wall jump or a pauselift plus the condition that makes it the tech. onGround is set only by the ground rays (which
    // reset coyoteFrames to its maximum) and by Movement.respawn (which leaves coyoteFrames alone), so a super off a ground that
    // is not "fresh" (onGround with coyote at the maximum) is held by coyote, and where that ground came from tells the techs apart:
    //   Floor    a real floor contact, or the contact the run started on;
    //   Clip     the contact began while the dot was rising, within 4 ticks of a facing flip: the GC/KB wall clip (the turned hitbox's
    //            ground ray starts inside a wall and grounds the dot in the air);
    //   Respawn  Movement.respawn put it above the floor and no ray has touched ground since;
    //   Start    nothing in the run explains it (it was already held when the run began).
    // The checks read the record only (no geometry); each method's summary says what it needs and where it can be fooled.
    public static partial class TechChecks
    {
        const int SuperFlipTicks = 4, SuperDashSteps = 12, SuperFlightSteps = 60, SuperSquishSteps = 10;

        enum SuperSource { Floor, Clip, Respawn, Start }

        enum SuperInwallKind { Plain, Into, Reverse }

        // ---- helpers ----

        static MoveState SuperFirst(TechSteps run)
        {
            TechStep step = run.Steps[0];
            return step.Calls.Count > 0 ? step.Calls[0].State : step.After;
        }

        /// <summary>The state before step <paramref name="i"/>: the previous step's, or the run's first.</summary>
        static MoveState SuperBefore(TechSteps run, int i) => i > 0 ? run.Steps[i - 1].After : SuperFirst(run);

        /// <summary>onGround from a ground ray this step (coyote full), not held by coyote.</summary>
        static bool SuperFresh(TechSteps run, MoveState m) => m.Ground && m.Coyote >= run.MaxCoyote;

        static bool SuperHasRespawn(TechStep step)
        {
            foreach (TechEvent e in step.Calls) if (e.Call == TechCall.Respawn) return true;
            return false;
        }

        /// <summary>Every step where CheckForSuperJump took the buffered jump (the overlay's super: the Super call with the jump
        /// buffered, and the next Sprite call with it spent), with the state at that Super call: the SuperJump, ReversedSuper,
        /// HighSuper, BackwardsDashJump spans and the hyper's Wavedash / Ultra.</summary>
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

        /// <summary>The step the dash that is active at step <paramref name="e"/> started on (a dash lasts 11 steps), or -1.</summary>
        static int SuperDashStart(TechSteps run, int e)
        {
            for (int j = e; j >= 0 && e - j <= SuperDashSteps; j--)
            {
                MoveState a = run.Steps[j].After;
                if (a.Dash && a.SinceDash == 0f && !SuperBefore(run, j).Dash) return j;
            }
            return -1;
        }

        /// <summary>The stick against the dash at the jump: CheckForSuperJump sends the momentum the stick's way (the overlay's ReversedSuper).</summary>
        static bool SuperReversed(MoveState at) => at.MoveX != 0f && at.DashX != 0f && Math.Sign(at.MoveX) != Math.Sign(at.DashX);

        /// <summary>
        /// Where the onGround a super at step <paramref name="e"/> (the Super call's state <paramref name="at"/>) jumped from came
        /// from. A ray contact in the super's own step or the latest one before it; its episode starts at the first step of the
        /// unbroken onGround stretch (coyote ticks included) that holds it, and that start is a Clip when the dot was rising
        /// before it and its facing flipped in the 4 steps before (the overlay's wall clip signature: GcWallClip needs the same
        /// rising landing and flip). No contact since a respawn is Respawn; no contact in the run at all is Floor when the run
        /// began on a contact, else Start.
        /// </summary>
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

        // ---- checkpoint mid-air super ----

        /// <summary>
        /// Checkpoint mid air super: a super (any dash jump but a hyper) off an onGround that Movement.respawn left above the floor:
        /// a respawn in the run and no ground ray has touched anything since, so coyote alone holds it. The mined demo starts after
        /// its respawn (the miner drops windows with a reset), so a run that begins with the ground already held and unexplained
        /// counts too, when it looks like a respawn's state: Velocity and momentum x zero, not rising. A coyote super off a ledge
        /// the run walks off (a real contact first) is not it. Limit: a run that starts mid-air in that state from something other
        /// than a respawn (a platform vanishing under a dot standing still) cannot be told apart.
        /// </summary>
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

        // ---- stored wall jump through a respawn ----

        /// <summary>
        /// Checkpoint mid air walljump / wall coyote restart: a wall jump off no wall (the overlay's StoredWallJump: the press spends
        /// a wall jump with OnWall off and wallCoyoteFrames above 0) whose count a quick restart carried: Movement.respawn leaves
        /// wallCoyoteFrames and wallJumpsLeft alone, so the run has a respawn, and between it and the jump no wall cling (OnWall with
        /// the count refilled) set the count again. A count carried over the ground by a landing alone is the stored wall jump.
        /// </summary>
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

        // ---- inwall supers ----

        /// <summary>A non-hyper, non-high ground super whose onGround is a wall clip's: the kind by the wall's side (the facing
        /// the hitbox was turned to when the clip grounded the dot) and the dash and momentum against it.</summary>
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

        static int SuperInwallSuper(TechSteps run, int from, int to, SuperInwallKind want, bool refilled)
        {
            foreach ((int e, MoveState at) in SuperJumps(run))
            {
                if (e < from || e > to) continue;
                if (!SuperInwall(run, e, at, out SuperInwallKind kind, out int landing) || kind != want) continue;
                if (!refilled || SuperRefilled(run, e, at, landing)) return e;
            }
            return -1;
        }

        /// <summary>
        /// Inwall super: a wall clip's ground in mid air (rising, the facing flipped within 4 ticks before it), then the dash away
        /// from the wall with the jump on its first frame: onGround still holds, so it is a super (805 up, 876 momentum away) from
        /// inside the wall. The overlay names it BackwardsDashJump (a jump pressed with the dash after a flip with a wall behind),
        /// which a standing backwards dash jump on a floor is too: that one has no clip, so it is not this.
        /// </summary>
        [TechCheck("inwall-super")]
        static int SuperInwallPlain(TechSteps run, int from, int to) => SuperInwallSuper(run, from, to, SuperInwallKind.Plain, false);

        /// <summary>
        /// Inwall reverse super: the clip's ground and a super with the stick against the dash (ReversedSuper): the dash goes the
        /// way the clip left the dot facing, into the wall, and the stick flipped in the freeze sends the momentum away from it.
        /// </summary>
        [TechCheck("inwall-reverse-super")]
        static int SuperInwallReverse(TechSteps run, int from, int to) => SuperInwallSuper(run, from, to, SuperInwallKind.Reverse, false);

        /// <summary>
        /// Inwall super into wall: the clip's ground and a plain super (stick with the dash) whose dash and momentum go into the
        /// wall the clip grounded the dot on: the momentum pins the dot to the wall face while it rises.
        /// </summary>
        [TechCheck("inwall-super-into-wall")]
        static int SuperInwallIntoWall(TechSteps run, int from, int to) => SuperInwallSuper(run, from, to, SuperInwallKind.Into, false);

        /// <summary>
        /// Infinite mossy blocks stamina: an inwall super into the wall whose clip grounding refilled the abilities: the landing on
        /// the Ground-tagged wall (touchedSolidGround) put back air jumps, wall jumps or air dashes the dot had spent (a dash that
        /// starts on the landing step counts the one it spent). The same clip with the abilities full refilled nothing.
        /// </summary>
        [TechCheck("infinite-mossy-stamina")]
        static int SuperMossyStamina(TechSteps run, int from, int to) => SuperInwallSuper(run, from, to, SuperInwallKind.Into, true);

        /// <summary>The abilities went up across the landing step <paramref name="landing"/> (the Super call's state when the
        /// landing is the super's own step).</summary>
        static bool SuperRefilled(TechSteps run, int e, MoveState at, int landing)
        {
            MoveState pre = SuperBefore(run, landing), post = at;
            int started = 0;
            if (landing != e)
            {
                post = run.Steps[landing].After;
                if (post.Dash && post.SinceDash == 0f && !pre.Dash) started = 1;
            }
            return post.WallJumps > pre.WallJumps || post.AirJumps > pre.AirJumps || post.AirDashes + started > pre.AirDashes;
        }

        // ---- stored superbounce ----

        /// <summary>
        /// A dash-boosted wall jump off no wall inside a super's dash: a SpecialWallJump(0.6, ...) (Kick value below 1) with OnWall
        /// off and wallCoyoteFrames above 0, so the stored count took getPlayerControlledInput's wall branch within dashBoostMax of
        /// the dash, and the latest super jump since that dash started was a hyper (<paramref name="hyper"/>) or not.
        /// </summary>
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

        /// <summary>
        /// Stored superbounce: a stored wall jump taken as the dash boost: after a super (not a hyper) the dash is a few frames old
        /// when the press comes, and a wall count the dot carries (a landing from a cling, a quick restart) makes it the superbounce's
        /// SpecialWallJump(0.6, 0.6, up * 410) off no wall: 730 up, 276 away, 410 momentum up, on top of the super's.
        /// </summary>
        [TechCheck("stored-superbounce")]
        static int SuperStoredSuperbounce(TechSteps run, int from, int to) => SuperStoredBoost(run, from, to, false);

        /// <summary>Hyper stored superbounce: the stored superbounce off a hyper (the dash's CheckForSuperJump took canHyper).</summary>
        [TechCheck("hyper-stored-superbounce")]
        static int SuperHyperStoredSuperbounce(TechSteps run, int from, int to) => SuperStoredBoost(run, from, to, true);

        // ---- reverse super pauselift ----

        /// <summary>
        /// Reverse super pauselift: a pauselift (the overlay's Pauselift: an inwall climb with the game paused) in the same flight as
        /// a reverse super that was the last super before its dash, with the reverse super's momentum going the way the pauselift
        /// dash goes (toward the wall, which the momentum pinned the dot's back against) and no ground touched between them.
        /// </summary>
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

        // ---- coloured blocks ----

        /// <summary>
        /// Reverse super on switching block: a reverse super (stick against the dash) whose dash swapped the coloured blocks (Swapped
        /// on a step from the dash's first to the super's) and whose floor then went: the dash's first step stood on a ray contact
        /// (coyote full) and the super's own ground check missed (coyote already counting down), so coyote alone held the onGround
        /// the super jumped from. Limit: a platform's edge under the dash's first steps looks the same when the swap is elsewhere.
        /// </summary>
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

        /// <summary>
        /// Super in switching blocks: a super whose dash swapped the coloured blocks (Swapped between the dash's first step and the
        /// super's) onto the dot: the dot was not squished before the swap, is squished after it (framesSquished up: more than
        /// 20 u inside a collider) and lives: no death within 10 steps of the dash's start, the squish count (it kills past 4) ran out.
        /// </summary>
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
