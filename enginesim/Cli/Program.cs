using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    static class Program
    {
        static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                switch (args.FirstOrDefault())
                {
                    case "replay": return Replay(args.Skip(1).ToArray());
                    case "bench": return Bench(args.Skip(1).ToArray());
                    case "solve": return Solve.Run(args.Skip(1).ToArray());
                    case "rays": return Rays(args[1], args[2]);
                    case "branch": return Branch(args[1], args.Length > 2 ? int.Parse(args[2]) : 200);
                    case "path":
                    {
                        // path <start-trace> <route.json>: where the course timer starts/stops along a route.
                        var (sim, _, _) = Open(args[1]);
                        using JsonDocument route = JsonDocument.Parse(File.ReadAllText(args[2]));
                        JsonElement list = route.RootElement.TryGetProperty("ticks", out JsonElement ticksList) ? ticksList : route.RootElement.GetProperty("inputs");
                        var inputs = list.EnumerateArray().Select(t => new InputTick
                        {
                            x = t.GetProperty("x").GetSingle(), held = t.GetProperty("held").GetBoolean(), press = t.GetProperty("press").GetBoolean(),
                            release = t.GetProperty("release").GetBoolean(), dash = t.GetProperty("dash").GetBoolean(), dashJump = t.GetProperty("dashJump").GetInt32(),
                        }).ToList();
                        // --verbose: every tick with the movement state a trick depends on.
                        bool verbose = args.Contains("--verbose");
                        string[] fields = { "Velocity", "momentum", "onGround", "OnWall", "cutsceneMode", "framesSinceLastDash", "airDashesLeft", "jumpBuffer", "wallCoyoteFrames" };
                        var infos = fields.Select(f => typeof(Movement).GetField(f, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).ToArray();
                        bool was = false;
                        int lastRun = 0;
                        var bf = BindingFlags.Instance | BindingFlags.NonPublic;
                        FieldInfo counter = typeof(spikeScript).GetField("counter", bf), grace = typeof(spikeScript).GetField("graceFrames", bf),
                            onDash = typeof(spikeScript).GetField("killPlayerOnDash", bf), killed = typeof(spikeScript).GetField("playerKilled", bf);
                        spikeScript[] spikes = sim.World.All<spikeScript>();
                        FieldInfo dashActive = typeof(Movement).GetField("dashActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        RunResult r = sim.Run(inputs, onStep: st =>
                        {
                            // Spike stays (spikeScript.counter > 0): the game kills when counter > graceFrames, or at once while dashing.
                            foreach (spikeScript s in spikes)
                                if ((int)counter.GetValue(s) > 0)
                                    Console.WriteLine($"tick {st}: SPIKE {s.name} at {s.transform.position.x:0},{s.transform.position.y:0} counter {counter.GetValue(s)} graceFrames {grace.GetValue(s)}"
                                        + $" killOnDash {onDash.GetValue(s)} playerKilled {killed.GetValue(s)} dashActive {dashActive.GetValue(sim.Player)}"
                                        + $" player {sim.Player.transform.position.x:0.0},{sim.Player.transform.position.y:0.0}");
                            bool timing = sim.TimingCourse() != null;
                            if (verbose)
                            {
                                // The input this tick consumed; dash ticks consume none (the game skips its input hook).
                                int run = (int)sim.Engine.RunTicks - 1;
                                InputTick k = run >= 0 && run < inputs.Count ? inputs[run] : default;
                                string input = run + 1 == lastRun ? "(no input)" : $"in[{run}] x={k.x}{(k.press ? " press" : "")}{(k.held ? " held" : "")}"
                                    + (k.release ? " release" : "") + (k.dash ? " dash" + (k.dashJump > 0 ? "+jump@" + k.dashJump : "") : "");
                                lastRun = run + 1;
                                Console.WriteLine($"tick {st}: pos {sim.Player.transform.position.x:0.0},{sim.Player.transform.position.y:0.0}"
                                    + (timing ? " t=" + Simulation.CourseTime(sim.TimingCourse()).ToString("0.00") : "") + " " + input + " | "
                                    + string.Join(" ", fields.Select((f, i) => f + "=" + infos[i]?.GetValue(sim.Player))));
                            }
                            else if (timing != was || st % 25 == 0)
                                Console.WriteLine($"tick {st}: pos {sim.Player.transform.position.x:0.0},{sim.Player.transform.position.y:0.0} timing {timing}"
                                    + (timing ? " t=" + Simulation.CourseTime(sim.TimingCourse()) : ""));
                            was = timing;
                        });
                        Console.WriteLine($"finished {r.Finished} time {r.Time} tick {r.FinishTick}");
                        return 0;
                    }
                    default:
                        Console.Error.WriteLine("usage:\n  replay <trace.json>... [--verbose]   compare offline runs with turbo traces tick by tick\n"
                            + "  bench <trace.json> [seconds]           runs/s and ticks/s on one core\n"
                            + "  solve <start-trace.json> [--threads N] [--minutes M] [--seed <trace.json>]... [--no-submit]\n"
                            + "                                         offline solver from that trace's start state; bests that beat\n"
                            + "                                         the seeds are confirmed in turbo (/api/enginesim/submit) when the game runs\n"
                            + "  rays <trace.json> <rays.json>          offline raycasts in the /api/enginesim/raycast format\n"
                            + "  branch <trace.json> [runs]             resumed (branched) runs must step exactly like from-start runs");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        internal static (Simulation sim, JsonDocument trace, List<InputTick> inputs) Open(string tracePath)
        {
            string worldPath = Path.ChangeExtension(tracePath, null) + ".world.json";
            World world = World.Load(worldPath);
            var sim = new Simulation(world);
            JsonDocument trace = JsonDocument.Parse(File.ReadAllText(tracePath), new JsonDocumentOptions { MaxDepth = 256 });
            sim.TargetCourse = trace.RootElement.GetProperty("course").GetInt32();
            if (trace.RootElement.TryGetProperty("finish", out JsonElement finish))
                sim.TargetFinish = (finish[0].GetSingle(), finish[1].GetSingle(), finish[2].GetSingle(), finish[3].GetSingle());
            sim.LoadStart(trace.RootElement.GetProperty("start"));
            var inputs = trace.RootElement.GetProperty("inputs").EnumerateArray().Select(t => new InputTick
            {
                x = t.GetProperty("x").GetSingle(),
                held = t.GetProperty("held").GetBoolean(),
                press = t.GetProperty("press").GetBoolean(),
                release = t.GetProperty("release").GetBoolean(),
                dash = t.GetProperty("dash").GetBoolean(),
                dashJump = t.GetProperty("dashJump").GetInt32(),
            }).ToList();
            return (sim, trace, inputs);
        }

        // Runs each trace's inputs offline from its start state and reports the first tick where any state differs.
        static int Replay(string[] args)
        {
            bool verbose = args.Contains("--verbose");
            int failures = 0;
            var assetRef = new System.Text.RegularExpressions.Regex("\\{\"\\$ref\":(-?\\d+)\\}");
            foreach (string path in args.Where(a => !a.StartsWith("--") && !a.EndsWith(".world.json", StringComparison.OrdinalIgnoreCase)))
            {
                var (sim, trace, inputs) = Open(path);
                // Assets (audio clips, sprites) are not scene objects and never exported; compare them as null.
                _normalize = s => assetRef.Replace(s, m => sim.World.Objects.ContainsKey(int.Parse(m.Groups[1].Value)) ? m.Value : "null");
                JsonElement[] ticks = trace.RootElement.GetProperty("ticks").EnumerateArray().ToArray();
                JsonElement result = trace.RootElement.GetProperty("result");
                FieldInfo[] movementFields = FieldState.For(typeof(Movement));
                int firstDiff = -1;
                var diffs = new List<string>();
                RunResult run = sim.Run(inputs, onStep: step =>
                {
                    if (args.Contains("--contacts") && step < 40)
                    {
                        Console.WriteLine("  step " + step + " pos " + Json(sim.Player.transform.position) + " turbo contacts " + (step < ticks.Length ? ticks[step].GetProperty("contacts").GetRawText() : ""));
                        foreach (string c in sim.Physics.DescribeContacts()) Console.WriteLine("    " + c);
                    }
                    if (step >= ticks.Length || firstDiff >= 0 && !verbose) return;
                    var here = new List<string>();
                    JsonElement t = ticks[step];
                    Compare(here, "position", Json(sim.Player.transform.position), t.GetProperty("position"));
                    Compare(here, "bodyPosition", Json(sim.Physics.GetPosition(sim.Player.GetComponent<Rigidbody2D>())), t.GetProperty("bodyPosition"));
                    Compare(here, "velocity", Json(sim.Physics.GetVelocity(sim.Player.GetComponent<Rigidbody2D>())), t.GetProperty("velocity"));
                    foreach (JsonElement entry in t.GetProperty("movement").EnumerateArray())
                    {
                        FieldInfo f = World.FindField(typeof(Movement), entry[0].GetString(), entry[1].GetString());
                        if (f == null || PresentationOnly.Contains(f.Name)) continue;
                        Compare(here, f.Name, Json(f.GetValue(sim.Player)), entry[2]);
                    }
                    foreach (JsonElement course in t.GetProperty("courses").EnumerateArray())
                    {
                        if (!sim.World.Objects.TryGetValue(course.GetProperty("id").GetInt32(), out UnityEngine.Object o) || !(o is courseScript c)) continue;
                        foreach (JsonElement entry in course.GetProperty("fields").EnumerateArray())
                        {
                            string name = entry[1].GetString();
                            if (name != "tracking" && name != "currentPathTime") continue;
                            FieldInfo f = World.FindField(typeof(courseScript), entry[0].GetString(), name);
                            Compare(here, "course" + c.courseNumber + "." + name, Json(f.GetValue(c)), entry[2]);
                        }
                    }
                    if (here.Count == 0) return;
                    if (firstDiff < 0) firstDiff = step;
                    if (diffs.Count < 60) diffs.Add("tick " + step + ": " + string.Join("; ", here));
                });
                bool sameResult = run.Finished == result.GetProperty("finished").GetBoolean()
                    && Json(run.Time) == result.GetProperty("time").GetRawText();
                Console.WriteLine(Path.GetFileName(path) + ": " + run.Stepped + "/" + ticks.Length + " ticks, "
                    + (firstDiff < 0 ? "every tick identical" : "first difference at tick " + firstDiff)
                    + ", result " + (run.Finished ? "finish " + Json(run.Time) : "no finish (" + run.EndReason + ")")
                    + " vs turbo " + (result.GetProperty("finished").GetBoolean() ? "finish " + result.GetProperty("time").GetRawText() : "no finish")
                    + (sameResult ? " [MATCH]" : " [MISMATCH]"));
                foreach (string d in diffs.Take(verbose ? 60 : 3)) Console.WriteLine("  " + d);
                if (firstDiff >= 0 || !sameResult || run.Stepped != ticks.Length) failures++;
            }
            return failures == 0 ? 0 : 3;
        }

        // Movement fields written only by presentation code the host cuts (enginesim/cuts.txt).
        static readonly HashSet<string> PresentationOnly = new HashSet<string> { "timeSinceLastAfterimage", "afterimageTimeAlive", "activeAfterimages", "inactiveAfterimages" };

        static Func<string, string> _normalize = s => s;

        static void Compare(List<string> diffs, string name, string offline, JsonElement turbo)
        {
            // Mono and .NET format a float's G9 text differently when it ends in an exact half (2424.140625 prints as
            // 2424.14063 in the game, 2424.14062 here); compare the float values, not their text.
            string expected = Numbers(_normalize(turbo.GetRawText()));
            string actual = Numbers(offline);
            if (actual != expected) diffs.Add(name + " " + Short(offline) + " != " + Short(turbo.GetRawText()));
        }

        static readonly System.Text.RegularExpressions.Regex Number = new System.Text.RegularExpressions.Regex(@"-?\d+(\.\d+)?([eE][-+]?\d+)?");

        static string Numbers(string s)
        {
            return Number.Replace(s, m =>
            {
                string digits = m.Value.Split('e', 'E')[0].Replace("-", "").Replace(".", "").TrimStart('0');
                return digits.Length <= 9
                    ? float.Parse(m.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)
                    : double.Parse(m.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            });
        }

        static string Short(string s) { return s.Length > 120 ? s.Substring(0, 120) + "..." : s; }

        // The plugin's EngineSimJson encoding, so values compare as text.
        internal static string Json(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value, int depth, HashSet<object> visiting)
        {
            switch (value)
            {
                case null: sb.Append("null"); return;
                case UnityEngine.Object o:
                    if (!o) sb.Append("null");
                    else sb.Append("{\"$ref\":").Append(o.GetInstanceID()).Append('}');
                    return;
                case float f:
                    sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "\"" + f.ToString(CultureInfo.InvariantCulture) + "\"" : f.ToString("G9", CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "\"" + d.ToString(CultureInfo.InvariantCulture) + "\"" : d.ToString("G17", CultureInfo.InvariantCulture));
                    return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string s: sb.Append(JsonSerializer.Serialize(s)); return;
                case LayerMask m: sb.Append(m.value); return;
                case Delegate _: case Type _: case MemberInfo _: case IntPtr _: case Coroutine _: case IEnumerator _: sb.Append("null"); return;
            }
            Type type = value.GetType();
            if (type.IsEnum) { sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)); return; }
            if (type.IsPrimitive) { sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (depth > 8 || !type.IsValueType && !visiting.Add(value)) { sb.Append("null"); return; }
            try
            {
                if (value is IDictionary dict)
                {
                    sb.Append("{\"$dict\":[");
                    bool first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('[');
                        Write(sb, e.Key, depth + 1, visiting);
                        sb.Append(',');
                        Write(sb, e.Value, depth + 1, visiting);
                        sb.Append(']');
                    }
                    sb.Append("]}");
                    return;
                }
                if (value is IEnumerable seq)
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in seq)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item, depth + 1, visiting);
                    }
                    sb.Append(']');
                    return;
                }
                if (!type.IsValueType && type.Assembly != typeof(Movement).Assembly) { sb.Append("null"); return; }
                sb.Append('{');
                bool firstField = true;
                for (Type t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
                    foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                        if (!firstField) sb.Append(',');
                        firstField = false;
                        sb.Append(JsonSerializer.Serialize(t == type ? field.Name : t.Name + "::" + field.Name)).Append(':');
                        Write(sb, field.GetValue(value), depth + 1, visiting);
                    }
                sb.Append('}');
            }
            finally
            {
                if (!type.IsValueType) visiting.Remove(value);
            }
        }

        // Raycasts against a trace's world, in the /api/enginesim/raycast format, for differential checks against Unity.
        static int Rays(string tracePath, string raysPath)
        {
            var (sim, _, _) = Open(tracePath);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(raysPath));
            var output = new List<object>();
            foreach (JsonElement r in doc.RootElement.GetProperty("rays").EnumerateArray())
            {
                RaycastHit2D hit = Physics2D.Raycast(new Vector2(r[0].GetSingle(), r[1].GetSingle()), new Vector2(r[2].GetSingle(), r[3].GetSingle()),
                    r[4].GetSingle(), r[5].GetInt32());
                output.Add(hit.collider == null ? null : new object[] { hit.collider.GetInstanceID(), G9(hit.point.x), G9(hit.point.y),
                    G9(hit.normal.x), G9(hit.normal.y), G9(hit.distance), G9(hit.fraction) });
            }
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, hits = output }));
            return 0;
        }

        static string G9(float f) { return f.ToString("G9", CultureInfo.InvariantCulture); }

        // Branch determinism, the solver's way: new inputs resumed from an archived snapshot (taken in an earlier, possibly
        // branched, run) must step exactly like the same inputs from the start (player, engine, courses, Invokes, body).
        static int Branch(string tracePath, int runs)
        {
            var (sim, _, route) = Open(tracePath);
            var rng = new System.Random(1);
            FieldInfo[] engineFields = FieldState.For(typeof(MacroEngine));
            string State()
            {
                var sb = new StringBuilder();
                sb.Append(Json(FieldState.Capture(sim.Player))).Append('|').Append(Json(sim.Player.transform.position));
                object[] engine = sim.Engine.CaptureState();
                for (int i = 0; i < engine.Length; i++)
                    if (!(engineFields[i].Name is "_steps" or "_stepIndex" or "_elapsedTicks" or "_elapsedMs" or "_wallJumpTriggered" or "<Status>k__BackingField"))
                        sb.Append('|').Append(engineFields[i].Name).Append('=').Append(Json(engine[i]));
                foreach (courseScript c in sim.Courses) sb.Append('|').Append(Json(FieldState.Capture(c)));
                foreach (Scheduler.Pending p in sim.Scheduler.Queue) sb.Append("|inv ").Append(p.Method).Append('@').Append(p.Due - sim.Scheduler.Tick);
                Rigidbody2D body = sim.Player.GetComponent<Rigidbody2D>();
                sb.Append('|').Append(Json(sim.Physics.GetPosition(body))).Append(Json(sim.Physics.GetVelocity(body)));
                return sb.ToString();
            }
            List<InputTick> Burst(List<InputTick> inputs, int count)
            {
                float x = inputs.Count > 0 ? inputs[inputs.Count - 1].x : 1f;
                while (inputs.Count < count)
                {
                    if (rng.NextDouble() < .35) x = new[] { -1f, 0f, 1f }[rng.Next(3)];
                    int length = rng.Next(3, 21), at = inputs.Count;
                    for (int i = 0; i < length; i++) inputs.Add(new InputTick { x = x });
                    if (rng.NextDouble() < .4)
                    {
                        int hold = new[] { 1, 3, 6, 10, 16 }[rng.Next(5)];
                        for (int i = at; i < Math.Min(inputs.Count, at + hold); i++) { InputTick k = inputs[i]; k.held = true; k.press = i == at; inputs[i] = k; }
                        if (at + hold < inputs.Count) { InputTick k = inputs[at + hold]; k.release = true; inputs[at + hold] = k; }
                    }
                    if (rng.NextDouble() < .25)
                    {
                        InputTick d = inputs[at];
                        d.dash = true;
                        if (rng.NextDouble() < .5) d.dashJump = rng.Next(1, 11);
                        inputs[at] = d;
                        int j = Math.Min(inputs.Count - 1, at + rng.Next(0, 6));
                        if (d.dashJump == 0 && rng.NextDouble() < .5) { InputTick k = inputs[j]; k.press = k.held = true; inputs[j] = k; }
                    }
                }
                return inputs;
            }
            // Each stepped tick's state, and per input index the snapshot taken there with its step.
            (List<string>, Dictionary<int, (SimSnapshot, int)>) Record(List<InputTick> inputs, SimSnapshot from, int only = -1)
            {
                var states = new List<string>();
                var snaps = new Dictionary<int, (SimSnapshot, int)>();
                int lastTick = -1;
                sim.Run(inputs, from, tick =>
                {
                    if (tick > 0 && tick < inputs.Count && !sim.Engine.InputsExhausted && tick != lastTick) snaps[tick] = (only < 0 || tick == only ? sim.Capture(tick) : null, states.Count - 1);
                    lastTick = tick;
                    return true;
                }, onStep: s => states.Add(State()));
                return (states, snaps);
            }
            int checks = 0, matches = 0;
            var archive = new List<(List<InputTick> source, int k, SimSnapshot snap)>();
            for (int r = 0; r < runs; r++)
            {
                List<InputTick> source = r % 2 == 0 ? route : Burst(new List<InputTick>(), 60);
                foreach (var (k, (snap, _)) in Record(source, null).Item2) archive.Add((source, k, snap));
                for (int b = 0; b < 20; b++)
                {
                    var (from, k, snapshot) = archive[rng.Next(archive.Count)];
                    var inputs = Burst(from.Take(k).ToList(), k + rng.Next(1, 60));
                    var (branch, taken) = Record(inputs, snapshot);
                    foreach (var (tk, (ts, _)) in taken) if (rng.Next(4) == 0) archive.Add((inputs, tk, ts));
                    var (full, starts) = Record(inputs, null, k);
                    if (!starts.TryGetValue(k, out var at)) continue;
                    int step = at.Item2;
                    checks++;
                    int bad = 0;
                    while (bad < branch.Count && step + 1 + bad < full.Count && branch[bad] == full[step + 1 + bad]) bad++;
                    if (bad == branch.Count && step + 1 + bad == full.Count) { matches++; continue; }
                    if (checks - matches > 5) continue;
                    Console.WriteLine($"run {r} resume at input {k} (step {step}): first difference {bad} steps later (resumed {branch.Count}, from start {full.Count - step - 1})");
                    Console.WriteLine("  inputs " + string.Join(" ", inputs.Skip(Math.Max(0, k - 3)).Take(bad + 6).Select(t => $"{t.x}{(t.held ? "h" : "")}{(t.press ? "P" : "")}{(t.release ? "R" : "")}{(t.dash ? "D" + t.dashJump : "")}")));
                    if (bad >= branch.Count || step + 1 + bad >= full.Count) continue;
                    string[] got = branch[bad].Split('|'), expected = full[step + 1 + bad].Split('|');
                    for (int i = 0; i < Math.Max(got.Length, expected.Length); i++)
                    {
                        string g = i < got.Length ? got[i] : "<none>", e = i < expected.Length ? expected[i] : "<none>";
                        if (g != e) Console.WriteLine($"  [{i}] resumed {Short(g)}\n       start   {Short(e)}");
                    }
                }
            }
            Console.WriteLine($"branch replays matching {matches}/{checks}");
            return matches == checks ? 0 : 3;
        }

        // Single-core throughput on one trace's inputs, from its start state.
        static int Bench(string[] args)
        {
            var (sim, _, inputs) = Open(args[0]);
            double seconds = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 5;
            sim.Run(inputs);
            var clock = Stopwatch.StartNew();
            long runs = 0, ticks = 0;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                ticks += sim.Run(inputs).Stepped;
                runs++;
            }
            double s = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"{runs / s:0.0} runs/s, {ticks / s:0} ticks/s, {s * 1e6 / ticks:0.00} us/tick ({inputs.Count} inputs); "
                + $"{sim.Physics.BodyCount} bodies, {sim.Physics.ContactCount} contacts, {sim.Physics.TouchingPairs} touching pairs, "
                + $"GC gen0 {GC.CollectionCount(0)} gen2 {GC.CollectionCount(2)}, allocated {GC.GetTotalAllocatedBytes() / 1e9:0.0} GB");
            return 0;
        }
    }
}
