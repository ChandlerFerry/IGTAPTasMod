using System;
using Box2D;

namespace IGTAP.EngineSim.Cli
{
    static class BodyEnableCheck
    {
        internal static int Run()
        {
            int fails = 0;
            void Check(string what, bool ok)
            {
                Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
                if (!ok) fails++;
            }

            var world = new b2World(new b2Vec2(0f, -10f));
            var floorShape = new b2PolygonShape();
            floorShape.SetAsBox(10f, 0.5f);
            world.CreateBody(new b2BodyDef { position = new b2Vec2(0f, -0.5f) }).CreateFixture(floorShape);
            b2Body box = world.CreateBody(new b2BodyDef { type = b2BodyType.b2_dynamicBody, position = new b2Vec2(0f, 2f), enabled = false });
            var boxShape = new b2PolygonShape();
            boxShape.SetAsBox(0.5f, 0.5f);
            b2Fixture fixture = box.CreateFixture(boxShape, 1f);
            void Steps(int n) { for (int i = 0; i < n; i++) world.Step(0.02f, 8, 3); }
            bool Found()
            {
                var aabb = new b2AABB { lowerBound = new b2Vec2(-5f, -5f), upperBound = new b2Vec2(5f, 5f) };
                world.QueryAABB(out b2Fixture[] hits, aabb);
                return Array.IndexOf(hits, fixture) >= 0;
            }

            object disabled = world.SaveState();
            Steps(50);
            Check("built disabled: no proxies, not moved", fixture.m_proxyCount == 0 && box.GetPosition().y == 2f && !Found());

            box.SetEnabled(true);
            Steps(100);
            b2Contact c = world.GetContactList();
            Check("enabled: falls and lands on the floor", Found() && box.GetPosition().y > 0.4f && box.GetPosition().y < 0.6f
                && world.GetContactCount() == 1 && c.IsTouching());

            box.SetEnabled(false);
            Check("disabled again: no proxies, no contacts", fixture.m_proxyCount == 0 && world.GetContactCount() == 0
                && box.GetContactList() == null && !Found());
            box.SetTransform(new b2Vec2(0f, -0.5f), 0f);
            Steps(5);
            Check("disabled inside the floor: no contact, not moved", world.GetContactCount() == 0 && box.GetPosition().y == -0.5f);

            box.SetEnabled(true);
            Steps(1);
            world.LoadState(disabled);
            Check("a snapshot taken disabled loads disabled", !box.IsEnabled() && fixture.m_proxyCount == 0 && world.GetContactCount() == 0
                && !Found() && box.GetPosition().y == 2f);

            Console.WriteLine(fails == 0 ? "body-selftest: PASS" : $"body-selftest: {fails} FAIL");
            return fails == 0 ? 0 : 1;
        }
    }
}
