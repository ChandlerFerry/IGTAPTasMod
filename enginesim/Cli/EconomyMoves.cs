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
    /// the walk's. Sources, by trust: realize's measurements (<see cref="Measured"/>, the latest per pair); the buy table's entries
    /// (walk, jump-through, dash-through) and the best's own splits (goal to goal and through each quick restart's landing, and a
    /// lap `lap:N` where the best plays a gate then its finish), both engine measurements: the fewer ticks of the two; the saved
    /// course routes (routes.json): a farm route as a lap from the course's finish, a record as `record:N` from its gate. A pair
    /// none of them has is estimated, and marked so: the straight line at the unlocks' top path speed (FullTasAuto's, the best's
    /// path and the cached speed probes), a box set as its boxes one after another, a lap or record as the walk to its gate and
    /// the course's known run. A quick restart is never estimated: where it lands is the checkpoint the run last touched
    /// (README ### Rules 35-36), which the table doesn't follow, so <see cref="Restarts"/> offers only the landings a restart from
    /// that place was seen to reach. The planner expands a place through <see cref="Targets"/> (estimates included) and
    /// <see cref="Restarts"/>. Saved as fulltas/&lt;run&gt;.moves.json; a Save keeps the measured moves the file had, and Load reads
    /// them back.
    /// </summary>
    sealed class MoveTable
    {
        static readonly Dictionary<string, int> Trust = new Dictionary<string, int>
        {
            ["measured"] = 0, ["buytable"] = 1, ["best"] = 1, ["farm"] = 2, ["record"] = 3,
        };
        /// <summary>One place per spot within this reach (the buy table's PieceReach).</summary>
        const float PlaceReach = 48f;
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
        /// <summary>The best's goal stops (0: the run's start), from the replay Load built the table on.</summary>
        List<FullTas.Stop> _goals;

        public IReadOnlyDictionary<string, Place> Places => _places;
        public int Count => _moves.Count;

        public static MoveTable Load(FullTas run, string buyTableFile, string routesFile, FullTas.Route best, string movesFile = null)
        {
            var table = new MoveTable();
            var (stops, speeds) = run.BestStops(best);
            foreach (var v in speeds) table._speeds[v.Key] = v.Value;
            table.AddBest(stops);
            if (buyTableFile != null) table.AddBuyTable(buyTableFile);
            if (routesFile != null) table.AddSavedLaps(routesFile);
            movesFile ??= run.MovesFile;
            if (File.Exists(movesFile))
                foreach (var kv in Read(movesFile)._moves)
                    if (kv.Value.Source == "measured") table.Put(kv.Value, kv.Key.caps);
            return table;
        }

        /// <summary>The most trusted move from here to there with these unlocks, else an estimate; null when neither is known.</summary>
        public Move Best(string from, string to, string caps)
        {
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

        /// <summary>Realize's real ticks for a move made with these unlocks: outranks every other source, and the latest measurement
        /// of a pair replaces the one before (the build as it plays now).</summary>
        public void Measured(Move m, string caps) => Put(m with { Source = "measured", Estimated = false }, caps);

        public void Save(string file)
        {
            if (File.Exists(file))
            {
                MoveTable old = Read(file);
                foreach (var kv in old._moves)
                    if (kv.Value.Source == "measured" && !(_moves.TryGetValue(kv.Key, out Move ours) && ours.Source == "measured")) Put(kv.Value, kv.Key.caps);
                foreach (Place p in old._places.Values) _places.TryAdd(p.Id, p);
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
                if (rank > wasRank || rank == wasRank && m.Source != "measured" && m.Ticks >= was.Ticks) return;
            }
            _moves[key] = m;
            if (!_targets.TryGetValue((m.From, caps), out List<string> to)) _targets[(m.From, caps)] = to = new List<string>();
            if (!to.Contains(m.To)) to.Add(m.To);
        }

        static int Rank(Move m)
        {
            int i = Trust.TryGetValue(m.Source, out int r) ? r : Trust.Count;
            return m.Estimated ? i + Trust.Count + 1 : i;
        }

        /// <summary>An estimate's ticks, -1 when it can't be made: a stored move as it is; a box set as its boxes in order; a lap or
        /// record as the walk to its gate and the course's known run; a restart landing never; else the straight line between the
        /// two places at the unlocks' top path speed.</summary>
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
                return _moves.TryGetValue((gate, "record:" + n, caps), out Move saved) ? walk + saved.Ticks : -1;
            }
            if (to.StartsWith("restart@", StringComparison.Ordinal)) return -1;
            if (!_places.TryGetValue(from, out Place a) || !_places.TryGetValue(to, out Place b) || !_speeds.TryGetValue(caps, out double v) || v <= 0) return -1;
            return (int)Math.Ceiling(Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) / v);
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
        /// Estimated.</summary>
        void AddSavedLaps(string routesFile)
        {
            foreach (string id in _caps.OrderBy(c => c, StringComparer.Ordinal).ToList())
            {
                if (!Caps.TryParse(id, out Caps have)) continue;
                string[] cats = Categories.All.Where(d => d.Caps.IsSubsetOf(have)).Select(d => d.Label).ToArray();
                foreach (var (course, (game, farm, source)) in SavedLaps(routesFile, cats))
                {
                    bool[] farms = source.Split(", ").Select(n => RouteName.Match(n).Groups[3].Success).ToArray();
                    Put(new Move("finish:" + course, "lap:" + course, (int)Math.Round(farm / TickSeconds), "farm", !farms.Contains(true)), id);
                    Put(new Move("start:" + course, "record:" + course, (int)Math.Round(game / TickSeconds), "record", !farms.Contains(false)), id);
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
        internal string RoutesFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "..", "routes.json");
        internal string BuyTableFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "fulltas", _name + ".buytable.json");
        internal string MovesFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "fulltas", _name + ".moves.json");

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

        /// <summary>A goal whose move ends at the player's arrival (Stop.Arrive): an economy buy.</summary>
        static bool IsArrivalGoal(string label) => label.StartsWith("buy:", StringComparison.Ordinal) && FullTasGoal.IsEconomy(label);

        /// <summary>
        /// plan-check --moves &lt;trace&gt; [--buytable FILE]: the movement table (MoveTable) of the trace's run against the best it
        /// is built from. The best's goal-to-goal splits from reward:bonus1 to the first buy:buyclone2 after it are Moves of
        /// source "best" with the fewest ticks the best took between those goals with those unlocks, an economy buy's ending at the
        /// player's arrival; a pair nothing measured comes back Estimated (the straight line at the unlocks' top path speed) and
        /// Targets offers it; a restart landing nothing showed from a place is neither estimated nor offered, Restarts offers the
        /// best's own; a buy table entry and the best's split of the same pair: the fewer ticks; a Measured move outranks every
        /// other source, even when slower; Save then Read gives the same table, and Save keeps an older file's measured moves.
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

            // A pair nothing measured: the straight line at the top path speed, and Targets offers it.
            string from = "reward:bonus1", at = table.CapsAtGoal(k1);
            Place here = table.Places[from];
            List<string> targets = table.Targets(from, at).ToList();
            Place there = table.Places.Values.Where(p => p.Id != from && (p.Kind == "box" || p.Kind == "at") && table.Best(from, p.Id, at)?.Estimated == true)
                .OrderBy(p => p.Id, StringComparer.Ordinal).First();
            Move guess = table.Best(from, there.Id, at);
            double speed = table.Speed(at);
            int expect = (int)Math.Ceiling(Math.Sqrt((here.X - there.X) * (here.X - there.X) + (here.Y - there.Y) * (here.Y - there.Y)) / speed);
            Check(guess.Source == "estimate" && guess.Ticks == expect,
                $"{from} -> {there.Id} [{at}] unmeasured: {Describe(guess)}, straight line at {speed:0.00} u/tick {expect}");
            Check(targets.Contains(there.Id), $"Targets({from}) offers it, among {targets.Count} ({targets.Count(t => table.Best(from, t, at).Estimated)} estimated)");

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
                if (shared.Count == 0) Console.WriteLine("SKIP no buy table entry shares a pair with the best's splits");
                foreach (var (key, fromTable, fromBest) in shared.Take(4))
                {
                    Move m = table.Best(key.from, key.to, key.caps);
                    Check(m != null && m.Ticks == Math.Min(fromTable, fromBest) && m.Source == (fromTable < fromBest ? "buytable" : "best"),
                        $"{key.from} -> {key.to} [{key.caps}] in the buy table ({fromTable}) and the best ({fromBest}): {Describe(m)}");
                }
            }
            else Console.WriteLine($"SKIP no buy table at {buyTable}");

            // Saved course laps (routes.json).
            foreach (string caps in table.Places.Values.Select(p => p.Caps).Distinct().OrderBy(c => c, StringComparer.Ordinal))
                foreach (string to in table.Targets("finish:1", caps).Concat(table.Targets("finish:2", caps)).Concat(table.Targets("start:1", caps)).Concat(table.Targets("start:2", caps)).Distinct())
                {
                    if (!to.StartsWith("lap:", StringComparison.Ordinal) && !to.StartsWith("finish:", StringComparison.Ordinal) && !to.StartsWith("record:", StringComparison.Ordinal)) continue;
                    string start = to.StartsWith("lap:", StringComparison.Ordinal) ? "finish:" + to.Substring(4) : "start:" + to.Substring(to.IndexOf(':') + 1);
                    Move lap = table.Best(start, to, caps);
                    if (lap != null && !lap.Estimated) Console.WriteLine($"lap [{caps}] {start} -> {to}: {Describe(lap)}");
                }

            // Realize's measurement outranks every other source, even a slower one.
            string next = Name(k1 + 1), capsK1 = table.CapsAtGoal(k1);
            Move before = table.Best(from, next, capsK1);
            var measured = new Move(from, next, before.Ticks + 7, "measured", false);
            table.Measured(measured, capsK1);
            Move after = table.Best(from, next, capsK1);
            Check(after == measured, $"Measured {from} -> {next} [{capsK1}] {measured.Ticks} over {Describe(before)}: {Describe(after)}");

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
