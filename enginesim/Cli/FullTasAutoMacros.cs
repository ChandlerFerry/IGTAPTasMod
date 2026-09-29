using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// Stage 4's macro edges (docs/fulltas.md "Auto"): recurring input patterns
    /// mined around the contact events (<see cref="ContactLog"/>) of the good routes — VMAN 34.28 and 34.88, the FalseEnding
    /// and TrueEnding bests, the course records — plus the runs the library files. A pattern is the input window played from
    /// the anchor contact on (its <see cref="Macro.Anchor"/> state: Land, Wall, DashEnd, Reset or Goal), with the preconditions
    /// it was mined under: the unlocks it presses (<see cref="Macro.Caps"/>), the dashes and air jumps it spends before any
    /// refresh, and the facing it was played with. Windows recur across the corpus (a FalseEnding lap repeats dozens of times,
    /// the down-dash landing chain every landing), so a window with one use is travel noise and is dropped.
    /// Loaded by reflection (FullTasAuto): a constructor filled by name (goals, labels, threads, name, library, log).
    /// </summary>
    sealed class FullTasAutoMacros
    {
        /// <summary>The window after a Land/Wall/DashEnd anchor, in input ticks (TechGrafts' WindowAfter).</summary>
        public const int ContactWindow = 30;
        /// <summary>A Reset/Goal anchor's lap window runs to the next anchor of the same kind (one repeated course lap or buy
        /// walk), at least <see cref="MinLap"/> and at most this many input ticks.</summary>
        public const int LapWindow = 240, MinLap = 20;
        /// <summary>Occurrences in the corpus before a window is kept (once: travel, twice: a pattern; --min-uses).</summary>
        public int MinUses = 2;
        /// <summary>The trigger's ticks after the anchor: windows grouping together share the button edges in them (the
        /// press, release or dash that does the tech), whatever stick and hold length follows. A window with no edge in them
        /// is travel and groups only when its whole body matches.</summary>
        public const int HeadTicks = 8;
        /// <summary>Macros <see cref="For"/> hands a state at once (the window seeds it fills).</summary>
        public const int TopFor = 5;

        // ---- the mined pattern ----

        public sealed class Pattern
        {
            public Macro Macro;
            /// <summary>The preconditions the inputs don't state: the resources spent before any refresh and the facing.</summary>
            public bool FacingRight;
            public int DashesNeeded, AirJumpsNeeded;
            /// <summary>down-dash-landing | hyper | wall-dash | lap | tech: what the window shows (the report's buckets).</summary>
            public string Shape;
            /// <summary>Occurrences and where they came from ("name@tick gN").</summary>
            public int Sites;
            public readonly List<string> Where = new List<string>();
            /// <summary>Every window variant in the group and its uses; the most used one is <see cref="Macro.Inputs"/>.</summary>
            public readonly Dictionary<string, int> VariantUses = new Dictionary<string, int>(StringComparer.Ordinal);
            /// <summary>The sites counted already (one boundary plays the pattern once, whatever window lengths were cut there).</summary>
            public readonly HashSet<(string name, int tick, LibPoint anchor)> Seen = new HashSet<(string, int, LibPoint)>();
            public string BestBody;
            /// <summary>The unlocks the group's windows press, merged (the representative's own at <see cref="Finalize"/>).</summary>
            public Caps Needs;
            public int Length => Macro.Inputs.Count;
            /// <summary>Uses × ticks, the ranking score.</summary>
            public long Score => (long)Macro.Uses * Macro.Ticks;

            /// <summary>The window of the group's most used variant, and its caps id.</summary>
            public void Finalize_() { Macro.Caps = Needs.Equals(default(Caps)) ? null : Needs.Id; }
        }

        /// <summary>A route the miner reads: its inputs and the trace it replays over.</summary>
        public sealed class Source
        {
            public string Name, RoutePath, TracePath;
            /// <summary>Measured on, never mined from.</summary>
            public bool HeldOut;
            public override string ToString() => Name;
        }

        readonly List<Source> _corpus = new List<Source>();
        readonly Dictionary<string, Pattern> _patterns = new Dictionary<string, Pattern>(StringComparer.Ordinal);
        readonly Action<string> _log;
        List<Pattern> _ranked = new List<Pattern>();

        /// <summary><paramref name="corpus"/> is for tests and tools (null: the built-in corpus).</summary>
        public FullTasAutoMacros(Action<string> log = null, IEnumerable<Source> corpus = null)
        {
            _log = log ?? Console.Error.WriteLine;
            if (corpus != null) _corpus.AddRange(corpus);
            else _corpus.AddRange(DefaultCorpus());
        }

        /// <summary>The built-in corpus (the saved and judged whole-run records and the course records) over the configured traces folder.</summary>
        public static List<Source> DefaultCorpus(string tracesDir = null, string repo = null)
        {
            string root = RepoRoot(repo);
            tracesDir ??= TechGrafts.ResolveTechDir(Path.Combine(root, "enginesim"));
            var sources = new List<Source>();
            void Add(string name, string route, bool heldOut)
            {
                route = Path.IsPathRooted(route) ? route : Path.Combine(root, route);
                if (!File.Exists(route)) return;
                string trace = TraceOf(route, tracesDir);
                if (trace == null || !File.Exists(trace)) return;
                sources.Add(new Source { Name = name, RoutePath = route, TracePath = trace, HeldOut = heldOut });
            }
            // Mined: every run's saved best (fulltas/<run>.json) and the fastest judged route of each run (offline-best/judged).
            foreach (string run in new[] { "VMAN", "FalseEnding-Any%", "TrueEnding-ANY%" })
                Add(run + " best", Path.Combine(tracesDir, "fulltas", run + ".json"), false);
            foreach (var run in JudgedRuns(tracesDir))
            {
                Add(run.Key + " judged", run.Value[0], false);
                // Held out for the hit-rate measurement: the run's next slower judged routes.
                foreach (string slower in run.Value.Skip(1).Take(4)) Add(run.Key + " " + Path.GetFileNameWithoutExtension(slower), slower, true);
            }
            // The course records: the fastest judged route per course and caps; the next fastest per course is held out.
            foreach (var group in CourseRoutes(tracesDir))
            {
                Add(group.Key + " record", group.Value[0], false);
                if (group.Value.Count > 1) Add(group.Key + " second", group.Value[1], true);
            }
            Add("TrueEnding-ANY% closest", Path.Combine(tracesDir, "fulltas", "TrueEnding-ANY%.closest.json"), true);
            return sources;
        }

        /// <summary>The judged whole-run routes (offline-best/judged/fulltas-&lt;run&gt;-&lt;seconds&gt;.json) by run, fastest first.</summary>
        static IEnumerable<KeyValuePair<string, List<string>>> JudgedRuns(string tracesDir)
        {
            string dir = Path.Combine(tracesDir, "offline-best", "judged");
            if (!Directory.Exists(dir)) return Enumerable.Empty<KeyValuePair<string, List<string>>>();
            var runs = new Dictionary<string, List<(double time, string path)>>();
            foreach (string f in Directory.GetFiles(dir, "fulltas-*.json"))
            {
                string stem = Path.GetFileNameWithoutExtension(f).Substring("fulltas-".Length);
                int dash = stem.LastIndexOf('-');
                if (dash <= 0 || !double.TryParse(stem.Substring(dash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) continue;
                string run = stem.Substring(0, dash);
                if (!runs.TryGetValue(run, out var list)) runs[run] = list = new List<(double, string)>();
                list.Add((t, f));
            }
            return runs.Select(r => new KeyValuePair<string, List<string>>(r.Key, r.Value.OrderBy(x => x.time).Select(x => x.path).ToList()));
        }

        /// <summary>The judged routes grouped by course and caps, fastest first (offline-best/judged).</summary>
        static IEnumerable<KeyValuePair<string, List<string>>> CourseRoutes(string tracesDir)
        {
            string dir = Path.Combine(tracesDir, "offline-best", "judged");
            if (!Directory.Exists(dir)) yield break;
            var groups = new SortedDictionary<string, List<(float time, string path)>>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.StartsWith("fulltas-", StringComparison.Ordinal)) continue;
                int cut = name.LastIndexOf('-');
                if (cut <= 0 || !float.TryParse(name.Substring(cut + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float time)) continue;
                string key = name.Substring(0, cut);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<(float, string)>();
                list.Add((time, file));
            }
            foreach (var g in groups)
                yield return new KeyValuePair<string, List<string>>(g.Key, g.Value.OrderBy(e => e.time).Select(e => e.path).ToList());
        }

        /// <summary>The repo root (the folder holding enginesim), walked up from the running binary, else the current folder.</summary>
        public static string RepoRoot(string repo = null)
        {
            if (!string.IsNullOrEmpty(repo)) return repo;
            for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "enginesim", "Runtime"))) return dir.FullName;
            return Directory.GetCurrentDirectory();
        }

        /// <summary>The trace a route file replays over: fulltas-&lt;run&gt;.json for a Full-TAS route (its "fulltas" run name or
        /// its "name"), start-c&lt;course&gt;-&lt;caps&gt;.json (else a route-c&lt;course&gt;-&lt;caps&gt;-*.json) for a course route.</summary>
        public static string TraceOf(string routePath, string tracesDir)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(routePath), new JsonDocumentOptions { MaxDepth = 256 });
            JsonElement root = doc.RootElement;
            string run = null;
            foreach (string key in new[] { "fulltas", "name" })
                if (run == null && root.TryGetProperty(key, out JsonElement r) && r.ValueKind == JsonValueKind.String) run = r.GetString();
            if (!string.IsNullOrEmpty(run)) return Path.Combine(tracesDir, "fulltas-" + run + ".json");
            if (root.TryGetProperty("course", out JsonElement c) && root.TryGetProperty("capabilities", out JsonElement caps))
            {
                string course = c.GetRawText(), id = caps.GetString().Replace("_", "").ToLowerInvariant();
                string exact = Path.Combine(tracesDir, "start-c" + course + "-" + id + ".json");
                if (File.Exists(exact)) return exact;
                string[] found = Directory.GetFiles(tracesDir, "route-c" + course + "-" + id + "-*.json");
                return found.Length == 0 ? null : found.OrderBy(f => f, StringComparer.Ordinal).First();
            }
            return null;
        }

        // ---- mining ----

        /// <summary>What one replayed route showed: the anchors on it and the player state after each input boundary.</summary>
        public sealed class Replay
        {
            public List<InputTick> Inputs;
            /// <summary>The kinds filed at each input boundary (bit flags), and the goal label a Goal anchor touched.</summary>
            public LibPoint[] Kind;
            public string[] GoalLabel;
            public int[] DashesLeft, AirJumpsLeft;
            public bool[] FacingRight, OnWall;
            public float[] X, Y, Speed;
            /// <summary>Goals reached at the boundary (the goal a mid-goal pattern serves).</summary>
            public int[] Progress;
            public bool Died;
        }

        static float Speed(Movement m)
        {
            float x = m.Velocity.x + m.momentum.x, y = m.Velocity.y + m.momentum.y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        /// <summary>Replays a route over its trace with the solver's own contact detection and files the anchors at the input
        /// boundaries the run passes them at (the library's TraceRun semantics: a contact seen in a tick is filed when the tick
        /// ends). <paramref name="captureAt"/> fills with the exact state at the listed boundaries.</summary>
        public static Replay ReplayRoute(Simulation sim, List<InputTick> inputs, string[] labels,
            Dictionary<int, SimSnapshot> captureAt = null)
        {
            var r = new Replay
            {
                Inputs = inputs,
                Kind = new LibPoint[inputs.Count + 1],
                GoalLabel = new string[inputs.Count + 1],
                DashesLeft = new int[inputs.Count + 1],
                AirJumpsLeft = new int[inputs.Count + 1],
                FacingRight = new bool[inputs.Count + 1],
                OnWall = new bool[inputs.Count + 1],
                X = new float[inputs.Count + 1],
                Y = new float[inputs.Count + 1],
                Speed = new float[inputs.Count + 1],
                Progress = new int[inputs.Count + 1],
            };
            var log = new ContactLog();
            var probe = new ContactProbe(sim);
            LibPoint pending = LibPoint.None;
            int progress = sim.Progress, last = -1;
            bool dash = sim.Player.dashActive;
            RunResult run = sim.Run(inputs, null, tick =>
            {
                last = tick;
                log.Tick(tick, sim.Player, probe.NearGate());
                foreach ((int at, Contact kind) in log.Contacts)
                    if (at == tick)
                        switch (kind)
                        {
                            case Contact.Landed: pending |= LibPoint.Land; break;
                            case Contact.TouchedWall: pending |= LibPoint.Wall; break;
                        }
                if (tick >= 1 && tick <= inputs.Count && inputs[tick - 1].reset) pending |= LibPoint.Reset;
                if (tick <= inputs.Count)
                {
                    if (pending != LibPoint.None)
                    {
                        r.Kind[tick] |= pending;
                        if ((pending & LibPoint.Goal) != 0 && labels != null && sim.Progress >= 1 && sim.Progress <= labels.Length)
                            r.GoalLabel[tick] = labels[sim.Progress - 1];
                    }
                    Movement m = sim.Player;
                    r.DashesLeft[tick] = m.airDashesLeft;
                    r.AirJumpsLeft[tick] = m.airJumpsLeft;
                    r.FacingRight[tick] = m.facingRight;
                    r.OnWall[tick] = m.OnWall;
                    WorldPos p = sim.PlayerWorld;
                    r.X[tick] = p.x;
                    r.Y[tick] = p.y;
                    r.Speed[tick] = Speed(m);
                    r.Progress[tick] = sim.Progress;
                    if (captureAt != null && captureAt.ContainsKey(tick)) captureAt[tick] = sim.Capture(tick);
                }
                pending = LibPoint.None;
                return true;
            }, 1000000, _ =>
            {
                Movement m = sim.Player;
                if (!m.dashActive && dash) pending |= LibPoint.DashEnd;
                if (sim.Progress != progress) pending |= LibPoint.Goal;
                dash = m.dashActive;
                progress = sim.Progress;
            });
            r.Died = run.EndReason != null && run.EndReason.StartsWith("Player death", StringComparison.Ordinal);
            return r;
        }

        /// <summary>The windows around the route's anchors, as patterns.</summary>
        void MineReplay(string name, Replay r, string capsId)
        {
            for (int t = 1; t <= r.Inputs.Count; t++)
            {
                LibPoint kind = r.Kind[t];
                if (kind == LibPoint.None) continue;
                foreach (LibPoint anchor in Anchors(kind))
                {
                    if (anchor is LibPoint.Reset or LibPoint.Goal)
                    {
                        int lap = Lap(r, t, anchor);
                        if (lap > 0) Cut(name, r, t, anchor, lap, capsId);
                    }
                    Cut(name, r, t, anchor, Math.Min(ContactWindow, r.Inputs.Count - t), capsId);
                }
            }
        }

        /// <summary>The anchor kinds one boundary files (Goal first: a landing can be the goal's tick too).</summary>
        public static IEnumerable<LibPoint> Anchors(LibPoint kind)
        {
            foreach (LibPoint p in new[] { LibPoint.Goal, LibPoint.Reset, LibPoint.Land, LibPoint.Wall, LibPoint.DashEnd })
                if ((kind & p) != 0) yield return p;
        }

        /// <summary>A Reset/Goal anchor's window: to the next anchor of the same kind (one repeated lap), capped.</summary>
        static int Lap(Replay r, int from, LibPoint anchor)
        {
            for (int t = from + MinLap; t <= r.Inputs.Count && t - from <= LapWindow; t++)
                if ((r.Kind[t] & anchor) != 0) return t - from;
            return 0;
        }

        void Cut(string name, Replay r, int t, LibPoint anchor, int w, string capsId)
        {
            if (w < 3 || t + w > r.Inputs.Count) return;
            var window = r.Inputs.GetRange(t, w);
            // A window with no button in it is travel, not tech (TechGrafts.Cut); one that resets is a restart, not a pattern.
            if (!window.Any(k => k.press || k.release || k.dash || k.springDash > 0) || window.Any(k => k.reset)) return;
            var (dashes, jumps, wallJump, omni) = Needs(window, r, t, w);
            bool facing = r.FacingRight[t];
            string body = Solve.EncodeTicks(window);
            // Windows group on the trigger's button edges (the press, release or dash that does the tech) and the anchor and
            // facing: the same tech done with a different stick or hold length is the same pattern. Travel (no edge in the
            // trigger's ticks) groups only when its whole body matches.
            string sig = HeadSignature(window);
            string key = ((int)anchor) + "|" + (facing ? "R" : "L") + "|" + (sig.Length > 0 ? "s" + sig : "b" + body);
            if (!_patterns.TryGetValue(key, out Pattern p))
            {
                string shape = Shape(anchor, window);
                p = new Pattern
                {
                    FacingRight = facing, DashesNeeded = dashes, AirJumpsNeeded = jumps, Shape = shape, BestBody = body,
                    Needs = new Caps { Dashes = dashes, AirJumps = jumps, WallJump = wallJump, Omni = omni },
                    Macro = new Macro
                    {
                        Anchor = anchor,
                        Inputs = window,
                        Name = shape + ":" + anchor + ":" + StableHash(key).ToString("x8", CultureInfo.InvariantCulture),
                    },
                };
                p.VariantUses[body] = 0;
                _patterns[key] = p;
            }
            p.VariantUses.TryGetValue(body, out int used);
            p.VariantUses[body] = used + 1;
            if (body != p.BestBody && used + 1 > p.VariantUses[p.BestBody]) { p.BestBody = body; p.Macro.Inputs = window; }
            // One boundary plays the pattern once, whatever window lengths were cut at it.
            if (!p.Seen.Add((name, t, anchor))) return;
            p.DashesNeeded = Math.Max(p.DashesNeeded, dashes);
            p.AirJumpsNeeded = Math.Max(p.AirJumpsNeeded, jumps);
            p.Needs.Dashes = Math.Max(p.Needs.Dashes, dashes);
            p.Needs.AirJumps = Math.Max(p.Needs.AirJumps, jumps);
            p.Needs.WallJump |= wallJump;
            p.Needs.Omni |= omni;
            p.Macro.Uses++;
            p.Macro.Ticks += w;
            p.Sites++;
            if (p.Where.Count < 6) p.Where.Add(name + "@" + t + " g" + r.Progress[t]
                + (r.GoalLabel[t] == null ? "" : " " + r.GoalLabel[t]) + (capsId == null ? "" : " [" + capsId + "]"));
        }

        /// <summary>The window's button edges from its trigger (its first press, release or dash) over
        /// <see cref="HeadTicks"/> ticks, offsets from the trigger: the tech without the lead-in, stick and hold noise around
        /// it, so the same tech groups whatever delay it was played at after the anchor. Empty when the window holds no edge
        /// (travel).</summary>
        static string HeadSignature(List<InputTick> window)
        {
            int start = window.FindIndex(k => k.press || k.release || k.dash || k.springDash > 0);
            if (start < 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = start; i < window.Count && i - start < HeadTicks; i++)
            {
                InputTick k = window[i];
                if (k.dash) sb.Append(i - start).Append(":D").Append(Sign(k.x)).Append(Sign(k.y)).Append(k.dashJump)
                    .Append(k.turnX.HasValue ? "t" : "").Append(k.turnY.HasValue ? "u" : "").Append(' ');
                else if (k.springDash > 0) sb.Append(i - start).Append(":S").Append(k.springDash).Append(' ');
                if (k.press) sb.Append(i - start).Append(":P").Append(Sign(k.x)).Append(Sign(k.y)).Append(' ');
                if (k.release) sb.Append(i - start).Append(":R").Append(' ');
            }
            return sb.ToString();
        }

        static string Sign(float v) => v > 0f ? "+" : v < 0f ? "-" : "0";

        static ulong StableHash(string text)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in text) h = (h ^ c) * 1099511628211UL;
            return h;
        }

        /// <summary>The resources the window spends before any refresh (the smallest dash/jump count it reaches), and the
        /// unlocks it presses: a wall jump (a press on a wall), the omnidash (a dash's vertical freeze turn).</summary>
        static (int dashes, int jumps, bool wallJump, bool omni) Needs(List<InputTick> window, Replay r, int t, int w)
        {
            bool wallJump = false, omni = false;
            int lowDashes = r.DashesLeft[t], lowJumps = r.AirJumpsLeft[t], presses = 0;
            for (int i = 0; i < w; i++)
            {
                InputTick k = window[i];
                lowDashes = Math.Min(lowDashes, r.DashesLeft[t + i]);
                lowJumps = Math.Min(lowJumps, r.AirJumpsLeft[t + i]);
                if (k.press && r.OnWall[t + i]) wallJump = true;
                if (k.turnY.HasValue && k.turnY.Value != 0f) omni = true;
                if (k.dash || k.dashJump > 0 || k.springDash > 0) presses++;
            }
            int dashes = Math.Max(r.DashesLeft[t] - lowDashes, presses > 0 ? 1 : 0), jumps = r.AirJumpsLeft[t] - lowJumps;
            return (Math.Min(dashes, 2), Math.Min(jumps, 2), wallJump, omni);
        }

        /// <summary>What the window shows (the report's buckets): the down-dash landing chain, a hyper (a dash off the ground
        /// with its jump frame), a wall dash (the dash off a wall), a repeated lap, else a tech window.</summary>
        static string Shape(LibPoint anchor, List<InputTick> window)
        {
            int dash = window.FindIndex(k => k.dash);
            if (anchor is LibPoint.Reset or LibPoint.Goal && window.Count >= MinLap) return "lap";
            if (dash >= 0 && dash <= 4 && window[dash].y < 0f && anchor == LibPoint.Land) return "down-dash-landing";
            if (dash >= 0 && dash <= 4 && window[dash].dashJump > 0) return "hyper";
            if (dash >= 0 && dash <= 6 && (anchor == LibPoint.Wall || window[dash].turnX.HasValue)) return "wall-dash";
            return "tech";
        }

        /// <summary>Re-mines: the corpus once per instance, then every library run not mined yet (after a new best). It runs on
        /// its own thread: building a Simulation and replaying on the caller's thread clobbers the engine's thread-static
        /// state, and the loop's own Simulation plays on that thread (VerifyFresh isolates the same way). <see cref="For"/>
        /// never replays and nothing is kept in statics, so it is safe on any thread.</summary>
        public void Mine(StateLibrary library)
        {
            var thread = new System.Threading.Thread(() =>
            {
                try { MineCore(library); }
                catch (Exception ex) { _log("macros: mine: " + ex.GetBaseException().Message); }
            }) { IsBackground = true, Name = "fulltas-macros" };
            thread.Start();
            thread.Join();
        }

        bool _corpusMined;
        readonly HashSet<ulong> _minedRuns = new HashSet<ulong>();

        void MineCore(StateLibrary library)
        {
            if (!_corpusMined)
            {
                _corpusMined = true;
                foreach (Source source in _corpus.Where(s => !s.HeldOut))
                {
                    try { MineFile(source); }
                    catch (Exception ex) { _log("macros: " + source.Name + ": " + ex.GetBaseException().Message); }
                }
            }
            if (library is StateLibrary lib)
                foreach (StateLibrary.LibRun run in lib.AllRuns)
                {
                    if (!_minedRuns.Add(run.Hash == 0 ? (ulong)run.Id + 1UL : run.Hash)) continue;
                    try { MineRun(lib, run); }
                    catch (Exception ex) { _log("macros: run " + run.How + ": " + ex.GetBaseException().Message); }
                }
            foreach (Pattern p in _patterns.Values) p.Finalize_();
            _ranked = _patterns.Values.Where(p => p.Macro.Uses >= MinUses).OrderByDescending(p => p.Score).ThenByDescending(p => p.Macro.Uses).ToList();
        }

        void MineFile(Source source)
        {
            List<InputTick> inputs;
            string[] labels;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(source.RoutePath), new JsonDocumentOptions { MaxDepth = 256 }))
            {
                inputs = Solve.ReadInputs(doc.RootElement);
                labels = LabelsOf(doc.RootElement);
            }
            labels ??= TraceLabels(source.TracePath);
            Simulation sim = Solve.Creator(source.TracePath, Solve.ReadHeader(source.TracePath))();
            MineReplay(source.Name, ReplayRoute(sim, inputs, labels), CapsOf(source.RoutePath));
        }

        /// <summary>The library run's own anchors (its filed states) and inputs, as one route's worth of patterns.</summary>
        void MineRun(StateLibrary lib, StateLibrary.LibRun run)
        {
            List<InputTick> inputs = run.Inputs.ToList();
            var r = new Replay
            {
                Inputs = inputs,
                Kind = new LibPoint[inputs.Count + 1],
                GoalLabel = new string[inputs.Count + 1],
                DashesLeft = new int[inputs.Count + 1],
                AirJumpsLeft = new int[inputs.Count + 1],
                FacingRight = new bool[inputs.Count + 1],
                OnWall = new bool[inputs.Count + 1],
                X = new float[inputs.Count + 1],
                Y = new float[inputs.Count + 1],
                Speed = new float[inputs.Count + 1],
                Progress = new int[inputs.Count + 1],
            };
            foreach (int id in run.States)
            {
                LibState s = lib.State(id);
                if (s.Input <= 0 || s.Input > inputs.Count) continue;
                r.Kind[s.Input] |= s.Kind & (LibPoint.Land | LibPoint.Wall | LibPoint.DashEnd | LibPoint.Reset | LibPoint.Goal);
                r.DashesLeft[s.Input] = s.Features.DashesLeft;
                r.AirJumpsLeft[s.Input] = s.Features.AirJumpsLeft;
                r.FacingRight[s.Input] = s.Features.FacingRight;
                r.OnWall[s.Input] = s.Kind == LibPoint.Wall;
                r.Progress[s.Input] = s.Goal;
            }
            // Between anchors the state is the next anchor's (a window's needs only grow more conservative this way).
            int nextDashes = 0, nextJumps = 0;
            bool nextFacing = true, nextWall = false;
            for (int t = inputs.Count; t >= 1; t--)
            {
                if (r.Kind[t] != LibPoint.None) { nextDashes = r.DashesLeft[t]; nextJumps = r.AirJumpsLeft[t]; nextFacing = r.FacingRight[t]; nextWall = r.OnWall[t]; }
                else { r.DashesLeft[t] = nextDashes; r.AirJumpsLeft[t] = nextJumps; r.FacingRight[t] = nextFacing; r.OnWall[t] = nextWall; }
            }
            MineReplay("lib:" + run.How, r, null);
        }

        static string[] LabelsOf(JsonElement root)
        {
            if (!root.TryGetProperty("goals", out JsonElement goals) || goals.ValueKind != JsonValueKind.Array) return null;
            var labels = new List<string>();
            foreach (JsonElement g in goals.EnumerateArray()) labels.Add(g.ValueKind == JsonValueKind.String ? g.GetString() : g.GetProperty("label").GetString());
            return labels.ToArray();
        }

        static string[] TraceLabels(string tracePath)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(tracePath), new JsonDocumentOptions { MaxDepth = 256 });
                if (doc.RootElement.TryGetProperty("fulltas", out JsonElement ft)) return LabelsOf(ft);
            }
            catch (Exception) { }
            return null;
        }

        static string CapsOf(string routePath)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(routePath), new JsonDocumentOptions { MaxDepth = 256 });
                if (doc.RootElement.TryGetProperty("capabilities", out JsonElement caps)) return caps.GetString();
            }
            catch (Exception) { }
            return null;
        }

        // ---- the contract ----

        /// <summary>Macros playable from this state: its anchor kind and unlocks match, and its resources and facing cover the
        /// pattern's preconditions. Ranked by how often the good routes played them.</summary>
        public IReadOnlyList<Macro> For(LibState state, string caps)
        {
            bool known = Caps.TryParse(caps, out Caps run);
            var list = new List<Macro>();
            foreach (Pattern p in _ranked)
            {
                if ((state.Kind & p.Macro.Anchor) == 0) continue;
                if (p.FacingRight != state.Features.FacingRight) continue;
                if (state.Features.DashesLeft < p.DashesNeeded || state.Features.AirJumpsLeft < p.AirJumpsNeeded) continue;
                if (known && p.Macro.Caps != null && !Caps.Parse(p.Macro.Caps).IsSubsetOf(run)) continue;
                list.Add(p.Macro);
                if (list.Count >= TopFor) break;
            }
            return list;
        }

        /// <summary>Everything mined, best first (the report and the measurement read it).</summary>
        public IReadOnlyList<Pattern> Patterns => _ranked;

        // ---- measurement ----

        /// <summary>One macro application from a state sampled on a held-out route, against the route's own next N ticks.</summary>
        public sealed class Sample
        {
            public string Route, Shape, Name;
            public int Tick, N;
            public bool WonPosition, WonSpeed, Died;
            /// <summary>Units the macro covered along the route's own direction, the route's own, and their end speeds.</summary>
            public float MacroAlong, RouteAlong, MacroSpeed, RouteSpeed;
            public long MacroPhysics, RoutePhysics;
            /// <summary>How far the route's own continuation run from the restored state ended from where the original replay
            /// ended: the exact-restore check (0: the comparison starts from a faithful state).</summary>
            public float OwnDrift;
        }

        /// <summary>
        /// The hit rate: from states sampled along the held-out routes, the share of pattern applications that beat the route's
        /// own next N ticks in position and speed (N = the pattern's input ticks). Both continuations run from the same exact
        /// state (the engine's exact restore), so the comparison is like for like.
        /// </summary>
        public List<Sample> Measure(int perRoute = 120)
        {
            var samples = new List<Sample>();
            foreach (Source source in _corpus.Where(s => s.HeldOut))
            {
                List<InputTick> inputs;
                string[] labels;
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(source.RoutePath), new JsonDocumentOptions { MaxDepth = 256 }))
                {
                    inputs = Solve.ReadInputs(doc.RootElement);
                    labels = LabelsOf(doc.RootElement) ?? TraceLabels(source.TracePath);
                }
                Simulation sim = Solve.Creator(source.TracePath, Solve.ReadHeader(source.TracePath))();
                samples.AddRange(MeasureOn(sim, source.Name, inputs, perRoute));
                _log("macros: measured " + source.Name);
            }
            return samples;
        }

        /// <summary>Applications at up to <paramref name="perRoute"/> anchors of one route, against the route's own inputs.</summary>
        public List<Sample> MeasureOn(Simulation sim, string name, List<InputTick> inputs, int perRoute)
        {
            // The anchors, then one replay that also captures the sampled states: the applications and their baseline (the
            // route's own next N ticks) all come from the run the states were taken on.
            Replay first = ReplayRoute(sim, inputs, null);
            var anchors = new List<int>();
            for (int t = 1; t <= inputs.Count; t++)
                if (first.Kind[t] != LibPoint.None && Anchors(first.Kind[t]).Any(a => _ranked.Any(p => p.Macro.Anchor == a)))
                    anchors.Add(t);
            int stride = Math.Max(1, anchors.Count / Math.Max(1, perRoute));
            var snaps = new Dictionary<int, SimSnapshot>();
            for (int i = 0; i < anchors.Count; i += stride) snaps[anchors[i]] = null;
            Replay replay = ReplayRoute(sim, inputs, null, snaps);
            var samples = new List<Sample>();
            foreach (var pair in snaps)
            {
                int t = pair.Key;
                if (pair.Value == null) continue;
                foreach (LibPoint anchor in Anchors(replay.Kind[t]))
                    foreach (Pattern p in _ranked)
                    {
                        if (p.Macro.Anchor != anchor || p.FacingRight != replay.FacingRight[t]) continue;
                        if (replay.DashesLeft[t] < p.DashesNeeded || replay.AirJumpsLeft[t] < p.AirJumpsNeeded) continue;
                        int n = p.Length;
                        if (t + n > inputs.Count) continue;
                        samples.Add(Apply(sim, name, inputs, pair.Value, t, p, replay));
                    }
            }
            return samples;
        }

        /// <summary>Plays the pattern and the route's own next N ticks from the same state at <paramref name="tick"/>. Each list
        /// runs one tick past <paramref name="stop"/> (the route's own next input, else its last one again, as
        /// <see cref="Simulation.Advance"/> does) and stops at the boundary: on a list that ends there, MacroEngine.Finish zeroes
        /// the stick on its last tick, and every end position came out a tick short (the "restore drift" of 2 units).</summary>
        Sample Apply(Simulation sim, string route, List<InputTick> inputs, SimSnapshot snap, int tick, Pattern p, Replay replay)
        {
            int n = p.Length, stop = tick + n;
            var macro = new List<InputTick>(stop + 1);
            macro.AddRange(inputs.GetRange(0, tick));
            macro.AddRange(p.Macro.Inputs);
            macro.Add(macro[macro.Count - 1]);
            var own = inputs.GetRange(0, stop);
            own.Add(inputs[Math.Min(stop, inputs.Count - 1)]);
            var a = Run(sim, own, snap, stop);
            var b = Run(sim, macro, snap, stop);
            float ax = a.x - replay.X[tick], ay = a.y - replay.Y[tick], bx = b.x - replay.X[tick], by = b.y - replay.Y[tick];
            float ownLen = (float)Math.Sqrt(ax * ax + ay * ay), along = ownLen <= 0f ? (float)Math.Sqrt(bx * bx + by * by)
                : (bx * ax + by * ay) / ownLen;
            float drx = a.x - replay.X[stop], dry = a.y - replay.Y[stop];
            return new Sample
            {
                Route = route, Shape = p.Shape, Name = p.Macro.Name, Tick = tick, N = n,
                RouteAlong = ownLen, MacroAlong = b.died ? 0f : along,
                RouteSpeed = a.speed, MacroSpeed = b.speed,
                Died = b.died, MacroPhysics = b.physics, RoutePhysics = a.physics,
                OwnDrift = (float)Math.Sqrt(drx * drx + dry * dry),
                WonPosition = !b.died && along > ownLen + .01f,
                WonSpeed = !b.died && b.speed > a.speed + .01f,
            };
        }

        /// <summary>One continuation from the exact state at the sample: the end position, end speed, physics ticks and death.</summary>
        static (float x, float y, float speed, long physics, bool died) Run(
            Simulation sim, List<InputTick> inputs, SimSnapshot snap, int stop)
        {
            float x = 0f, y = 0f, speed = 0f;
            long physics = 0;
            RunResult r = sim.Run(inputs, snap, tick =>
            {
                WorldPos at = sim.PlayerWorld;
                x = at.x;
                y = at.y;
                speed = Speed(sim.Player);
                physics = sim.Engine.PhysicsTicks;
                return tick < stop;
            }, 1000000);
            bool died = r.EndReason != null && r.EndReason.StartsWith("Player death", StringComparison.Ordinal);
            return (x, y, speed, physics, died);
        }

        // ---- the command ----

        /// <summary>
        /// <c>macros</c>: mines the corpus and the library runs' patterns and measures the hit rate on the held-out routes
        /// (the share of applications that beat the route's own next N ticks in position and speed).
        /// </summary>
        public static int Command(string[] args)
        {
            string traces = TechGrafts.ResolveTechDir(Path.Combine(RepoRoot(), "enginesim"), Solve.Option(args, "--tech-dir", (string)null));
            var corpus = DefaultCorpus(traces, Solve.Option(args, "--repo", (string)null));
            if (args.Contains("--no-default")) corpus.Clear();
            foreach (string a in Args(args, "--mine")) corpus.Add(FileSource(a, traces, false));
            foreach (string a in Args(args, "--measure")) corpus.Add(FileSource(a, traces, true));
            var macros = new FullTasAutoMacros(corpus: corpus) { MinUses = Solve.Option(args, "--min-uses", 2) };
            macros.Mine(null);
            Console.WriteLine($"macros: {macros.Patterns.Count} patterns from {corpus.Count(s => !s.HeldOut)} routes");
            foreach (var g in macros.Patterns.GroupBy(p => p.Shape).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {g.Key,-18} {g.Count(),4} patterns, {g.Sum(p => p.Macro.Uses),5} uses");
            foreach (Pattern p in macros.Patterns.Take(Solve.Option(args, "--dump", 12)))
                Console.WriteLine($"  {p.Shape,-18} {p.Macro.Anchor,-8} uses {p.Macro.Uses,3} ticks {p.Macro.Ticks,5} len {p.Length,3}"
                    + $" dash {p.DashesNeeded} jump {p.AirJumpsNeeded} {(p.FacingRight ? "right" : "left")} {string.Join(" ", p.Where.Take(2))}");
            List<Sample> samples = macros.Measure(Solve.Option(args, "--samples", 120));
            Report(samples, macros.Patterns, Solve.Option(args, "--out", (string)null));
            return 0;
        }

        static IEnumerable<string> Args(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) yield return args[i + 1];
        }

        static Source FileSource(string spec, string traces, bool heldOut)
        {
            string route = spec, trace = null;
            int at = spec.IndexOf('@');
            if (at > 0) { route = spec.Substring(0, at); trace = spec.Substring(at + 1); }
            return new Source { Name = Path.GetFileNameWithoutExtension(route), RoutePath = route, TracePath = trace ?? TraceOf(route, traces), HeldOut = heldOut };
        }

        /// <summary>The hit rate overall and per pattern shape, printed and written to <paramref name="outPath"/>.</summary>
        public static void Report(List<Sample> samples, IReadOnlyList<Pattern> patterns, string outPath)
        {
            var text = new System.Text.StringBuilder();
            void Line(string s) { Console.WriteLine(s); text.AppendLine(s); }
            int apps = samples.Count, both = samples.Count(s => s.WonPosition && s.WonSpeed),
                pos = samples.Count(s => s.WonPosition), speed = samples.Count(s => s.WonSpeed);
            var drifts = samples.Select(s => s.OwnDrift).OrderBy(d => d).ToList();
            float max = drifts.Count == 0 ? 0f : drifts[drifts.Count - 1], med = drifts.Count == 0 ? 0f : drifts[drifts.Count / 2],
                p95 = drifts.Count == 0 ? 0f : drifts[drifts.Count * 95 / 100];
            int off = samples.Count(s => s.OwnDrift > .1f);
            Line($"macros: {patterns.Count} patterns mined; {apps} applications on held-out routes:");
            Line($"  beat the route's own next N ticks in position AND speed {both} ({Pct(both, apps)}),"
                + $" position {Pct(pos, apps)}, speed {Pct(speed, apps)}");
            Line($"  own-continuation rerun drift after the restore: median {med:0.####}, p95 {p95:0.####}, max {max:0.##} units"
                + $" ({off} of {apps} over 0.1)");
            Line("  margin sensitivity (with the speed win, the macro's lead over the route's own end position):");
            foreach (float margin in new[] { 0f, 1f, 5f, 20f })
                Line($"    lead > {margin,4:0.#} units: {Pct(samples.Count(s => s.WonSpeed && s.MacroAlong > s.RouteAlong + margin), apps)}");
            Line($"    lead beyond the sample's own rerun drift: {Pct(samples.Count(s => s.WonSpeed && s.MacroAlong > s.RouteAlong + Math.Max(.01f, s.OwnDrift)), apps)}");
            Line("  by shape:");
            foreach (var g in samples.GroupBy(s => s.Shape).OrderByDescending(g => g.Count()))
            {
                int a = g.Count(), w = g.Count(s => s.WonPosition && s.WonSpeed), p = g.Count(s => s.WonPosition);
                Line($"    {g.Key,-18} {w,5}/{a,-6} {Pct(w, a),-7} position-only {Pct(p, a)}");
            }
            Line("  by route:");
            foreach (var g in samples.GroupBy(s => s.Route).OrderByDescending(g => g.Count()))
            {
                int a = g.Count(), w = g.Count(s => s.WonPosition && s.WonSpeed);
                Line($"    {g.Key,-26} {w,5}/{a,-6} {Pct(w, a)}");
            }
            Line("  mined patterns (top per shape):");
            foreach (var g in patterns.GroupBy(p => p.Shape).OrderBy(g => ShapeOrder(g.Key)))
                foreach (Pattern p in g.Take(4))
                    Line($"    {p.Shape,-18} {p.Macro.Anchor,-8} uses {p.Macro.Uses,3} sites {p.Sites,3} len {p.Length,3}"
                        + $" dash {p.DashesNeeded} jump {p.AirJumpsNeeded} {(p.FacingRight ? "right" : "left")}"
                        + (p.Macro.Caps == null ? "" : " caps " + p.Macro.Caps) + " | " + string.Join(" ", p.Where.Take(2)));
            if (outPath != null) File.WriteAllText(outPath, text.ToString());
        }

        static int ShapeOrder(string shape)
        {
            switch (shape)
            {
                case "down-dash-landing": return 0;
                case "hyper": return 1;
                case "wall-dash": return 2;
                case "lap": return 3;
                default: return 4;
            }
        }

        static string Pct(int n, int d) => d == 0 ? "-" : (100.0 * n / d).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }
}
