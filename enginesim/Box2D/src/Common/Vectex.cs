using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Box2D
{
    public static class Vectex // vector extensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetIdx(in this b2Vec2 candidate, in int n) => n == 0 ? candidate.X : candidate.Y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Cross(float s, b2Vec2 a) => new b2Vec2(-s * a.Y, s * a.X);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Cross(b2Vec2 a, float s) => new b2Vec2(s * a.Y, -s * a.X);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Cross(b2Vec2 a, b2Vec2 b) => a.X * b.Y - a.Y * b.X;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValid(this b2Vec2 candidate) => Math.IsValid(candidate.X) && Math.IsValid(candidate.Y);

        [Obsolete("This is now a System.Numerics.b2Vec2, and cannot be mutated this way. Please create a new b2Vec2 and assign it to the property or field you're trying to modify.",
                  true)]
        public static void Set(this b2Vec2 v, float x, float y)
        { }

        [Obsolete("This is now a System.Numerics.b2Vec2, and cannot be mutated this way. Please create a new b2Vec2 and assign it to the property or field you're trying to modify.",
                  true)]
        public static void Set(this b2Vec2 v, float x)
        { }

        [Obsolete("This is now a System.Numerics.b2Vec2, and cannot be mutated this way. Please create a new b2Vec2 and assign it to the property or field you're trying to modify.",
                  true)]
        public static void SetZero(this b2Vec2 v)
        { }
    }
}