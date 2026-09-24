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
    public struct b2SeparationFunction
    {
        private b2DistanceProxy m_proxyA;
        private b2DistanceProxy m_proxyB;
        private b2Sweep m_sweepA;
        private b2Sweep m_sweepB;
        private SeparationFunctionType m_type;
        private b2Vec2 m_axis;
        private b2Vec2 m_localPoint;

        private enum SeparationFunctionType
        {
            Points,
            FaceA,
            FaceB
        }

        public float Initialize(
            b2SimplexCache cache,
            in b2DistanceProxy proxyA,
            in b2Sweep sweepA,
            in b2DistanceProxy proxyB,
            in b2Sweep sweepB,
            float t1)
        {
            m_proxyA = proxyA;
            m_proxyB = proxyB;
            int count = cache.count;
            //Debug.Assert(0 < count && count < 3);

            m_sweepA = sweepA;
            m_sweepB = sweepB;

            m_sweepA.GetTransform(out b2Transform xfA, t1);
            m_sweepB.GetTransform(out b2Transform xfB, t1);

            if (count == 1)
            {
                m_type = SeparationFunctionType.Points;
                b2Vec2 localPointA = m_proxyA._vertices[cache.indexA[0]];
                b2Vec2 localPointB = m_proxyB._vertices[cache.indexB[0]];
                b2Vec2 pointA = Math.Mul(xfA, localPointA);
                b2Vec2 pointB = Math.Mul(xfB, localPointB);
                m_axis = pointB - pointA;
                float s = m_axis.Length();
                m_axis = b2Vec2.Normalize(m_axis);
                return s;
            }

            if (cache.indexA[0] == cache.indexA[1])
            {
                // Two points on B and one on A.
                m_type = SeparationFunctionType.FaceB;
                b2Vec2 localPointB1 = proxyB._vertices[cache.indexB[0]];
                b2Vec2 localPointB2 = proxyB._vertices[cache.indexB[1]];

                m_axis = b2Vec2.Normalize(Vectex.Cross(localPointB2 - localPointB1, 1.0f));
                var normal = b2Vec2.Transform(m_axis, xfB.q); // Math.Mul(xfB.q, m_axis);

                m_localPoint = 0.5f * (localPointB1 + localPointB2);
                b2Vec2 pointB = Math.Mul(xfB, m_localPoint);

                b2Vec2 localPointA = proxyA._vertices[cache.indexA[0]];
                b2Vec2 pointA = Math.Mul(xfA, localPointA);

                float s = b2Vec2.Dot(pointA - pointB, normal);
                if (s < 0.0f)
                {
                    m_axis = -m_axis;
                    s = -s;
                }

                return s;
            }

            {
                // Two points on A and one or two points on B.
                m_type = SeparationFunctionType.FaceA;
                b2Vec2 localPointA1 = m_proxyA._vertices[cache.indexA[0]];
                b2Vec2 localPointA2 = m_proxyA._vertices[cache.indexA[1]];

                m_axis = b2Vec2.Normalize(Vectex.Cross(localPointA2 - localPointA1, 1.0f));
                var normal = b2Vec2.Transform(m_axis, xfA.q); // Math.Mul(xfA.q, m_axis);

                m_localPoint = 0.5f * (localPointA1 + localPointA2);
                b2Vec2 pointA = Math.Mul(xfA, m_localPoint);

                b2Vec2 localPointB = m_proxyB._vertices[cache.indexB[0]];
                b2Vec2 pointB = Math.Mul(xfB, localPointB);

                float s = b2Vec2.Dot(pointB - pointA, normal);
                if (s < 0.0f)
                {
                    m_axis = -m_axis;
                    s = -s;
                }

                return s;
            }
        }

        public float Evaluate(int indexA, int indexB, float t)
        {
            m_sweepA.GetTransform(out b2Transform xfA, t);
            m_sweepB.GetTransform(out b2Transform xfB, t);

            if (m_type == SeparationFunctionType.Points)
            {
                b2Vec2 localPointA = m_proxyA._vertices[indexA];
                b2Vec2 localPointB = m_proxyB._vertices[indexB];

                b2Vec2 pointA = Math.Mul(xfA, localPointA);
                b2Vec2 pointB = Math.Mul(xfB, localPointB);
                return b2Vec2.Dot(pointB - pointA, m_axis);
            }

            if (m_type == SeparationFunctionType.FaceA)
            {
                var normal = b2Vec2.Transform(m_axis, xfA.q); // Math.Mul(xfA.q, m_axis);
                b2Vec2 pointA = Math.Mul(xfA, m_localPoint);

                b2Vec2 localPointB = m_proxyB._vertices[indexB];
                b2Vec2 pointB = Math.Mul(xfB, localPointB);

                return b2Vec2.Dot(pointB - pointA, normal);
            }

            if (m_type == SeparationFunctionType.FaceB)
            {
                var normal = b2Vec2.Transform(m_axis, xfB.q); // Math.Mul(xfB.q, m_axis);
                b2Vec2 pointB = Math.Mul(xfB, m_localPoint);

                b2Vec2 localPointA = m_proxyA._vertices[indexA];
                b2Vec2 pointA = Math.Mul(xfA, localPointA);

                return b2Vec2.Dot(pointA - pointB, normal);
            }

            return 0.0f;

            //Debug.Assert(false);
        }

        public float FindMinSeparation(out int indexA, out int indexB, float t)
        {
            m_sweepA.GetTransform(out b2Transform xfA, t);
            m_sweepB.GetTransform(out b2Transform xfB, t);

            switch (m_type)
            {
                case SeparationFunctionType.Points:
                    {
                        b2Vec2 axisA = Math.MulT(xfA.q, m_axis);
                        b2Vec2 axisB = Math.MulT(xfB.q, -m_axis);

                        indexA = m_proxyA.GetSupport(axisA);
                        indexB = m_proxyB.GetSupport(axisB);

                        b2Vec2 localPointA = m_proxyA.GetVertex(indexA);
                        b2Vec2 localPointB = m_proxyB.GetVertex(indexB);

                        b2Vec2 pointA = Math.Mul(xfA, localPointA);
                        b2Vec2 pointB = Math.Mul(xfB, localPointB);

                        return b2Vec2.Dot(pointB - pointA, m_axis);
                    }
                case SeparationFunctionType.FaceA:
                    {
                        var normal = b2Vec2.Transform(m_axis, xfA.q); // Math.Mul(xfA.q, m_axis);
                        b2Vec2 pointA = Math.Mul(xfA, m_localPoint);

                        b2Vec2 axisB = Math.MulT(xfB.q, -normal);

                        indexA = -1;
                        indexB = m_proxyB.GetSupport(axisB);

                        b2Vec2 localPointB = m_proxyB.GetVertex(indexB);
                        b2Vec2 pointB = Math.Mul(xfB, localPointB);

                        return b2Vec2.Dot(pointB - pointA, normal);
                    }
                case SeparationFunctionType.FaceB:
                    {
                        var normal = b2Vec2.Transform(m_axis, xfB.q); // Math.Mul(xfB.q, m_axis);
                        b2Vec2 pointB = Math.Mul(xfB, m_localPoint);

                        b2Vec2 axisA = Math.MulT(xfA.q, -normal);

                        indexB = -1;
                        indexA = m_proxyA.GetSupport(axisA);

                        b2Vec2 localPointA = m_proxyA.GetVertex(indexA);
                        b2Vec2 pointA = Math.Mul(xfA, localPointA);

                        return b2Vec2.Dot(pointA - pointB, normal);
                    }
            }

            //Debug.Assert(false);
            indexA = -1;
            indexB = -1;
            return 0.0f;
        }
    }
}