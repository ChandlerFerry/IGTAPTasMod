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
}
