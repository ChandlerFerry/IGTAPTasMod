using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IGTAPTasMod;

namespace IGTAP.EngineSim
{
    /// <summary>Route-corpus grafts: the saved category, farm, bonus and transition routes (routes.json, <see cref="RouteName"/>)
    /// mined like the tech demos. Each route replays offline from its start trace (start-c&lt;target&gt;-&lt;caps&gt;.json, else a
    /// route-c&lt;target&gt;-&lt;caps&gt;-*.json captured from the same quick restart) with the solver's contact detection, and the
    /// window around each contact (<see cref="WindowBefore"/>..<see cref="WindowAfter"/>, travel without a button dropped, as
    /// <see cref="Cut"/>) is scored by what it did: its net displacement per physics tick from the contact to its end. Windows
    /// are keyed by what the run brings to the contact (the unlocks the window spends, the contact kind, the facing, the
    /// speed's bucket, the dashes and air jumps left); per key only those at or above the key's median and the fastest
    /// <see cref="RouteKeep"/> stay, after identical windows are merged (the faster kept). A window needs what it spends (a
    /// dash, an air jump, a wall jump, the omnidash's aim), never more than its route's category, and the category's refill
    /// orbs and zip movers. Its tech is "route &lt;target&gt;_&lt;CAT&gt;": one pick weight per route family, like one lab demo.
    /// Cached in route-grafts.cache.json beside the tech cache, keyed by the routes' inputs and the files they replay over.</summary>
    public sealed partial class TechGrafts
    {
        /// <summary>The route corpus merged into <see cref="Patterns"/> (<see cref="WithRoutes"/>); null before.</summary>
        [JsonIgnore] public TechGrafts Routes;
        /// <summary>A route corpus's mining time (its cache keeps the first mining's).</summary>
        public double MineSeconds;

        /// <summary>The route patterns' <see cref="TechPattern.Category"/> (<c>--no-route-grafts</c> drops them).</summary>
        public const string RouteCategory = "route";
        /// <summary>Windows kept per start key.</summary>
        public const int RouteKeep = 3;
        /// <summary>What the route miner cuts, versioned (bump it when the cut or the scoring changes).</summary>
        const int RouteMinerVersion = 1;

        /// <summary>A saved route's name: <c>&lt;target&gt;_&lt;CAT&gt;[ farm] &lt;time&gt;</c> (stale, tech/ and fulltas/ routes don't match).</summary>
        static readonly Regex RouteName = new Regex(@"^(?<target>[0-9a-z]+)_(?<cat>" + Caps.LabelPattern + @")(?<farm> farm)? (?<time>\d+\.\d+)$",
            RegexOptions.CultureInvariant);

        /// <summary>The hook's half: the route corpus (<see cref="LoadRoutes"/>) added to these patterns.</summary>
        TechGrafts WithRoutes(string tracesDir, bool refresh, string techCache)
        {
            Routes = LoadRoutes(tracesDir, refresh, Path.Combine(Path.GetDirectoryName(techCache), "route-grafts.cache.json"));
            Patterns.AddRange(Routes.Patterns);
            return this;
        }

