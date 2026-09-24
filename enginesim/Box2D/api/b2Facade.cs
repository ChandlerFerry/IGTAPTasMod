using System;

namespace Box2D
{
    /// <summary>Callback for b2World.QueryAABB (Box2D v2 API shape).</summary>
    public abstract class b2QueryCallback
    {
        /// <summary>Called for each fixture found in the query AABB. Return false to terminate the query.</summary>
        public abstract bool ReportFixture(b2Fixture fixture);
    }

    /// <summary>Callback for b2World.RayCast (Box2D v2 API shape).</summary>
    public abstract class b2RayCastCallback
    {
        /// <summary>
        ///  Called for each fixture hit by the ray. Return the new max fraction
        ///  (0 terminates, fraction clips, 1 continues), like Box2D v2.
        /// </summary>
        public abstract float ReportFixture(b2Fixture fixture, in b2Vec2 point, in b2Vec2 normal, float fraction);
    }

    /// <summary>
    ///  Box2D v2 free-function entry points (C++ b2Collision.h / b2_distance.h /
    ///  b2_time_of_impact.h), as promised to the integrator: statics on b2Collision.
    /// </summary>
    public static class b2Collision
    {
        /// <summary>Compute the closest points between two shapes (GJK). Warm start via <paramref name="cache"/>.</summary>
        public static void b2Distance(out b2DistanceOutput output, ref b2SimplexCache cache, in b2DistanceInput input)
        {
            b2Contact.Distance(out output, ref cache, in input);
        }

        /// <summary>Compute the upper bound on time before two shapes penetrate (swept separating axis).</summary>
        public static void b2TimeOfImpact(out b2TOIOutput output, in b2TOIInput input)
        {
            TOI.TimeOfImpact(out output, in input);
        }

        /// <summary>Determine if two AABBs overlap.</summary>
        public static bool b2TestOverlap(in b2AABB a, in b2AABB b)
        {
            return Collision.TestOverlap(in a, in b);
        }

        /// <summary>Determine if two generic shapes overlap (uses b2Distance).</summary>
        public static bool b2TestOverlap(
            b2Shape shapeA,
            int indexA,
            b2Shape shapeB,
            int indexB,
            in b2Transform xfA,
            in b2Transform xfB)
        {
            b2DistanceInput input = default;
            input.proxyA.Set(shapeA, indexA);
            input.proxyB.Set(shapeB, indexB);
            input.transformA = xfA;
            input.transformB = xfB;
            input.useRadii = true;
            b2SimplexCache cache = new b2SimplexCache();
            cache.count = 0;
            b2Distance(out b2DistanceOutput output, ref cache, in input);
            return output.distance < 10.0f * b2Settings.FLT_EPSILON;
        }
    }

    public static class b2WorldExtensions
    {
        /// <summary>Exact world snapshot for branching. Implemented in the next drop.</summary>
        public static object SaveState(this b2World world) =>
            throw new NotImplementedException("b2World.SaveState lands in the next drop.");

        /// <summary>Exact world restore for branching. Implemented in the next drop.</summary>
        public static void LoadState(this b2World world, object state) =>
            throw new NotImplementedException("b2World.LoadState lands in the next drop.");
    }
}
