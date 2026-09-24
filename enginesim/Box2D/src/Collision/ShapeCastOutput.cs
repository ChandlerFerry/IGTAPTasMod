using System.Numerics;

namespace Box2D
{
    /// Output results for b2ShapeCast
    public struct b2ShapeCastOutput
    {
        public b2Vec2 point;
        public b2Vec2 normal;
        public float lambda;
        public int iterations;
    }
}