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

using System.Numerics;

namespace Box2D
{
    public class b2CircleContact : b2Contact
    {
        private readonly b2CircleShape circleA;

        private readonly b2CircleShape circleB;

        public b2CircleContact(b2Fixture fA, int indexA, b2Fixture fB, int indexB) : base(fA, indexA, fB, indexB)
        {
            circleB = (b2CircleShape)m_fixtureB.b2Shape;
            circleA = (b2CircleShape)m_fixtureA.b2Shape;
        }

        public override void Evaluate(b2Manifold manifold, in b2Transform xfA, in b2Transform xfB)
        {
            b2Vec2 pA = Math.Mul(xfA, circleA.m_p);
            b2Vec2 pB = Math.Mul(xfB, circleB.m_p);

            b2Vec2 d = pB - pA;
            float distSqr = b2Vec2.Dot(d, d);
            float rA = circleA.m_radius, rB = circleB.m_radius;
            float radius = rA + rB;
            if (distSqr > radius * radius)
            {
                return;
            }

            manifold.type = b2ManifoldType.Circles;
            manifold.localPoint = circleA.m_p;
            manifold.localNormal = b2Vec2.Zero;
            manifold.pointCount = 1;

            manifold.NewPoint(0);
            manifold.points[0].localPoint = circleB.m_p;
            manifold.points[0].id.key = 0;
        }
    }
}