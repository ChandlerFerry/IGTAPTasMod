
namespace Box2D
{
    /// <summary>
    ///  Implement this class to provide collision filtering. In other words, you can implement
    ///  this class if you want finer control over contact creation.
    /// </summary>
    public class b2ContactFilter
    {
        /// <summary>
        ///  Return true if contact calculations should be performed between these two shapes.
        ///  If you implement your own collision filter you may want to build from this implementation.
        ///  @warning for performance reasons this is only called when the AABBs begin to overlap.
        /// </summary>
        public virtual bool ShouldCollide(b2Fixture fixtureA, b2Fixture fixtureB)
        {
            b2Filter filterA = fixtureA.m_filter;
            b2Filter filterB = fixtureB.m_filter;

            if (filterA.groupIndex == filterB.groupIndex && filterA.groupIndex != 0)
            {
                return filterA.groupIndex > 0;
            }

            bool collide = (filterA.maskBits & filterB.categoryBits) != 0 && (filterA.categoryBits & filterB.maskBits) != 0;
            return collide;
        }
    }
}