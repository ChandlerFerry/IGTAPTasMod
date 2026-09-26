using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Math = Box2D.Math;

namespace Box2D
{
    /// <summary>
    ///  /// The chain has one-sided collision, with the surface normal pointing to the right of the edge.
    ///  This provides a counter-clockwise winding like the polygon shape.
    ///  Connectivity information is used to create smooth collisions.
    /// </summary>
    /// <warning>The chain will not collide properly if there are self-intersections.</warning>
    public class b2ChainShape : b2Shape
    {
        public const byte contactMatch = 3;
        public int m_count;
        public b2Vec2 m_prevVertex, m_nextVertex;
        public b2Vec2[] m_vertices;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2ChainShape() => m_radius = b2Settings.polygonRadius;

        public b2Vec2[] Vertices
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_vertices;
        }

        public override byte ContactMatch => contactMatch;

        /// <summary>
        ///  Create a loop. This automatically adjusts connectivity.
        /// </summary>
        /// <param name="vertices">An array of vertices. These are copied</param>
        public void CreateLoop(in b2Vec2[] vertices)
        {
            int count = vertices.Length;
            if (count < 3)
            {
                return;
            }

            m_count = count + 1;
            m_vertices = new b2Vec2[m_count];
            Array.Copy(vertices, m_vertices, count);
            m_vertices[count] = m_vertices[0];
            m_prevVertex = m_vertices[m_count - 2];
            m_nextVertex = m_vertices[1];
        }

        /// <summary>
        ///  Create a chain with ghost vertices to connect multiple chains together.
        /// </summary>
        /// <param name="vertices">An array of vertices. These are copied</param>
        public void CreateChain(b2Vec2[] verts, int count, in b2Vec2 prevVertex, in b2Vec2 nextVertex)
        {
            b2Vec2[] copy = count == verts.Length ? verts : new b2Vec2[count];
            if (copy != verts)
            {
                Array.Copy(verts, copy, count);
            }

            CreateChain(in copy, in prevVertex, in nextVertex);
        }

        public void CreateChain(in b2Vec2[] vertices, in b2Vec2 prevVertex, in b2Vec2 nextVertex)
        {
            int count = vertices.Length;

            m_count = count;
            m_vertices = new b2Vec2[m_count];
            Array.Copy(vertices, m_vertices, m_count);

            m_prevVertex = prevVertex;
            m_nextVertex = nextVertex;
        }

        public override b2Shape Clone() => (b2ChainShape)MemberwiseClone();

        public override int GetChildCount() => m_count - 1;

        public void GetChildEdge(out b2EdgeShape edge, int index)
        {
            edge = new b2EdgeShape
            {
                m_radius = m_radius,
                m_vertex0 = index > 0 ? m_vertices[index - 1] : m_prevVertex,
                m_vertex1 = m_vertices[index + 0],
                m_vertex2 = m_vertices[index + 1],
                m_vertex3 = index < m_count - 2 ? m_vertices[index + 2] : m_nextVertex,
                m_oneSided = false
            };
        }

        public override bool TestPoint(in b2Transform xf, in b2Vec2 p) => false;

        public override bool RayCast(
            out b2RayCastOutput output,
            in b2RayCastInput input,
            in b2Transform transform,
            int childIndex)
        {
            var edgeShape = new b2EdgeShape();

            int i1 = childIndex;
            int i2 = childIndex + 1;
            if (i2 == m_count)
            {
                i2 = 0;
            }

            edgeShape.m_vertex1 = m_vertices[i1];
            edgeShape.m_vertex2 = m_vertices[i2];

            return edgeShape.RayCast(out output, input, transform, 0);
        }

        public override void ComputeAABB(out b2AABB aabb, in b2Transform xf, int childIndex)
        {
            int i1 = childIndex;
            int i2 = childIndex + 1;
            if (i2 == m_count)
            {
                i2 = 0;
            }

            b2Vec2 v1 = Math.Mul(xf, m_vertices[i1]);
            b2Vec2 v2 = Math.Mul(xf, m_vertices[i2]);

            aabb.lowerBound = b2Vec2.Min(v1, v2);
            aabb.upperBound = b2Vec2.Max(v1, v2);
        }

        public override void ComputeMass(out b2MassData massData, float density)
        {
            massData = default;
        }
    }
}