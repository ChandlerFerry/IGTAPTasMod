using MathF = System.Math;
using Float = System.Double;

namespace Box2D
{
    /// <summary>
    ///  Gear joint definition. This definition requires two existing
    ///  revolute or prismatic joints (any combination will work).
    ///  The provided joints must attach a dynamic body to a static body.
    /// </summary>
    public class b2GearJointDef : b2JointDef
    {
        /// <summary>
        ///  The first revolute/prismatic joint attached to the gear joint.
        /// </summary>
        public b2Joint Joint1;

        /// <summary>
        ///  The second revolute/prismatic joint attached to the gear joint.
        /// </summary>
        public b2Joint Joint2;

        /// <summary>
        ///  The gear ratio.
        ///  @see b2GearJoint for explanation.
        /// </summary>
        public float Ratio;

        public b2GearJointDef() => Ratio = 1.0f;
    }
}