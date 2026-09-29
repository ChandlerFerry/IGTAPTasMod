/*
  Box2D Copyright © 2020 Ben Ukhanov & Hugh Phoenix-Hulme https://github.com/benzuk/box2d-netstandard
  Box2DX Copyright (c) 2009 Ihar Kalasouski http://code.google.com/p/box2dx
  
// MIT License

// Copyright (c) 2019 Erin Catto

// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:

// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
*/

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Math = Box2D.Math;

namespace Box2D
{
    /// <summary>
    ///  The world class manages all physics entities, dynamic simulation,
    ///  and asynchronous queries.
    /// </summary>
    public partial class b2World
    {
        //public b2BroadPhase     _broadPhase;
        public readonly ContactManager m_contactManager;
        private bool m_continuousPhysics;
        private bool m_subStepping;

        private bool m_allowSleep;

        private int m_bodyCount;

        // Contacts number their start of touching from this (b2Contact.m_touchOrder). Only the order matters, but a snapshot
        // saves and restores it with the contacts, so a restored world is the saved one bit for bit (b2World.StateDigest).
        private long m_touchCount;
        public long NextTouchOrder() => ++m_touchCount;

        // The two proxies' vertex buffers of this world's GJK queries (b2DistanceProxy.Set): a sensor contact's overlap
        // test and SolveTOI's time of impact. Neither runs inside the other, and a world is stepped by one thread.
        internal readonly b2Vec2[] m_proxyBufferA = new b2Vec2[2], m_proxyBufferB = new b2Vec2[2];

        private b2Body m_bodyList;

        private b2Vec2 m_gravity;

        private float m_inv_dt0;
        private bool m_locked;

        public bool m_newContacts;

        private bool m_stepComplete;

        public b2World() : this(new b2Vec2(0, -10))
        { }

        /// <summary>
        ///  Construct a world object.
        /// </summary>
        /// <param name="worldAABB">A bounding box that completely encompasses all your shapes.</param>
        /// <param name="gravity">The world gravity vector.</param>
        /// <param name="doSleep">Improve performance by not simulating inactive bodies.</param>
        public b2World(b2Vec2 gravity)
        {
            m_bodyList = null;

            m_bodyCount = 0;

            m_continuousPhysics = true;
            m_subStepping = false;

            m_stepComplete = true;
            m_allowSleep = true;
            m_gravity = gravity;

            m_newContacts = false;
            m_locked = false;

            m_inv_dt0 = 0.0f;

            m_contactManager = new ContactManager();
        }

        /// <summary>
        ///  Get the world body list. With the returned body, use b2Body.GetNext to get
        ///  the next body in the world list. A null body indicates the end of the list.
        /// </summary>
        /// <returns>The head of the world body list.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Body GetBodyList() => m_bodyList;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Contact GetContactList() => m_contactManager.m_contactList;

        /// <summary>
        ///  Get the number of bodies.
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetBodyCount() => m_bodyCount;

        /// <summary>
        ///  Get the number of contacts (each may have 0 or more contact points).
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetContactCount() => m_contactManager.m_contactCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsLocked() => m_locked;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ContactManager GetContactManager() => m_contactManager;

        /// <summary>
        ///  Register a contact filter to provide specific control over collision.
        ///  Otherwise the default filter is used (b2_defaultFilter).
        /// </summary>
        /// <param name="filter"></param>
        public void SetContactFilter(b2ContactFilter filter)
        {
            m_contactManager.m_contactFilter = filter;
        }

        /// <summary>
        ///  Create a rigid body given a definition. No reference to the definition
        ///  is retained.
        ///  @warning This function is locked during callbacks.
        /// </summary>
        /// <param name="def"></param>
        /// <returns></returns>
        public b2Body CreateBody(b2BodyDef def)
        {
            //Debug.Assert(_locked == false);

            if (m_locked)
            {
                throw new
                    Box2DException("Cannot create bodies in the middle of Step. Has this been spawned from an event such as a b2ContactListener callback?");
            }


            var b = new b2Body(def, this);

            // Add to world doubly linked list.
            b.m_prev = null;
            b.m_next = m_bodyList;
            if (m_bodyList != null)
            {
                m_bodyList.m_prev = b;
            }

            m_bodyList = b;
            ++m_bodyCount;

            return b;
        }

