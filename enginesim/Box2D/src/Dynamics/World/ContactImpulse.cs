
namespace Box2D
{
    /// b2Contact impulses for reporting. Impulses are used instead of forces because
    /// sub-step forces may approach infinity for rigid body collisions. These
    /// match up one-to-one with the contact points in b2Manifold.
    public class b2ContactImpulse
    {
        public float[] normalImpulses = new float[b2Settings.MaxManifoldPoints];
        public float[] tangentImpulses = new float[b2Settings.MaxManifoldPoints];
    }
}