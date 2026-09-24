/*
  Literal C# port of Box2D v2.4.1 src/collision/b2_collide_edge.cpp
  (b2CollideEdgeAndCircle + b2CollideEdgeAndPolygon, MIT License,
  Copyright (c) 2019 Erin Catto). Expression order preserved exactly.

  This is the 2.4.x rewritten edge-vs-polygon algorithm (one-sided edges with
  Gauss-map smooth collision handling). Selected when
  b2Settings.UseLegacyEdgePolygon == false; when true (default) the legacy
  2.3.x b2EPCollider SAT algorithm (EdgeAndPolygonCollider) is used instead.
*/
using System;

namespace Box2D
{
    public class EdgeAndPolygonCollider241 : Collider<b2EdgeShape, b2PolygonShape>
    {
        // This structure is used to keep track of the best separating axis.
        private struct EPAxis241
        {
            public enum Type
            {
                Unknown = 0,
                EdgeA = 1,
                EdgeB = 2,
            }

            public b2Vec2 normal;
            public Type type;
            public int index;
            public float separation;
        }

        // This holds polygon B expressed in frame A.
        private sealed class TempPolygon241
        {
            public readonly b2Vec2[] vertices = new b2Vec2[b2Settings.MaxPolygonVertices];
            public readonly b2Vec2[] normals = new b2Vec2[b2Settings.MaxPolygonVertices];
            public int count;
        }

        // Reference face used for clipping
        private struct ReferenceFace241
        {
            public int i1, i2;
            public b2Vec2 v1, v2;
            public b2Vec2 normal;
            public b2Vec2 sideNormal1;
            public float sideOffset1;
            public b2Vec2 sideNormal2;
            public float sideOffset2;
        }

        private static EPAxis241 ComputeEdgeSeparation(TempPolygon241 polygonB, in b2Vec2 v1, in b2Vec2 normal1)
        {
            EPAxis241 axis;
            axis.type = EPAxis241.Type.EdgeA;
            axis.index = -1;
            axis.separation = -float.MaxValue;
            axis.normal = b2Vec2.Zero;

            // Find axis with least overlap (min-max problem)
            for (int j = 0; j < 2; ++j)
            {
                b2Vec2 axes = j == 0 ? normal1 : -normal1;
                float sj = float.MaxValue;

                // Find deepest polygon vertex along axis j
                for (int i = 0; i < polygonB.count; ++i)
                {
                    float si = b2Vec2.Dot(axes, polygonB.vertices[i] - v1);
                    if (si < sj)
                    {
                        sj = si;
                    }
                }

                if (sj > axis.separation)
                {
                    axis.index = j;
                    axis.separation = sj;
                    axis.normal = axes;
                }
            }

            return axis;
        }

        private static EPAxis241 ComputePolygonSeparation(TempPolygon241 polygonB, in b2Vec2 v1, in b2Vec2 v2)
        {
            EPAxis241 axis;
            axis.type = EPAxis241.Type.Unknown;
            axis.index = -1;
            axis.separation = -float.MaxValue;
            axis.normal = b2Vec2.Zero;

            for (int i = 0; i < polygonB.count; ++i)
            {
                b2Vec2 n = -polygonB.normals[i];

                float s1 = b2Vec2.Dot(n, polygonB.vertices[i] - v1);
                float s2 = b2Vec2.Dot(n, polygonB.vertices[i] - v2);
                float s = Math.Min(s1, s2);

                if (s > axis.separation)
                {
                    axis.type = EPAxis241.Type.EdgeB;
                    axis.index = i;
                    axis.separation = s;
                    axis.normal = n;
                }
            }

            return axis;
        }

        public override void Collide(out b2Manifold manifold, in b2EdgeShape edgeA, in b2Transform xfA, in b2PolygonShape polygonB, in b2Transform xfB)
        {
            CollideEdgeAndPolygon241(out manifold, edgeA, in xfA, polygonB, in xfB);
        }

