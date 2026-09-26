using System;

namespace UnityEngine
{
    public struct Quaternion : IEquatable<Quaternion>
    {
        public float x, y, z, w;

        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }

        public static Quaternion identity { get { return new Quaternion(0f, 0f, 0f, 1f); } }

        public static Quaternion operator *(Quaternion lhs, Quaternion rhs)
        {
            return new Quaternion
            {
                x = lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
                y = lhs.w * rhs.y + lhs.y * rhs.w + lhs.z * rhs.x - lhs.x * rhs.z,
                z = lhs.w * rhs.z + lhs.z * rhs.w + lhs.x * rhs.y - lhs.y * rhs.x,
                w = lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z
            };
        }

        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            float num = rotation.x * 2f;
            float num2 = rotation.y * 2f;
            float num3 = rotation.z * 2f;
            float num4 = rotation.x * num;
            float num5 = rotation.y * num2;
            float num6 = rotation.z * num3;
            float num7 = rotation.x * num2;
            float num8 = rotation.x * num3;
            float num9 = rotation.y * num3;
            float num10 = rotation.w * num;
            float num11 = rotation.w * num2;
            float num12 = rotation.w * num3;
            Vector3 result = default(Vector3);
            result.x = (1f - (num5 + num6)) * point.x + (num7 - num12) * point.y + (num8 + num11) * point.z;
            result.y = (num7 + num12) * point.x + (1f - (num4 + num6)) * point.y + (num9 - num10) * point.z;
            result.z = (num8 - num11) * point.x + (num9 + num10) * point.y + (1f - (num4 + num5)) * point.z;
            return result;
        }

        public static float Dot(Quaternion a, Quaternion b) { return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w; }
        static bool IsEqualUsingDot(float dot) { return dot > 0.999999f; }
        public static bool operator ==(Quaternion lhs, Quaternion rhs) { return IsEqualUsingDot(Dot(lhs, rhs)); }
        public static bool operator !=(Quaternion lhs, Quaternion rhs) { return !(lhs == rhs); }
        public override int GetHashCode() { return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2) ^ (w.GetHashCode() >> 1); }
        public override bool Equals(object other) { return other is Quaternion q && Equals(q); }
        public bool Equals(Quaternion other) { return x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w); }

        public static Quaternion Euler(float x, float y, float z) { return Euler(new Vector3(x, y, z)); }

        public static Quaternion Euler(Vector3 euler)
        {
            float d2r = MathF.PI / 180f * 0.5f;
            float cx = MathF.Cos(euler.x * d2r), sx = MathF.Sin(euler.x * d2r);
            float cy = MathF.Cos(euler.y * d2r), sy = MathF.Sin(euler.y * d2r);
            float cz = MathF.Cos(euler.z * d2r), sz = MathF.Sin(euler.z * d2r);
            var qx = new Quaternion(sx, 0f, 0f, cx);
            var qy = new Quaternion(0f, sy, 0f, cy);
            var qz = new Quaternion(0f, 0f, sz, cz);
            return qy * qx * qz;
        }

        public Vector3 eulerAngles
        {
            get
            {
                float sinX = 2f * (w * x - y * z);
                float ex = MathF.Abs(sinX) >= 1f ? MathF.CopySign(90f, sinX) : MathF.Asin(sinX) * 57.29578f;
                float ey = MathF.Atan2(2f * (w * y + x * z), 1f - 2f * (x * x + y * y)) * 57.29578f;
                float ez = MathF.Atan2(2f * (w * z + x * y), 1f - 2f * (x * x + z * z)) * 57.29578f;
                return new Vector3(Positive(ex), Positive(ey), Positive(ez));
            }
            set { this = Euler(value); }
        }

        static float Positive(float degrees) { return degrees < 0f ? degrees + 360f : degrees; }
    }
}
