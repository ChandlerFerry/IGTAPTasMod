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



namespace Box2D
{
    /// <summary>
    ///  Delegate of b2World.
    /// </summary>
    public class ContactManager
    {
        public b2BroadPhase m_broadPhase;
        public int m_contactCount;
        public b2ContactFilter m_contactFilter;
        // Unity's world contact list is oldest first: a new contact goes to the end (vanilla Box2D puts it at the
        // head). It is the order Collide updates contacts in (so contacts that start touching in the same step report
        // in it) and SolveTOI's tie-break between equally early impacts (the first wins): turbo traces of a player
        // meeting two coplanar wall shapes at once (diverge-c5-13.14 ticks 206, 305, 412) and of a corner of four
        // shapes (tick 884) match only this. Bodies' contact edge lists stay newest first (the island's order).
        public b2Contact m_contactList;
        public b2Contact m_contactTail;
        public b2ContactListener m_contactListener;
        // UpdatePairs' callback, made once (a method group argument is a new delegate per call).
        private readonly System.Action<object, object> m_addPair;

        public ContactManager()
        {
            m_contactList = null;
            m_contactCount = 0;
            m_contactFilter = new b2ContactFilter();
            m_contactListener = null;
            m_broadPhase = new b2BroadPhase();
            m_addPair = AddPair;
        }

        public void Destroy(b2Contact c)
        {
            b2Fixture fixtureA = c.FixtureA;
            b2Fixture fixtureB = c.FixtureB;
            if (fixtureA == null || fixtureB == null)
            {
                return;
            }

            b2Body bodyA = fixtureA.b2Body;
            b2Body bodyB = fixtureB.b2Body;

            if (bodyA == null || bodyB == null)
            {
                // Stale pair: one of the fixtures was detached after the pair was
                // buffered. Contacts only exist between attached fixtures.
                return;
            }

            if (m_contactListener != null && c.Touching)
            {
                m_contactListener.EndContact(c);
            }

            if (c.Touching)
            {
                c.LeaveTouchOrder();
            }

            // Remove from the world.
            if (c.m_prev != null)
            {
                c.m_prev.m_next = c.m_next;
            }

            if (c.m_next != null)
            {
                c.m_next.m_prev = c.m_prev;
            }

            if (c == m_contactList)
            {
                m_contactList = c.m_next;
            }

            if (c == m_contactTail)
            {
                m_contactTail = c.m_prev;
            }

            // Remove from body 1
            if (c.m_nodeA.prev != null)
            {
                c.m_nodeA.prev.next = c.m_nodeA.next;
            }

            if (c.m_nodeA.next != null)
            {
                c.m_nodeA.next.prev = c.m_nodeA.prev;
            }

            if (c.m_nodeA == bodyA.m_contactList)
            {
                bodyA.m_contactList = c.m_nodeA.next;
            }

            // Remove from body 2
            if (c.m_nodeB.prev != null)
            {
                c.m_nodeB.prev.next = c.m_nodeB.next;
            }

            if (c.m_nodeB.next != null)
            {
                c.m_nodeB.next.prev = c.m_nodeB.prev;
            }

            if (c.m_nodeB == bodyB.m_contactList)
            {
                bodyB.m_contactList = c.m_nodeB.next;
            }

            // provided all the above removes all references, it'll be picked up by the GC

            --m_contactCount;
        }

        public void Collide()
        {
            // Update awake contacts.
            b2Contact c = m_contactList;
            while (c != null)
            {
                b2Fixture fixtureA = c.FixtureA;
                b2Fixture fixtureB = c.FixtureB;
                int indexA = c.ChildIndexA;
                int indexB = c.ChildIndexB;
            if (fixtureA == null || fixtureB == null)
            {
                return;
            }

                b2Body bodyA = fixtureA.b2Body;
                b2Body bodyB = fixtureB.b2Body;

            if (bodyA == null || bodyB == null)
            {
                // Stale pair: one of the fixtures was detached after the pair was
                // buffered. Contacts only exist between attached fixtures.
                return;
            }

                // Is this contact flagged for filtering?
                if ((c.m_flags & b2CollisionFlags.b2Filter) == b2CollisionFlags.b2Filter)
                {
                    // Should these bodies collide?
                    if (bodyB.ShouldCollide(bodyA) == false)
                    {
                        b2Contact cNuke = c;
                        c = cNuke.GetNext();
                        Destroy(cNuke);
                        continue;
                    }

                    // Check user filtering.
                    if (m_contactFilter != null && m_contactFilter.ShouldCollide(fixtureA, fixtureB) == false)
                    {
                        b2Contact cNuke = c;
                        c = cNuke.GetNext();
                        Destroy(cNuke);
                        continue;
                    }

                    // Clear the filtering flag.
                    c.m_flags &= ~b2CollisionFlags.b2Filter;
                }

                bool activeA = bodyA.IsAwake() && bodyA.m_type != b2BodyType.Static;
                bool activeB = bodyB.IsAwake() && bodyB.m_type != b2BodyType.Static;

                // At least one body must be awake and it must be dynamic or kinematic.
                if (activeA == false && activeB == false)
                {
                    c = c.GetNext();
                    continue;
                }

                int proxyIdA = fixtureA.m_proxies[indexA].proxyId;
                int proxyIdB = fixtureB.m_proxies[indexB].proxyId;
                bool overlap = m_broadPhase.TestOverlap(proxyIdA, proxyIdB);

                // Here we destroy contacts that cease to overlap in the broad-phase.
                if (overlap == false)
                {
                    b2Contact cNuke = c;
                    c = cNuke.GetNext();
                    Destroy(cNuke);
                    continue;
                }

                // The contact persists.
                c.Update(m_contactListener);
                c = c.GetNext();
            }
        }

