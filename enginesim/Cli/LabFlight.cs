using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Box2D;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// The free-flight bound of <c>lab solve</c>: the highest the player can be on each of the next ticks, for a state that has
    /// spent its dash, off ground and wall. It is an admissible cutoff: it only ever over-estimates the height, so a state it
    /// cuts could not have touched the finish box by the search's last tick (Lab.Solve's <c>prunedFlight</c>).
    /// <para>Why it holds (Movement.cs line numbers). In free flight (cutsceneMode none, not onGround, not OnWall,
    /// wallCoyoteFrames 0 or less, no final cling, no dash left: airDashesLeft 0, and only a landing, a wall cling, a spring or
    /// an orb refills it) only these change Velocity.y: gravity (1109-1116: at least <c>gravity</c> a tick, 1.4x with the stick
    /// down while the body's y speed is under 300, <c>fastFallMod</c> below -75), the apex anti-gravity (1129-1133: +a while
    /// -50 &lt; Velocity.y &lt; 200), endDash (2016-2027: x0.35 at an up dash's end, 10 if under 10; its tick is fixed by
    /// dashFramesRemaining), the jump cut (1668-1675: to 50, after a normal jump) and the double jump (1757-1779: 745 + half of
    /// a Velocity.y under 200 with the boost control, under 700 without). momentum only decays (1263-1295), and Velocity.x only
    /// moves toward the stick, clamped to runSpeed (1788-1841), so the body's velocity (Velocity + momentum + Box2D gravity,
    /// 1303) bounds the step both ways. Every other source of height or of a refill needs a contact, and each is ruled out in
    /// the reach of the remaining ticks: the ground branch (962) and the +10 pop (958) need a ground ray to find a surface top
    /// 18-40 u under the transform (their origins at x +-15 / +-6 lie inside the player's own collider, so they cannot start
    /// inside a solid: Box2D keeps a dynamic body out of the static ones it collides with, checked on the contact masks and the collider's current overlap), Update's step-up (806-816) a top in [y-33, y+20]
    /// (a ray start inside a solid there is blocked by its own ray4 unless the top is in that band too), OnCollisionStay's
    /// zeroing (2516-2531) a top or bottom face on the collider; so no static box may have its top or bottom face in the
    /// vertical reach. The wall branch (1006: Velocity.y 200 or less, or dashActive) and ray3 (x +-25, Velocity.y under 200)
    /// are ruled out on each tick where their gate can open by no solid being within their reach then. Triggers (springs,
    /// orbs, zones) and moving bodies anywhere in reach, and colliders that are not axis-aligned boxes, end the bound: only a
    /// spike trigger is allowed (spikeScript only kills). Box2D friction on a side contact only slows the body's rise; it
    /// opens the fast-fall gate, which is modelled. Every threshold and range is widened by <see cref="Margin"/>.</para>
    /// </summary>
    static class LabFlight
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Slack on every threshold and range (u, u/s): float rounding, Box2D's 0.01 contact skin, a rebase's rounding.</summary>
        public const float Margin = 2f;

        /// <summary>How deep the player's collider may sit in a static solid: Box2D's steady penetration is its 0.005 linear slop
        /// plus the 0.01 polygon skin; a ray origin this far inside the collider can't be inside a solid.</summary>
        const float FootprintSlack = 0.25f;

        static readonly FieldInfo B2Field = typeof(PhysicsWorld).GetField("_b2", Fields);
        static readonly Dictionary<string, FieldInfo> MovementFields = new Dictionary<string, FieldInfo>();

        /// <summary>Movement's pending Invokes that change no position, velocity or unlock (the buffers, the jump grace; the wall
        /// jump's collider re-enable is not among them: it ends a Ground exclusion, which a free-flight state must not have).</summary>
        static readonly HashSet<string> HarmlessInvokes = new HashSet<string> { "cancelJumpBuffer", "cancelDashBuffer", "cancelJumpCutBuffer", "endRecentJump" };
        /// <summary>Movement's coroutines that only scale the sprite (jump and landing squash) or stop time (TimeStop: no physics
        /// tick runs while it holds, so it moves nothing).</summary>
        static readonly string[] HarmlessRoutines = { "IE_Jump", "IE_Land", "IE_TimeStop" };

        static object Get(Movement m, string name)
        {
            FieldInfo f;
            lock (MovementFields)
                if (!MovementFields.TryGetValue(name, out f))
                    MovementFields[name] = f = typeof(Movement).GetField(name, Fields) ?? throw new MissingFieldException("Movement." + name);
            return f.GetValue(m);
        }

        static float F(Movement m, string name) => Convert.ToSingle(Get(m, name));
        static bool B(Movement m, string name) => (bool)Get(m, name);
        static Vector2 V(Movement m, string name) => (Vector2)Get(m, name);

        struct Box { public float X0, Y0, X1, Y1, Friction; }

        /// <summary>The player's colliders as the bound needs them, relative to the transform (every collider config the normal
        /// collider can switch to: default, dash, current).</summary>
        struct Shape { public float Top, BottomLow, BottomHigh, HalfX, ReachX; }

        /// <summary>One line of the search's header: the constants the bound reads and the player's collider.</summary>
        public static string Describe(Simulation sim)
        {
            Movement m = sim.Player;
            Shape s = PlayerShape(m, out string bad);
            float dt = sim.World.FixedDeltaTime;
            return $"free-flight bound: gravity {50f * dt * m.gravity:0.##}/tick (x1.4 fast fall, fastFallMod {F(m, "fastFallMod"):0.##}), "
                + $"apex anti-grav {50f * dt * F(m, "jumpApexAntiGrav"):0.##}/tick, airJumpForce {m.airJumpForce:0}, runSpeed {m.runSpeed * 10f:0}, "
                + $"body gravity {BodyGravity(sim, m).y:0.###}/tick, player friction {PlayerFriction(sim, m):0.##}; collider top {s.Top:0.#}, "
                + $"bottom {s.BottomLow:0.#}..{s.BottomHigh:0.#}, half width {s.HalfX:0.#}{(bad == null ? "" : " (" + bad + ")")}";
        }

        /// <summary>
        /// The highest the player's transform can be (world frame) at the end of each of the next <paramref name="horizon"/>
        /// ticks (index t = 1..horizon; index 0 is now), or null with <paramref name="why"/> when the state isn't in free flight
        /// or something in reach could end it.
        /// </summary>
        public static float[] Bound(Simulation sim, int horizon, bool boostControl, out string why)
        {
            Movement m = sim.Player;
            why = Preconditions(sim, m);
            if (why != null) return null;
            Shape shape = PlayerShape(m, out why);
            if (why != null) return null;

            float dt = sim.World.FixedDeltaTime;
            var c = new Consts
            {
                Dt = dt,
                G = 50f * dt * m.gravity,
                FastFall = MathF.Max(1f, F(m, "fastFallMod")),
                SlowFall = MathF.Min(1f, F(m, "fastFallMod")),
                Apex = 50f * dt * F(m, "jumpApexAntiGrav"),
                AirJump = m.airJumpForce,
                JumpGate = boostControl ? 200f : 700f,
                MaxDown = m.maxDownwardsSpeed * 8f,
                Body = BodyGravity(sim, m),
                AnyFriction = PlayerFriction(sim, m) > 0f,
            };
            Vector2 v = V(m, "Velocity"), mom = V(m, "momentum");
            c.MomHi = MathF.Max(mom.y, 0f);
            c.MomLo = MathF.Min(mom.y, 0f);
            c.DashEnd = 0;
            if (m.dashActive)
            {
                float r = F(m, "dashFramesRemaining");
                c.DashEnd = r <= 0f ? 1 : (int)MathF.Ceiling(r) + 1;
                c.UpDashEnd = V(m, "dashDirection").y > 0f;
            }
            c.JumpsLeft = m.doubleJumpUnlocked ? System.Math.Max(0, m.airJumpsLeft) : 0;

            // Horizontal reach: Velocity.x after the first tick's MoveLeftAndRight is within runSpeed (x wallJumpDirection
            // under a wall jump's lock) unless a buffered dash press skips it (getPlayerControlledInput returns early only with
            // dashCooldown under 0, and endDash sets it to 0.02 or more); momentum.x only decays or, against the stick, crosses 0
            // by at most MovingOppositeMomentumDecay.
            float run = m.runSpeed * 10f * MathF.Max(1f, MathF.Abs(m.wallJumpDirection));
            float vx = m.dashCooldown - dt >= 0f ? run : MathF.Max(MathF.Abs(v.x), run);
            c.StepX = (vx + MathF.Max(MathF.Abs(mom.x), F(m, "MovingOppositeMomentumDecay")) + MathF.Abs(c.Body.x)) * dt;

            Vector3 at = m.transform.position;
            float x0 = at.x, y0 = at.y;
            Rigidbody2D rb = m.GetComponent<Rigidbody2D>();
            var start = new Branch { Vlo = v.y, Vhi = v.y, Ylo = y0, Yhi = y0, CumLo = y0, CumHi = y0, BodyLo = rb.linearVelocity.y,
                Normal = B(m, "MostRecentUpwardsMoveWasNormalJump"), Jumps = c.JumpsLeft };

            // Pass 1, every gate open and no geometry: the reach any branch can have, for the one query of the world.
            var reach = new Reach { Lo = y0, Hi = y0 };
            Walk(c, start, 0, horizon, null, null, ref reach);
            float xLo = x0 - horizon * c.StepX - Margin, xHi = x0 + horizon * c.StepX + Margin;
            float px = MathF.Max(45f, shape.ReachX) + Margin, py = 45f + MathF.Max(MathF.Abs(shape.BottomLow), MathF.Abs(shape.Top)) + Margin;
            List<Box> boxes = Solids(sim, m, xLo - px, reach.Lo - py, xHi + px, reach.Hi + py, PlayerFriction(sim, m), out why);
            if (why != null) return null;
            // The rays' argument needs the collider out of every solid now (free flight writes no position, so Box2D keeps it out).
            foreach (BoxCollider2D own in m.GetComponents<BoxCollider2D>())
            {
                if (own.isTrigger || !own.enabled) continue;
                Vector3 scale = m.transform.lossyScale;
                float cx = x0 + own.offset.x * scale.x, cy = y0 + own.offset.y * scale.y;
                float hx = own.size.x * MathF.Abs(scale.x) / 2f - FootprintSlack, hy = own.size.y * MathF.Abs(scale.y) / 2f - FootprintSlack;
                foreach (Box b in boxes)
                    if (b.X0 < cx + hx && b.X1 > cx - hx && b.Y0 < cy + hy && b.Y1 > cy - hy) { why = "the collider overlaps a solid"; return null; }
            }
            // A top face where a ground ray, a pop ray, the step-up ray or the collider's bottom can meet it; a bottom face near
            // the collider's top or the step-up rays.
            float topLo = reach.Lo + MathF.Min(-40f, shape.BottomLow) - Margin, topHi = reach.Hi + MathF.Max(20f, shape.BottomHigh) + Margin;
            float botLo = reach.Lo - 45f - Margin, botHi = reach.Hi + MathF.Max(45f, shape.Top) + Margin;
            float probeX = MathF.Max(29f, shape.ReachX) + Margin;
            foreach (Box b in boxes)
            {
                if (b.X1 < xLo - probeX || b.X0 > xHi + probeX) continue;
                if (b.Y1 >= topLo && b.Y1 <= topHi) { why = $"a top face at y {b.Y1 - sim.WorldOriginInScene.y:0.#} in reach"; return null; }
                if (b.Y0 >= botLo && b.Y0 <= botHi) { why = $"a bottom face at y {b.Y0 - sim.WorldOriginInScene.y:0.#} in reach"; return null; }
            }

            // Pass 2: the gates as the state allows them, the gated contacts checked tick by tick against the boxes.
            var geo = new Geometry { Boxes = boxes, X0 = x0, Shape = shape };
            var best = new float[horizon + 1];
            for (int t = 0; t <= horizon; t++) best[t] = float.NegativeInfinity;
            best[0] = y0;
            var unused = new Reach { Lo = y0, Hi = y0 };
            why = Walk(c, start, 0, horizon, geo, best, ref unused);
            if (why != null) return null;
            float originY = sim.WorldOriginInScene.y;
            for (int t = 0; t <= horizon; t++) best[t] += Margin - originY;
            best[0] = y0 - originY;
            return best;
        }

        /// <summary>The player's collider around the transform over every collider config (the finish box is a touch of any player
        /// BoxCollider2D: Simulation.Touches): its highest top, lowest bottom and widest reach either side.</summary>
        public static (float top, float bottom, float reachX) Extents(Movement m)
        {
            Shape s = PlayerShape(m, out _);
            return (s.Top, s.BottomLow, s.ReachX);
        }

        sealed class Consts
        {
            public float Dt, G, FastFall, SlowFall, Apex, AirJump, JumpGate, MaxDown, MomHi, MomLo, StepX;
            public bool AnyFriction;
            public Vector2 Body;
            public int DashEnd, JumpsLeft;
            public bool UpDashEnd;
        }

        struct Branch
        {
            /// <summary>Velocity.y at the end of the tick (both bounds), the transform's y, its lowest and highest so far, the
            /// body's lowest y speed after the step (the next tick's fast-fall gate reads it).</summary>
            public float Vlo, Vhi, Ylo, Yhi, CumLo, CumHi, BodyLo;
            /// <summary>MostRecentUpwardsMoveWasNormalJump may be on (the jump cut can fire); double jumps left.</summary>
            public bool Normal;
            public int Jumps;
            public bool Friction;
        }

        struct Reach { public float Lo, Hi; }

        sealed class Geometry
        {
            public List<Box> Boxes;
            public float X0;
            public Shape Shape;
        }

        /// <summary>
        /// Walks one branch from tick <paramref name="from"/> to <paramref name="horizon"/>, forking at every tick a double jump
        /// can happen; <paramref name="best"/> takes each tick's highest transform y. With <paramref name="geo"/> null (pass 1)
        /// no contact is checked, a side contact's friction is assumed wherever friction exists, and <paramref name="reach"/>
        /// takes the lowest and highest y of any branch.
        /// </summary>
        static string Walk(Consts c, Branch b, int from, int horizon, Geometry geo, float[] best, ref Reach reach)
        {
            for (int t = from + 1; t <= horizon; t++)
            {
                float xLo = geo == null ? 0f : geo.X0 - t * c.StepX - Margin, xHi = geo == null ? 0f : geo.X0 + t * c.StepX + Margin;
                // Fast fall: the stick down with the body's y speed under 300 (1104), or a side contact's friction slowed it.
                bool fastFall = b.BodyLo < 300f + Margin || (geo == null ? c.AnyFriction : b.Friction);
                bool dashOn = c.DashEnd > 0 && t <= c.DashEnd;
                // The wall branch and ray3 read Velocity.y before gravity: last tick's end.
                bool wallGate = b.Vlo <= 200f + Margin || dashOn, rayGate = b.Vlo < 200f + Margin;

                float hi = b.Vhi - c.G * c.SlowFall;
                float lo = b.Vlo - c.G * (b.Vlo < -75f ? c.FastFall : 1f) * (fastFall ? 1.4f : 1f);
                if (hi < -c.MaxDown) hi = -c.MaxDown;
                if (lo < 200f && hi > -50f) hi = MathF.Max(hi, MathF.Min(hi, 200f) + c.Apex);
                bool normal = b.Normal;
                if (c.DashEnd == t)
                {
                    if (c.UpDashEnd) { hi *= 0.35f; lo = MathF.Min(lo * 0.35f, lo); normal = false; }
                    else if (lo < 10f) hi = MathF.Max(hi, 10f);
                }
                if (normal && hi > 150f && lo < 580f) lo = MathF.Min(lo, 50f);

                if (b.Jumps > 0 && lo < c.JumpGate + Margin)
                {
                    // The double jump: Velocity.y = airJumpForce + max(v, 0) / 2 for a v under the gate.
                    Branch jump = b;
                    jump.Vlo = c.AirJump + MathF.Max(lo, 0f) / 2f;
                    jump.Vhi = c.AirJump + MathF.Max(MathF.Min(hi, c.JumpGate + Margin), 0f) / 2f;
                    jump.Normal = true;
                    jump.Jumps = b.Jumps - 1;
                    string why = Advance(c, ref jump, t, xLo, xHi, wallGate, rayGate, geo, best, ref reach);
                    if (why != null) return why;
                    why = Walk(c, jump, t, horizon, geo, best, ref reach);
                    if (why != null) return why;
                }
                b.Vlo = lo;
                b.Vhi = hi;
                b.Normal = normal;
                string stop = Advance(c, ref b, t, xLo, xHi, wallGate, rayGate, geo, best, ref reach);
                if (stop != null) return stop;
            }
            return null;
        }

        /// <summary>Tick <paramref name="t"/>'s step with the branch's Velocity.y bounds set: the body's velocity, the position, and
        /// the gated contacts of the tick.</summary>
        static string Advance(Consts c, ref Branch b, int t, float xLo, float xHi, bool wallGate, bool rayGate, Geometry geo, float[] best, ref Reach reach)
        {
            float bodyHi = b.Vhi + c.MomHi + c.Body.y, bodyLo = b.Vlo + c.MomLo + c.Body.y;
            b.Yhi += bodyHi * c.Dt;
            b.Ylo += bodyLo * c.Dt;
            b.BodyLo = bodyLo;
            b.CumLo = MathF.Min(b.CumLo, b.Ylo);
            b.CumHi = MathF.Max(b.CumHi, b.Yhi);
            if (geo == null)
            {
                reach.Lo = MathF.Min(reach.Lo, b.CumLo);
                reach.Hi = MathF.Max(reach.Hi, b.CumHi);
                return null;
            }
            if (b.Yhi > best[t]) best[t] = b.Yhi;
            b.Friction = false;
            Shape s = geo.Shape;
            foreach (Box box in geo.Boxes)
            {
                // The wall rays (x +-15, 12 + 3 u sideways, at y - 18 and y + 19) and ray3 (x +-25, y - 18 down 22).
                if (wallGate && box.X1 >= xLo - 30f - Margin && box.X0 <= xHi + 30f + Margin
                    && box.Y1 >= b.CumLo - 18f - Margin && box.Y0 <= b.CumHi + 19f + Margin)
                    return $"a wall contact possible on tick {t} (x {box.X0:0.#}..{box.X1:0.#})";
                if (rayGate && box.X1 >= xLo - 25f - Margin && box.X0 <= xHi + 25f + Margin
                    && box.Y1 >= b.CumLo - 40f - Margin && box.Y0 <= b.CumHi - 18f + Margin)
                    return $"a ray3 contact possible on tick {t} (x {box.X0:0.#}..{box.X1:0.#})";
                if (box.Friction > 0f && box.X1 >= xLo - s.ReachX - Margin && box.X0 <= xHi + s.ReachX + Margin
                    && box.Y1 >= b.CumLo + s.BottomLow - Margin && box.Y0 <= b.CumHi + s.Top + Margin)
                    b.Friction = true;
            }
            return null;
        }

        static string Preconditions(Simulation sim, Movement m)
        {
            string mode = Get(m, "cutsceneMode").ToString();
            if (mode != "none") return "cutsceneMode " + mode;
            if (B(m, "isDead")) return "dead";
            if (m.onGround) return "onGround";
            if (m.OnWall) return "OnWall";
            if (F(m, "wallCoyoteFrames") > 0f) return "wall coyote frames left";
            if (B(m, "isFinalWallClingActive")) return "final wall cling";
            if (m.pauseMenu != null && m.pauseMenu.menuOpen) return "pause menu open";
            // A squish (848-866) sets the body's velocity to 0 for the tick; on static geometry the collider's distance is the
            // contact skin at most, so only a squish distance above that could fire.
            if (F(m, "squishDistance") > -Margin) return "squishDistance " + F(m, "squishDistance").ToString("0.##") + " could fire on a side contact";
            if (m.dashUnlocked && m.airDashesLeft > 0) return "a dash left";
            if (m.currentlyTouchedMovingPlatform != null) return "on a moving platform";
            foreach (Scheduler.Pending p in sim.Scheduler.Queue)
                if (!(ReferenceEquals(p.Target, m) && HarmlessInvokes.Contains(p.Method)))
                    return "pending " + (p.Target == null ? "?" : p.Target.GetType().Name) + "." + p.Method;
            foreach (Scheduler.Routine r in sim.Scheduler.Routines)
                if (!(ReferenceEquals(r.Target, m) && HarmlessRoutines.Any(n => r.Body.GetType().Name.Contains(n))))
                    return "running coroutine " + (r.Target == null ? "?" : r.Target.GetType().Name) + "." + r.Body.GetType().Name;
            return null;
        }

        static Shape PlayerShape(Movement m, out string bad)
        {
            bad = null;
            Vector3 scale = m.transform.lossyScale;
            float sx = MathF.Abs(scale.x), sy = MathF.Abs(scale.y);
            var normal = (BoxCollider2D)Get(m, "normalCollider");
            var s = new Shape { Top = float.NegativeInfinity, BottomLow = float.PositiveInfinity, BottomHigh = float.NegativeInfinity,
                HalfX = float.PositiveInfinity, ReachX = 0f };
            foreach (BoxCollider2D box in m.GetComponents<BoxCollider2D>())
            {
                var configs = new List<(Vector2 offset, Vector2 size)> { (box.offset, box.size) };
                if (ReferenceEquals(box, normal))
                {
                    configs.Add((V(m, "defaultColliderOffset"), V(m, "defaultColliderSize")));
                    configs.Add((V(m, "dashColliderOffset"), V(m, "dashColliderSize")));
                }
                foreach (var (offset, size) in configs)
                {
                    float hx = size.x * sx / 2f, hy = size.y * sy / 2f, ox = MathF.Abs(offset.x * sx), oy = offset.y * sy;
                    s.Top = MathF.Max(s.Top, oy + hy);
                    s.ReachX = MathF.Max(s.ReachX, ox + hx);
                    s.BottomLow = MathF.Min(s.BottomLow, oy - hy);
                    if (box.isTrigger) continue;
                    if (!ReferenceEquals(box, normal)) continue;
                    s.BottomHigh = MathF.Max(s.BottomHigh, oy - hy);
                    s.HalfX = MathF.Min(s.HalfX, hx - ox);
                }
            }
            // The ground and pop rays' origins (x +-15 and +-6 at y - 18) must lie inside the normal collider in every config.
            if (normal == null || !(s.HalfX > 15f + FootprintSlack) || !(s.BottomHigh < -18f - FootprintSlack))
                bad = "the ground rays' origins aren't inside the player's collider";
            return s;
        }

        static Vector2 BodyGravity(Simulation sim, Movement m)
        {
            Vector2 g = sim.World.Physics2DSettings.TryGetValue("gravity", out JsonElement e) ? World.Vec2(e) : Vector2.zero;
            float scale = m.GetComponent<Rigidbody2D>().gravityScale;
            return g * scale * sim.World.FixedDeltaTime;
        }

        static float PlayerFriction(Simulation sim, Movement m)
        {
            float friction = 0f;
            foreach (BoxCollider2D box in m.GetComponents<BoxCollider2D>())
                if (!box.isTrigger) friction = MathF.Max(friction, box.friction);
            return friction;
        }

        /// <summary>
        /// Every collider of the live physics world in the rectangle (scene frame), as axis-aligned boxes; why is set when one
        /// could end free flight wherever it is (a trigger other than a spike, a moving body, a collider that isn't an
        /// axis-aligned box).
        /// </summary>
        static List<Box> Solids(Simulation sim, Movement m, float x0, float y0, float x1, float y1, float playerFriction, out string why)
        {
            why = null;
            // Brings the Box2D world up to date (a rebase moves static bodies lazily): a zero-length ray changes nothing.
            sim.Physics.Raycast(new Vector2(x0, y0), Vector2.down, 0f, 0);
            var world = (b2World)B2Field.GetValue(sim.Physics);
            var area = new b2AABB { lowerBound = new b2Vec2(x0, y0), upperBound = new b2Vec2(x1, y1) };
            const int Max = 4096;
            int count = world.QueryAABB(out b2Fixture[] fixtures, area, Max);
            if (count >= Max) { why = "too many colliders in reach"; return null; }
            var boxes = new List<Box>();
            GameObject player = m.gameObject;
            var normal = (BoxCollider2D)Get(m, "normalCollider");
            (int mask, int layer)? playerMask = null;
            var masks = new List<(int mask, int layer, string name)>();
            for (int i = 0; i < count; i++)
            {
                b2Fixture f = fixtures[i];
                Collider2D collider = ColliderOf(f);
                if (collider != null && ReferenceEquals(collider.gameObject, player))
                {
                    if (ReferenceEquals(collider, normal)) playerMask = (Mask(f, "ContactMask"), Mask(f, "Layer"));
                    continue;
                }
                b2Shape shape = f.GetShape();
                b2Body body = f.GetBody();
                b2Transform xf = body.GetTransform();
                string name = collider == null ? "?" : collider.gameObject.name;
                for (int child = 0; child < shape.GetChildCount(); child++)
                {
                    shape.ComputeAABB(out b2AABB box, xf, child);
                    if (box.upperBound.x < x0 || box.lowerBound.x > x1 || box.upperBound.y < y0 || box.lowerBound.y > y1) continue;
                    if (f.IsSensor())
                    {
                        if (collider != null && collider.GetComponent<spikeScript>() != null && OnlySpike(collider.gameObject)) continue;
                        why = "a trigger in reach (" + name + ")";
                        return null;
                    }
                    if (body.Type() != b2BodyType.Static) { why = "a moving body in reach (" + name + ")"; return null; }
                    if (!AxisAlignedBox(shape, body)) { why = "a collider that isn't an axis-aligned box in reach (" + name + ")"; return null; }
                    boxes.Add(new Box { X0 = box.lowerBound.x, Y0 = box.lowerBound.y, X1 = box.upperBound.x, Y1 = box.upperBound.y,
                        Friction = MathF.Sqrt(MathF.Max(0f, f.m_friction) * playerFriction) });
                    masks.Add((Mask(f, "ContactMask"), Mask(f, "Layer"), name));
                }
            }
            // Box2D keeps the collider out of a solid only if the two collide (the layer matrix and the collider's
            // excludeLayers: a wall jump's Ground exclusion, PhysicsWorld's contact filter).
            if (playerMask == null) { why = "the player's collider isn't in the physics world"; return null; }
            for (int i = 0; i < masks.Count; i++)
                if ((playerMask.Value.mask & (1 << masks[i].layer)) == 0 || (masks[i].mask & (1 << playerMask.Value.layer)) == 0)
                { why = "a solid the player passes through in reach (" + masks[i].name + ")"; return null; }
            return boxes;
        }

        static bool OnlySpike(GameObject go)
        {
            foreach (MonoBehaviour script in go.GetComponents<MonoBehaviour>())
                if (script != null && !(script is spikeScript)) return false;
            return true;
        }

        static FieldInfo _colliderField;

        static int Mask(b2Fixture f, string field) => (int)f.UserData.GetType().GetField(field, Fields).GetValue(f.UserData);

        static Collider2D ColliderOf(b2Fixture f)
        {
            object state = f.UserData;
            if (state == null) return null;
            FieldInfo field = _colliderField;
            if (field == null || field.DeclaringType != state.GetType()) _colliderField = field = state.GetType().GetField("Collider", Fields);
            return field?.GetValue(state) as Collider2D;
        }

        /// <summary>A 4-vertex polygon, axis-aligned in the body's frame, on a body turned by a multiple of 90 degrees: its AABB is
        /// the shape (plus its skin).</summary>
        static bool AxisAlignedBox(b2Shape shape, b2Body body)
        {
            if (!(shape is b2PolygonShape poly) || poly.m_count != 4) return false;
            float quarter = body.GetAngle() / (MathF.PI / 2f);
            if (MathF.Abs(quarter - MathF.Round(quarter)) > 1e-4f) return false;
            for (int i = 0; i < 4; i++)
            {
                b2Vec2 a = poly.m_vertices[i], b = poly.m_vertices[(i + 1) % 4];
                if (MathF.Abs(a.x - b.x) > 1e-3f && MathF.Abs(a.y - b.y) > 1e-3f) return false;
            }
            return true;
        }
    }
}
