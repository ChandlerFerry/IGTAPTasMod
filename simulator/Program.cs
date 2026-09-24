using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

record InputFrame(float X, bool Press, bool Held, bool Release, bool DashPress = false);
record Surface(float X1, float X2, float Y);
record Ceiling(float X1, float X2, float Y);
record Wall(float X, float Y1, float Y2, int ExposedSide, bool RefillsJumps);
record Gate(float MinX, float MaxX, float MinY, float MaxY);
record SimulationEnvironment(Surface[] surfaces, Ceiling[] ceilings, Wall[] walls, Gate start, Gate finish,
    PhysicsConfig physics, float initialX, float initialY, Gate viewport, string course);
record PhysicsConfig(float MaxSpeed, float Acceleration, float JumpForce, float WallJumpForce,
    float Gravity, float FastFallGravity, float ApexGravity, float ApexSpeedBonus, float TerminalVelocity, float DownwardDrag,
    float WallSlideFriction, float MaxWallSlideSpeed,
    int CoyoteFrames, int WallCoyoteFrames, int WallLockFrames, int MaxWallJumps,
    bool DashUnlocked, int MaxAirDashes, int InitialAirDashes, float InitialDashCooldown,
    float DashSpeed, int DashFrames, int DashCooldownFrames)
{
    public static PhysicsConfig DemoDefaults => new(450f, 100f, 780f, 715f, 25f, 33.75f, 20f, 0f,
        1000f, .2f, 10f, 150f, 5, 6, 5, 4, false, 0, 0, -1f, 1670f, 10, 9);
}
record SimResult(bool Finished, int StartTick, int FinishTick, float X, float Y, float PeakY, int LandingMask, int WallJumpCount)
{
    public int CourseTicks => Finished ? FinishTick - StartTick : int.MaxValue;
}

sealed class SimState
{
    public float X, Y, VX, VY;
    public bool Grounded = true;
    public bool OnWall;
    public bool FacingRight = true;
    public int CoyoteFrames = 6;
    public int WallCoyoteFrames;
    public int WallJumpsLeft = 4;
    public int WallLock = -1;
    public float WallJumpDirection;
    public float WallCoyoteDirection;
    public bool StepUpPending;
    public int JumpBuffer, JumpCut, RecentlyJumped;
    public bool JumpHeld;
    public bool CeilingTouch;
    public int AirDashesLeft, DashBuffer, DashFramesRemaining;
    public float DashCooldown;
    public bool DashActive;
    public float DashDirection;
    public float PeakY;
    public Wall? LedgeCornerLanding;
    public int LandingMask;
    public int WallJumpCount;

}

sealed class FastSimulator
{
    const float Dt = 0.02f;
    // Full-game normal collider: 41x72 with a +2 local-Y offset.
    const float NormalOffsetY = 2f, NormalHalfHeight = 36f;
    // The movement script's falling pose is five pixels below its body origin;
    // upward ceiling resolution uses the centered 41x54 collider pose.
    const float AirOffsetY = -5f, AirHalfHeight = 27f, CeilingOffsetY = 0f;
    const float HalfWidth = 20.5f;
    const float GroundProbeHalfWidth = 15f;
    const float WallProbeOriginX = 15f, WallProbeDistance = 12f;
    readonly Surface[] surfaces;
    readonly Ceiling[] ceilings;
    readonly Wall[] walls;
    readonly Gate start;
    readonly Gate finish;
    readonly PhysicsConfig physics;
    readonly float initialX;
    readonly float initialY;
    readonly RayWorld? rays = RayWorld.Current;

    public FastSimulator(Surface[] surfaces, Ceiling[] ceilings, Wall[] walls, Gate start, Gate finish,
        PhysicsConfig? physics = null, float initialX = 2241f, float initialY = -304f)
    {
        this.surfaces = surfaces; this.ceilings = ceilings; this.walls = walls;
        this.start = start; this.finish = finish; this.physics = physics ?? PhysicsConfig.DemoDefaults;
        this.initialX = initialX; this.initialY = initialY;
    }

    public SimResult Run(IReadOnlyList<InputFrame> inputs, List<(float x, float y, float vx, float vy, bool ground)>? trace = null,
        bool continueAfterFinish = false)
    {
        var s = CreateState();
        int startTick = -1, finishTick = -1;
        bool finishOverlap = false;
        for (int tick = 0; tick < inputs.Count + 80; tick++)
        {
            InputFrame input = tick < inputs.Count ? inputs[tick] : new InputFrame(0, false, false, false);
            trace?.Add((s.X, s.Y, s.VX, s.VY, s.Grounded));
            Step(s, input);
            bool nowStart = Overlaps(s, start);
            bool nowFinish = Overlaps(s, finish);
            // Trigger callbacks are observed on the fixed-step boundary after the
            // overlapping sample; both course start and completion count from there.
            if (startTick < 0 && nowStart) startTick = tick + 1;
            if (startTick >= 0 && finishTick < 0 && nowFinish && !finishOverlap)
            {
                finishTick = tick + 2;
                if (!continueAfterFinish) break;
            }
            finishOverlap = nowFinish;
            if (s.Y < -1000f) break;
        }
        return new SimResult(finishTick >= 0, startTick, finishTick, s.X, s.Y, s.PeakY, s.LandingMask, s.WallJumpCount);
    }

    public SimState CreateState() => new() { X = initialX, Y = initialY, PeakY = initialY,
        AirDashesLeft = physics.InitialAirDashes, DashCooldown = physics.InitialDashCooldown };
    public void Advance(SimState state, InputFrame input) => Step(state, input);