        // Find islands, integrate and solve constraints, solve position constraints
        private void Solve(b2TimeStep step)
        {
            // Size the island for the worst case (reused across steps while it is big enough; only counts are read).
            if (m_island == null || !m_island.Fits(m_bodyCount, m_contactManager.m_contactCount))
                m_island = new b2Island(m_bodyCount, m_contactManager.m_contactCount);
            b2Island island = m_island;

            // Clear all the island flags.
            for (b2Body b = m_bodyList; b != null; b = b.m_next)
            {
                b.UnsetFlag(b2BodyFlags.b2Island);
            }

            for (b2Contact c = m_contactManager.m_contactList; c != null; c = c.m_next)
            {
                c.m_flags &= ~b2CollisionFlags.b2Island;
            }

            // Build and simulate all awake islands.
            int stackSize = m_bodyCount;
            //Stack<b2Body> stack = new Stack<b2Body>(_bodyCount);
            //b2Body stack     = (b2Body**) m_stackAllocator.Allocate(stackSize * sizeof(b2Body*));
            if (m_islandStack == null || m_islandStack.Length < m_bodyCount) m_islandStack = new b2Body[m_bodyCount];
            b2Body[] stack = m_islandStack;
            for (b2Body seed = m_bodyList; seed != null; seed = seed.m_next)
            {
                if (seed.HasFlag(b2BodyFlags.b2Island))
                {
                    continue;
                }

                if (seed.IsAwake() == false || seed.IsEnabled() == false)
                {
                    continue;
                }

                // The seed can be dynamic or kinematic.
                if (seed.m_type == b2BodyType.Static)
                {
                    continue;
                }

                // Reset island and stack.
                island.Clear();
                var stackCount = 0;
                stack[stackCount++] = seed;
                seed.SetFlag(b2BodyFlags.b2Island);

                // Perform a depth first search (DFS) on the constraint graph.
                while (stackCount > 0)
                {
                    // Grab the next body off the stack and add it to the island.
                    b2Body b = stack[--stackCount];
                    ////Debug.Assert(b.IsEnabled() == true);
                    island.Add(b);

                    // To keep islands as small as possible, we don't
                    // propagate islands across static bodies.
                    if (b.m_type == b2BodyType.Static)
                    {
                        continue;
                    }

                    // Make sure the body is awake (without resetting sleep timer).
                    b.SetFlag(b2BodyFlags.Awake);

                    // Search all contacts connected to this body.
                    for (b2ContactEdge ce = b.m_contactList; ce != null; ce = ce.next)
                    {
                        b2Contact contact = ce.contact;

                        // Has this contact already been added to an island?
                        if ((contact.m_flags & b2CollisionFlags.b2Island) == b2CollisionFlags.b2Island)
                        {
                            continue;
                        }

                        // Is this contact solid and touching?
                        if (contact.IsEnabled() == false ||
                            contact.IsTouching() == false)
                        {
                            continue;
                        }

                        // Skip sensors.
                        bool sensorA = contact.m_fixtureA.IsSensor();
                        bool sensorB = contact.m_fixtureB.IsSensor();
                        if (sensorA || sensorB)
                        {
                            continue;
                        }

                        island.Add(contact);
                        contact.m_flags |= b2CollisionFlags.b2Island;

                        b2Body other = ce.other;

                        // Was the other body already added to this island?
                        if (other.HasFlag(b2BodyFlags.b2Island))
                        {
                            continue;
                        }

                        //Debug.Assert(stackCount < stackSize);
                        stack[stackCount++] = other;
                        other.SetFlag(b2BodyFlags.b2Island);
                    }
                }

                island.Solve(step, m_gravity, m_allowSleep);

                // Post solve cleanup.
                for (var i = 0; i < island.m_bodyCount; ++i)
                {
                    // Allow static bodies to participate in other islands.
                    b2Body b = island.m_bodies[i];
                    if (b.m_type == b2BodyType.Static)
                    {
                        b.UnsetFlag(b2BodyFlags.b2Island);
                    }
                }
            }

            stack = null;

            {
                // Synchronize fixtures, check for out of range bodies.
                for (b2Body b = m_bodyList; b != null; b = b.GetNext())
                {
                    // If a body was not in an island then it did not move.
                    if (!b.HasFlag(b2BodyFlags.b2Island))
                    {
                        continue;
                    }

                    if (b.m_type == b2BodyType.Static)
                    {
                        continue;
                    }

                    // Update fixtures (for broad-phase).
                    b.SynchronizeFixtures();
                }

                // Look for new contacts.
                m_contactManager.FindNewContacts();
            }
        }

