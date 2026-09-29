using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Box2D;
using IGTAPTasMod;
using UnityEngine;
using Math = System.Math;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// determinism &lt;trace.json&gt; &lt;route.json&gt; [--edges N] [--seed S] [--passes P] [--no-child]: whether a run from a
    /// snapshot depends on anything but the snapshot. It captures snapshots along the route (before and after its rebases),
    /// draws a fixed set of edges from them (a stretch of the route's inputs, 17 to 150 ticks, as a search's edge), and runs
    /// the set in order and then in shuffled orders. Each restore must give back the physics state the capture saw
    /// (<see cref="PhysicsWorld.StateDigest"/>), and each edge must end in the same state (physics, player, RNG, goals) in
    /// every pass. A mid-edge capture, restored after other runs and continued on its edge's own inputs, must end where the
    /// uninterrupted edge did. Snapshots stored as bytes (<see cref="SnapshotStore"/>) must load into a fresh simulation
    /// and end their edges the same. Then it runs itself again in a new process with another shuffle and compares every
    /// edge's digest. Exit 0 when everything is identical.
    /// </summary>
    static class Determinism
    {
        public static int Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: determinism <trace.json> <route.json> [--edges N] [--seed S] [--passes P] [--no-child]");
                return 2;
            }
            string trace = args[0], route = args[1];
            int edgeCount = Solve.Option(args, "--edges", 300);
            int seed = Solve.Option(args, "--seed", 1);
            int passes = Solve.Option(args, "--passes", 3);
            int shuffle = Solve.Option(args, "--shuffle", 0);
            int dumpAt = Array.IndexOf(args, "--dump");
            string dump = dumpAt > 0 ? args[dumpAt + 1] : null;
            bool child = dump != null;

            JsonElement header = Solve.ReadHeader(trace);
            Func<Simulation> create = Solve.Creator(trace, header);
            Simulation Make()
            {
                Simulation made = create();
                if (header.TryGetProperty("fulltas", out JsonElement fulltas) && made.Goals != null)
                {
                    made.StrictBuys = true;
                    made.Goals.Limit = fulltas.GetProperty("goals").GetArrayLength();
                }
                return made;
            }
            Simulation sim = Make();
            List<InputTick> inputs = Plan.ReadRoute(route);
            var clock = Stopwatch.StartNew();
            SimSnapshot origin = sim.Start;

            // Snapshots spread over the route; a rebase's side is where the snapshot's Rebases count changes.
            int points = 10;
            var snaps = new List<(int at, SimSnapshot snap, ulong digest)>();
            for (int k = 0; k < points; k++)
            {
                int at = Math.Max(1, (int)((long)(inputs.Count - 30) * (k + 1) / (points + 1)));
                sim.SetStart(origin, 0);
                SimSnapshot s = sim.Advance(inputs.Take(at).ToList());
                if (s == null)
                {
                    // The run ended first (a death, a finish): no state there to branch from.
                    Console.WriteLine($"the route's run ends before input {at}: no snapshot there");
                    continue;
                }
                snaps.Add((at, s, sim.Physics.StateDigest()));
            }
            if (snaps.Count == 0)
            {
                Console.Error.WriteLine("the route's run ends before its first snapshot point");
                return 2;
            }
            sim.SetStart(origin, 0);

            // The edge set, fixed by the seed (the same in every process and order): a search's edge runs the route's inputs to
            // its snapshot and then its own. Every third one captures a state halfway (as a search keeps a node) and a child
            // edge runs from that capture later in the pass, after other edges ran in between.
            var rng = new System.Random(seed);
            int[] lengths = { 17, 17, 40, 80, 150 };
            var edges = new (int snap, List<InputTick> inputs, int captureAt, List<InputTick> child)[edgeCount];
            List<InputTick> Stretch(int from)
            {
                int length = lengths[rng.Next(lengths.Length)];
                int room = inputs.Count - from - length;
                // Near the snapshot's own continuation, or anywhere later in the route (a search's wilder edges: deaths, walls).
                int off = from + rng.Next(0, Math.Max(1, rng.Next(2) == 0 ? Math.Min(40, room) : room));
                return inputs.Skip(off).Take(length).ToList();
            }
            for (int e = 0; e < edgeCount; e++)
            {
                // A route snapshot is a start (Simulation.Advance rebases its inputs to 0): the edge's inputs are its own.
                int j = rng.Next(snaps.Count), at = snaps[j].at;
                List<InputTick> list = Stretch(at);
                int captureAt = -1;
                List<InputTick> childInputs = null;
                if (e % 3 == 0 && list.Count >= 2)
                {
                    captureAt = list.Count / 2;
                    childInputs = list.Take(captureAt).Concat(Stretch(at + captureAt)).ToList();
                }
                edges[e] = (j, list, captureAt, childInputs);
            }

            int restoreMismatches = 0, restores = 0, captures = 0;
            // IGTAP_DETERMINISM_DUMP=<edge>: that edge's capture and the restore of it as text (determinism-*.txt), to diff.
            int dumpEdge = int.TryParse(Environment.GetEnvironmentVariable("IGTAP_DETERMINISM_DUMP"), out int d) ? d : -1;
            System.Text.StringBuilder captureLog = null;
            // [pass, edge] for the edges, [pass, edgeCount + edge] for their children.
            var results = new ulong[passes, 2 * edgeCount];
            var local = new int[passes, 2 * edgeCount];
            // The player's end state alone (no physics internals): whether a difference reached the game.
            var player = new ulong[passes, 2 * edgeCount];
            var ends = new string[passes, 2 * edgeCount];
            int crossing = 0, sideSwitches = 0;
            // Pass 0's mid-edge captures and each such edge's end state (no run counters): the continuation check below.
            var firstCaptures = new (SimSnapshot state, long physics)[edgeCount];
            var edgeEnds = new (ulong state, int local, long physics)[edgeCount];
            void Restored(SimSnapshot snap, ulong digest, string what)
            {
                restores++;
                sim.Restore(snap);
                ulong restored = sim.Physics.StateDigest();
                if (restored != digest && restoreMismatches++ < 10)
                    Console.WriteLine($"restore of {what} gave physics {restored:x16}, the capture had {digest:x16}");
            }
            for (int p = 0; p < passes; p++)
            {
                var shuffler = p > 0 || child ? new System.Random(seed * 7919 + p + 1000 * shuffle) : null;
                var order = Enumerable.Range(0, edgeCount).ToList();
                if (shuffler != null)
                    for (int i = order.Count - 1; i > 0; i--) { int k = shuffler.Next(i + 1); (order[i], order[k]) = (order[k], order[i]); }
                var captured = new (SimSnapshot state, ulong digest)[edgeCount];
                int lastSide = -1;
                for (int n = 0; n < order.Count; n++)
                {
                    int task = order[n], e = task % edgeCount;
                    var (j, list, captureAt, childInputs) = edges[e];
                    SimSnapshot from = task < edgeCount ? snaps[j].snap : captured[e].state;
                    int side = from.Rebases.Length;
                    if (lastSide >= 0 && side != lastSide) sideSwitches++;
                    lastSide = side;
                    if (task < edgeCount) Restored(from, snaps[j].digest, $"the snapshot at input {snaps[j].at} (pass {p}, edge {e})");
                    else Restored(from, captured[e].digest, $"edge {e}'s capture at input {captureAt} (pass {p})");
                    if (e == dumpEdge && task >= edgeCount && p == 0)
                    {
                        var restoreLog = new System.Text.StringBuilder();
                        sim.Physics.StateDigest(restoreLog);
                        File.WriteAllText("determinism-capture.txt", captureLog.ToString());
                        File.WriteAllText("determinism-restore.txt", restoreLog.ToString());
                    }
                    RunResult r = task < edgeCount
                        ? sim.Run(list, from, t =>
                        {
                            if (t == captureAt)
                            {
                                captures++;
                                captured[e] = (sim.Capture(t), sim.Physics.StateDigest(e == dumpEdge ? captureLog = new System.Text.StringBuilder() : null));
                                if (p == 0) firstCaptures[e] = (captured[e].state, sim.Engine.PhysicsTicks);
                                // Later in the pass: in order right after its edge, shuffled anywhere after it.
                                order.Insert(shuffler == null ? n + 1 : shuffler.Next(n + 1, order.Count + 1), edgeCount + e);
                            }
                            return true;
                        }, extraTicks: 1)
                        : sim.Run(childInputs, from, extraTicks: 1);
                    if (p == 0 && task < edgeCount && sim.Rebased != side > 0) crossing++;
                    results[p, task] = EndDigest(sim, r, out player[p, task]);
                    var body = sim.Physics.PlayerBody();
                    ends[p, task] = $"player at {body.px:R},{body.py:R} moving {body.vx:R},{body.vy:R} after {r.Stepped} ticks{(r.EndReason != null ? " (" + r.EndReason + ")" : "")}";
                    local[p, task] = sim.Fingerprint();
                    if (p == 0 && task < edgeCount) edgeEnds[e] = (StateDigest(sim), local[p, task], r.PhysicsTicks);
                }
            }

            int differ = 0, runs = 0, playerDiffer = 0;
            for (int task = 0; task < 2 * edgeCount; task++)
            {
                if (task >= edgeCount && edges[task - edgeCount].captureAt < 0) continue;
                runs++;
                for (int p = 1; p < passes; p++)
                    if (results[p, task] != results[0, task] || local[p, task] != local[0, task])
                    {
                        bool game = false;
                        for (int q = 1; q < passes; q++) game |= player[q, task] != player[0, task] || local[q, task] != local[0, task];
                        if (game) playerDiffer++;
                        if (differ++ < 10)
                            Console.WriteLine($"{(task < edgeCount ? "edge " + task : "child of edge " + (task - edgeCount))}: pass {p} ended {results[p, task]:x16}/{local[p, task]:x8}, pass 0 {results[0, task]:x16}/{local[0, task]:x8}"
                                + (game ? $"\n  pass 0: {ends[0, task]}\n  pass {p}: {ends[p, task]}" : " (physics internals only)"));
                        break;
                    }
            }
            ulong all = SetDigest(results, 2 * edgeCount);
            if (child)
            {
                File.WriteAllLines(dump, Enumerable.Range(0, 2 * edgeCount).Select(e => results[0, e].ToString("x16")));
                Console.WriteLine($"other process: {runs} runs, digest {all:x16}, restore mismatches {restoreMismatches}, runs differing between passes {differ}");
                return restoreMismatches == 0 && differ == 0 ? 0 : 1;
            }
            Console.WriteLine($"{snaps.Count} snapshots (inputs {string.Join(",", snaps.Select(s => s.at + (s.snap.Rebases.Length > 0 ? "r" + s.snap.Rebases.Length : "")))}), "
                + $"{edgeCount} edges ({crossing} cross a rebase) and {runs - edgeCount} children from their captures ({captures} captured), {passes} passes (1 in order, {passes - 1} shuffled; {sideSwitches} restores across a rebase), {clock.Elapsed.TotalSeconds:0.0} s");
            Console.WriteLine($"restores equal to their capture: {restores - restoreMismatches}/{restores}");
            Console.WriteLine($"runs identical in every pass: {runs - differ}/{runs} (the player's end state: {runs - playerDiffer}/{runs}), digest {all:x16}");

            // A restored state must continue as the run it was captured on: each mid-edge capture of pass 0, restored after
            // other runs, plays the rest of its edge's own inputs and must end in the state the edge ended in (a restore that
            // left another run's state in place, say a body's transform waiting for its sync, shows here).
            int continued = 0, continuedSame = 0;
            for (int e = 0; e < edgeCount; e++)
            {
                if (firstCaptures[e].state == null) continue;
                continued++;
                // As many loop steps as the edge had left (a freeze's physics ticks spend the loop's budget without an input).
                var (capture, atPhysics) = firstCaptures[e];
                sim.Run(edges[e].inputs, capture, extraTicks: (int)(edgeEnds[e].physics - atPhysics) - (edges[e].inputs.Count - edges[e].captureAt));
                if (StateDigest(sim) == edgeEnds[e].state && sim.Fingerprint() == edgeEnds[e].local) continuedSame++;
                else if (continued - continuedSame <= 10)
                    Console.WriteLine($"edge {e} continued from its capture at input {edges[e].captureAt} ended differently from the edge's own run");
            }
            Console.WriteLine($"captures continued with their edge's own inputs: {continuedSame}/{continued} end as the uninterrupted edge");

            // A snapshot stored as bytes and loaded into a fresh simulation (a Full-TAS solve's goal states, SnapshotStore) must
            // restore the captured state, and its edges must end as they did here. Every save comes first: a simulation takes
            // its thread's hooks (the input hook, the physics backend) when it is made, so this one's restores once the fresh one
            // exists would run through the fresh one's.
            int stored = 0, storedSame = 0, storedRuns = 0, storedRunsSame = 0, unstorable = 0;
            var store = new SnapshotStore(sim);
            var saved = new byte[snaps.Count][];
            for (int j = 0; j < snaps.Count; j++)
            {
                sim.Restore(snaps[j].snap);
                try { saved[j] = store.Save(snaps[j].snap); }
                catch (NotSupportedException) { unstorable++; }
            }
            Simulation fresh = Make();
            var freshStore = new SnapshotStore(fresh);
            for (int j = 0; j < snaps.Count; j++)
            {
                if (saved[j] == null) continue;
                SimSnapshot loaded;
                try { loaded = freshStore.Load(saved[j]); }
                catch (NotSupportedException ex)
                {
                    unstorable++;
                    Console.WriteLine($"the snapshot at input {snaps[j].at} can't be loaded into a fresh simulation: {ex.Message}");
                    continue;
                }
                fresh.Restore(loaded);
                stored++;
                ulong digest = fresh.Physics.StateDigest();
                if (digest == snaps[j].digest) storedSame++;
                else Console.WriteLine($"the snapshot at input {snaps[j].at}, stored and loaded into a fresh simulation, restored physics {digest:x16}, the capture had {snaps[j].digest:x16}");
                for (int e = 0, n = 0; e < edgeCount && n < 5; e++)
                {
                    if (edges[e].snap != j) continue;
                    n++;
                    storedRuns++;
                    fresh.SetStart(loaded, 0);
                    RunResult r = fresh.Run(edges[e].inputs, loaded, extraTicks: 1);
                    if (EndDigest(fresh, r, out _) == results[0, e]) storedRunsSame++;
                    else Console.WriteLine($"edge {e} from the stored snapshot at input {snaps[j].at} ended differently in the fresh simulation");
                }
            }
            Console.WriteLine($"stored snapshots loaded into a fresh simulation: {storedSame}/{stored} restore the captured state, {storedRunsSame}/{storedRuns} of their edges end the same"
                + (unstorable > 0 ? $" ({unstorable} not storable)" : ""));

            int crossDiffer = -1;
            if (!args.Contains("--no-child"))
            {
                string file = Path.GetTempFileName();
                var start = new ProcessStartInfo(Environment.ProcessPath) { RedirectStandardOutput = true, UseShellExecute = false };
                foreach (string a in new[] { "determinism", trace, route, "--edges", edgeCount.ToString(), "--seed", seed.ToString(), "--passes", "1",
                    "--shuffle", (shuffle + 1).ToString(), "--dump", file })
                    start.ArgumentList.Add(a);
                using (Process process = Process.Start(start))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    Console.Write(output);
                }
                string[] theirs = File.ReadAllLines(file);
                File.Delete(file);
                crossDiffer = 0;
                for (int task = 0; task < 2 * edgeCount; task++)
                    if (task >= theirs.Length || theirs[task] != results[0, task].ToString("x16"))
                    {
                        if (crossDiffer++ < 10)
                            Console.WriteLine($"run {task}: this process ended {results[0, task]:x16}, the other {(task < theirs.Length ? theirs[task] : "(missing)")}");
                    }
                Console.WriteLine($"runs identical across processes: {runs - crossDiffer}/{runs}");
            }
            bool ok = restoreMismatches == 0 && differ == 0 && continuedSame == continued && crossDiffer <= 0 && storedSame == stored && storedRunsSame == storedRuns;
            Console.WriteLine(ok ? "DETERMINISTIC" : "NOT DETERMINISTIC");
            return ok ? 0 : 1;
        }

        static ulong SetDigest(ulong[,] results, int edgeCount)
        {
            var h = b2Digest.Start();
            for (int e = 0; e < edgeCount; e++) h.Add((long)results[0, e]);
            return h.Value;
        }

        /// <summary>The state a run ended in, as a digest that is the same in every process: its result, the player's transform,
        /// body and Movement fields, the courses' fields and the RNG (<paramref name="player"/>), then the whole physics state.</summary>
        static ulong EndDigest(Simulation sim, RunResult r, out ulong player)
        {
            var h = b2Digest.Start();
            h.Add(r.Stepped); h.Add(r.PhysicsTicks); h.Add(r.Progress); h.Add(r.Finished); h.Add(r.Time); h.Add(r.FinishTick);
            if (r.EndReason != null) foreach (char c in r.EndReason) h.Add(c);
            if (r.GoalPhysicsTicks != null) foreach (long t in r.GoalPhysicsTicks) h.Add(t);
            AddPlayer(ref h, sim);
            player = h.Value;
            h.Add((long)sim.Physics.StateDigest());
            return h.Value;
        }

        /// <summary><see cref="EndDigest"/> without the run's result (its tick counters count from where the run started).</summary>
        static ulong StateDigest(Simulation sim)
        {
            var h = b2Digest.Start();
            AddPlayer(ref h, sim);
            h.Add((long)sim.Physics.StateDigest());
            return h.Value;
        }

        static void AddPlayer(ref b2Digest h, Simulation sim)
        {
            Transform t0 = sim.Player.transform;
            Add(ref h, t0.position, 0); Add(ref h, t0.localScale, 0); Add(ref h, t0.rotation, 0);
            Add(ref h, sim.Player, 0);
            foreach (courseScript c in sim.Courses) Add(ref h, c, 0);
            UnityEngine.Random.State rng = UnityEngine.Random.state;
            h.Add(rng.s0); h.Add(rng.s1); h.Add(rng.s2); h.Add(rng.s3);
            var (px, py, angle, vx, vy, w) = sim.Physics.PlayerBody();
            h.Add(px); h.Add(py); h.Add(angle); h.Add(vx); h.Add(vy); h.Add(w);
        }

        /// <summary>A value's content, not its identity: world objects by instance id (the same in every load of an export).</summary>
        static void Add(ref b2Digest h, object value, int depth)
        {
            switch (value)
            {
                case null: h.Add(-1); return;
                case bool b: h.Add(b); return;
                case int i: h.Add(i); return;
                case long l: h.Add(l); return;
                case float f: h.Add(f); return;
                case double d: h.Add(BitConverter.DoubleToInt64Bits(d)); return;
                case string s: foreach (char c in s) h.Add(c); h.Add(-2); return;
                case Enum e: h.Add(Convert.ToInt64(e)); return;
                case UnityEngine.Object o when depth > 0: h.Add(o.GetInstanceID()); return;
                case System.Collections.IEnumerable items:
                    if (depth > 2) { h.Add(-3); return; }
                    foreach (object item in items) Add(ref h, item, depth + 1);
                    h.Add(-4);
                    return;
            }
            Type type = value.GetType();
            if (depth > 2 || type.IsPrimitive) { h.Add(type.IsPrimitive ? Convert.ToInt64(value) : -5); return; }
            foreach (FieldInfo field in FieldState.For(type)) Add(ref h, field.GetValue(value), depth + 1);
        }
    }
}
