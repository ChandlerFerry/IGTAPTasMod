
namespace Box2D
{
    public abstract class Collider<TShapeA, TShapeB>
        where TShapeA : b2Shape where TShapeB : b2Shape
    {
        public abstract void Collide(
            out b2Manifold manifold,
            in TShapeA shapeA,
            in b2Transform xfA,
            in TShapeB shapeB,
            in b2Transform xfB);
    }
}