        // Find TOI contacts and solve them.
        public void SetContinuousPhysics(bool flag) => m_continuousPhysics = flag;

        public void SetSubStepping(bool flag) => m_subStepping = flag;

        private void SolveTOI(in b2TimeStep step)
        {
            // The TOI island's capacity is part of its logic (it stops adding at capacity): reuse only an exact match.
            if (m_toiIsland == null || m_toiIsland.m_bodyCapacity != 2 * b2Settings.maxTOIContacts
                || m_toiIsland.m_contactCapacity != b2Settings.maxTOIContacts)
                m_toiIsland = new b2Island(2 * b2Settings.maxTOIContacts, b2Settings.maxTOIContacts);
            b2Island island = m_toiIsland;

            if (m_stepComplete)
            {
                for (b2Body b = m_bodyList; b != null; b = b.m_next)
                {
                    b.UnsetFlag(b2BodyFlags.b2Island);
                    b.m_sweep.alpha0 = 0.0f;
                }

                for (b2Contact c = m_contactManager.m_contactList; c != null; c = c.m_next)
                {
                    // Invalidate TOI
                    c.m_flags &= ~(b2CollisionFlags.Toi | b2CollisionFlags.b2Island);
                    c.m_toiCount = 0;
                    c.m_toi = 1.0f;
                }
            }

            // Find TOI events and solve them.
            for (; ; )
            {
                // Find the first TOI.
                b2Contact minContact = null;
                var minAlpha = 1.0f;

                for (b2Contact c = m_contactManager.m_contactList; c != null; c = c.m_next)
                {
                    // Is this contact disabled?
                    if (c.IsEnabled() == false)
                    {
                        continue;
                    }

                    // Prevent excessive sub-stepping.
                    if (c.m_toiCount > b2Settings.maxSubSteps)
                    {
                        continue;
                    }

                    var alpha = 1.0f;
                    if ((c.m_flags & b2CollisionFlags.Toi) == b2CollisionFlags.Toi)
                    {
                        // This contact has a valid cached TOI.
                        alpha = c.m_toi;
                    }
                    else
                    {
                        b2Fixture fA = c.FixtureA;
                        b2Fixture fB = c.FixtureB;

                        // Is there a sensor?
                        if (fA.IsSensor() || fB.IsSensor())
                        {
                            continue;
                        }

                        b2Body bA = fA.b2Body;
                        b2Body bB = fB.b2Body;

                        bool activeA = bA.IsAwake() && bA.m_type != b2BodyType.Static;
                        bool activeB = bB.IsAwake() && bB.m_type != b2BodyType.Static;

                        // Is at least one body active (awake and dynamic or kinematic)?
                        if (activeA == false && activeB == false)
                        {
                            continue;
                        }

                        bool collideA = bA.IsBullet() || bA.m_type != b2BodyType.Dynamic;
                        bool collideB = bB.IsBullet() || bB.m_type != b2BodyType.Dynamic;

                        // Are these two non-bullet dynamic bodies?
                        if (collideA == false && collideB == false)
                        {
                            continue;
                        }

                        // Compute the TOI for this contact.
                        // Put the sweeps onto the same time interval.
                        float alpha0 = bA.m_sweep.alpha0;

                        if (bA.m_sweep.alpha0 < bB.m_sweep.alpha0)
                        {
                            bA.m_sweep.Advance(alpha0 = bB.m_sweep.alpha0);
                        }
                        else if (bB.m_sweep.alpha0 < bA.m_sweep.alpha0)
                        {
                            bB.m_sweep.Advance(alpha0);
                        }

                        //Debug.Assert(alpha0 < 1.0f);

                        // Compute the time of impact in interval [0, minTOI]
                        b2TOIInput input;
                        input.proxyA = new b2DistanceProxy();
                        input.proxyA.Set(fA.b2Shape, c.ChildIndexA, m_proxyBufferA);
                        input.proxyB = new b2DistanceProxy();
                        input.proxyB.Set(fB.b2Shape, c.ChildIndexB, m_proxyBufferB);
                        input.sweepA = bA.m_sweep;
                        input.sweepB = bB.m_sweep;
                        input.tMax = 1.0f;

                        TOI.TimeOfImpact(out b2TOIOutput output, in input);

                        // Beta is the fraction of the remaining portion of the .
                        if (output.state == TOIOutputState.Touching)
                        {
                            alpha = MathF.Min(alpha0 + (1.0f - alpha0) * output.t, 1.0f);
                        }
                        else
                        {
                            alpha = 1.0f;
                        }

                        c.m_toi = alpha;
                        c.m_flags |= b2CollisionFlags.Toi;
                    }

                    if (alpha < minAlpha)
                    {
                        // This is the minimum TOI found so far.
                        minContact = c;
                        minAlpha = alpha;
                    }
                }

                if (minContact == null || 1.0f - 10.0f * b2Settings.FLT_EPSILON < minAlpha)
                {
                    // No more TOI events. Done!
                    m_stepComplete = true;
                    break;
                }

                {
                    // Advance the bodies to the TOI.
                    b2Fixture fA = minContact.m_fixtureA;
                    b2Fixture fB = minContact.m_fixtureB;
                    b2Body bA = fA.b2Body;
                    b2Body bB = fB.b2Body;

                    b2Sweep backup1 = bA.m_sweep;
                    b2Sweep backup2 = bB.m_sweep;

                    bA.Advance(minAlpha);
                    bB.Advance(minAlpha);

                    // The TOI contact likely has some new contact points.
                    minContact.Update();
                    minContact.m_flags &= ~b2CollisionFlags.Toi;
                    ++minContact.m_toiCount;

                    // Is the contact solid?
                    if (minContact.IsEnabled() == false || minContact.IsTouching() == false)
                    {
                        // Restore the sweeps.
                        minContact.SetEnabled(false);
                        bA.m_sweep = backup1;
                        bB.m_sweep = backup2;
                        bA.SynchronizeTransform();
                        bB.SynchronizeTransform();
                        continue;
                    }

                    bA.SetAwake(true);
                    bB.SetAwake(true);

                    // Build the island
                    island.Clear();
                    island.Add(bA);
                    island.Add(bB);
                    island.Add(minContact);

                    bA.SetFlag(b2BodyFlags.b2Island);
                    bB.SetFlag(b2BodyFlags.b2Island);
                    minContact.m_flags |= b2CollisionFlags.b2Island;

                    // Get contacts on bodyA and bodyB.
                    for (var i = 0; i < 2; ++i)
                    {
                        b2Body body = i == 0 ? bA : bB;
                        if (body.m_type == b2BodyType.Dynamic)
                        {
                            for (b2ContactEdge ce = body.m_contactList; ce != null; ce = ce.next)
                            {
                                if (island.m_bodyCount == island.m_bodyCapacity)
                                {
                                    break;
                                }

                                if (island.m_contactCount == island.m_contactCapacity)
                                {
                                    break;
                                }

                                b2Contact contact = ce.contact;

                                // Has this contact already been added to the island?
                                if ((contact.m_flags & b2CollisionFlags.b2Island) == b2CollisionFlags.b2Island)
                                {
                                    continue;
                                }

                                // Only add static, kinematic, or bullet bodies.
                                b2Body other = ce.other;
                                if (other.m_type == b2BodyType.Dynamic &&
                                    body.IsBullet() == false && other.IsBullet() == false)
                                {
                                    continue;
                                }

                                // Skip sensors.
                                bool sensorA = contact.m_fixtureA.IsSensor();
                                bool sensorB = contact.m_fixtureB.IsSensor();
                                if (sensorA || sensorB)
                                {
                                    continue;
                                }

                                // Tentatively advance the body to the TOI.
                                b2Sweep backup = other.m_sweep;
                                if (!other.HasFlag(b2BodyFlags.b2Island))
                                {
                                    other.Advance(minAlpha);
                                }

                                // Update the contact points
                                contact.Update();

                                // Was the contact disabled by the user?
                                if (contact.IsEnabled() == false)
                                {
                                    other.m_sweep = backup;
                                    other.SynchronizeTransform();
                                    continue;
                                }

                                // Are there contact points?
                                if (contact.IsTouching() == false)
                                {
                                    other.m_sweep = backup;
                                    other.SynchronizeTransform();
                                    continue;
                                }

                                // Add the contact to the island
                                contact.m_flags |= b2CollisionFlags.b2Island;
                                island.Add(contact);

                                // Has the other body already been added to the island?
                                if (other.HasFlag(b2BodyFlags.b2Island))
                                {
                                    continue;
                                }

                                // Add the other body to the island.
                                other.SetFlag(b2BodyFlags.b2Island);

                                if (other.m_type != b2BodyType.Static)
                                {
                                    other.SetAwake(true);
                                }

                                island.Add(other);
                            }
                        }
                    }

                    b2TimeStep subStep;
                    subStep.dt = (1.0f - minAlpha) * step.dt;
                    subStep.inv_dt = 1.0f / subStep.dt;
                    subStep.dtRatio = 1.0f;
                    subStep.positionIterations = 20;
                    subStep.velocityIterations = step.velocityIterations;
                    subStep.warmStarting = false;
                    island.SolveTOI(in subStep, bA.m_islandIndex, bB.m_islandIndex);

                    // Reset island flags and synchronize broad-phase proxies.
                    for (var i = 0; i < island.m_bodyCount; ++i)
                    {
                        b2Body body = island.m_bodies[i];
                        body.UnsetFlag(b2BodyFlags.b2Island);

                        if (body.m_type != b2BodyType.Dynamic)
                        {
                            continue;
                        }

                        body.SynchronizeFixtures();

                        // Invalidate all contact TOIs on this displaced body.
                        for (b2ContactEdge ce = body.m_contactList; ce != null; ce = ce.next)
                        {
                            ce.contact.m_flags &= ~(b2CollisionFlags.Toi | b2CollisionFlags.b2Island);
                        }
                    }

                    // Commit fixture proxy movements to the broad-phase so that new contacts are created.
                    // Also, some contacts can be destroyed.
                    m_contactManager.FindNewContacts();

                    if (m_subStepping)
                    {
                        m_stepComplete = false;
                        break;
                    }
                }
            }
        }

