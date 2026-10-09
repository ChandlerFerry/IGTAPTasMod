using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Caps = IGTAPTasMod.Caps;
using Categories = IGTAPTasMod.Categories;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>A place the run stands at, as the buy table's places (FullTasBuyTable.cs): its id the goal it stands at (`start:N`
    /// a course's gate, `finish:N` its finish, `buy:`/`reward:` a box, `at:` an area goal; a goal that buys several boxes at once,
    /// `buy:a+b`, is one place), `start` the run's start, or `restart@x,y` a quick restart's landing (one id per spot, within
    /// 48 u). X, Y: the world frame (WorldPos, FloatingOrigin's rebases undone); Caps: the unlocks (Caps.Id) the run first stood
    /// there with.</summary>
    sealed record Place(string Id, string Kind, string Box, double X, double Y, string Caps);

    /// <summary>Physics ticks from one place to another. To: a place id; a buy table box set, its labels in the order bought joined
    /// by <see cref="MoveTable.SetJoin"/> (`buy:a + buy:b`: one walk buying a then b, ending at b's place; not the goal `buy:a+b`,
    /// a place of its own); `lap:N`, Course N's gate then its finish (it ends at `finish:N`); or `record:N`, from Course N's gate
    /// a lap on its saved record's path (the clones' path; it ends at `finish:N`). Source: measured, buytable, best, farm, record
    /// or estimate.</summary>
    sealed record Move(string From, string To, int Ticks, string Source, bool Estimated);

    /// <summary>
    /// The economy planner's movement model (docs/fulltas.md, "Movement table"): ticks between places, per unlock set (Caps.Id).
    /// A move into a box the planner buys (an economy buy, FullTasGoal.IsEconomy) ends at the player's arrival, the first physics
    /// step its touch could buy (the box's trigger stay with its cooldown over): the Cash it waits for there is the economy's, not
    /// the walk's. Sources, by trust: the engine's measurements, realize's (<see cref="Measured"/>), the buy table's entries
    /// (walk, jump-through, dash-through) and the best's own splits (goal to goal and through each quick restart's landing, and a
    /// lap `lap:N` where the best plays a gate then its finish): the fewest ticks of them (a way that played once can play
    /// again); the saved course routes (routes.json): a farm route as a lap from the course's finish, a record as `record:N`
    /// from its gate. A pair none of them has is estimated, and marked so: the stored moves chained through the places they
    /// end at, else the walkable distance (<see cref="Walk"/>) at the unlocks' top path speed (FullTasAuto's, the best's path
    /// and the cached speed probes); a box set as its boxes
    /// one after another, a lap or record as the walk to its gate and the course's known run. A quick restart is never estimated: where it lands is the checkpoint the run last touched
    /// (README ### Rules 35-36), which the table doesn't follow, so <see cref="Restarts"/> offers only the landings a restart from
    /// that place was seen to reach. The planner expands a place through <see cref="Targets"/> (estimates included) and
    /// <see cref="Restarts"/>. Saved as fulltas/&lt;run&gt;.moves.json; a Save keeps the measured moves the file had, and Load reads
    /// them back.
    /// </summary>
    sealed class MoveTable
    {
        static readonly Dictionary<string, int> Trust = new Dictionary<string, int>
        {
            ["measured"] = 0, ["buytable"] = 0, ["best"] = 0, ["farm"] = 2, ["record"] = 3,
        };
        /// <summary>One place per spot within this reach (the buy table's PieceReach).</summary>
        internal const float PlaceReach = 48f;
        /// <summary>A saved route's seconds in physics ticks (50 Hz).</summary>
        const double TickSeconds = 0.02;
        /// <summary>A box set's labels in a To, in the order bought (as the buy table names its sets: a label has no spaces, an
        /// area goal's has commas).</summary>
        internal const string SetJoin = " + ";

        readonly Dictionary<(string from, string to, string caps), Move> _moves = new Dictionary<(string, string, string), Move>();
        readonly Dictionary<(string from, string caps), List<string>> _targets = new Dictionary<(string, string), List<string>>();
        readonly Dictionary<string, Place> _places = new Dictionary<string, Place>();
        /// <summary>Top path speed per unlock set, units per physics tick.</summary>
        readonly Dictionary<string, double> _speeds = new Dictionary<string, double>();
        /// <summary>Every unlock set the best or the buy table stood anywhere with.</summary>
        readonly HashSet<string> _caps = new HashSet<string>();
        /// <summary>Per start place and unlock set, the fewest ticks to each place over stored moves chained (<see cref="Chain"/>);
        /// cleared by every <see cref="Put"/>.</summary>
        readonly Dictionary<(string from, string caps), Dictionary<string, int>> _chains = new Dictionary<(string, string), Dictionary<string, int>>();
        /// <summary>The buy table's entries whose search found no way (<see cref="Missed"/>).</summary>
        readonly HashSet<(string from, string to, string caps)> _misses = new HashSet<(string, string, string)>();
        /// <summary>Moves realize tried from a real state and couldn't build, by (from, to, unlocks, tree group), with why: never
        /// offered again on this engine build (docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md, section 1).</summary>
        readonly Dictionary<(string from, string to, string caps, int tree), string> _failed = new Dictionary<(string, string, string, int), string>();
        /// <summary>This engine build (the Runtime and CLI assemblies' MVIDs) and its search budgets: a failure from another build or budget is
        /// dropped on Read (an engine fix or a bigger search can make it play).</summary>
        internal static readonly string Build = typeof(Simulation).Assembly.ManifestModule.ModuleVersionId.ToString("N") + "-"
            + typeof(MoveTable).Assembly.ManifestModule.ModuleVersionId.ToString("N") + "-" + FullTas.PieceTicks + "-" + FullTas.StepTicks;
        int Group => Math.Max(0, _treeGroup);

        /// <summary>Records that the move doesn't play here; true when it is a new fact. Failed from two places, it is out from
        /// every place (from "*"): what no search reaches from two places is out of reach (Zip%'s buyfree6b, tried from each place
        /// in turn, about 9 minutes each).</summary>
        internal bool Failed(string from, string to, string caps, string why)
        {
            if (!_failed.TryAdd((from, to, caps, Group), why)) return false;
            if (from != AnyPlace && _failed.Keys.Count(k => k.to == to && k.caps == caps && k.tree == Group && k.from != AnyPlace) >= 2)
                _failed.TryAdd((AnyPlace, to, caps, Group), "failed from two places; the last: " + why);
            _chains.Clear();
            return true;
        }

        const string AnyPlace = "*";

        internal bool HasFailed(string from, string to, string caps) => _failed.ContainsKey((from, to, caps, Group)) || _failed.ContainsKey((AnyPlace, to, caps, Group));

        internal IEnumerable<(string from, string to, string caps, int tree, string why)> Failures =>
            _failed.OrderBy(kv => kv.Key.from, StringComparer.Ordinal).ThenBy(kv => kv.Key.to, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.caps, StringComparer.Ordinal).ThenBy(kv => kv.Key.tree).Select(kv => (kv.Key.from, kv.Key.to, kv.Key.caps, kv.Key.tree, kv.Value));
        /// <summary>The best's goal stops (0: the run's start), from the replay Load built the table on.</summary>
        List<FullTas.Stop> _goals;
        /// <summary>The walkable distance (world units) to a place from each of these places (FullTas.WalkField: the engine's
        /// DistanceField, the run's avoid boxes as walls; infinite: no way); null for a table not built on a run (the straight
        /// line then).</summary>
        internal Func<Place, IReadOnlyList<Place>, double[]> Walk;
        /// <summary>Per target place, the walkable distance from each place (<see cref="Walk"/>, asked once per target).</summary>
        readonly Dictionary<string, Dictionary<string, double>> _walks = new Dictionary<string, Dictionary<string, double>>();

        public IReadOnlyDictionary<string, Place> Places => _places;
        public int Count => _moves.Count;

        public static MoveTable Load(FullTas run, string buyTableFile, string routesFile, FullTas.Route best, string movesFile = null)
        {
            var table = new MoveTable { Walk = run.WalkField() };
            var (stops, speeds) = run.BestStops(best);
            foreach (var v in speeds) table._speeds[v.Key] = v.Value;
            table.AddBest(stops);
            if (buyTableFile != null) table.AddBuyTable(buyTableFile);
            foreach (var (id, at) in run.CourseGates()) table.AddPlace(id, at.x, at.y, null);
            if (routesFile != null) { table.AddSavedLaps(routesFile); table.AddSavedTransitions(routesFile); table.AtTreeLevel(run.TreeLevel(best)); }
            movesFile ??= run.MovesFile;
            if (File.Exists(movesFile))
            {
                MoveTable saved = Read(movesFile);
                foreach (var kv in saved._moves)
                    if (kv.Value.Source == "measured") table.Put(kv.Value, kv.Key.caps);
                foreach (var f in saved.Failures) table._failed[(f.from, f.to, f.caps, f.tree)] = f.why;
            }
            return table;
        }

        /// <summary>The most trusted move from here to there with these unlocks, else an estimate; null when neither is known.</summary>
        public Move Best(string from, string to, string caps)
        {
            if (HasFailed(from, to, caps)) return null;
            if (_moves.TryGetValue((from, to, caps), out Move m)) return m;
            int ticks = Guess(from, to, caps);
            return ticks < 0 ? null : new Move(from, to, ticks, "estimate", true);
        }

        /// <summary>Every To <see cref="Best"/> has a move for from here with these unlocks: the stored ones first, then every place
        /// (but restart landings, the run's start and this place itself), gate's `lap:N` and `record:N` an estimate reaches. Box sets
        /// only as stored (the planner chains single buys, or asks <see cref="Best"/> for a set).</summary>
        public IEnumerable<string> Targets(string from, string caps)
        {
            var seen = new HashSet<string>();
            if (_targets.TryGetValue((from, caps), out List<string> stored))
                foreach (string to in stored)
                    if (seen.Add(to)) yield return to;
            foreach (Place p in _places.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                if (p.Kind == "restart" || p.Kind == "start") continue;
                string[] tos = p.Kind == "gate" ? new[] { p.Id, "lap:" + p.Id.Substring(6), "record:" + p.Id.Substring(6) } : new[] { p.Id };
                foreach (string to in tos)
                    if (to != from && !seen.Contains(to) && Guess(from, to, caps) >= 0 && seen.Add(to)) yield return to;
            }
        }

        /// <summary>The landings (`restart@x,y`) a quick restart from here was seen to reach with these unlocks (the best's or realize's
        /// moves into them): the checkpoint the run last touched decides where it lands, so the planner keys its place by the landing
        /// a restart takes it to and offers only these.</summary>
        public IEnumerable<string> Restarts(string from, string caps)
        {
            return _targets.TryGetValue((from, caps), out List<string> to) ? to.Where(t => t.StartsWith("restart@", StringComparison.Ordinal)) : Enumerable.Empty<string>();
        }

        /// <summary>Realize's real ticks for a move made with these unlocks: an engine measurement as the buy table's and the best's
        /// are, so it replaces an estimate, a saved route's lap or a slower measurement of the pair, never a faster one. A move
        /// whose ticks aren't known (negative: realize's way waited inside it for its Cash) isn't recorded.</summary>
        public void Measured(Move m, string caps)
        {
            if (m.Ticks >= 0) Put(m with { Source = "measured", Estimated = false }, caps);
        }

        public void Save(string file)
        {
            if (File.Exists(file))
            {
                MoveTable old = Read(file);
                foreach (var kv in old._moves)
                    if (kv.Value.Source == "measured") Put(kv.Value, kv.Key.caps);
                foreach (Place p in old._places.Values) _places.TryAdd(p.Id, p);
                foreach (var f in old.Failures) _failed.TryAdd((f.from, f.to, f.caps, f.tree), f.why);
                foreach (var v in old._speeds)
                    if (!_speeds.TryGetValue(v.Key, out double s) || v.Value > s) _speeds[v.Key] = v.Value;
            }
            var json = new JsonObject
            {
                ["places"] = new JsonArray(_places.Values.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Id, ["kind"] = p.Kind, ["box"] = p.Box, ["x"] = p.X, ["y"] = p.Y, ["caps"] = p.Caps,
                }).ToArray()),
                ["speeds"] = new JsonObject(_speeds.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => KeyValuePair.Create(v.Key, (JsonNode)v.Value))),
                ["moves"] = new JsonArray(_moves.OrderBy(kv => kv.Key.from, StringComparer.Ordinal).ThenBy(kv => kv.Key.to, StringComparer.Ordinal)
                    .ThenBy(kv => kv.Key.caps, StringComparer.Ordinal).Select(kv => (JsonNode)new JsonObject
                    {
                        ["from"] = kv.Value.From, ["to"] = kv.Value.To, ["caps"] = kv.Key.caps, ["ticks"] = kv.Value.Ticks,
                        ["source"] = kv.Value.Source, ["estimated"] = kv.Value.Estimated,
                    }).ToArray()),
                ["failed"] = new JsonArray(Failures.Select(f => (JsonNode)new JsonObject
                {
                    ["from"] = f.from, ["to"] = f.to, ["caps"] = f.caps, ["tree"] = f.tree, ["why"] = f.why, ["build"] = Build,
                }).ToArray()),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            Solve.WriteShared(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>A saved table as written: every move, place and speed.</summary>
        public static MoveTable Read(string file)
        {
            var table = new MoveTable();
            JsonNode json = JsonNode.Parse(File.ReadAllText(file));
            foreach (JsonNode p in json["places"].AsArray())
            {
                string id = p["id"].GetValue<string>();
                table._places[id] = new Place(id, p["kind"].GetValue<string>(), p["box"]?.GetValue<string>(), p["x"].GetValue<double>(),
                    p["y"].GetValue<double>(), p["caps"]?.GetValue<string>());
            }
            foreach (var v in json["speeds"].AsObject()) table._speeds[v.Key] = v.Value.GetValue<double>();
            foreach (JsonNode m in json["moves"].AsArray())
                table.Put(new Move(m["from"].GetValue<string>(), m["to"].GetValue<string>(), m["ticks"].GetValue<int>(), m["source"].GetValue<string>(),
                    m["estimated"].GetValue<bool>()), m["caps"].GetValue<string>());
            foreach (JsonNode f in json["failed"]?.AsArray() ?? new JsonArray())
                if (f["build"]?.GetValue<string>() == Build)
                    table._failed[(f["from"].GetValue<string>(), f["to"].GetValue<string>(), f["caps"].GetValue<string>(), f["tree"].GetValue<int>())] = f["why"].GetValue<string>();
            return table;
        }

        /// <summary>The best's stop at its goal k (0: the run's start); null for a table not built on a best.</summary>
        internal FullTas.Stop? GoalStop(int k) => _goals != null && k >= 0 && k < _goals.Count ? _goals[k] : null;

        /// <summary>The best's unlocks after its goal k (0: the run's start); null for a table not built on a best.</summary>
        internal string CapsAtGoal(int k) => GoalStop(k)?.Caps;

        /// <summary>The top path speed with these unlocks, units per physics tick (0: unknown).</summary>
        internal double Speed(string caps) => _speeds.TryGetValue(caps, out double v) ? v : 0.0;

        /// <summary>The whole table as text, one line per place, speed and move (a round trip's comparison).</summary>
        internal string Dump()
        {
            return string.Join("\n", _places.Values.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.ToString())
                .Concat(_speeds.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + " " + v.Value.ToString("R", CultureInfo.InvariantCulture)))
                .Concat(_moves.OrderBy(kv => kv.Key.from, StringComparer.Ordinal).ThenBy(kv => kv.Key.to, StringComparer.Ordinal)
                    .ThenBy(kv => kv.Key.caps, StringComparer.Ordinal).Select(kv => kv.Key.caps + " " + kv.Value)));
        }

        void Put(Move m, string caps)
        {
            var key = (m.From, m.To, caps);
            if (_moves.TryGetValue(key, out Move was))
            {
                int rank = Rank(m), wasRank = Rank(was);
                if (rank > wasRank || rank == wasRank && m.Ticks >= was.Ticks) return;
            }
            _moves[key] = m;
            _chains.Clear();
            if (!_targets.TryGetValue((m.From, caps), out List<string> to)) _targets[(m.From, caps)] = to = new List<string>();
            if (!to.Contains(m.To)) to.Add(m.To);
        }

        static int Rank(Move m)
        {
            int i = Trust.TryGetValue(m.Source, out int r) ? r : Trust.Count;
            return m.Estimated ? i + Trust.Count + 1 : i;
        }

        /// <summary>An estimate's ticks, -1 when it can't be made: a stored move as it is; a box set as its boxes in order; a lap or
        /// record as the walk to its gate and the course's known run; a restart landing never; else stored moves chained
        /// (<see cref="Chain"/>), else the walkable distance between the two places (<see cref="Walk"/>: through the level's free
        /// space, the run's avoid boxes as walls; the straight line on a table with no run, never less than it) at the unlocks'
        /// top path speed (a lower bound: air counts as walkable, so a climb is its height; a chain of played legs is a way the
        /// run can go), none where the walk finds no way, never under the fewest measured ticks to there from a spot of the same
        /// place (<see cref="Floor"/>). A pair the buy table's search found no way for (<see cref="Missed"/>) has no estimate with
        /// those unlocks, and no walk with more (only a chain).</summary>
        int Guess(string from, string to, string caps)
        {
            if (_moves.TryGetValue((from, to, caps), out Move m)) return m.Ticks;
            if (to.Contains(SetJoin, StringComparison.Ordinal))
            {
                int sum = 0;
                string at = from;
                foreach (string box in to.Split(SetJoin))
                {
                    int leg = Guess(at, box, caps);
                    if (leg < 0) return -1;
                    sum += leg;
                    at = box;
                }
                return sum;
            }
            bool lap = to.StartsWith("lap:", StringComparison.Ordinal), record = to.StartsWith("record:", StringComparison.Ordinal);
            if (lap || record)
            {
                string n = to.Substring(to.IndexOf(':') + 1), gate = "start:" + n;
                int walk = from == gate ? 0 : Guess(from, gate, caps);
                if (walk < 0) return -1;
                if (lap && _moves.TryGetValue((gate, "finish:" + n, caps), out Move course)) return walk + course.Ticks;
                if (_moves.TryGetValue((gate, "record:" + n, caps), out Move saved)) return walk + saved.Ticks;
                // A course nothing ran (no saved route with these unlocks): its gate to its finish as any other pair.
                int run = lap ? Guess(gate, "finish:" + n, caps) : -1;
                return run < 0 ? -1 : walk + run;
            }
            if (to.StartsWith("restart@", StringComparison.Ordinal)) return -1;
            bool? missed = Missed(from, to, caps);
            if (missed == true) return -1;
            int chain = Chain(from, to, caps);
            if (chain >= 0) return chain;
            if (missed == false) return -1;
            if (!_places.TryGetValue(from, out Place a) || !_places.TryGetValue(to, out Place b) || !_speeds.TryGetValue(caps, out double v) || v <= 0) return -1;
            double walked = Walked(a, b);
            if (double.IsPositiveInfinity(walked)) return -1;
            return Math.Max((int)Math.Ceiling(walked / v), Floor(from, to, caps));
        }

        /// <summary>The walkable distance from a to b (<see cref="Walk"/>), never under the straight line; the straight line on a
        /// table with no run; infinite where the walk finds no way.</summary>
        internal double Walked(Place a, Place b)
        {
            double line = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            if (Walk == null) return line;
            if (!_walks.TryGetValue(b.Id, out Dictionary<string, double> to) || !to.ContainsKey(a.Id))
            {
                List<Place> all = _places.Values.ToList();
                double[] d = Walk(b, all);
                _walks[b.Id] = to = new Dictionary<string, double>();
                for (int i = 0; i < all.Count; i++) to[all[i].Id] = d[i];
            }
            double walk = to[a.Id];
            return double.IsNaN(walk) ? line : Math.Max(line, walk);
        }

        /// <summary>The buy table searched from here to there and found no way (technique `miss`): true with these very unlocks,
        /// false with fewer (a subset; more may open a way, but nothing played shows one), null when it never missed.</summary>
        internal bool? Missed(string from, string to, string caps)
        {
            if (_misses.Contains((from, to, caps))) return true;
            if (!Caps.TryParse(caps, out Caps have)) return null;
            foreach (var miss in _misses)
                if (miss.from == from && miss.to == to && Caps.TryParse(miss.caps, out Caps fewer) && fewer.IsSubsetOf(have)) return false;
            return null;
        }

        /// <summary>The fewest measured ticks to there with these unlocks from any spot of the same place (a place within
        /// PlaceReach of this one, itself included); 0 when none was measured.</summary>
        internal int Floor(string from, string to, string caps)
        {
            if (!_places.TryGetValue(from, out Place a)) return 0;
            int fewest = 0;
            foreach (Place p in _places.Values)
                if (Math.Abs(p.X - a.X) <= PlaceReach && Math.Abs(p.Y - a.Y) <= PlaceReach && _moves.TryGetValue((p.Id, to, caps), out Move m) && !m.Estimated
                    && (fewest == 0 || m.Ticks < fewest)) fewest = m.Ticks;
            return fewest;
        }

        /// <summary>The buy table's searches that found no way (from, to, the unlocks searched with).</summary>
        internal IEnumerable<(string from, string to, string caps)> Misses => _misses;

        /// <summary>The fewest ticks from one place to another over stored moves, none of them estimated, chained through the places
        /// they end at (a lap or record lap at its course's finish, a box set at its last box); -1 when no chain reaches it. Each
        /// leg was played, so the chain is a way the run can go (not one it was seen to go in one piece).</summary>
        internal int Chain(string from, string to, string caps)
        {
            if (!_chains.TryGetValue((from, caps), out Dictionary<string, int> dist))
            {
                _chains[(from, caps)] = dist = new Dictionary<string, int> { [from] = 0 };
                var queue = new PriorityQueue<string, int>();
                queue.Enqueue(from, 0);
                while (queue.TryDequeue(out string at, out int d))
                {
                    if (d > dist[at] || !_targets.TryGetValue((at, caps), out List<string> tos)) continue;
                    foreach (string next in tos)
                    {
                        Move m = _moves[(at, next, caps)];
                        string end = EndOf(next);
                        if (m.Estimated || end == null || HasFailed(at, next, caps) || dist.TryGetValue(end, out int was) && was <= d + m.Ticks) continue;
                        dist[end] = d + m.Ticks;
                        queue.Enqueue(end, d + m.Ticks);
                    }
                }
            }
            return to != from && dist.TryGetValue(to, out int ticks) ? ticks : -1;
        }

        /// <summary>The place a move into this To ends at (null: none the table knows).</summary>
        string EndOf(string to)
        {
            if (to.StartsWith("lap:", StringComparison.Ordinal) || to.StartsWith("record:", StringComparison.Ordinal)) to = "finish:" + to.Substring(to.IndexOf(':') + 1);
            else if (to.Contains(SetJoin, StringComparison.Ordinal)) to = to.Substring(to.LastIndexOf(SetJoin, StringComparison.Ordinal) + SetJoin.Length);
            return _places.ContainsKey(to) ? to : null;
        }

        // ponytail: a place is the goal it stands at, so the best's or the buy table's several spots at one box (a wide box, a gate
        // crossed high or low) share an id and the cheapest way from any of them stands for all; per-spot ids if the plan's walks
        // come out short of the build's.
        void AddPlace(string id, double x, double y, string caps)
        {
            if (caps != null) _caps.Add(caps);
            if (_places.ContainsKey(id)) return;
            string kind = id == "start" ? "start" : id.StartsWith("start:", StringComparison.Ordinal) ? "gate" : id.StartsWith("finish:", StringComparison.Ordinal) ? "finish"
                : id.StartsWith("restart@", StringComparison.Ordinal) ? "restart" : id.StartsWith("at:", StringComparison.Ordinal) ? "at" : "box";
            _places[id] = new Place(id, kind, kind == "box" ? id : null, Math.Round(x, 2), Math.Round(y, 2), caps);
        }

        /// <summary>A box's place (world frame) where neither the best nor the buy table stood: its unlocks unknown (no caps), every
        /// move to and from it an estimate until realize measures one.</summary>
        internal void AddBoxPlace(string label, double x, double y) => AddPlace(label, x, y, null);

        string RestartId(double x, double y)
        {
            foreach (Place p in _places.Values)
                if (p.Kind == "restart" && Math.Abs(p.X - x) <= PlaceReach && Math.Abs(p.Y - y) <= PlaceReach) return p.Id;
            return "restart@" + Math.Round(x).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(y).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The best's stops in tick order (FullTas.BestStops): a move from each stop's tick to the next one's arrival, a
        /// goal-to-goal split past a restart's landing too, and a lap from each stop the best walks from to a gate and then that
        /// course's finish. A move whose end the best reached before its start (a box touched before a restart it came back from) is
        /// left out.</summary>
        void AddBest(List<FullTas.Stop> stops)
        {
            var ids = new string[stops.Count];
            _goals = new List<FullTas.Stop>();
            for (int i = 0; i < stops.Count; i++)
            {
                FullTas.Stop s = stops[i];
                ids[i] = s.Goal ? s.Id : RestartId(s.X, s.Y);
                AddPlace(ids[i], s.X, s.Y, s.Caps);
                if (s.Goal) _goals.Add(s);
            }
            void Split(int from, int to)
            {
                long ticks = stops[to].Arrive - stops[from].Tick;
                if (ticks >= 0) Put(new Move(ids[from], ids[to], (int)ticks, "best", false), stops[from].Caps);
            }
            int lastGoal = -1;
            for (int i = 0; i < stops.Count; i++)
            {
                if (i > 0) Split(i - 1, i);
                if (!stops[i].Goal) continue;
                if (lastGoal >= 0 && lastGoal != i - 1) Split(lastGoal, i);
                lastGoal = i;
            }
            for (int i = 0; i < stops.Count; i++)
            {
                int gate = NextGoal(stops, i), finish = gate < 0 ? -1 : NextGoal(stops, gate);
                if (finish < 0 || !stops[gate].Id.StartsWith("start:", StringComparison.Ordinal) || stops[finish].Id != "finish:" + stops[gate].Id.Substring(6)) continue;
                Put(new Move(ids[i], "lap:" + stops[gate].Id.Substring(6), (int)(stops[finish].Tick - stops[i].Tick), "best", false), stops[i].Caps);
            }
        }

        static int NextGoal(List<FullTas.Stop> stops, int i)
        {
            for (int j = i + 1; j < stops.Count; j++)
                if (stops[j].Goal) return j;
            return -1;
        }

        /// <summary>The buy table's measured entries (fulltas/&lt;run&gt;.buytable.json, FullTasBuyTable.cs): a move from the place
        /// to the box set, per entry; returns them with their unlocks.</summary>
        internal List<(Move move, string caps)> AddBuyTable(string file)
        {
            var added = new List<(Move, string)>();
            if (!File.Exists(file)) return added;
            JsonNode table = JsonNode.Parse(File.ReadAllText(file));
            var ids = new Dictionary<string, string>();
            static string Strip(string name, string suffix) => name.EndsWith(suffix, StringComparison.Ordinal) ? name.Substring(0, name.Length - suffix.Length) : name;
            foreach (JsonNode p in table["places"].AsArray())
            {
                string kind = p["kind"].GetValue<string>(), name = p["name"].GetValue<string>();
                double x = p["x"].GetValue<double>(), y = p["y"].GetValue<double>();
                string id = kind switch
                {
                    "box" => p["box"].GetValue<string>(),
                    "gate" => Strip(name, " gate"),
                    "finish" => Strip(name, " (the finish)"),
                    "start" => Strip(name, " (the phase's start)"),
                    "restart" => RestartId(x, y),
                    _ => null,
                };
                if (id == null) continue;
                ids[p["id"].GetValue<string>()] = id;
                AddPlace(id, x, y, p["caps"].GetValue<string>());
            }
            foreach (JsonNode e in table["entries"].AsArray())
            {
                string technique = e["technique"].GetValue<string>();
                if (technique == "miss" && ids.TryGetValue(e["place"].GetValue<string>(), out string missed))
                    _misses.Add((missed, string.Join(SetJoin, e["boxes"].AsArray().Select(b => b.GetValue<string>())), e["caps"].GetValue<string>()));
                if (technique != "walk" && technique != "jump-through" && technique != "dash-through" || e["ticks"] == null) continue;
                if (!ids.TryGetValue(e["place"].GetValue<string>(), out string from)) continue;
                var m = new Move(from, string.Join(SetJoin, e["boxes"].AsArray().Select(b => b.GetValue<string>())), e["ticks"].GetValue<int>(), "buytable", false);
                string caps = e["caps"].GetValue<string>();
                _caps.Add(caps);
                Put(m, caps);
                added.Add((m, caps));
            }
            return added;
        }

        /// <summary>Saved course laps (<see cref="SavedLaps"/>) for every unlock set the best or the buy table stood with: a farm
        /// route's lap as `finish:N` to `lap:N`, a record as `start:N` to `record:N` (its own key: the best's run of the course doesn't
        /// hide it, the planner's record laps take its time); the one SavedLaps derives from the other (±0.4 s) is marked
        /// Estimated. A course with no saved route these unlocks cover gets the fastest record of any category as its gate to
        /// finish run, estimated: more unlocks only make a course faster, so it is a lower bound, as a walk is.</summary>
        void AddSavedLaps(string routesFile)
        {
            var anyCategory = SavedLaps(routesFile, Categories.All.Select(d => d.Label).ToArray());
            foreach (string id in _caps.OrderBy(c => c, StringComparer.Ordinal).ToList())
            {
                if (!Caps.TryParse(id, out Caps have)) continue;
                string[] cats = Categories.All.Where(d => d.Caps.IsSubsetOf(have)).Select(d => d.Label).ToArray();
                var covered = SavedLaps(routesFile, cats);
                foreach (var (course, (game, farm, source)) in covered)
                {
                    bool[] farms = source.Split(", ").Select(n => RouteName.Match(n).Groups[3].Success).ToArray();
                    Put(new Move("finish:" + course, "lap:" + course, (int)Math.Round(farm / TickSeconds), "farm", !farms.Contains(true)), id);
                    Put(new Move("start:" + course, "record:" + course, (int)Math.Round(game / TickSeconds), "record", !farms.Contains(false)), id);
                }
                foreach (var (course, (game, _, _)) in anyCategory)
                    if (!covered.ContainsKey(course)) Put(new Move("start:" + course, "finish:" + course, (int)Math.Round(game / TickSeconds), "record", true), id);
            }
        }

        /// <summary>The Tree's levels whose boxes change the world, each standing for the levels up to it (tools/tree-transitions.py's
        /// LEVELS; README ### Rules 21).</summary>
        static readonly int[] TreeLevels = { 0, 1, 2, 3, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 19, 20, 22, 27 };

        /// <summary>A tree<paramref name="level"/>to<paramref name="course"/> route plays at tree level <paramref name="at"/>: its level's
        /// group (the first listed level at or over it), or for Courses 7 and 9 level 0 at levels 1 and 2 (they play as 0).</summary>
        internal static bool TreeLevelPlays(int level, int course, int at)
        {
            return TreeGroup(at) == level || (course == 7 || course == 9) && level == 0 && at <= 2;
        }

        /// <summary>The listed level tree level <paramref name="at"/> plays as (the world is the same across a group).</summary>
        internal static int TreeGroup(int at) => TreeLevels.FirstOrDefault(l => l >= at, TreeLevels[^1]);

        /// <summary>Every saved transition (<see cref="AddSavedTransitions"/>) with its tree level; only those <see cref="AtTreeLevel"/>
        /// plays are in the table.</summary>
        readonly List<(int level, int course, Move move, string caps)> _transitions = new List<(int, int, Move, string)>();
        int _treeGroup = -1;

        /// <summary>Only the tree transitions that play at tree level <paramref name="at"/> in the table (README ### Rules 21): a state's
        /// own level decides (EconomyPlan's Options), as a plan that buys tree boxes moves it on.</summary>
        internal void AtTreeLevel(int at)
        {
            if (TreeGroup(at) == _treeGroup) return;
            _treeGroup = TreeGroup(at);
            foreach (var key in _moves.Where(kv => kv.Value.Source == "transition").Select(kv => kv.Key).ToList())
            {
                _moves.Remove(key);
                _targets[(key.from, key.caps)].Remove(key.to);
            }
            _chains.Clear();
            foreach (var (level, course, move, caps) in _transitions)
                if (TreeLevelPlays(level, course, at)) Put(move, caps);
        }

        static readonly Regex TransitionName = new Regex(@"^tree\d+to(\d+)_(" + Caps.LabelPattern + @") (\d+(?:\.\d+)?)$");

        /// <summary>
        /// The saved tree transitions (`tree0to7_WJ_DS_DJ_BS 2.66`, courses.json: from bonus 11's checkpoint by the atom room to Course
        /// C's start checkpoint) as laps from that checkpoint's quick-restart landing: a restart, the transition (its name's time, physics
        /// ticks), a restart onto the course's reset point and its fastest saved standing route (input ticks), estimated. Courses 7-9
        /// are reached so (a tree box's quick restart lands at bonus 11's checkpoint); walked from another course's finish the plan
        /// priced them as a grid walk realize can't play (Zip%'s first Course 7 lap from Course 6, 12 minutes of search for none).
        /// </summary>
        void AddSavedTransitions(string routesFile)
        {
            string coursesFile = Path.Combine(Path.GetDirectoryName(routesFile), "courses.json");
            if (!File.Exists(routesFile) || !File.Exists(coursesFile)) return;
            using JsonDocument courses = JsonDocument.Parse(File.ReadAllText(coursesFile));
            using JsonDocument routes = JsonDocument.Parse(File.ReadAllText(routesFile));
            var standing = new Dictionary<(int course, string caps), int>();
            var transitions = new List<(string name, int course, Caps caps, int ticks)>();
            foreach (JsonElement m in routes.RootElement.GetProperty("macros").EnumerateArray())
            {
                string name = m.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                Match t = TransitionName.Match(name), r = RouteName.Match(name);
                if (t.Success && Caps.TryParse(t.Groups[2].Value, out Caps tc))
                    transitions.Add((name.Substring(0, name.IndexOf('_')), int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture), tc,
                        (int)Math.Round(double.Parse(t.Groups[3].Value, CultureInfo.InvariantCulture) / TickSeconds)));
                else if (r.Success && !r.Groups[3].Success && !r.Groups[5].Success && Caps.TryParse(r.Groups[2].Value, out Caps rc))
                {
                    int ticks = m.GetProperty("steps").EnumerateArray().Sum(st => Math.Max(1, st.TryGetProperty("durationTicks", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 1));
                    var key = (int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture), rc.Id);
                    if (!standing.TryGetValue(key, out int was) || ticks < was) standing[key] = ticks;
                }
            }
            foreach (var (name, course, caps, ticks) in transitions)
            {
                // Kept with its tree level (README ### Rules 21): its route is exact inputs over that tree, and a grown tree cuts it
                // (Zip% at level 7: tree0to7 and tree0to9 priced Courses 7 and 9's laps realize couldn't play); AtTreeLevel picks.
                int level = int.Parse(name.Substring(4, name.IndexOf("to", StringComparison.Ordinal) - 4), CultureInfo.InvariantCulture);
                JsonElement? from = courses.RootElement.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(c => c.Value.TryGetProperty("name", out JsonElement cn)
                    && cn.ValueKind == JsonValueKind.String && cn.GetString() == name);
                if (from == null) continue;
                string landing = RestartId(from.Value.GetProperty("x").GetDouble(), from.Value.GetProperty("y").GetDouble());
                AddPlace(landing, from.Value.GetProperty("x").GetDouble(), from.Value.GetProperty("y").GetDouble(), caps.Id);
                foreach (string id in _caps.ToList())
                {
                    if (!Caps.TryParse(id, out Caps have) || !caps.IsSubsetOf(have)) continue;
                    int run = standing.Where(kv => kv.Key.course == course && Caps.TryParse(kv.Key.caps, out Caps rc) && rc.IsSubsetOf(have))
                        .Select(kv => kv.Value).DefaultIfEmpty(-1).Min();
                    if (run > 0) _transitions.Add((level, course, new Move(landing, "lap:" + course, 1 + ticks + 1 + run, "transition", true), id));
                }
            }
        }

        internal static readonly Regex RouteName = new Regex(@"^(\d+)_(" + Caps.LabelPattern + @") (farm )?(\d+(?:\.\d+)?)( stale)?$");

        /// <summary>
        /// Lap times per course from the saved routes (routes.json in the config folder): the record (the game timer, the clones'
        /// path) and the farm route (a lap of the course's steady loop, finish to finish in live time, README ### Rules 30; a stale
        /// one from before farms were loops: one lap from the quick restart), fastest in any category up to `caps`, non-stale first.
        /// </summary>
        internal static Dictionary<int, (double game, double farm, string source)> SavedLaps(string routesFile, string[] caps)
        {
            var result = new Dictionary<int, (double, double, string)>();
            if (!File.Exists(routesFile)) return result;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(routesFile));
            var found = new Dictionary<(int, bool), (double time, bool stale, string name)>();
            foreach (JsonElement m in doc.RootElement.GetProperty("macros").EnumerateArray())
            {
                string name = m.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                Match r = name == null ? null : RouteName.Match(name);
                if (r == null || !r.Success || !Categories.IsCategory(r.Groups[2].Value) || !caps.Contains(r.Groups[2].Value)) continue;
                int course = int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                bool farm = r.Groups[3].Success, stale = r.Groups[5].Success;
                double time = double.Parse(r.Groups[4].Value, CultureInfo.InvariantCulture);
                var key = (course, farm);
                if (!found.TryGetValue(key, out var old) || (old.stale && !stale) || (old.stale == stale && time < old.time)) found[key] = (time, stale, name);
            }
            foreach (int course in found.Keys.Select(k => k.Item1).Distinct())
            {
                bool hasGame = found.TryGetValue((course, false), out var g), hasFarm = found.TryGetValue((course, true), out var f);
                double game = hasGame ? g.time : f.time - 0.4, farm = hasFarm ? f.time : g.time + 0.4;
                result[course] = (game, farm, string.Join(", ", new[] { hasGame ? g.name : null, hasFarm ? f.name : null }.Where(x => x != null)));
            }
            return result;
        }
    }

    sealed partial class FullTas
    {
        /// <summary>The Tree's level at the best's last goal: the tree boxes it bought (each box once; README ### Rules 21's level).</summary>
        internal int TreeLevel(Route best) => _labels.Take(best.Progress).Where(l => l.StartsWith("buy:buytree", StringComparison.Ordinal)).Distinct().Count();

        internal string RoutesFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "..", "routes.json");
        internal string BuyTableFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "fulltas", _name + ".buytable.json");
        internal string MovesFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "fulltas", _name + ".moves.json");

        /// <summary>The movement table's walk (MoveTable.Walk): the engine's DistanceField of this run's simulation (the geometry
        /// as it stands when asked), the run's avoid boxes for the target place's goal as walls (forced-paths.json, as `--auto`'s
        /// lower bound sets them), from each place to the target, in world units.</summary>
        internal Func<Place, IReadOnlyList<Place>, double[]> WalkField()
        {
            bool logged = false;
            return (to, from) =>
            {
                DistanceField field = DistanceField.For(_eval);
                field.SetAvoid(Solve.FullTasPaths(_name, new[] { to.Id }, _labels).Avoid);
                CapturePos At(Place p) => _eval.ToCapture(new WorldPos((float)p.X, (float)p.Y));
                float[] d = field.To(At(to), from.Select(At).ToList());
                if (!logged) { logged = true; Log($"moves: walkable distances on a {field.LastCell:0.#} u grid ({from.Count} places)"); }
                return d.Select(x => (double)x).ToArray();
            };
        }

        /// <summary>Where the best stood: the run's start, each goal it reached and each quick restart's landing, with the unlocks it
        /// held there. Tick: where the next move starts, the goal's physics tick, or a landing's: the count when the reset input is
        /// played (it takes no physics tick: the buy table's press and land are one tick), X, Y one tick later. Arrive: where a move
        /// into it ends, the tick itself but at an economy buy (FullTasGoal.IsEconomy, a `buy:`), where it is the first physics
        /// step after the stop before at which the player's touch of each of the goal's boxes could buy (the box's trigger stay,
        /// Simulation.BoxTouched, with the box's cooldown over: only Cash could stop it).</summary>
        internal readonly record struct Stop(string Id, bool Goal, long Tick, long Arrive, float X, float Y, string Caps);

        /// <summary>The best replayed from the start (Profile): its stops in tick order, and the top path speed per unlock set (the
        /// best's path, and the speed probes `--auto` cached for this run, FullTasAuto.cs MeasureSpeeds).</summary>
        internal (List<Stop> stops, Dictionary<string, double> speeds) BestStops(Route best)
        {
            // Per economy buy goal (its index), per box: the first trigger stay after the goal before that could buy.
            var arrivals = new Dictionary<int, Dictionary<int, long>>();
            Action<upgradeBox, UnityEngine.Collider2D> was = _eval.BoxTouched;
            _eval.BoxTouched = (box, collision) =>
            {
                was?.Invoke(box, collision);
                int next = _eval.Progress;
                if (next >= _goals || !IsArrivalGoal(_labels[next]) || !(Shop.Cooldown(box) <= 0f)) return;
                string[] boxes = _runGoals.Goals[next].boxes ?? Array.Empty<string>();
                for (int i = 0; i < boxes.Length; i++)
                {
                    if (!_eval.Shop.Named(new[] { boxes[i] }, box)) continue;
                    if (!arrivals.TryGetValue(next, out var first)) arrivals[next] = first = new Dictionary<int, long>();
                    first.TryAdd(i, _eval.Engine.PhysicsTicks);
                }
            };
            List<Sample> s;
            try { s = Profile(best.Inputs, false); }
            finally { _eval.BoxTouched = was; }
            LoadSpeeds();
            Sustained(s, "the best's path");
            var stops = new List<Stop> { new Stop("start", true, 0, 0, _startAt.x, _startAt.y, s.Count > 0 ? s[0].Caps : null) };
            int j = 0;
            for (int k = 1; k <= best.Progress; k++)
            {
                while (j < s.Count && s[j].Progress < k) j++;
                if (j == s.Count) break;
                // Where it stood on the goal's own tick, the best's (a replay that counts the goal on another tick says so).
                long tick = best.GoalPhysics[k - 1];
                int at = j;
                while (at + 1 < s.Count && s[at].Tick < tick) at++;
                if (s[j].Tick != tick)
                    Log($"moves: goal {k} {_labels[k - 1]} reached on tick {s[j].Tick} replayed, {tick} in the best: its place is the best's tick's");
                long arrive = tick;
                if (arrivals.TryGetValue(k - 1, out var first) && first.Count == _runGoals.Goals[k - 1].boxes.Length)
                    arrive = Math.Min(tick, first.Values.Max());
                stops.Add(new Stop(_labels[k - 1], true, tick, arrive, s[at].X, s[at].Y, s[at].Caps));
            }
            int early = stops.Count(t => t.Arrive < t.Tick), economy = stops.Count(t => t.Goal && IsArrivalGoal(t.Id));
            Log($"moves: {early} of {economy} economy buys arrive before their buy ({stops.Sum(t => t.Tick - t.Arrive)} ticks of waiting at a box left to the economy)");
            long end = best.Progress == 0 ? 0 : best.GoalPhysics[best.Progress - 1];
            j = 0;
            for (int i = 0; i < best.Inputs.Count; i++)
            {
                if (!best.Inputs[i].reset) continue;
                while (j < s.Count && s[j].Input <= i) j++;
                if (j == s.Count || s[j].Tick >= end) break;
                long press = j > 0 ? s[j - 1].Tick : 0;
                stops.Add(new Stop(null, false, press, press, s[j].X, s[j].Y, s[j].Caps));
            }
            return (stops.OrderBy(t => t.Tick).ThenBy(t => t.Goal ? 0 : 1).ToList(), _vtop.ToDictionary(v => v.Key, v => (double)v.Value.speed));
        }

        /// <summary>Every course's start gate (`start:N`) and end gate (`finish:N`, one that finishes the course) in the world: its box's
        /// centre, world frame. The movement table's places for the courses the best never ran, so laps there are offered (Area 2's
        /// Courses 6-10 after an arrival).</summary>
        internal List<(string id, WorldPos at)> CourseGates()
        {
            var gates = new List<(string, WorldPos)>();
            void Add(UnityEngine.MonoBehaviour gate, string kind)
            {
                courseScript course = gate.GetComponentInParent<courseScript>();
                UnityEngine.BoxCollider2D box = gate.GetComponent<UnityEngine.BoxCollider2D>();
                if (course == null || course.courseNumber <= 0 || box == null) return;
                UnityEngine.Vector3 p = gate.transform.position;
                UnityEngine.Rect r = DistanceField.GateRect(box, p.x, p.y, gate.transform.lossyScale);
                gates.Add((kind + course.courseNumber, _eval.ToWorld(new ScenePos(r.x + r.width / 2f, r.y + r.height / 2f))));
            }
            foreach (startGate g in _eval.World.All<startGate>()) Add(g, "start:");
            foreach (endGate g in _eval.World.All<endGate>())
                if ((bool)DistanceField.EndOfCourse.GetValue(g)) Add(g, "finish:");
            return gates;
        }

        /// <summary>A goal whose move ends at the player's arrival (Stop.Arrive): an economy buy.</summary>
        static bool IsArrivalGoal(string label) => label.StartsWith("buy:", StringComparison.Ordinal) && FullTasGoal.IsEconomy(label);

        /// <summary>
        /// plan-check --moves &lt;trace&gt; [--buytable FILE]: the movement table (MoveTable) of the trace's run against the best it
        /// is built from. The best's goal-to-goal splits from reward:bonus1 to the first buy:buyclone2 after it are Moves of
        /// source "best" with the fewest ticks the best took between those goals with those unlocks, an economy buy's ending at the
        /// player's arrival; a pair nothing measured comes back Estimated (measured moves chained, no longer than two legs that
        /// reach it; else the walkable distance, never under the straight line, at the unlocks' top path speed) and
        /// Targets offers it; a restart landing nothing showed from a place is neither estimated nor offered, Restarts offers the
        /// best's own; a buy table entry and the best's split of the same pair: the fewer ticks; the buy table's reward box and lap
        /// entries (fulltas-buytable --targets) read as moves to `reward:bonusN` and `lap:N`; a Measured move, an engine
        /// measurement as the best's and the buy table's are, replaces an estimate (even when slower) or a slower measurement,
        /// never a faster one, and one of -1 ticks (a wait inside its way) isn't recorded; Save then Read gives the same table,
        /// and Save keeps an older file's measured moves; every course's gates are places, a course the best never ran has an
        /// estimated lap from its gate; the planner's start (GoalState) stands on its goal's tick, a goal reached in no-input
        /// ticks and the last goal included.
        /// Prints each PASS or FAIL; exits 1 on a FAIL.
        /// </summary>
        internal static int MovesCheck(string[] args)
        {
            var run = new FullTas(args);
            run.Open();
            string dir = Path.Combine(Path.GetTempPath(), "igtap-moves-check-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
                if (!ok) fails++;
            }
            Route best = run._best;
            MoveTable table = MoveTable.Load(run, null, run.RoutesFile, best, Path.Combine(dir, "none.moves.json"));
            string[] labels = run._labels;
            string Name(int k) => k == 0 ? "start" : labels[k - 1];
            long Tick(int k) => k == 0 ? 0 : best.GoalPhysics[k - 1];
            long Arrive(int k) => table.GoalStop(k).Value.Arrive;
            // The best's own way between two goals as the table files it (an economy buy's to the arrival; null: none, the end came
            // first).
            long? Split(int k)
            {
                long t = Arrive(k) - Tick(k - 1);
                return t >= 0 ? t : null;
            }

            // The best's splits, bonus 1 to its first Course 2 clone: each goal's a Move of its own, the fewest ticks the best took
            // between those two goals with those unlocks.
            int k1 = Array.IndexOf(labels, "reward:bonus1") + 1;
            int k2 = k1 == 0 ? -1 : Array.IndexOf(labels, "buy:buyclone2", k1) + 1;
            if (k1 == 0 || k2 <= 0 || k2 > best.Progress) { Console.WriteLine("FAIL the best has no reward:bonus1 then buy:buyclone2"); return 1; }
            Console.WriteLine($"best: reward:bonus1 (goal {k1}, tick {Tick(k1)}) to buy:buyclone2 (goal {k2}, tick {Tick(k2)}): {Tick(k2) - Tick(k1)} ticks in {k2 - k1} goals");
            for (int k = k1 + 1; k <= k2; k++)
            {
                string caps = table.CapsAtGoal(k - 1);
                long fewest = Enumerable.Range(1, best.Progress).Where(g => Name(g - 1) == Name(k - 1) && Name(g) == Name(k) && table.CapsAtGoal(g - 1) == caps && Split(g) != null)
                    .Min(g => Split(g).Value);
                Move m = table.Best(Name(k - 1), Name(k), caps);
                bool arrival = IsArrivalGoal(Name(k));
                Check(m != null && m.Source == "best" && !m.Estimated && m.Ticks == fewest && (arrival || Arrive(k) == Tick(k)),
                    $"{Name(k - 1)} -> {Name(k)} [{caps}]: {Describe(m)}, the best's fewest {fewest} (this goal's {Tick(k) - Tick(k - 1)} to the "
                    + (arrival ? $"buy, {Arrive(k) - Tick(k - 1)} to the arrival)" : "goal)"));
            }
            // An economy buy the best waited for in its box: the move ends at the arrival, the wait is the economy's.
            int w = Enumerable.Range(1, best.Progress).FirstOrDefault(g => Arrive(g) < Tick(g));
            if (w == 0) Check(false, "no economy buy of the best arrives before its buy");
            else
            {
                string caps = table.CapsAtGoal(w - 1);
                long fewest = Enumerable.Range(1, best.Progress).Where(g => Name(g - 1) == Name(w - 1) && Name(g) == Name(w) && table.CapsAtGoal(g - 1) == caps && Split(g) != null)
                    .Min(g => Split(g).Value);
                Move m = table.Best(Name(w - 1), Name(w), caps);
                Check(m != null && m.Ticks == fewest && fewest <= Arrive(w) - Tick(w - 1),
                    $"goal {w} {Name(w - 1)} -> {Name(w)} [{caps}]: {Tick(w) - Tick(w - 1)} ticks to the buy, {Arrive(w) - Tick(w - 1)} to the arrival "
                    + $"(the box touched with its cooldown over on tick {Arrive(w)}, bought on {Tick(w)}); the table's {Describe(m)}, the pair's fewest {fewest}");
            }

            // A pair nothing measured and no chain of measured moves reaches: the walkable distance at the top path speed, and
            // Targets offers it.
            string from = "reward:bonus1", at = table.CapsAtGoal(k1);
            double Line(Place a, Place b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            Place here = table.Places[from];
            List<string> targets = table.Targets(from, at).ToList();
            Place there = table.Places.Values.Where(p => p.Id != from && (p.Kind == "box" || p.Kind == "at") && table.Best(from, p.Id, at)?.Estimated == true
                && table.Chain(from, p.Id, at) < 0).OrderBy(p => p.Id, StringComparer.Ordinal).First();
            Move guess = table.Best(from, there.Id, at);
            double speed = table.Speed(at);
            double walked = table.Walked(here, there);
            int expect = Math.Max((int)Math.Ceiling(walked / speed), table.Floor(from, there.Id, at));
            Check(guess.Source == "estimate" && guess.Ticks == expect && walked >= Line(here, there),
                $"{from} -> {there.Id} [{at}] unmeasured: {Describe(guess)}, walkable {walked:0} u (straight line {Line(here, there):0}) at {speed:0.00} u/tick {expect}");
            Check(targets.Contains(there.Id), $"Targets({from}) offers it, among {targets.Count} ({targets.Count(t => table.Best(from, t, at).Estimated)} estimated)");
            // Ruling W: Course 1's finish to Course 2's gate with the wall jump has no measured way or chain; its estimate is the walk
            // around the walls between them, longer than the straight line the planner took before (114 ticks).
            string c1 = "finish:1", c2 = "start:2", wj = table.Places.Values.Select(p => p.Caps).FirstOrDefault(c => c == "wj") ?? at;
            if (!table.Places.ContainsKey(c1) || !table.Places.ContainsKey(c2)) Console.WriteLine($"SKIP no {c1} or {c2} place");
            else
            {
                Move m = table.Best(c1, c2, wj);
                Place a = table.Places[c1], b = table.Places[c2];
                double around = table.Walked(a, b);
                int line = (int)Math.Ceiling(Line(a, b) / table.Speed(wj));
                Check(m != null && m.Estimated && table.Chain(c1, c2, wj) < 0 && m.Ticks == Math.Max((int)Math.Ceiling(around / table.Speed(wj)), table.Floor(c1, c2, wj)) && m.Ticks > line,
                    $"{c1} -> {c2} [{wj}] unmeasured: {Describe(m)}, walkable {around:0} u against the straight line's {Line(a, b):0} u ({line} ticks)");
            }
            // One that two measured moves chain to (a -> b -> c, a -> c unmeasured): an estimate no longer than the two legs.
            var chained = table.Places.Keys.SelectMany(a => table.Targets(a, at).Where(b => table.Places.ContainsKey(b) && table.Best(a, b, at) is Move ab && !ab.Estimated)
                .SelectMany(b => table.Targets(b, at).Where(c => c != a && table.Places.ContainsKey(c) && table.Best(b, c, at) is Move bc && !bc.Estimated && table.Best(a, c, at)?.Estimated == true)
                    .Select(c => (a, b, c)))).FirstOrDefault();
            if (chained.a == null) Check(false, $"no two measured moves chain to an unmeasured pair [{at}]");
            else
            {
                int legs = table.Best(chained.a, chained.b, at).Ticks + table.Best(chained.b, chained.c, at).Ticks;
                Move m = table.Best(chained.a, chained.c, at);
                Check(m.Estimated && m.Ticks <= legs, $"{chained.a} -> {chained.c} [{at}] unmeasured, through {chained.b} in {legs}: {Describe(m)}");
            }

            // A restart is never estimated: a landing the best never reached from this place is neither a move nor a target.
            string landing = table.Places.Values.Where(p => p.Kind == "restart" && !table.Restarts(from, at).Contains(p.Id)).Select(p => p.Id).FirstOrDefault();
            if (landing == null) Console.WriteLine("SKIP no restart landing the best didn't reach from " + from);
            else Check(table.Best(from, landing, at) == null && !targets.Contains(landing), $"{from} -> {landing} [{at}]: {Describe(table.Best(from, landing, at))}, not a target");
            int lapFinish = Enumerable.Range(1, best.Progress).FirstOrDefault(g => labels[g - 1].StartsWith("finish:", StringComparison.Ordinal)
                && table.Restarts(labels[g - 1], table.CapsAtGoal(g)).Any());
            if (lapFinish == 0) Console.WriteLine("SKIP the best never restarts after a finish");
            else
            {
                string fin = labels[lapFinish - 1], finCaps = table.CapsAtGoal(lapFinish);
                Check(table.Restarts(fin, finCaps).All(r => table.Best(fin, r, finCaps)?.Source == "best"),
                    $"Restarts({fin}) [{finCaps}]: " + string.Join(", ", table.Restarts(fin, finCaps).Select(r => r + " " + Describe(table.Best(fin, r, finCaps)))));
            }

            // The buy table and the best's split of the same pair: the fewer ticks of the two.
            string buyTable = Solve.Option(args, "--buytable", run.BuyTableFile);
            if (File.Exists(buyTable))
            {
                var bests = Enumerable.Range(1, best.Progress).Where(g => Split(g) != null)
                    .GroupBy(g => (from: Name(g - 1), to: Name(g), caps: table.CapsAtGoal(g - 1))).ToDictionary(g => g.Key, g => g.Min(k => Split(k).Value));
                List<(Move move, string caps)> entries = table.AddBuyTable(buyTable);
                Console.WriteLine($"buy table {buyTable}: {entries.Count} measured entries");
                var shared = entries.GroupBy(e => (from: e.move.From, to: e.move.To, e.caps)).Where(g => bests.ContainsKey(g.Key))
                    .Select(g => (key: g.Key, table: (long)g.Min(e => e.move.Ticks), best: bests[g.Key])).ToList();
                // Its reward box and lap entries (--targets) are read under the table's own ids, as moves Best weighs.
                foreach (string kind in new[] { "reward:", "lap:" })
                {
                    var e = entries.FirstOrDefault(e => e.move.To.StartsWith(kind, StringComparison.Ordinal));
                    Move m = e.move == null ? null : table.Best(e.move.From, e.move.To, e.caps);
                    Check(m != null && m.Ticks <= e.move.Ticks && !m.Estimated && table.Targets(e.move.From, e.caps).Contains(e.move.To),
                        e.move == null ? $"the buy table has no measured {kind} entry" : $"buy table {e.move.From} -> {e.move.To} [{e.caps}] {e.move.Ticks}: {Describe(m)}");
                }
                if (shared.Count == 0) Console.WriteLine("SKIP no buy table entry shares a pair with the best's splits");
                foreach (var (key, fromTable, fromBest) in shared.Take(4))
                {
                    Move m = table.Best(key.from, key.to, key.caps);
                    Check(m != null && m.Ticks == Math.Min(fromTable, fromBest) && m.Source == (fromTable < fromBest ? "buytable" : "best"),
                        $"{key.from} -> {key.to} [{key.caps}] in the buy table ({fromTable}) and the best ({fromBest}): {Describe(m)}");
                }

                // A pair the buy table's search missed: no estimate with its unlocks; with more, only a chain of measured moves.
                bool Subset(string few, string many) => Caps.TryParse(few, out Caps a) && Caps.TryParse(many, out Caps b) && a.IsSubsetOf(b);
                string[] capsSeen = table.Places.Values.Select(p => p.Caps).Where(c => c != null).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToArray();
                var misses = table.Misses.Where(x => !x.to.Contains(MoveTable.SetJoin, StringComparison.Ordinal) && table.Best(x.from, x.to, x.caps)?.Estimated != false)
                    .OrderBy(x => x.from == "finish:1" && x.to == "reward:bonus1" ? 0 : 1).ThenBy(x => x.from, StringComparer.Ordinal).ThenBy(x => x.to, StringComparer.Ordinal).ToList();
                if (misses.Count == 0) Console.WriteLine("SKIP the buy table has no miss");
                else
                {
                    var x = misses[0];
                    Check(table.Best(x.from, x.to, x.caps) == null && !table.Targets(x.from, x.caps).Contains(x.to),
                        $"{x.from} -> {x.to} [{x.caps}] the buy table's search missed: {Describe(table.Best(x.from, x.to, x.caps))}, not a target");
                    var more = misses.SelectMany(y => capsSeen.Where(c => c != y.caps && Subset(y.caps, c) && table.Best(y.from, y.to, c)?.Estimated != false).Select(c => (y, c))).FirstOrDefault();
                    if (more.c == null) Console.WriteLine("SKIP no miss with more unlocks than it was searched with");
                    else
                    {
                        Move m = table.Best(more.y.from, more.y.to, more.c);
                        int chain = table.Chain(more.y.from, more.y.to, more.c);
                        Check(m == null ? chain < 0 : m.Ticks == chain, $"{more.y.from} -> {more.y.to} [{more.c}], missed with [{more.y.caps}]: {Describe(m)}, the chain of measured moves {chain} (no straight line)");
                    }
                }
                // An estimate never under the fewest measured ticks to there from a spot of the same place.
                var near = table.Places.Values.SelectMany(a => table.Places.Values.Where(b => b.Id != a.Id && Math.Abs(a.X - b.X) <= MoveTable.PlaceReach && Math.Abs(a.Y - b.Y) <= MoveTable.PlaceReach)
                    .SelectMany(b => capsSeen.SelectMany(c => table.Targets(b.Id, c).Where(t => table.Places.ContainsKey(t) && t != a.Id && table.Best(b.Id, t, c) is Move bt && !bt.Estimated
                        && table.Best(a.Id, t, c)?.Estimated == true && table.Chain(a.Id, t, c) < 0).Select(t => (a: a.Id, b: b.Id, t, c)))))
                    .FirstOrDefault();
                if (near.a == null)
                {
                    // None on this table: a made-up one (two boxes 20 u apart, one with a measured 500-tick move to a third 5000 u off).
                    string spots = Path.Combine(dir, "spots.json");
                    File.WriteAllText(spots, "{\"places\":[{\"id\":\"buy:a\",\"kind\":\"box\",\"box\":\"buy:a\",\"x\":0,\"y\":0,\"caps\":\"wj\"},"
                        + "{\"id\":\"buy:b\",\"kind\":\"box\",\"box\":\"buy:b\",\"x\":20,\"y\":0,\"caps\":\"wj\"},{\"id\":\"buy:t\",\"kind\":\"box\",\"box\":\"buy:t\",\"x\":5000,\"y\":0,\"caps\":\"wj\"}],"
                        + "\"speeds\":{\"wj\":100},\"moves\":[{\"from\":\"buy:b\",\"to\":\"buy:t\",\"caps\":\"wj\",\"ticks\":500,\"source\":\"buytable\",\"estimated\":false}]}");
                    Move m = MoveTable.Read(spots).Best("buy:a", "buy:t", "wj");
                    Check(m != null && m.Estimated && m.Ticks == 500, $"buy:a -> buy:t [wj] unmeasured (straight line 50), buy:b 20 u off measured 500: {Describe(m)}");
                }
                else
                {
                    Move m = table.Best(near.a, near.t, near.c), spot = table.Best(near.b, near.t, near.c);
                    Check(m.Ticks >= spot.Ticks, $"{near.a} -> {near.t} [{near.c}] unmeasured, {near.b} at the same spot measured {spot.Ticks}: {Describe(m)}");
                }
            }
            else Console.WriteLine($"SKIP no buy table at {buyTable}");

            // Saved course laps (routes.json).
            foreach (string caps in table.Places.Values.Select(p => p.Caps).Where(c => c != null).Distinct().OrderBy(c => c, StringComparer.Ordinal))
                foreach (string to in table.Targets("finish:1", caps).Concat(table.Targets("finish:2", caps)).Concat(table.Targets("start:1", caps)).Concat(table.Targets("start:2", caps)).Distinct())
                {
                    if (!to.StartsWith("lap:", StringComparison.Ordinal) && !to.StartsWith("finish:", StringComparison.Ordinal) && !to.StartsWith("record:", StringComparison.Ordinal)) continue;
                    string start = to.StartsWith("lap:", StringComparison.Ordinal) ? "finish:" + to.Substring(4) : "start:" + to.Substring(to.IndexOf(':') + 1);
                    Move lap = table.Best(start, to, caps);
                    if (lap != null && !lap.Estimated) Console.WriteLine($"lap [{caps}] {start} -> {to}: {Describe(lap)}");
                }

            // Every course's gates are places, so a course the best never ran has a lap from its gate (its run a walk estimate).
            string lastCaps = table.CapsAtGoal(best.Progress);
            foreach (var (id, _) in run.CourseGates())
            {
                Check(table.Places.ContainsKey(id), $"course place {id}");
                string n = id.Substring(id.IndexOf(':') + 1);
                if (!id.StartsWith("start:", StringComparison.Ordinal) || labels.Contains(id)) continue;
                Check(table.Targets(id, lastCaps).Contains("lap:" + n) && table.Best(id, "lap:" + n, lastCaps)?.Estimated == true,
                    $"lap:{n}, a course the best never ran, from its gate [{lastCaps}]: {Describe(table.Best(id, "lap:" + n, lastCaps))}");
            }

            // The planner's start (GoalState) stands on the tick its goal is reached, a goal reached in ticks that take no input too
            // (one that shares its input tick with the goal before: TouchGrass-ANY%'s 478-479, in a 269-tick fall), the last goal
            // included (it doesn't end the run there).
            int noInput = Enumerable.Range(2, Math.Max(0, best.Progress - 1)).FirstOrDefault(k => best.GoalInput[k - 1] == best.GoalInput[k - 2]);
            foreach (int k in new[] { noInput, best.Progress }.Where(k => k > 0).Distinct())
            {
                var (state, route) = run.GoalState(k);
                run.Read(state);
                Check(state.Goal.Next == k && run._eval.Engine.PhysicsTicks == best.GoalPhysics[k - 1],
                    $"GoalState({k}) {labels[k - 1]}: goal {state.Goal.Next} on tick {run._eval.Engine.PhysicsTicks} (the best's {best.GoalPhysics[k - 1]}), {route.Count} inputs (its input tick {best.GoalInput[k - 1]})");
            }

            // Realize's measurement, an engine measurement as the best's split is: the fewest ticks stand, so a slower one never
            // replaces a faster one and a faster one does; it replaces an estimate even when slower; one whose ticks hold a wait
            // (-1) isn't recorded.
            string next = Name(k1 + 1), capsK1 = table.CapsAtGoal(k1);
            Move before = table.Best(from, next, capsK1);
            table.Measured(new Move(from, next, before.Ticks + 7, "measured", false), capsK1);
            Move after = table.Best(from, next, capsK1);
            Check(after == before, $"Measured {from} -> {next} [{capsK1}] {before.Ticks + 7}, slower than {Describe(before)}: {Describe(after)}");
            var measured = new Move(from, next, before.Ticks - 3, "measured", false);
            table.Measured(measured, capsK1);
            after = table.Best(from, next, capsK1);
            Check(after == measured, $"Measured {from} -> {next} [{capsK1}] {measured.Ticks}, faster than {Describe(before)}: {Describe(after)}");
            table.Measured(new Move(from, next, measured.Ticks + 1, "measured", false), capsK1);
            Check(table.Best(from, next, capsK1) == measured, $"Measured again {measured.Ticks + 1}, slower than the last: {Describe(table.Best(from, next, capsK1))}");
            table.Measured(new Move(from, next, -1, "measured", false), capsK1);
            Check(table.Best(from, next, capsK1) == measured, $"Measured -1 (a wait inside the way): not recorded, {Describe(table.Best(from, next, capsK1))}");
            var walkedOver = new Move(from, there.Id, guess.Ticks + 50, "measured", false);
            table.Measured(walkedOver, at);
            Check(table.Best(from, there.Id, at) == walkedOver, $"Measured {from} -> {there.Id} [{at}] {walkedOver.Ticks}, over its estimate {Describe(guess)}: {Describe(table.Best(from, there.Id, at))}");

            // Save and Read round-trip; a later Save keeps the file's measured moves (merged across runs).
            string file = Path.Combine(dir, "moves.json");
            table.Save(file);
            MoveTable read = MoveTable.Read(file);
            Check(read.Dump() == table.Dump(), $"Save then Read: {table.Count} moves, {table.Places.Count} places alike");
            var other = new MoveTable();
            var later = new Move("start", "reward:bonus1", 1234, "measured", false);
            other.Measured(later, "wj");
            other.Save(file);
            MoveTable merged = MoveTable.Read(file);
            Check(merged.Best(from, next, capsK1) == measured && merged.Best("start", "reward:bonus1", "wj") == later,
                "a second Save into the same file keeps the first's measured move beside its own");
            Directory.Delete(dir, true);
            Console.WriteLine(fails == 0 ? "moves: PASS" : $"moves: {fails} FAIL");
            return fails == 0 ? 0 : 1;
        }

        static string Describe(Move m) => m == null ? "none" : $"{m.Ticks} ticks ({m.Source}{(m.Estimated ? ", estimated" : "")})";
    }
}
