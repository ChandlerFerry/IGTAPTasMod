namespace Box2D
{
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
