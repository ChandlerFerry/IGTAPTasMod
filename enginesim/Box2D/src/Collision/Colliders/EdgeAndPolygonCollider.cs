using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Math = Box2D.Math;

namespace Box2D
{
    public class EdgeAndPolygonCollider : Collider<b2EdgeShape, b2PolygonShape>
    {
        public override void Collide(
            out b2Manifold manifold,
            in b2EdgeShape edgeA,
            in b2Transform xfA,
            in b2PolygonShape polygonB,
            in b2Transform xfB)
        {
            b2Transform xf = Math.MulT(xfA, xfB);
            b2Vec2 centroidB = Math.Mul(xf, polygonB.m_centroid);

            b2Vec2? v0 = edgeA.m_vertex0;
            b2Vec2 v1 = edgeA.m_vertex1;
            b2Vec2 v2 = edgeA.m_vertex2;
            b2Vec2? v3 = edgeA.m_vertex3;

            var edge1 = b2Vec2.Normalize(v2 - v1);
            b2Vec2 normal1 = new b2Vec2(edge1.Y, -edge1.X), normal0 = b2Vec2.Zero, normal2 = b2Vec2.Zero;
            float offset1 = b2Vec2.Dot(normal1, centroidB - v1);
            float offset0 = 0.0f, offset2 = 0.0f;

            bool convex1 = false, convex2 = false;

            // Is there a preceding edge?
            if (v0.HasValue)
            {
                var edge0 = b2Vec2.Normalize(v1 - v0.Value);
                normal0 = new b2Vec2(edge0.Y, -edge0.X);
                convex1 = Vectex.Cross(edge0, edge1) >= 0.0f;
                offset0 = b2Vec2.Dot(normal0, centroidB - v0.Value);
            }

            // Is there a following edge?
            if (v3.HasValue)
            {
                var edge2 = b2Vec2.Normalize(v3.Value - v2);
                normal2 = new b2Vec2(edge2.Y, -edge2.X);
                convex2 = Vectex.Cross(edge1, edge2) > 0.0f;
                offset2 = b2Vec2.Dot(normal2, centroidB - v2);
            }

            b2Vec2 normal, lowerLimit, upperLimit;

            bool front;

            // Determine front or back collision. Determine collision normal limits.
            if (v0.HasValue && v3.HasValue)
            {
                if (convex1 && convex2)
                {
                    front = offset0 >= 0.0f || offset1 >= 0.0f || offset2 >= 0.0f;
                    if (front)
                    {
                        normal = normal1;
                        lowerLimit = normal0;
                        upperLimit = normal2;
                    }
                    else
                    {
                        normal = -normal1;
                        lowerLimit = -normal1;
                        upperLimit = -normal1;
                    }
                }
                else if (convex1)
                {
                    front = offset0 >= 0.0f || offset1 >= 0.0f && offset2 >= 0.0f;
                    if (front)
                    {
                        normal = normal1;
                        lowerLimit = normal0;
                        upperLimit = normal1;
                    }
                    else
                    {
                        normal = -normal1;
                        lowerLimit = -normal2;
                        upperLimit = -normal1;
                    }
                }
                else if (convex2)
                {
                    front = offset2 >= 0.0f || offset0 >= 0.0f && offset1 >= 0.0f;
                    if (front)
                    {
                        normal = normal1;
                        lowerLimit = normal1;
                        upperLimit = normal2;
                    }
                    else
                    {
                        normal = -normal1;
                        lowerLimit = -normal1;
                        upperLimit = -normal0;
                    }
                }
                else
                {
                    front = offset0 >= 0.0f && offset1 >= 0.0f && offset2 >= 0.0f;
                    if (front)
                    {
                        normal = normal1;
                        lowerLimit = normal1;
                        upperLimit = normal1;
                    }
                    else
                    {
                        normal = -normal1;
                        lowerLimit = -normal2;
                        upperLimit = -normal0;
                    }
                }
            }
            else if (v0.HasValue)
            {
                if (convex1)
                {
                    front = offset0 >= 0.0f || offset1 >= 0.0f;
                    if (front)
                    {
                        normal = normal1;
                        lowerLimit = normal0;
                        upperLimit = -normal1;
                    }
                    else
                    {
                        normal = -normal1;
                        lowerLimit = normal1;
                        upperLimit = -normal1;
                    }
                }
                else
                {
                    front = offset0 >= 0.0f && offset1 >= 0.0f;
                    if (front)
                    {
                        normal = normal1;
                        lowerLimit = normal1;
                        upperLimit = -normal1;
                    }
                    else
                    {
                        normal = -normal1;
                        lowerLimit = normal1;
                        upperLimit = -normal0;
                    }
                }
            }

            else if (v3.HasValue)
            {
                if (convex2)
                {
                    front = offset1 >= 0.0f || offset2 >= 0.0f;
                    if (front)
                    {
                        normal = normal1;
                        lowerLimit = -normal1;
                        upperLimit = normal2;
                    }
                    else
                    {
                        normal = -normal1;
                        lowerLimit = -normal1;
                        upperLimit = normal1;
                    }
                }
                else
                {
                    front = offset1 >= 0.0f && offset2 >= 0.0f;
                    if (front)
                    {
                        normal = normal1;
                        lowerLimit = -normal1;
                        upperLimit = normal1;
                    }
                    else
                    {
                        normal = -normal1;
                        lowerLimit = -normal2;
                        upperLimit = normal1;
                    }
                }
            }

            else
            {
                front = offset1 >= 0.0f;
                if (front)
                {
                    normal = normal1;
                    lowerLimit = -normal1;
                    upperLimit = -normal1;
                }
                else
                {
                    normal = -normal1;
                    lowerLimit = normal1;
                    upperLimit = normal1;
                }
            }

            // Get polygonB in frameA (every slot Collide reads, the first count, is written here)
            TempPolygon m_polygonB = t_polygonB ??= new TempPolygon();
            m_polygonB.count = polygonB.m_count;
            for (var i = 0; i < polygonB.m_count; ++i)
            {
                m_polygonB.vertices[i] = Math.Mul(xf, polygonB.m_vertices[i]);
                m_polygonB.normals[i] = b2Vec2.Transform(polygonB.m_normals[i], xf.q);
            }

            float m_radius = polygonB.m_radius + edgeA.m_radius;
            manifold = new b2Manifold();
            manifold.pointCount = 0;

            EPAxis edgeAxis = ComputeEdgeSeparation(front, m_polygonB, normal, v1);

            // If no valid normal can be found than this edge should not collide.
            if (edgeAxis.type == EPAxis.AxisType.Unknown || edgeAxis.separation > m_radius)
            {
                return;
            }

            EPAxis polygonAxis = ComputePolygonSeparation(normal, m_polygonB, v1, v2, m_radius, upperLimit, lowerLimit);

            if (polygonAxis.type != EPAxis.AxisType.Unknown && polygonAxis.separation > m_radius)
            {
                return;
            }

            // Use hysteresis for jitter reduction.
            const float k_relativeTol = 0.98f;
            const float k_absoluteTol = 0.001f;

            EPAxis primaryAxis;
            if (polygonAxis.type == EPAxis.AxisType.Unknown)
            {
                primaryAxis = edgeAxis;
            }
            else if (polygonAxis.separation > k_relativeTol * edgeAxis.separation + k_absoluteTol)
            {
                primaryAxis = polygonAxis;
            }
            else
            {
                primaryAxis = edgeAxis;
            }

            Span<b2ClipVertex> ie = stackalloc b2ClipVertex[2];

            ReferenceFace rf;
            if (primaryAxis.type == EPAxis.AxisType.EdgeA)
            {
                manifold.type = b2ManifoldType.FaceA;

                // Search for the polygon normal that is most anti-parallel to the edge normal.
                var bestIndex = 0;
                float bestValue = b2Vec2.Dot(normal, m_polygonB.normals[0]);
                for (var i = 1; i < m_polygonB.count; ++i)
                {
                    float value = b2Vec2.Dot(normal, m_polygonB.normals[i]);
                    if (value < bestValue)
                    {
                        bestValue = value;
                        bestIndex = i;
                    }
                }

                int i1 = bestIndex;
                int i2 = i1 + 1 < m_polygonB.count ? i1 + 1 : 0;

                ie[0].v = m_polygonB.vertices[i1];
                ie[0].id.cf.indexA = 0;
                ie[0].id.cf.indexB = (byte)i1;
                ie[0].id.cf.typeA = (byte)b2ContactFeatureType.Face;
                ie[0].id.cf.typeB = (byte)b2ContactFeatureType.Vertex;

                ie[1].v = m_polygonB.vertices[i2];
                ie[1].id.cf.indexA = 0;
                ie[1].id.cf.indexB = (byte)i2;
                ie[1].id.cf.typeA = (byte)b2ContactFeatureType.Face;
                ie[1].id.cf.typeB = (byte)b2ContactFeatureType.Vertex;

                if (front)
                {
                    rf.i1 = 0;
                    rf.i2 = 1;
                    rf.v1 = v1;
                    rf.v2 = v2;
                    rf.normal = normal1;
                }
                else
                {
                    rf.i1 = 1;
                    rf.i2 = 0;
                    rf.v1 = v2;
                    rf.v2 = v1;
                    rf.normal = -normal1;
                }
            }
            else
            {
                manifold.type = b2ManifoldType.FaceB;

                ie[0].v = v1;
                ie[0].id.cf.indexA = 0;
                ie[0].id.cf.indexB = (byte)primaryAxis.index;
                ie[0].id.cf.typeA = (byte)b2ContactFeatureType.Vertex;
                ie[0].id.cf.typeB = (byte)b2ContactFeatureType.Face;

                ie[1].v = v2;
                ie[1].id.cf.indexA = 0;
                ie[1].id.cf.indexB = (byte)primaryAxis.index;
                ie[1].id.cf.typeA = (byte)b2ContactFeatureType.Vertex;
                ie[1].id.cf.typeB = (byte)b2ContactFeatureType.Face;

                rf.i1 = primaryAxis.index;
                rf.i2 = rf.i1 + 1 < m_polygonB.count ? rf.i1 + 1 : 0;
                rf.v1 = m_polygonB.vertices[rf.i1];
                rf.v2 = m_polygonB.vertices[rf.i2];
                rf.normal = m_polygonB.normals[rf.i1];
            }

            rf.sideNormal1 = new b2Vec2(rf.normal.Y, -rf.normal.X);
            rf.sideNormal2 = -rf.sideNormal1;
            rf.sideOffset1 = b2Vec2.Dot(rf.sideNormal1, rf.v1);
            rf.sideOffset2 = b2Vec2.Dot(rf.sideNormal2, rf.v2);

            // Clip incident edge against extruded edge1 side edges.
            int np;

            // Clip to box side 1
            Span<b2ClipVertex> clipPoints1 = stackalloc b2ClipVertex[2];
            np = Collision.ClipSegmentToLine(clipPoints1, ie, rf.sideNormal1, rf.sideOffset1, rf.i1);
            if (np < b2Settings.MaxManifoldPoints)
            {
                return;
            }

            // Clip to negative box side 1
            Span<b2ClipVertex> clipPoints2 = stackalloc b2ClipVertex[2];
            np = Collision.ClipSegmentToLine(clipPoints2, clipPoints1, rf.sideNormal2, rf.sideOffset2,
                                             rf.i2);
            if (np < b2Settings.MaxManifoldPoints)
            {
                return;
            }

            // Now clipPoints2 contains the clipped points.
            if (primaryAxis.type == EPAxis.AxisType.EdgeA)
            {
                manifold.localNormal = rf.normal;
                manifold.localPoint = rf.v1;
            }
            else
            {
                manifold.localNormal = polygonB.m_normals[rf.i1];
                manifold.localPoint = polygonB.m_vertices[rf.i1];
            }

            var pointCount = 0;
            for (var i = 0; i < b2Settings.MaxManifoldPoints; ++i)
            {
                float separation = b2Vec2.Dot(rf.normal, clipPoints2[i].v - rf.v1);

                if (separation <= m_radius)
                {
                    var cp = new b2ManifoldPoint();

                    if (primaryAxis.type == EPAxis.AxisType.EdgeA)
                    {
                        cp.localPoint = Math.MulT(xf, clipPoints2[i].v);
                        cp.id = clipPoints2[i].id;
                    }
                    else
                    {
                        cp.localPoint = clipPoints2[i].v;
                        cp.id.cf.typeA = clipPoints2[i].id.cf.typeB;
                        cp.id.cf.typeB = clipPoints2[i].id.cf.typeA;
                        cp.id.cf.indexA = clipPoints2[i].id.cf.indexB;
                        cp.id.cf.indexB = clipPoints2[i].id.cf.indexA;
                    }

                    manifold.points[pointCount] = cp;
                    ++pointCount;
                }
            }

            manifold.pointCount = pointCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static EPAxis ComputeEdgeSeparation(bool front, in TempPolygon polygonB, in b2Vec2 normal, in b2Vec2 v1)
        {
            EPAxis axis;
            axis.type = EPAxis.AxisType.EdgeA;
            axis.index = front ? 0 : 1;
            axis.separation = float.MaxValue;
            for (var i = 0; i < polygonB.count; ++i)
            {
                float s = b2Vec2.Dot(normal, polygonB.vertices[i] - v1);
                if (s < axis.separation)
                {
                    axis.separation = s;
                }
            }

            return axis;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static EPAxis ComputePolygonSeparation(in b2Vec2 normal, in TempPolygon polygonB, in b2Vec2 v1,
            in b2Vec2 v2, in float radius, in b2Vec2 upperLimit, in b2Vec2 lowerLimit)
        {
            EPAxis axis;
            axis.type = EPAxis.AxisType.Unknown;
            axis.index = -1;
            axis.separation = float.MinValue;
            var perp = new b2Vec2(-normal.Y, normal.X);
            for (var i = 0; i < polygonB.count; ++i)
            {
                b2Vec2 n = -polygonB.normals[i];

                float s1 = b2Vec2.Dot(n, polygonB.vertices[i] - v1);
                float s2 = b2Vec2.Dot(n, polygonB.vertices[i] - v2);
                float s = MathF.Min(s1, s2);

                if (s > radius)
                {
                    // No collision
                    axis.type = EPAxis.AxisType.EdgeB;
                    axis.index = i;
                    axis.separation = s;
                    return axis;
                }

                // Adjacency
                if (b2Vec2.Dot(n, perp) >= 0.0f)
                {
                    if (b2Vec2.Dot(n - upperLimit, normal) < -b2Settings.angularSlop)
                    {
                        continue;
                    }
                }
                else
                {
                    if (b2Vec2.Dot(n - lowerLimit, normal) < -b2Settings.angularSlop)
                    {
                        continue;
                    }
                }

                if (s > axis.separation)
                {
                    axis.type = EPAxis.AxisType.EdgeB;
                    axis.index = i;
                    axis.separation = s;
                }
            }

            return axis;
        }

        // The collider is one static instance shared by every world and thread; its scratch polygon is one per thread.
        [ThreadStatic] private static TempPolygon t_polygonB;

        private class TempPolygon
        {
            public readonly b2Vec2[] normals = new b2Vec2[b2Settings.MaxPolygonVertices];
            public readonly b2Vec2[] vertices = new b2Vec2[b2Settings.MaxPolygonVertices];
            public int count;
        }

        // This structure is used to keep track of the best separating axis.
        private struct EPAxis
        {
            public enum AxisType
            {
                Unknown,
                EdgeA,
                EdgeB
            }

            public AxisType type;
            public int index;
            public float separation;
        }

        private struct ReferenceFace
        {
            public int i1;
            public int i2;

            public b2Vec2 v1;
            public b2Vec2 v2;

            public b2Vec2 normal;

            public b2Vec2 sideNormal1;
            public float sideOffset1;

            public b2Vec2 sideNormal2;
            public float sideOffset2;
        }
    }
}