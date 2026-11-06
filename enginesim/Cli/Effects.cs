using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    static class Effects
    {
        const int Window = 20;

        static readonly HashSet<string> Bookkeeping = new HashSet<string>
        {
            "Clock", "LastJumpPress", "LastJumpRelease", "LastDashPress", "JumpPresses", "DashPresses", "JumpReleases", "JumpHeld",
            "JumpButtonsDown", "_stepIndex", "_elapsedTicks", "_pressedStep", "_quickRestartStep", "_runTicks", "_physicsTicks",
            "_heldFrames", "_finishWaitTicks", "<Status>k__BackingField",
        };

        sealed class Counted
        {
            public readonly string Field;
            public readonly List<int> Baseline = new List<int>();
            public readonly HashSet<int> Dropped = new HashSet<int>();
            public int Start;
            public Counted(string field) { Field = field; }
        }

        public static int Run(string[] args)
        {
            if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) || args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("usage: effects <trace.json> <route.json> [--out F]");
            var clock = Stopwatch.StartNew();
            var (header, labels, inputs) = TechsUsed.Load(args[0], args[1], "effects");
            Simulation sim = TechsUsed.Create(args[0], header, labels);
            MacroEngine engine = sim.Engine;
            FieldInfo[] engineFields = FieldState.For(typeof(MacroEngine));
            bool[] counts = engineFields.Select(f => Bookkeeping.Contains(f.Name)).ToArray();
            var values = new object[engineFields.Length];
            FieldInfo dashBuffer = typeof(Movement).GetField("dashBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
            bool[] dashWaits = engineFields.Select(f => f.Name == "_dashJumpFrame" || f.Name == "_dashJumpAxis").ToArray();
            bool[] turnWaits = engineFields.Select(f => f.Name == "_pendingTurnX" || f.Name == "_pendingTurnY").ToArray();
            int GoalTicks()
            {
                if (sim.Goals == null) return 0;
                var h = new HashCode();
                for (int g = 0; g < sim.Goals.Next; g++) { h.Add(sim.Goals.PhysicsTicks[g]); h.Add(sim.Goals.HeldFrames[g]); }
                return h.ToHashCode();
            }
            int EngineHash()
            {
                FieldState.Read(engine, values);
                bool buffered = (bool)dashBuffer.GetValue(sim.Player), dashing = buffered || sim.Player.cutsceneMode == Movement.cutsceneModes.dash;
                var h = new HashCode();
                for (int k = 0; k < values.Length; k++)
                    if (!counts[k] && (dashing || !dashWaits[k]) && (buffered || !turnWaits[k])) h.Add(values[k] is ValueType || values[k] is string ? values[k]?.GetHashCode() ?? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(values[k]));
                return h.ToHashCode();
            }
            var jumps = new Counted("JumpPresses");
            var dashes = new Counted("DashPresses");
            var releases = new Counted("JumpReleases");
            Counted[] kinds = { jumps, dashes, releases };
            int Now(Counted c) => c == jumps ? engine.JumpPresses : c == dashes ? engine.DashPresses : engine.JumpReleases;
            int Saved(SimSnapshot s, Counted c) => (int)s.Engine[Array.FindIndex(engineFields, f => f.Name == c.Field)];

            SimSnapshot start = sim.Advance(new List<InputTick>());
            foreach (Counted c in kinds) c.Start = Saved(start, c);
            int n = inputs.Count;
            int extra = labels != null ? 1000000 : 400;

            var fingerprint = new int[n + 1];
            var engineHash = new int[n + 1];
            var physics = new long[n + 1];
            var held = new long[n + 1];
            foreach (Counted c in kinds) c.Baseline.Add(c.Start);
            RunResult route = sim.Run(inputs, start, t =>
            {
                if (t <= n)
                {
                    fingerprint[t] = sim.Fingerprint();
                    engineHash[t] = EngineHash() ^ GoalTicks();
                    physics[t] = engine.PhysicsTicks;
                    held[t] = engine.HeldFrames;
                }
                return true;
            }, extra, _ =>
            {
                foreach (Counted c in kinds)
                {
                    while (c.Baseline.Count <= engine.PhysicsTicks) c.Baseline.Add(c.Baseline[c.Baseline.Count - 1]);
                    c.Baseline[(int)engine.PhysicsTicks] = Now(c);
                }
            });
            int last = Math.Min(n - 1, route.FinishTick);
            bool SameEnd(RunResult r) => r.Finished == route.Finished && r.Time == route.Time && r.PhysicsTicks == route.PhysicsTicks
                && r.HeldFrames == route.HeldFrames && r.Progress == route.Progress
                && (route.GoalPhysicsTicks == null || route.GoalPhysicsTicks.SequenceEqual(r.GoalPhysicsTicks));

            var r = inputs.ToList();
            var inertX = new bool[n];
            var inertY = new bool[n];
            var turns = new HashSet<int>();
            var diverged = new bool[n + 1];
            int open = -1, tried = 0;
            SimSnapshot at = start;
            var missing = new Dictionary<Counted, List<int>>();

            bool Plays(int changed, out int back)
            {
                tried++;
                int from = Math.Max(changed, open) + 1, limit = from + Window;
                int found = -1;
                bool extraPress = false, ours = false;
                var before = kinds.ToDictionary(c => c, c => Saved(at, c));
                foreach (Counted c in kinds) missing[c] = new List<int>();
                RunResult played = sim.Run(new Prefix(r, Math.Min(n, limit + 2)), at, t =>
                {
                    if (t < from) return true;
                    if (t > limit || t > n) { ours = true; return false; }
                    if (fingerprint[t] == sim.Fingerprint() && engineHash[t] == (EngineHash() ^ GoalTicks()) && physics[t] == engine.PhysicsTicks && held[t] == engine.HeldFrames)
                    {
                        found = t;
                        ours = true;
                        return false;
                    }
                    return true;
                }, 400, _ =>
                {
                    int p = (int)engine.PhysicsTicks;
                    foreach (Counted c in kinds)
                    {
                        int now = Now(c), made = now - before[c];
                        before[c] = now;
                        if (p <= 0 || p >= c.Baseline.Count) { if (made > 0) extraPress = true; continue; }
                        var expected = new List<int>();
                        for (int o = c.Baseline[p - 1]; o < c.Baseline[p]; o++)
                            if (!c.Dropped.Contains(o - c.Start)) expected.Add(o - c.Start);
                        if (made > expected.Count) extraPress = true;
                        for (int k = 0; k < expected.Count - made; k++) missing[c].Add(expected[expected.Count - 1 - k]);
                    }
                });
                back = found;
                return !extraPress && (found >= 0 || !ours && SameEnd(played));
            }

            bool Drop(int i, int end, Func<InputTick, int, InputTick> change)
            {
                List<InputTick> saved = r.GetRange(i, end - i + 1);
                for (int t = i; t <= end; t++) r[t] = change(r[t], t);
                if (Plays(end, out int back))
                {
                    open = Math.Max(open, end);
                    for (int t = i; t <= (back < 0 ? n : back); t++) diverged[t] = true;
                    foreach (var (c, ords) in missing) foreach (int o in ords) c.Dropped.Add(o);
                    return true;
                }
                for (int t = i; t <= end; t++) r[t] = saved[t - i];
                return false;
            }

            for (int i = 0; i <= last; i++)
            {
                if (i > 0)
                {
                    SimSnapshot next = null;
                    sim.Run(new Prefix(r, Math.Min(n, i + 2)), at, t =>
                    {
                        if (t < i) return true;
                        next = sim.Capture(t);
                        return false;
                    }, 400);
                    if (next == null) break;
                    at = next;
                }
                if (r[i].dash)
                {
                    bool kept = Drop(i, i, (k, _) => { k.dash = false; k.dashJump = 0; k.turnX = k.turnY = null; return k; });
                    int? dash = missing[dashes].Count > 0 ? missing[dashes][0] : (int?)null;
                    if (!kept && dash.HasValue && (r[i].turnX.HasValue || r[i].turnY.HasValue)
                        && Drop(i, i, (k, _) => { k.turnX = k.turnY = null; return k; }))
                        turns.Add(dash.Value);
                    if (!kept && r[i].dashJump != 0) Drop(i, i, (k, _) => { k.dashJump = 0; return k; });
                }
                if (r[i].springDash != 0) Drop(i, i, (k, _) => { k.springDash = 0; return k; });
                if (r[i].press) Drop(i, i, (k, _) => { k.press = false; return k; });
                bool letGo = i > 0 && r[i - 1].held && !r[i].held;
                if (r[i].release || letGo)
                {
                    int end = i;
                    if (letGo) while (end + 1 < n && !r[end + 1].press && !r[end + 1].held && !r[end + 1].release) end++;
                    Drop(i, end, (k, t) => { if (t == i) k.release = false; if (letGo) k.held = true; return k; });
                }
                if (r[i].x != 0f && Drop(i, i, (k, _) => { k.x = 0f; return k; })) inertX[i] = true;
                if (r[i].y != 0f && Drop(i, i, (k, _) => { k.y = 0f; return k; })) inertY[i] = true;
            }

            var after = kinds.ToDictionary(c => c, c => c.Start);
            int wrong = -1;
            RunResult check = sim.Run(r, start, t =>
            {
                if (t <= n && !diverged[t] && fingerprint[t] != sim.Fingerprint() && wrong < 0) wrong = t;
                return true;
            }, extra, _ => { foreach (Counted c in kinds) after[c] = Now(c); });
            if (wrong >= 0 || !SameEnd(check) || kinds.Any(c => c.Baseline[c.Baseline.Count - 1] - after[c] != c.Dropped.Count))
                throw new InvalidOperationException("The inputs found inert don't play the route as it plays together"
                    + (wrong >= 0 ? " (its state differs before input tick " + wrong + ")" : !SameEnd(check) ? $" (it ends {check.EndReason}: {check.Finished} {check.Time} {check.PhysicsTicks} {check.HeldFrames} {check.Progress}, the route {route.EndReason}: {route.Finished} {route.Time} {route.PhysicsTicks} {route.HeldFrames} {route.Progress}; goal {Enumerable.Range(0, route.GoalPhysicsTicks?.Length ?? 0).FirstOrDefault(g => route.GoalPhysicsTicks[g] != check.GoalPhysicsTicks[g])}: {string.Join(",", route.GoalPhysicsTicks?.Zip(check.GoalPhysicsTicks).Where(p => p.First != p.Second).Take(5) ?? Enumerable.Empty<(long, long)>())})" : " (its presses differ)"));

            int Total(Counted c) => c.Baseline[c.Baseline.Count - 1] - c.Start;
            int[][] Ranges(bool[] marks)
            {
                var ranges = new List<int[]>();
                for (int t = 0; t < marks.Length; t++)
                {
                    if (!marks[t]) continue;
                    if (ranges.Count > 0 && ranges[ranges.Count - 1][1] == t - 1) ranges[ranges.Count - 1][1] = t;
                    else ranges.Add(new[] { t, t });
                }
                return ranges.ToArray();
            }
            string json = JsonSerializer.Serialize(new
            {
                inputs = n, finishTick = route.FinishTick, finished = route.Finished,
                jump = jumps.Dropped.OrderBy(o => o), dash = dashes.Dropped.OrderBy(o => o), turn = turns.OrderBy(o => o),
                release = releases.Dropped.OrderBy(o => o), x = Ranges(inertX), y = Ranges(inertY),
                totals = new { jump = Total(jumps), dash = Total(dashes), release = Total(releases) },
                tried, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
            });
            string outPath = Solve.Option(args, "--out", (string)null);
            if (outPath != null) File.WriteAllText(outPath, json);
            Console.WriteLine($"{Path.GetFileName(args[1])}: inert {jumps.Dropped.Count} of {Total(jumps)} jump presses, {dashes.Dropped.Count} of {Total(dashes)} dashes "
                + $"({turns.Count} turns), {releases.Dropped.Count} of {Total(releases)} releases, x on {inertX.Count(b => b)} and y on {inertY.Count(b => b)} input ticks "
                + $"({tried} drops tried, {clock.Elapsed.TotalSeconds:0.0} s)");
            if (outPath == null) Console.WriteLine(json);
            return 0;
        }

        sealed class Prefix : IReadOnlyList<InputTick>
        {
            readonly List<InputTick> _list;
            public Prefix(List<InputTick> list, int count) { _list = list; Count = count; }
            public InputTick this[int index] => _list[index];
            public int Count { get; }
            public IEnumerator<InputTick> GetEnumerator() { for (int i = 0; i < Count; i++) yield return _list[i]; }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
