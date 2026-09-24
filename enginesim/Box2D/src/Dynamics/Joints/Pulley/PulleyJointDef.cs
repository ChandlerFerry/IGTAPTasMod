using System.Numerics;

namespace Box2D
{
    /// <summary>
    ///  Pulley joint definition. This requires two ground anchors,
    ///  two dynamic body anchor points, max lengths for each side,
    ///  and a pulley ratio.
    /// </summary>
    public class b2PulleyJointDef : b2JointDef
    {
        /// <summary>
        ///  The first ground anchor in world coordinates. This point never moves.
        /// </summary>
        public b2Vec2 GroundAnchorA;

        /// <summary>
        ///  The second ground anchor in world coordinates. This point never moves.
        /// </summary>
        public b2Vec2 GroundAnchorB;

        /// <summary>
        ///  The a reference length for the segment attached to body1.
        /// </summary>
        public float LengthA;

        /// <summary>
        ///  The a reference length for the segment attached to body2.
        /// </summary>
        public float LengthB;

        /// <summary>
        ///  The local anchor point relative to body1's origin.
        /// </summary>
        public b2Vec2 LocalAnchorA;

        /// <summary>
        ///  The local anchor point relative to body2's origin.
        /// </summary>
        public b2Vec2 LocalAnchorB;

        /// <summary>
        ///  The maximum length of the segment attached to body1.
        /// </summary>
        public float MaxLength1;

        /// <summary>
        ///  The maximum length of the segment attached to body2.
        /// </summary>
        public float MaxLength2;

        /// <summary>
        ///  The pulley ratio, used to simulate a block-and-tackle.
        /// </summary>
        public float Ratio;

        public b2PulleyJointDef()
        {
            GroundAnchorA = new b2Vec2(-1.0f, 1.0f);
            GroundAnchorB = new b2Vec2(1.0f, 1.0f);
            LocalAnchorA = new b2Vec2(-1.0f, 0.0f);
            LocalAnchorB = new b2Vec2(1.0f, 0.0f);
            Ratio = 1.0f;
            collideConnected = true;
        }

        /// Initialize the bodies, anchors, lengths, max lengths, and ratio using the world anchors.
        public void Initialize(
            b2Body body1,
            b2Body body2,
            b2Vec2 groundAnchor1,
            b2Vec2 groundAnchor2,
            b2Vec2 anchor1,
            b2Vec2 anchor2,
            float ratio)
        {
            bodyA = body1;
            bodyB = body2;
            GroundAnchorA = groundAnchor1;
            GroundAnchorB = groundAnchor2;
            LocalAnchorA = body1.GetLocalPoint(anchor1);
            LocalAnchorB = body2.GetLocalPoint(anchor2);
            b2Vec2 dA = anchor1 - groundAnchor1;
            LengthA = dA.Length();
            b2Vec2 dB = anchor2 - groundAnchor2;
            LengthB = dB.Length();
            Ratio = ratio;
            //Debug.Assert(ratio > b2Settings.FLT_EPSILON);
        }
    }
}