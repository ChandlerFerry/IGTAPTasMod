using System;
using System.Runtime.CompilerServices;

namespace Box2D
{
    public struct b2Vec2 : IEquatable<b2Vec2>
    {
        public float x;
        public float y;

        public b2Vec2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public float X
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => x;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => x = value;
        }

        public float Y
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => y;
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => y = value;
        }

        public static readonly b2Vec2 Zero = new b2Vec2(0f, 0f);
        public static readonly b2Vec2 One = new b2Vec2(1f, 1f);
        public static readonly b2Vec2 UnitX = new b2Vec2(1f, 0f);
        public static readonly b2Vec2 UnitY = new b2Vec2(0f, 1f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Length() => MathF.Sqrt(x * x + y * y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float LengthSquared() => x * x + y * y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Dot(b2Vec2 a, b2Vec2 b) => a.x * b.x + a.y * b.y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Distance(b2Vec2 a, b2Vec2 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float DistanceSquared(b2Vec2 a, b2Vec2 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            return dx * dx + dy * dy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Normalize(b2Vec2 value)
        {
            float length = MathF.Sqrt(value.x * value.x + value.y * value.y);
            if (length < b2Settings.FLT_EPSILON)
            {
                return value;
            }

            float invLength = 1.0f / length;
            value.x *= invLength;
            value.y *= invLength;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Negate(b2Vec2 value) => new b2Vec2(-value.x, -value.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Min(b2Vec2 a, b2Vec2 b) => new b2Vec2(a.x < b.x ? a.x : b.x, a.y < b.y ? a.y : b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Max(b2Vec2 a, b2Vec2 b) => new b2Vec2(a.x > b.x ? a.x : b.x, a.y > b.y ? a.y : b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Abs(b2Vec2 value) => new b2Vec2(MathF.Abs(value.x), MathF.Abs(value.y));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Reflect(b2Vec2 vector, b2Vec2 normal)
        {
            float dot = vector.x * normal.x + vector.y * normal.y;
            return new b2Vec2(vector.x - 2f * dot * normal.x, vector.y - 2f * dot * normal.y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Transform(b2Vec2 v, b2Rot q) =>
            new b2Vec2(q.c * v.x - q.s * v.y, q.s * v.x + q.c * v.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Transform(b2Vec2 v, System.Numerics.Matrix3x2 m) =>
            new b2Vec2(m.M11 * v.x + m.M21 * v.y, m.M12 * v.x + m.M22 * v.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 MulT(b2Vec2 v, System.Numerics.Matrix3x2 m) =>
            new b2Vec2(v.x * m.M11 + v.y * m.M12, v.x * m.M21 + v.y * m.M22);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 operator +(b2Vec2 a, b2Vec2 b) => new b2Vec2(a.x + b.x, a.y + b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 operator -(b2Vec2 a, b2Vec2 b) => new b2Vec2(a.x - b.x, a.y - b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 operator -(b2Vec2 a) => new b2Vec2(-a.x, -a.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 operator *(b2Vec2 a, b2Vec2 b) => new b2Vec2(a.x * b.x, a.y * b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 operator *(b2Vec2 a, float s) => new b2Vec2(a.x * s, a.y * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 operator *(float s, b2Vec2 a) => new b2Vec2(a.x * s, a.y * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 operator /(b2Vec2 a, b2Vec2 b) => new b2Vec2(a.x / b.x, a.y / b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 operator /(b2Vec2 a, float s) => new b2Vec2(a.x / s, a.y / s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(b2Vec2 a, b2Vec2 b) => a.x == b.x && a.y == b.y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(b2Vec2 a, b2Vec2 b) => a.x != b.x || a.y != b.y;

        public bool Equals(b2Vec2 other) => x == other.x && y == other.y;

        public override bool Equals(object obj) => obj is b2Vec2 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(x, y);

        public override string ToString() => $"({x}, {y})";
    }
}
