namespace Box2D
{
    // C++ b2Max/b2Min/b2Abs/b2Clamp literal semantics (b2_math.h templates):
    // b2Max(a, b) = a > b ? a : b; b2Min(a, b) = a < b ? a : b;
    // b2Abs(a) = a > 0 ? a : -a; b2Clamp(a, low, high) = b2Max(low, b2Min(a, high)).
    // These differ from System.Math in NaN / signed-zero edge cases; physics code
    // must use these forms to stay bit-faithful to the C++ engine.
    public static partial class Math
    {
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Abs(int a) => a > 0 ? a : -a;
        public static float Abs(float a) => a > 0.0f ? a : -a;
        public static float Clamp(float a, float low, float high) => Max(low, Min(a, high));
        public static int Clamp(int a, int low, int high) => Max(low, Min(a, high));
    }
}