    void Step(SimState s, InputFrame input)
    {
        float velocityBeforeGravity = s.VY;
        Wall? ledgeCornerLanding = s.LedgeCornerLanding;
        s.LedgeCornerLanding = null;
        // Movement.OnCollisionStay2D zeroes Velocity.y after each physics step
        // that is still touching a ceiling; OnCollisionEnter2D does not, so the
        // impact step itself keeps its speed (#1 ticks 531-534: 373, -21, -21, -42).
        if (s.CeilingTouch) s.VY = 0f;
        s.CeilingTouch = false;
        bool launchedThisStep = false;
        bool cutThisStep = false;
        if (s.JumpBuffer > 0) s.JumpBuffer--;
        if (s.JumpCut > 0) s.JumpCut--;
        if (s.RecentlyJumped > 0) s.RecentlyJumped--;
        if (s.DashBuffer > 0) s.DashBuffer--;
        if (s.DashCooldown > -1f) s.DashCooldown -= Dt;
        if (s.DashActive && s.DashFramesRemaining <= 0)
        {
            s.DashActive = false;
            s.DashCooldown = physics.DashCooldownFrames * Dt;
            if (s.VY < 10f) s.VY = 10f;
        }
        if (s.DashFramesRemaining > -1) s.DashFramesRemaining--;
        // Route playback (MacroEngine.RunRawInput) writes Movement's fields
        // directly: a press sets jumpBuffer without the game's 0.12 s cancel
        // Invoke, so it stays buffered until a jump consumes it (#1 presses at
        // 460 and wall-jumps at 469). A release, or a held step followed by a
        // non-held one, sets jumpCutBuffer for this tick and the next.
        if (input.Press)
        {
            s.JumpBuffer = int.MaxValue;
            s.JumpCut = 0;
        }
        if (input.Release || s.JumpHeld && !input.Held) s.JumpCut = 2;
        s.JumpHeld = input.Held;
        if (input.DashPress) s.DashBuffer = 3;
        bool applyStepUp = s.StepUpPending;
        s.StepUpPending = false;

        float halfHeight = s.Grounded ? NormalHalfHeight : AirHalfHeight;
        float offsetY = s.Grounded ? NormalOffsetY : AirOffsetY;
        bool wasGroundedBeforeSupport = s.Grounded;
        Surface? support = FindSupport(s.X, s.Y + offsetY - halfHeight, s.VY);
        // Staying grounded is decided by Movement's real foot rays (r1/r3/r5 minus
        // spike blockers r2/r4), cast from the post-integration pose and facing.
        // They are asymmetric (-15/+6/+25 facing right), unlike FindSupport's +/-15.
        bool raysLeftGround = false;
        if (support != null && s.Grounded && rays != null)
        {
            var (px, py, facing) = ProbePose(s, input, velocityBeforeGravity);
            if (rays.Ground(px, py, facing, velocityBeforeGravity) == null) { support = null; raysLeftGround = true; }
        }
        // Not on a step that launches a buffered wall jump: the rays then see the launched
        // pose (vy 703 disables the forward ray; #1 A3 candidate tick 182).
        bool wallJumpPending = s.JumpBuffer > 0 && (s.OnWall || s.WallCoyoteFrames > 0) && s.WallJumpsLeft > 0;
        bool forwardRaySupport = false;
        if (support == null && !s.Grounded && s.RecentlyJumped == 0 && s.VY < 200f && !wallJumpPending)
        {
            support = FindForwardRaySupport(s, input, velocityBeforeGravity);
            forwardRaySupport = support != null;
        }
        // Airborne landings are also acquired by the real foot rays at the post-integration
        // pose, while the body is still above the surface (#3 candidate tick 309: grounded
        // 6 px up, jumps at 310). Like a forward-ray landing: no snap, jump next step.
        if (support == null && !s.Grounded && s.RecentlyJumped == 0 && velocityBeforeGravity <= 0f && !wallJumpPending && rays != null)
        {
            var (px, py, facing) = ProbePose(s, input, velocityBeforeGravity);
            float? hit = rays.Ground(px, py, facing, velocityBeforeGravity);
            if (hit != null)
            {
                support = new Surface(px - 1f, px + 1f, py - 18f - hit.Value);
                forwardRaySupport = true;
            }
        }
        if (support != null && s.RecentlyJumped == 0 && s.VY <= 200f)
        {
            s.LandingMask |= LandingBit(support.Y);
            s.Grounded = true;
            s.CoyoteFrames = physics.CoyoteFrames;
            s.OnWall = false;
            s.WallJumpsLeft = physics.MaxWallJumps;
            s.AirDashesLeft = physics.MaxAirDashes;
            float targetY = support.Y - NormalOffsetY + NormalHalfHeight;
            // The forward ray can establish ground while the airborne body is
            // still below its eventual resting pose. Native Course 2 tick 455
            // keeps integrating at the entering VY instead of snapping up.
            if (!forwardRaySupport && s.Y < targetY) s.Y = targetY;
            s.VY = 0f;
        }
        else if (support == null && s.Grounded)
        {
            // Movement.FixedUpdate keeps its private onGround flag (and normal
            // collider/gravity) until maxCoyoteFrames has counted below zero.
            // This matters when the route walks off the final platform.
            s.CoyoteFrames--;
            if (s.CoyoteFrames < 0) s.Grounded = false;
        }
        bool detectedStepUp;
        if (rays != null)
        {
            var (px, py, facing) = ProbePose(s, input, velocityBeforeGravity);
            detectedStepUp = support == null && !s.Grounded && !applyStepUp && !wallJumpPending && rays.StepUp(px, py, facing);
        }
        else detectedStepUp = support == null && !s.Grounded && !applyStepUp && FindStepUpSurface(s, input.X) != null;
        if (detectedStepUp)
            s.StepUpPending = true;

        Wall? wallContact = !detectedStepUp && !applyStepUp && !s.Grounded && (s.VY <= 200f || s.DashActive) && s.WallJumpDirection == 0f
            ? FindWallContact(s, input, velocityBeforeGravity) : null;
        bool wallEnteredThisStep = wallContact != null && !s.OnWall;
        if (wallContact != null)
        {
            s.VY += physics.Gravity - physics.WallSlideFriction;
            if (s.VY < -physics.MaxWallSlideSpeed * .8f)
                s.VY += (-physics.MaxWallSlideSpeed - s.VY) * physics.DownwardDrag;
            if (wallEnteredThisStep) s.VY = Math.Clamp(s.VY, -150f, 0f);
            if (wallContact.RefillsJumps)
            {
                s.WallJumpsLeft = physics.MaxWallJumps;
                s.AirDashesLeft = physics.MaxAirDashes;
            }
            if (s.DashActive)
            {
                s.DashActive = false;
                s.DashFramesRemaining = -1;
                s.DashCooldown = physics.DashCooldownFrames * Dt;
            }
            s.Grounded = false;
            s.OnWall = true;
            s.WallCoyoteFrames = physics.WallCoyoteFrames;
            s.WallCoyoteDirection = wallContact.X > s.X ? -1f : 1f;
        }
        else if (!s.Grounded)
        {
            if (s.WallCoyoteFrames > 0) s.WallCoyoteFrames--;
            if (s.WallCoyoteFrames <= 0) s.OnWall = false;
        }

        if (s.VY < -75f && !s.Grounded && !s.OnWall) s.VY -= physics.FastFallGravity;
        else s.VY -= physics.Gravity;
        float dragThreshold = physics.TerminalVelocity * .8f;
        if (s.VY < -dragThreshold)
            s.VY += (-dragThreshold - s.VY) * physics.DownwardDrag;
        if (s.VY < 200f && s.VY > -50f && !s.Grounded && !s.OnWall)
            s.VY += physics.Gravity - physics.ApexGravity;

        bool dashStarted = s.DashBuffer > 0 && physics.DashUnlocked && s.AirDashesLeft > 0
            && s.DashCooldown < 0f && s.WallLock < 0 && !s.DashActive;
        if (dashStarted)
        {
            s.DashBuffer = 0;
            s.DashActive = true;
            s.DashFramesRemaining = physics.DashFrames;
            s.DashDirection = input.X != 0f ? MathF.Sign(input.X) : s.FacingRight ? 1f : -1f;
            if (s.OnWall && s.WallCoyoteFrames == physics.WallCoyoteFrames)
                s.DashDirection = -s.DashDirection;
            s.X -= s.DashDirection * 20f;
            s.AirDashesLeft--;
            s.DashCooldown = 100f;
            s.WallLock = -1;
            s.WallJumpDirection = 0f;
        }
        if (s.DashActive)
        {
            s.VX = s.DashDirection * physics.DashSpeed;
            s.VY = 0f;
        }
        else if (s.WallLock >= 0)
        {
            s.VX = s.WallJumpDirection * physics.MaxSpeed;
            s.WallLock--;
            if (s.WallLock < 0) s.WallJumpDirection = 0f;
        }
        else if (input.X != 0f)
        {
            // Movement.FixedUpdate scales acceleration by the actual MoveAxis
            // value.  Keeping the magnitude here is important for one-frame
            // analog trims; treating every non-zero value as +/-1 invents
            // velocity that the game never applies.
            float acceleration = physics.Acceleration;
            s.VX += input.X * acceleration;
            // Reversal gets a second acceleration impulse only while velocity
            // is still travelling against the requested direction. Do not
            // apply it again on the frame that crosses through zero.
            if (s.VX != 0f && MathF.Sign(s.VX) != MathF.Sign(input.X))
                s.VX += input.X * acceleration;
            // Movement's apex acceleration reads the velocity entering this
            // FixedUpdate. The gravity pass above has already changed s.VY;
            // testing that value misses the last apex tick (native tick 46).
            if ((!s.Grounded || forwardRaySupport) && !s.OnWall
                && velocityBeforeGravity < 200f && velocityBeforeGravity > -50f)
                s.VX += input.X * physics.ApexSpeedBonus;
        }
        else
        {
            if (s.VX > 0f) s.VX -= physics.Acceleration;
            else if (s.VX < 0f) s.VX += physics.Acceleration;
            // Native float rounding puts two-impulse braking just below the
            // strict threshold at Course 2 ticks 224/229. Avoid preserving a
            // 102.222 px/s residue from a sub-millipixel difference.
            if (MathF.Abs(s.VX) < physics.Acceleration + .001f) s.VX = 0f;
        }
        if (!s.DashActive) s.VX = Math.Clamp(s.VX, -physics.MaxSpeed, physics.MaxSpeed);

        // Like a wall contact, a forward-ray landing found at the top of this step
        // is only jumped from on the next one (oracle SIM3 tick 164: onGround +
        // jumpBuffer, jump shows at 165). Crossed-surface landings resolve after this check.
        if (!s.DashActive && s.JumpBuffer > 0 && s.Grounded && !(forwardRaySupport && !wasGroundedBeforeSupport))
        {
            s.JumpBuffer = 0;
            s.Grounded = false;
            s.CoyoteFrames = -1;
            s.RecentlyJumped = 2;
            // A jump on the step after a ledge-corner landing, once the body
            // has moved over the ledge top, first depenetrates the normal
            // collider to its resting pose plus 2 px (18.90 #5 tick 431). Still
            // hanging off the corner, it does not (18.64 tick 145).
            if (ledgeCornerLanding != null && (ledgeCornerLanding.ExposedSide < 0
                    ? s.X + HalfWidth > ledgeCornerLanding.X : s.X - HalfWidth < ledgeCornerLanding.X))
                s.Y = Math.Max(s.Y, ledgeCornerLanding.Y2 - NormalOffsetY + NormalHalfHeight + 2f);
            s.Y += 5.54f;
            // Movement applies the launch impulse before gravity in the same
            // fixed step; exported force is therefore one gravity increment
            // above the velocity visible in authoritative traces.
            s.VY = physics.JumpForce - physics.Gravity;
            launchedThisStep = true;
        }
        else if (!s.DashActive && s.JumpBuffer > 0 && !wallEnteredThisStep
            && (s.OnWall || s.WallCoyoteFrames > 0) && s.WallJumpsLeft > 0)
        {
            s.JumpBuffer = 0;
            s.WallJumpsLeft--;
            s.WallJumpCount++;
            s.Grounded = false;
            s.OnWall = false;
            s.WallCoyoteFrames = 0;
            s.WallJumpDirection = s.WallCoyoteDirection != 0f
                ? s.WallCoyoteDirection : s.FacingRight ? -1f : 1f;
            // The Transform nudge is visible vertically in the authoritative
            // route trace. Unity's collision solver resolves the horizontal
            // nudge back to the wall contact before integrating launch speed.
            s.Y += 1.54f;
            s.VX = s.WallJumpDirection * physics.MaxSpeed;
            s.VY = physics.WallJumpForce - physics.Gravity;
            s.WallLock = physics.WallLockFrames;
            launchedThisStep = true;
        }
        // The replay injects release before Movement.FixedUpdate. The cut
        // predicate therefore observes the velocity entering the step, while
        // the exported velocity already includes that step's gravity/apex pass.
        if (!s.DashActive && s.JumpCut > 0 && velocityBeforeGravity < 580f && velocityBeforeGravity > 150f)
        {
            s.VY = 50f;
            cutThisStep = true;
        }

        if (s.VX > 0f) s.FacingRight = true;
        else if (s.VX < 0f) s.FacingRight = false;

        // Step-up writes Transform.position during FixedUpdate. The Rigidbody
        // trace retains its old pose for that sample, then synchronizes to the
        // top of the ledge on the following integration. Keep the surface
        // pending for one step so jump buffering observes the same ordering.
        if (applyStepUp)
        {
            // The script adds 10 pixels to Transform.position; the 2D solver's
            // contact skin contributes roughly one more pixel when the body is
            // synchronized on the next fixed step.
            s.Y += 11f;
            s.Grounded = true;
            s.CoyoteFrames = physics.CoyoteFrames;
            s.OnWall = false;
            s.WallJumpsLeft = physics.MaxWallJumps;
            s.AirDashesLeft = physics.MaxAirDashes;
            s.VY = 0f;
        }
        float oldBottom = s.Y + (s.Grounded ? NormalOffsetY - NormalHalfHeight : AirOffsetY - AirHalfHeight);
        float nextX = s.X + s.VX * Dt;
        // Rigidbody integration uses the velocity entering FixedUpdate;
        // launch impulses are applied immediately by the movement controller.
        float integrationVY = applyStepUp ? 0f : s.DashActive || launchedThisStep || cutThisStep ? s.VY : velocityBeforeGravity;
        if (cutThisStep)
            s.VY = FindWallContact(s, input, velocityBeforeGravity) != null ? -physics.Gravity : 50f - physics.ApexGravity;
        float nextY = s.Y + integrationVY * Dt;
        if (integrationVY > 0f)
        {
            // Same head height as the resting pose below (18.90 tick 446 stops at 2025 - 27).
            float oldTop = s.Y + CeilingOffsetY + AirHalfHeight;
            float nextTop = nextY + CeilingOffsetY + AirHalfHeight;
            // The air collider's wider edge follows facing. Its trailing edge
            // is only 20.5 px from Transform X; using 25.515 px on both sides
            // invents a horizontal push at Course 2 tick 445.
            float leftReach = s.FacingRight ? HalfWidth : 25.515f;
            float rightReach = s.FacingRight ? 25.515f : HalfWidth;
            foreach (Ceiling ceiling in ceilings)
                if (nextX + rightReach >= ceiling.X1 && nextX - leftReach <= ceiling.X2
                    && oldTop <= ceiling.Y + .1f && nextTop >= ceiling.Y)
                {
                    float leftPenetration = nextX + rightReach - ceiling.X1;
                    float rightPenetration = ceiling.X2 - (nextX - leftReach);
                    float oldLeftPenetration = s.X + rightReach - ceiling.X1;
                    float oldRightPenetration = ceiling.X2 - (s.X - leftReach);
                    float oldHorizontalPenetration = Math.Min(oldLeftPenetration, oldRightPenetration);
                    float verticalPenetration = nextTop - ceiling.Y;
                    // At the concave underside corner Unity keeps the ceiling
                    // manifold when the competing side penetration is only
                    // fractionally smaller.  Requiring a meaningful horizontal
                    // advantage prevents a false wall-wrap/wall-jump while still
                    // allowing the known .55 route to clear around the corner.
                    if (oldHorizontalPenetration < 7f
                        && Math.Min(leftPenetration, rightPenetration) + 2f < verticalPenetration)
                    {
                        nextX = leftPenetration < rightPenetration
                            ? ceiling.X1 - rightReach : ceiling.X2 + leftReach;
                        break;
                    }
                    nextY = ceiling.Y - CeilingOffsetY - AirHalfHeight;
                    s.CeilingTouch = true;
                    break;
                }
        }
        // A descending body that first moves into a ledge corner can receive
        // a one-step ground contact without a downward ray over the top. In
        // the native Course 2 trace this occurs at tick 142, after four ticks
        // at the same X where the +25 ground ray did not report support.
        Wall? ledgeCorner = !applyStepUp && (!s.Grounded || forwardRaySupport && !wasGroundedBeforeSupport) && !s.OnWall && !launchedThisStep && !cutThisStep
            ? FindLedgeCorner(s, nextX) : null;
        if (ledgeCorner != null)
        {
            nextY = Math.Max(nextY, ledgeCorner.Y2 + AirHalfHeight);
            s.LedgeCornerLanding = ledgeCorner;
            s.VY = -physics.Gravity;
            s.Grounded = true;
            s.CoyoteFrames = physics.CoyoteFrames;
            s.WallJumpsLeft = physics.MaxWallJumps;
            s.AirDashesLeft = physics.MaxAirDashes;
            s.LandingMask |= LandingBit(ledgeCorner.Y2);
        }
        // Unity keeps the existing wall manifold for this integration step;
        // use the pre-step vertical pose so walking past a wall top occurs on
        // the same frame as the authoritative solver.
        if (!applyStepUp && ledgeCorner == null)
        {
            nextX = ResolveWallCollision(s.X, nextX, s.Y, nextY, out Wall? resolvedWall);
            // The native far-wall trace changes from VY=-820.08 to -177 on
            // the same tick that horizontal collision moves X onto the wall.
            // The script's facing-dependent ray sees it on the next tick; the
            // Rigidbody corner contact establishes the wall manifold now.
            if (resolvedWall != null && velocityBeforeGravity < -150f && !s.Grounded
                && !s.OnWall && s.WallJumpDirection == 0f)
            {
                s.VY = -150f - physics.Gravity;
                s.OnWall = true;
                s.WallCoyoteFrames = physics.WallCoyoteFrames;
                s.WallCoyoteDirection = resolvedWall.X > s.X ? -1f : 1f;
                if (resolvedWall.RefillsJumps)
                {
                    s.WallJumpsLeft = physics.MaxWallJumps;
                    s.AirDashesLeft = physics.MaxAirDashes;
                }
            }
        }
        // Unity's ground ray changes to the tall collider before the body has
        // fully settled. Let it descend at normal gravity into contact instead
        // of teleporting to the surface. On the exact edge-crossing step the
        // existing contact still prevents vertical motion.
        if (support != null && s.Grounded && MathF.Abs(oldBottom - support.Y) < .1f)
            nextY = s.Y;
        float nextBottom = nextY + (s.Grounded ? NormalOffsetY - NormalHalfHeight : AirOffsetY - AirHalfHeight);
        if (s.VY <= 0f && s.RecentlyJumped == 0)
        {
            // Contact callbacks come from the pose the physics step starts at.
            Surface? landing = FindCrossedSurface(raysLeftGround ? s.X : nextX, oldBottom, nextBottom);
            // A body still resting on the edge it is walking off keeps its height and
            // OnCollisionStay2D's zeroed speed, but onGround and coyote come from the
            // rays (oracle walk-off tick 37: cy 5 with vy -27).
            if (landing != null && raysLeftGround)
            {
                nextY = landing.Y - NormalOffsetY + NormalHalfHeight;
                s.VY = -physics.Gravity;
            }
            else if (landing != null)
            {
                s.LandingMask |= LandingBit(landing.Y);
                s.Grounded = true;
                s.CoyoteFrames = physics.CoyoteFrames;
                s.AirDashesLeft = physics.MaxAirDashes;
                nextY = landing.Y - NormalOffsetY + NormalHalfHeight;
                // Native Rigidbody traces retain one gravity increment while
                // resting on a platform. Keeping -gravity here makes the
                // first walk-off frame integrate that entering velocity.
                s.VY = -physics.Gravity;
            }
        }
        s.X = nextX;
        s.Y = nextY;
        s.PeakY = Math.Max(s.PeakY, s.Y);
    }