        /// <summary>Box2D v2.4.1 b2CollideEdgeAndPolygon, literal port.</summary>
        public static void CollideEdgeAndPolygon241(
            out b2Manifold manifold,
            b2EdgeShape edgeA,
            in b2Transform xfA,
            b2PolygonShape polygonB,
            in b2Transform xfB)
        {
            manifold = new b2Manifold();
            manifold.pointCount = 0;

            b2Transform xf = Math.MulT(xfA, xfB);

            b2Vec2 centroidB = Math.Mul(xf, polygonB.m_centroid);

            b2Vec2 v1 = edgeA.m_vertex1;
            b2Vec2 v2 = edgeA.m_vertex2;

            b2Vec2 edge1 = v2 - v1;
            edge1 = b2Vec2.Normalize(edge1);

            // Normal points to the right for a CCW winding
            b2Vec2 normal1 = new b2Vec2(edge1.y, -edge1.x);
            float offset1 = b2Vec2.Dot(normal1, centroidB - v1);

            bool oneSided = edgeA.m_oneSided;
            if (oneSided && offset1 < 0.0f)
            {
                return;
            }

            // Get polygonB in frameA
            var tempPolygonB = new TempPolygon241();
            tempPolygonB.count = polygonB.m_count;
            for (int i = 0; i < polygonB.m_count; ++i)
            {
                tempPolygonB.vertices[i] = Math.Mul(xf, polygonB.m_vertices[i]);
                tempPolygonB.normals[i] = b2Vec2.Transform(polygonB.m_normals[i], xf.q);
            }

            float radius = polygonB.m_radius + edgeA.m_radius;

            EPAxis241 edgeAxis = ComputeEdgeSeparation(tempPolygonB, v1, normal1);
            if (edgeAxis.separation > radius)
            {
                return;
            }

            EPAxis241 polygonAxis = ComputePolygonSeparation(tempPolygonB, v1, v2);
            if (polygonAxis.separation > radius)
            {
                return;
            }

            // Use hysteresis for jitter reduction.
            const float k_relativeTol = 0.98f;
            const float k_absoluteTol = 0.001f;

            EPAxis241 primaryAxis;
            if (polygonAxis.separation - radius > k_relativeTol * (edgeAxis.separation - radius) + k_absoluteTol)
            {
                primaryAxis = polygonAxis;
            }
            else
            {
                primaryAxis = edgeAxis;
            }

            if (oneSided)
            {
                // Smooth collision
                // See https://box2d.org/posts/2020/06/ghost-collisions/

                b2Vec2 edge0 = v1 - (edgeA.m_vertex0 ?? b2Vec2.Zero);
                edge0 = b2Vec2.Normalize(edge0);
                b2Vec2 normal0 = new b2Vec2(edge0.y, -edge0.x);
                bool convex1 = Vectex.Cross(edge0, edge1) >= 0.0f;

                b2Vec2 edge2 = (edgeA.m_vertex3 ?? b2Vec2.Zero) - v2;
                edge2 = b2Vec2.Normalize(edge2);
                b2Vec2 normal2 = new b2Vec2(edge2.y, -edge2.x);
                bool convex2 = Vectex.Cross(edge1, edge2) >= 0.0f;

                const float sinTol = 0.1f;
                bool side1 = b2Vec2.Dot(primaryAxis.normal, edge1) <= 0.0f;

                // Check Gauss Map
                if (side1)
                {
                    if (convex1)
                    {
                        if (Vectex.Cross(primaryAxis.normal, normal0) > sinTol)
                        {
                            // Skip region
                            return;
                        }

                        // Admit region
                    }
                    else
                    {
                        // Snap region
                        primaryAxis = edgeAxis;
                    }
                }
                else
                {
                    if (convex2)
                    {
                        if (Vectex.Cross(normal2, primaryAxis.normal) > sinTol)
                        {
                            // Skip region
                            return;
                        }

                        // Admit region
                    }
                    else
                    {
                        // Snap region
                        primaryAxis = edgeAxis;
                    }
                }
            }

            var clipPoints = new b2ClipVertex[2];
            ReferenceFace241 rf;
            if (primaryAxis.type == EPAxis241.Type.EdgeA)
            {
                manifold.type = b2ManifoldType.FaceA;

                // Search for the polygon normal that is most anti-parallel to the edge normal.
                int bestIndex = 0;
                float bestValue = b2Vec2.Dot(primaryAxis.normal, tempPolygonB.normals[0]);
                for (int i = 1; i < tempPolygonB.count; ++i)
                {
                    float value = b2Vec2.Dot(primaryAxis.normal, tempPolygonB.normals[i]);
                    if (value < bestValue)
                    {
                        bestValue = value;
                        bestIndex = i;
                    }
                }

                int i1 = bestIndex;
                int i2 = i1 + 1 < tempPolygonB.count ? i1 + 1 : 0;

                clipPoints[0].v = tempPolygonB.vertices[i1];
                clipPoints[0].id.key = 0;
                clipPoints[0].id.cf.indexA = 0;
                clipPoints[0].id.cf.indexB = (byte)i1;
                clipPoints[0].id.cf.typeA = (byte)b2ContactFeatureType.Face;
                clipPoints[0].id.cf.typeB = (byte)b2ContactFeatureType.Vertex;

                clipPoints[1].v = tempPolygonB.vertices[i2];
                clipPoints[1].id.key = 0;
                clipPoints[1].id.cf.indexA = 0;
                clipPoints[1].id.cf.indexB = (byte)i2;
                clipPoints[1].id.cf.typeA = (byte)b2ContactFeatureType.Face;
                clipPoints[1].id.cf.typeB = (byte)b2ContactFeatureType.Vertex;

                rf.i1 = 0;
                rf.i2 = 1;
                rf.v1 = v1;
                rf.v2 = v2;
                rf.normal = primaryAxis.normal;
                rf.sideNormal1 = -edge1;
                rf.sideNormal2 = edge1;
            }
            else
            {
                manifold.type = b2ManifoldType.FaceB;

                clipPoints[0].v = v2;
                clipPoints[0].id.key = 0;
                clipPoints[0].id.cf.indexA = 1;
                clipPoints[0].id.cf.indexB = (byte)primaryAxis.index;
                clipPoints[0].id.cf.typeA = (byte)b2ContactFeatureType.Vertex;
                clipPoints[0].id.cf.typeB = (byte)b2ContactFeatureType.Face;

                clipPoints[1].v = v1;
                clipPoints[1].id.key = 0;
                clipPoints[1].id.cf.indexA = 0;
                clipPoints[1].id.cf.indexB = (byte)primaryAxis.index;
                clipPoints[1].id.cf.typeA = (byte)b2ContactFeatureType.Vertex;
                clipPoints[1].id.cf.typeB = (byte)b2ContactFeatureType.Face;

                rf.i1 = primaryAxis.index;
                rf.i2 = rf.i1 + 1 < tempPolygonB.count ? rf.i1 + 1 : 0;
                rf.v1 = tempPolygonB.vertices[rf.i1];
                rf.v2 = tempPolygonB.vertices[rf.i2];
                rf.normal = tempPolygonB.normals[rf.i1];

                // CCW winding
                rf.sideNormal1 = new b2Vec2(rf.normal.y, -rf.normal.x);
                rf.sideNormal2 = -rf.sideNormal1;
            }

            rf.sideOffset1 = b2Vec2.Dot(rf.sideNormal1, rf.v1);
            rf.sideOffset2 = b2Vec2.Dot(rf.sideNormal2, rf.v2);

            // Clip incident edge against reference face side planes
            b2ClipVertex[] clipPoints1;
            b2ClipVertex[] clipPoints2;
            int np;

            // Clip to side 1
            np = Collision.ClipSegmentToLine(out clipPoints1, in clipPoints, in rf.sideNormal1, rf.sideOffset1, rf.i1);

            if (np < b2Settings.MaxManifoldPoints)
            {
                return;
            }

            // Clip to side 2
            np = Collision.ClipSegmentToLine(out clipPoints2, in clipPoints1, in rf.sideNormal2, rf.sideOffset2, rf.i2);

            if (np < b2Settings.MaxManifoldPoints)
            {
                return;
            }

            // Now clipPoints2 contains the clipped points.
            if (primaryAxis.type == EPAxis241.Type.EdgeA)
            {
                manifold.localNormal = rf.normal;
                manifold.localPoint = rf.v1;
            }
            else
            {
                manifold.localNormal = polygonB.m_normals[rf.i1];
                manifold.localPoint = polygonB.m_vertices[rf.i1];
            }

            int pointCount = 0;
            for (int i = 0; i < b2Settings.MaxManifoldPoints; ++i)
            {
                float separation = b2Vec2.Dot(rf.normal, clipPoints2[i].v - rf.v1);

                if (separation <= radius)
                {
                    b2ManifoldPoint cp = manifold.points[pointCount];
                    if (cp == null)
                    {
                        cp = new b2ManifoldPoint();
                        manifold.points[pointCount] = cp;
                    }

                    if (primaryAxis.type == EPAxis241.Type.EdgeA)
                    {
                        cp.localPoint = Math.MulT(xf, clipPoints2[i].v);
                        cp.id = clipPoints2[i].id;
                    }
                    else
                    {
                        cp.localPoint = clipPoints2[i].v;
                        cp.id.key = 0;
                        cp.id.cf.typeA = clipPoints2[i].id.cf.typeB;
                        cp.id.cf.typeB = clipPoints2[i].id.cf.typeA;
                        cp.id.cf.indexA = clipPoints2[i].id.cf.indexB;
                        cp.id.cf.indexB = clipPoints2[i].id.cf.indexA;
                    }

                    ++pointCount;
                }
            }

            manifold.pointCount = pointCount;
        }

