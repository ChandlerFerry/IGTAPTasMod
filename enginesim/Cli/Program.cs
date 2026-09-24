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
                    case "path":
                    {
                        // path <start-trace> <route.json>: where the course timer starts/stops along a route.
                        var (sim, _, _) = Open(args[1]);
                        using JsonDocument route = JsonDocument.Parse(File.ReadAllText(args[2]));
                        var inputs = route.RootElement.GetProperty("ticks").EnumerateArray().Select(t => new InputTick
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
                        RunResult r = sim.Run(inputs, onStep: st =>
                        {
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
                            + "                                         offline solver from that trace's start state; new bests are\n"
                            + "                                         confirmed in turbo (/api/enginesim/submit) when the game runs\n"
                            + "  rays <trace.json> <rays.json>          offline raycasts in the /api/enginesim/raycast format");
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
