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
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Box2D
{
    /// <summary>
    ///  A fixture is used to attach a shape to a body for collision detection. A fixture
    ///  inherits its transform from its parent. Fixtures hold additional non-geometric data
    ///  such as friction, collision filters, etc.
    ///  Fixtures are created via b2Body.CreateFixture.
    ///  @warning you cannot reuse fixtures.
    /// </summary>
    [DebuggerDisplay("b2Fixture of {m_body.UserData}")]
    public class b2Fixture
    {
        public b2Body m_body;

        public float m_density;
        public b2Filter m_filter;
        public float m_friction;

        public b2Fixture m_next;
        public FixtureProxy[] m_proxies;
        public int m_proxyCount;
        public float m_restitution;

        // non-public default constructor
        public b2Fixture()
        {
            UserData = null;
            m_proxyCount = 0;
            m_density = 0f;
        }

        public b2Shape b2Shape
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            private set;
        }

        internal bool Sensor
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            set;
        }

        public b2Body b2Body
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_body;
        }

        public b2Fixture Next
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_next;
        }

        public float Restitution
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_restitution;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => m_restitution = value;
        }

        public object UserData
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            set;
        }

        public void Create(b2Body body, b2FixtureDef def)
        {
            if (def.shape == null)
            {
                throw new ArgumentNullException("def.shape");
            }

            UserData = def.userData;
            m_friction = def.friction;
            m_restitution = def.restitution;

            m_body = body;
            m_next = null;

            m_filter = def.filter;

            Sensor = def.isSensor;

            b2Shape = def.shape.Clone();

            // Reserve proxy space
            int childCount = b2Shape.GetChildCount();
            m_proxies = new FixtureProxy[childCount];
            for (var i = 0; i < childCount; ++i)
            {
                m_proxies[i] = new FixtureProxy();
            }

            m_proxyCount = 0;

            m_density = def.density;
        }

        public void CreateProxies(b2BroadPhase broadPhase, in b2Transform xf)
        {
            //Debug.Assert(m_proxyCount == 0);

            // Create proxies in the broad-phase.
            m_proxyCount = b2Shape.GetChildCount();

            for (var i = 0; i < m_proxyCount; ++i)
            {
                FixtureProxy proxy = m_proxies[i];
                b2Shape.ComputeAABB(out proxy.aabb, in xf, i);
                proxy.proxyId = broadPhase.CreateProxy(proxy.aabb, proxy);
                proxy.fixture = this;
                proxy.childIndex = i;
            }
        }

        public void DestroyProxies(b2BroadPhase broadPhase)
        {
            // Destroy proxies in the broad-phase.
            for (var i = 0; i < m_proxyCount; ++i)
            {
                FixtureProxy proxy = m_proxies[i];
                broadPhase.DestroyProxy(proxy.proxyId);
                proxy.proxyId = -1;
            }

            m_proxyCount = 0;
        }

        public void Synchronize(b2BroadPhase broadPhase, in b2Transform transform1, in b2Transform transform2)
        {
            if (m_proxyCount == 0)
            {
                return;
            }

            for (var i = 0; i < m_proxyCount; ++i)
            {
                FixtureProxy proxy = m_proxies[i];

                // Compute an b2AABB that covers the swept shape (may miss some rotation effect).
                b2Shape.ComputeAABB(out b2AABB aabb1, in transform1, proxy.childIndex);
                b2Shape.ComputeAABB(out b2AABB aabb2, in transform2, proxy.childIndex);

                proxy.aabb = b2AABB.Combine(aabb1, aabb2);

                b2Vec2 displacement = aabb2.GetCenter() - aabb1.GetCenter();

                broadPhase.MoveProxy(proxy.proxyId, proxy.aabb, displacement);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSensor() => Sensor;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Body GetBody() => b2Body;

        public b2Fixture GetNext() => m_next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Shape GetShape() => this.b2Shape;

        public bool TestPoint(in b2Vec2 p) => b2Shape.TestPoint(m_body.GetTransform(), p);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool RayCast(out b2RayCastOutput output, in b2RayCastInput input, int childIndex) =>
            b2Shape.RayCast(out output, in input, m_body.GetTransform(), childIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetMassData(out b2MassData massData)
        {
            b2Shape.ComputeMass(out massData, m_density);
        }

    }
}