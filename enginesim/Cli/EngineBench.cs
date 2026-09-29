using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// Engine bench: enginebench &lt;trace&gt; &lt;route&gt; [--scale K] [--threads T] [--only full|restore|capture|branch|mt]
    /// Fixed operation counts (so two builds do the same work in the same order and print comparable digests): full
    /// replays, restores and captures at snapshots along the route, branch runs (restore + N ticks, like a plan edge;
    /// 80-tick ones across the rebase), and branch runs on T threads.
    /// </summary>
    static class EngineBench
    {
        public static int Run(string[] args)
        {
            string trace = args[0], route = args[1];
            // Timing on a shared machine: stay ahead of the background searches for the few seconds a bench runs.
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            int scale = Solve.Option(args, "--scale", 1);
            int threads = Solve.Option(args, "--threads", 4);
            int only = Array.IndexOf(args, "--only");
            string what = only > 0 ? args[only + 1] : "all";
            JsonElement header = Solve.ReadHeader(trace);
            Func<Simulation> make = Solve.Creator(trace, header);
            List<InputTick> inputs;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(route), new JsonDocumentOptions { MaxDepth = 256 }))
                inputs = doc.RootElement.TryGetProperty("ticks", out JsonElement ticks) ? ticks.EnumerateArray().Select(InputTick.FromJson).ToList() : Solve.ReadInputs(doc.RootElement);
            int goals = header.GetProperty("fulltas").GetProperty("goals").GetArrayLength();
            Simulation sim = make();
            sim.StrictBuys = true;
            sim.Goals.Limit = goals;
            SimSnapshot origin = sim.Start;
            RunResult first = sim.Run(inputs, null, null, 1000000);
            Console.WriteLine($"route: {inputs.Count} inputs, progress {first.Progress}, physics ticks {first.PhysicsTicks}, end {first.EndReason}, digest {Digest(sim, first)}");

            if (what == "all" || what == "full")
            {
                long gc0 = GC.CollectionCount(0), alloc0 = GC.GetTotalAllocatedBytes();
                var clock = Stopwatch.StartNew();
                TimeSpan cpu0 = Cpu();
                int runs = 4 * scale, fp = 0;
                long stepped = 0;
                for (int i = 0; i < runs; i++) { RunResult r = sim.Run(inputs, null, null, 1000000); stepped += r.Stepped; fp = unchecked(fp * 31 + Digest(sim, r)); }
                double s = clock.Elapsed.TotalSeconds;
                Console.WriteLine($"full replay: {runs / s:0.00} runs/s, {stepped / s:0} ticks/s, {s * 1e6 / stepped:0.00} us/tick, cpu {(Cpu() - cpu0).TotalSeconds * 1e6 / stepped:0.00} us/tick, "
                    + $"alloc {(GC.GetTotalAllocatedBytes() - alloc0) / (double)stepped:0} B/tick, gen0 {GC.CollectionCount(0) - gc0}, digest {fp}");
            }

            int[] at = { 200, 800, 1000, 1400, 1650 };
            var snaps = new List<(int at, SimSnapshot snap)>();
            {
                var clock = Stopwatch.StartNew();
                foreach (int a in at)
                {
                    sim.SetStart(origin, 0);
                    snaps.Add((a, sim.Advance(inputs.Take(a).ToList())));
                }
                sim.SetStart(origin, 0);
                Console.WriteLine($"advance+capture of {at.Length} prefixes: {clock.Elapsed.TotalMilliseconds:0} ms");
            }

            if (what == "all" || what == "restore")
                foreach (var (a, snap) in snaps)
                {
                    long alloc0 = GC.GetTotalAllocatedBytes();
                    int n = 20000 * scale;
                    var clock = Stopwatch.StartNew();
                    TimeSpan cpu0 = Cpu();
                    for (int i = 0; i < n; i++) sim.Restore(snaps[(i & 1) == 0 ? 0 : snaps.FindIndex(x => x.at == a)].snap);
                    double s = clock.Elapsed.TotalSeconds;
                    Console.WriteLine($"restore @{a} (alternating with @200): {n / s:0} restores/s, {s * 1e6 / n:0.0} us, cpu {(Cpu() - cpu0).TotalSeconds * 1e6 / n:0.0} us, alloc {(GC.GetTotalAllocatedBytes() - alloc0) / (double)n:0} B/restore");
                }

            if (what == "all" || what == "capture")
                foreach (var (a, snap) in snaps)
                {
                    sim.Restore(snap);
                    long alloc0 = GC.GetTotalAllocatedBytes();
                    int n = 10000 * scale;
                    var clock = Stopwatch.StartNew();
                    TimeSpan cpu0 = Cpu();
                    for (int i = 0; i < n; i++) { sim.Restore(snap); sim.Capture(0); }
                    double s = clock.Elapsed.TotalSeconds;
                    Console.WriteLine($"restore+capture @{a}: {n / s:0}/s, {s * 1e6 / n:0.0} us, cpu {(Cpu() - cpu0).TotalSeconds * 1e6 / n:0.0} us, alloc {(GC.GetTotalAllocatedBytes() - alloc0) / (double)n:0} B");
                }

            if (what == "all" || what == "branch" || what == "cross")
            {
                if (what != "cross")
                foreach (var (a, snap) in snaps)
                {
                    List<InputTick> rest = inputs.Skip(a).ToList();
                    var (edges, ticks, s, fp) = Branches(sim, snap, rest, 17, 3000 * scale);
                    Console.WriteLine($"branch @{a} (17 ticks): {edges / s:0} edges/s, {ticks / s:0} ticks/s, {s * 1e6 / edges:0.0} us/edge, cpu {LastCpu * 1e6 / edges:0.0} us/edge, digest {fp}");
                }
                {
                    var (a, snap) = snaps[2];
                    List<InputTick> rest = inputs.Skip(a).ToList();
                    var (edges, ticks, s, fp) = Branches(sim, snap, rest, 120, 300 * scale);
                    Console.WriteLine($"branch @{a} (120 ticks, across the rebase): {edges / s:0} edges/s, {ticks / s:0} ticks/s, {s * 1e6 / edges:0.0} us/edge, cpu {LastCpu * 1e6 / edges:0.0} us/edge, digest {fp}");
                }
            }

            if (what == "all" || what == "mt")
            {
                int a = 1400;
                long totalEdges = 0, totalTicks = 0;
                double wall = 0;
                var ready = new CountdownEvent(threads);
                var go = new ManualResetEventSlim();
                var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
                {
                    Simulation mine = make();
                    mine.StrictBuys = true;
                    mine.Goals.Limit = goals;
                    SimSnapshot s0 = mine.Advance(inputs.Take(a).ToList());
                    List<InputTick> rest = inputs.Skip(a).ToList();
                    ready.Signal();
                    go.Wait();
                    var (edges, ticks, s, _) = Branches(mine, s0, rest, 17, 6000 * scale);
                    Interlocked.Add(ref totalEdges, edges);
                    Interlocked.Add(ref totalTicks, ticks);
                    lock (Gate) wall = Math.Max(wall, s);
                }, 64 << 20)).ToArray();
                foreach (Thread w in workers) w.Start();
                ready.Wait();
                long gc0 = GC.CollectionCount(0);
                go.Set();
                foreach (Thread w in workers) w.Join();
                Console.WriteLine($"mt {threads} threads branch @{a} (17 ticks): {totalEdges / wall:0} edges/s, {totalTicks / wall:0} ticks/s ({totalTicks / wall / threads:0} per thread), gen0 {GC.CollectionCount(0) - gc0}");
            }
            return 0;
        }

        static readonly object Gate = new object();
        [ThreadStatic] static double LastCpu;
        static TimeSpan Cpu() => Process.GetCurrentProcess().TotalProcessorTime;
        static readonly StreamWriter Dump = Environment.GetEnvironmentVariable("IGTAP_BENCH_DUMP") is string d && d.Length > 0 ? new StreamWriter(d) { AutoFlush = true } : null;

        /// <summary>A process-independent digest of the state after a run (HashCode and identity hashes vary per process).</summary>
        internal static int Digest(Simulation sim, RunResult r)
        {
            ulong h = 14695981039346656037UL;
            void Mix(long v) { for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; } }
            void F(float f) { Mix(BitConverter.SingleToInt32Bits(f)); }
            WorldPos p = sim.PlayerWorld;
            F(p.x); F(p.y);
            var b = sim.Physics.PlayerBody();
            F(b.px); F(b.py); F(b.vx); F(b.vy); F(b.angle); F(b.w);
            var rng = UnityEngine.Random.state;
            Mix(rng.s0); Mix(rng.s1); Mix(rng.s2); Mix(rng.s3);
            Mix(r.Stepped); Mix(r.PhysicsTicks); Mix(r.Progress); Mix(r.Finished ? 1 : 0); F(r.Time);
            if (r.EndReason != null) foreach (char c in r.EndReason) Mix(c);
            if (r.GoalPhysicsTicks != null) foreach (long t in r.GoalPhysicsTicks) Mix(t);
            return (int)(h ^ (h >> 32));
        }

        static (long edges, long ticks, double seconds, int fingerprint) Branches(Simulation sim, SimSnapshot snap, List<InputTick> rest, int branch, int count)
        {
            // A plan edge: the snapshot's inputs are rebased to 0, so the run's inputs are the edge.
            var rng = new Random(1);
            var clock = Stopwatch.StartNew();
            TimeSpan cpu0 = Cpu();
            long ticks = 0;
            int fp = 0;
            var edge = new List<InputTick>(branch + 1);
            for (int e = 0; e < count; e++)
            {
                edge.Clear();
                int off = rng.Next(0, Math.Max(1, rest.Count - branch));
                for (int i = 0; i < branch && off + i < rest.Count; i++) edge.Add(rest[off + i]);
                sim.SetStart(snap, 0);
                RunResult r = sim.Run(edge, snap, extraTicks: 0);
                ticks += r.Stepped;
                int d = Digest(sim, r);
                fp = unchecked(fp * 31 + d);
                Dump?.WriteLine($"{e} off {off} {d} stepped {r.Stepped} end {r.EndReason}");
            }
            LastCpu = (Cpu() - cpu0).TotalSeconds;
            return (count, ticks, clock.Elapsed.TotalSeconds, fp);
        }
    }
}
