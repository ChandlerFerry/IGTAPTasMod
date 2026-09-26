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
    // The Tech tab: every named tech (techs.json in the config folder, seeded from the plugin's embedded techs.json on
    // first load), sorted glitchless / NMG / major, each with a demo route that performs it. A demo is the library route
    // "tech/<name>", played from its tech's start: a category route's prefix ("2_WJ_DS": that course's quick restart and
    // unlocks), a Full-TAS run's ("fulltas/VMAN_NONE": its new game), or none (the route's savestate, or the quick
    // restart where the player is). A demo ends on its last input: it finishes no course and is never a record.
    public sealed partial class TasPlugin
    {
        public sealed class TechDef
        {
            public string id;
            public string name;
            // glitchless | nmg | major
            public string category;
            // community: the community's tech list; game: named in the game's code; ours: named or found in this repo.
            public string origin;
            public string description = "";
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string note;
            // The overlay detector (TasOverlayTechs' Tech) that sees a playback perform it; its in-game label is this name.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string detector;
            // Techs this one may be the same as.
            public List<string> related = new List<string>();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string start;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string routeId;
        }

        static readonly string[] TechCategories = { "glitchless", "nmg", "major" };
        static readonly string[] TechOrigins = { "community", "game", "ours" };
        // A demo cut from a playback keeps this many input ticks after the tech, to show what it did.
        const int TechDemoTailTicks = 25;
        static readonly Regex TechStart = new Regex(@"^(fulltas/[A-Za-z0-9%]+_NONE|[^_\s/]+_(?:NONE|WJ_DS_DJ_BS|WJ_DS_DJ|WJ_DS|WJ))(?= |$)", RegexOptions.IgnoreCase);

        List<TechDef> _techs = new List<TechDef>();
        readonly string[] _techLabels = (string[])TechNames.Clone();

        string TechsPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "techs.json"); } }

        void LoadTechs()
        {
            try
            {
                if (File.Exists(TechsPath))
                {
                    _techs = JsonConvert.DeserializeObject<List<TechDef>>(File.ReadAllText(TechsPath)) ?? new List<TechDef>();
                    CheckTechs();
                    RefreshTechLabels();
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("techs.json rejected (" + ex.Message + "); starting over from the built-in list.");
                Quarantine(TechsPath);
            }
            using (Stream seed = Assembly.GetExecutingAssembly().GetManifestResourceStream("IGTAPTasMod.techs.json"))
            using (var reader = new StreamReader(seed))
                _techs = JsonConvert.DeserializeObject<List<TechDef>>(reader.ReadToEnd());
            CheckTechs();
            SaveTechs();
        }

        void CheckTechs()
        {
            var ids = new HashSet<string>();
            foreach (TechDef t in _techs)
            {
                if (string.IsNullOrWhiteSpace(t.id) || !ids.Add(t.id)) throw new InvalidDataException("tech id \"" + t.id + "\" is missing or repeated.");
                if (string.IsNullOrWhiteSpace(t.name)) throw new InvalidDataException("tech " + t.id + " has no name.");
                if (!TechCategories.Contains(t.category)) throw new InvalidDataException("tech " + t.id + ": category is one of " + string.Join(", ", TechCategories) + ".");
                if (!TechOrigins.Contains(t.origin)) throw new InvalidDataException("tech " + t.id + ": origin is one of " + string.Join(", ", TechOrigins) + ".");
                if (t.detector != null && !Enum.IsDefined(typeof(Tech), t.detector)) throw new InvalidDataException("tech " + t.id + ": no detector " + t.detector + ".");
                if (t.description == null) t.description = "";
                if (t.related == null) t.related = new List<string>();
            }
        }

        void SaveTechs()
        {
            WriteJsonAtomic(TechsPath, _techs, false);
            RefreshTechLabels();
        }

        // The overlay names a detected tech after the catalog's tech with its detector.
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

        bool IsTechDemo(MacroDefinition route)
        {
            return route != null && _techs.Any(t => t.routeId == route.id);
        }

        // What playback runs for a tech's demo: its inputs under its start's name, so the course, the unlocks and a Full-TAS
        // run's new game follow the start (RestoreRouteStart, ApplyCategoryAbilities, ReplaySelected). Other routes as they are.
        MacroDefinition TechPlayable(MacroDefinition route)
        {
            TechDef tech = route == null ? null : _techs.Find(t => t.routeId == route.id);
            if (tech == null) return route;
            return new MacroDefinition { id = route.id, name = (string.IsNullOrEmpty(tech.start) ? "" : tech.start + " ") + "tech " + tech.name,
                savestateId = route.savestateId, steps = route.steps };
        }

        // The tech's demo becomes `source`'s first `ticks` input ticks (all of them when 0), from `source`'s start.
        void SetTechDemo(TechDef tech, MacroDefinition source, int ticks)
        {
            List<MacroStep> played = source.steps.Where(s => s.enabled).ToList();
            if (played.Any(s => s.type != MacroStepType.RawInput))
                throw new InvalidOperationException("A demo is raw input; " + source.name + " has other actions.");
            List<SolverTick> inputs = ToTicks(played);
            if (ticks > 0 && ticks < inputs.Count) inputs.RemoveRange(ticks, inputs.Count - ticks);
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
            route.name = "tech/" + tech.name;
            route.savestateId = source.savestateId ?? "";
            route.steps = ToSteps(inputs);
            SaveLibrary();
            SaveTechs();
        }

        // After a playback: each tech whose detector it saw and that has no demo yet gets one, cut after the first time.
        void FillTechDemos()
        {
            if (_techSeenRoute == null || _techSeenFilled) return;
            _techSeenFilled = true;
            foreach (TechSpan span in _techSeen)
            {
                string detector = span.tech.ToString();
                TechDef tech = _techs.Find(t => t.detector == detector);
                if (tech == null || TechRoute(tech) != null) continue;
                try
                {
                    SetTechDemo(tech, _techSeenRoute, (int)span.input + TechDemoTailTicks);
                    Logger.LogInfo("Tech demo: " + tech.name + " from " + _techSeenRoute.name + " (input tick " + span.input + ")");
                }
                catch (InvalidOperationException ex) { Logger.LogWarning("No tech demos from this playback: " + ex.Message); return; }
            }
        }

        void RemoveRoute(MacroDefinition route)
        {
            int index = _library.macros.IndexOf(route);
            if (index < 0) return;
            _library.macros.RemoveAt(index);
            _library.selectedIndex = Math.Max(0, Math.Min(_library.selectedIndex > index ? _library.selectedIndex - 1 : _library.selectedIndex,
                _library.macros.Count - 1));
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
                    _techs.Add(new TechDef { id = id, name = name, category = category, origin = "ours" });
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
                        if (route != null) { route.name = "tech/" + name; SaveLibrary(); }
                    }
                    if (body["category"] != null) tech.category = OneOf(body, "category", TechCategories);
                    if (body["origin"] != null) tech.origin = OneOf(body, "origin", TechOrigins);
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
                        RemoveRoute(route);
                        SaveLibrary();
                    }
                    _techs.Remove(tech);
                    foreach (TechDef t in _techs) t.related.Remove(tech.id);
                    SaveTechs();
                    return new { ok = true };
                }
                // `id` goes into `into`: its text is appended, and its detector and demo fill what `into` lacks.
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
                            fromRoute.name = "tech/" + into.name;
                        }
                        else RemoveRoute(fromRoute);
                        SaveLibrary();
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
                // The demo: from the last playback, cut after one of its seen techs ("playback", `seen` its index); the
                // selected route whole ("selected"); or none ("none").
                case "/api/techs/demo":
                {
                    TechDef tech = FindTech(Text(body, "id"));
                    if (PlaybackActive) throw new InvalidOperationException("Stop playback first.");
                    string from = OneOf(body, "from", new[] { "playback", "selected", "none" });
                    if (from == "playback")
                    {
                        int seen = Number(body, "seen", -1);
                        if (_techSeenRoute == null || seen < 0 || seen >= _techSeen.Count) throw new InvalidOperationException("That playback tech is gone.");
                        SetTechDemo(tech, _techSeenRoute, (int)_techSeen[seen].input + TechDemoTailTicks);
                    }
                    else if (from == "selected")
                    {
                        MacroDefinition selected = SelectedMacro ?? throw new InvalidOperationException("Select a route first.");
                        if (selected.id == tech.routeId) throw new InvalidOperationException("The selected route is already " + tech.name + "'s demo.");
                        SetTechDemo(tech, selected, 0);
                    }
                    else
                    {
                        MacroDefinition route = TechRoute(tech);
                        if (route != null) { RemoveRoute(route); SaveLibrary(); }
                        tech.routeId = tech.start = null;
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
                    return new { t.id, t.name, t.category, t.origin, t.description, t.note, t.detector, t.related, t.start,
                        routeId = route == null ? null : route.id,
                        ticks = route == null ? 0 : route.steps.Where(s => s.enabled).Sum(s => Math.Max(1, s.durationTicks)) };
                }),
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
