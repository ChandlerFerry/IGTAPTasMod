using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// coverage &lt;trace&gt; &lt;route-human.json&gt; [--out file.md] [--no-tech-grafts]: issue #19's step 0. Replays the route
    /// with the solver's contact detection (<see cref="ContactLog"/>) and maps each of its inputs (<see cref="Humanizer.Events"/>,
    /// with the human file's class) onto the primitive library: the contact whose <see cref="Planner.Primitives"/> window
    /// reaches it and the graft kind that makes that input there (its tick relative to the graft's start and its value), or
    /// a mined tech demo window (<see cref="TechGrafts"/>) with the same input at that offset (±<see cref="TechGrafts.Jitter"/>);
    /// else none. Prints a markdown table and the coverage count.
    /// </summary>
    static class Coverage
    {
        public static int Run(string[] args)
        {
            string trace = args[0], routePath = args[1];
            JsonElement header = Solve.ReadHeader(trace);
            string caps = header.GetProperty("capabilities").GetString();
            Caps unlocks = Caps.Parse(caps);
            List<InputTick> route;
            var classes = new Dictionary<(int, int), string>();
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(routePath), new JsonDocumentOptions { MaxDepth = 256 }))
            {
                route = Solve.ReadInputs(doc.RootElement);
                if (doc.RootElement.TryGetProperty("events", out JsonElement events))
                {
                    int i = 0;
                    foreach (JsonElement e in events.EnumerateArray()) classes[(i++, e.GetProperty("tick").GetInt32())] = e.GetProperty("class").GetString();
                }
            }
            TechGrafts mined = args.Contains("--no-tech-grafts") ? null : TechGrafts.Load(Path.GetDirectoryName(Path.GetFullPath(trace)),
                techDir: Solve.Option(args, "--tech-dir", (string)null));
            TechPattern[] patterns = mined?.Patterns.Where(p => p.Requires.IsSubsetOf(unlocks) && !p.Resets).ToArray() ?? Array.Empty<TechPattern>();

            Simulation sim = Solve.Creator(trace, header)();
            var log = new ContactLog();
            var gate = new ContactProbe(sim);
            int last = -1;
            RunResult r = sim.Run(route, null, tick => { last = tick; log.Tick(tick, sim.Player, gate.NearGate()); return true; }, 400, _ =>
            {
                if (unlocks.Omni) log.Slide((int)sim.Engine.RunTicks, sim.Player);
                log.Step(sim);
            });
            bool died = r.EndReason != null && r.EndReason.StartsWith("Player death", StringComparison.Ordinal);
            if (died) log.Contacts.Add((last + 1, Contact.Died));
            List<(int tick, Contact kind)> contacts = log.Contacts;

            List<Humanizer.Event> list = Humanizer.Events(route, route);
            var rows = new List<string>();
            int covered = 0, replayed = 0, graftHits = 0, minedHits = 0, gridHits = 0;
            var byClass = new Dictionary<string, (int all, int hit)>();
            for (int i = 0; i < list.Count; i++)
            {
                Humanizer.Event e = list[i];
                string cls = classes.TryGetValue((i, e.Tick), out string c) ? c : e.Start ? "band" : "-";
                string contact = "-", graft = "none", tech = "none";
                bool inRun = e.Tick <= last;
                if (inRun) replayed++;
                if (inRun && !e.Start)
                {
                    (int tick, Contact kind)? nearest = null;
                    foreach (var ct in contacts)
                    {
                        if (ct.tick <= e.Tick + 8) nearest = ct;
                        if (graft == "none" && Graft(e, route, ct, unlocks) is string g) { graft = g; contact = $"{ct.kind} @{ct.tick} ({Off(e.Tick - ct.tick)})"; }
                        if (tech == "none" && Mined(e, route, ct, patterns) is string t)
                        {
                            tech = t;
                            if (graft == "none") contact = $"{ct.kind} @{ct.tick} ({Off(e.Tick - ct.tick)})";
                        }
                    }
                    if (contact == "-" && nearest is var (nt, nk)) contact = $"{nk} @{nt} ({Off(e.Tick - nt)}), no window";
                }
                bool hit = graft != "none" || tech != "none";
                if (hit) covered++;
                if (graft != "none") graftHits++;
                if (tech != "none") minedHits++;
                bool grid = Grid(e, route, unlocks);
                if (grid) gridHits++;
                string key = cls.StartsWith("window", StringComparison.Ordinal) ? "window" : cls;
                byClass.TryGetValue(key, out var b);
                byClass[key] = (b.all + 1, b.hit + (hit ? 1 : 0));
                rows.Add($"| {e.Tick} | {Describe(e, route)} | {cls} | {(inRun ? contact : "after the run ended")} | {graft} | {tech} | {(grid ? "yes" : "no")} |");
            }
            var s = new StringBuilder();
            s.AppendLine($"# Coverage of `{Path.GetFileName(routePath)}` ({caps}) from `{Path.GetFileName(trace)}`");
            s.AppendLine();
            s.AppendLine($"Replay: {(r.Finished ? $"finishes, {r.Time.ToString("0.00", CultureInfo.InvariantCulture)}s" : r.EndReason)} (last input tick {last}); "
                + $"{contacts.Count} contacts; {list.Count} inputs, {replayed} replayed. {patterns.Length} mined patterns for these unlocks.");
            s.AppendLine();
            s.AppendLine($"Covered: {covered}/{list.Count} ({graftHits} by a planner primitive, {minedHits} by a mined window). By class: "
                + string.Join(", ", byClass.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.hit}/{k.Value.all}")) + ".");
            s.AppendLine();
            s.AppendLine($"Planner grid: {gridHits}/{list.Count} inputs have a value the planner's decisions make at any node (Planner.Decisions: "
                + "x -1/0/1, y -1/0/1, any press or release, a dash at jump frame 0/3/5/7/9 or the grafts' 1/3, turned only with the stick at rest); "
                + "its nodes come at every contact and at most Cap ticks apart, an air node also makes its jump cut, double jump, stick change, "
                + "dash and dash jump 2, 4 or 6 ticks later, and the polish moves each input up to 6 ticks.");
            s.AppendLine();
            s.AppendLine("| Tick | Input | Class | Contact (offset) | Primitive | Mined window | Planner grid |");
            s.AppendLine("|---|---|---|---|---|---|---|");
            foreach (string row in rows) s.AppendLine(row);
            string text = s.ToString();
            string outFile = Solve.Option(args, "--out", (string)null);
            if (outFile != null) File.WriteAllText(outFile, text);
            Console.WriteLine(text);
            return 0;
        }

        /// <summary>The input's value is one the planner's decisions make (<see cref="Planner"/>: its stick, press, release and dash grid).</summary>
        static bool Grid(Humanizer.Event e, List<InputTick> route, Caps caps)
        {
            InputTick k = route[e.Tick];
            switch (e.Kind)
            {
                case Humanizer.Kind.StickX: return e.Value == 0f || Math.Abs(e.Value) == 1f;
                case Humanizer.Kind.StickY: return true;
                case Humanizer.Kind.Press:
                case Humanizer.Kind.Release: return true;
                default:
                    if (caps.Omni) return k.dashJump is 0 or 3 or 4 or 5 or 6 or 8 or 11;
                    bool frame = k.dashJump is 0 or 1 or 3 or 5 or 7 or 9;
                    bool turn = !(k.turnX.HasValue || k.turnY.HasValue) || k.x == 0f && k.dashJump == 0;
                    return frame && turn;
            }
        }

        static string Off(int o) { return (o >= 0 ? "+" : "") + o; }

        static string Describe(Humanizer.Event e, List<InputTick> route)
        {
            switch (e.Kind)
            {
                case Humanizer.Kind.StickX: return "x " + Humanizer.Num(e.Value);
                case Humanizer.Kind.StickY: return "y " + Humanizer.Num(e.Value);
                case Humanizer.Kind.Press: return "press jump";
                case Humanizer.Kind.Release: return "release jump";
                default:
                    InputTick k = route[e.Tick];
                    return "dash" + (k.dashJump > 0 ? " jump@" + k.dashJump : "") + $" ({Humanizer.Num(k.x)},{Humanizer.Num(k.y)})"
                        + (k.turnX.HasValue || k.turnY.HasValue ? $" turn {Humanizer.Num(k.turnX ?? 0f)},{Humanizer.Num(k.turnY ?? 0f)}" : "");
            }
        }

        static readonly int[] Holds = { 1, 3, 6, 10, 16, 24, 30, 40 }, DashHolds = { 3, 6, 10, 16, 24 }, BounceHolds = { 3, 6, 10, 16 }, ClipHolds = { 0, 3, 6, 9, 12, 16, 20, 26 };
        static readonly float[] ClipFlicks = { .3f, .35f, .4f, .45f, .5f, .55f }, ClipBacks = { .3f, .4f, .5f, .65f, 1f }, TurnAways = { .3f, .5f, .7f, 1f };

        /// <summary>
        /// The planner primitive at this contact that makes the input: its start `at` anywhere in the contact's window, the input at
        /// the kind's own offset from `at` with a value it makes (the jump's holds, the dash jump's frames 1-4, the primed dash 1-6
        /// after its jump, the superbounce's jump 0-5 after its dash, the wall clip's flick, jump and hold, the turn away, the gate
        /// tap, the omnidash kinds' aims and frames). The primitive's coast (the stick full one way from `at`) makes a full x there.
        /// </summary>
        static string Graft(Humanizer.Event e, List<InputTick> route, (int tick, Contact kind) c, Caps caps)
        {
            if (!Planner.Primitives.TryGetValue(c.kind, out var w)) return null;
            IEnumerable<string> kinds = !caps.Dash ? new[] { "jump" } : caps.Omni ? w.plain.Concat(w.omni ?? OmniKinds) : w.plain;
            if (c.kind is Contact.WallBehindLeft or Contact.WallBehindRight) kinds = new[] { "wallclip" };
            else if (c.kind is Contact.WallAheadLeft or Contact.WallAheadRight or Contact.WallAboveLeft or Contact.WallAboveRight) kinds = w.plain;
            else if (c.kind is Contact.NearGateLeft or Contact.NearGateRight) kinds = new[] { "gatetap" };
            InputTick k = route[e.Tick];
            foreach (string kind in kinds.Distinct())
                for (int at = c.tick + w.lo; at <= c.tick + w.hi; at++)
                {
                    int o = e.Tick - at;
                    if (Makes(kind, e, k, o)) return kind + " at " + Off(at - c.tick);
                }
            return null;
        }

        static readonly string[] OmniKinds = { "aim", "steep", "wavedash", "ultra", "dashbounce", "wavebounce", "wavejump", "superground", "bouncechain" };

        static bool Makes(string kind, Humanizer.Event e, InputTick k, int o)
        {
            float v = e.Value;
            bool full = Math.Abs(v) == 1f;
            // Every graft's coast: the stick full one way from its start, y as it was.
            if (e.Kind == Humanizer.Kind.StickX && o == 0 && full) return true;
            switch (kind)
            {
                case "jump":
                    return e.Kind == Humanizer.Kind.Press && o == 0 || e.Kind == Humanizer.Kind.Release && Holds.Contains(o);
                case "dash":
                    return e.Kind == Humanizer.Kind.Dash && o == 0 && k.dashJump == 0;
                case "dashjump":
                    return e.Kind == Humanizer.Kind.Dash && o == 0 && k.dashJump >= 1 && k.dashJump <= 4;
                case "primed":
                    return e.Kind == Humanizer.Kind.Press && o == 0 || e.Kind == Humanizer.Kind.Dash && o >= 1 && o <= 6 && k.dashJump == 0
                        || e.Kind == Humanizer.Kind.Release && o >= 1 + DashHolds.Min() && o <= 6 + DashHolds.Max();
                case "superbounce":
                    return e.Kind == Humanizer.Kind.Dash && o == 0 && k.dashJump == 0 || e.Kind == Humanizer.Kind.Press && o >= 0 && o <= 5
                        || e.Kind == Humanizer.Kind.Release && o >= BounceHolds.Min() && o <= 5 + BounceHolds.Max()
                        || e.Kind == Humanizer.Kind.StickX && full && o >= 8 && o <= 8;
                case "backdash":
                    return e.Kind == Humanizer.Kind.Dash && o == 0 && k.dashJump == 0 || e.Kind == Humanizer.Kind.Press && o == 0
                        || e.Kind == Humanizer.Kind.StickX && (full || v == 0f) && o >= -4 && o <= 0
                        || e.Kind == Humanizer.Kind.Release && DashHolds.Contains(o);
                case "wallclip":
                    return e.Kind == Humanizer.Kind.StickX && o == 0 && ClipFlicks.Contains(Math.Abs(v))
                        || e.Kind == Humanizer.Kind.Release && (o == 0 || ClipHolds.Contains(o - 2))
                        || e.Kind == Humanizer.Kind.Press && o == 1
                        || e.Kind == Humanizer.Kind.StickX && o == 1 && ClipBacks.Contains(Math.Abs(v))
                        || e.Kind == Humanizer.Kind.StickX && o == 2 && v == 0f;
                case "turnaway":
                    return e.Kind == Humanizer.Kind.StickX && o == 0 && TurnAways.Contains(Math.Abs(v))
                        || e.Kind == Humanizer.Kind.StickX && o >= 1 && o <= 4 && v == 0f;
                case "rest":
                    return e.Kind == Humanizer.Kind.StickX && o == 0 && v == 0f;
                case "gatetap":
                    return e.Kind == Humanizer.Kind.Press && o == 0 || e.Kind == Humanizer.Kind.StickX && o >= 0 && o <= 24 && (v == 0f || TurnAways.Contains(Math.Abs(v)));
                case "aim":
                    return e.Kind == Humanizer.Kind.Dash && o == 0;
                case "steep":
                    return e.Kind == Humanizer.Kind.Dash && o == 0 && k.x == 0f && k.y == 0f;
                case "wavedash":
                case "cornerhyper":
                case "cornerultra":
                    return e.Kind == Humanizer.Kind.Dash && o >= (kind == "cornerultra" ? -16 : 0) && o <= 0 && k.y < 0f;
                case "ultra":
                case "wavebounce":
                    return e.Kind == Humanizer.Kind.Dash && o >= 0 && o <= 16 && k.y < 0f;
                case "dashbounce":
                    return e.Kind == Humanizer.Kind.Dash && o == 0 && k.x == 0f && k.y < 0f;
                case "bouncechain":
                    return e.Kind == Humanizer.Kind.Dash && o >= 0 && o <= 5 * 24 && (k.x == 0f && k.y < 0f || k.y < 0f && o == 0);
                case "superground":
                    return e.Kind == Humanizer.Kind.Dash && o >= 0 && o <= 20 && (k.y == 0f || k.x == 0f && k.y < 0f);
                case "wavejump":
                    return e.Kind == Humanizer.Kind.Dash && o == 0 && k.y > 0f;
            }
            return false;
        }

        /// <summary>A mined window of this contact kind with the same input at the event's offset from the contact (±the graft jitter).</summary>
        static string Mined(Humanizer.Event e, List<InputTick> route, (int tick, Contact kind) c, TechPattern[] patterns)
        {
            InputTick k = route[e.Tick];
            foreach (TechPattern p in patterns)
            {
                if (p.Kind != c.kind) continue;
                for (int jitter = -TechGrafts.Jitter; jitter <= TechGrafts.Jitter; jitter++)
                {
                    int i = e.Tick - (c.tick + jitter) - p.First;
                    if (i < 0 || i >= p.Ticks.Length) continue;
                    InputTick t = p.Ticks[i], before = i > 0 ? p.Ticks[i - 1] : default;
                    bool same = e.Kind switch
                    {
                        Humanizer.Kind.Press => t.press,
                        Humanizer.Kind.Release => i > 0 && !t.press && (t.release || before.held && !t.held),
                        Humanizer.Kind.Dash => t.dash && t.dashJump == k.dashJump && t.x == k.x && t.y == k.y,
                        Humanizer.Kind.StickX => t.x == e.Value && (i == 0 || before.x != t.x),
                        _ => t.y == e.Value && (i == 0 || before.y != t.y),
                    };
                    if (same) return p.Tech + (p.Mirrored ? " (mirrored)" : "") + " " + Off(jitter + p.First + i);
                }
            }
            return null;
        }
    }
}
