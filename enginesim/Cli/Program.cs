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
                    case "humanize": return Humanize.Run(args.Skip(1).ToArray());
                    case "map": return Map.Run(args.Skip(1).ToArray());
                    case "ultras": return Ultras.Run(args.Skip(1).ToArray());
                    case "fulltas": return FullTas.Run(args.Skip(1).ToArray());
                    case "rays": return Rays(args[1], args[2]);
                    case "probe": return Probe.Run(args.Skip(1).ToArray());
                    case "clip": return ClipLab.Run(args.Skip(1).ToArray());
                    case "path":
                    {
                        var (sim, _, _) = Open(args[1]);
                        using JsonDocument route = JsonDocument.Parse(File.ReadAllText(args[2]));
                        JsonElement list = route.RootElement.TryGetProperty("ticks", out JsonElement ticksList) ? ticksList : route.RootElement.GetProperty("inputs");
                        var inputs = list.EnumerateArray().Select(InputTick.FromJson).ToList();
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
                        RunResult r = sim.Run(inputs, extraTicks: 1000000, onStep: st =>
                        {
                            Vector3 pos = sim.PlayerWorld;
                            foreach (spikeScript s in spikes)
                                if ((int)counter.GetValue(s) > 0)
                                    Console.WriteLine($"tick {st}: SPIKE {s.name} at {s.transform.position.x - sim.CurrentOrigin.x:0},{s.transform.position.y - sim.CurrentOrigin.y:0} counter {counter.GetValue(s)} graceFrames {grace.GetValue(s)}"
                                        + $" killOnDash {onDash.GetValue(s)} playerKilled {killed.GetValue(s)} dashActive {dashActive.GetValue(sim.Player)}"
                                        + $" player {pos.x:0.0},{pos.y:0.0}");
                            bool timing = sim.TimingCourse() != null;
                            if (verbose)
                            {
                                int run = (int)sim.Engine.RunTicks - 1;
                                InputTick k = run >= 0 && run < inputs.Count ? inputs[run] : default;
                                string input = run + 1 == lastRun ? "(no input)" : $"in[{run}] x={k.x}{(k.y != 0f ? " y=" + k.y : "")}{(k.press ? " press" : "")}{(k.held ? " held" : "")}"
                                    + (k.release ? " release" : "") + (k.dash ? " dash" + (k.dashJump > 0 ? "+jump@" + k.dashJump : "") + (k.turnX.HasValue ? " turn=" + k.turnX : "") : "")
                                    + (k.springDash > 0 ? " springdash@" + k.springDash : "")
                                    + (k.lockX.HasValue || k.lockY.HasValue ? " lock=" + k.lockX + "," + k.lockY : "");
                                lastRun = run + 1;
                                Console.WriteLine($"tick {st}: pos {pos.x:0.0},{pos.y:0.0}"
                                    + (timing ? " t=" + Simulation.CourseTime(sim.TimingCourse()).ToString("0.00") : "") + " " + input + " | "
                                    + string.Join(" ", fields.Select((f, i) => f + "=" + infos[i]?.GetValue(sim.Player))));
                            }
                            else if (timing != was || st % 25 == 0)
                                Console.WriteLine($"tick {st}: pos {pos.x:0.0},{pos.y:0.0} timing {timing}"
                                    + (timing ? " t=" + Simulation.CourseTime(sim.TimingCourse()) : ""));
                            was = timing;
                        });
                        Console.WriteLine($"finished {r.Finished} time {r.Time} tick {r.FinishTick}" + (r.Finished ? "" : " (" + r.EndReason + ")"));
                        return 0;
                    }
                    default:
                        Console.Error.WriteLine("usage:\n  replay <trace.json>... [--verbose]   compare offline runs with turbo traces tick by tick\n"
                            + "  bench <trace.json> [seconds]           runs/s and ticks/s on one core\n"
                            + "  solve <start-trace.json> [--threads N] [--minutes M] [--seed <trace.json>]... [--no-submit] [--fresh]\n"
                            + "        [--via x0,y0,x1,y1]... [--avoid x0,y0,x1,y1]... [--path-name name] [--optional name]...\n"
                            + "                                         offline solver from that trace's start state; bests that beat\n"
                            + "                                         the seeds are confirmed in turbo (/api/enginesim/submit) when the game runs;\n"
                            + "                                         --via/--avoid force a path (boxes passed in order / boxes that cut a run);\n"
                            + "                                         --optional turns on a named optional block of forced-paths.json\n"
                            + "                                         each new best also gets a human version (humanize)\n"
                            + "  humanize <start-trace.json> <best.json> [--threads N] [--via x0,y0,x1,y1]... [--avoid x0,y0,x1,y1]...\n"
                            + "                                         same-time version of a best with fewer perfect inputs and each\n"
                            + "                                         input's timing/value slack: <best>-human.json and <best>-human.md\n"
                            + "  fulltas <trace.json> [--threads N] [--minutes total] [--window-minutes m] [--stall-minutes s] [--window W]\n"
                            + "          [--seed <file>]... [--no-submit] [--fresh] [--unsolved] [--goal N]\n"
                            + "                                         Full-TAS search in rolling windows of W goals from a fulltas trace;\n"
                            + "                                         polishes the goals the best reaches from goal 1 (--unsolved: reaches\n"
                            + "                                         the goals it misses first); run state in <traces>/fulltas/<run>.json,\n"
                            + "                                         bests to the gate (/api/fulltas/submit) when the game runs; --seed:\n"
                            + "                                         saved course routes ({goals, standing, restart, ticks}) as window starts;\n"
                            + "                                         --goal N: polish only goal N-1 to goal N, over and over\n"
                            + "  rays <trace.json> <rays.json>          offline raycasts in the /api/enginesim/raycast format\n"
                            + "  probe <trace.json> <probe.json> [--out file]\n"
                            + "                                         one start state (the trace's, moved, or a studio savestate), branches of\n"
                            + "                                         inputs from it, every tick logged; updatesPerTick/fps model 1x frames\n"
                            + "  clip map <trace.json> x0 y0 x1 y1 [step] | clip run <trace.json> <lab.json> [--threads N] [--out csv] [--top N]\n"
                            + "     | clip log <trace.json> <lab.json> <start> <branch>\n"
                            + "                                         lab for any start state (inside walls too): a grid of starts x branches\n"
                            + "                                         on all cores, ranked by the height reached; one run logged per tick\n"
                            + "  map <start-trace.json> --out <file> [--seed <file>]... [--threads N] [--optional name]...\n"
                            + "                                         the target's routes, archive coverage, colliders and forced-path\n"
                            + "                                         boxes as one JSON for the studio's Optimize map\n"
                            + "  ultras <fulltas-VMAN.json> --out <file> [--courses <courses.json>] [--spacing U] [--threads N]\n"
                            + "                                         floors a hyper lands on and the game's ultras two chained wavedashes\n"
                            + "                                         get from launch points along them, for the studio's Ultra map\n");
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
            sim.SetTarget(trace.RootElement);
            sim.LoadStart(trace.RootElement.GetProperty("start"));
            var inputs = trace.RootElement.GetProperty("inputs").EnumerateArray().Select(InputTick.FromJson).ToList();
            return (sim, trace, inputs);
        }

        static int Replay(string[] args)
        {
            bool verbose = args.Contains("--verbose");
            int failures = 0;
            var assetRef = new System.Text.RegularExpressions.Regex("\\{\"\\$ref\":(-?\\d+)\\}");
            foreach (string path in args.Where(a => !a.StartsWith("--") && !a.EndsWith(".world.json", StringComparison.OrdinalIgnoreCase)))
            {
                var (sim, trace, inputs) = Open(path);
                _normalize = s => assetRef.Replace(s, m => sim.World.Objects.ContainsKey(int.Parse(m.Groups[1].Value)) ? m.Value : "null");
                JsonElement[] ticks = trace.RootElement.GetProperty("ticks").EnumerateArray().ToArray();
                JsonElement result = trace.RootElement.GetProperty("result");
                FieldInfo[] movementFields = FieldState.For(typeof(Movement));
                int firstDiff = -1;
                int firstRandomDiff = -1, firstCashDiff = -1;
                var diffs = new List<string>();
                RunResult run = sim.Run(inputs, extraTicks: 1000000, onStep: step =>
                {
                    if (step >= ticks.Length || firstDiff >= 0 && !verbose) return;
                    var here = new List<string>();
                    JsonElement t = ticks[step];
                    Compare(here, "position", Json(sim.Player.transform.position), t.GetProperty("position"));
                    Compare(here, "bodyPosition", Json(sim.Physics.GetPosition(sim.Player.GetComponent<Rigidbody2D>())), t.GetProperty("bodyPosition"));
                    Compare(here, "velocity", Json(sim.Physics.GetVelocity(sim.Player.GetComponent<Rigidbody2D>())), t.GetProperty("velocity"));
                    foreach (JsonElement entry in t.GetProperty("movement").EnumerateArray())
                    {
                        FieldInfo f = World.FindField(typeof(Movement), entry[0].GetString(), entry[1].GetString());
                        if (f == null || Simulation.MovementPresentationFields.Contains(f.Name)) continue;
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
                    if (t.TryGetProperty("cash", out JsonElement cash) && Compare(here, "cash", Json(sim.Shop.CashNow), cash) && firstCashDiff < 0) firstCashDiff = step;
                    if (t.TryGetProperty("random", out JsonElement random))
                    {
                        UnityEngine.Random.State rng = UnityEngine.Random.state;
                        if (Compare(here, "random", Json(new[] { rng.s0, rng.s1, rng.s2, rng.s3 }), random) && firstRandomDiff < 0) firstRandomDiff = step;
                    }
                    if (here.Count == 0) return;
                    if (firstDiff < 0) firstDiff = step;
                    if (diffs.Count < 60) diffs.Add("tick " + step + ": " + string.Join("; ", here));
                });
                bool sameResult = run.Finished == result.GetProperty("finished").GetBoolean()
                    && Json(run.Time) == result.GetProperty("time").GetRawText();
                Console.WriteLine(Path.GetFileName(path) + ": " + run.Stepped + "/" + ticks.Length + " ticks, "
                    + (firstDiff < 0 ? "every tick identical" : "first difference at tick " + firstDiff)
                    + "; random " + (firstRandomDiff < 0 ? "identical on every tick" : "first difference at tick " + firstRandomDiff)
                    + ", cash " + (firstCashDiff < 0 ? "identical on every tick" : "first difference at tick " + firstCashDiff)
                    + ", result " + (run.Finished ? "finish " + Json(run.Time) : "no finish (" + run.EndReason + ")")
                    + " vs turbo " + (result.GetProperty("finished").GetBoolean() ? "finish " + result.GetProperty("time").GetRawText() : "no finish")
                    + (sameResult ? " [MATCH]" : " [MISMATCH]"));
                foreach (string d in diffs.Take(verbose ? 60 : 3)) Console.WriteLine("  " + d);
                if (firstDiff >= 0 || !sameResult || run.Stepped != ticks.Length) failures++;
            }
            return failures == 0 ? 0 : 3;
        }

        static Func<string, string> _normalize = s => s;

        static bool Compare(List<string> diffs, string name, string offline, JsonElement turbo)
        {
            string expected = Numbers(_normalize(turbo.GetRawText()));
            string actual = Numbers(offline);
            if (actual == expected) return false;
            diffs.Add(name + " " + Short(offline) + " != " + Short(turbo.GetRawText()));
            return true;
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
