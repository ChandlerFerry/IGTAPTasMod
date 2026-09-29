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


/*
b2Position Correction Notes
=========================
I tried the several algorithms for position correction of the 2D revolute joint.
I looked at these systems:
- simple pendulum (1m diameter sphere on massless 5m stick) with initial angular velocity of 100 rad/s.
- suspension bridge with 30 1m long planks of length 1m.
- multi-link chain with 30 1m long links.

Here are the algorithms:

baumgarte - A fraction of the position error is added to the velocity error. There is no
separate position solver.

Pseudo Velocities - After the velocity solver and position integration,
the position error, Jacobian, and effective mass are recomputed. Then
the velocity constraints are solved with pseudo velocities and a fraction
of the position error is added to the pseudo velocity error. The pseudo
velocities are initialized to zero and there is no warm-starting. After
the position solver, the pseudo velocities are added to the positions.
This is also called the First Order b2World method or the b2Position LCP method.

Modified Nonlinear Gauss-Seidel (NGS) - Like Pseudo Velocities except the
position error is re-computed for each constraint and the positions are updated
after the constraint is solved. The radius vectors (aka Jacobians) are
re-computed too (otherwise the algorithm has horrible instability). The pseudo
velocity states are not needed because they are effectively zero at the beginning
of each iteration. Since we have the current position error, we allow the
iterations to terminate early if the error becomes smaller than b2_linearSlop.

Full NGS or just NGS - Like Modified NGS except the effective mass are re-computed
each time a constraint is solved.

Here are the results:
baumgarte - this is the cheapest algorithm but it has some stability problems,
especially with the bridge. The chain links separate easily close to the root
and they jitter as they struggle to pull together. This is one of the most common
methods in the field. The big drawback is that the position correction artificially
affects the momentum, thus leading to instabilities and false bounce. I used a
bias factor of 0.2. A larger bias factor makes the bridge less stable, a smaller
factor makes joints and contacts more spongy.

Pseudo Velocities - the is more stable than the baumgarte method. The bridge is
stable. However, joints still separate with large angular velocities. Drag the
simple pendulum in a circle quickly and the joint will separate. The chain separates
easily and does not recover. I used a bias factor of 0.2. A larger value lead to
the bridge collapsing when a heavy cube drops on it.

Modified NGS - this algorithm is better in some ways than baumgarte and Pseudo
Velocities, but in other ways it is worse. The bridge and chain are much more
stable, but the simple pendulum goes unstable at high angular velocities.

Full NGS - stable in all tests. The joints display good stiffness. The bridge
still sags, but this is better than infinite forces.

Recommendations
Pseudo Velocities are not really worthwhile because the bridge and chain cannot
recover from joint separation. In other cases the benefit over baumgarte is small.

Modified NGS is not a robust method for the revolute joint due to the violent
instability seen in the simple pendulum. Perhaps it is viable with other constraint
types, especially scalar constraints where the effective mass is a scalar.

This leaves baumgarte and Full NGS. baumgarte has small, but manageable instabilities
and is very fast. I don't think we can escape baumgarte, especially in highly
demanding cases where high constraint fidelity is not needed.

Full NGS is robust and easy on the eyes. I recommend this as an option for
higher fidelity simulation and certainly for suspension bridges and long chains.
Full NGS might be a good choice for ragdolls, especially motorized ragdolls where
joint separation can be problematic. The number of NGS iterations can be reduced
for better performance without harming robustness much.

Each joint in a can be handled differently in the position solver. So I recommend
a system where the user can select the algorithm on a per joint basis. I would
probably default to the slower Full NGS and let the user select the faster
baumgarte method in performance critical scenarios.
*/

/*
Cache Performance

The Box2D solvers are dominated by cache misses. Data structures are designed
to increase the number of cache hits. Much of misses are due to random access
to body data. The constraint structures are iterated over linearly, which leads
to few cache misses.

The _bodies are not accessed during iteration. Instead read only data, such as
the mass values are stored with the constraints. The mutable data are the constraint
impulses and the _bodies velocities/positions. The impulses are held inside the
constraint structures. The body velocities/positions are held in compact, temporary
arrays to increase the number of cache hits. Linear and angular velocity are
stored in a single array since multiple arrays lead to multiple misses.
*/

/*
2D Rotation

R = [cos(theta) -sin(theta)]
    [sin(theta) cos(theta) ]

thetaDot = omega

Let q1 = cos(theta), q2 = sin(theta).
R = [q1 -q2]
    [q2  q1]

q1Dot = -thetaDot * q2
q2Dot = thetaDot * q1

q1_new = q1_old - dt * w * q2
q2_new = q2_old + dt * w * q1
then normalize.

This might be faster than computing sin+cos.
However, we can compute sin+cos of the same angle fast.
*/

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Math = System.Math;

namespace Box2D
{
    public class b2Island
    {
        public readonly int m_bodyCapacity;
        public readonly int m_contactCapacity;
        private readonly b2Contact[] m_contacts;

