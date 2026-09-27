using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using IGTAP.EngineSim;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// clip: a lab for states no input reaches yet (the player inside a wall, any speed, any Movement fields).
    /// <c>clip map</c> draws the ground colliders of a box; <c>clip run</c> runs every start of a grid against every branch
    /// of inputs on all cores and ranks the runs by the height they reach; <c>clip log</c> logs one start × branch tick by
    /// tick (the probe's rows).
    /// </summary>
    static class ClipLab
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo VelocityField = typeof(Movement).GetField("Velocity", Fields);
        static readonly FieldInfo MomentumField = typeof(Movement).GetField("momentum", Fields);
        static readonly FieldInfo NormalCollider = typeof(Movement).GetField("normalCollider", Fields);

        public static int Run(string[] args)
        {
            switch (args.FirstOrDefault())
            {
                case "map": return MapBox(args.Skip(1).ToArray());
                case "run": return RunGrid(args.Skip(1).ToArray());
                case "log": return LogOne(args.Skip(1).ToArray());
                default:
                    Console.Error.WriteLine("usage:\n  clip map <trace.json> x0 y0 x1 y1 [step]\n"
                        + "  clip run <trace.json> <lab.json> [--threads N] [--out results.csv] [--top N]\n"
                        + "  clip log <trace.json> <lab.json> <start index> <branch name> [--out file]");
                    return 2;
            }
        }

        /// <summary>World-frame occupancy of a box: '#' Ground layer (groundRaycastLayer 128), 'S' a Spikes (64) edge in the cell (spike composites are outlines), '.' free.</summary>
        static int MapBox(string[] args)
        {
            var (sim, _, _) = Program.Open(args[0]);
            float x0 = F(args[1]), y0 = F(args[2]), x1 = F(args[3]), y1 = F(args[4]), step = args.Length > 5 ? F(args[5]) : 4f;
            var sb = new StringBuilder();
            sb.AppendLine($"x {x0}..{x1}, y {y1} (top row) .. {y0}, step {step}; origin {sim.Origin.x},{sim.Origin.y}");
            for (float y = y1; y >= y0; y -= step)
            {
                sb.Append(y.ToString("0").PadLeft(6)).Append(' ');
                for (float x = x0; x <= x1; x += step)
                {
                    Vector2 p = new Vector2(x + sim.Origin.x, y + sim.Origin.y);
                    bool ground = Physics2D.Raycast(p, Vector2.down, 0.001f, 128), spikes = Physics2D.Raycast(p, Vector2.down, step, 64);
                    sb.Append(spikes ? 'S' : ground ? '#' : '.');
                }
                sb.AppendLine();
            }
            Console.Write(sb);
            return 0;
        }

        sealed class Lab
        {
            public List<Dictionary<string, JsonElement>> Starts = new List<Dictionary<string, JsonElement>>();
            public List<(string name, List<InputTick> ticks)> Branches = new List<(string, List<InputTick>)>();
            public List<InputTick> Prefix = new List<InputTick>();
            public int Horizon = 40;
            public int Top = 40;
        }

        /// <summary>
        /// lab.json: <c>base</c> (one start) crossed with <c>axes</c> ({key: [values]}) and/or an explicit <c>starts</c> list,
        /// <c>prefix</c> ticks every branch starts with, <c>branches</c> [{name, ticks}], <c>horizon</c> physics ticks after the
        /// inputs. A start's keys: x, y (world frame), vx, vy (the script's Velocity, and the body's with momentum), mx, my
        /// (momentum), scaleX, collider ("default" | "dash"), savestate (a studio savestate file first), anything else a
        /// Movement field. A tick is the probe's object or a string "x,y,flags[*n]" (flags H held, P press, R release, D dash,
        /// Jn dashJump n, Lx:y the stick through the dash lock, Tx the stick x held in the dash's freeze; *n repeats it).
        /// </summary>
        static Lab Load(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            var lab = new Lab();
            var baseStart = new Dictionary<string, JsonElement>();
            if (root.TryGetProperty("base", out JsonElement b))
                foreach (JsonProperty p in b.EnumerateObject()) baseStart[p.Name] = p.Value.Clone();
            if (root.TryGetProperty("axes", out JsonElement axes))
            {
                var combos = new List<Dictionary<string, JsonElement>> { baseStart };
                foreach (JsonProperty axis in axes.EnumerateObject())
                    combos = combos.SelectMany(c => axis.Value.EnumerateArray().Select(v =>
                        new Dictionary<string, JsonElement>(c) { [axis.Name] = v.Clone() })).ToList();
                lab.Starts.AddRange(combos);
            }
            if (root.TryGetProperty("starts", out JsonElement starts))
                foreach (JsonElement s in starts.EnumerateArray())
                {
                    var d = new Dictionary<string, JsonElement>(baseStart);
                    foreach (JsonProperty p in s.EnumerateObject()) d[p.Name] = p.Value.Clone();
                    lab.Starts.Add(d);
                }
            if (lab.Starts.Count == 0) lab.Starts.Add(baseStart);
            if (root.TryGetProperty("prefix", out JsonElement prefix)) lab.Prefix = Ticks(prefix);
            if (root.TryGetProperty("branches", out JsonElement branches))
                foreach (JsonElement br in branches.EnumerateArray())
                    lab.Branches.Add((br.GetProperty("name").GetString(), Ticks(br.GetProperty("ticks"))));
            if (lab.Branches.Count == 0) lab.Branches.Add(("prefix", new List<InputTick>()));
            if (root.TryGetProperty("horizon", out JsonElement h)) lab.Horizon = h.GetInt32();
            if (root.TryGetProperty("top", out JsonElement top)) lab.Top = top.GetInt32();
            return lab;
        }

        static List<InputTick> Ticks(JsonElement list)
        {
            var ticks = new List<InputTick>();
            foreach (JsonElement t in list.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.String) { ticks.Add(InputTick.FromJson(t)); continue; }
                string text = t.GetString();
                int repeat = 1, star = text.IndexOf('*');
                if (star >= 0) { repeat = int.Parse(text.Substring(star + 1), CultureInfo.InvariantCulture); text = text.Substring(0, star); }
                string[] parts = text.Split(',');
                string flags = parts.Length > 2 ? parts[2] : "";
                var tick = new InputTick { x = F(parts[0]), y = parts.Length > 1 && parts[1].Length > 0 ? F(parts[1]) : 0f };
                for (int i = 0; i < flags.Length; i++)
                {
                    switch (flags[i])
                    {
                        case 'H': tick.held = true; break;
                        case 'P': tick.press = true; tick.held = true; break;
                        case 'R': tick.release = true; break;
                        case 'D': tick.dash = true; break;
                        case 'T':
                        {
                            // Tx — the stick x held in the dash's freeze (InputTick.turnX), e.g. T-1
                            int e = i + 1;
                            while (e < flags.Length && (char.IsDigit(flags[e]) || flags[e] == '-' || flags[e] == '.')) e++;
                            tick.turnX = F(flags.Substring(i + 1, e - i - 1));
                            i = e - 1;
                            break;
                        }
                        case 'L':
                        {
                            // Lx:y — the stick held through the dash lock (MacroStep.lockMoveX/lockMoveY), e.g. L-1:0
                            int e = i + 1;
                            while (e < flags.Length && (char.IsDigit(flags[e]) || flags[e] == '-' || flags[e] == '.' || flags[e] == ':')) e++;
                            string[] lx = flags.Substring(i + 1, e - i - 1).Split(':');
                            tick.lockX = F(lx[0]);
                            if (lx.Length > 1) tick.lockY = F(lx[1]);
                            i = e - 1;
                            break;
                        }
                        case 'J':
                            int j = i + 1;
                            while (j < flags.Length && char.IsDigit(flags[j])) j++;
                            tick.dashJump = int.Parse(flags.Substring(i + 1, j - i - 1), CultureInfo.InvariantCulture);
                            i = j - 1;
                            break;
                        default: throw new FormatException("Unknown tick flag '" + flags[i] + "' in \"" + t.GetString() + "\".");
                    }
                }
                for (int i = 0; i < repeat; i++) ticks.Add(tick);
            }
            return ticks;
        }

        /// <summary>
        /// Puts the simulation in one lab start and returns it as a snapshot every branch restores. A start with no keys is the
        /// trace's own start, untouched (its prefix and branches replay exactly like the trace's inputs).
        /// </summary>
        static SimSnapshot Apply(Simulation sim, Dictionary<string, JsonElement> start, string baseDir)
        {
            if (start.Count == 0) return sim.Start;
            sim.Restore(sim.Start);
            var probeStart = new Dictionary<string, object>();
            if (start.TryGetValue("savestate", out JsonElement ss)) probeStart["savestate"] = ss.GetString();
            Vector3 world = sim.PlayerWorld;
            bool moved = start.ContainsKey("x") || start.ContainsKey("y");
            if (moved) probeStart["position"] = new[] { start.TryGetValue("x", out JsonElement x) ? x.GetSingle() : world.x, start.TryGetValue("y", out JsonElement y) ? y.GetSingle() : world.y };
            if (start.TryGetValue("scaleX", out JsonElement sx)) probeStart["scaleX"] = sx.GetSingle();
            var fields = new Dictionary<string, object>();
            if (start.TryGetValue("facing", out JsonElement facing))
            {
                probeStart["scaleX"] = facing.GetSingle() > 0f ? 1f : -1f;
                fields["facingRight"] = facing.GetSingle() > 0f ? "true" : "false";
            }
            foreach (var (key, value) in start)
            {
                switch (key)
                {
                    case "savestate": case "x": case "y": case "vx": case "vy": case "mx": case "my": case "scaleX": case "facing": case "collider": case "note": break;
                    default:
                        if (typeof(Movement).GetField(key, Fields) == null) throw new InvalidOperationException("Movement has no field " + key + ".");
                        fields[key] = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                        break;
                }
            }
            if (start.TryGetValue("collider", out JsonElement col))
            {
                var box = (BoxCollider2D)NormalCollider.GetValue(sim.Player);
                string which = col.GetString();
                box.offset = (Vector2)typeof(Movement).GetField(which + "ColliderOffset", Fields).GetValue(sim.Player);
                box.size = (Vector2)typeof(Movement).GetField(which + "ColliderSize", Fields).GetValue(sim.Player);
            }
            probeStart["fields"] = fields;
            if (start.ContainsKey("vx") || start.ContainsKey("vy") || start.ContainsKey("mx") || start.ContainsKey("my"))
            {
                Vector2 v = (Vector2)VelocityField.GetValue(sim.Player), m = (Vector2)MomentumField.GetValue(sim.Player);
                if (start.TryGetValue("vx", out JsonElement vx)) v.x = vx.GetSingle();
                if (start.TryGetValue("vy", out JsonElement vy)) v.y = vy.GetSingle();
                if (start.TryGetValue("mx", out JsonElement mx)) m.x = mx.GetSingle();
                if (start.TryGetValue("my", out JsonElement my)) m.y = my.GetSingle();
                fields["Velocity"] = $"{v.x.ToString(CultureInfo.InvariantCulture)},{v.y.ToString(CultureInfo.InvariantCulture)}";
                fields["momentum"] = $"{m.x.ToString(CultureInfo.InvariantCulture)},{m.y.ToString(CultureInfo.InvariantCulture)}";
                probeStart["velocity"] = new[] { v.x + m.x, v.y + m.y };
            }
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(probeStart));
            Probe.ApplyStart(sim, doc.RootElement, baseDir);
            // A lab start is mid-air as often as not: the inputs start on the first tick, without the macro's wait for three
            // grounded ticks (which also normalizes the quick-restart state) or its start alignment.
            sim.Engine.GetType().GetField("_waitingForStable", Fields).SetValue(sim.Engine, false);
            sim.Engine.GetType().GetField("_preRollTicksRemaining", Fields).SetValue(sim.Engine, 0);
            sim.SetStart(sim.Capture(0), 0);
            return sim.Start;
        }

        struct Outcome
        {
            public int Start, Branch;
            public float MaxY, EndY, MaxX, MinX, LaunchVy, BodyVy, BodyVx, LaunchVx;
            public int MaxYStep;
            public string End;
        }

        static Outcome RunOne(Simulation sim, SimSnapshot from, Lab lab, int startIndex, int branchIndex, List<InputTick> inputs)
        {
            float maxY = float.NegativeInfinity, maxX = float.NegativeInfinity, minX = float.PositiveInfinity, launch = float.NegativeInfinity, bodyVy = float.NegativeInfinity, bodyVx = 0f, launchVx = 0f;
            int maxStep = 0;
            float endY = 0f;
            RunResult r = sim.Run(inputs, from, extraTicks: lab.Horizon, onStep: step =>
            {
                Vector3 p = sim.PlayerWorld;
                if (p.y > maxY) { maxY = p.y; maxStep = step; }
                maxX = Math.Max(maxX, p.x); minX = Math.Min(minX, p.x);
                Vector2 v = (Vector2)VelocityField.GetValue(sim.Player), m = (Vector2)MomentumField.GetValue(sim.Player);
                var body = sim.Physics.PlayerBody();
                if (v.y + m.y > launch) { launch = v.y + m.y; launchVx = v.x + m.x; }
                bodyVy = Math.Max(bodyVy, body.vy);
                if (Math.Abs(body.vx) > Math.Abs(bodyVx)) bodyVx = body.vx;
                endY = p.y;
            });
            return new Outcome { Start = startIndex, Branch = branchIndex, MaxY = maxY, MaxYStep = maxStep, EndY = endY, MaxX = maxX, MinX = minX,
                LaunchVy = launch, BodyVy = bodyVy, BodyVx = bodyVx, LaunchVx = launchVx, End = r.EndReason };
        }

        static int RunGrid(string[] args)
        {
            string trace = args[0], labPath = args[1];
            Lab lab = Load(labPath);
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(labPath));
            int threads = Opt(args, "--threads", Environment.ProcessorCount);
            int top = Opt(args, "--top", lab.Top);
            string outPath = OptS(args, "--out", null);
            // The prefix runs once per start (Simulation.Advance); every branch restores the state at its end.
            var inputs = lab.Branches.Select(b => b.ticks).ToList();
            long total = (long)lab.Starts.Count * lab.Branches.Count;
            Console.Error.WriteLine($"{lab.Starts.Count} starts x {lab.Branches.Count} branches = {total} runs on {threads} threads");
            var results = new Outcome[total];
            int nextStart = -1;
            long done = 0;
            var errors = new ConcurrentQueue<string>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var workers = Enumerable.Range(0, Math.Max(1, Math.Min(threads, lab.Starts.Count))).Select(_ => new Thread(() =>
            {
                var (sim, _, _) = Program.Open(trace);
                SimSnapshot pristine = sim.Start;
                int s;
                while ((s = Interlocked.Increment(ref nextStart)) < lab.Starts.Count)
                {
                    SimSnapshot from;
                    try
                    {
                        sim.SetStart(pristine, 0);
                        from = Apply(sim, lab.Starts[s], baseDir);
                        if (lab.Prefix.Count > 0)
                        {
                            sim.SetStart(from, 0);
                            from = sim.Advance(lab.Prefix) ?? throw new InvalidOperationException("the prefix ended before its last tick");
                        }
                    }
                    catch (Exception ex) { errors.Enqueue($"start {s}: {ex.Message}"); continue; }
                    for (int b = 0; b < inputs.Count; b++)
                        results[(long)s * inputs.Count + b] = RunOne(sim, from, lab, s, b, inputs[b]);
                    long d = Interlocked.Add(ref done, inputs.Count);
                    if (s % Math.Max(1, lab.Starts.Count / 20) == 0)
                        Console.Error.WriteLine($"  {d}/{total} runs, {d / Math.Max(0.001, clock.Elapsed.TotalSeconds):0}/s");
                }
            }, 16 << 20) { IsBackground = true }).ToList();
            workers.ForEach(t => t.Start());
            workers.ForEach(t => t.Join());
            foreach (string e in errors.Take(10)) Console.Error.WriteLine(e);

            string[] varying = lab.Starts.SelectMany(s => s.Keys).Distinct()
                .Where(k => k != "savestate" && lab.Starts.Select(s => s.TryGetValue(k, out JsonElement v) ? v.GetRawText() : "").Distinct().Count() > 1).ToArray();
            string Describe(Dictionary<string, JsonElement> start) =>
                string.Join(" ", varying.Select(k => k + "=" + (start.TryGetValue(k, out JsonElement v) ? v.GetRawText().Trim('"') : "-")));
            var valid = results.Where(o => o.End != null || o.MaxY != 0f).ToList();
            if (outPath != null)
            {
                using var w = new StreamWriter(outPath);
                string[] keys = lab.Starts.SelectMany(s => s.Keys).Where(k => k != "savestate").Distinct().ToArray();
                w.WriteLine("start,branch," + string.Join(",", keys) + ",maxY,maxYStep,endY,minX,maxX,launchVy,launchVx,bodyVy,bodyVx,end");
                foreach (Outcome o in valid)
                {
                    var st = lab.Starts[o.Start];
                    w.WriteLine(o.Start + ",\"" + lab.Branches[o.Branch].name + "\"," + string.Join(",", keys.Select(k => st.TryGetValue(k, out JsonElement v) ? v.GetRawText().Trim('"').Replace(',', ';') : ""))
                        + $",{o.MaxY:0.##},{o.MaxYStep},{o.EndY:0.##},{o.MinX:0.##},{o.MaxX:0.##},{o.LaunchVy:0.#},{o.LaunchVx:0.#},{o.BodyVy:0.#},{o.BodyVx:0.#},\"{o.End}\"");
                }
            }
            Console.WriteLine($"{valid.Count} runs in {clock.Elapsed.TotalSeconds:0.0}s; top {top} by maxY:");
            foreach (Outcome o in valid.OrderByDescending(o => o.MaxY).Take(top))
                Console.WriteLine($"  maxY {o.MaxY,8:0.0} @{o.MaxYStep,-3} launchVy {o.LaunchVy,7:0} (vx {o.LaunchVx,6:0}) bodyVy {o.BodyVy,7:0} bodyVx {o.BodyVx,7:0} x {o.MinX:0}..{o.MaxX:0}  "
                    + $"[{o.Start}] {Describe(lab.Starts[o.Start])} | {lab.Branches[o.Branch].name}");
            return 0;
        }

        static int LogOne(string[] args)
        {
            string trace = args[0], labPath = args[1];
            Lab lab = Load(labPath);
            int s = int.Parse(args[2], CultureInfo.InvariantCulture);
            int b = lab.Branches.FindIndex(x => x.name == args[3]);
            if (b < 0) throw new InvalidOperationException("No branch " + args[3] + ".");
            var (sim, _, _) = Program.Open(trace);
            SimSnapshot from = Apply(sim, lab.Starts[s], Path.GetDirectoryName(Path.GetFullPath(labPath)));
            List<InputTick> inputs = lab.Prefix.Concat(lab.Branches[b].ticks).ToList();
            string[] extra = new[] { "wallJumpFacing", "dashFramesRemaining", "jumpBuffer", "wallCoyoteFrames", "wallJumpDirection", "wallJumpsLeft", "airDashesLeft", "dashCooldown" }
                .Concat(OptS(args, "--fields", "").Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            FieldInfo[] extraInfos = extra.Select(n => typeof(Movement).GetField(n, Fields) ?? throw new InvalidOperationException("Movement has no field " + n + ".")).ToArray();
            var rows = new List<Dictionary<string, object>>();
            Dictionary<string, object> first = Probe.Row(sim, -1, extra, extraInfos);
            rows.Add(first);
            RunResult r = sim.Run(inputs, from, extraTicks: lab.Horizon, onStep: step => rows.Add(Probe.Row(sim, step, extra, extraInfos)));
            var sb = new StringBuilder();
            sb.AppendLine("end: " + r.EndReason);
            foreach (var row in rows)
            {
                int input = row.TryGetValue("input", out object i) ? (int)i : -1;
                string tick = input >= 0 && input < inputs.Count ? TickText(inputs[input]) : "";
                sb.AppendLine($"{row["step"],3} in{input,3} {tick,-12} pos {row["x"],9},{row["y"],9} V {row["vx"],7},{row["vy"],7} m {row["mx"],7},{row["my"],7} "
                    + $"body {row["bvx"],7},{row["bvy"],7} g{B(row["ground"])} w{B(row["wall"])} {row["mode"],-5} d{B(row["dash"])} f{row["f"]} R{B(row["facingRight"])} "
                    + string.Join(" ", extra.Select(e => e + "=" + (row[e] is float[] a ? string.Join(",", a) : row[e]))));
            }
            string outPath = OptS(args, "--out", null);
            if (outPath != null) File.WriteAllText(outPath, sb.ToString()); else Console.Write(sb);
            return 0;
        }

        static string B(object b) { return (bool)b ? "1" : "0"; }

        static string TickText(InputTick k)
        {
            return $"{k.x:0.##},{k.y:0.##},{(k.press ? "P" : k.held ? "H" : "")}{(k.release ? "R" : "")}{(k.dash ? "D" : "")}{(k.dashJump > 0 ? "J" + k.dashJump : "")}"
                + (k.lockX.HasValue || k.lockY.HasValue ? $"L{k.lockX ?? 0f:0.##}:{k.lockY ?? 0f:0.##}" : "")
                + (k.turnX.HasValue ? $"T{k.turnX:0.##}" : "");
        }

        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
        static int Opt(string[] args, string name, int fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback; }
        static string OptS(string[] args, string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    }
}
