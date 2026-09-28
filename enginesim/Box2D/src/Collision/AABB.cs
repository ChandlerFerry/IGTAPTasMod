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

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Box2D
{
    /// <summary>
    ///  An axis aligned bounding box.
    /// </summary>
    public struct b2AABB
    {
        /// <summary>
        ///  The lower vertex
        /// </summary>
        public b2Vec2 lowerBound;

        /// <summary>
        ///  The upper vertex
        /// </summary>
        public b2Vec2 upperBound;

        public b2Vec2 LowerBound
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => lowerBound;
        }

        public b2Vec2 UpperBound
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => upperBound;
        }

        public b2Vec2 Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => upperBound - lowerBound;
        }

        /// Get the center of the b2AABB.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetCenter() => 0.5f * (lowerBound + upperBound);

        /// Get the extents of the b2AABB (half-widths).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetExtents() => 0.5f * (upperBound - lowerBound);

        /// Get the perimeter length
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetPerimeter()
        {
            float wx = upperBound.X - lowerBound.X;
            float wy = upperBound.Y - lowerBound.Y;
            return 2.0f * (wx + wy);
        }

        /// Combine an b2AABB into this one.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Combine(in b2AABB aabb)
        {
            lowerBound = b2Vec2.Min(lowerBound, aabb.lowerBound);
            upperBound = b2Vec2.Max(upperBound, aabb.upperBound);
        }

        /// Combine two AABBs into this one.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2AABB Combine(in b2AABB aabb1, in b2AABB aabb2)
        {
            b2AABB result = default;
            result.lowerBound = b2Vec2.Min(aabb1.lowerBound, aabb2.lowerBound);
            result.upperBound = b2Vec2.Max(aabb1.upperBound, aabb2.upperBound);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2AABB Enlarged(float amount)
        {
            b2Vec2 vecAmt = new b2Vec2(amount, amount);
            return new b2AABB(lowerBound - vecAmt, upperBound + vecAmt);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Intersects(in b2AABB other)
        {
            return other.lowerBound.Y <= this.upperBound.Y &&
                   other.upperBound.Y >= this.lowerBound.Y &&
                   other.upperBound.X >= this.lowerBound.X &&
                   other.lowerBound.X <= this.upperBound.X;
        }

        /// Does this aabb contain the provided b2AABB.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(in b2AABB aabb)
        {
            var result = true;
            result = result && lowerBound.X <= aabb.lowerBound.X;
            result = result && lowerBound.Y <= aabb.lowerBound.Y;
            result = result && aabb.upperBound.X <= upperBound.X;
            result = result && aabb.upperBound.Y <= upperBound.Y;
            return result;
        }

        private bool RayCast(out b2RayCastOutput output, in b2RayCastInput input)
        {
            output = default;
            float tmin = float.MinValue;
            float tmax = float.MaxValue;

            b2Vec2 p = input.p1;
            b2Vec2 d = input.p2 - input.p1;
            var absD = b2Vec2.Abs(d);

            b2Vec2 normal = b2Vec2.Zero;

            for (var i = 0; i < 2; ++i)
            {
                if (absD.GetIdx(i) < b2Settings.FLT_EPSILON)
                {
                    // Parallel.
                    if (p.GetIdx(i) < lowerBound.GetIdx(i) || upperBound.GetIdx(i) < p.GetIdx(i))
                    {
                        return false;
                    }
                }
                else
                {
                    float inv_d = 1.0f / d.GetIdx(i);
                    float t1 = (lowerBound.GetIdx(i) - p.GetIdx(i)) * inv_d;
                    float t2 = (upperBound.GetIdx(i) - p.GetIdx(i)) * inv_d;

                    // Sign of the normal vector.
                    float s = -1.0f;

                    if (t1 > t2)
                    {
                        float temp = t1;
                        t1 = t2;
                        t2 = temp;
                        s = 1.0f;
                    }

                    // Push the min up
                    if (t1 > tmin)
                    {
                        normal = new b2Vec2(i == 0 ? s : 0, i == 1 ? s : 0);
                        tmin = t1;
                    }

                    // Pull the max down
                    tmax = MathF.Min(tmax, t2);

                    if (tmin > tmax)
                    {
                        return false;
                    }
                }
            }

            // Does the ray start inside the box?
            // Does the ray intersect beyond the max fraction?
            if (tmin < 0.0f || input.maxFraction < tmin)
            {
                return false;
            }

            // Intersection.
            output.fraction = tmin;
            output.normal = normal;
            return true;
        }

        private bool IsValid()
        {
            b2Vec2 d = upperBound - lowerBound;
            bool valid = d.X >= 0.0f && d.Y >= 0.0f;
            valid = valid && lowerBound.IsValid() && upperBound.IsValid();
            return valid;
        }

        public b2AABB(b2Vec2 lowerBound, b2Vec2 upperBound)
        {
            this.lowerBound = lowerBound;
            this.upperBound = upperBound;
        }
    }
}