    Wall? FindWallContact(SimState s, InputFrame input, float velocityBeforeGravity)
    {
        float direction = s.FacingRight ? 1f : -1f;
        // The exported trace is sampled at a fixed-step boundary. Predict the
        // horizontal velocity that this step's input will apply before testing
        // the next ray origin. At the far wall, a left-held 970 route reaches
        // the ray at tick 182; a right-braking 932 route is still outside it at
        // tick 242. Projecting the entering velocity makes those two contacts
        // indistinguishable.
        //
        // Oracle 905 run 5ffd4f72 (pre-jump prefix on getPlayerControlledInput)
        // proves this projection ALSO pulls contact one tick early on approach:
        // tick 133 has u3=13.8 px (Update probe, 14 reach) with r10/r11 MISS
        // (FixedUpdate probe, 12 reach), so OnWall is false; tick 134 has
        // r10=r11=10.5 px and only then sets OnWall with wallCoyoteFrames=7;
        // tick 135 consumes the wall jump. Native contact and jump are one tick
        // apart. Removing the projection fixes that but breaks the 970 far-wall
        // discrimination (audit-reference drops to simulated=-1, peakError 718px),
        // so it stays. The one-tick early contact needs a fix that keeps both
        // behaviours -- e.g. require the UNPROJECTED native r10/r11 distance to
        // also be within reach -- and must clear the 970 non-regression gate.
        float probeVX = PredictHorizontalVelocity(s, input, velocityBeforeGravity);
        float probeX = s.X + probeVX * Dt + direction * WallProbeOriginX;
        foreach (Wall wall in walls)
        {
            if ((s.FacingRight ? -1 : 1) != wall.ExposedSide) continue;
            float distance = (wall.X - probeX) * direction;
            if (distance < -.1f || distance > WallProbeDistance + .1f) continue;
            // Like X, the ray origin is the pose after this step's integration
            // (18.10 tick 357: only the projected -18 ray is below the wall top).
            float rayY = s.Y + velocityBeforeGravity * Dt;
            if ((rayY - 18f >= wall.Y1 && rayY - 18f <= wall.Y2)
                || (rayY + 19f >= wall.Y1 && rayY + 19f <= wall.Y2)) return wall;
        }
        return null;
    }

