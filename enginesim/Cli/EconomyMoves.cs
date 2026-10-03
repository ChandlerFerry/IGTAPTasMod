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

namespace IGTAP.EngineSim.Cli
{
    /// <summary>A place the run stands at, as the buy table's places (FullTasBuyTable.cs): its id the goal it stands at (`start:N`
    /// a course's gate, `finish:N` its finish, `buy:`/`reward:` a box, `at:` an area goal), `start` the run's start, or
    /// `restart@x,y` a quick restart's landing (the checkpoint decides where: one id per spot, within 48 u). X, Y: the world frame
    /// (WorldPos, FloatingOrigin's rebases undone); Caps: the unlocks (Caps.Id) the run first stood there with.</summary>
    sealed record Place(string Id, string Kind, string Box, double X, double Y, string Caps);

    /// <summary>Physics ticks from one place to another. To: a place id (a buy table box set: its labels in order, joined by <see cref="MoveTable.SetJoin"/>),
    /// `lap:N`, Course N's gate then its finish (it ends at `finish:N`), or `record:N`, from Course N's gate a lap on its saved
    /// record's path (the clones' path; it ends at `finish:N`). Source: measured, buytable, best, farm, record or estimate.</summary>
    sealed record Move(string From, string To, int Ticks, string Source, bool Estimated);

    /// <summary>
    /// The economy planner's movement model (docs/fulltas.md, "Movement table"): ticks between places, per unlock set (Caps.Id),
    /// from, in this order of trust: realize's measurements (<see cref="Measured"/>); the buy table's entries (walk, jump-through,
    /// dash-through: the cheapest per box set); the best's own splits, goal to goal and through each quick restart's landing, and a
    /// lap (`lap:N`) where the best plays a gate then its finish; the saved course routes (routes.json): a farm route as a lap from
    /// the course's finish, a record as `record:N` from its gate. A pair none of them has is estimated: the straight line at the
    /// unlocks' top path speed (FullTasAuto's, the best's path and the cached speed probes), a box set as its boxes one after
    /// another, a lap as the walk to its gate and the course's known run; marked Estimated. Saved as fulltas/&lt;run&gt;.moves.json;
    /// a Save keeps the measured moves the file had, and Load reads them back.
    /// </summary>
    sealed class MoveTable
    {
        static readonly string[] Trust = { "measured", "buytable", "best", "farm", "record" };
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
        /// <summary>A quick restart's own ticks, press to landing (the fewest seen; -1: none seen).</summary>
        int _reset = -1;
        /// <summary>The best's unlocks after each of its goals (0: the run's start), from the replay Load built the table on.</summary>
        List<string> _goalCaps;

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

