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
using System.Runtime.InteropServices;

namespace Box2D
{
    public static partial class Math
    {
        /// <summary>
        ///  This function is used to ensure that a floating point number is
        ///  not a NaN or infinity.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValid(float x) =>
            !(float.IsNaN(x) || float.IsNegativeInfinity(x) || float.IsPositiveInfinity(x));

        /// <summary>
        ///  C++ b2MulT(b2Mat22, b2Vec2) literal: (b2Dot(v, ex), b2Dot(v, ey)).
        ///  (The previous port inverted the matrix first, which rounds differently
        ///  and misbehaves on singular matrices.)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 MulT(Matrix3x2 A, b2Vec2 v)
        {
            b2Vec2 ex = new b2Vec2(A.M11, A.M12);
            b2Vec2 ey = new b2Vec2(A.M21, A.M22);
            return new b2Vec2(b2Vec2.Dot(v, ex), b2Vec2.Dot(v, ey));
        }


        /// <summary>
        ///  A * B
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Matrix3x2 Mul(Matrix3x2 A, Matrix3x2 B)
        {
            // C++ b2Mul(b2Mat22 A, b2Mat22 B) = (b2Mul(A, B.ex), b2Mul(A, B.ey)).
            Matrix3x2 C;
            C.M11 = A.M11 * B.M11 + A.M21 * B.M12;
            C.M12 = A.M12 * B.M11 + A.M22 * B.M12;
            C.M21 = A.M11 * B.M21 + A.M21 * B.M22;
            C.M22 = A.M12 * B.M21 + A.M22 * B.M22;
            C.M31 = 0.0f;
            C.M32 = 0.0f;
            return C;
        }

        /// <summary>
        ///  A^T * B
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Matrix3x2 MulT(Matrix3x2 A, Matrix3x2 B)
        {
            // C++ b2MulT(b2Mat22 A, b2Mat22 B): c1 = (dot(A.ex,B.ex), dot(A.ey,B.ex)),
            // c2 = (dot(A.ex,B.ey), dot(A.ey,B.ey)).
            Matrix3x2 C;
            C.M11 = A.M11 * B.M11 + A.M12 * B.M12;
            C.M12 = A.M21 * B.M11 + A.M22 * B.M12;
            C.M21 = A.M11 * B.M21 + A.M12 * B.M22;
            C.M22 = A.M21 * B.M21 + A.M22 * B.M22;
            C.M31 = 0.0f;
            C.M32 = 0.0f;
            return C;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 Mul(b2Transform T, b2Vec2 v) =>
            new b2Vec2((T.q.M11 * v.x - T.q.M12 * v.y) + T.p.x, (T.q.M12 * v.x + T.q.M11 * v.y) + T.p.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Vec2 MulT(b2Transform T, b2Vec2 v) => MulT(T.q, v - T.p);



        // v2 = A.q.b2Rot(B.q.b2Rot(v1) + B.p) + A.p
        //    = (A.q * B.q).b2Rot(v1) + A.q.b2Rot(B.p) + A.p
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Transform Mul(in b2Transform A, in b2Transform B)
        {
            b2Transform C;
            C.q = Mul(A.q, B.q);
            C.p = A.p + b2Vec2.Transform(B.p, A.q); //Mul(A.q, B.p);
            return C;
        }

        // v2 = A.q' * (B.q * v1 + B.p - A.p)
        //    = A.q' * B.q * v1 + A.q' * (B.p - A.p)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static b2Transform MulT(in b2Transform A, in b2Transform B)
        {
            b2Transform C;
            C.q = MulT(A.q, B.q);
            C.p = MulT(A.q, B.p - A.p);
            return C;
        }
    }
}