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

namespace Box2D
{
    /// <summary>
    ///  A circle shape.
    /// </summary>
    public class b2CircleShape : b2Shape
    {
        public const byte contactMatch = 0;
        public b2Vec2 m_p;

        public b2CircleShape()
        {
            m_radius = 0;
            m_p = b2Vec2.Zero;
        }

        public b2Vec2 Center
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_p;
            set => m_p = value;
        }

        public float Radius
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_radius;
            set => m_radius = value;
        }

        public override byte ContactMatch => contactMatch;

        public override b2Shape Clone() => (b2CircleShape)MemberwiseClone();

        public override int GetChildCount() => 1;

        public override bool TestPoint(in b2Transform transform, in b2Vec2 p)
        {
            b2Vec2 center = transform.p + b2Vec2.Transform(m_p, transform.q); //   Math.Mul(transform.q, m_p);
            b2Vec2 d = p - center;
            return b2Vec2.Dot(d, d) <= m_radius * m_radius;
        }

        public override bool RayCast(
            out b2RayCastOutput output,
            in b2RayCastInput input,
            in b2Transform transform,
            int childIndex)
        {
            output = default;

            b2Vec2 position = transform.p + b2Vec2.Transform(m_p, transform.q); // Math.Mul(transform.q, m_p);
            b2Vec2 s = input.p1 - position;
            float b = b2Vec2.Dot(s, s) - m_radius * m_radius;

            // Solve quadratic equation.
            b2Vec2 r = input.p2 - input.p1;
            float c = b2Vec2.Dot(s, r);
            float rr = b2Vec2.Dot(r, r);
            float sigma = c * c - rr * b;

            // Check for negative discriminant and short segment.
            if (sigma < 0.0f || rr < b2Settings.FLT_EPSILON)
            {
                return false;
            }

            // Find the point of intersection of the line with the circle.
            float a = -(c + MathF.Sqrt(sigma));

            // Is the intersection point on the segment?
            if (0.0f <= a && a <= input.maxFraction * rr)
            {
                a /= rr;
                output.fraction = a;
                output.normal = b2Vec2.Normalize(s + a * r);
                return true;
            }

            return false;
        }

        public override void ComputeAABB(out b2AABB aabb, in b2Transform transform, int childIndex)
        {
            b2Vec2 p = transform.p + b2Vec2.Transform(m_p, transform.q); // Math.Mul(transform.q, m_p);
            aabb.lowerBound = new b2Vec2(p.X - m_radius, p.Y - m_radius);
            aabb.upperBound = new b2Vec2(p.X + m_radius, p.Y + m_radius);
        }

        public override void ComputeMass(out b2MassData massData, float density)
        {
            massData.mass = density * b2Settings.Pi * m_radius * m_radius;
            massData.center = m_p;

            // inertia about the local origin
            massData.I = massData.mass * (0.5f * m_radius * m_radius + b2Vec2.Dot(m_p, m_p));
        }

        public void Set(in b2Vec2 center, in float radius)
        {
            m_p = center;
            m_radius = radius;
        }
    }
}