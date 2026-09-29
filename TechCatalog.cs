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
            /// <summary>The fewest unlocks the tech needs: NONE, WJ, WJ_DS, WJ_DS_DJ, WJ_DS_DJ_BS, or OMNI (the omnidirectional dash: the statue's, or Vman's free box).</summary>
            public string requires = "NONE";
            public string origin;
            public string description = "";
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string note;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string detector;
            public List<string> related = new List<string>();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string start;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string routeId;
            /// <summary>The demo plays with the omnidirectional dash on top of its start's unlocks (Vman techs); put back after.</summary>
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool omniDash;
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
            /// The demo plays on an injected lab map (labmaps/&lt;id&gt;.json): it injects the lab, starts at its lab start and
            /// restores at the end. Lab-only: never a route, never on a route's path (root README "### Rules").
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string labmap;
            /// <summary>Which of the lab map's demos (labmaps/&lt;labmap&gt;.&lt;labDemo&gt;.demo.json, with its own start); none: &lt;labmap&gt;.demo.json.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string labDemo;
        }

        static readonly string[] TechCategories = { "glitchless", "nmg", "major" };
        static readonly string[] TechOrigins = { "community", "game", "ours" };
        static readonly string[] TechRequires = { "NONE", "WJ", "WJ_DS", "WJ_DS_DJ", "WJ_DS_DJ_BS", "OMNI" };
        const int TechDemoTailTicks = 25;
        static readonly Regex TechStart = new Regex(@"^(fulltas/[A-Za-z0-9%-]+_NONE|[^_\s/]+_(?:NONE|WJ_DS_DJ_BS_OM|WJ_DS_DJ_BS|WJ_DS_DJ|WJ_DS|WJ))(?= |$)", RegexOptions.IgnoreCase);

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
        /// The live catalog (the Tech tab's, edited in the game) keeps its own text, demos and order; a plugin build adds what
        /// its built-in list has that the live one lacks: techs it doesn't have (by id, at the end) and a detector for a tech
        /// that has none, when no other tech has that detector yet. Each is offered once (techs.seeded.json remembers it), so a
        /// tech deleted or a detector cleared in the Tech tab stays that way. True when the catalog changed.
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
            foreach (TechDef s in seed)
            {
                TechDef live = _techs.Find(t => t.id == s.id);
                if (offered.Add(s.id) && live == null)
                {
                    _techs.Add(s);
                    Logger.LogInfo("techs.json: added " + s.name + " from the built-in list.");
                    changed = true;
                }
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

        void CheckTechs()
        {
            var ids = new HashSet<string>();
            foreach (TechDef t in _techs)
            {
                if (string.IsNullOrWhiteSpace(t.id) || !ids.Add(t.id)) throw new InvalidDataException("tech id \"" + t.id + "\" is missing or repeated.");
                if (string.IsNullOrWhiteSpace(t.name)) throw new InvalidDataException("tech " + t.id + " has no name.");
                if (!TechCategories.Contains(t.category)) throw new InvalidDataException("tech " + t.id + ": category is one of " + string.Join(", ", TechCategories) + ".");
                if (string.IsNullOrEmpty(t.requires)) t.requires = "NONE";
                if (!TechRequires.Contains(t.requires)) throw new InvalidDataException("tech " + t.id + ": requires is one of " + string.Join(", ", TechRequires) + ".");
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

        /// <summary>A demo's (or a probe's) own unlocks on top of its start's (the omnidirectional dash), saved with the category's so playback's end puts them back.</summary>
        void ApplyTechAbilities(MacroDefinition route, Movement movement)
        {
            if (route == null || movement == null) return;
            TechDef tech = _techs.Find(t => t.routeId == route.id);
            bool probeOmni = route.id == ProbeRouteId && _probe != null && _probe.omniDash;
            if (!(tech != null && tech.omniDash) && !probeOmni) return;
            if (!_playbackSavedAbilities.ContainsKey("omniDashUnlocked")) _playbackSavedAbilities["omniDashUnlocked"] = GetMovementField(movement, "omniDashUnlocked");
            SetMovementField(movement, "omniDashUnlocked", true);
        }

        bool IsTechDemo(MacroDefinition route)
        {
            return route != null && _techs.Any(t => t.routeId == route.id);
        }

        MacroDefinition TechPlayable(MacroDefinition route)
        {
            TechDef tech = route == null ? null : _techs.Find(t => t.routeId == route.id);
            if (tech == null) return route;
            string start = string.IsNullOrEmpty(route.savestateId) || !FullTasRouteName.IsMatch(tech.start + " ") ? tech.start : null;
            return new MacroDefinition { id = route.id, name = (string.IsNullOrEmpty(start) ? "" : start + " ") + "tech " + tech.name,
                savestateId = route.savestateId, steps = route.steps };
        }

        void SetTechDemo(TechDef tech, MacroDefinition source, TechSpan span)
        {
            List<MacroStep> played = source.steps.Where(s => s.enabled).ToList();
            if (played.Any(s => s.type != MacroStepType.RawInput))
                throw new InvalidOperationException("A demo is raw input; " + source.name + " has other actions.");
            List<SolverTick> inputs = ToTicks(played);
            Savestate state = _savestates.Find(c => c.id == source.savestateId);
            tech.fastForward = 0;
            if (span != null)
            {
                int ticks = (int)span.input + TechDemoTailTicks;
                if (ticks < inputs.Count) inputs.RemoveRange(ticks, inputs.Count - ticks);
                if (span.savestate != null && tech.floatingOrigin) tech.fastForward = (int)span.savestateInput;
                else if (span.savestate != null)
                {
                    inputs.RemoveRange(0, (int)Math.Min(span.savestateInput, inputs.Count));
                    state = span.savestate;
                }
            }
            if (inputs.Count == 0) throw new InvalidOperationException(source.name + " has no inputs for a demo.");
            TechDef owner = _techs.Find(t => t.routeId == source.id);
            Match start = TechStart.Match(source.name ?? "");
            tech.start = owner != null ? owner.start : start.Success ? start.Groups[1].Value : null;
            MacroDefinition route = TechRoute(tech);
            if (route == null)
            {
                route = new MacroDefinition();
                _library.macros.Add(route);
                tech.routeId = route.id;
            }
            string replaced = route.savestateId;
            route.name = "tech/" + tech.name;
            route.savestateId = state == null ? "" : AddTechSavestate(state, route.name);
            route.steps = ToSteps(inputs);
            SaveLibrary();
            DropSavestate(replaced);
            SaveTechs();
        }

        string AddTechSavestate(Savestate state, string name)
        {
            Savestate copy = JsonConvert.DeserializeObject<Savestate>(JsonConvert.SerializeObject(state));
            copy.id = Guid.NewGuid().ToString("N");
            copy.name = name;
            copy.createdUtc = DateTime.UtcNow.ToString("o");
            _savestates.Insert(0, copy);
            WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(copy.id) + ".json"), copy, false);
            return copy.id;
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

        void FillTechDemos()
        {
            if (_techSeenRoute == null || _techSeenFilled) return;
            _techSeenFilled = true;
            foreach (TechSpan span in _techSeen)
            {
                string detector = span.tech.ToString();
                TechDef tech = _techs.Find(t => t.detector == detector);
                MacroDefinition route = tech == null ? null : TechRoute(tech);
                if (tech == null || route != null && (tech.floatingOrigin || route.id != _techSeenRoute.id || !string.IsNullOrEmpty(route.savestateId) || span.savestate == null)) continue;
                try
                {
                    SetTechDemo(tech, _techSeenRoute, span);
                    bool own = span.savestate != null && !tech.floatingOrigin;
                    Logger.LogInfo("Tech demo: " + tech.name + " from " + _techSeenRoute.name + " (input ticks " + (own ? span.savestateInput : 0)
                        + "-" + span.input + (own ? ", from its own savestate)" : ", from the route's start)"));
                }
                catch (InvalidOperationException ex) { Logger.LogWarning("No tech demos from this playback: " + ex.Message); return; }
            }
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
                case "/api/techs/add":
                {
                    string name = Text(body, "name").Trim();
                    if (name.Length == 0) throw new InvalidOperationException("A tech needs a name.");
                    RejectCategoryName("tech/" + name);
                    string category = OneOf(body, "category", TechCategories);
                    string slug = TechSlug(name), id = slug.Length == 0 ? "tech" : slug;
                    for (int n = 2; _techs.Any(t => t.id == id); n++) id = (slug.Length == 0 ? "tech" : slug) + "-" + n;
                    _techs.Add(new TechDef { id = id, name = name, category = category, origin = "ours",
                        requires = body["requires"] != null ? OneOf(body, "requires", TechRequires) : "NONE" });
                    SaveTechs();
                    return new { ok = true, id };
                }
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
                    if (body["requires"] != null) tech.requires = OneOf(body, "requires", TechRequires);
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
                    if (body["fastForward"] != null) tech.fastForward = Math.Max(0, Number(body, "fastForward", 0));
                    if (body["labmap"] != null)
                    {
                        string labmap = Text(body, "labmap").Trim();
                        if (labmap.Length > 0) LoadLabMap(labmap);
                        tech.labmap = labmap.Length == 0 ? null : labmap;
                        if (tech.labmap == null) tech.labDemo = null;
                    }
                    if (body["start"] != null)
                    {
                        string start = Text(body, "start").Trim();
                        if (start.Length > 0 && TechStart.Match(start).Length != start.Length)
                            throw new InvalidOperationException("A start is a category route's prefix (2_WJ_DS) or a Full-TAS run's (fulltas/VMAN_NONE).");
                        tech.start = start.Length == 0 ? null : start;
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
                {
                    TechDef tech = FindTech(Text(body, "id"));
                    MacroDefinition route = TechRoute(tech) ?? throw new InvalidOperationException(tech.name + " has no demo yet.");
                    if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
                    _library.selectedIndex = _library.macros.IndexOf(route);
                    SaveLibrary();
                    ReplaySelected(0, Truthy(body, "walk") ? DefaultWalkSlowdown : 0f);
                    return new { ok = true };
                }
                case "/api/techs/demo":
                {
                    TechDef tech = FindTech(Text(body, "id"));
                    if (PlaybackActive) throw new InvalidOperationException("Stop playback first.");
                    string from = OneOf(body, "from", new[] { "playback", "selected", "ticks", "lab", "none" });
                    if (from == "lab")
                    {
                        // A lab map's solved demo (labmaps/<id>.demo.json): the tech's demo plays on the injected lab map.
                        InstallLabDemo(tech, body);
                    }
                    else if (from == "playback")
                    {
                        int seen = Number(body, "seen", -1);
                        if (_techSeenRoute == null || seen < 0 || seen >= _techSeen.Count) throw new InvalidOperationException("That playback tech is gone.");
                        SetTechDemo(tech, _techSeenRoute, _techSeen[seen]);
                    }
                    else if (from == "selected")
                    {
                        MacroDefinition selected = SelectedMacro ?? throw new InvalidOperationException("Select a route first.");
                        if (selected.id == tech.routeId) throw new InvalidOperationException("The selected route is already " + tech.name + "'s demo.");
                        SetTechDemo(tech, selected, null);
                    }
                    else if (from == "ticks")
                    {
                        // A reproduction: raw input ticks with a category's unlocks (start: 1_WJ_DS_DJ), from its quick restart or
                        // from a savestate (savestateId: e.g. a lab/ one on flat ground), which the demo gets its own copy of; or a
                        // Full-TAS run's (fulltas/VMAN_NONE): from a new game of that run, its unlocks the ones the ticks buy.
                        string start = Text(body, "start").Trim();
                        string savestateId = Text(body, "savestateId");
                        if (savestateId.Length > 0 && _savestates.Find(c => c.id == savestateId) == null) throw new InvalidOperationException("Savestate not found.");
                        if (TechStart.Match(start).Length != start.Length || start.Length == 0)
                            throw new InvalidOperationException("start is a course category prefix (1_WJ_DS_DJ) or a Full-TAS run's (fulltas/VMAN_NONE).");
                        if (!(body["ticks"] is JArray ticks) || ticks.Count == 0) throw new InvalidOperationException("ticks: the demo's inputs.");
                        tech.omniDash = body["omniDash"] != null && (bool)body["omniDash"];
                        SetTechDemo(tech, new MacroDefinition { name = start + " " + tech.name, savestateId = savestateId,
                            steps = ToSteps(ticks.ToObject<List<SolverTick>>()) }, null);
                        // fastForward (a Full-TAS start): the first input ticks run in turbo, so the demo starts where the tech begins.
                        if (body["fastForward"] != null)
                        {
                            tech.fastForward = Math.Max(0, Number(body, "fastForward", 0));
                            SaveTechs();
                        }
                    }
                    else
                    {
                        MacroDefinition route = TechRoute(tech);
                        if (route != null) RemoveTechRoute(route);
                        tech.routeId = tech.start = null;
                        tech.labmap = tech.labDemo = null;
                        tech.fastForward = 0;
                        SaveTechs();
                    }
                    return new { ok = true };
                }
            }
            throw new InvalidOperationException("Unknown tech command: " + path);
        }

        static string TechSlug(string name)
        {
            return Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        }

        object BuildTechWebState()
        {
            return new
            {
                list = _techs.Select(t =>
                {
                    MacroDefinition route = TechRoute(t);
                    return new { t.id, t.name, t.category, t.requires, t.origin, confirmed = route != null, t.omniDash, t.floatingOrigin, t.fastForward, t.description, t.note, t.detector, t.related, t.start, t.labmap, t.labDemo,
                        routeId = route == null ? null : route.id,
                        savestate = route != null && _savestates.Any(c => c.id == route.savestateId),
                        ticks = route == null ? 0 : route.steps.Where(s => s.enabled).Sum(s => Math.Max(1, s.durationTicks)) };
                }),
                labs = LabMapIds().Select(id =>
                {
                    string techId = _techs.Find(t => t.labmap == id)?.id;
                    try
                    {
                        LabMapDef lab = LoadLabMap(id);
                        string demoFile = LabMapFile(lab.id + ".demo.json");
                        JObject demo = demoFile == null ? null : JsonConvert.DeserializeObject<JObject>(File.ReadAllText(demoFile));
                        return (object)new
                        {
                            lab.id, tech = techId ?? lab.tech, lab.name, lab.category, lab.techNote, lab.anchor, lab.region, lab.finish,
                            start = new { lab.start.x, lab.start.y, lab.start.vx, lab.start.vy, lab.start.facing, fields = lab.start.fields },
                            shapes = lab.shapes.Select(s => new { s.kind, s.at, s.size, s.points, s.layer, s.tag, s.trigger, s.spike }),
                            hand = lab.hand == null ? null : new
                            {
                                shapes = (lab.hand.shapes ?? new List<LabShapeDef>()).Select(s => new { s.kind, s.at, s.size, s.points, s.layer, s.tag, s.trigger, s.spike }),
                                start = lab.hand.start == null ? null : new { lab.hand.start.x, lab.hand.start.y, lab.hand.start.facing },
                            },
                            demoTicks = demo?["ticks"], demoInputTicks = demo?["inputTicks"], demoPath = demo?["path"],
                        };
                    }
                    catch (Exception ex) { return new { id, tech = techId, error = ex.Message } as object; }
                }).ToList(),
                injectedLab = InjectedLabId,
                detectors = Enumerable.Range(0, TechNames.Length).Select(i => new { id = ((Tech)i).ToString(), label = TechNames[i] }),
                seen = _techSeenRoute == null ? null : new
                {
                    routeId = _techSeenRoute.id,
                    route = _techSeenRoute.name,
                    running = OverlayListsRun,
                    items = _techSeen.Select(s => new { detector = s.tech.ToString(), label = TechLabel(s.tech), s.start, s.end, s.input }),
                },
            };
        }
    }
}
