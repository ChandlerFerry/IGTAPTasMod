using System.Numerics;

namespace Box2D
{
    public class b2MotorJointDef : b2JointDef
    {
        /// The bodyB angle minus bodyA angle in radians.
        public float angularOffset;

        /// b2Position correction factor in the range [0,1].
        public float correctionFactor;

        /// b2Position of bodyB minus the position of bodyA, in bodyA's frame, in meters.
        public b2Vec2 linearOffset;

        /// The maximum motor force in N.
        public float maxForce;

        /// The maximum motor torque in N-m.
        public float maxTorque;

        private b2MotorJointDef()
        {
            maxForce = 1.0f;
            maxTorque = 1.0f;
            correctionFactor = 0.3f;
        }

        /// Initialize the bodies and offsets using the current transforms.
        public void Initialize(b2Body bA, b2Body bB)
        {
            bodyA = bA;
            bodyB = bB;
            b2Vec2 xB = bodyB.GetPosition();
            linearOffset = bodyA.GetLocalPoint(xB);

            float angleA = bodyA.GetAngle();
            float angleB = bodyB.GetAngle();
            angularOffset = angleB - angleA;
        }
    }
}