    float PredictHorizontalVelocity(SimState s, InputFrame input, float velocityBeforeGravity)
    {
        if (s.DashActive) return s.DashDirection * physics.DashSpeed;
        if (s.WallLock >= 0) return s.WallJumpDirection * physics.MaxSpeed;
        float vx = s.VX;
        if (input.X != 0f)
        {
            vx += input.X * physics.Acceleration;
            if (vx != 0f && MathF.Sign(vx) != MathF.Sign(input.X))
                vx += input.X * physics.Acceleration;
            if (!s.Grounded && !s.OnWall && velocityBeforeGravity < 200f && velocityBeforeGravity > -50f)
                vx += input.X * physics.ApexSpeedBonus;
        }
        else
        {
            if (vx > 0f) vx -= physics.Acceleration;
            else if (vx < 0f) vx += physics.Acceleration;
            if (MathF.Abs(vx) < physics.Acceleration + .001f) vx = 0f;
        }
        return Math.Clamp(vx, -physics.MaxSpeed, physics.MaxSpeed);
    }

    float ResolveWallCollision(float oldX, float nextX, float oldY, float nextY, out Wall? contact)
    {
        const float collisionHalfWidth = 25.515f;
        contact = null;
        foreach (Wall wall in walls)
        {
            bool existingContact = MathF.Abs(MathF.Abs(oldX - wall.X) - collisionHalfWidth) < .6f;
            float y = existingContact ? oldY : nextY;
            if (y + 22f < wall.Y1 || y - 32f > wall.Y2) continue;
            // A body that was already over the column and above its top meets the
            // top, not the face (#1 candidate tick 307 lands on the 6592 block top);
            // one already sunk into the corner is still pushed out sideways (18.64 tick 143).
            bool overTop = oldY - 32f >= wall.Y2 - .5f;
            if (wall.ExposedSide > 0 && nextX < oldX && oldX > wall.X && !(overTop && oldX - collisionHalfWidth <= wall.X - .6f)
                && nextX - collisionHalfWidth < wall.X)
            {
                nextX = wall.X + collisionHalfWidth;
                contact = wall;
            }
            else if (wall.ExposedSide < 0 && nextX > oldX && oldX < wall.X && !(overTop && oldX + collisionHalfWidth >= wall.X + .6f)
                && nextX + collisionHalfWidth > wall.X)
            {
                nextX = wall.X - collisionHalfWidth;
                contact = wall;
            }
        }
        return nextX;
    }

    Wall? FindLedgeCorner(SimState s, float nextX)
    {
        if (s.VY >= 0f || s.VX == 0f) return null;
        const float collisionHalfWidth = 25.515f;
        foreach (Wall wall in walls)
        {
            if (!wall.RefillsJumps) continue;
            bool enters = s.VX > 0f
                ? wall.ExposedSide < 0 && s.X + collisionHalfWidth < wall.X
                    && nextX + collisionHalfWidth >= wall.X
                : wall.ExposedSide > 0 && s.X - collisionHalfWidth > wall.X
                    && nextX - collisionHalfWidth <= wall.X;
            if (!enters) continue;
            float aboveTop = s.Y - wall.Y2;
            if (aboveTop >= AirHalfHeight - 1f && aboveTop <= AirHalfHeight + 5f)
                return wall;
        }
        return null;
    }

