using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        public sealed class TechDef
        {
            public string id;
            public string name;
            public string category;
            /// <summary>The fewest unlocks the tech needs: a category's label (WJ_DS) or another set's (DS_OM: the omnidirectional
            /// dash, the statue's or Vman's free box, whatever else the run has).</summary>
            public string requires = "NONE";
            public string origin;
            public string description = "";
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string note;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string detector;
            public List<string> related = new List<string>();
            /// <summary>The start a route demo played from (a category route's prefix or a Full-TAS run's); a lab demo starts at its lab's.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string start;
            /// <summary>A demo route from before every tech was its lab (a library route): kept on disk and playable from the
            /// library, never shown in the Tech tab.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string routeId;
            /// <summary>
            /// The demo depends on FloatingOrigin (where the world was rebased): it draws the origin overlay, and it always plays
            /// from its start, never from a savestate, which holds only the player and not the world's origin.
            /// </summary>
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool floatingOrigin;
            /// <summary>
            /// A Full-TAS demo's first input ticks, run in turbo right after the new game loads, so it starts where the tech
            /// begins with the world as the run left it (a savestate holds only the player).
            /// </summary>
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public int fastForward;
            /// <summary>
            /// The tech's own lab map (labmaps/&lt;id&gt;.json): its demo injects the lab, starts at its lab start and restores at
            /// the end. Lab-only: never a route, never on a route's path (root README "### Rules"). A lab file naming the tech
            /// ("tech") is one of its labs too (<see cref="TechLabs"/>).
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string labmap;
            /// <summary>Which of the lab map's demos (labmaps/&lt;labmap&gt;.&lt;labDemo&gt;.demo.json, with its own start); none: &lt;labmap&gt;.demo.json.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string labDemo;
            /// <summary>
            /// The offline tech miner's key window (enginesim TechGrafts): {"contact": a ContactLog kind, "tick": the demo input tick
            /// near it}, for a demo whose last button isn't where the tech happens. Kept as written; the plugin doesn't read it.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public JObject graftKey;
            /// <summary>The offline miner's marker, "todo": known real, no lab yet (the Tech tab lists it as not reproduced). Kept as written.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string graft;
            /// <summary>Every other techs.json field (the offline side's: graftDemos, ...), written back as read.</summary>
            [JsonExtensionData] public IDictionary<string, JToken> extra;
        }

        static readonly string[] TechCategories = { "glitchless", "nmg", "major" };
        static readonly string[] TechOrigins = { "community", "game", "ours" };
        /// <summary>techs.json "requires" (<see cref="TechDef.requires"/>): a set of unlocks as its label.</summary>
        static bool IsTechRequires(string requires) { return Caps.TryParse(requires, out _); }

        /// <summary>The body's "requires": a set of unlocks as its label.</summary>
        static string RequiredUnlocks(JObject body)
        {
            string value = Text(body, "requires");
            if (!IsTechRequires(value)) throw new InvalidOperationException("requires is a set of unlocks as its label (WJ_DS, DS_OM).");
            return value;
        }

        // A tech demo's start: a category route's prefix (2_WJ_DS) or a Full-TAS run's (fulltas/VMAN_NONE, its route-name
        // category fixed); the category part must be one of the categories.
        static readonly Regex TechStart = new Regex(@"^(fulltas/[A-Za-z0-9%-]+_NONE|(?<token>[^_\s/]+)_(?<label>" + Caps.LabelPattern + @"))(?= |$)", RegexOptions.IgnoreCase);

        /// <summary>The start this name has (a category route's prefix or a Full-TAS run's), or empty when its category part
        /// is no category.</summary>
        static Match MatchTechStart(string name)
        {
            Match m = TechStart.Match(name ?? "");
            return !m.Groups["label"].Success || Categories.IsCategory(m.Groups["label"].Value.ToUpperInvariant()) ? m : Match.Empty;
        }

        List<TechDef> _techs = new List<TechDef>();
        readonly string[] _techLabels = (string[])TechNames.Clone();

        string TechsPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "techs.json"); } }

        void LoadTechs()
        {
            List<TechDef> seed = SeedTechs();
            try
            {
                if (File.Exists(TechsPath))
                {
                    _techs = JsonConvert.DeserializeObject<List<TechDef>>(File.ReadAllText(TechsPath)) ?? new List<TechDef>();
                    CheckTechs();
                    if (MergeSeedTechs(seed)) SaveTechs();
                    else RefreshTechLabels();
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("techs.json rejected (" + ex.Message + "); starting over from the built-in list.");
                Quarantine(TechsPath);
            }
            _techs = seed;
            CheckTechs();
            SaveTechs();
        }

        static List<TechDef> SeedTechs()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("IGTAPTasMod.techs.json"))
            using (var reader = new StreamReader(stream))
                return JsonConvert.DeserializeObject<List<TechDef>>(reader.ReadToEnd());
        }

        string TechsSeededPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "techs.seeded.json"); } }

        /// <summary>
        /// The live catalog (the Tech tab's, edited in the game) keeps its own text and order; a plugin build adds what its
        /// built-in list has that the live one lacks: techs it doesn't have (by id, at the end) and a detector for a tech that
        /// has none, when no other tech has that detector yet. Each is offered once (techs.seeded.json remembers it), so a tech
        /// deleted or a detector cleared in the Tech tab stays that way. A tech the built-in list once offered and has since
        /// dropped (buy-upgrade-from-top, faster-acceleration) goes from the live catalog once too ("-&lt;id&gt;" in
        /// techs.seeded.json); one the built-in list never offered is the user's and stays. The lab and miner fields (labmap
        /// with its labDemo, start, graftKey, graft) are the repo's: the built-in ones fill a live tech's gaps on every load, and
        /// a live "todo" goes once the built-in tech has a lab and no marker. True when the catalog changed.
        /// </summary>
        bool MergeSeedTechs(List<TechDef> seed)
        {
            var offered = new HashSet<string>();
            try
            {
                if (File.Exists(TechsSeededPath)) offered.UnionWith(JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(TechsSeededPath)) ?? new List<string>());
            }
            catch (Exception ex) { Logger.LogWarning("techs.seeded.json unreadable (" + ex.Message + "); offering the built-in techs again."); }
            int before = offered.Count;
            bool changed = false;
            foreach (TechDef live in _techs.Where(t => offered.Contains(t.id) && !seed.Any(s => s.id == t.id)).ToList())
            {
                if (!offered.Add("-" + live.id)) continue;
                _techs.Remove(live);
                Logger.LogInfo("techs.json: removed " + live.name + ", which the built-in list dropped.");
                changed = true;
            }
            foreach (TechDef s in seed)
            {
                TechDef live = _techs.Find(t => t.id == s.id);
                if (offered.Add(s.id) && live == null)
                {
                    _techs.Add(s);
                    Logger.LogInfo("techs.json: added " + s.name + " from the built-in list.");
                    changed = true;
                }
                if (live != null && FillSeedGaps(live, s)) changed = true;
                if (s.detector != null && offered.Add(s.id + "#" + s.detector) && live != null && live.detector == null
                    && !_techs.Any(t => t.detector == s.detector))
                {
                    live.detector = s.detector;
                    changed = true;
                }
            }
            if (offered.Count != before) WriteJsonAtomic(TechsSeededPath, offered.OrderBy(o => o, StringComparer.Ordinal).ToList(), false);
            if (!changed) return false;
            foreach (TechDef t in _techs) t.related.RemoveAll(r => !_techs.Any(o => o.id == r));
            CheckTechs();
            return true;
        }

        /// <summary><see cref="MergeSeedTechs"/>'s lab and miner fields: the built-in tech's fill the live one's gaps. True when one did.</summary>
        static bool FillSeedGaps(TechDef live, TechDef seed)
        {
            bool changed = false;
            if (live.labmap == null && seed.labmap != null)
            {
                live.labmap = seed.labmap;
                live.labDemo = seed.labDemo;
                changed = true;
            }
            if (live.start == null && seed.start != null) { live.start = seed.start; changed = true; }
            if (live.graftKey == null && seed.graftKey != null) { live.graftKey = seed.graftKey; changed = true; }
            if (live.graft == null && seed.graft != null) { live.graft = seed.graft; changed = true; }
            else if (live.graft == "todo" && seed.graft == null && seed.labmap != null) { live.graft = null; changed = true; }
            return changed;
        }

        void CheckTechs()
        {
            var ids = new HashSet<string>();
            foreach (TechDef t in _techs)
            {
                if (string.IsNullOrWhiteSpace(t.id) || !ids.Add(t.id)) throw new InvalidDataException("tech id \"" + t.id + "\" is missing or repeated.");
                if (string.IsNullOrWhiteSpace(t.name)) throw new InvalidDataException("tech " + t.id + " has no name.");
                if (!TechCategories.Contains(t.category)) throw new InvalidDataException("tech " + t.id + ": category is one of " + string.Join(", ", TechCategories) + ".");
                if (string.IsNullOrEmpty(t.requires)) t.requires = "NONE";
                if (!IsTechRequires(t.requires)) throw new InvalidDataException("tech " + t.id + ": requires is a set of unlocks as its label (WJ_DS, DS_OM).");
                if (!TechOrigins.Contains(t.origin)) throw new InvalidDataException("tech " + t.id + ": origin is one of " + string.Join(", ", TechOrigins) + ".");
                if (t.detector != null && !Enum.IsDefined(typeof(Tech), t.detector)) throw new InvalidDataException("tech " + t.id + ": no detector " + t.detector + ".");
                if (t.labmap != null && (t.labmap.Length == 0 || t.labmap.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    throw new InvalidDataException("tech " + t.id + ": labmap is a lab map's id (labmaps/<id>.json).");
                if (t.labDemo != null && (t.labDemo.Length == 0 || t.labmap == null || t.labDemo.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    throw new InvalidDataException("tech " + t.id + ": labDemo names one of its labmap's demos (labmaps/<labmap>.<labDemo>.demo.json).");
                if (t.description == null) t.description = "";
                if (t.related == null) t.related = new List<string>();
            }
        }

        void SaveTechs()
        {
            WriteJsonAtomic(TechsPath, _techs, false);
            RefreshTechLabels();
        }

        void RefreshTechLabels()
        {
            for (int i = 0; i < TechNames.Length; i++)
            {
                string detector = ((Tech)i).ToString();
                TechDef named = _techs.Find(t => t.detector == detector);
                _techLabels[i] = named != null ? named.name.ToUpperInvariant() : TechNames[i];
            }
        }

        string TechLabel(Tech tech) { return _techLabels[(int)tech]; }

        TechDef FindTech(string id)
        {
            return _techs.Find(t => t.id == id) ?? throw new InvalidOperationException("No tech \"" + id + "\".");
        }

        MacroDefinition TechRoute(TechDef tech)
        {
            return string.IsNullOrEmpty(tech.routeId) ? null : _library.macros.Find(m => m.id == tech.routeId);
        }

        /// <summary>The tech whose demo a route plays: the tech trace's (EngineSimTechTrace), a tech probe's, the Tech tab's lab
        /// demo (<see cref="PlayTechLab"/>), or the one whose library demo route it is.</summary>
        TechDef TechOfRoute(MacroDefinition route)
        {
            if (_techStart != null) return _techStart;
            if (route == null) return null;
            if (route.id == LabDemoRouteId) return _labPlay;
            return route.id == ProbeRouteId ? _probe?.tech : _techs.Find(t => t.routeId == route.id);
        }

        /// <summary>A probe's own unlocks on top of its start's (the omnidirectional dash), saved with the category's so playback's end puts them back.</summary>
        void ApplyTechAbilities(MacroDefinition route, Movement movement)
        {
            if (route == null || movement == null || route.id != ProbeRouteId || _probe == null || !_probe.omniDash) return;
            if (!_playbackSavedAbilities.ContainsKey("omniDashUnlocked")) _playbackSavedAbilities["omniDashUnlocked"] = GetMovementField(movement, "omniDashUnlocked");
            SetMovementField(movement, "omniDashUnlocked", true);
        }

        bool IsTechDemo(MacroDefinition route)
        {
            return route != null && (route.id == LabDemoRouteId || _techs.Any(t => t.routeId == route.id) || route.id == ProbeRouteId && _probe?.tech != null);
        }

        MacroDefinition TechPlayable(MacroDefinition route)
        {
            TechDef tech = route == null ? null : _techs.Find(t => t.routeId == route.id);
            if (tech == null) return route;
            string start = string.IsNullOrEmpty(route.savestateId) || !FullTasRouteName.IsMatch(tech.start + " ") ? tech.start : null;
            return new MacroDefinition { id = route.id, name = (string.IsNullOrEmpty(start) ? "" : start + " ") + "tech " + tech.name,
                savestateId = route.savestateId, steps = route.steps };
        }

        void DropSavestate(string id)
        {
            if (string.IsNullOrEmpty(id) || _library.macros.Any(m => m.savestateId == id)) return;
            _savestates.RemoveAll(c => c.id == id);
            try { File.Delete(Path.Combine(_savestatesPath, SafeId(id) + ".json")); }
            catch (Exception ex) { Logger.LogWarning("Could not delete savestate " + id + ": " + ex.Message); }
        }

        void NameTechRoute(MacroDefinition route, string name)
        {
            route.name = "tech/" + name;
            Savestate state = _savestates.Find(c => c.id == route.savestateId);
            if (state == null || _library.macros.Any(m => m != route && m.savestateId == state.id)) return;
            state.name = route.name;
            WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(state.id) + ".json"), state, false);
        }

        void RemoveTechRoute(MacroDefinition route)
        {
            int index = _library.macros.IndexOf(route);
            if (index < 0) return;
            _library.macros.RemoveAt(index);
            _library.selectedIndex = Math.Max(0, Math.Min(_library.selectedIndex > index ? _library.selectedIndex - 1 : _library.selectedIndex,
                _library.macros.Count - 1));
            SaveLibrary();
            DropSavestate(route.savestateId);
        }

        /// <summary>The route id of a lab demo the Tech tab plays (<see cref="PlayTechLab"/>): never in the library.</summary>
        const string LabDemoRouteId = "lab-demo";
        /// <summary>The lab demo playing (or last played) from the Tech tab: its tech on that lab (<see cref="TechLabDemo"/>) and its route.</summary>
        TechDef _labPlay;
        MacroDefinition _labPlayRoute;

        /// <summary>Each lab map's id by the tech its file names ("tech"); a lab that doesn't load names none.</summary>
        Dictionary<string, List<string>> LabsNamingTechs()
        {
            var byTech = new Dictionary<string, List<string>>();
            foreach (string id in LabMapIds())
            {
                string tech;
                try { tech = CachedLabMap(id).tech; }
                catch (Exception) { continue; }
                if (string.IsNullOrEmpty(tech)) continue;
                if (!byTech.TryGetValue(tech, out List<string> ids)) byTech[tech] = ids = new List<string>();
                ids.Add(id);
            }
            return byTech;
        }

        /// <summary>
        /// A tech's labs, each with the demo it plays: its own labmap (with its labDemo, else the main demo) first, then every
        /// other lab map whose file names the tech, with its main demo (spring-double-hit and spring-double-hit-side). A tech
        /// with none isn't reproduced yet.
        /// </summary>
        static List<(string lab, string demo)> TechLabs(TechDef tech, Dictionary<string, List<string>> naming)
        {
            var labs = new List<(string lab, string demo)>();
            if (!string.IsNullOrEmpty(tech.labmap)) labs.Add((tech.labmap, tech.labDemo));
            if (naming.TryGetValue(tech.id, out List<string> ids))
                foreach (string id in ids)
                    if (id != tech.labmap) labs.Add((id, null));
            return labs;
        }

        /// <summary>
        /// One of a tech's lab demos (<paramref name="labId"/>, null: its first lab): the tech as it plays there (its labmap
        /// and labDemo that lab's, its start the lab category's Course 1 start, whose zone and unlocks the lab's demo was solved
        /// with) and the demo's input ticks, read from the lab's demo file on every call. <paramref name="demoName"/> picks
        /// another of the lab's demos (labmaps/&lt;lab&gt;.&lt;demo&gt;.demo.json; empty: its main demo); null plays the one
        /// <see cref="TechLabs"/> gives the lab.
        /// </summary>
        TechDef TechLabDemo(TechDef tech, string labId, out List<SolverTick> inputs, string demoName = null)
        {
            List<(string lab, string demo)> labs = TechLabs(tech, LabsNamingTechs());
            if (labs.Count == 0) throw new InvalidOperationException(tech.name + " has no lab yet.");
            (string lab, string demo) chosen = labId == null ? labs[0] : labs.Find(l => l.lab == labId);
            if (chosen.lab == null) throw new InvalidOperationException(tech.name + " has no lab " + labId + ".");
            if (demoName != null)
            {
                if (demoName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("A lab demo's name is file-safe.");
                chosen.demo = demoName.Length == 0 ? null : demoName;
            }
            LabMapDef lab = LoadLabMap(chosen.lab);
            string start = "1_" + lab.category;
            if (MatchTechStart(start).Length != start.Length) throw new InvalidOperationException("Lab map " + lab.id + "'s category " + lab.category + " is no category.");
            string demoFile = lab.id + (string.IsNullOrEmpty(chosen.demo) ? "" : "." + chosen.demo) + ".demo.json";
            JObject demo = CachedLabJson(demoFile) ?? throw new InvalidOperationException("Lab map " + lab.id + " has no demo " + demoFile + " yet.");
            inputs = demo["inputs"]?.ToObject<List<SolverTick>>();
            if (inputs == null || inputs.Count == 0) throw new InvalidOperationException(demoFile + " has no inputs.");
            return new TechDef { id = tech.id, name = tech.name, category = tech.category, requires = tech.requires, origin = tech.origin,
                labmap = lab.id, labDemo = chosen.demo, start = start, floatingOrigin = tech.floatingOrigin };
        }

        /// <summary>Plays one of a tech's lab demos (<see cref="TechLabDemo"/>) at 1x (walk: slowed), as a route that never enters the library.</summary>
        void PlayTechLab(TechDef tech, string labId, string demoName, bool walk)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            TechDef playing = TechLabDemo(tech, labId, out List<SolverTick> inputs, demoName);
            _labPlay = playing;
            _labPlayRoute = new MacroDefinition { id = LabDemoRouteId, name = playing.start + " tech " + tech.name, savestateId = "", steps = ToSteps(inputs) };
            EnsureGameUnpaused();
            _walkSlowdown = walk ? DefaultWalkSlowdown : 0f;
            _pendingReplay = _labPlayRoute;
            _pendingReplayStartDelayTicks = 0;
        }

        static string OneOf(JObject body, string key, string[] values)
        {
            string value = Text(body, key);
            if (!values.Contains(value)) throw new InvalidOperationException(key + " is one of " + string.Join(", ", values) + ".");
            return value;
        }

        object HandleTechs(string path, JObject body)
        {
            switch (path)
            {
                case "/api/techs/update":
                {
                    TechDef tech = FindTech(Text(body, "id"));
                    if (body["name"] != null)
                    {
                        string name = Text(body, "name").Trim();
                        if (name.Length == 0) throw new InvalidOperationException("A tech needs a name.");
                        RejectCategoryName("tech/" + name);
                        tech.name = name;
                        MacroDefinition route = TechRoute(tech);
                        if (route != null) { NameTechRoute(route, name); SaveLibrary(); }
                    }
                    if (body["category"] != null) tech.category = OneOf(body, "category", TechCategories);
                    if (body["origin"] != null) tech.origin = OneOf(body, "origin", TechOrigins);
                    if (body["requires"] != null) tech.requires = RequiredUnlocks(body);
                    if (body["description"] != null) tech.description = Text(body, "description").Trim();
                    if (body["note"] != null)
                    {
                        string note = Text(body, "note").Trim();
                        tech.note = note.Length == 0 ? null : note;
                    }
                    if (body["detector"] != null)
                    {
                        string detector = Text(body, "detector");
                        if (detector.Length > 0 && !Enum.IsDefined(typeof(Tech), detector)) throw new InvalidOperationException("No detector " + detector + ".");
                        TechDef other = detector.Length == 0 ? null : _techs.Find(t => t != tech && t.detector == detector);
                        if (other != null) throw new InvalidOperationException(other.name + " already has the " + detector + " detector.");
                        tech.detector = detector.Length == 0 ? null : detector;
                    }
                    if (body["floatingOrigin"] != null) tech.floatingOrigin = (bool)body["floatingOrigin"];
                    if (body["labmap"] != null || body["labDemo"] != null)
                    {
                        // The tech's own lab and which of its demos it plays (empty: the main one); another lab's file naming the
                        // tech stays one of its labs either way.
                        string labmap = body["labmap"] != null ? Text(body, "labmap").Trim() : tech.labmap ?? "";
                        string labDemo = body["labDemo"] != null ? Text(body, "labDemo").Trim() : labmap == tech.labmap ? tech.labDemo ?? "" : "";
                        if (labmap.Length > 0) LoadLabMap(labmap);
                        if (labDemo.Length > 0 && (labmap.Length == 0 || labDemo.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                            throw new InvalidOperationException("A lab demo is one of its lab map's demos (labmaps/<labmap>.<labDemo>.demo.json).");
                        tech.labmap = labmap.Length == 0 ? null : labmap;
                        tech.labDemo = labDemo.Length == 0 ? null : labDemo;
                    }
                    if (body["related"] is JArray related)
                    {
                        List<string> ids = related.Select(r => r.ToString()).Where(r => r != tech.id).Distinct().ToList();
                        foreach (string id in ids) FindTech(id);
                        tech.related = ids;
                    }
                    if (body["position"] != null)
                    {
                        // Where the tech sits in the list (and in its Tech tab column): an index, or -1 for the end.
                        int position = Number(body, "position", -1);
                        _techs.Remove(tech);
                        _techs.Insert(position < 0 || position > _techs.Count ? _techs.Count : position, tech);
                    }
                    SaveTechs();
                    return new { ok = true };
                }
                case "/api/techs/delete":
                {
                    TechDef tech = FindTech(Text(body, "id"));
                    MacroDefinition route = TechRoute(tech);
                    if (route != null)
                    {
                        if (PlaybackActive) throw new InvalidOperationException("Stop playback before deleting a tech with a demo.");
                        RemoveTechRoute(route);
                    }
                    _techs.Remove(tech);
                    foreach (TechDef t in _techs) t.related.Remove(tech.id);
                    SaveTechs();
                    return new { ok = true };
                }
                case "/api/techs/merge":
                {
                    TechDef from = FindTech(Text(body, "id")), into = FindTech(Text(body, "into"));
                    if (from == into) throw new InvalidOperationException("A tech can't merge into itself.");
                    if (PlaybackActive) throw new InvalidOperationException("Stop playback before merging techs.");
                    if (from.description.Length > 0 && from.description != into.description)
                        into.description = (into.description.Length > 0 ? into.description + "\n\n" : "") + from.name + ": " + from.description;
                    if (from.note != null) into.note = (into.note == null ? "" : into.note + "\n\n") + from.name + ": " + from.note;
                    if (into.detector == null) into.detector = from.detector;
                    if (into.labmap == null && from.labmap != null)
                    {
                        into.labmap = from.labmap;
                        into.labDemo = from.labDemo;
                    }
                    MacroDefinition fromRoute = TechRoute(from);
                    if (fromRoute != null)
                    {
                        if (TechRoute(into) == null)
                        {
                            into.routeId = fromRoute.id;
                            into.start = from.start;
                            NameTechRoute(fromRoute, into.name);
                            SaveLibrary();
                        }
                        else RemoveTechRoute(fromRoute);
                    }
                    into.related = into.related.Concat(from.related).Where(r => r != into.id && r != from.id).Distinct().ToList();
                    _techs.Remove(from);
                    foreach (TechDef t in _techs)
                        if (t.related.Remove(from.id) && t != into && !t.related.Contains(into.id)) t.related.Add(into.id);
                    SaveTechs();
                    return new { ok = true };
                }
                case "/api/techs/play":
                    // One of the tech's lab demos (lab: its id, else the tech's first lab; demo: which of that lab's demos, else
                    // the one the tech plays there), at 1x or walked.
                    PlayTechLab(FindTech(Text(body, "id")), body["lab"] == null ? null : Text(body, "lab"), body["demo"] == null ? null : Text(body, "demo"),
                        Truthy(body, "walk"));
                    return new { ok = true };
            }
            throw new InvalidOperationException("Unknown tech command: " + path);
        }

        /// <summary>
        /// The techs with their labs (<see cref="TechLabs"/>: each lab's id, the demo the tech plays there and that demo's length),
        /// those labs (shapes, starts, ghosts' paths) and the detectors (studio static state).
        /// </summary>
        object BuildTechStaticWebState()
        {
            Dictionary<string, List<string>> naming = LabsNamingTechs();
            List<(TechDef tech, List<(string lab, string demo)> labs)> techs = _techs.Select(t => (t, TechLabs(t, naming))).ToList();
            return new
            {
                list = techs.Select(e => new
                {
                    e.tech.id, e.tech.name, e.tech.category, e.tech.requires, e.tech.origin, e.tech.floatingOrigin, e.tech.description,
                    e.tech.note, e.tech.detector, e.tech.related, e.tech.labmap, e.tech.labDemo, e.tech.graft,
                    labs = e.labs.Select(l =>
                    {
                        JObject demo;
                        try { demo = CachedLabJson(l.lab + (string.IsNullOrEmpty(l.demo) ? "" : "." + l.demo) + ".demo.json"); }
                        catch (Exception) { demo = null; }
                        return new { id = l.lab, l.demo, ticks = demo?["ticks"], inputTicks = demo?["inputTicks"] };
                    }).ToList(),
                }),
                labs = techs.SelectMany(e => e.labs.Select(l => l.lab)).Distinct().Select(id =>
                {
                    try
                    {
                        LabMapDef lab = CachedLabMap(id);
                        return (object)new
                        {
                            lab.id, lab.tech, lab.name, lab.category, lab.techNote, lab.anchor, lab.region, lab.finish,
                            start = new { lab.start.x, lab.start.y, lab.start.vx, lab.start.vy, lab.start.facing, fields = lab.start.fields },
                            shapes = lab.shapes.Select(s => new { s.kind, s.at, s.size, s.points, s.layer, s.tag, s.trigger, s.spike, s.rotation, s.spring }),
                            hand = lab.hand == null ? null : new
                            {
                                shapes = (lab.hand.shapes ?? new List<LabShapeDef>()).Select(s => new { s.kind, s.at, s.size, s.points, s.layer, s.tag, s.trigger, s.spike, s.rotation, s.spring }),
                                start = lab.hand.start == null ? null : new { lab.hand.start.x, lab.hand.start.y, lab.hand.start.facing },
                            },
                            // The ghosts' paths in the lab's `ghosts` order (the in-game tints'), else the main demo's alone.
                            ghosts = (lab.ghosts ?? new List<LabGhostDef> { new LabGhostDef() }).Select(g =>
                            {
                                JObject file = CachedLabJson(lab.id + (string.IsNullOrEmpty(g.demo) ? "" : "." + g.demo) + ".demo.json");
                                return new { g.label, g.demo, path = file == null ? null : file["path"] };
                            }),
                        };
                    }
                    catch (Exception ex) { return new { id, error = ex.Message } as object; }
                }).ToList(),
                detectors = Enumerable.Range(0, TechNames.Length).Select(i => new { id = ((Tech)i).ToString(), label = TechNames[i] }),
            };
        }

        /// <summary>The injected lab and the last playback's techs (studio live state).</summary>
        object BuildTechLiveWebState()
        {
            return new
            {
                injectedLab = InjectedLabId,
                seen = _techSeenRoute == null ? null : new
                {
                    routeId = _techSeenRoute.id,
                    route = _techSeenRoute.name,
                    running = OverlayListsRun,
                    // A Tech tab lab demo's: which tech on which lab, for its card's verdict.
                    lab = _techSeenRoute == _labPlayRoute ? new { tech = _labPlay.id, lab = _labPlay.labmap } : null,
                    items = _techSeen.Select(s => new { detector = s.tech.ToString(), label = TechLabel(s.tech), s.start, s.end, s.input }),
                },
            };
        }
    }
}
