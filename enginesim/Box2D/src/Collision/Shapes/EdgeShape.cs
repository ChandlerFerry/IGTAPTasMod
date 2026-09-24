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
    ///  A line segment (edge) shape. These can be connected in chains or loops
    ///  to other edge shapes. Edges created independently are two-sided and do
    ///  no provide smooth movement across junctions.
    /// </summary>
    public class b2EdgeShape : b2Shape
    {
        public const byte contactMatch = 1;
        public bool m_oneSided;
        public b2Vec2? m_vertex0;
        public b2Vec2 m_vertex1;
        public b2Vec2 m_vertex2;
        public b2Vec2? m_vertex3;

        public b2EdgeShape() => m_radius = b2Settings.polygonRadius;

        public b2EdgeShape(b2Vec2 v1, b2Vec2 v2) : this()
        {
            SetTwoSided(v1, v2);
        }

        public b2Vec2 Vertex1
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_vertex1;
        }

        public b2Vec2 Vertex2
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_vertex2;
        }

        public override byte ContactMatch => contactMatch;

        /// <summary>
        ///  Set this as a part of a sequence. Vertex v0 precedes the edge and vertex v3
        ///  follows. These extra vertices are used to provide smooth movement
        ///  across junctions. This also makes the collision one-sided. The edge
        ///  normal points to the right looking from v1 to v2.
        /// </summary>
        public void SetOneSided(in b2Vec2 v0, in b2Vec2 v1, in b2Vec2 v2, in b2Vec2 v3)
        {
            m_vertex0 = v0;
            m_vertex1 = v1;
            m_vertex2 = v2;
            m_vertex3 = v3;
            m_oneSided = true;
        }

        public void SetTwoSided(in b2Vec2 v1, in b2Vec2 v2)
        {
            m_vertex1 = v1;
            m_vertex2 = v2;
            m_oneSided = false;
        }

        [Obsolete("Use SetTwoSided instead", true)]
        public void Set(in b2Vec2 v1, in b2Vec2 v2)
        {
            m_vertex1 = v1;
            m_vertex2 = v2;
        }

        public override b2Shape Clone() => (b2EdgeShape)MemberwiseClone();

        public override int GetChildCount() => 1;

        public override bool TestPoint(in b2Transform xf, in b2Vec2 p) => false;

        public override bool RayCast(
            out b2RayCastOutput output,
            in b2RayCastInput input,
            in b2Transform xf,
            int childIndex)
        {
            output = default;
            // Put the ray into the edge's frame of reference.
            b2Vec2 p1 = Math.MulT(xf.q, input.p1 - xf.p);
            b2Vec2 p2 = Math.MulT(xf.q, input.p2 - xf.p);
            b2Vec2 d = p2 - p1;

            b2Vec2 v1 = m_vertex1;
            b2Vec2 v2 = m_vertex2;
            b2Vec2 e = v2 - v1;

            // Normal points to the right, looking from v1 at v2
            var normal = b2Vec2.Normalize(new b2Vec2(e.Y, -e.X));

            // q = p1 + t * d
            // dot(normal, q - v1) = 0
            // dot(normal, p1 - v1) + t * dot(normal, d) = 0
            float numerator = b2Vec2.Dot(normal, v1 - p1);
            if (m_oneSided && numerator > 0.0f)
            {
                return false;
            }

            float denominator = b2Vec2.Dot(normal, d);

            if (denominator == 0.0f)
            {
                return false;
            }

            float t = numerator / denominator;
            if (t < 0.0f || input.maxFraction < t)
            {
                return false;
            }

            b2Vec2 q = p1 + t * d;

            // q = v1 + s * r
            // s = dot(q - v1, r) / dot(r, r)
            b2Vec2 r = v2 - v1;
            float rr = b2Vec2.Dot(r, r);
            if (rr == 0.0f)
            {
                return false;
            }

            float s = b2Vec2.Dot(q - v1, r) / rr;
            if (s < 0.0f || 1.0f < s)
            {
                return false;
            }

            output.fraction = t;
            if (numerator > 0.0f)
            {
                output.normal = -b2Vec2.Transform(normal, xf.q); //   Math.Mul(xf.q, normal);
            }
            else
            {
                output.normal = b2Vec2.Transform(normal, xf.q); //Math.Mul(xf.q, normal);
            }

            return true;
        }

        public override void ComputeAABB(out b2AABB aabb, in b2Transform xf, int childIndex)
        {
            b2Vec2 v1 = Math.Mul(xf, m_vertex1);
            b2Vec2 v2 = Math.Mul(xf, m_vertex2);

            var lower = b2Vec2.Min(v1, v2);
            var upper = b2Vec2.Max(v1, v2);

            var r = new b2Vec2(m_radius, m_radius);
            aabb.lowerBound = lower - r;
            aabb.upperBound = upper + r;
        }

        public override void ComputeMass(out b2MassData massData, float density)
        {
            massData.mass = 0.0f;
            massData.center = 0.5f * (m_vertex1 + m_vertex2);
            massData.I = 0.0f;
        }

        public void Set(b2Vec2 v0, b2Vec2 v1, b2Vec2 v2, b2Vec2 v3)
        {
            m_vertex0 = v0;
            m_vertex1 = v1;
            m_vertex2 = v2;
            m_vertex3 = v3;
        }
    }
}