    static int LandingBit(float y)
    {
        float[] coursePlatforms = [-343f, -279f, -55f, 169f, 297f];
        for (int i = 0; i < coursePlatforms.Length; i++)
            if (MathF.Abs(y - coursePlatforms[i]) < 2f) return 1 << i;
        return 0;
    }

    Surface? FindSupport(float x, float bottom, float verticalVelocity)
    {
        Surface? best = null;
        foreach (Surface surface in surfaces)
        {
            // Movement checks the two downward rays at local X +/-15, not the
            // full 20.5-pixel collider extents. This releases platform edges
            // one fixed step earlier at full running speed.
            // On narrow ledges the paired blocking rays suppress the trailing
            // foot ray at the edge, leaving the central/forward probes as the
            // effective support span.
            float probeHalfWidth = surface.X2 - surface.X1 <= 100.01f ? 8f : GroundProbeHalfWidth;
            if (x + probeHalfWidth < surface.X1 || x - probeHalfWidth > surface.X2) continue;
            float gap = bottom - surface.Y;
            // The exported origin/pose puts the effective 22-pixel Unity ray
            // about eleven pixels below the compact airborne body position.
            // A wider 23-pixel body-bottom reach acquires support early.
            float maximumGap = verticalVelocity <= 0f ? 11f : 6f;
            if (gap >= -2f && gap <= maximumGap && (best == null || surface.Y > best.Y)) best = surface;
        }
        return best;
    }

    // Transform pose Movement's probes see this step: after this step's input and
    // integration, with the body stopped by any wall face it runs into.
    (float x, float y, bool facingRight) ProbePose(SimState s, InputFrame input, float velocityBeforeGravity)
    {
        float nextVX = PredictHorizontalVelocity(s, input, velocityBeforeGravity);
        float y = s.Y + velocityBeforeGravity * Dt;
        float x = ResolveWallCollision(s.X, s.X + nextVX * Dt, s.Y, y, out _);
        return (x, y, nextVX > 0f || nextVX == 0f && s.FacingRight);
    }

    Surface? FindForwardRaySupport(SimState s, InputFrame input, float velocityBeforeGravity)
    {
        float nextVX = PredictHorizontalVelocity(s, input, velocityBeforeGravity);
        bool facingRight = nextVX > 0f || nextVX == 0f && s.FacingRight;
        float nextX = s.X + nextVX * Dt;
        // The native forward ray samples after the Rigidbody has resolved its
        // horizontal wall contact. Its contact skin sits about 0.64 px inside
        // the nominal 25.515 px wall stand-off at Course 2 tick 455.
        nextX = ResolveWallCollision(s.X, nextX, s.Y, s.Y, out Wall? probeWall);
        float skinX = probeWall != null ? MathF.Sign(probeWall.X - nextX) * .64f : 0f;
        float direction = facingRight ? 1f : -1f;
        // Movement's forward ground ray starts 25 px from Transform X and 18
        // px below Transform Y, extends down 22 px, and requires the paired
        // blocker ray at 21 px to miss. Predict input first so a newly flipped
        // facing direction can find a ledge on this fixed step.
        // The contact skin only reaches a ray that starts inside the solid (the
        // underside hit at tick 455). A ray dropping onto a top from above uses
        // the resolved stand-off: at 18.42 tick 424 it is 0.5 px short of the
        // wall top and native wall-slides instead of landing.
        float forwardX = nextX + direction * 25f;
        float skinnedForwardX = forwardX + skinX;
        float blockerX = nextX + skinX + direction * 21f;
        // Post-integration pose, like the wall rays (SIM3 tick 164 lands on the 5952 ledge only from there).
        float rayTop = s.Y + velocityBeforeGravity * Dt - 18f;
        float rayBottom = rayTop - 22f;
        bool RayHits(float x1, float x2, float y, float x) => x >= x1 && x <= x2
            && y >= rayBottom && y <= rayTop;
        // A ray starting inside the solid can register its underside. The
        // snapshot records that horizontal edge as a ceiling, although the
        // native downward ray reports ground at Course 2 tick 455.
        // Native tick 455 (oracle diagnostics, run 10f8b042): the blocking-mask
        // blocker ray at 21 px MISSES (r4 = null) while the ground-mask forward
        // ray at 25 px hits at distance 0 (r3). The "ceiling" is the same ledge's
        // underside on the ground layer, which the native blocker ray cannot see.
        // Only a surface at the blocker X may reject forward support.
        if (surfaces.Any(surface => RayHits(surface.X1, surface.X2, surface.Y, blockerX))) return null;
        Surface? best = null;
        foreach (Surface surface in surfaces)
            if (RayHits(surface.X1, surface.X2, surface.Y, forwardX)
                && (best == null || surface.Y > best.Y)) best = surface;
        foreach (Ceiling ceiling in ceilings)
            if (RayHits(ceiling.X1, ceiling.X2, ceiling.Y, skinnedForwardX)
                && (best == null || ceiling.Y > best.Y)) best = new Surface(ceiling.X1, ceiling.X2, ceiling.Y);
        return best;
    }

    Surface? FindStepUpSurface(SimState s, float moveX)
    {
        // A wall slide keeps its contact manifold until the body clears the
        // ledge. The loose forward probe otherwise invents an 11 px step up
        // through Course 2's first 320 px block (native ticks 154 and 163).
        // A held facing direction alone does not make the controller step up
        // while the route has released horizontal movement beside the wall.
        if (s.OnWall || moveX == 0f || MathF.Sign(moveX) != (s.FacingRight ? 1f : -1f)) return null;
        float probeX = s.X + s.VX * Dt + (s.FacingRight ? 25f : -25f);
        float probeY = s.Y + s.VY * Dt - 18f;
        float footY = s.Y + AirOffsetY - AirHalfHeight;
        foreach (Surface surface in surfaces)
        {
            if (probeX < surface.X1 || probeX > surface.X2) continue;
            // The game's step nudge is roughly 10 px. A surface more than that
            // above the airborne foot is a wall, even if the high forward ray
            // happens to cross its top while the player is rising beside it.
            if (surface.Y - footY > 10f) continue;
            float gap = probeY - surface.Y;
            if (gap >= 0f && gap <= 5f) return surface;
        }
        return null;
    }

    Surface? FindCrossedSurface(float x, float oldBottom, float newBottom)
    {
        Surface? best = null;
        foreach (Surface surface in surfaces)
        {
            if (x + HalfWidth < surface.X1 || x - HalfWidth > surface.X2) continue;
            if (oldBottom >= surface.Y - 1f && newBottom <= surface.Y && (best == null || surface.Y > best.Y)) best = surface;
        }
        return best;
    }

    static bool Overlaps(SimState s, Gate gate, float padding = 0f)
    {
        float offsetY = s.Grounded ? NormalOffsetY : AirOffsetY;
        float halfHeight = s.Grounded ? NormalHalfHeight : AirHalfHeight;
        return s.X + HalfWidth >= gate.MinX - padding && s.X - HalfWidth <= gate.MaxX + padding
            && s.Y + offsetY + halfHeight >= gate.MinY - padding && s.Y + offsetY - halfHeight <= gate.MaxY + padding;
    }
}

static class Loader
{
    public sealed record RouteStep(string Label, int Duration, float X, bool Press, bool Held, bool Release,
        bool DashPress = false);

