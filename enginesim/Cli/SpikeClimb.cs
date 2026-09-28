using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// climb: a beam search up a wall by chained dash wallbounces (spike wallbounce): drift, hold toward the wall, press jump,
    /// dash into the wall. The dash ends on the wall ray before a spike's second tick, the buffered jump bounces off
    /// (SpecialWallJump: 730 up, 410 upward momentum) and the jump's turn takes the hurtbox 10 units off the wall; the touch
    /// refills the dash. Each round tries every cycle from every beam state and keeps the highest by apex; it stops when a
    /// state reaches the next goal. Writes a Full-TAS seed (the inputs after --from, the goal it reaches).
    /// </summary>
    static class SpikeClimb
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo VelocityField = typeof(Movement).GetField("Velocity", Fields);
        static readonly FieldInfo MomentumField = typeof(Movement).GetField("momentum", Fields);
        const float Gravity = 1350f;
        const int Tail = 3;

        sealed class State
        {
            public List<InputTick> Inputs;
            public float X, Y, Score;
            public int Progress;
            public bool Held;
        }

        public static int Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: climb <trace.json> <route.json> --from N --wall X [--toward -1|1] [--beam 24] [--rounds 40] [--threads N] [--out seed.json]");
                return 2;
            }
            string trace = args[0];
            JsonElement header = Solve.ReadHeader(trace);
            List<InputTick> route;
            string[] labels;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(args[1]), new JsonDocumentOptions { MaxDepth = 256 }))
            {
                route = Solve.ReadInputs(doc.RootElement);
                labels = doc.RootElement.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToArray();
            }
            int from = Solve.Option(args, "--from", route.Count);
            float toward = Solve.Option(args, "--toward", -1f), wall = Solve.Option(args, "--wall", float.NaN);
            int beam = Solve.Option(args, "--beam", 24), rounds = Solve.Option(args, "--rounds", 40);
            int threads = Solve.Option(args, "--threads", Environment.ProcessorCount);
            string output = Solve.Option(args, "--out", Path.ChangeExtension(args[1], null) + ".climb.json");
            List<InputTick> prefix = route.GetRange(0, from);
            Func<Simulation> make = Solve.Creator(trace, header);

            // Each worker thread builds its own simulation and keeps it: a snapshot holds its own world, and the game's statics
            // are per thread.
            var level = new List<State>();
            var children = new List<State>();
            int next = 0, goal = -1;
            bool quit = false;
            Exception failed = null;
            var barrier = new Barrier(threads + 1);
            for (int w = 0; w < threads; w++)
            {
                int index = w;
                new Thread(() =>
                {
                    Simulation sim = null;
                    SimSnapshot start = null;
                    try
                    {
                        sim = make();
                        sim.StrictBuys = true;
                        start = sim.Advance(prefix) ?? throw new InvalidOperationException("the route ends before its first " + from + " inputs");
                        if (index == 0)
                        {
                            sim.Restore(start);
                            goal = sim.Progress;
                            Console.WriteLine($"climb from input {from}: player {sim.PlayerWorld.x:0.0},{sim.PlayerWorld.y:0.0}, {goal}/{labels.Length} goals");
                            if (args.Contains("--diagnose")) { Diagnose(sim, start, toward, wall, from > 0 && prefix[from - 1].held); Environment.Exit(0); }
                        }
                    }
                    catch (Exception ex) { failed = ex; }
                    while (true)
                    {
                        barrier.SignalAndWait();
                        if (quit) return;
                        var mine = new List<State>();
                        try
                        {
                            for (int k; failed == null && (k = Interlocked.Increment(ref next) - 1) < level.Count;)
                                Expand(sim, start, level[k], toward, wall, mine);
                        }
                        catch (Exception ex) { failed = ex; }
                        lock (children) children.AddRange(mine);
                        barrier.SignalAndWait();
                    }
                }) { IsBackground = true, Name = "climb-" + w }.Start();
            }
            bool heldAtStart = from > 0 && prefix[from - 1].held;
            level.Add(new State { Inputs = new List<InputTick>(), Held = heldAtStart });
            for (int round = 1; round <= rounds && level.Count > 0; round++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                children.Clear();
                next = 0;
                barrier.SignalAndWait();
                barrier.SignalAndWait();
                if (failed != null) throw new InvalidOperationException("a climb worker failed", failed);
                if (round == 1 && goal >= labels.Length) throw new InvalidOperationException("the prefix reaches every goal");
                State done = children.Where(c => c.Progress > goal).OrderBy(c => c.Inputs.Count).FirstOrDefault();
                if (done != null)
                {
                    Console.WriteLine($"round {round}: reached goal {goal + 1} {labels[goal]} after {done.Inputs.Count} inputs");
                    Write(output, labels[goal], prefix, done.Inputs);
                    return 0;
                }
                // Highest first, one per 8-unit square (and jump state), so the beam keeps different lines.
                level = children.OrderByDescending(c => c.Score)
                    .GroupBy(c => (Mathf.FloorToInt(c.X / 8f), Mathf.FloorToInt(c.Y / 8f), c.Held)).Select(g => g.First())
                    .Take(beam).ToList();
                if (level.Count > 0)
                {
                    Console.WriteLine($"round {round}: {children.Count} alive, best y {level[0].Y:0.0} (score {level[0].Score:0.0}) at x {level[0].X:0.0}, {level[0].Inputs.Count} inputs, {clock.Elapsed.TotalSeconds:0}s");
                    Write(output + ".partial.json", labels[goal], prefix, level[0].Inputs);
                }
            }
            quit = true;
            barrier.SignalAndWait();
            Console.WriteLine("no state reached the goal");
            return 1;
        }

        /// <summary>Every cycle from the parent's state (replayed from the worker's start): the ones the player lives through.</summary>
        static void Expand(Simulation sim, SimSnapshot start, State parent, float toward, float wall, List<State> into)
        {
            sim.SetStart(start, 0);
            SimSnapshot at = parent.Inputs.Count == 0 ? start : After(sim, parent.Inputs, 40 * parent.Inputs.Count);
            if (at == null) return;
            sim.SetStart(at, 0);
            foreach (List<InputTick> cycle in Cycles(toward, parent.Held))
            {
                SimSnapshot end = After(sim, cycle, 40);
                if (end == null) continue;
                sim.Restore(end);
                Movement p = sim.Player;
                Vector2 v = (Vector2)VelocityField.GetValue(p) + (Vector2)MomentumField.GetValue(p);
                WorldPos pos = sim.PlayerWorld;
                float up = Math.Max(0f, v.y);
                into.Add(new State
                {
                    Inputs = parent.Inputs.Concat(cycle).ToList(),
                    X = pos.x, Y = pos.y, Held = cycle[cycle.Count - 1].held, Progress = sim.Progress,
                    // Apex, and a dash and midair jump in hand are worth a bounce and a jump more; a state drifting off the
                    // wall is worth less.
                    Score = pos.y + up * up / (2f * Gravity) + 60f * p.airDashesLeft + 40f * p.airJumpsLeft
                        - (float.IsNaN(wall) ? 0f : Math.Max(0f, Math.Abs(pos.x - wall) - 80f)),
                });
            }
        }

        /// <summary>
        /// One bounce: a ticks of stick d (away, neutral or a little toward), b ticks of stick s toward the wall, then the dash
        /// toward it; jump pressed j ticks before the dash and held through the bounce and Tail ticks after it. A held jump is
        /// released on the first tick. Part-way sticks place the dash's start in the few units it needs (off the spikes, one
        /// dash tick from the wall).
        /// </summary>
        static IEnumerable<List<InputTick>> Cycles(float toward, bool held) { return Named(toward, held).Select(c => c.ticks); }

        static IEnumerable<(string name, List<InputTick> ticks)> Named(float toward, bool held)
        {
            foreach (float d in new[] { -toward, -toward / 2f, 0f, toward / 4f })
                for (int a = 0; a <= 8; a++)
                    for (int b = 1; b <= 8; b++)
                        foreach (float stick in new[] { toward, toward / 2f, toward / 4f })
                        for (int j = 0; j <= 3; j++)
                        {
                            int dash = a + b, press = dash - j;
                            if (press < (held ? 1 : 0)) continue;
                            var ticks = new List<InputTick>(dash + 1 + Tail);
                            for (int i = 0; i <= dash + Tail; i++)
                            {
                                float x = i < a ? d : i < dash ? stick : toward;
                                bool on = i >= press;
                                ticks.Add(new InputTick { x = i > dash ? 0f : x, held = on, press = i == press, release = held && i == 0, dash = i == dash });
                            }
                            yield return ($"d{d} a{a} s{stick} b{b} j{j}", ticks);
                        }
        }

        /// <summary>
        /// --diagnose: every cycle from the start, one line each: the wall distance (player center) when the dash is read, the
        /// nearest it came, whether a dash was running when it died, and the most upward momentum after the dash (a bounce).
        /// </summary>
        static void Diagnose(Simulation sim, SimSnapshot start, float toward, float wall, bool held)
        {
            FieldInfo dashActive = typeof(Movement).GetField("dashActive", Fields);
            sim.SetStart(start, 0);
            foreach (var (name, cycle) in Named(toward, held))
            {
                int dashAt = cycle.FindIndex(t => t.dash);
                float dashDist = float.NaN, nearest = float.MaxValue, bounce = 0f;
                bool dashing = false;
                var padded = cycle.Concat(Enumerable.Repeat(new InputTick { x = 0f, held = true }, 8)).ToList();
                RunResult r = sim.Run(padded, null, tick =>
                {
                    Movement p = sim.Player;
                    float dist = (sim.PlayerWorld.x - wall) * -toward;
                    if (tick == dashAt) dashDist = dist;
                    nearest = Math.Min(nearest, dist);
                    dashing = (bool)dashActive.GetValue(p);
                    if (tick > dashAt) bounce = Math.Max(bounce, ((Vector2)MomentumField.GetValue(p)).y);
                    return true;
                }, 20);
                bool died = r.EndReason != null && r.EndReason.StartsWith("Player death", StringComparison.Ordinal);
                Console.WriteLine($"{name} | dash at {dashDist:0.0} nearest {nearest:0.0} bounce {bounce:0} {(died ? "DIED" + (dashing ? " dashing" : "") : "alive")} y {sim.PlayerWorld.y:0.0}");
            }
        }

        /// <summary>
        /// Simulation.Advance with a cap: the state once the inputs are read, null when the player dies or the inputs aren't all
        /// read within <paramref name="extra"/> ticks more than there are inputs (a state that reads none).
        /// </summary>
        static SimSnapshot After(Simulation sim, List<InputTick> inputs, int extra)
        {
            SimSnapshot at = null;
            var padded = inputs.Concat(new[] { inputs[inputs.Count - 1] }).ToList();
            sim.Run(padded, null, tick =>
            {
                if (tick < inputs.Count) return true;
                sim.Engine.RebaseInputs();
                at = sim.Capture(0);
                return false;
            }, extra);
            return at;
        }

        static void Write(string path, string goal, List<InputTick> prefix, List<InputTick> climb)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                name = "climb " + Path.GetFileNameWithoutExtension(path),
                goals = new[] { goal },
                capabilities = "none",
                standing = false,
                restart = false,
                from = prefix.Count,
                ticks = climb.Select(t => t.ToJson()),
            }));
        }
    }
}
