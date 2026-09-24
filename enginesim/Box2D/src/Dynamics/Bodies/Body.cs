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

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Box2D
{
    /// <summary>
    ///  A rigid body. These are created via b2World.CreateBody.
    /// </summary>
    [DebuggerDisplay("{UserData}")]
    public class b2Body
    {
        private readonly b2World m_world;
        public float m_angularDamping;
        public float m_angularVelocity;
        public b2ContactEdge m_contactList;
        public int m_fixtureCount;

        public b2Fixture m_fixtureList;
        internal b2BodyFlags m_flags;

        public b2Vec2 m_force;
        public float m_gravityScale;
        internal float m_I;
        public float m_invI;
        public float m_invMass;

        public int m_islandIndex;

        public b2JointEdge m_jointList;

        public float m_linearDamping;

        public b2Vec2 m_linearVelocity;

        public float m_mass;
        public b2Body m_next;
        public b2Body m_prev;

        public float m_sleepTime;

        public b2Sweep m_sweep; // the swept motion for CCD
        public float m_torque;

        public b2BodyType m_type;

        public b2Transform m_xf; // the body origin transform

        // No public default constructor
        private b2Body()
        { }

        // private

        private b2Body(in b2BodyDef bd, b2World world)
        { }


        public b2Body(b2BodyDef bd, b2World world)
        {
            //Debug.Assert(bd.position.IsValid());
            //Debug.Assert(bd.linearVelocity.IsValid());

            m_flags = 0;

            if (bd.bullet)
            {
                SetFlag(b2BodyFlags.Bullet);
            }

            if (bd.fixedRotation)
            {
                SetFlag(b2BodyFlags.FixedRotation);
            }

            if (bd.allowSleep)
            {
                SetFlag(b2BodyFlags.AutoSleep);
            }

            if (bd.awake && bd.type != b2BodyType.Static)
            {
                SetFlag(b2BodyFlags.Awake);
            }

            if (bd.enabled)
            {
                SetFlag(b2BodyFlags.Enabled);
            }

            m_world = world;

            m_xf.p = bd.position;
            m_xf.q = Matrex.CreateRotation(bd.angle); // Actually about twice as fast to use our own function

            m_sweep.localCenter = b2Vec2.Zero;
            m_sweep.c0 = m_xf.p;
            m_sweep.c = m_xf.p;
            m_sweep.a0 = bd.angle;
            m_sweep.a = bd.angle;
            m_sweep.alpha0 = 0.0f;

            m_jointList = null;
            m_contactList = null;
            m_prev = null;
            m_next = null;

            m_linearVelocity = bd.linearVelocity;
            m_angularVelocity = bd.angularVelocity;

            m_linearDamping = bd.linearDamping;
            m_angularDamping = bd.angularDamping;
            m_gravityScale = bd.gravityScale;

            m_force = b2Vec2.Zero;
            m_torque = 0.0f;

            m_sleepTime = 0.0f;

            m_type = bd.type;

            m_mass = 0.0f;
            m_invMass = 0.0f;

            m_I = 0.0f;
            m_invI = 0.0f;

            UserData = bd.userData;

            m_fixtureList = null;
            m_fixtureCount = 0;
        }


        public b2Transform b2Transform
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_xf;
        }

        public b2Vec2 b2Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_xf.p;
        }

        public object UserData
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            set;
        }

        // public
        /// <summary>
        ///  Creates a fixture and attaches it to this body. Use this function if you need
        ///  to set some fixture parameters, like friction. Otherwise you can create the
        ///  fixture directly from a shape.
        ///  If the density is non-zero, this function automatically updates the mass of the body.
        ///  Contacts are not created until the next time step.
        /// </summary>
        /// <param name="def">the fixture definition.</param>
        /// <warning>This function is locked during callbacks.</warning>
        public b2Fixture CreateFixture(in b2FixtureDef def)
        {
            m_world.StaticChanged(this);
            //Debug.Assert(_world.IsLocked() == false);
            if (m_world.IsLocked())
            {
                throw new
                    Box2DException("Cannot create fixtures in the middle of Step. Has this been spawned from an event such as a b2ContactListener callback?");
            }

            var fixture = new b2Fixture();
            fixture.Create(this, def);

            if (HasFlag(b2BodyFlags.Enabled))
            {
                b2BroadPhase broadPhase = m_world.m_contactManager.m_broadPhase;
                fixture.CreateProxies(broadPhase, m_xf);
            }

            fixture.m_next = m_fixtureList;
            m_fixtureList = fixture;
            ++m_fixtureCount;

            fixture.m_body = this;

            // Adjust mass properties if needed.
            if (fixture.m_density > 0.0f)
            {
                ResetMassData();
            }

            // Let the world know we have a new fixture. This will cause new contacts
            // to be created at the beginning of the next time step.
            m_world.m_newContacts = true;

            return fixture;
        }

        /// <summary>
        ///  Creates a fixture from a shape and attach it to this body.
        ///  This is a convenience function. Use b2FixtureDef if you need to set parameters
        ///  like friction, restitution, user data, or filtering.
        ///  If the density is non-zero, this function automatically updates the mass of the body.
        /// </summary>
        /// <param name="shape">the shape to be cloned.</param>
        /// <param name="density">the shape density (set to zero for static bodies).</param>
        /// <warning>This function is locked during callbacks.</warning>
        public b2Fixture CreateFixture(in b2Shape shape, float density = 0f)
        {
            var def = new b2FixtureDef();
            def.shape = shape;
            def.density = density;

            return CreateFixture(def);
        }

        public void DestroyFixture(b2Fixture fixture)
        {
            m_world.StaticChanged(this);
            if (fixture == null)
            {
                return;
            }

            //Debug.Assert(_world.IsLocked() == false);
            if (m_world.IsLocked())
            {
                throw new
                    Box2DException("Cannot destroy fixtures in the middle of Step. Has this been spawned from an event such as a b2ContactListener callback?");
            }

            //Debug.Assert(fixture.m_body == this);

            // Remove the fixture from this body's singly linked list.
            //Debug.Assert(_fixtureCount > 0);
            b2Fixture node = m_fixtureList;
            b2Fixture prevNode = null;
            bool found = false;
            while (node != null)
            {
                if (node == fixture)
                {
                    if (prevNode == null)
                        m_fixtureList = fixture.m_next;
                    else
                        prevNode.m_next = fixture.m_next;

                    found = true;
                    break;
                }

                prevNode = node;
                node = node.m_next;
            }

            // You tried to remove a shape that is not attached to this body.
            if (!found)
            {
                // C++ b2Body::DestroyFixture only asserts fixture->m_body == this and then
                // proceeds unconditionally, so stale/duplicate destroys must stay safe.
            }

            float density = fixture.m_density;

            // Destroy any contacts associated with the fixture.
            b2ContactEdge edge = m_contactList;
            while (edge != null)
            {
                b2Contact c = edge.contact;
                edge = edge.next;

                b2Fixture fixtureA = c.GetFixtureA();
                b2Fixture fixtureB = c.GetFixtureB();

                if (fixture == fixtureA || fixture == fixtureB)
                // This destroys the contact and removes it from
                // this body's contact list.
                {
                    m_world.m_contactManager.Destroy(c);
                }
            }

            if (fixture.m_proxyCount > 0)
            {
                b2BroadPhase broadPhase = m_world.m_contactManager.m_broadPhase;
                fixture.DestroyProxies(broadPhase);
            }

            fixture.m_body = null;
            fixture.m_next = null;

            --m_fixtureCount;

            if (density > 0.0f)
            {
                // Reset the mass data.
                ResetMassData();
            }
        }

        public void SetTransform(in b2Vec2 position, float angle)
        {
            m_world.StaticChanged(this);
            //Debug.Assert(_world.IsLocked() == false);
            if (m_world.IsLocked())
            {
                return;
            }

            m_xf.q = Matrex.CreateRotation(angle); //  Actually about twice as fast to use our own function
            m_xf.p = position;

            m_sweep.c = Math.Mul(m_xf, m_sweep.localCenter);
            m_sweep.a = angle;

            m_sweep.c0 = m_sweep.c;
            m_sweep.a0 = angle;

            b2BroadPhase broadPhase = m_world.m_contactManager.m_broadPhase;
            for (b2Fixture f = m_fixtureList; f != null; f = f.m_next)
            {
                f.Synchronize(broadPhase, m_xf, m_xf);
            }

            m_world.m_newContacts = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Transform GetTransform() => m_xf;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetPosition() => m_xf.p;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetAngle() => m_sweep.a;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetWorldCenter() => m_sweep.c;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetLocalCenter() => m_sweep.localCenter;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetLinearVelocity(in b2Vec2 v)
        {
            if (m_type == b2BodyType.Static)
            {
                return;
            }

            if (b2Vec2.Dot(v, v) > 0f)
            {
                SetAwake(true);
            }

            m_linearVelocity = v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetLinearVelocity() => m_linearVelocity;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAngularVelocity(float omega)
        {
            if (m_type == b2BodyType.Static)
            {
                return;
            }

            if (omega * omega > 0f)
            {
                SetAwake(true);
            }

            m_angularVelocity = omega;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetAngularVelocity() => m_angularVelocity;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ApplyForce(in b2Vec2 force, in b2Vec2 point, bool wake = true)
        {
            if (m_type != b2BodyType.Dynamic)
            {
                return;
            }

            if (wake && !HasFlag(b2BodyFlags.Awake))
            {
                SetAwake(true);
            }

            if (HasFlag(b2BodyFlags.Awake))
            {
                m_force += force;
                m_torque += Vectex.Cross(point - m_sweep.c, force);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ApplyForceToCenter(in b2Vec2 force, bool wake = true)
        {
            if (m_type != b2BodyType.Dynamic)
            {
                return;
            }

            if (wake && !HasFlag(b2BodyFlags.Awake))
            {
                SetAwake(true);
            }

            if (HasFlag(b2BodyFlags.Awake))
            {
                m_force += force;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ApplyTorque(float torque, bool wake = true)
        {
            if (m_type != b2BodyType.Dynamic)
            {
                return;
            }

            if (wake && !HasFlag(b2BodyFlags.Awake))
            {
                SetAwake(true);
            }

            if (HasFlag(b2BodyFlags.Awake))
            {
                m_torque += torque;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ApplyLinearImpulse(in b2Vec2 impulse, in b2Vec2 point, bool wake = true)
        {
            if (m_type != b2BodyType.Dynamic)
            {
                return;
            }

            if (wake && !HasFlag(b2BodyFlags.Awake))
            {
                SetAwake(true);
            }

            if (HasFlag(b2BodyFlags.Awake))
            {
                m_linearVelocity += m_invMass * impulse;
                m_torque += m_invI * Vectex.Cross(point - m_sweep.c, impulse);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ApplyLinearImpulseToCenter(in b2Vec2 impulse, bool wake = true)
        {
            if (m_type != b2BodyType.Dynamic)
            {
                return;
            }

            if (wake && !HasFlag(b2BodyFlags.Awake))
            {
                SetAwake(true);
            }

            if (HasFlag(b2BodyFlags.Awake))
            {
                m_linearVelocity += m_invMass * impulse;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ApplyAngularImpulse(float impulse, bool wake = true)
        {
            if (m_type != b2BodyType.Dynamic)
            {
                return;
            }

            if (wake && !HasFlag(b2BodyFlags.Awake))
            {
                SetAwake(true);
            }

            if (HasFlag(b2BodyFlags.Awake))
            {
                m_angularVelocity += m_invI * impulse;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetMass() => m_mass;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetInertia() => m_I + m_mass * b2Vec2.Dot(m_sweep.localCenter, m_sweep.localCenter);

        public void GetMassData(out b2MassData data)
        {
            data.mass = m_mass;
            data.I = m_I + m_mass * b2Vec2.Dot(m_sweep.localCenter, m_sweep.localCenter);
            data.center = m_sweep.localCenter;
        }

        public void SetMassData(in b2MassData massData)
        {
            //Debug.Assert(_world.IsLocked() == false);
            if (m_world.IsLocked())
            {
                return;
            }

            if (m_type != b2BodyType.Dynamic)
            {
                return;
            }

            m_invMass = 0.0f;
            m_I = 0.0f;
            m_invI = 0.0f;

            m_mass = massData.mass;
            if (m_mass <= 0.0f)
            {
                m_mass = 1.0f;
            }

            m_invMass = 1.0f / m_mass;

            if (massData.I > 0.0f && !HasFlag(b2BodyFlags.FixedRotation))
            {
                m_I = massData.I - m_mass * b2Vec2.Dot(massData.center, massData.center);
                //Debug.Assert(_I > 0.0f);
                m_invI = 1.0f / m_I;
            }

            // Move center of mass.
            b2Vec2 oldCenter = m_sweep.c;
            m_sweep.localCenter = massData.center;
            m_sweep.c0 = m_sweep.c = Math.Mul(m_xf, m_sweep.localCenter);

            // Update center of mass velocity.
            m_linearVelocity += Vectex.Cross(m_angularVelocity, m_sweep.c - oldCenter);
        }

        public void ResetMassData()
        {
            // Compute mass data from shapes. Each shape has its own density.
            m_mass = 0.0f;
            m_invMass = 0.0f;
            m_I = 0.0f;
            m_invI = 0.0f;
            m_sweep.localCenter = b2Vec2.Zero;

            // Static and kinematic bodies have zero mass.
            if (m_type == b2BodyType.Static || m_type == b2BodyType.Kinematic)
            {
                m_sweep.c0 = m_xf.p;
                m_sweep.c = m_xf.p;
                m_sweep.a0 = m_sweep.a;
                return;
            }

            //Debug.Assert(_type == b2BodyType.Dynamic);

            // Accumulate mass over all fixtures.
            b2Vec2 localCenter = b2Vec2.Zero;
            for (b2Fixture f = m_fixtureList; f != null; f = f.m_next)
            {
                if (f.m_density == 0.0f)
                {
                    continue;
                }

                f.GetMassData(out b2MassData massData);
                m_mass += massData.mass;
                localCenter += massData.mass * massData.center;
                m_I += massData.I;
            }

            // Compute center of mass.
            if (m_mass > 0.0f)
            {
                m_invMass = 1.0f / m_mass;
                localCenter *= m_invMass;
            }

            if (m_I > 0.0f && !HasFlag(b2BodyFlags.FixedRotation))
            {
                // Center the inertia about the center of mass.
                m_I -= m_mass * b2Vec2.Dot(localCenter, localCenter);
                //Debug.Assert(_I > 0.0f);
                m_invI = 1.0f / m_I;
            }
            else
            {
                m_I = 0.0f;
                m_invI = 0.0f;
            }

            // Move center of mass.
            b2Vec2 oldCenter = m_sweep.c;
            m_sweep.localCenter = localCenter;
            m_sweep.c0 = m_sweep.c = Math.Mul(m_xf, m_sweep.localCenter);

            // Update center of mass velocity.
            m_linearVelocity += Vectex.Cross(m_angularVelocity, m_sweep.c - oldCenter);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetWorldPoint(in b2Vec2 localPoint) => Math.Mul(m_xf, localPoint);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetWorldVector(in b2Vec2 localVector) =>
            b2Vec2.Transform(localVector, m_xf.q); //  Math.Mul(_xf.q, localVector);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetLocalPoint(in b2Vec2 worldPoint) => Math.MulT(m_xf, worldPoint);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetLocalVector(in b2Vec2 worldVector) => Math.MulT(m_xf.q, worldVector);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetLinearVelocityFromWorldPoint(in b2Vec2 worldPoint) =>
            m_linearVelocity + Vectex.Cross(m_angularVelocity, worldPoint - m_sweep.c);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetLinearVelocityFromLocalPoint(in b2Vec2 localPoint) =>
            GetLinearVelocityFromWorldPoint(GetWorldPoint(localPoint));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetLinearDamping() => m_linearDamping;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetLinearDamping(float linearDamping)
        {
            m_linearDamping = linearDamping;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetAngularDamping() => m_angularDamping;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAngularDamping(float angularDamping)
        {
            m_angularDamping = angularDamping;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetGravityScale() => m_gravityScale;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetGravityScale(float scale)
        {
            m_gravityScale = scale;
        }

        public void SetType(b2BodyType type)
        {
            m_world.StaticChanged(null);
            //Debug.Assert(_world.IsLocked() == false);
            if (m_world.IsLocked())
            {
                return;
            }

            if (m_type == type)
            {
                return;
            }

            m_type = type;

            ResetMassData();

            if (m_type == b2BodyType.Static)
            {
                m_linearVelocity = b2Vec2.Zero;
                m_angularVelocity = 0.0f;
                m_sweep.a0 = m_sweep.a;
                m_sweep.c0 = m_sweep.c;
                m_flags &= ~b2BodyFlags.Awake;
                SynchronizeFixtures();
            }

            SetAwake(true);

            m_force = b2Vec2.Zero;
            m_torque = 0.0f;

            // Delete the attached contacts.
            b2ContactEdge ce = m_contactList;
            while (ce != null)
            {
                b2ContactEdge ce0 = ce;
                ce = ce.next;
                m_world.m_contactManager.Destroy(ce0.contact);
            }

            m_contactList = null;

            // Touch the proxies so that new contacts will be created (when appropriate)
            b2BroadPhase broadPhase = m_world.m_contactManager.m_broadPhase;
            for (b2Fixture f = m_fixtureList; f != null; f = f.m_next)
            {
                int proxyCount = f.m_proxyCount;
                for (var i = 0; i < proxyCount; ++i)
                {
                    broadPhase.TouchProxy(f.m_proxies[i].proxyId);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2BodyType Type() => m_type;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFlag(b2BodyFlags flag)
        {
            m_flags |= flag;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsetFlag(b2BodyFlags flag)
        {
            m_flags &= ~flag;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBullet(bool flag)
        {
            if (flag)
            {
                SetFlag(b2BodyFlags.Bullet);
            }
            else
            {
                UnsetFlag(b2BodyFlags.Bullet);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsBullet() => HasFlag(b2BodyFlags.Bullet);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetSleepingAllowed(bool flag)
        {
            if (flag)
            {
                SetFlag(b2BodyFlags.AutoSleep);
            }
            else
            {
                UnsetFlag(b2BodyFlags.AutoSleep);
                SetAwake(true);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSleepingAllowed() => HasFlag(b2BodyFlags.AutoSleep);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAwake(bool flag)
        {
            if (m_type == b2BodyType.Static)
            {
                return;
            }

            if (flag)
            {
                SetFlag(b2BodyFlags.Awake);
                m_sleepTime = 0f;
            }
            else
            {
                UnsetFlag(b2BodyFlags.Awake);
                m_sleepTime = 0f;
                m_linearVelocity = b2Vec2.Zero;
                m_angularVelocity = 0f;
                m_force = b2Vec2.Zero;
                m_torque = 0f;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAwake() => HasFlag(b2BodyFlags.Awake);

        public void SetEnabled(bool flag)
        {
            m_world.StaticChanged(this);
            //Debug.Assert(_world.IsLocked() == false);

            if (flag == IsEnabled())
            {
                return;
            }

            if (flag)
            {
                SetFlag(b2BodyFlags.Enabled);

                // Create all proxies.
                b2BroadPhase broadPhase = m_world.m_contactManager.m_broadPhase;
                for (b2Fixture f = m_fixtureList; f != null; f = f.m_next)
                {
                    f.CreateProxies(broadPhase, m_xf);
                }

                // Contacts are created at the beginning of the next
                m_world.m_newContacts = true;
            }
            else
            {
                UnsetFlag(b2BodyFlags.Enabled);

                // Destroy all proxies.
                b2BroadPhase broadPhase = m_world.m_contactManager.m_broadPhase;
                for (b2Fixture f = m_fixtureList; f != null; f = f.m_next)
                {
                    f.DestroyProxies(broadPhase);
                }

                // Destroy the attached contacts.
                b2ContactEdge ce = m_contactList;
                while (ce != null)
                {
                    b2ContactEdge ce0 = ce;
                    ce = ce.next;
                    m_world.m_contactManager.Destroy(ce0.contact);
                }

                m_contactList = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEnabled() => HasFlag(b2BodyFlags.Enabled);

        public void SetFixedRotation(bool flag)
        {
            if (flag == HasFlag(b2BodyFlags.FixedRotation))
            {
                return;
            }

            if (flag)
            {
                SetFlag(b2BodyFlags.FixedRotation);
            }
            else
            {
                UnsetFlag(b2BodyFlags.FixedRotation);
            }

            m_angularVelocity = 0.0f;

            ResetMassData();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsFixedRotation() => HasFlag(b2BodyFlags.FixedRotation);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Fixture GetFixtureList() => m_fixtureList;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2JointEdge GetJointList() => m_jointList;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2ContactEdge GetContactList() => m_contactList;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Body GetNext() => m_next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetUserData<T>() => (T)UserData;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetUserData(object data)
        {
            UserData = data;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2World GetWorld() => m_world;

        public void Dump()
        {
            // Todo: Dump in some form. We could just serialize.
        }


        public void SynchronizeFixtures()
        {
            b2BroadPhase broadPhase = m_world.m_contactManager.m_broadPhase;

            if (IsAwake())
            {
                var xf1 = new b2Transform();
                xf1.q = Matrex.CreateRotation(m_sweep.a0); // Actually about twice as fast to use our own function
                xf1.p = m_sweep.c0 - b2Vec2.Transform(m_sweep.localCenter, xf1.q); //Math.Mul(xf1.q, _sweep.localCenter);

                for (b2Fixture f = m_fixtureList; f != null; f = f.m_next)
                {
                    f.Synchronize(broadPhase, xf1, m_xf);
                }
            }
            else
            {
                for (b2Fixture f = m_fixtureList; f != null; f = f.m_next)
                {
                    f.Synchronize(broadPhase, m_xf, m_xf);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SynchronizeTransform()
        {
            m_xf.q = Matrex.CreateRotation(m_sweep.a); // Actually about twice as fast to use our own function
            m_xf.p = m_sweep.c - b2Vec2.Transform(m_sweep.localCenter, m_xf.q); // Math.Mul(_xf.q, _sweep.localCenter);
        }

        public bool ShouldCollide(in b2Body other)
        {
            // At least one body should be dynamic.
            if (m_type != b2BodyType.Dynamic && other.m_type != b2BodyType.Dynamic)
            {
                return false;
            }

            // Does a joint prevent collision?
            for (b2JointEdge jn = m_jointList; jn != null; jn = jn.next)
            {
                if (jn.other == other && jn.joint.m_collideConnected == false)
                {
                    return false;
                }
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(float alpha)
        {
            // Advance to the new safe time. This doesn't sync the broad-phase.
            m_sweep.Advance(alpha);
            m_sweep.c = m_sweep.c0;
            m_sweep.a = m_sweep.a0;
            m_xf.q = Matrex.CreateRotation(m_sweep.a); // Actually about twice as fast to use our own function
            m_xf.p = m_sweep.c - b2Vec2.Transform(m_sweep.localCenter, m_xf.q); //Math.Mul(_xf.q, _sweep.localCenter);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasFlag(b2BodyFlags flag) => (m_flags & flag) == flag;


        // This is used to prevent connected bodies from colliding.
        // It may lie, depending on the collideConnected flag.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsConnected(b2Body other)
        {
            for (b2JointEdge jn = m_jointList; jn != null; jn = jn.next)
            {
                if (jn.other == other)
                {
                    return jn.joint.m_collideConnected == false;
                }
            }

            return false;
        }
    }
}