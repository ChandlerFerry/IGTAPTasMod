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
    public class b2EdgeAndCircleContact : b2Contact
    {
        private readonly b2CircleShape circleB;

        protected b2EdgeShape edgeA;

        public b2EdgeAndCircleContact(b2Fixture fixtureA, int indexA, b2Fixture fixtureB, int indexB)
            : base(fixtureA, indexA, fixtureB, indexB)
        {
            //Debug.Assert(fixtureA.Type == ShapeType.Edge);
            //Debug.Assert(fixtureB.Type == ShapeType.Circle);
            m_manifold.pointCount = 0;
            m_manifold.points[0] = new b2ManifoldPoint();
            m_manifold.points[0].normalImpulse = 0.0f;
            m_manifold.points[0].tangentImpulse = 0.0f;

            edgeA = m_fixtureA.b2Shape is b2EdgeShape ? (b2EdgeShape)m_fixtureA.b2Shape : null;
            circleB = (b2CircleShape)m_fixtureB.b2Shape;
        }

        public override void Evaluate(out b2Manifold manifold, in b2Transform xfA, in b2Transform xfB)
        {
            manifold = new b2Manifold();

            //manifold.pointCount = 0;

            b2Vec2 Q = Math.MulT(xfA, Math.Mul(xfB, circleB.m_p));

            b2Vec2 A = edgeA.m_vertex1, B = edgeA.m_vertex2;
            b2Vec2 e = B - A;

            float u = b2Vec2.Dot(e, B - Q);
            float v = b2Vec2.Dot(e, Q - A);

            float radius = edgeA.m_radius + circleB.m_radius;

            b2ContactFeature cf;
            cf.indexB = 0;
            cf.typeB = (byte)b2ContactFeatureType.Vertex;


            // Region A
            if (v <= 0.0f)
            {
                b2Vec2 P = A;
                b2Vec2 d = Q - P;
                float dd = b2Vec2.Dot(d, d);
                if (dd > radius * radius)
                {
                    return;
                }

                // Is there an edge connected to A?
                if (edgeA.m_vertex0.HasValue)
                {
                    b2Vec2 A1 = edgeA.m_vertex0.Value;
                    b2Vec2 B1 = A;
                    b2Vec2 e1 = B1 - A1;
                    float u1 = b2Vec2.Dot(e1, B1 - Q);

                    // Is the circle in Region AB of the previous edge?
                    if (u1 > 0.0f)
                    {
                        return;
                    }
                }

                cf.indexA = 0;
                cf.typeA = (byte)b2ContactFeatureType.Vertex;
                manifold.pointCount = 1;
                manifold.type = b2ManifoldType.Circles;
                manifold.localNormal = b2Vec2.Zero;
                manifold.localPoint = P;
                manifold.points[0] = new b2ManifoldPoint();
                manifold.points[0].id.key = 0;
                manifold.points[0].id.cf = cf;
                manifold.points[0].localPoint = circleB.m_p;
                return;
            }

            // Region B
            if (u <= 0.0f)
            {
                b2Vec2 P = B;
                b2Vec2 d = Q - P;
                float dd = b2Vec2.Dot(d, d);
                if (dd > radius * radius)
                {
                    return;
                }

                // Is there an edge connected to B?
                if (edgeA.m_vertex3.HasValue)
                {
                    b2Vec2 B2 = edgeA.m_vertex3.Value;
                    b2Vec2 A2 = B;
                    b2Vec2 e2 = B2 - A2;
                    float v2 = b2Vec2.Dot(e2, Q - A2);

                    // Is the circle in Region AB of the next edge?
                    if (v2 > 0.0f)
                    {
                        return;
                    }
                }

                cf.indexA = 1;
                cf.typeA = (byte)b2ContactFeatureType.Vertex;
                manifold.pointCount = 1;
                manifold.type = b2ManifoldType.Circles;
                manifold.localNormal = b2Vec2.Zero;
                manifold.localPoint = P;
                manifold.points[0] = new b2ManifoldPoint();
                manifold.points[0].id.key = 0;
                manifold.points[0].id.cf = cf;
                manifold.points[0].localPoint = circleB.m_p;
                return;
            }

            {
                // Region AB
                float den = b2Vec2.Dot(e, e);
                //Debug.Assert(den > 0.0f);
                b2Vec2 P = 1.0f / den * (u * A + v * B);
                b2Vec2 d = Q - P;
                float dd = b2Vec2.Dot(d, d);
                if (dd > radius * radius)
                {
                    return;
                }

                var n = new b2Vec2(-e.Y, e.X);
                if (b2Vec2.Dot(n, Q - A) < 0.0f)
                {
                    n = new b2Vec2(-n.X, -n.Y);
                }

                n = b2Vec2.Normalize(n);

                cf.indexA = 0;
                cf.typeA = (byte)b2ContactFeatureType.Face;
                manifold.pointCount = 1;
                manifold.type = b2ManifoldType.FaceA;
                manifold.localNormal = n;
                manifold.localPoint = A;
                manifold.points[0] = new b2ManifoldPoint();
                manifold.points[0].id.key = 0;
                manifold.points[0].id.cf = cf;
                manifold.points[0].localPoint = circleB.m_p;
            }
        }
    }
}