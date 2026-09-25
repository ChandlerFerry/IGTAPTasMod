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
using Math = Box2D.Math;

namespace Box2D
{
    /// <summary>
    ///  The class manages contact between two shapes. A contact exists for each overlapping
    ///  b2AABB in the broad-phase (except if filtered). Therefore a contact object may exist
    ///  that has no contact points.
    /// </summary>
    public abstract class b2Contact
    {
        private const byte CircleCircle = (b2CircleShape.contactMatch << 2) + b2CircleShape.contactMatch;
        private const byte CircleEdge = (b2CircleShape.contactMatch << 2) + b2EdgeShape.contactMatch;
        private const byte CirclePolygon = (b2CircleShape.contactMatch << 2) + b2PolygonShape.contactMatch;
        private const byte CircleChain = (b2CircleShape.contactMatch << 2) + b2ChainShape.contactMatch;
        private const byte EdgeCircle = (b2EdgeShape.contactMatch << 2) + b2CircleShape.contactMatch;
        private const byte EdgeEdge = (b2EdgeShape.contactMatch << 2) + b2EdgeShape.contactMatch;
        private const byte EdgePolygon = (b2EdgeShape.contactMatch << 2) + b2PolygonShape.contactMatch;
        private const byte EdgeChain = (b2EdgeShape.contactMatch << 2) + b2ChainShape.contactMatch;
        private const byte PolygonCircle = (b2PolygonShape.contactMatch << 2) + b2CircleShape.contactMatch;
        private const byte PolygonEdge = (b2PolygonShape.contactMatch << 2) + b2EdgeShape.contactMatch;
        private const byte PolygonPolygon = (b2PolygonShape.contactMatch << 2) + b2PolygonShape.contactMatch;
        private const byte PolygonChain = (b2PolygonShape.contactMatch << 2) + b2ChainShape.contactMatch;
        private const byte ChainCircle = (b2ChainShape.contactMatch << 2) + b2CircleShape.contactMatch;
        private const byte ChainEdge = (b2ChainShape.contactMatch << 2) + b2EdgeShape.contactMatch;
        private const byte ChainPolygon = (b2ChainShape.contactMatch << 2) + b2PolygonShape.contactMatch;
        private const byte ChainChain = (b2ChainShape.contactMatch << 2) + b2ChainShape.contactMatch;

        public readonly b2Fixture m_fixtureA;
        public readonly b2Fixture m_fixtureB;

        internal readonly int m_indexA;
        internal readonly int m_indexB;

        // Nodes for connecting bodies.
        public readonly b2ContactEdge m_nodeA;
        public readonly b2ContactEdge m_nodeB;

        public b2CollisionFlags m_flags;

        public float m_friction;

        public b2Manifold m_manifold = new b2Manifold();
        public b2Contact m_next;

        // b2World pool and list pointers.
        public b2Contact m_prev;
        public float m_restitution;

        public float m_tangentSpeed;
        public float m_toi;

        public int m_toiCount;

        // This contact's place among its fixtures' pair's touching contacts (fixture UserData pairs, a Unity collider
        // pair). Unity keeps those in a list and reports them in its order: a contact that starts touching goes to the
        // end (b2World.NextTouchOrder), one that stops is replaced by the last (LeaveTouchOrder). Verified against every
        // multi-contact pair in the turbo trace corpus (a corner of four shapes: diverge-c5-13.14 ticks 881-885).
        public long m_touchOrder;

        // Swap-remove from the pair's list: the pair's last touching contact takes this one's place.
        internal void LeaveTouchOrder()
        {
            b2Body body = m_fixtureA.b2Body.Type() == b2BodyType.Static ? m_fixtureB.b2Body : m_fixtureA.b2Body;
            object userA = m_fixtureA.UserData, userB = m_fixtureB.UserData;
            b2Contact last = null;
            for (b2ContactEdge e = body.m_contactList; e != null; e = e.next)
            {
                b2Contact other = e.contact;
                if (other == this || (other.m_flags & b2CollisionFlags.Touching) == 0 || other.m_touchOrder < m_touchOrder) continue;
                object a = other.m_fixtureA.UserData, b = other.m_fixtureB.UserData;
                if (!(a == userA && b == userB || a == userB && b == userA)) continue;
                if (last == null || other.m_touchOrder > last.m_touchOrder) last = other;
            }

            if (last != null) last.m_touchOrder = m_touchOrder;
        }

