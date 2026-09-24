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

namespace Box2D
{
    public class b2Settings
    {
        public static int MaxTOIIterations = 20;

        public static float aabbMultiplier = 4.0f;
        public static float aabbExtension = 0.1f;
        public static int maxTOIContacts = 32;
        public static int maxSubSteps = 8;

        public static float FLT_EPSILON = 1.192092896e-07f; //1.192092896e-07F;//smallest such that 1.0f+FLT_EPSILON != 1.0f
        public static float FLT_EPSILON_SQUARED => FLT_EPSILON * FLT_EPSILON;

        public static float Pib2 = 3.14159265359f;        // Original code. Comes out at 3.1415927f
        public static float Pi = 3.14159265359f;                // Also 3.1415927f
        public static float Pi2 = 3.14159265359f; // Also displayed as "3.1415927f"
        public static float Tau = 2f * Pi;

        // Global tuning constants based on meters-kilograms-seconds (MKS) units.

        // Collision
        public static int MaxManifoldPoints = 2;
        public static int MaxPolygonVertices = 8;

        // Dynamics

        /// <summary>
        ///  A small length used as a collision and constraint tolerance. Usually it is
        ///  chosen to be numerically significant, but visually insignificant.
        /// </summary>
        public static float linearSlop = 0.005f; // 0.5 cm

        /// <summary>
        ///  A small angle used as a collision and constraint tolerance. Usually it is
        ///  chosen to be numerically significant, but visually insignificant.
        /// </summary>
        public static float angularSlop = 2.0f / 180.0f * Pi; // 2 degrees

        /// <summary>
        ///  The radius of the polygon/edge shape skin. This should not be modified. Making
        ///  this smaller means polygons will have and insufficient for continuous collision.
        ///  Making it larger may create artifacts for vertex collision.
        /// </summary>
        public static float polygonRadius = 2.0f * linearSlop;

        /// <summary>
        ///  This must be larger than linearSlop.
        /// </summary>

        /// <summary>
        ///  Maximum number of contacts to be handled to solve a TOI island.
        /// </summary>
        public static int MaxTOIContactsPerIsland = 32;

        /// <summary>
        ///  Maximum number of joints to be handled to solve a TOI island.
        /// </summary>
        public static int MaxTOIJointsPerIsland = 32;

        /// <summary>
        ///  A velocity threshold for elastic collisions. Any collision with a relative linear
        ///  velocity below this threshold will be treated as inelastic.
        /// </summary>
        public static float velocityThreshold = 1.0f; // 1 m/s

        /// <summary>
        ///  The maximum linear position correction used when solving constraints.
        ///  This helps to prevent overshoot.
        /// </summary>
        public static float maxLinearCorrection = 0.2f; // 20 cm

        /// <summary>
        ///  The maximum angular position correction used when solving constraints.
        ///  This helps to prevent overshoot.
        /// </summary>
        public static float maxAngularCorrection = 8.0f / 180.0f * Pi; // 8 degrees

        /// <summary>
        ///  The maximum linear translation of a body per step. This limit is very large and is used
        ///  to prevent numerical problems. You shouldn't need to adjust this.
        /// </summary>
        public static float maxTranslation = 2.0f;

        public static float maxTranslationSquared => maxTranslation * maxTranslation;

        /// <summary>
        ///  The maximum angular velocity of a body. This limit is very large and is used
        ///  to prevent numerical problems. You shouldn't need to adjust this.
        /// </summary>
        public static float maxRotation = 0.5f * Pi;

        public static float maxRotationSquared => maxRotation * maxRotation;

        /// <summary>
        ///  This scale factor controls how fast overlap is resolved. Ideally this would be 1 so
        ///  that overlap is removed in one time step. However using values close to 1 often lead to overshoot.
        /// </summary>
        public static float baumgarte = 0.2f;

        // Sleep

        /// <summary>
        ///  The time that a body must be still before it will go to sleep.
        /// </summary>
        public static float timeToSleep = 0.5f; // half a second

        /// <summary>
        ///  A body cannot sleep if its linear velocity is above this tolerance.
        /// </summary>
        public static float linearSleepTolerance = 0.01f; // 1 cm/s

        /// <summary>
        ///  A body cannot sleep if its angular velocity is above this tolerance.
        /// </summary>
        public static float angularSleepTolerance = 2.0f / 180.0f * Pi; // 2 degrees/s

        public static bool BlockSolve = true;
        public static bool useLegacyGravityIntegration = true;
        public static bool UseLegacyEdgePolygon = true;
        public static float toiBaumgarte = 0.75f;

        public static float FORCE_SCALE(float x) => x;

        public static float FORCE_INV_SCALE(float x) => x;

        /// <summary>
        ///  Friction mixing law. Feel free to customize this.
        /// </summary>
        public static float MixFriction(float friction1, float friction2) =>
            MathF.Sqrt(friction1 * friction2);

        /// <summary>
        ///  Restitution mixing law. Feel free to customize this.
        /// </summary>
        public static float MixRestitution(float restitution1, float restitution2) =>
            restitution1 > restitution2 ? restitution1 : restitution2;
    }
}