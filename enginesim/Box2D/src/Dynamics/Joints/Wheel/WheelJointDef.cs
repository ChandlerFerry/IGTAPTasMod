using System;
using System.Numerics;

namespace Box2D
{
    public class b2WheelJointDef : b2JointDef
    {
        public float damping;

        [Obsolete("Use stiffness and damping instead of frequencyHz and dampingRatio")]
        public float? dampingRatio;

        public bool enableLimit;
        public bool enableMotor;

        [Obsolete("Use stiffness and damping instead of frequencyHz and dampingRatio")]
        public float? frequencyHz;

        public b2Vec2 localAnchorA;
        public b2Vec2 localAnchorB;
        public b2Vec2 localAxisA;
        public float lowerTranslation;
        public float maxMotorTorque;
        public float motorSpeed;

        public float stiffness;
        public float upperTranslation;

        public void Initialize(b2Body bA, b2Body bB, in b2Vec2 anchor, in b2Vec2 axis)
        {
            bodyA = bA;
            bodyB = bB;
            localAnchorA = bodyA.GetLocalPoint(anchor);
            localAnchorB = bodyB.GetLocalPoint(anchor);
            localAxisA = bodyA.GetLocalVector(axis);
        }
    }
}