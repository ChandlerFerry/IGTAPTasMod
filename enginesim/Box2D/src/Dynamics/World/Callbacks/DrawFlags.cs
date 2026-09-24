using System;

namespace Box2D
{
    [Flags]
    public enum b2DrawFlags
    {
        b2Shape = 0x0001,       // draw shapes
        b2Joint = 0x0002,       // draw joint connections
        Aabb = 0x0008,        // draw axis aligned bounding boxes
        Pair = 0x0020,        // draw broad-phase pairs
        CenterOfMass = 0x0040 // draw center of mass frame
    }
}