        /// <summary>
        ///  Take a time step. This performs collision detection, integration,
        ///  and constraint solution.
        /// </summary>
        /// <param name="dt">The amount of time to simulate, this should not vary.</param>
        /// <param name="velocityIterations">Iterations for the velocity constraint solver.</param>
        /// <param name="positionIterations">Iterations for the position constraint solver.</param>
        public void Step(float dt, int velocityIterations, int positionIterations)
        {
            if (m_newContacts)
            {
                m_contactManager.FindNewContacts();
                m_newContacts = false;
            }

            m_locked = true;

            b2TimeStep step;
            step.dt = dt;
            step.velocityIterations = velocityIterations;
            step.positionIterations = positionIterations;
            if (dt > 0.0f)
            {
                step.inv_dt = 1.0f / dt;
            }
            else
            {
                step.inv_dt = 0.0f;
            }

            step.dtRatio = m_inv_dt0 * dt;

            step.warmStarting = true;

            // Update contacts. This is where some contacts are destroyed.
            {
                m_contactManager.Collide();
            }

            // Integrate velocities, solve velocity constraints, and integrate positions.
            if (m_stepComplete && step.dt > 0.0f)
            {
                Solve(step);
            }

            // Handle TOI events.
            if (m_continuousPhysics && step.dt > 0.0f)
            {
                SolveTOI(step);
            }

            if (step.dt > 0.0f)
            {
                m_inv_dt0 = step.inv_dt;
            }

            ClearForces();

            m_locked = false;
        }

