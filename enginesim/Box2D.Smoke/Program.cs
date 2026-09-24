using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Box2D;

namespace Box2DSmoke
{
    /// <summary>
    ///  Smoke scenario: a dynamic box (half extents 15x19, fixed rotation, polygon
    ///  radius 0.01) with its linear velocity SET to (460, -939) before every step,
    ///  falling onto a static ground box and sliding into a static wall.
    ///  60 steps at dt = 0.0199999921f, 8 velocity / 3 position iterations, no gravity.
    ///  Prints position and velocity per step (9 significant digits), asserts that
    ///  two fresh worlds replay bit-identically, asserts SaveState/LoadState restores
    ///  a bit-identical continuation, and measures steps/sec.
    /// </summary>
    internal static class Program
    {
        private const float Dt = 0.0199999921f;
        private const int Steps = 60;
        private const int VelocityIterations = 8;
        private const int PositionIterations = 3;
        private const int SaveStep = 20;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static void BuildWorld(out b2World world, out b2Body player)
        {
            world = new b2World(new b2Vec2(0f, 0f));

            // Static ground: top surface at y = -30, x in [-60, 60].
            var groundDef = new b2BodyDef { type = b2BodyType.b2_staticBody, position = new b2Vec2(0f, -40f) };
            b2Body ground = world.CreateBody(groundDef);
            var groundShape = new b2PolygonShape();
            groundShape.SetAsBox(60f, 10f);
            groundShape.m_radius = 0.01f;
            ground.CreateFixture(new b2FixtureDef { shape = groundShape });

            // Static wall: left face at x = 25, y in [-30, 50].
            var wallDef = new b2BodyDef { type = b2BodyType.b2_staticBody, position = new b2Vec2(35f, 10f) };
            b2Body wall = world.CreateBody(wallDef);
            var wallShape = new b2PolygonShape();
            wallShape.SetAsBox(10f, 40f);
            wallShape.m_radius = 0.01f;
            wall.CreateFixture(new b2FixtureDef { shape = wallShape });

            // Dynamic player box: half extents 15x19, fixed rotation, polygon radius 0.01.
            var playerDef = new b2BodyDef
            {
                type = b2BodyType.b2_dynamicBody,
                position = new b2Vec2(0f, 0f),
                fixedRotation = true,
            };
            player = world.CreateBody(playerDef);
            var playerShape = new b2PolygonShape();
            playerShape.SetAsBox(15f, 19f);
            playerShape.m_radius = 0.01f;
            player.CreateFixture(new b2FixtureDef { shape = playerShape, density = 1f });
        }

        private static string Format(int step, b2Body body)
        {
            b2Vec2 p = body.GetPosition();
            b2Vec2 v = body.GetLinearVelocity();
            return string.Format(
                Inv,
                "step={0:D2} pos=({1:G9}, {2:G9}) vel=({3:G9}, {4:G9})",
                step,
                p.x,
                p.y,
                v.x,
                v.y);
        }

        private static void StepOnce(b2World world, b2Body player, int step, List<string> record)
        {
            player.SetLinearVelocity(new b2Vec2(460f, -939f));
            world.Step(Dt, VelocityIterations, PositionIterations);
            record?.Add(Format(step, player));
        }

        /// <summary>Fresh world, steps 1..60, recorded.</summary>
        private static List<string> RunPlain()
        {
            BuildWorld(out b2World world, out b2Body player);
            var lines = new List<string>(Steps);
            for (int step = 1; step <= Steps; step++)
            {
                StepOnce(world, player, step, lines);
            }

            return lines;
        }

        /// <summary>
        ///  Fresh world, steps 1..20 recorded, SaveState, 5 drifted steps (21..25),
        ///  LoadState, then steps 21..60 recorded from the restored state.
        /// </summary>
        private static List<string> RunSaveLoad()
        {
            BuildWorld(out b2World world, out b2Body player);
            var ignored = new List<string>(Steps);
            for (int step = 1; step <= SaveStep; step++)
            {
                StepOnce(world, player, step, ignored);
            }

            object saved = world.SaveState();

            for (int step = SaveStep + 1; step <= SaveStep + 5; step++)
            {
                StepOnce(world, player, step, null);
            }

            world.LoadState(saved);

            var tail = new List<string>(Steps - SaveStep);
            for (int step = SaveStep + 1; step <= Steps; step++)
            {
                StepOnce(world, player, step, tail);
            }

            return tail;
        }