        static TechGrafts LoadRoutes(string tracesDir, bool refresh, string cache)
        {
            string config = Directory.Exists(Path.GetDirectoryName(tracesDir)) ? Path.GetDirectoryName(tracesDir) : null;
            var sources = new List<DemoSource>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (MacroDefinition macro in Macros(config).Values.OrderBy(m => m.name, StringComparer.Ordinal))
            {
                Match m = RouteName.Match(macro.name ?? "");
                if (!m.Success || !Caps.TryParse(m.Groups["cat"].Value, out Caps caps) || !names.Add(macro.name)) continue;
                string target = m.Groups["target"].Value;
                var source = new DemoSource { Tech = new TechDef { id = macro.name, requires = caps.Label, start = target }, Macro = macro };
                source.Trace = RouteTrace(tracesDir, target, caps.Id);
                if (source.Trace != null)
                {
                    source.KeyFiles.Add(source.Trace);
                    source.KeyFiles.Add(WorldOf(source.Trace));
                }
                sources.Add(source);
            }
            string key = "routes " + RouteMinerVersion + " " + CacheKey(sources);
            if (!refresh && File.Exists(cache))
            {
                try
                {
                    TechGrafts cached = JsonSerializer.Deserialize<TechGrafts>(File.ReadAllText(cache), Json);
                    if (cached != null && cached.Key == key)
                    {
                        cached.CacheFile = cache;
                        cached.FromCache = true;
                        return cached;
                    }
                }
                catch (Exception) { }
            }
            var clock = Stopwatch.StartNew();
            var mined = new TechGrafts { CacheFile = cache, Key = key };
            var cuts = new List<RouteCut>();
            foreach (DemoSource source in sources) mined.Demos.Add(MineRoute(source, cuts));
            mined.Patterns.AddRange(KeepFastest(cuts, mined.Demos));
            mined.MineSeconds = clock.Elapsed.TotalSeconds;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache));
                File.WriteAllText(cache, JsonSerializer.Serialize(mined, Json));
            }
            catch (Exception ex) { Console.Error.WriteLine("route grafts: cache not written (" + ex.Message + ")"); }
            return mined;
        }

        // The route's start: its target's start trace in this category, else a route captured from that same quick restart.
        static string RouteTrace(string tracesDir, string target, string capsId)
        {
            string exact = Path.Combine(tracesDir, "start-c" + target + "-" + capsId + ".json");
            if (File.Exists(exact)) return exact;
            return Directory.Exists(tracesDir)
                ? Directory.GetFiles(tracesDir, "route-c" + target + "-" + capsId + "-*.json")
                    .Where(f => !f.EndsWith(".world.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault()
                : null;
        }

        /// <summary>One window of a route: its pattern, its start key and its speed (u per physics tick, contact to end).</summary>
        sealed class RouteCut
        {
            public TechPattern Pattern;
            public TechDemo Route;
            public string Key;
            public float Speed;
        }

        /// <summary>The route replayed from its start trace (canonical, as a solve starts), every contact's window cut.</summary>
        static TechDemo MineRoute(DemoSource source, List<RouteCut> cuts)
        {
            Match name = RouteName.Match(source.Tech.id);
            var demo = new TechDemo
            {
                Tech = RouteCategory + " " + name.Groups["target"].Value + "_" + name.Groups["cat"].Value, Name = source.Tech.id,
                Category = RouteCategory, Requires = source.Tech.requires, Start = source.Tech.start,
                Source = source.Trace == null ? null : Path.GetFileName(source.Trace),
            };
            if (source.Trace == null) { demo.Error = "no start trace for " + demo.Start + " " + demo.Requires; return demo; }
            try
            {
                using JsonDocument trace = JsonDocument.Parse(File.ReadAllText(source.Trace), new JsonDocumentOptions { MaxDepth = 256 });
                JsonElement root = trace.RootElement;
                var sim = new Simulation(World.Load(WorldOf(source.Trace)));
                sim.SetTarget(root);
                sim.LoadStart(root.GetProperty("start"), canonical: true);
                List<InputTick> inputs = ToInputs(source.Macro);
                demo.InputTicks = inputs.Count;
                int n = inputs.Count + 1;
                var x = new float[n]; var y = new float[n]; var vx = new float[n]; var vy = new float[n];
                var physics = new long[n]; var dashes = new int[n]; var jumps = new int[n];
                var right = new bool[n]; var wall = new bool[n];
                var log = new ContactLog();
                var probe = new ContactProbe(sim);
                int last = 0;
                void Record(int tick)
                {
                    if (tick >= n) return;
                    Movement p = sim.Player;
                    WorldPos at = sim.PlayerWorld;
                    (x[tick], y[tick], vx[tick], vy[tick]) = (at.x, at.y, p.Velocity.x + p.momentum.x, p.Velocity.y + p.momentum.y);
                    (physics[tick], dashes[tick], jumps[tick], right[tick], wall[tick]) = (sim.Engine.RunTicks, p.airDashesLeft, p.airJumpsLeft, p.facingRight, p.OnWall);
                }
                Record(0);
                // Run's budget counts physics ticks, a dash's too: the route plays to its finish.
                RunResult run = sim.Run(inputs, afterTick: tick =>
                {
                    last = tick;
                    log.Tick(tick, sim.Player, probe.NearGate());
                    Record(tick);
                    return true;
                }, extraTicks: 1000000, onStep: _ =>
                {
                    log.Slide((int)sim.Engine.RunTicks, sim.Player);
                    log.Step(sim);
                });
                demo.PhysicsTicks = run.Stepped;
                demo.Finished = run.Finished;
                demo.Died = run.EndReason != null && run.EndReason.StartsWith("Player death", StringComparison.Ordinal);
                demo.Notes.Add(run.Finished ? "finished: record " + run.Time.ToString("0.00", CultureInfo.InvariantCulture) + "s, "
                    + run.PhysicsTicks + " physics ticks" : "no finish: " + run.EndReason);
                // A route that doesn't replay to its finish here isn't the route that was judged: none of its windows.
                if (!run.Finished) return demo;
                Caps category = Caps.Parse(demo.Requires);
                int end = Math.Min(last, inputs.Count);
                foreach ((int tick, Contact kind) in log.Contacts)
                {
                    if (kind == Contact.Died || tick < 0 || tick >= end) continue;
                    int lo = Math.Max(0, tick - WindowBefore), hi = Math.Min(inputs.Count - 1, tick + WindowAfter);
                    int to = Math.Min(hi + 1, end);
                    long spent = physics[to] - physics[tick];
                    if (spent <= 0 || to - tick < 5) continue;
                    var window = inputs.GetRange(lo, hi - lo + 1);
                    if (!TouchContact(kind) && !window.Any(k => k.press || k.release || k.dash || k.reset)) continue;
                    // What the window spends, within its category: its dashes and air jumps (the most it goes below its start's,
                    // at least one for a dash pressed), a wall jump (a press on a wall), the omnidash's aim (a dash or freeze turn
                    // with a vertical); block swap with a dash; the category's orbs and zip movers as they were.
                    int lowDash = dashes[lo], lowJump = jumps[lo];
                    bool wallJump = false, aim = false, dash = false;
                    for (int i = lo; i <= hi; i++)
                    {
                        InputTick k = window[i - lo];
                        lowDash = Math.Min(lowDash, dashes[Math.Min(i + 1, end)]);
                        lowJump = Math.Min(lowJump, jumps[Math.Min(i + 1, end)]);
                        wallJump |= k.press && wall[i];
                        dash |= k.dash || k.dashJump > 0 || k.springDash > 0;
                        aim |= k.dash && k.y != 0f || k.turnY.HasValue && k.turnY.Value != 0f;
                    }
                    bool airJump = log.Contacts.Any(c => c.kind == Contact.DoubleJump && c.tick - 1 >= lo && c.tick - 1 <= hi);
                    var needs = new Caps
                    {
                        WallJump = category.WallJump && wallJump,
                        Dashes = Math.Min(category.Dashes, Math.Max(dashes[lo] - lowDash, dash ? 1 : 0)),
                        AirJumps = Math.Min(category.AirJumps, Math.Max(jumps[lo] - lowJump, airJump ? 1 : 0)),
                        Orbs = category.Orbs, Zip = category.Zip,
                    };
                    needs.BlockSwap = category.BlockSwap && needs.Dash;
                    needs.Omni = category.Omni && needs.Dash && aim;
                    var pattern = new TechPattern
                    {
                        Tech = demo.Tech, Name = demo.Name, Category = RouteCategory, Requires = needs,
                        Kind = kind, First = lo - tick, Last = hi - tick, Ticks = window.ToArray(), DemoContact = tick, DemoStart = demo.Source,
                        Dir = DirOf(kind, window, tick - lo), Resets = window.Any(k => k.reset),
                    };
                    float speed = (float)Math.Sqrt((x[to] - x[tick]) * (x[to] - x[tick]) + (y[to] - y[tick]) * (y[to] - y[tick])) / spent;
                    // The start key: what the run brings to the contact (the speed in 200 u/s bands, rising, level or falling).
                    string startKey = needs.Id + "|" + kind + "|" + (right[tick] ? "R" : "L") + "|" + Math.Min(5, (int)(Math.Abs(vx[tick]) / 200f))
                        + (vy[tick] > 100f ? "u" : vy[tick] < -100f ? "d" : "l") + "|" + dashes[tick] + "|" + jumps[tick];
                    cuts.Add(new RouteCut { Pattern = pattern, Route = demo, Key = startKey, Speed = speed });
                }
            }
            catch (Exception ex)
            {
                demo.Error = ex.GetBaseException().Message;
            }
            return demo;
        }

        /// <summary>Per start key the windows at or above its median speed, the fastest <see cref="RouteKeep"/>, identical windows
        /// merged first (the faster kept); each with its twin for a sided contact, listed under its route.</summary>
        static List<TechPattern> KeepFastest(List<RouteCut> cuts, List<TechDemo> routes)
        {
            var seen = new HashSet<TechPattern>(SameWindow.Instance);
            var unique = cuts.OrderByDescending(c => c.Speed).Where(c => seen.Add(c.Pattern)).ToList();
            foreach (var key in unique.GroupBy(c => c.Key, StringComparer.Ordinal))
            {
                float[] speeds = key.Select(c => c.Speed).OrderBy(s => s).ToArray();
                float median = speeds[speeds.Length / 2];
                foreach (RouteCut c in key.Where(c => c.Speed >= median).Take(RouteKeep)) AddWithTwin(c.Route, c.Pattern);
            }
            // The twins can repeat another route's window mirrored: one of each.
            seen.Clear();
            return routes.SelectMany(r => r.Patterns).Where(p => seen.Add(p)).ToList();
        }

        // ---- the CLI listing ----

        /// <summary>routegrafts &lt;traces dir or trace&gt; [--refresh] [--tech-dir &lt;dir&gt;] [--tech-cache &lt;file&gt;]: the saved
        /// routes replayed and mined, with the pattern counts per unlock set and contact kind.</summary>
        public static int RouteCommand(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: routegrafts <traces dir or trace.json> [--refresh] [--tech-dir <dir>] [--tech-cache <file>]   mine the saved routes into graft patterns");
                return 2;
            }
            string path = args[0];
            string tracesDir = ResolveTechDir(Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path)),
                Option(args, "--tech-dir"));
            string cache = Option(args, "--tech-cache") is string file ? Path.GetFullPath(file) : Path.Combine(tracesDir, "tech-grafts.cache.json");
            TechGrafts routes = LoadRoutes(tracesDir, args.Contains("--refresh"), Path.Combine(Path.GetDirectoryName(cache), "route-grafts.cache.json"));
            Console.WriteLine($"route grafts: {routes.Patterns.Count} patterns over {routes.Demos.Count(d => d.Patterns.Count > 0)} of {routes.Demos.Count} saved routes, "
                + $"mined in {routes.MineSeconds:0.0}s ({(routes.FromCache ? "cache" : "mined")}, key {routes.Key.Substring(0, 20)}, {routes.CacheFile})");
            foreach (TechDemo route in routes.Demos)
                Console.WriteLine($"  {route.Name,-44} {route.Source ?? "-",-40} {(route.Error ?? string.Join("; ", route.Notes)),-48} {route.Patterns.Count,4} patterns");
            Console.WriteLine();
            Console.WriteLine("by unlock set (the window's needs) and contact kind:");
            foreach (var set in routes.Patterns.GroupBy(p => p.Requires.Label).OrderBy(g => g.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {set.Key,-26} {set.Count(),5}: " + string.Join(", ", set.GroupBy(p => p.Kind).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count())));
            return 0;
        }

        static string Option(string[] args, string name)
        {
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }
    }
}