        public void ClearForces()
        {
            for (b2Body body = m_bodyList; body != null; body = body.GetNext())
            {
                body.m_force = b2Vec2.Zero;
                body.m_torque = 0.0f;
            }
        }

        // The broadphase callbacks are bound once and read the caller's callback from a field, so queries don't allocate.
        private b2QueryCallback m_queryCallback;
        private b2Island m_island, m_toiIsland;
        private b2Body[] m_islandStack;
        private b2RayCastCallback m_rayCastCallback;
        private Func<int, bool> m_queryProxy;
        private Func<b2RayCastInput, int, float> m_rayCastProxy;

        public void QueryAABB(b2QueryCallback callback, in b2AABB aabb)
        {
            m_queryCallback = callback;
            m_contactManager.m_broadPhase.Query(m_queryProxy ??= QueryProxy, aabb);
        }

        private bool QueryProxy(int proxyId)
        {
            var proxy = (FixtureProxy)m_contactManager.m_broadPhase.GetUserData(proxyId);
            return m_queryCallback.ReportFixture(proxy.fixture);
        }

        public int QueryAABB(out b2Fixture[] fixtures, in b2AABB aabb, int maxFixtures = 256)
        {
            var result = new b2Fixture[maxFixtures];

            var i = 0;

            bool internalCallback(int proxyId)
            {
                var proxy = (FixtureProxy)m_contactManager.m_broadPhase.GetUserData(proxyId);
                result[i++] = proxy.fixture;
                return i != maxFixtures;
            }

            m_contactManager.m_broadPhase.Query(internalCallback, aabb);

            fixtures = result;
            return i;
        }

