using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Box2D
{
    public struct b2Rot
    {
        /// Sine and cosine
        public float s;

        /// Sine and cosine
        public float c;

        /// Initialize from an angle in radians
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Rot(float angle)
        {
            s = MathF.Sin(angle);
            c = MathF.Cos(angle);
        }

        /// Set using an angle in radians.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(float angle)
        {
            s = MathF.Sin(angle);
            c = MathF.Cos(angle);
        }

        /// Set to the identity rotation
        private void SetIdentity()
        {
            s = 0.0f;
            c = 1.0f;
        }

        /// Get the angle in radians
        private float GetAngle() => MathF.Atan2(s, c);

        /// Get the x-axis
        private b2Vec2 GetXAxis() => new b2Vec2(c, s);

        /// Get the u-axis
        private b2Vec2 GetYAxis() => new b2Vec2(-s, c);
    }
}