        /// <summary>Box2D v2.4.1 b2CollideEdgeAndCircle, literal port.</summary>
        public static void CollideEdgeAndCircle241(
            out b2Manifold manifold,
            b2EdgeShape edgeA,
            in b2Transform xfA,
            b2CircleShape circleB,
            in b2Transform xfB)
        {
            manifold = new b2Manifold();
            manifold.pointCount = 0;

            // Compute circle in frame of edge
            b2Vec2 Q = Math.MulT(xfA, Math.Mul(xfB, circleB.m_p));

            b2Vec2 A = edgeA.m_vertex1;
            b2Vec2 B = edgeA.m_vertex2;
            b2Vec2 e = B - A;

            // Normal points to the right for a CCW winding
            b2Vec2 n = new b2Vec2(e.y, -e.x);
            float offset = b2Vec2.Dot(n, Q - A);

            bool oneSided = edgeA.m_oneSided;
            if (oneSided && offset < 0.0f)
            {
                return;
            }

            // Barycentric coordinates
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
                if (edgeA.m_oneSided)
                {
                    b2Vec2 A1 = edgeA.m_vertex0 ?? b2Vec2.Zero;
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
                b2ManifoldPoint mp = manifold.points[0] = new b2ManifoldPoint();
                mp.id.key = 0;
                mp.id.cf = cf;
                mp.localPoint = circleB.m_p;
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
                if (edgeA.m_oneSided)
                {
                    b2Vec2 B2 = edgeA.m_vertex3 ?? b2Vec2.Zero;
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
                b2ManifoldPoint mp = manifold.points[0] = new b2ManifoldPoint();
                mp.id.key = 0;
                mp.id.cf = cf;
                mp.localPoint = circleB.m_p;
                return;
            }

            // Region AB
            float den = b2Vec2.Dot(e, e);
            b2Vec2 P2 = (1.0f / den) * (u * A + v * B);
            b2Vec2 d2 = Q - P2;
            float dd2 = b2Vec2.Dot(d2, d2);
            if (dd2 > radius * radius)
            {
                return;
            }

            if (offset < 0.0f)
            {
                n = new b2Vec2(-n.x, -n.y);
            }

            n = b2Vec2.Normalize(n);

            cf.indexA = 0;
            cf.typeA = (byte)b2ContactFeatureType.Face;
            manifold.pointCount = 1;
            manifold.type = b2ManifoldType.FaceA;
            manifold.localNormal = n;
            manifold.localPoint = A;
            b2ManifoldPoint mp2 = manifold.points[0] = new b2ManifoldPoint();
            mp2.id.key = 0;
            mp2.id.cf = cf;
            mp2.localPoint = circleB.m_p;
        }
    }
}