        public void RayCast(b2RayCastCallback callback, in b2Vec2 point1, in b2Vec2 point2)
        {
            m_rayCastCallback = callback;
            b2RayCastInput input;
            input.maxFraction = 1.0f;
            input.p1 = point1;
            input.p2 = point2;
            m_contactManager.m_broadPhase.RayCast(m_rayCastProxy ??= RayCastProxy, in input);
        }

        // b2WorldRayCastWrapper: the callback's return clips the ray (closest hit), -1 ignores the fixture, 0 ends
        // the cast. The base port ignored the return and never clipped, so a ray through several shapes of one
        // composite reported the last in tree order instead of the nearest (route-c1-wjds-1.06 tick 41: the
        // dash step-up ray hit a ledge's interior diagonal, 2.3 units below its top).
        private float RayCastProxy(b2RayCastInput input, int proxyId)
        {
            object userData = m_contactManager.m_broadPhase.GetUserData(proxyId);
            var proxy = (FixtureProxy)userData;
            b2Fixture fixture = proxy.fixture;
            int index = proxy.childIndex;
            bool hit = fixture.RayCast(out b2RayCastOutput output, input, index);

            if (hit)
            {
                float fraction = output.fraction;
                b2Vec2 point = (1f - fraction) * input.p1 + fraction * input.p2;
                return m_rayCastCallback.ReportFixture(fixture, point, output.normal, fraction);
            }

            return input.maxFraction;
        }
    }
}