        public b2Contact(b2Fixture fA, int indexA, b2Fixture fB, int indexB)
        {
            m_flags = b2CollisionFlags.Enabled;

            m_fixtureA = fA;
            m_fixtureB = fB;

            m_indexA = indexA;
            m_indexB = indexB;

            m_manifold.pointCount = 0;

            m_prev = null;
            m_next = null;

            m_nodeA = new b2ContactEdge();
            m_nodeB = new b2ContactEdge();

            m_toiCount = 0;

            m_friction = b2Settings.MixFriction(m_fixtureA.m_friction, m_fixtureB.m_friction);
            m_restitution = b2Settings.MixRestitution(m_fixtureA.Restitution, m_fixtureB.Restitution);

            m_tangentSpeed = 0f;
        }

        public b2Manifold b2Manifold
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_manifold;
        }

        public bool Enabled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IsEnabled();
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => SetEnabled(value);
        }

        public bool Touching
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IsTouching();
        }

        public b2Contact Next
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => GetNext();
        }

        /// <summary>
        ///  Get the first fixture in this contact.
        /// </summary>
        public b2Fixture FixtureA
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_fixtureA;
        }

        /// <summary>
        ///  Get the second fixture in this contact.
        /// </summary>
        public b2Fixture FixtureB
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_fixtureB;
        }

        public float Friction
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => GetFriction();
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => SetFriction(value);
        }

        public float Restitution
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => GetRestitution();
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => SetRestitution(value);
        }

        public float TangentSpeed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => GetTangentSpeed();
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => SetTangentSpeed(value);
        }

        public int ChildIndexA
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => GetChildIndexA();
        }

        public int ChildIndexB
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => GetChildIndexB();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Manifold GetManifold() => m_manifold;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetWorldManifold(out b2WorldManifold worldManifold)
        {
            b2Body bodyA = m_fixtureA.b2Body;
            b2Body bodyB = m_fixtureB.b2Body;
            b2Shape shapeA = m_fixtureA.b2Shape;
            b2Shape shapeB = m_fixtureB.b2Shape;

            worldManifold = new b2WorldManifold();
            worldManifold.Initialize(m_manifold, bodyA.GetTransform(), shapeA.m_radius, bodyB.GetTransform(),
                                     shapeB.m_radius);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetEnabled(bool flag)
        {
            if (flag)
            {
                m_flags |= b2CollisionFlags.Enabled;
            }
            else
            {
                m_flags &= ~b2CollisionFlags.Enabled;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEnabled() => (m_flags & b2CollisionFlags.Enabled) == b2CollisionFlags.Enabled;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsTouching() => (m_flags & b2CollisionFlags.Touching) == b2CollisionFlags.Touching;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Contact GetNext() => m_next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Fixture GetFixtureA() => FixtureA;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Fixture GetFixtureB() => FixtureB;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FlagForFiltering()
        {
            m_flags |= b2CollisionFlags.b2Filter;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetFriction() => m_friction;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFriction(float friction)
        {
            m_friction = friction;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetFriction()
        {
            m_friction = b2Settings.MixFriction(m_fixtureA.m_friction, m_fixtureB.m_friction);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRestitution(float restitution)
        {
            m_restitution = restitution;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetRestitution() => m_restitution;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetRestitution()
        {
            m_restitution = b2Settings.MixRestitution(m_fixtureA.m_restitution, m_fixtureB.m_restitution);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTangentSpeed(float speed)
        {
            m_tangentSpeed = speed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetTangentSpeed() => m_tangentSpeed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetChildIndexA() => m_indexA;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetChildIndexB() => m_indexB;

        public abstract void Evaluate(out b2Manifold manifold, in b2Transform xfA, in b2Transform xfB);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Contact Create(b2Fixture fixtureA, int indexA, b2Fixture fixtureB, int indexB)
        {
            int match = (fixtureA.b2Shape.ContactMatch << 2) + fixtureB.b2Shape.ContactMatch;
            return (match switch
            {
                CircleCircle => new b2CircleContact(fixtureA, indexA, fixtureB, indexB),
                CircleEdge => new b2EdgeAndCircleContact(fixtureB, indexB, fixtureA, indexA),
                CirclePolygon => new b2PolyAndCircleContact(fixtureB, indexB, fixtureA, indexA),
                CircleChain => new b2ChainAndCircleContact(fixtureB, indexB, fixtureA, indexA),
                EdgeCircle => new b2EdgeAndCircleContact(fixtureA, indexA, fixtureB, indexB),
                EdgeEdge => null,
                EdgePolygon => new b2EdgeAndPolygonContact(fixtureA, indexA, fixtureB, indexB),
                EdgeChain => null,
                PolygonCircle => new b2PolyAndCircleContact(fixtureA, indexA, fixtureB, indexB),
                PolygonEdge => new b2EdgeAndPolygonContact(fixtureB, indexB, fixtureA, indexA),
                PolygonPolygon => new b2PolygonContact(fixtureA, indexA, fixtureB, indexB),
                PolygonChain => new b2ChainAndPolygonContact(fixtureB, indexB, fixtureA, indexA),
                ChainCircle => new b2ChainAndCircleContact(fixtureA, indexA, fixtureB, indexB),
                ChainEdge => null,
                ChainPolygon => new b2ChainAndPolygonContact(fixtureA, indexA, fixtureB, indexB),
                ChainChain => null,
                _ => null
            })!;
        }

        public void Update(b2ContactListener listener)
        {
            b2Manifold oldManifold = m_manifold;

            // Re-enable this contact.
            m_flags |= b2CollisionFlags.Enabled;

            bool touching;
            bool wasTouching = (m_flags & b2CollisionFlags.Touching) != 0;

            bool sensorA = m_fixtureA.IsSensor();
            bool sensorB = m_fixtureB.IsSensor();
            bool sensor = sensorA || sensorB;

            b2Body bodyA = m_fixtureA.b2Body;
            b2Body bodyB = m_fixtureB.b2Body;
            b2Transform xfA = bodyA.b2Transform;
            b2Transform xfB = bodyB.b2Transform;

            // Is this contact a sensor?
            if (sensor)
            {
                b2Shape shapeA = m_fixtureA.b2Shape;
                b2Shape shapeB = m_fixtureB.b2Shape;
                touching = TestOverlap(shapeA, m_indexA, shapeB, m_indexB, xfA, xfB);

                // Sensors don't generate manifolds.
                m_manifold.pointCount = 0;
            }
            else
            {
                Evaluate(out m_manifold, xfA, xfB);
                touching = m_manifold.pointCount > 0;

                // Match old contact ids to new contact ids and copy the
                // stored impulses to warm start the solver.
                for (var i = 0; i < m_manifold.pointCount; ++i)
                {
                    b2ManifoldPoint mp2 = m_manifold.points[i];
                    mp2.normalImpulse = 0.0f;
                    mp2.tangentImpulse = 0.0f;
                    b2ContactID id2 = mp2.id;

                    for (var j = 0; j < oldManifold.pointCount; ++j)
                    {
                        b2ManifoldPoint mp1 = oldManifold.points[j];

                        if (mp1.id.key == id2.key)
                        {
                            mp2.normalImpulse = mp1.normalImpulse;
                            mp2.tangentImpulse = mp1.tangentImpulse;
                            break;
                        }
                    }
                }

                if (touching != wasTouching)
                {
                    bodyA.SetAwake(true);
                    bodyB.SetAwake(true);
                }
            }

            if (touching)
            {
                m_flags |= b2CollisionFlags.Touching;
                if (!wasTouching) m_touchOrder = bodyA.GetWorld().NextTouchOrder();
            }
            else
            {
                m_flags &= ~b2CollisionFlags.Touching;
                if (wasTouching) LeaveTouchOrder();
            }

            if (listener != null)
            {
                if (touching && !wasTouching)
                {
                    listener.BeginContact(this);
                }

                if (!touching && wasTouching)
                {
                    listener.EndContact(this);
                }

                if (touching && !sensor)
                {
                    listener.PreSolve(this, oldManifold);
                }
            }
        }

        private bool TestOverlap(in b2Shape shapeA, int indexA,
            in b2Shape shapeB, int indexB,
            in b2Transform xfA, in b2Transform xfB)
        {
            var input = new b2DistanceInput();
            input.proxyA.Set(shapeA, indexA);
            input.proxyB.Set(shapeB, indexB);
            input.transformA = xfA;
            input.transformB = xfB;
            input.useRadii = true;

            var cache = new b2SimplexCache();
            cache.count = 0;

            Distance(out b2DistanceOutput output, ref cache, in input);

            return output.distance < 10.0f * b2Settings.FLT_EPSILON;
        }

        public static void Distance(out b2DistanceOutput output, ref b2SimplexCache cache, in b2DistanceInput input)
        {
            output = new b2DistanceOutput();

            b2DistanceProxy proxyA = input.proxyA;
            b2DistanceProxy proxyB = input.proxyB;

            b2Transform transformA = input.transformA;
            b2Transform transformB = input.transformB;

            // Initialize the simplex.
            var simplex = new b2Simplex();
            simplex.ReadCache(cache, proxyA, transformA, proxyB, transformB);

            // Get simplex vertices as an array.
            ref b2Array3<b2SimplexVertex> vertices = ref simplex.m_v;
            const int k_maxIters = 20;

            // These store the vertices of the last simplex so that we
            // can check for duplicates and prevent cycling.
            Span<int> saveA = stackalloc int[3], saveB = stackalloc int[3];
            var saveCount = 0;

            // Main iteration loop.
            var iter = 0;
            while (iter < k_maxIters)
            {
                // Copy simplex so we can identify duplicates.
                saveCount = simplex.m_count;
                for (var i = 0; i < saveCount; ++i)
                {
                    saveA[i] = vertices[i].indexA;
                    saveB[i] = vertices[i].indexB;
                }

                switch (simplex.m_count)
                {
                    case 1:
                        break;

                    case 2:
                        simplex.Solve2();
                        break;

                    case 3:
                        simplex.Solve3();
                        break;
                }

                // If we have 3 points, then the origin is in the corresponding triangle.
                if (simplex.m_count == 3)
                {
                    break;
                }

                // Get search direction.
                b2Vec2 d = simplex.GetSearchDirection();

                // Ensure the search direction is numerically fit.
                if (d.LengthSquared() < b2Settings.FLT_EPSILON_SQUARED)
                // The origin is probably contained by a line segment
                // or triangle. Thus the shapes are overlapped.

                // We can't return zero here even though there may be overlap.
                // In case the simplex is a point, segment, or triangle it is difficult
                // to determine if the origin is contained in the CSO or very close to it.
                {
                    break;
                }

                // Compute a tentative new simplex vertex using support points.
                ref b2SimplexVertex vertex = ref vertices[simplex.m_count];
                vertex.indexA = proxyA.GetSupport(Math.MulT(transformA.q, -d));
                vertex.wA = Math.Mul(transformA, proxyA.GetVertex(vertex.indexA));
                vertex.indexB = proxyB.GetSupport(Math.MulT(transformB.q, d));
                vertex.wB = Math.Mul(transformB, proxyB.GetVertex(vertex.indexB));
                vertex.w = vertex.wB - vertex.wA;

                // Iteration count is equated to the number of support point calls.
                ++iter;
                //++b2_gjkIters;

                // Check for duplicate support points. This is the main termination criteria.
                var duplicate = false;
                for (var i = 0; i < saveCount; ++i)
                {
                    if (vertex.indexA == saveA[i] && vertex.indexB == saveB[i])
                    {
                        duplicate = true;
                        break;
                    }
                }

                // If we found a duplicate support point we must exit to avoid cycling.
                if (duplicate)
                {
                    break;
                }

                // New vertex is ok and needed.
                ++simplex.m_count;
            }

            //_gjkMaxIters = b2Max(b2_gjkMaxIters, iter);

            // Prepare output.
            simplex.GetWitnessPoints(out output.pointA, out output.pointB);
            output.distance = b2Vec2.Distance(output.pointA, output.pointB);
            output.iterations = iter;

            // Cache the simplex.
            simplex.WriteCache(ref cache);

            // Apply radii if requested.
            if (input.useRadii)
            {
                float rA = proxyA._radius;
                float rB = proxyB._radius;

                if (output.distance > rA + rB && output.distance > b2Settings.FLT_EPSILON)
                {
                    // Shapes are still no overlapped.
                    // Move the witness points to the outer surface.
                    output.distance -= rA + rB;
                    b2Vec2 normal = output.pointB - output.pointA;
                    normal = b2Vec2.Normalize(normal);
                    output.pointA += rA * normal;
                    output.pointB -= rB * normal;
                }
                else
                {
                    // Shapes are overlapped when radii are considered.
                    // Move the witness points to the middle.
                    b2Vec2 p = 0.5f * (output.pointA + output.pointB);
                    output.pointA = p;
                    output.pointB = p;
                    output.distance = 0.0f;
                }
            }
        }

        public bool ShapeCast(out b2ShapeCastOutput output, in b2ShapeCastInput input)
        {
            output.iterations = 0;
            output.lambda = 1.0f;
            output.normal = b2Vec2.Zero;
            output.point = b2Vec2.Zero;

            b2DistanceProxy proxyA = input.proxyA;
            b2DistanceProxy proxyB = input.proxyB;

            float radiusA = MathF.Max(proxyA._radius, b2Settings.polygonRadius);
            float radiusB = MathF.Max(proxyB._radius, b2Settings.polygonRadius);
            float radius = radiusA + radiusB;

            b2Transform xfA = input.transformA;
            b2Transform xfB = input.transformB;

            b2Vec2 r = input.translationB;
            b2Vec2 n = b2Vec2.Zero;
            var lambda = 0.0f;

            // Initial simplex
            var simplex = new b2Simplex();
            simplex.m_count = 0;

            // Get simplex vertices as an array.
            ref b2Array3<b2SimplexVertex> vertices = ref simplex.m_v;

            // Get support point in -r direction
            int indexA = proxyA.GetSupport(Math.MulT(xfA.q, -r));
            b2Vec2 wA = Math.Mul(xfA, proxyA.GetVertex(indexA));
            int indexB = proxyB.GetSupport(Math.MulT(xfB.q, r));
            b2Vec2 wB = Math.Mul(xfB, proxyB.GetVertex(indexB));
            b2Vec2 v = wA - wB;

            // Sigma is the target distance between polygons
            float sigma = MathF.Max(b2Settings.polygonRadius, radius - b2Settings.polygonRadius);
            float tolerance = 0.5f * b2Settings.linearSlop;

            // Main iteration loop.
            const int k_maxIters = 20;
            var iter = 0;
            while (iter < k_maxIters && MathF.Abs(v.Length() - sigma) > tolerance)
            {
                Debug.Assert(simplex.m_count < 3);

                output.iterations += 1;

                // Support in direction -v (A - B)
                indexA = proxyA.GetSupport(Math.MulT(xfA.q, -v));
                wA = Math.Mul(xfA, proxyA.GetVertex(indexA));
                indexB = proxyB.GetSupport(Math.MulT(xfB.q, v));
                wB = Math.Mul(xfB, proxyB.GetVertex(indexB));
                b2Vec2 p = wA - wB;

                // -v is a normal at p
                v = b2Vec2.Normalize(v);

                // Intersect ray with plane
                float vp = b2Vec2.Dot(v, p);
                float vr = b2Vec2.Dot(v, r);
                if (vp - sigma > lambda * vr)
                {
                    if (vr <= 0.0f)
                    {
                        return false;
                    }

                    lambda = (vp - sigma) / vr;
                    if (lambda > 1.0f)
                    {
                        return false;
                    }

                    n = -v;
                    simplex.m_count = 0;
                }

                // Reverse simplex since it works with B - A.
                // Shift by lambda * r because we want the closest point to the current clip point.
                // Note that the support point p is not shifted because we want the plane equation
                // to be formed in unshifted space.
                b2SimplexVertex vertex = vertices[simplex.m_count];
                vertex.indexA = indexB;
                vertex.wA = wB + lambda * r;
                vertex.indexB = indexA;
                vertex.wB = wA;
                vertex.w = vertex.wB - vertex.wA;
                vertex.a = 1.0f;
                simplex.m_count += 1;

                switch (simplex.m_count)
                {
                    case 1:
                        break;

                    case 2:
                        simplex.Solve2();
                        break;

                    case 3:
                        simplex.Solve3();
                        break;

                    default:
                        Debug.Assert(false);
                        break;
                }

                // If we have 3 points, then the origin is in the corresponding triangle.
                if (simplex.m_count == 3)
                // Overlap
                {
                    return false;
                }

                // Get search direction.
                v = simplex.GetClosestPoint();

                // Iteration count is equated to the number of support point calls.
                ++iter;
            }

            // Prepare output.
            simplex.GetWitnessPoints(out b2Vec2 pointB, out b2Vec2 pointA);

            if (v.LengthSquared() > 0.0f)
            {
                n = -v;
                n = b2Vec2.Normalize(n);
            }

            output.point = pointA + radiusA * n;
            output.normal = n;
            output.lambda = lambda;
            output.iterations = iter;
            return true;
        }
    }
}