using System.Numerics;

namespace Box2D
{
    public class b2FrictionJointDef : b2JointDef
    {
        public b2Vec2 localAnchorA;
        public b2Vec2 localAnchorB;
        public float maxForce;
        public float maxTorque;

        public void Initialize(b2Body bA, b2Body bB, in b2Vec2 anchor)
        {
            bodyA = bA;
            bodyB = bB;
            localAnchorA = bodyA.GetLocalPoint(anchor);
            localAnchorB = bodyB.GetLocalPoint(anchor);
        }
    }
}