        /// <summary>Every To a move from here with these unlocks has (estimates aside).</summary>
        public IEnumerable<string> Targets(string from, string caps)
        {
            return _targets.TryGetValue((from, caps), out List<string> to) ? to : Enumerable.Empty<string>();
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
                if (old._reset >= 0 && (_reset < 0 || old._reset < _reset)) _reset = old._reset;
            }
            var json = new JsonObject
            {
                ["places"] = new JsonArray(_places.Values.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Id, ["kind"] = p.Kind, ["box"] = p.Box, ["x"] = p.X, ["y"] = p.Y, ["caps"] = p.Caps,
                }).ToArray()),
                ["speeds"] = new JsonObject(_speeds.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => KeyValuePair.Create(v.Key, (JsonNode)v.Value))),
                ["reset"] = _reset,
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
            table._reset = json["reset"].GetValue<int>();
            foreach (JsonNode m in json["moves"].AsArray())
                table.Put(new Move(m["from"].GetValue<string>(), m["to"].GetValue<string>(), m["ticks"].GetValue<int>(), m["source"].GetValue<string>(),
                    m["estimated"].GetValue<bool>()), m["caps"].GetValue<string>());
            return table;
        }

        /// <summary>The best's unlocks after its goal k (0: the run's start); null for a table not built on a best.</summary>
        internal string CapsAtGoal(int k) => _goalCaps != null && k >= 0 && k < _goalCaps.Count ? _goalCaps[k] : null;

        /// <summary>The top path speed with these unlocks, units per physics tick (0: unknown).</summary>
        internal double Speed(string caps) => _speeds.TryGetValue(caps, out double v) ? v : 0.0;

        /// <summary>The whole table as text, one line per place, speed and move (a round trip's comparison).</summary>
        internal string Dump()
        {
            return string.Join("\n", _places.Values.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.ToString())
                .Concat(_speeds.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + " " + v.Value.ToString("R", CultureInfo.InvariantCulture)))
                .Concat(new[] { "reset " + _reset })
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
            int i = Array.IndexOf(Trust, m.Source);
            if (i < 0) i = Trust.Length;
            return m.Estimated ? i + Trust.Length + 1 : i;
        }

        /// <summary>An estimate's ticks, -1 when it can't be made: a stored move as it is; a box set as its boxes in order; a lap as
        /// the walk to its gate and the course's known run; a restart as the fewest a restart took; else the straight line between the
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
            if (to.StartsWith("lap:", StringComparison.Ordinal))
            {
                string n = to.Substring(4);
                int walk = Guess(from, "start:" + n, caps);
                if (walk < 0) return -1;
                if (_moves.TryGetValue(("start:" + n, "finish:" + n, caps), out Move course)) return walk + course.Ticks;
                return _moves.TryGetValue(("start:" + n, "record:" + n, caps), out Move record) ? walk + record.Ticks : -1;
            }
            if (to.StartsWith("restart@", StringComparison.Ordinal)) return _reset >= 0 && _places.ContainsKey(to) ? _reset : -1;
            if (!_places.TryGetValue(from, out Place a) || !_places.TryGetValue(to, out Place b) || !_speeds.TryGetValue(caps, out double v) || v <= 0) return -1;
            return (int)Math.Ceiling(Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) / v);
        }

        // ponytail: a place is the goal it stands at, so the best's or the buy table's several spots at one box (a wide box, a gate
        // crossed high or low) share an id and the cheapest way from any of them stands for all; per-spot ids if the plan's walks
        // come out short of the build's.
        void AddPlace(string id, double x, double y, string caps)
        {
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

        /// <summary>The best's stops in tick order (FullTas.BestStops): a move between each two, a goal-to-goal split past a restart's
        /// landing too, and a lap from each stop the best walks from to a gate and then that course's finish.</summary>
        void AddBest(List<FullTas.Stop> stops)
        {
            var ids = new string[stops.Count];
            _goalCaps = new List<string>();
            for (int i = 0; i < stops.Count; i++)
            {
                FullTas.Stop s = stops[i];
                ids[i] = s.Goal ? s.Id : RestartId(s.X, s.Y);
                AddPlace(ids[i], s.X, s.Y, s.Caps);
                if (s.Goal) _goalCaps.Add(s.Caps);
                else if (s.Reset >= 0 && (_reset < 0 || s.Reset < _reset)) _reset = s.Reset;
            }
            int lastGoal = -1;
            for (int i = 0; i < stops.Count; i++)
            {
                if (i > 0) Put(new Move(ids[i - 1], ids[i], (int)(stops[i].Tick - stops[i - 1].Tick), "best", false), stops[i - 1].Caps);
                if (!stops[i].Goal) continue;
                if (lastGoal >= 0 && lastGoal != i - 1)
                    Put(new Move(ids[lastGoal], ids[i], (int)(stops[i].Tick - stops[lastGoal].Tick), "best", false), stops[lastGoal].Caps);
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
                if (p["restart"] is JsonNode r)
                {
                    int ticks = (int)(r["land"].GetValue<long>() - r["press"].GetValue<long>());
                    if (_reset < 0 || ticks < _reset) _reset = ticks;
                }
            }
            foreach (JsonNode e in table["entries"].AsArray())
            {
                string technique = e["technique"].GetValue<string>();
                if (technique != "walk" && technique != "jump-through" && technique != "dash-through" || e["ticks"] == null) continue;
                if (!ids.TryGetValue(e["place"].GetValue<string>(), out string from)) continue;
                var m = new Move(from, string.Join(SetJoin, e["boxes"].AsArray().Select(b => b.GetValue<string>())), e["ticks"].GetValue<int>(), "buytable", false);
                string caps = e["caps"].GetValue<string>();
                Put(m, caps);
                added.Add((m, caps));
            }
            return added;
        }

        /// <summary>Saved course laps (<see cref="SavedLaps"/>) for every unlock set the table has places with: a farm route's lap
        /// as `finish:N` to `lap:N`, a record as `start:N` to `record:N` (its own key: the best's run of the course doesn't hide it,
        /// the planner's record laps take its time); the one SavedLaps derives from the other (±0.4 s) is
        /// marked Estimated.</summary>
        void AddSavedLaps(string routesFile)
        {
            foreach (string id in _places.Values.Select(p => p.Caps).Where(c => c != null).Distinct().ToList())
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

        /// <summary>Where the best stood: the run's start, each goal it reached (on the goal's physics tick) and each quick restart's
        /// landing (the first physics tick after the reset input), with the unlocks it held there; Reset: a landing's ticks from the
        /// tick before it (-1 for a goal).</summary>
        internal readonly record struct Stop(string Id, bool Goal, long Tick, float X, float Y, string Caps, int Reset);

        /// <summary>The best replayed from the start (Profile): its stops in tick order, and the top path speed per unlock set (the
        /// best's path, and the speed probes `--auto` cached for this run, FullTasAuto.cs MeasureSpeeds).</summary>
        internal (List<Stop> stops, Dictionary<string, double> speeds) BestStops(Route best)
        {
            List<Sample> s = Profile(best.Inputs, false);
            LoadSpeeds();
            Sustained(s, "the best's path");
            var stops = new List<Stop> { new Stop("start", true, 0, _startAt.x, _startAt.y, s.Count > 0 ? s[0].Caps : null, -1) };
            int j = 0;
            for (int k = 1; k <= best.Progress; k++)
            {
                while (j < s.Count && s[j].Progress < k) j++;
                if (j == s.Count) break;
                // Where it stood on the goal's own tick, the best's (a replay that counts the goal on another tick says so).
                int at = j;
                while (at + 1 < s.Count && s[at].Tick < best.GoalPhysics[k - 1]) at++;
                if (s[j].Tick != best.GoalPhysics[k - 1])
                    Log($"moves: goal {k} {_labels[k - 1]} reached on tick {s[j].Tick} replayed, {best.GoalPhysics[k - 1]} in the best: its place is the best's tick's");
                stops.Add(new Stop(_labels[k - 1], true, best.GoalPhysics[k - 1], s[at].X, s[at].Y, s[at].Caps, -1));
            }
            long end = best.Progress == 0 ? 0 : best.GoalPhysics[best.Progress - 1];
            j = 0;
            for (int i = 0; i < best.Inputs.Count; i++)
            {
                if (!best.Inputs[i].reset) continue;
                while (j < s.Count && s[j].Input <= i) j++;
                if (j == s.Count || s[j].Tick >= end) break;
                stops.Add(new Stop(null, false, s[j].Tick, s[j].X, s[j].Y, s[j].Caps, (int)(s[j].Tick - (j > 0 ? s[j - 1].Tick : 0))));
            }
            return (stops.OrderBy(t => t.Tick).ThenBy(t => t.Goal ? 0 : 1).ToList(), _vtop.ToDictionary(v => v.Key, v => (double)v.Value.speed));
        }

        /// <summary>
        /// plan-check --moves &lt;trace&gt; [--buytable FILE]: the movement table (MoveTable) of the trace's run against the best it
        /// is built from. The best's goal-to-goal splits from reward:bonus1 to the first buy:buyclone2 after it are Moves of
        /// source "best" with the fewest ticks the best took between those goals with those unlocks; a pair nothing measured comes
        /// back Estimated (the straight line at the unlocks' top path speed); a buy table entry outranks the best's split of the same
        /// pair; a Measured move outranks every other source, even when slower; Save then Read gives the same table, and Save keeps
        /// an older file's measured moves. Prints each PASS or FAIL; exits 1 on a FAIL.
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
            string Stop(int k) => k == 0 ? "start" : labels[k - 1];
            long Tick(int k) => k == 0 ? 0 : best.GoalPhysics[k - 1];

            // The best's splits, bonus 1 to its first Course 2 clone: each goal's a Move of its own, the fewest ticks the best took
            // between those two goals with those unlocks.
            int k1 = Array.IndexOf(labels, "reward:bonus1") + 1;
            int k2 = k1 == 0 ? -1 : Array.IndexOf(labels, "buy:buyclone2", k1) + 1;
            if (k1 == 0 || k2 <= 0 || k2 > best.Progress) { Console.WriteLine("FAIL the best has no reward:bonus1 then buy:buyclone2"); return 1; }
            Console.WriteLine($"best: reward:bonus1 (goal {k1}, tick {Tick(k1)}) to buy:buyclone2 (goal {k2}, tick {Tick(k2)}): {Tick(k2) - Tick(k1)} ticks in {k2 - k1} goals");
            for (int k = k1 + 1; k <= k2; k++)
            {
                string caps = table.CapsAtGoal(k - 1);
                long fewest = Enumerable.Range(1, best.Progress).Where(g => Stop(g - 1) == Stop(k - 1) && Stop(g) == Stop(k) && table.CapsAtGoal(g - 1) == caps)
                    .Min(g => Tick(g) - Tick(g - 1));
                Move m = table.Best(Stop(k - 1), Stop(k), caps);
                Check(m != null && m.Source == "best" && !m.Estimated && m.Ticks == fewest,
                    $"{Stop(k - 1)} -> {Stop(k)} [{caps}]: {Describe(m)}, the best's fewest {fewest} (this goal's {Tick(k) - Tick(k - 1)})");
            }

            // A pair nothing measured: the straight line at the top path speed.
            string from = "reward:bonus1", at = table.CapsAtGoal(k1);
            Place here = table.Places[from];
            Place there = table.Places.Values.Where(p => p.Id != from && p.Kind != "restart" && table.Targets(from, at).All(t => t != p.Id))
                .OrderBy(p => p.Id, StringComparer.Ordinal).First();
            Move guess = table.Best(from, there.Id, at);
            double speed = table.Speed(at);
            int expect = (int)Math.Ceiling(Math.Sqrt((here.X - there.X) * (here.X - there.X) + (here.Y - there.Y) * (here.Y - there.Y)) / speed);
            Check(guess != null && guess.Estimated && guess.Source == "estimate" && guess.Ticks == expect,
                $"{from} -> {there.Id} [{at}] unmeasured: {Describe(guess)}, straight line at {speed:0.00} u/tick {expect}");

            // The buy table outranks the best's split of the same pair.
            string buyTable = Solve.Option(args, "--buytable", run.BuyTableFile);
            if (File.Exists(buyTable))
            {
                List<(Move move, string caps)> entries = table.AddBuyTable(buyTable);
                Console.WriteLine($"buy table {buyTable}: {entries.Count} measured entries");
                var shared = entries.GroupBy(e => (from: e.move.From, to: e.move.To, e.caps))
                    .Select(g => (key: g.Key, fewest: g.Min(e => e.move.Ticks)))
                    .FirstOrDefault(g => Enumerable.Range(1, best.Progress).Any(k => Stop(k - 1) == g.key.from && Stop(k) == g.key.to && table.CapsAtGoal(k - 1) == g.key.caps));
                if (shared.key.from == null) Console.WriteLine("SKIP no buy table entry shares a pair with the best's splits");
                else
                {
                    Move m = table.Best(shared.key.from, shared.key.to, shared.key.caps);
                    Check(m != null && m.Source == "buytable" && m.Ticks == shared.fewest,
                        $"{shared.key.from} -> {shared.key.to} [{shared.key.caps}] in the buy table and the best's splits: {Describe(m)}, the table's fewest {shared.fewest}");
                }
            }
            else Console.WriteLine($"SKIP no buy table at {buyTable}");

            // Saved course laps (routes.json).
            foreach (string caps in table.Places.Values.Select(p => p.Caps).Distinct().OrderBy(c => c, StringComparer.Ordinal))
                foreach (string to in table.Targets("finish:1", caps).Concat(table.Targets("finish:2", caps)).Concat(table.Targets("start:1", caps)).Concat(table.Targets("start:2", caps)))
                {
                    if (!to.StartsWith("lap:", StringComparison.Ordinal) && !to.StartsWith("finish:", StringComparison.Ordinal) && !to.StartsWith("record:", StringComparison.Ordinal)) continue;
                    string start = to.StartsWith("lap:", StringComparison.Ordinal) ? "finish:" + to.Substring(4) : "start:" + to.Substring(to.IndexOf(':') + 1);
                    Console.WriteLine($"lap [{caps}] {start} -> {to}: {Describe(table.Best(start, to, caps))}");
                }

            // Realize's measurement outranks every other source, even a slower one.
            string next = Stop(k1 + 1), capsK1 = table.CapsAtGoal(k1);
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