        private static string EdgeSceneRun()
        {
            var world = new b2World(new b2Vec2(0f, -10f));

            var edgeBodyDef = new b2BodyDef { type = b2BodyType.b2_staticBody, position = new b2Vec2(0f, -2f) };
            b2Body edgeBody = world.CreateBody(edgeBodyDef);
            var edge = new b2EdgeShape();
            edge.SetTwoSided(new b2Vec2(-20f, 0f), new b2Vec2(4f, 0f));
            edge.m_radius = 0.01f;
            edgeBody.CreateFixture(new b2FixtureDef { shape = edge });

            var chainBodyDef = new b2BodyDef { type = b2BodyType.b2_staticBody, position = new b2Vec2(4f, -2f) };
            b2Body chainBody = world.CreateBody(chainBodyDef);
            var chain = new b2ChainShape();
            chain.CreateLoop(new[]
            {
                new b2Vec2(0f, 0f),
                new b2Vec2(8f, 0f),
                new b2Vec2(8f, 6f),
                new b2Vec2(0f, 6f),
            });
            chain.m_radius = 0.01f;
            chainBody.CreateFixture(new b2FixtureDef { shape = chain });

            var playerDef = new b2BodyDef { type = b2BodyType.b2_dynamicBody, position = new b2Vec2(0f, 4f), fixedRotation = true };
            b2Body player = world.CreateBody(playerDef);
            var box = new b2PolygonShape();
            box.SetAsBox(1f, 1f);
            box.m_radius = 0.01f;
            player.CreateFixture(new b2FixtureDef { shape = box, density = 1f });

            var record = new List<string>(30);
            for (int step = 1; step <= 30; step++)
            {
                world.Step(Dt, VelocityIterations, PositionIterations);
                record.Add(Format(step, player));
            }

            return string.Join("\n", record);
        }

        /// <summary>Runs the edge/chain micro-scene in both collider modes twice each.</summary>
        private static bool ExerciseEdgeColliders()
        {
            bool ok = true;
            foreach (bool legacy in new[] { true, false })
            {
                b2Settings.UseLegacyEdgePolygon = legacy;
                string a = EdgeSceneRun();
                string b = EdgeSceneRun();
                if (a != b)
                {
                    ok = false;
                    Console.WriteLine("MISMATCH edge-scene mode legacy=" + legacy + ":");
                    Console.WriteLine("  A: " + a);
                    Console.WriteLine("  B: " + b);
                }
            }

            b2Settings.UseLegacyEdgePolygon = true;
            return ok;
        }

        private static int Main()
        {
            List<string> runA = RunPlain();
            List<string> runB = RunPlain();

            foreach (string line in runA)
            {
                Console.WriteLine(line);
            }

            bool deterministic = true;
            for (int i = 0; i < Steps; i++)
            {
                if (runA[i] != runB[i])
                {
                    deterministic = false;
                    Console.WriteLine("MISMATCH fresh-replay step " + (i + 1) + ":");
                    Console.WriteLine("  A: " + runA[i]);
                    Console.WriteLine("  B: " + runB[i]);
                    break;
                }
            }

            Console.WriteLine(deterministic ? "DETERMINISM OK" : "DETERMINISM FAILED");

            List<string> tailA = runA.GetRange(SaveStep, Steps - SaveStep);
            List<string> tailS = RunSaveLoad();
            bool saveOk = tailS.Count == tailA.Count;
            for (int i = 0; saveOk && i < tailS.Count; i++)
            {
                if (tailS[i] != tailA[i])
                {
                    saveOk = false;
                    Console.WriteLine("MISMATCH save/load step " + (SaveStep + 1 + i) + ":");
                    Console.WriteLine("  A: " + tailA[i]);
                    Console.WriteLine("  S: " + tailS[i]);
                }
            }

            Console.WriteLine(saveOk ? "SAVESTATE OK" : "SAVESTATE FAILED");

            // Exercise the 2.4.1 edge/polygon path (b2Settings.UseLegacyEdgePolygon
            // = false) and verify it is deterministic as well.
            b2Settings.UseLegacyEdgePolygon = false;
            List<string> run241A = RunPlain();
            List<string> run241B = RunPlain();
            b2Settings.UseLegacyEdgePolygon = true;

            bool ok241 = true;
            for (int i = 0; i < Steps; i++)
            {
                if (run241A[i] != run241B[i])
                {
                    ok241 = false;
                    Console.WriteLine("MISMATCH 2.4.1-edgepath step " + (i + 1) + ":");
                    Console.WriteLine("  A: " + run241A[i]);
                    Console.WriteLine("  B: " + run241B[i]);
                    break;
                }
            }

            Console.WriteLine(ok241 ? "EDGEPATH OK (UseLegacyEdgePolygon=false deterministic)" : "EDGEPATH FAILED");

            // Micro-scene with real edge + chain shapes so both edge/polygon
            // collider paths actually execute (box dropped onto a static edge and
            // a static chain segment).
            bool edgeOk = ExerciseEdgeColliders();
            Console.WriteLine(edgeOk ? "EDGECOLLIDE OK (edge + chain paths executed in both modes)" : "EDGECOLLIDE FAILED");

            // Benchmark: repeated scenario runs.
            const int benchIters = 2000;
            RunPlain(); // warm up JIT
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < benchIters; i++)
            {
                RunPlain();
            }

            sw.Stop();
            double stepsPerSec = benchIters * (double)Steps / sw.Elapsed.TotalSeconds;
            Console.WriteLine(string.Format(
                Inv,
                "BENCH steps/sec={0:F0} ({1} steps in {2:F3}s)",
                stepsPerSec,
                benchIters * Steps,
                sw.Elapsed.TotalSeconds));

            return deterministic && saveOk ? 0 : 1;
        }
    }
}
