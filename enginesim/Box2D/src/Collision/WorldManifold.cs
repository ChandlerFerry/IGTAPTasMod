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
    /// <summary>
    ///  This is used to compute the current state of a contact manifold.
    /// </summary>
    public class b2WorldManifold
    {
        public float[] separations = new float[b2Settings.MaxManifoldPoints];

        /// <summary>
        ///  b2World vector pointing from A to B.
        /// </summary>
        public b2Vec2 normal;

        /// <summary>
        ///  b2World contact point (point of intersection).
        /// </summary>
        public b2Vec2[] points = new b2Vec2[b2Settings.MaxManifoldPoints];

        /// Evaluate the manifold with supplied transforms. This assumes
        /// modest motion from the original state. This does not change the
        /// point count, impulses, etc. The radii must come from the shapes
        /// that generated the manifold.
        public void Initialize(
            b2Manifold manifold,
            b2Transform xfA,
            float radiusA,
            b2Transform xfB,
            float radiusB)
        {
            if (manifold.pointCount == 0)
            {
                return;
            }

            switch (manifold.type)
            {
                case b2ManifoldType.Circles:
                    {
                        normal = new b2Vec2(1.0f, 0.0f);
                        b2Vec2 pointA = Math.Mul(xfA, manifold.localPoint);
                        b2Vec2 pointB = Math.Mul(xfB, manifold.points[0].localPoint);
                        if (b2Vec2.DistanceSquared(pointA, pointB) > b2Settings.FLT_EPSILON_SQUARED)
                        {
                            normal = b2Vec2.Normalize(pointB - pointA);
                        }

                        b2Vec2 cA = pointA + radiusA * normal;
                        b2Vec2 cB = pointB - radiusB * normal;
                        points[0] = 0.5f * (cA + cB);
                        separations[0] = b2Vec2.Dot(cB - cA, normal);
                    }
                    break;

                case b2ManifoldType.FaceA:
                    {
                        normal = b2Vec2.Transform(manifold.localNormal, xfA.q); // Math.Mul(xfA.q, manifold.localNormal);
                        b2Vec2 planePoint = Math.Mul(xfA, manifold.localPoint);

                        for (var i = 0; i < manifold.pointCount; ++i)
                        {
                            b2Vec2 clipPoint = Math.Mul(xfB, manifold.points[i].localPoint);
                            b2Vec2 cA = clipPoint + (radiusA - b2Vec2.Dot(clipPoint - planePoint, normal)) * normal;
                            b2Vec2 cB = clipPoint - radiusB * normal;
                            points[i] = 0.5f * (cA + cB);
                            separations[i] = b2Vec2.Dot(cB - cA, normal);
                        }
                    }
                    break;

                case b2ManifoldType.FaceB:
                    {
                        normal = b2Vec2.Transform(manifold.localNormal, xfB.q); // Math.Mul(xfB.q, manifold.localNormal);
                        b2Vec2 planePoint = Math.Mul(xfB, manifold.localPoint);

                        for (var i = 0; i < manifold.pointCount; ++i)
                        {
                            b2Vec2 clipPoint = Math.Mul(xfA, manifold.points[i].localPoint);
                            b2Vec2 cB = clipPoint + (radiusB - b2Vec2.Dot(clipPoint - planePoint, normal)) * normal;
                            b2Vec2 cA = clipPoint - radiusA * normal;

                            points[i] = 0.5f * (cA + cB);
                            separations[i] = b2Vec2.Dot(cA - cB, normal);
                        }

                        // Ensure normal points from A to B.
                        normal = -normal;
                    }
                    break;
            }
        }
    }
}