        private readonly b2Position[] m_positions;
        private readonly b2Velocity[] m_velocities;

        public b2Body[] m_bodies;

        public int m_bodyCount;
        public int m_contactCount;

        // This island's contact solver, reused by every Solve/SolveTOI (see ContactSolver).
        private readonly ContactSolver m_contactSolver = new ContactSolver();

        public b2Island(int bodyCapacity, int contactCapacity)
        {
            m_bodyCapacity = bodyCapacity;
            m_contactCapacity = contactCapacity;

            m_bodies = new b2Body[bodyCapacity];
            m_contacts = new b2Contact[contactCapacity];

            m_velocities = new b2Velocity[m_bodyCapacity];
            m_positions = new b2Position[m_bodyCapacity];
        }

        // Whether this island can be reused for a step that would size a new one with these arguments.
        public bool Fits(int bodyCapacity, int contactCapacity)
            => m_bodyCapacity >= bodyCapacity && m_contactCapacity >= contactCapacity;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            m_bodyCount = 0;
            m_contactCount = 0;
        }

        public void Solve(in b2TimeStep step, in b2Vec2 gravity, bool allowSleep)
        {
            float h = step.dt;
            // Integrate velocities and apply damping.
            for (var i = 0; i < m_bodyCount; ++i)
            {
                b2Body b = m_bodies[i];

                b2Vec2 c = b.m_sweep.c;
                float a = b.m_sweep.a;
                b2Vec2 v = b.m_linearVelocity;
                float w = b.m_angularVelocity;

                b.m_sweep.c0 = b.m_sweep.c;
                b.m_sweep.a0 = b.m_sweep.a;

                if (b.m_type == b2BodyType.Dynamic)
                {
                    // Integrate velocities.
                    // 2.3.x (Unity); 2.4.1 is h * invMass * (gravityScale * mass * gravity + force), rounding differently.
                    v += h * (b.m_gravityScale * gravity + b.m_invMass * b.m_force);
                    w += h * b.m_invI * b.m_torque;

                    // Apply damping.
                    // ODE: dv/dt + c * v = 0
                    // Solution: v(t) = v0 * exp(-c * t)
                    // Time step: v(t + dt) = v0 * exp(-c * (t + dt)) = v0 * exp(-c * t) * exp(-c * dt) = v * exp(-c * dt)
                    // v2 = exp(-c * dt) * v1
                    // Pade approximation:
                    // v2 = v1 * 1 / (1 + c * dt)

                    v *= 1.0f / (1.0f + h * b.m_linearDamping);
                    w *= 1.0f / (1.0f + h * b.m_angularDamping);
                }

                m_positions[i].c = c;
                m_positions[i].a = a;
                m_velocities[i].v = v;
                m_velocities[i].w = w;
            }

            // Initialize velocity constraints.
            ContactSolverDef contactSolverDef;
            contactSolverDef.step = step;
            contactSolverDef.contacts = m_contacts;
            contactSolverDef.count = m_contactCount;
            contactSolverDef.positions = m_positions;
            contactSolverDef.velocities = m_velocities;

            ContactSolver contactSolver = m_contactSolver;
            contactSolver.Prepare(contactSolverDef);
            contactSolver.InitializeVelocityConstraints();

            if (step.warmStarting)
            {
                contactSolver.WarmStart();
            }

            // Solve velocity constraints
            for (var i = 0; i < step.velocityIterations; ++i)
            {
                contactSolver.SolveVelocityConstraints();
            }

            // Store impulses for warm starting
            contactSolver.StoreImpulses();

            // Integrate positions
            for (var i = 0; i < m_bodyCount; ++i)
            {
                b2Vec2 c = m_positions[i].c;
                float a = m_positions[i].a;
                b2Vec2 v = m_velocities[i].v;
                float w = m_velocities[i].w;

                // Check for large velocities
                b2Vec2 translation = h * v;
                if (b2Vec2.Dot(translation, translation) > b2Settings.maxTranslationSquared)
                {
                    float ratio = b2Settings.maxTranslation / translation.Length();
                    v *= ratio;
                }

                float rotation = h * w;
                if (rotation * rotation > b2Settings.maxRotationSquared)
                {
                    float ratio = b2Settings.maxRotation / MathF.Abs(rotation);
                    w *= ratio;
                }

                // Integrate
                c += h * v;
                a += h * w;

                m_positions[i].c = c;
                m_positions[i].a = a;
                m_velocities[i].v = v;
                m_velocities[i].w = w;
            }

            // Solve position constraints
            var positionSolved = false;
            for (var i = 0; i < step.positionIterations; ++i)
            {
                bool contactsOkay = contactSolver.SolvePositionConstraints();

                if (contactsOkay)
                {
                    // Exit early if the position errors are small.
                    positionSolved = true;
                    break;
                }
            }

            // Copy state buffers back to the bodies
            for (var i = 0; i < m_bodyCount; ++i)
            {
                b2Body body = m_bodies[i];
                body.m_sweep.c = m_positions[i].c;
                body.m_sweep.a = m_positions[i].a;
                body.m_linearVelocity = m_velocities[i].v;
                body.m_angularVelocity = m_velocities[i].w;
                body.SynchronizeTransform();
            }

            if (allowSleep)
            {
                float minSleepTime = float.MaxValue;

                float linTolSqr = b2Settings.linearSleepTolerance * b2Settings.linearSleepTolerance;
                float angTolSqr = b2Settings.angularSleepTolerance * b2Settings.angularSleepTolerance;

                for (var i = 0; i < m_bodyCount; ++i)
                {
                    b2Body b = m_bodies[i];
                    if (b.Type() == b2BodyType.Static)
                    {
                        continue;
                    }

                    if (!b.HasFlag(b2BodyFlags.AutoSleep) ||
                        b.m_angularVelocity * b.m_angularVelocity > angTolSqr ||
                        b2Vec2.Dot(b.m_linearVelocity, b.m_linearVelocity) > linTolSqr)
                    {
                        b.m_sleepTime = 0.0f;
                        minSleepTime = 0.0f;
                    }
                    else
                    {
                        b.m_sleepTime += h;
                        minSleepTime = Math.Min(minSleepTime, b.m_sleepTime);
                    }
                }

                if (minSleepTime >= b2Settings.timeToSleep && positionSolved)
                {
                    for (var i = 0; i < m_bodyCount; ++i)
                    {
                        b2Body b = m_bodies[i];
                        b.SetAwake(false);
                    }
                }
            }
        }