        public void FindNewContacts()
        {
            m_broadPhase.UpdatePairs(m_addPair);
        }

        private void AddPair(object proxyUserDataA, object proxyUserDataB)
        {
            var proxyA = (FixtureProxy)proxyUserDataA;
            var proxyB = (FixtureProxy)proxyUserDataB;

            b2Fixture fixtureA = proxyA.fixture;
            b2Fixture fixtureB = proxyB.fixture;

            int indexA = proxyA.childIndex;
            int indexB = proxyB.childIndex;

            if (fixtureA == null || fixtureB == null)
            {
                return;
            }

            b2Body bodyA = fixtureA.b2Body;
            b2Body bodyB = fixtureB.b2Body;

            if (bodyA == null || bodyB == null)
            {
                // Stale pair: one of the fixtures was detached after the pair was
                // buffered. Contacts only exist between attached fixtures.
                return;
            }

            // Are the fixtures on the same body?
            if (bodyA == bodyB)
            {
                return;
            }

            // TODO_ERIN use a hash table to remove a potential bottleneck when both
            // bodies have a lot of contacts.
            // Does a contact already exist?
            b2ContactEdge edge = bodyB.GetContactList();
            while (edge != null)
            {
                if (edge.other == bodyA)
                {
                    b2Fixture fA = edge.contact.GetFixtureA();
                    b2Fixture fB = edge.contact.GetFixtureB();
                    int iA = edge.contact.GetChildIndexA();
                    int iB = edge.contact.GetChildIndexB();

                    if (fA == fixtureA && fB == fixtureB && iA == indexA && iB == indexB)
                    // A contact already exists.
                    {
                        return;
                    }

                    if (fA == fixtureB && fB == fixtureA && iA == indexB && iB == indexA)
                    // A contact already exists.
                    {
                        return;
                    }
                }

                edge = edge.next;
            }

            // Does a joint override collision? Is at least one body dynamic?
            if (bodyB.ShouldCollide(bodyA) == false)
            {
                return;
            }

            // Check user filtering.
            if (m_contactFilter != null && m_contactFilter.ShouldCollide(fixtureA, fixtureB) == false)
            {
                return;
            }

            // Call the factory.
            var c = b2Contact.Create(fixtureA, indexA, fixtureB, indexB);
            if (c == null)
            {
                return;
            }

            // b2Contact creation may swap fixtures.
            fixtureA = c.GetFixtureA();
            fixtureB = c.GetFixtureB();
            indexA = c.GetChildIndexA();
            indexB = c.GetChildIndexB();
            bodyA = fixtureA.GetBody();
            bodyB = fixtureB.GetBody();

            // Insert into the world, at the end (see m_contactList).
            c.m_prev = m_contactTail;
            c.m_next = null;
            if (m_contactTail != null)
            {
                m_contactTail.m_next = c;
            }
            else
            {
                m_contactList = c;
            }

            m_contactTail = c;

            // Connect to island graph.

            // Connect to body A
            c.m_nodeA.contact = c;
            c.m_nodeA.other = bodyB;

            c.m_nodeA.prev = null;
            c.m_nodeA.next = bodyA.m_contactList;
            if (bodyA.m_contactList != null)
            {
                bodyA.m_contactList.prev = c.m_nodeA;
            }

            bodyA.m_contactList = c.m_nodeA;

            // Connect to body B
            c.m_nodeB.contact = c;
            c.m_nodeB.other = bodyA;

            c.m_nodeB.prev = null;
            c.m_nodeB.next = bodyB.m_contactList;
            if (bodyB.m_contactList != null)
            {
                bodyB.m_contactList.prev = c.m_nodeB;
            }

            bodyB.m_contactList = c.m_nodeB;

            ++m_contactCount;
        }
    }
}