
namespace Box2D
{
    /// <summary>
    ///  b2Joint definitions are used to construct joints.
    /// </summary>
    public class b2JointDef
    {
        /// <summary>
        ///  The first attached body.
        /// </summary>
        public b2Body bodyA;

        /// <summary>
        ///  The second attached body.
        /// </summary>
        public b2Body bodyB;

        /// <summary>
        ///  Set this flag to true if the attached bodies should collide.
        /// </summary>
        public bool collideConnected;

        /// <summary>
        ///  Use this to attach application specific data to your joints.
        /// </summary>
        public object UserData;

        public b2JointDef()
        {
            UserData = null;
            bodyA = null;
            bodyB = null;
            collideConnected = false;
        }
    }
}