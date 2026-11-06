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
            public string requires = "NONE";
            public string description = "";
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string note;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string detector;
            public List<string> related = new List<string>();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string start;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string routeId;
            // Depends on FloatingOrigin: always plays from its start, never a savestate (holds the player, not the world's origin).
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool floatingOrigin;
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public int fastForward;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string labmap;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string labDemo;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public JObject graftKey;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string graft;
            [JsonExtensionData] public IDictionary<string, JToken> extra;
        }

        static readonly string[] TechCategories = { "glitchless", "nmg", "major" };
        static bool IsTechRequires(string requires) { return Caps.TryParse(requires, out _); }

        static string RequiredUnlocks(JObject body)
        {
            string value = Text(body, "requires");
            if (!IsTechRequires(value)) throw new InvalidOperationException("requires is a set of unlocks as its label (WJ_DS, DS_OM).");
            return value;
        }

        static readonly Regex TechStart = new Regex(@"^(fulltas/[A-Za-z0-9%-]+_NONE|(?<token>[^_\s/]+)_(?<label>" + Caps.LabelPattern + @"))(?= |$)", RegexOptions.IgnoreCase);

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

        TechDef TechOfRoute(MacroDefinition route)
        {
            if (_techStart != null) return _techStart;
            if (route == null) return null;
            if (route.id == LabDemoRouteId) return _labPlay;
            if (route.id == ProbeRouteId) return _probe?.tech;
            foreach (TechDef t in _techs)
                if (t.routeId == route.id) return t;
            return null;
        }

        void ApplyTechAbilities(MacroDefinition route, Movement movement)
        {
            if (route == null || movement == null || route.id != ProbeRouteId || _probe == null || !_probe.omniDash) return;
            if (!_playbackSavedAbilities.ContainsKey("omniDashUnlocked")) _playbackSavedAbilities["omniDashUnlocked"] = GetMovementField(movement, "omniDashUnlocked");
            SetMovementField(movement, "omniDashUnlocked", true);
        }

        bool IsTechDemo(MacroDefinition route)
        {
            if (route == null) return false;
            if (route.id == LabDemoRouteId) return true;
            foreach (TechDef t in _techs)
                if (t.routeId == route.id) return true;
            return route.id == ProbeRouteId && _probe?.tech != null;
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

        const string LabDemoRouteId = "lab-demo";
        TechDef _labPlay;
        MacroDefinition _labPlayRoute;

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

        static List<(string lab, string demo)> TechLabs(TechDef tech, Dictionary<string, List<string>> naming)
        {
            var labs = new List<(string lab, string demo)>();
            if (!string.IsNullOrEmpty(tech.labmap)) labs.Add((tech.labmap, tech.labDemo));
            if (naming.TryGetValue(tech.id, out List<string> ids))
                foreach (string id in ids)
                    if (id != tech.labmap) labs.Add((id, null));
            return labs;
        }

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
            return new TechDef { id = tech.id, name = tech.name, category = tech.category, requires = tech.requires,
                labmap = lab.id, labDemo = chosen.demo, start = start, floatingOrigin = tech.floatingOrigin };
        }

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
                    PlayTechLab(FindTech(Text(body, "id")), body["lab"] == null ? null : Text(body, "lab"), body["demo"] == null ? null : Text(body, "demo"),
                        Truthy(body, "walk"));
                    return new { ok = true };
            }
            throw new InvalidOperationException("Unknown tech command: " + path);
        }

        object BuildTechStaticWebState()
        {
            Dictionary<string, List<string>> naming = LabsNamingTechs();
            List<(TechDef tech, List<(string lab, string demo)> labs)> techs = _techs.Select(t => (t, TechLabs(t, naming))).ToList();
            return new
            {
                list = techs.Select(e => new
                {
                    e.tech.id, e.tech.name, e.tech.category, e.tech.requires, e.tech.floatingOrigin, e.tech.description,
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
                    lab = _techSeenRoute == _labPlayRoute ? new { tech = _labPlay.id, lab = _labPlay.labmap } : null,
                    items = _techSeen.Select(s => new { detector = s.tech.ToString(), label = TechLabel(s.tech), s.start, s.end, s.input }),
                },
            };
        }
    }
}
