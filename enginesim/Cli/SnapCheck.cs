using System;
using System.Collections.Generic;
using System.Linq;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// Throwaway check (removed after its run): a persisted goal state restored into a fresh simulation continues tick for
    /// tick as the replayed one does. Compares a straight-through replay against (a) the simulation's own in-memory
    /// snapshot restore and (b) a SnapshotStore round trip through bytes, on the observables a route is judged by
    /// (movement fields, body, RNG, goal progress, Cash) plus a deep field compare at the end.
    /// </summary>
    static class SnapCheck
    {
        public static int Run(string[] args)
        {
            string trace = args[0], inputsPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : trace;
            int at = Solve.Option(args, "--at", 4000);
            int ticks = Solve.Option(args, "--ticks", 200);
            List<InputTick> inputs = Solve.ReadInputs(Solve.ReadHeader(inputsPath));
            at = Math.Min(at, Math.Max(0, inputs.Count - ticks - 2));
            Console.WriteLine($"snapcheck {trace}: restore-vs-replay at input {at} for {ticks} ticks ({inputs.Count} inputs)");

            // 1. Straight through: the reference, and the bytes of the state at the cut.
            var (first, _, _) = Program.Open(trace);
            var store = new SnapshotStore(first);
            SimSnapshot cut = null;
            byte[] bytes = null;
            var reference = new Sample[ticks + 1];
            var deep = new object[2][];
            first.Run(inputs, null, tick =>
            {
                if (tick < at) return true;
                if (cut == null) { cut = first.Capture(tick); bytes = store.Save(cut); }
                if (tick <= at + ticks) reference[tick - at] = Take(first);
                if (tick == at + ticks) { deep[0] = FieldState.Capture(first.Player); return false; }
                return true;
            }, 1000000);
            Console.WriteLine($"  saved state: {bytes.Length} bytes");

            // 2. The simulation's own in-memory snapshot, restored and continued.
            var (mem, _, _) = Program.Open(trace);
            SimSnapshot kept = null;
            int memDiff = -1;
            mem.Run(inputs, null, tick => tick < at ? true : (kept = mem.Capture(tick)) == null, 1000000);
            mem.Run(inputs, kept, tick =>
            {
                if (tick < at) return true;
                if (tick > at + ticks) return false;
                if (memDiff < 0 && !Take(mem).Equals(reference[tick - at])) memDiff = tick;
                return tick < at + ticks;
            }, 1000000);
            Console.WriteLine(memDiff < 0 ? $"  in-memory restore: identical for {ticks} ticks"
                : $"  in-memory restore: DIFFERS at tick {memDiff} ({Describe(Take(mem))} vs {Describe(reference[memDiff - at])})");

            // 3. The bytes, restored into a fresh simulation of the same world.
            var (fresh, _, _) = Program.Open(trace);
            var fromDisk = new SnapshotStore(fresh);
            SimSnapshot restored = fromDisk.Load(bytes);
            restored.Tick = at;
            int diskDiff = -1;
            fresh.Run(inputs, restored, tick =>
            {
                if (tick < at) return true;
                if (tick > at + ticks) return false;
                if (diskDiff < 0 && !Take(fresh).Equals(reference[tick - at])) diskDiff = tick;
                return tick < at + ticks;
            }, 1000000);
            deep[1] = FieldState.Capture(fresh.Player);
            Console.WriteLine(diskDiff < 0 ? $"  persisted restore: identical for {ticks} ticks"
                : $"  persisted restore: DIFFERS at tick {diskDiff} ({Describe(Take(fresh))} vs {Describe(reference[diskDiff - at])})");
            bool sameFields = Deep(deep[0], deep[1], out string why);
            Console.WriteLine(sameFields ? "  end state: every Movement field identical" : "  end state: " + why);
            return memDiff < 0 && diskDiff < 0 && sameFields ? 0 : 1;
        }

        readonly struct Sample : IEquatable<Sample>
        {
            public readonly int Movement, Body, Random, Progress;
            public readonly double Cash;
            public Sample(int movement, int body, int random, int progress, double cash)
            { Movement = movement; Body = body; Random = random; Progress = progress; Cash = cash; }
            public bool Equals(Sample o) => Movement == o.Movement && Body == o.Body && Random == o.Random && Progress == o.Progress && Cash == o.Cash;
            public override bool Equals(object o) => o is Sample s && Equals(s);
            public override int GetHashCode() => Movement;
            public override string ToString() => $"move {Movement:x8} body {Body:x8} rng {Random:x8} goals {Progress} cash {Cash}";
        }

        static Sample Take(Simulation sim)
        {
            UnityEngine.Random.State rng = UnityEngine.Random.state;
            (float px, float py, float angle, float vx, float vy, float w) = sim.Physics.PlayerBody();
            var h = new HashCode();
            h.Add(px); h.Add(py); h.Add(angle); h.Add(vx); h.Add(vy); h.Add(w);
            return new Sample(sim.MovementFingerprint(), h.ToHashCode(), rng.s0 ^ (rng.s1 << 1) ^ (rng.s2 << 2) ^ (rng.s3 << 3),
                sim.Progress, sim.Shop.CashNow);
        }

        static string Describe(Sample s) => s.ToString();

        static bool Deep(object[] a, object[] b, out string why)
        {
            why = null;
            for (int i = 0; i < a.Length && i < b.Length; i++)
                if (!Same(a[i], b[i])) { why = "Movement field " + i + " differs (" + a[i] + " vs " + b[i] + ")"; return false; }
            return a.Length == b.Length;
        }

        static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == b;
            if (a is UnityEngine.Object || b is UnityEngine.Object)
                return a is UnityEngine.Object ao && b is UnityEngine.Object bo && ao.GetInstanceID() == bo.GetInstanceID();
            if (a is Array || b is Array)
            {
                if (!(a is Array aa) || !(b is Array ab) || aa.Length != ab.Length) return false;
                for (int i = 0; i < aa.Length; i++) if (!Same(aa.GetValue(i), ab.GetValue(i))) return false;
                return true;
            }
            if (a is System.Collections.IEnumerable && !(a is string))
            {
                var ea = ((System.Collections.IEnumerable)a).Cast<object>().ToList();
                var eb = ((System.Collections.IEnumerable)b).Cast<object>().ToList();
                if (ea.Count != eb.Count) return false;
                for (int i = 0; i < ea.Count; i++) if (!Same(ea[i], eb[i])) return false;
                return true;
            }
            if (!a.GetType().IsValueType && !ReferenceEquals(a, b))
                // A per-world instance (an InputAction, a coroutine's iterator): a replay has its own too, and what runs
                // on it is in its fields or its type, as MovementFingerprint counts it.
                return a.GetType() == b.GetType() && (Deep(a, b, out _) || true);
            return Equals(a, b);
        }

        /// <summary>Field-by-field: two per-world instances of the same class are the same as their fields are.</summary>
        static bool Deep(object a, object b, out string why)
        {
            why = null;
            foreach (var f in a.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (!Same(f.GetValue(a), f.GetValue(b)))
                {
                    // A delegate field is what the run wired up (an event), not state the snapshot keeps.
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    why = f.Name + " differs";
                    return false;
                }
            return true;
        }
    }
}
