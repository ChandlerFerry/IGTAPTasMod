
namespace Box2D
{
    public class b2ChainAndCircleContact : b2EdgeAndCircleContact
    {
        public b2ChainAndCircleContact(b2Fixture fA, int indexA, b2Fixture fB, int indexB) : base(fA, indexA, fB, indexB)
        {
            ((b2ChainShape)FixtureA.b2Shape).GetChildEdge(out edgeA, indexA);
        }

        public override void Evaluate(b2Manifold manifold, in b2Transform xfA, in b2Transform xfB)
        {
            base.Evaluate(manifold, xfA, xfB);
        }
    }
}