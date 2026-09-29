
namespace Box2D
{
    public class b2ChainAndPolygonContact : b2Contact
    {
        private static readonly Collider<b2EdgeShape, b2PolygonShape> collider = new EdgeAndPolygonCollider();

        private readonly b2EdgeShape edge;

        public b2ChainAndPolygonContact(b2Fixture fA, int indexA, b2Fixture fB, int indexB) : base(fA, indexA, fB, indexB)
        {
            var chain = (b2ChainShape)FixtureA.b2Shape;
            chain.GetChildEdge(out edge, indexA);
        }

        public override void Evaluate(b2Manifold manifold, in b2Transform xfA, in b2Transform xfB)
        {
            collider.Collide(manifold, edge, xfA, (b2PolygonShape)FixtureB.b2Shape, xfB);
        }
    }
}