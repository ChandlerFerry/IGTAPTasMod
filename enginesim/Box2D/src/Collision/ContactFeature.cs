namespace Box2D
{
    /// The features that intersect to form the contact point
    /// This must be 4 bytes or less.
    public struct b2ContactFeature
    {
        public byte indexA;

        ///< Feature index on shapeA
        public byte indexB;

        ///< Feature index on shapeB
        public byte typeA;

        ///< The feature type on shapeA
        public byte typeB; ///< The feature type on shapeB
	}
}