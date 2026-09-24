using System;
using System.Numerics;

namespace Box2D
{
    public class b2WeldJointDef : b2JointDef
    {
        /// <summary>
        ///  The rotational damping in N*m*s
        /// </summary>
        public float damping;

        [Obsolete("Use b2Joint.AngularStiffness to get stiffness & damping values", true)]
        public float dampingRatio;

        [Obsolete("Use b2Joint.AngularStiffness to get stiffness & damping values", true)]
        public float frequencyHz;

        public b2Vec2 localAnchorA;
        public b2Vec2 localAnchorB;
        public float referenceAngle;

        /// <summary>
        ///  The rotational stiffness in N*m
        ///  Disable softness with a value of 0
        /// </summary>
        public float stiffness;
    }
}