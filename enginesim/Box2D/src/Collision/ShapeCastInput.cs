using System.Numerics;

namespace Box2D
{
    public struct b2ShapeCastInput
    {
        public b2DistanceProxy proxyA;
        public b2DistanceProxy proxyB;
        public b2Transform transformA;
        public b2Transform transformB;
        public b2Vec2 translationB;
    }
}