        public void SolveTOI(in b2TimeStep subStep, int toiIndexA, int toiIndexB)
        {
            //Debug.Assert(toiIndexA < _bodyCount);
            //Debug.Assert(toiIndexB < _bodyCount);

            for (var i = 0; i < m_bodyCount; i++)
            {
                b2Body b = m_bodies[i];
                m_positions[i].c = b.m_sweep.c;
                m_positions[i].a = b.m_sweep.a;
                m_velocities[i].v = b.m_linearVelocity;
                m_velocities[i].w = b.m_angularVelocity;
            }


            var contactSolverDef = new ContactSolverDef();
            contactSolverDef.contacts = m_contacts;
            contactSolverDef.count = m_contactCount;
            contactSolverDef.step = subStep;
            contactSolverDef.positions = m_positions;
            contactSolverDef.velocities = m_velocities;

            ContactSolver contactSolver = m_contactSolver;
            contactSolver.Prepare(contactSolverDef);

            for (var i = 0; i < subStep.positionIterations; ++i)
            {
                if (contactSolver.SolveTOIPositionConstraints(toiIndexA, toiIndexB))
                {
                    break;
                }
            }

            // Leap of faith to new safe state
            m_bodies[toiIndexA].m_sweep.c0 = m_positions[toiIndexA].c;
            m_bodies[toiIndexA].m_sweep.a0 = m_positions[toiIndexA].a;
            m_bodies[toiIndexB].m_sweep.c0 = m_positions[toiIndexB].c;
            m_bodies[toiIndexB].m_sweep.a0 = m_positions[toiIndexB].a;

            // No warm starting is needed for TOI events because warm
            // starting impulses were applied in the discrete solver.
            contactSolver.InitializeVelocityConstraints();

            // Solve velocity constraints.
            for (var i = 0; i < subStep.velocityIterations; ++i)
            {
                contactSolver.SolveVelocityConstraints();
            }

            // Don't store the TOI contact forces for warm starting
            // because they can be quite large.

            float h = subStep.dt;

            // Integrate positions.
            for (var i = 0; i < m_bodyCount; ++i)
            {
                b2Vec2 c = m_positions[i].c;
                float a = m_positions[i].a;
                b2Vec2 v = m_velocities[i].v;
                float w = m_velocities[i].w;

                // Check for large velocities.
                b2Vec2 translation = h * v;
                if (b2Vec2.Dot(translation, translation) > b2Settings.maxTranslationSquared)
                {
                    float ratio = b2Settings.maxTranslation / translation.Length();
                    v *= ratio;
                }

                float rotation = h * w;
                if (rotation * rotation > b2Settings.maxRotationSquared)
                {
                    float ratio = b2Settings.maxRotation / MathF.Abs(rotation);
                    w *= ratio;
                }

                // Integrate
                c += h * v;
                a += h * w;

                m_positions[i].c = c;
                m_positions[i].a = a;
                m_velocities[i].v = v;
                m_velocities[i].w = w;

                // Sync bodies
                b2Body body = m_bodies[i];
                body.m_sweep.c = c;
                body.m_sweep.a = a;
                body.m_linearVelocity = v;
                body.m_angularVelocity = w;
                body.SynchronizeTransform();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(b2Body body)
        {
            //Debug.Assert(_bodyCount < _bodyCapacity);
            body.m_islandIndex = m_bodyCount;
            m_bodies[m_bodyCount++] = body;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(b2Contact contact)
        {
            //Debug.Assert(_contactCount < _contactCapacity);
            m_contacts[m_contactCount++] = contact;
        }

    }
}