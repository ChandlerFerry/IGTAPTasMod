/*
  Box2D Copyright © 2020 Ben Ukhanov & Hugh Phoenix-Hulme https://github.com/benzuk/box2d-netstandard
  Box2DX Copyright (c) 2009 Ihar Kalasouski http://code.google.com/p/box2dx
  
// MIT License

// Copyright (c) 2019 Erin Catto

// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:

// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
*/

using System.Diagnostics;
using System.Numerics;

namespace Box2D
{
    /// <summary>
    ///  A manifold for two touching convex shapes.
    /// </summary>
    [DebuggerDisplay("localNormal = {" + nameof(localNormal) + "}")]
    public class b2Manifold
    {
        public b2Vec2 localNormal;

        /// <summary>
        ///  Usage depends on manifold type.
        /// </summary>
        public b2Vec2 localPoint;

        /// <summary>
        ///  The number of manifold points.
        /// </summary>
        public int pointCount;

        /// <summary>
        ///  The points of contact.
        /// </summary>
        public b2ManifoldPoint[ /*b2Settings.MaxManifoldPoints*/] points = new b2ManifoldPoint[b2Settings.MaxManifoldPoints];

        public b2ManifoldType type;

        /// <summary>Back to a new manifold's values (b2Contact.Update reuses two per contact instead of allocating).</summary>
        internal void Reset()
        {
            localNormal = default;
            localPoint = default;
            pointCount = 0;
            type = default;
        }

        /// <summary>Point i as a new b2ManifoldPoint: the slot's own object, zeroed.</summary>
        internal b2ManifoldPoint NewPoint(int i)
        {
            b2ManifoldPoint p = points[i] ??= new b2ManifoldPoint();
            p.id.key = 0;
            p.localPoint = default;
            p.normalImpulse = 0f;
            p.tangentImpulse = 0f;
            return p;
        }
    }
}