    public static int SelfTest()
    {
        // Exercise the course/hash contract with disjoint courses and a
        // course-independent Grid solid, independent of the live game export.
        static object Collider(string name, string tag, float x, float y, float width, float height,
            bool trigger) => new
            {
                name, tag, centerX = x, centerY = y, sizeX = width, sizeY = height, trigger,
                points = Array.Empty<float>(), pathStarts = Array.Empty<int>(), colliderType = "BoxCollider2D", rotation = 0f
            };
        var model = new
        {
            playerX = 0f, playerY = 0f, movement = Array.Empty<object>(),
            colliders = new[]
            {
                Collider("Courses/course 2/DisableBits/Start", "Untagged", 0, 0, 10, 10, true),
                Collider("Courses/course 2/DisableBits/RealEnd", "Untagged", 30, 0, 10, 10, true),
                Collider("Courses/course 2/Clones/activeCloneArea", "Untagged", 0, 0, 200, 200, true),
                Collider("Courses/course 2/solid", "Ground", 30, 0, 10, 10, false),
                Collider("Courses/Course 3/DisableBits/Start", "Untagged", 1000, 0, 10, 10, true),
                Collider("Courses/Course 3/DisableBits/RealEnd", "Untagged", 1030, 0, 10, 10, true),
                Collider("Courses/Course 3/Clones/activeCloneArea", "Untagged", 1000, 0, 200, 200, true),
                Collider("Courses/Course 3/solid", "Ground", 1030, 0, 10, 10, false),
                Collider("Grid/shared", "Ground", 1010, 0, 10, 10, false)
            }
        };
        string file = Path.GetTempFileName();
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(model);
            File.WriteAllBytes(file, bytes);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            SimulationEnvironment second = Environment(file, 0, 0, false, "course 2", hash);
            SimulationEnvironment third = Environment(file, 1000, 0, false, "3", hash);
            if (second.course != "course 2" || second.surfaces.Length != 1
                || third.course != "course 3" || third.surfaces.Length != 2)
                throw new Exception("Loader course selection or shared Grid solid filtering failed");
            try
            {
                Environment(file, 0, 0, false, "course 3", hash);
                throw new Exception("Loader accepted a restart pose outside the required course");
            }
            catch (InvalidDataException) { }
            try
            {
                Environment(file, 1000, 0, false, "course 3", new string('0', 64));
                throw new Exception("Loader accepted a mismatched model SHA-256");
            }
            catch (InvalidDataException) { }
            Console.WriteLine("LOADER self-test passed: explicit courses, pose, SHA-256, shared Grid");
            return 0;
        }
        finally { File.Delete(file); }
    }

    public static SimulationEnvironment Environment(string modelPath, float? restartX = null,
        float? restartY = null, bool allowDash = true, string? requiredCourse = null,
        string? expectedSha256 = null)
    {
        // Hash and parse the same bytes. A mutable live export must not be able
        // to change between an integrity check and geometry extraction.
        byte[] modelBytes = File.ReadAllBytes(modelPath);
        RayWorld.Use(modelPath);
        if (expectedSha256 != null)
        {
            string expected = expectedSha256.Trim();
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new ArgumentException("Expected model SHA-256 must contain exactly 64 hexadecimal digits", nameof(expectedSha256));
            string actual = Convert.ToHexString(SHA256.HashData(modelBytes));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Model SHA-256 mismatch for '{modelPath}': expected {expected}, actual {actual}");
        }
        using JsonDocument doc = JsonDocument.Parse(modelBytes);
        float initialX = restartX ?? doc.RootElement.GetProperty("playerX").GetSingle();
        float initialY = restartY ?? doc.RootElement.GetProperty("playerY").GetSingle();
        if (!float.IsFinite(initialX) || !float.IsFinite(initialY))
            throw new InvalidDataException("Restart pose must contain finite X and Y coordinates");
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("movement", out JsonElement movement))
            foreach (JsonElement item in movement.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out JsonElement nameElement)
                    || !item.TryGetProperty("value", out JsonElement valueElement)) continue;
                float value;
                if (valueElement.ValueKind == JsonValueKind.Number && valueElement.TryGetSingle(out value))
                    scalars[nameElement.GetString() ?? ""] = value;
                else if (valueElement.ValueKind == JsonValueKind.String
                    && float.TryParse(valueElement.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out value))
                    scalars[nameElement.GetString() ?? ""] = value;
            }
        float Scalar(string name, float fallback) => scalars.TryGetValue(name, out float value) ? value : fallback;
        bool Flag(string name) => scalars.TryGetValue(name, out float value) && value != 0f
            || movement.ValueKind == JsonValueKind.Array && movement.EnumerateArray().Any(item =>
                item.TryGetProperty("name", out JsonElement field) && field.GetString() == name
                && item.TryGetProperty("value", out JsonElement encoded)
                && encoded.ValueKind == JsonValueKind.String
                && bool.TryParse(encoded.GetString(), out bool parsed) && parsed);
        float maxSpeed = Scalar("runSpeed", 45f) * 10f;
        float gravity = Scalar("gravity", 25f);
        var physics = new PhysicsConfig(maxSpeed,
            maxSpeed / Math.Max(1f, Scalar("framesToReachTopSpeed", 4.5f)),
            Scalar("jumpForce", 780f), Scalar("wallJumpForce", 715f), gravity,
            // The game adds its 0.1 base falling multiplier to the exported
            // post-maximum drag field (0.2 in the full game => 1.3x gravity).
            gravity * (1.1f + Scalar("PostMaxDownwardsSpeedDrag", .25f)),
            gravity - Scalar("jumpApexAntiGrav", 5f), Scalar("jumpApexSpeedBonus", 0f) * 10f,
            Scalar("maxDownwardsSpeed", 100f) * 10f, Scalar("PostMaxDownwardsSpeedDrag", .25f),
            Scalar("wallSlideFriction", 10f), Scalar("maxWallSlideSpeed", 150f),
            Math.Max(0, (int)Scalar("maxCoyoteFrames", 6f)),
            Math.Max(0, (int)Scalar("maxWallCoyoteFrames", 7f) - 1),
            Math.Max(0, (int)Scalar("wallJumpMovementLockFrames", 5f)),
            Math.Max(0, (int)Scalar("maxWallJumps", 4f)),
            allowDash && Flag("dashUnlocked"),
            allowDash ? Math.Max(0, (int)Scalar("maxAirDashes", 0f)) : 0,
            allowDash ? Math.Max(0, (int)Scalar("airDashesLeft", 0f)) : 0,
            Scalar("dashCooldown", -1f),
            Scalar("dashSpeed", 167f) * 10f,
            Math.Max(1, (int)Scalar("dashFrames", 10f)),
            Math.Max(0, (int)MathF.Ceiling(Scalar("defaultDashCooldown", .18f) / .02f)));
        var surfaces = new List<Surface>();
        var ceilings = new List<Ceiling>();
        var walls = new List<Wall>();
        var courseStarts = new Dictionary<string, Gate>(StringComparer.OrdinalIgnoreCase);
        var finishes = new Dictionary<string, Gate>(StringComparer.OrdinalIgnoreCase);
        var viewports = new Dictionary<string, Gate>(StringComparer.OrdinalIgnoreCase);
        var coursePattern = new Regex(@"^Courses/(?<course>course \d+)/", RegexOptions.IgnoreCase);
        foreach (JsonElement collider in doc.RootElement.GetProperty("colliders").EnumerateArray())
        {
            string name = collider.GetProperty("name").GetString() ?? "";
            Match match = coursePattern.Match(name);
            if (!match.Success) continue;
            string course = match.Groups["course"].Value.ToLowerInvariant();
            float cx = collider.GetProperty("centerX").GetSingle(), cy = collider.GetProperty("centerY").GetSingle();
            float sx = collider.GetProperty("sizeX").GetSingle(), sy = collider.GetProperty("sizeY").GetSingle();
            var gate = new Gate(cx - sx / 2, cx + sx / 2, cy - sy / 2, cy + sy / 2);
            if (name.EndsWith("/DisableBits/Start", StringComparison.OrdinalIgnoreCase)) courseStarts[course] = gate;
            else if (name.EndsWith("/DisableBits/RealEnd", StringComparison.OrdinalIgnoreCase)) finishes[course] = gate;
            else if (name.EndsWith("/Clones/activeCloneArea", StringComparison.OrdinalIgnoreCase)) viewports[course] = gate;
        }
        string selectedCourse;
        if (requiredCourse != null)
        {
            string requested = requiredCourse.Trim();
            Match courseNumber = Regex.Match(requested, @"^(?:course\s*)?(\d+)$", RegexOptions.IgnoreCase);
            selectedCourse = courseNumber.Success ? "course " + courseNumber.Groups[1].Value : requested.ToLowerInvariant();
            if (!courseStarts.ContainsKey(selectedCourse) || !finishes.ContainsKey(selectedCourse)
                || !viewports.ContainsKey(selectedCourse))
                throw new InvalidDataException($"Course '{selectedCourse}' has no complete start, finish, and viewport in '{modelPath}'");
            Gate requiredViewport = viewports[selectedCourse];
            if (initialX < requiredViewport.MinX || initialX > requiredViewport.MaxX
                || initialY < requiredViewport.MinY || initialY > requiredViewport.MaxY)
                throw new InvalidDataException($"Restart pose ({initialX:0.###}, {initialY:0.###}) is outside '{selectedCourse}' viewport "
                    + $"[{requiredViewport.MinX:0.###}, {requiredViewport.MaxX:0.###}] x "
                    + $"[{requiredViewport.MinY:0.###}, {requiredViewport.MaxY:0.###}]");
        }
        else
        {
            // Keep legacy callers working; automated solves must name a course.
            selectedCourse = courseStarts.Keys.Where(finishes.ContainsKey)
                .OrderBy(course => viewports.TryGetValue(course, out Gate? area)
                    && initialX >= area.MinX && initialX <= area.MaxX && initialY >= area.MinY && initialY <= area.MaxY ? 0 : 1)
                .ThenBy(course =>
                {
                    Gate gate = courseStarts[course];
                    float dx = initialX - (gate.MinX + gate.MaxX) / 2;
                    float dy = initialY - (gate.MinY + gate.MaxY) / 2;
                    return dx * dx + dy * dy;
                }).FirstOrDefault() ?? throw new InvalidDataException("No complete course start/end gate pair found");
        }
        Gate start = courseStarts[selectedCourse], finish = finishes[selectedCourse];
        Gate viewport = viewports.TryGetValue(selectedCourse, out Gate? selectedViewport)
            ? selectedViewport : new Gate(Math.Min(start.MinX, finish.MinX) - 1000, Math.Max(start.MaxX, finish.MaxX) + 1000,
                Math.Min(start.MinY, finish.MinY) - 1000, Math.Max(start.MaxY, finish.MaxY) + 1000);
        foreach (JsonElement collider in doc.RootElement.GetProperty("colliders").EnumerateArray())
        {
            string name = collider.GetProperty("name").GetString() ?? "";
            Match owner = coursePattern.Match(name);
            if (owner.Success && !owner.Groups["course"].Value.Equals(selectedCourse, StringComparison.OrdinalIgnoreCase))
                continue;
            // The tilemap solids under Grid/ and biggerGrid/ are shared by
            // several courses, so retain them and clip their edges below.
            float cx = collider.GetProperty("centerX").GetSingle(), cy = collider.GetProperty("centerY").GetSingle();
            float sx = collider.GetProperty("sizeX").GetSingle(), sy = collider.GetProperty("sizeY").GetSingle();
            var gate = new Gate(cx - sx / 2, cx + sx / 2, cy - sy / 2, cy + sy / 2);
            if (collider.GetProperty("trigger").GetBoolean()) continue;
            string tag = collider.GetProperty("tag").GetString() ?? "";
            if (tag != "Ground" && tag != "Moss") continue;
            JsonElement points = collider.GetProperty("points");
            JsonElement starts = collider.GetProperty("pathStarts");
            if (points.GetArrayLength() >= 4 && starts.GetArrayLength() > 0)
            {
                float[] p = points.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                int[] ps = starts.EnumerateArray().Select(v => v.GetInt32()).Append(p.Length / 2).ToArray();
                for (int path = 0; path < ps.Length - 1; path++)
                    for (int i = ps[path]; i < ps[path + 1]; i++)
                    {
                        int j = i + 1 < ps[path + 1] ? i + 1 : ps[path];
                        float x1 = p[i * 2], y1 = p[i * 2 + 1], x2 = p[j * 2], y2 = p[j * 2 + 1];
                        if (MathF.Abs(y1 - y2) < .01f && MathF.Abs(x1 - x2) > .01f)
                        {
                            if (x2 < x1) surfaces.Add(new Surface(x2, x1, y1));
                            else ceilings.Add(new Ceiling(x1, x2, y1));
                        }
                        else if (MathF.Abs(x1 - x2) < .01f && MathF.Abs(y1 - y2) > .01f)
                            walls.Add(new Wall(x1, Math.Min(y1, y2), Math.Max(y1, y2), y2 > y1 ? 1 : -1,
                                string.Equals(tag, "Ground", StringComparison.OrdinalIgnoreCase)));
                    }
            }
            else if (collider.GetProperty("colliderType").GetString() == "BoxCollider2D" && MathF.Abs(collider.GetProperty("rotation").GetSingle()) < .01f)
            {
                surfaces.Add(new Surface(cx - sx / 2, cx + sx / 2, cy + sy / 2));
                ceilings.Add(new Ceiling(cx - sx / 2, cx + sx / 2, cy - sy / 2));
                bool refillsJumps = string.Equals(tag, "Ground", StringComparison.OrdinalIgnoreCase);
                walls.Add(new Wall(cx - sx / 2, cy - sy / 2, cy + sy / 2, -1, refillsJumps));
                walls.Add(new Wall(cx + sx / 2, cy - sy / 2, cy + sy / 2, 1, refillsJumps));
            }
        }
        return new SimulationEnvironment(
            surfaces.Where(s => s.X2 > viewport.MinX && s.X1 < viewport.MaxX && s.Y > viewport.MinY && s.Y < viewport.MaxY).ToArray(),
            ceilings.Where(c => c.X2 > viewport.MinX && c.X1 < viewport.MaxX && c.Y > viewport.MinY && c.Y < viewport.MaxY).ToArray(),
            walls.Where(w => w.X > viewport.MinX && w.X < viewport.MaxX && w.Y2 > viewport.MinY && w.Y1 < viewport.MaxY).ToArray(),
            start, finish, physics, initialX, initialY, viewport, selectedCourse);
    }

    public static List<InputFrame> Recording(string path, out List<(float x, float y, float vx, float vy, bool ground)> truth)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var inputs = new List<InputFrame>(); truth = new();
        foreach (JsonElement s in doc.RootElement.GetProperty("samples").EnumerateArray())
        {
            inputs.Add(new InputFrame(s.GetProperty("moveX").GetSingle(), s.GetProperty("jumpPressed").GetBoolean(),
                s.GetProperty("jumpHeld").GetBoolean(), s.GetProperty("jumpReleased").GetBoolean(),
                s.TryGetProperty("dashPressed", out JsonElement dashPressed) && dashPressed.GetBoolean()));
            truth.Add((s.GetProperty("positionX").GetSingle(), s.GetProperty("positionY").GetSingle(),
                s.GetProperty("velocityX").GetSingle(), s.GetProperty("velocityY").GetSingle(), s.GetProperty("onGround").GetBoolean()));
        }
        return inputs;
    }

    public static List<RouteStep> Route(string path, string routeName)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (JsonElement macro in doc.RootElement.GetProperty("macros").EnumerateArray())
        {
            if (!string.Equals(macro.GetProperty("name").GetString(), routeName, StringComparison.Ordinal)) continue;
            var result = new List<RouteStep>();
            foreach (JsonElement step in macro.GetProperty("steps").EnumerateArray())
            {
                if (step.TryGetProperty("enabled", out JsonElement enabled) && !enabled.GetBoolean()) continue;
                result.Add(new RouteStep(step.GetProperty("label").GetString() ?? "", step.GetProperty("durationTicks").GetInt32(),
                    step.GetProperty("rawMoveX").GetSingle(), step.GetProperty("rawJumpPressed").GetBoolean(),
                    step.GetProperty("rawJumpHeld").GetBoolean(), step.GetProperty("rawJumpReleased").GetBoolean(),
                    step.TryGetProperty("rawDashPressed", out JsonElement dashPressed) && dashPressed.GetBoolean()));
            }
            return result;
        }
        throw new InvalidDataException("Route not found: " + routeName);
    }
}

