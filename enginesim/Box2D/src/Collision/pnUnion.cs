using System.Runtime.InteropServices;

namespace Box2D
{
    [StructLayout(LayoutKind.Explicit)]
    public struct pnUnion
    {
        [FieldOffset(0)]
        public int parent;

        [FieldOffset(0)]
        public int next;
    }
}