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
    public class b2PolyAndCircleContact : b2Contact
    {
        private readonly b2CircleShape circleB;
        private readonly b2PolygonShape polygonA;

        public b2PolyAndCircleContact(b2Fixture fA, int indexA, b2Fixture fB, int indexB) : base(fA, indexA, fB, indexB)
        {
            polygonA = (b2PolygonShape)m_fixtureA.b2Shape;
            circleB = (b2CircleShape)m_fixtureB.b2Shape;
        }

        public override void Evaluate(out b2Manifold manifold, in b2Transform xfA, in b2Transform xfB)
        {
            manifold = new b2Manifold();

            //manifold.pointCount = 0;

            // Compute circle position in the frame of the polygon.
            b2Vec2 c = Math.Mul(xfB, circleB.m_p);
            b2Vec2 cLocal = Math.MulT(xfA, c);

            // Find the min separating edge.
            var normalIndex = 0;
            float separation = float.MinValue;
            float radius = polygonA.m_radius + circleB.m_radius;
            int vertexCount = polygonA.m_count;
            b2Vec2[] vertices = polygonA.m_vertices;
            b2Vec2[] normals = polygonA.m_normals;

            for (var i = 0; i < vertexCount; ++i)
            {
                float s = b2Vec2.Dot(normals[i], cLocal - vertices[i]);
                if (s > radius)
                // Early out.
                {
                    return;
                }

                if (s > separation)
                {
                    separation = s;
                    normalIndex = i;
                }
            }

            // Vertices that subtend the incident face.
            int vertIndex1 = normalIndex;
            int vertIndex2 = vertIndex1 + 1 < vertexCount ? vertIndex1 + 1 : 0;
            b2Vec2 v1 = vertices[vertIndex1];
            b2Vec2 v2 = vertices[vertIndex2];
            manifold.points[0] = new b2ManifoldPoint();

            // If the center is inside the polygon ...
            if (separation < b2Settings.FLT_EPSILON)
            {
                manifold.pointCount = 1;
                manifold.type = b2ManifoldType.FaceA;
                manifold.localNormal = normals[normalIndex];
                manifold.localPoint = 0.5f * (v1 + v2);
                manifold.points[0].localPoint = circleB.m_p;
                manifold.points[0].id.key = 0;
                return;
            }

            // Compute barycentric coordinates
            float u1 = b2Vec2.Dot(cLocal - v1, v2 - v1);
            float u2 = b2Vec2.Dot(cLocal - v2, v1 - v2);
            if (u1 <= 0.0f)
            {
                if (b2Vec2.DistanceSquared(cLocal, v1) > radius * radius)
                {
                    return;
                }

                manifold.pointCount = 1;
                manifold.type = b2ManifoldType.FaceA;
                manifold.localNormal = b2Vec2.Normalize(cLocal - v1);
                manifold.localPoint = v1;
                manifold.points[0].localPoint = circleB.m_p;
                manifold.points[0].id.key = 0;
            }
            else if (u2 <= 0.0f)
            {
                if (b2Vec2.DistanceSquared(cLocal, v2) > radius * radius)
                {
                    return;
                }

                manifold.pointCount = 1;
                manifold.type = b2ManifoldType.FaceA;
                manifold.localNormal = b2Vec2.Normalize(cLocal - v2);
                manifold.localPoint = v2;
                manifold.points[0].localPoint = circleB.m_p;
                manifold.points[0].id.key = 0;
            }
            else
            {
                b2Vec2 faceCenter = 0.5f * (v1 + v2);
                float s = b2Vec2.Dot(cLocal - faceCenter, normals[vertIndex1]);
                if (s > radius)
                {
                    return;
                }

                manifold.pointCount = 1;
                manifold.type = b2ManifoldType.FaceA;
                manifold.localNormal = normals[vertIndex1];
                manifold.localPoint = faceCenter;
                manifold.points[0].localPoint = circleB.m_p;
                manifold.points[0].id.key = 0;
            }
        }
    }
}