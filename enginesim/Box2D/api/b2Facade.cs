using System;

namespace Box2D
{
    public abstract class b2QueryCallback
    {
        public abstract bool ReportFixture(b2Fixture fixture);
    }

    public abstract class b2RayCastCallback
    {
        public abstract float ReportFixture(b2Fixture fixture, in b2Vec2 point, in b2Vec2 normal, float fraction);
    }

    public static class b2Collision
    {
        public static void b2Distance(out b2DistanceOutput output, ref b2SimplexCache cache, in b2DistanceInput input)
        {
            b2Contact.Distance(out output, ref cache, in input);
        }

        /// <summary>
        /// Shapes whose AABBs (radius included) are further apart than this can't be within GJK's 10·FLT_EPSILON of each
        /// other: the gap is a lower bound on their distance, and far above the float error at world coordinates.
        /// </summary>
        const float SeparatedMargin = 1f;

        /// <summary>
        /// C++ b2TestOverlap. <paramref name="bufferA"/> and <paramref name="bufferB"/> are two distinct two-slot scratch arrays
        /// the caller owns (b2DistanceProxy.Set): the query keeps no reference to them after it returns.
        /// </summary>
        public static bool b2TestOverlap(
            b2Shape shapeA,
            int indexA,
            b2Shape shapeB,
            int indexB,
            in b2Transform xfA,
            in b2Transform xfB,
            b2Vec2[] bufferA,
            b2Vec2[] bufferB)
        {
            shapeA.ComputeAABB(out b2AABB a, xfA, indexA);
            shapeB.ComputeAABB(out b2AABB b, xfB, indexB);
            if (b.lowerBound.x - a.upperBound.x > SeparatedMargin || a.lowerBound.x - b.upperBound.x > SeparatedMargin
                || b.lowerBound.y - a.upperBound.y > SeparatedMargin || a.lowerBound.y - b.upperBound.y > SeparatedMargin)
                return false;
            if (shapeA is b2PolygonShape pa && shapeB is b2PolygonShape pb
                && (VertexDeepInside(pa, xfA, pb, xfB) || VertexDeepInside(pb, xfB, pa, xfA)))
                return true;
            b2DistanceInput input = default;
            input.proxyA.Set(shapeA, indexA, bufferA);
            input.proxyB.Set(shapeB, indexB, bufferB);
            input.transformA = xfA;
            input.transformB = xfB;
            input.useRadii = true;
            b2SimplexCache cache = new b2SimplexCache();
            cache.count = 0;
            b2Distance(out b2DistanceOutput output, ref cache, in input);
            return output.distance < 10.0f * b2Settings.FLT_EPSILON;
        }

        /// <summary>A vertex a polygon's core puts this far inside the other's core overlaps it beyond any float error.</summary>
        const float DeepInside = 0.1f;

        /// <summary>Some vertex of <paramref name="a"/> lies deep inside <paramref name="b"/>: the shapes overlap (GJK distance 0).</summary>
        static bool VertexDeepInside(b2PolygonShape a, in b2Transform xfA, b2PolygonShape b, in b2Transform xfB)
        {
            if (b.m_count < 3) return false;
            for (int i = 0; i < a.m_count; i++)
            {
                b2Vec2 p = Math.MulT(xfB, Math.Mul(xfA, a.m_vertices[i]));
                int j = 0;
                while (j < b.m_count && b2Vec2.Dot(b.m_normals[j], p - b.m_vertices[j]) < -DeepInside) j++;
                if (j == b.m_count) return true;
            }
            return false;
        }
    }
}
