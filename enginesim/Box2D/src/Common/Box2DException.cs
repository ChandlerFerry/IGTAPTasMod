using System;

namespace Box2D
{
    public class Box2DException : Exception
    {
        public Box2DException(string message) : base(message)
        { }
    }
}