class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "model-info")
        {
            if (args.Length is not (6 or 7) || args.Length == 7 && args[6] != "dash")
                throw new ArgumentException("Usage: IGTAP.FastSim model-info <model.json> <course> <restartX> <restartY> <sha256> [dash]");
            float x = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            float y = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
            bool allowDash = args.Length == 7;
            SimulationEnvironment selected = Loader.Environment(args[1], x, y, allowDash,
                requiredCourse: args[2], expectedSha256: args[5]);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                course = selected.course,
                sha256 = args[5].Trim().ToUpperInvariant(),
                restartPose = new { x = selected.initialX, y = selected.initialY },
                dashAllowed = allowDash,
                selected.start,
                selected.finish,
                selected.viewport,
                surfaces = selected.surfaces.Length,
                ceilings = selected.ceilings.Length,
                walls = selected.walls.Length,
                selected.physics
            }));
            return 0;
        }
        if (args.Length > 0 && args[0] == "suite")
            return OfflineSuite.Run(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "self-test")
        {
            int prior = OfflineSuite.SelfTest();
            if (prior != 0) return prior;
            return Loader.SelfTest();
        }
        if (args.Length == 5 && args[0] == "ray-probe")
        {
            float?[] probes = RayWorld.Load(args[1]).Probes(float.Parse(args[2]), float.Parse(args[3]), bool.Parse(args[4]));
            Console.WriteLine(string.Join(' ', probes.Select((d, i) => $"r{i + 1}={d?.ToString("0.###") ?? "-"}")));
            return 0;
        }
        if (args.Length > 1 && args[0] == "ray-validate")
            return RayWorld.Validate(args.Skip(1).ToArray());
        if (args.Length >= 7 && args[0] == "route-trace")
        {
            // route-trace <model> <routes> <route> <native-result> <fromTick> <toTick>: per-tick sim vs native.
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(args[4]));
            var nativeTrace = OfflineSuite.ParseAttemptTrace(document.RootElement.GetProperty("earlyTrace").GetString() ?? "");
            var traceEnvironment = Loader.Environment(args[1], nativeTrace[0].x, nativeTrace[0].y, false, "course 2");
            var traceSimulator = CreateSimulator(traceEnvironment);
            List<Loader.RouteStep> steps = Loader.Route(args[2], args[3]);
            InputFrame[] frames = BuildFrames(steps, steps.Select(step => step.Duration).ToArray(), -1, -1, 0);
            int from = int.Parse(args[5]), to = int.Parse(args[6]);
            SimState state = traceSimulator.CreateState();
            for (int tick = 0; tick < Math.Min(to, frames.Length); tick++)
            {
                traceSimulator.Advance(state, frames[tick]);
                if (tick + 1 < from) continue;
                var n = tick + 1 < nativeTrace.Count ? nativeTrace[tick + 1] : default;
                InputFrame f = frames[tick];
                Console.WriteLine($"{tick + 1} in(x={f.X:0.##} p={(f.Press ? 1 : 0)} h={(f.Held ? 1 : 0)} r={(f.Release ? 1 : 0)}) " +
                    $"sim({state.X:0.###},{state.Y:0.###} v={state.VX:0.#},{state.VY:0.#} g={(state.Grounded ? 1 : 0)} w={(state.OnWall ? 1 : 0)} wc={state.WallCoyoteFrames} wl={state.WallLock} cy={state.CoyoteFrames} jb={state.JumpBuffer}) " +
                    $"nat({n.x:0.###},{n.y:0.###} v={n.vx:0.#},{n.vy:0.#})");
            }
            var fullTrace = new List<(float x, float y, float vx, float vy, bool ground)>();
            SimResult traced = traceSimulator.Run(frames, fullTrace);
            Console.WriteLine($"sim startTick={traced.StartTick} finishTick={traced.FinishTick} courseTicks={traced.CourseTicks} " +
                $"firstSpikeContact={OfflineSuite.FirstHazardTick(args[1], traceEnvironment.viewport, fullTrace, 0)} " +
                $"firstWithin2px={OfflineSuite.FirstHazardTick(args[1], traceEnvironment.viewport, fullTrace, 2)}");
            return 0;
        }
        if (args.Length < 2) { Console.Error.WriteLine("Usage: IGTAP.FastSim <model.json> <recording.json>"); return 2; }
        var environment = Loader.Environment(args[0]);
        List<InputFrame> inputs = Loader.Recording(args[1], out var truth);
        var simulator = truth.Count > 0
            ? new FastSimulator(environment.surfaces, environment.ceilings, environment.walls, environment.start,
                environment.finish, environment.physics, truth[0].x, truth[0].y)
            : CreateSimulator(environment);
        var predicted = new List<(float x, float y, float vx, float vy, bool ground)>();
        SimResult result = simulator.Run(inputs, predicted);
        int count = Math.Min(truth.Count, predicted.Count);
        double squared = 0, max = 0; int groundMatches = 0;
        for (int i = 0; i < count; i++)
        {
            double dx = truth[i].x - predicted[i].x, dy = truth[i].y - predicted[i].y;
            double error = Math.Sqrt(dx * dx + dy * dy); squared += error * error; max = Math.Max(max, error);
            if (truth[i].ground == predicted[i].ground) groundMatches++;
        }
        Console.WriteLine($"surfaces={environment.surfaces.Length} samples={count} rmse={Math.Sqrt(squared / count):0.###} max={max:0.###} ground={(100d * groundMatches / count):0.0}%");
        Console.WriteLine($"finish={result.Finished} startTick={result.StartTick} finishTick={result.FinishTick} courseTicks={result.CourseTicks} final=({result.X:0.##},{result.Y:0.##}) peakY={result.PeakY:0.##} landings=0x{result.LandingMask:X}");
        for (int i = 0; i < count; i += 25)
            Console.WriteLine($"t={i,3} true=({truth[i].x,8:0.##},{truth[i].y,7:0.##}) sim=({predicted[i].x,8:0.##},{predicted[i].y,7:0.##}) g={truth[i].ground}/{predicted[i].ground}");
        return 0;
    }

    static FastSimulator CreateSimulator(SimulationEnvironment environment) => new(environment.surfaces,
        environment.ceilings, environment.walls, environment.start, environment.finish, environment.physics,
        environment.initialX, environment.initialY);




    static InputFrame[] BuildFrames(List<Loader.RouteStep> steps, int[] durations, int hopStep, int hopTick, int hopHold)
    {
        var frames = new List<InputFrame>(durations.Sum() + 80);
        for (int stepIndex = 0; stepIndex < steps.Count; stepIndex++)
        {
            Loader.RouteStep step = steps[stepIndex];
            for (int tick = 0; tick < durations[stepIndex]; tick++)
            {
                bool press = tick == 0 && step.Press;
                bool release = tick == 0 && step.Release;
                if (stepIndex == hopStep && tick == hopTick) press = true;
                if (stepIndex == hopStep && tick == hopTick + hopHold) release = true;
                frames.Add(new InputFrame(step.X, press, step.Held || (stepIndex == hopStep && tick >= hopTick && tick < hopTick + hopHold),
                    release, tick == 0 && step.DashPress));
            }
        }
        return frames.ToArray();
    }
}
