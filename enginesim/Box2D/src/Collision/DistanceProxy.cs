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
    public struct b2DistanceProxy
    {
        // A circle's, an edge's and a chain child's vertices go into the caller's two-slot buffer (Set's last argument),
        // which must outlive every use of this proxy and not be shared with the other proxy of the same query.
        public b2Vec2[] _vertices;
        public int _count;
        public float _radius;

        private void Set(in b2Vec2[] vertices, int count, float radius)
        {
            _vertices = vertices;
            _count = count;
            _radius = radius;
        }

        public void Set(in b2Shape shape, in int index, b2Vec2[] buffer)
        {
            switch (shape)
            {
                case b2CircleShape circle:
                    buffer[0] = circle.m_p;
                    _vertices = buffer;
                    _count = 1;
                    _radius = circle.m_radius;
                    break;
                case b2PolygonShape polygon:
                    _vertices = polygon.m_vertices;
                    _count = polygon.m_count;
                    _radius = polygon.m_radius;
                    break;
                case b2ChainShape chain:
                    buffer[0] = chain.m_vertices[index];
                    if (index + 1 < chain.m_count)
                    {
                        buffer[1] = chain.m_vertices[index + 1];
                    }
                    else
                    {
                        buffer[1] = chain.m_vertices[0];
                    }

                    _vertices = buffer;
                    _count = 2;
                    _radius = chain.m_radius;

                    break;
                case b2EdgeShape edge:
                    buffer[0] = edge.m_vertex1;
                    buffer[1] = edge.m_vertex2;
                    _vertices = buffer;
                    _count = 2;
                    _radius = edge.m_radius;
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public int GetSupport(b2Vec2 d)
        {
            var bestIndex = 0;
            float bestValue = b2Vec2.Dot(_vertices[0], d);

            for (var i = 1; i < _count; ++i)
            {
                float value = b2Vec2.Dot(_vertices[i], d);
                if (value > bestValue)
                {
                    bestIndex = i;
                    bestValue = value;
                }
            }

            return bestIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public b2Vec2 GetVertex(int index) => _vertices[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSupport(in b2Vec2 d)
        {
            var bestIndex = 0;
            float bestValue = b2Vec2.Dot(_vertices[0], d);

            for (var i = 1; i < _count; ++i)
            {
                float value = b2Vec2.Dot(_vertices[i], d);
                if (value > bestValue)
                {
                    bestIndex = i;
                    bestValue = value;
                }
            }

            return bestIndex;
        }

    }
}