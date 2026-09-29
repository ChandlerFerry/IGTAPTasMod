using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IGTAPTasMod
{
    /// <summary>
    /// Injected lab maps (labmaps/&lt;id&gt;.json): a custom, temporary piece of level built additively in empty space to
    /// demonstrate one tech exactly (root README "### Rules": lab only, never a route, restored afterwards).
    /// <c>POST /api/labmap/inject {id}</c> builds one root of colliders at its anchor (world frame through
    /// FloatingOrigin.currentOrigin), <c>POST /api/labmap/restore</c> destroys it and puts back anything the lab session
    /// touched (the player included). A tech demo whose tech carries a lab map injects, plays from the lab start (the lab
    /// savestate <c>place</c> mechanism) and, at a 1x playback's end, leaves the player in the lab's hand play at its start
    /// (Unload puts back what loading it changed); a scene change restores, and the save gate and autosave refuse while a
    /// lab map is injected.
    /// </summary>
    public sealed partial class TasPlugin
    {
        public sealed class LabShapeDef
        {
            public string kind = "box";
            public float[] at;
            public float[] size;
            public float[][] points;
            public string layer = "Ground";
            public string tag = "Ground";
            public bool trigger;
            public bool spike;
        }

        public sealed class LabStartDef
        {
            public float? x, y, vx, vy, mx, my, facing;
            public JObject fields;
        }

        public sealed class LabMapDef
        {
            public int schema = 1;
            public string id;
            public string name;
            public string tech;
            public string techNote;
            public string category = "NONE";
            public float[] anchor;
            public float[] region;
            public List<LabShapeDef> shapes = new List<LabShapeDef>();
            public LabStartDef start = new LabStartDef();
            public float[] finish;
            public int maxTicks = 90;
            /// <summary>Playing the lab by hand (Tech tab, <c>inject {place: true}</c>): extra shapes and a standing start. The solver never sees it.</summary>
            public LabHandDef hand;
            /// <summary>The input trainer shown while the lab is injected (<see cref="LabTrainer"/>): "pauselift", "final-cling-float", or none.</summary>
            public string trainer;
            /// <summary>
            /// The ghosts the lab shows (<see cref="LabGhost"/>), in order: each one of its demos' paths with a label, the first
            /// in cyan, then green, magenta, yellow and orange, all on one clock. None: hand play shows the main demo's ghost alone.
            /// A lab that lists them also shows them in its demo's playback, all but the demo being played, so the played tech runs
            /// beside what it beats (freeze-aim: the freeze aim beside three keyboard tries that don't land).
            /// </summary>
            public List<LabGhostDef> ghosts;
        }

        /// <summary>A lab's hand-play setup: a floor to stand on (<c>shapes</c>, added to the lab's own) and where you stand (<c>start</c>).</summary>
        public sealed class LabHandDef
        {
            public List<LabShapeDef> shapes = new List<LabShapeDef>();
            public LabStartDef start;
        }

        /// <summary>
        /// One of a lab's ghosts: its demo (labmaps/&lt;id&gt;.&lt;demo&gt;.demo.json; none: &lt;id&gt;.demo.json), what it shows (the
        /// on-screen key), a short badge drawn on its head ("KB": a keyboard's inputs), and the lane it runs in: <c>offset</c>
        /// [dx, dy] from the lab, where a see-through copy of the lab's shapes (no colliders) is drawn, so ghosts side by side
        /// don't overlap. No offset: the lab itself.
        /// </summary>
        public sealed class LabGhostDef
        {
            public string demo;
            public string label;
            public string badge;
            public float[] offset;
        }

        sealed class LabSession
        {
            public string Id;
            public LabMapDef Lab;
            public GameObject Root;
            public Savestate PlayerBefore;
            public string Scene;
            public bool PlaybackSeen;
            /// <summary>Built for hand play (its hand shapes are in): a demo re-injects the lab without them.</summary>
            public bool Hand;
            /// <summary>A 1x demo playback's lab: its end goes back to hand play (<see cref="ReturnToHandPlay"/>) instead of restoring.</summary>
            public bool ReturnToHand;
            /// <summary>Set when the player was put at the lab start (<c>place</c>): the unlocks, course reset point and respawn point it replaced.</summary>
            public List<(string field, object value)> SavedAbilities;
            public Vector2? CourseResetPointBefore, RespawnPointBefore;
            /// <summary>The ghosts replaying demo paths on a loop (<see cref="LabGhost"/>): hand play, and a playback of a lab with <c>ghosts</c>.</summary>
            public List<LabGhost> Ghosts = new List<LabGhost>();
            /// <summary>The ghosts' shared clock (seconds of 1x play since they were built): every ghost's loop starts over together.</summary>
            public float GhostClock;
        }

        LabSession _lab;

        /// <summary>While a lab map is injected: no autosave, and SaveSolverRoute refuses (a lab is never a route).</summary>
        internal bool LabInjected { get { return _lab != null; } }

        string LabMapsDir { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "labmaps"); } }

        /// <summary>A seeded lab map (or its solved demo) from the plugin's embedded labmaps/, as techs.json seeds itself.</summary>
        string LabMapFile(string fileName)
        {
            string path = Path.Combine(LabMapsDir, fileName);
            if (File.Exists(path)) return path;
            using (Stream seed = Assembly.GetExecutingAssembly().GetManifestResourceStream("IGTAPTasMod.labmaps." + fileName))
            {
                if (seed == null) return null;
                Directory.CreateDirectory(LabMapsDir);
                using (var reader = new StreamReader(seed)) File.WriteAllText(path, reader.ReadToEnd());
                return path;
            }
        }

        internal LabMapDef LoadLabMap(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("A lab map needs a file-safe id.");
            string path = LabMapFile(id + ".json") ?? throw new InvalidOperationException("No lab map \"" + id + "\" in " + LabMapsDir + ".");
            var lab = JsonConvert.DeserializeObject<LabMapDef>(File.ReadAllText(path));
            lab.id = string.IsNullOrEmpty(lab.id) ? id : lab.id;
            if (lab.shapes == null || lab.shapes.Count == 0) throw new InvalidOperationException("Lab map " + id + " has no shapes.");
            if (lab.anchor == null || lab.anchor.Length < 2) throw new InvalidOperationException("Lab map " + id + " has no anchor [x, y].");
            if (lab.finish == null || lab.finish.Length < 4) throw new InvalidOperationException("Lab map " + id + " has no finish [x, y, w, h].");
            return lab;
        }

        // The studio's lab list (BuildTechStaticWebState) reads every lab map and demo: parsed once per file version (path and
        // write time), the folder listed at most once a second. The web state only reads them; LoadLabMap still parses afresh
        // for an injection, which keeps what it gets.
        static readonly string[] EmbeddedLabMapIds = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(r => r.StartsWith("IGTAPTasMod.labmaps.", StringComparison.Ordinal) && r.EndsWith(".json", StringComparison.Ordinal) && !r.EndsWith(".demo.json", StringComparison.Ordinal))
            .Select(r => r.Substring("IGTAPTasMod.labmaps.".Length, r.Length - "IGTAPTasMod.labmaps.".Length - ".json".Length)).ToArray();
        List<string> _labMapIds;
        float _labMapIdsAt = float.NegativeInfinity;
        readonly Dictionary<string, (DateTime written, LabMapDef lab)> _labMapCache = new Dictionary<string, (DateTime, LabMapDef)>();
        readonly Dictionary<string, (DateTime written, JObject json)> _labJsonCache = new Dictionary<string, (DateTime, JObject)>();

        /// <summary>Every lab map's id: the config folder's files and the plugin's embedded seeds (solved demos excluded).</summary>
        internal List<string> LabMapIds()
        {
            if (_labMapIds != null && Time.unscaledTime - _labMapIdsAt < 1f) return new List<string>(_labMapIds);
            var ids = new SortedSet<string>(EmbeddedLabMapIds, StringComparer.Ordinal);
            if (Directory.Exists(LabMapsDir))
                foreach (string file in Directory.GetFiles(LabMapsDir, "*.json"))
                    if (!file.EndsWith(".demo.json", StringComparison.Ordinal)) ids.Add(Path.GetFileNameWithoutExtension(file));
            _labMapIds = ids.ToList();
            _labMapIdsAt = Time.unscaledTime;
            return new List<string>(_labMapIds);
        }

        /// <summary><see cref="LoadLabMap"/> for reading only: the same lab until its file changes.</summary>
        LabMapDef CachedLabMap(string id)
        {
            string path = string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : LabMapFile(id + ".json");
            if (path == null) return LoadLabMap(id);
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (_labMapCache.TryGetValue(path, out var cached) && cached.written == written) return cached.lab;
            LabMapDef lab = LoadLabMap(id);
            _labMapCache[path] = (written, lab);
            return lab;
        }

        /// <summary>A lab demo's JSON (labmaps/&lt;name&gt;), parsed once per version of its file; null: no such demo. For reading only.</summary>
        JObject CachedLabJson(string fileName)
        {
            string path = LabMapFile(fileName);
            if (path == null) return null;
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (_labJsonCache.TryGetValue(path, out var cached) && cached.written == written) return cached.json;
            JObject json = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path));
            _labJsonCache[path] = (written, json);
            return json;
        }

        internal string InjectedLabId { get { return _lab?.Id; } }

        object HandleLabMap(string path, JObject body)
        {
            switch (path)
            {
                case "/api/labmap/inject":
                {
                    // place: also put the player at the lab start with the lab's unlocks, to play it by hand.
                    string id = Text(body, "id");
                    bool place = body["place"] != null && (bool)body["place"];
                    if (place && PlaybackActive) throw new InvalidOperationException("Stop playback first.");
                    LabMapDef lab = LoadLabMap(id);
                    if (_lab != null) RestoreLab("another lab map");
                    InjectLab(lab, place);
                    if (place) PlaceAtLabStart();
                    return new { ok = true, id, objects = _lab.Root.transform.childCount, anchor = _lab.Lab.anchor, scene = _lab.Scene, placed = place };
                }
                case "/api/labmap/restore":
                    if (_lab == null) return new { ok = true, injected = false };
                    RestoreLab("Web Studio");
                    return new { ok = true, injected = true };
                case "/api/labmap/status":
                    return new { ok = true, injected = _lab != null, id = _lab?.Id, scene = _lab?.Scene, playing = PlaybackActive };
                case "/api/labmap/demo":
                {
                    // The solved demo of a lab map (labmaps/<id>.demo.json): its inputs, for the studio and tech-check.
                    string id = Text(body, "id");
                    string path2 = LabMapFile(id + ".demo.json") ?? throw new InvalidOperationException("No solved demo for lab map \"" + id + "\".");
                    return new { ok = true, demo = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path2)) };
                }
            }
            throw new InvalidOperationException("Unknown lab map command: " + path);
        }

        /// <summary>
        /// The lab map of a tech's demo is injected before its start is restored (RestoreRouteStart), for a 1x playback and for
        /// the tech trace's turbo run alike (which shows no ghosts). A 1x playback ends in the lab's hand play at its start
        /// (<see cref="ReturnToHandPlay"/>), not back where the lab was loaded from; one started from hand play (or from the
        /// playback before it) keeps that session's before-state, so Unload still puts back what loading the lab changed.
        /// Returns the demo's tech when it plays on a lab map (its start is then <see cref="PlaceLabDemoStart"/>), else null.
        /// </summary>
        internal TechDef LabMapEnsureFor(MacroDefinition route)
        {
            TechDef tech = TechOfRoute(route);
            if (tech == null || string.IsNullOrEmpty(tech.labmap)) return null;
            string playing = _techStart == null && !_silentEvaluation ? tech.labDemo ?? "" : null;
            LabMapDef lab = LoadLabMap(tech.labmap);
            // A tech trace's turbo run reuses a plain injection (one it left); a 1x playback always injects afresh, its ghosts'
            // clock starting with its first tick and its end going back to hand play.
            if (playing == null && _lab != null && _lab.Id == tech.labmap && !_lab.Hand && _lab.Ghosts.Count == 0) return tech;
            LabSession carried = null;
            if (playing != null && _lab != null && _lab.Id == tech.labmap && (_lab.Hand || _lab.ReturnToHand)) carried = DetachLab();
            else if (_lab != null) RestoreLab(_lab.Id != tech.labmap ? "another lab map" : _lab.Hand ? "the demo plays without the hand-play floor" : "the demo starts afresh");
            InjectLab(lab, false, playing);
            if (playing == null) return tech;
            _lab.ReturnToHand = true;
            if (carried != null) CarryLabState(carried, _lab);
            return tech;
        }

        /// <summary>
        /// A lab demo's start, built from its lab file each time (no stored savestate, which a game update's new assembly hash
        /// rejects, and which kept whatever state the player had when the demo was installed): the player's Movement reset to
        /// the start the demo was solved from (<see cref="ResetToLabBase"/>), then placed at the demo's own start (else the lab's)
        /// with its fields, as `lab replay` places the offline one over that trace's start.
        /// </summary>
        void PlaceLabDemoStart(TechDef tech, Movement player)
        {
            LabMapDef lab = _lab?.Lab ?? LoadLabMap(tech.labmap);
            string demoFile = tech.labmap + (string.IsNullOrEmpty(tech.labDemo) ? "" : "." + tech.labDemo) + ".demo.json";
            JObject demo = CachedLabJson(demoFile) ?? throw new InvalidOperationException("Lab map " + tech.labmap + " has no demo " + demoFile + ".");
            LabStartDef start = demo["start"] is JObject own ? own.ToObject<LabStartDef>() : lab.start;
            ResetToLabBase(player, (string)demo["solvedIn"]);
            SavestateResult placed = SavestateService.Restore(player, PlacedSavestate(LabStartBody(lab, start)), MacroLibrary.CurrentSchemaVersion);
            if (!placed.Success) throw new InvalidOperationException("Lab start: " + placed.Message);
        }

        /// <summary>
        /// Movement fields a lab start's reset keeps from the live player: the save's (Movement.save: the respawn point, the
        /// light) and the session's (course reset point, checkpoint respawns, the menu, pause and init flags, the block swap's
        /// respawn colour).
        /// </summary>
        static readonly HashSet<string> LabBaseKept = new HashSet<string> { "respawnPoint", "courseResetPoint", "isRespawningAtCheckpoints",
            "lightActive", "lightRadius", "init", "isOnMainMenu", "GamePaused", "DEBUG_startingCourse", "isBlueActiveOnRespawn" };

        /// <summary>The start trace a lab demo is solved from when its demo names none (`lab solve`'s usual one).</summary>
        const string LabBaseTrace = "start-c1-none.json";

        readonly Dictionary<string, (DateTime written, List<(FieldInfo field, object value)> fields, JArray colliders)> _labBases =
            new Dictionary<string, (DateTime, List<(FieldInfo, object)>, JArray)>();

        /// <summary>
        /// Every value-type Movement field (and the hitbox colliders) set to a start trace's (enginesim/&lt;trace&gt;, the one the
        /// lab demo was solved from: its `solvedIn`), all but <see cref="LabBaseKept"/>: no leftover of earlier play (a
        /// wallCoyoteFrames, a dash's timers) reaches the lab start, and the in-game start is the offline one. Object references
        /// and lists stay the live player's.
        /// </summary>
        void ResetToLabBase(Movement player, string trace)
        {
            string path = Path.Combine(EngineSimDir, string.IsNullOrEmpty(trace) ? LabBaseTrace : trace);
            if (!File.Exists(path))
                throw new InvalidOperationException("A lab start is placed over the start trace its demo was solved from, and " + path
                    + " is missing: capture it (tools/enginesim-capture.py start 1 NONE).");
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (!_labBases.TryGetValue(path, out var based) || based.written != written)
            {
                JObject start;
                using (var reader = new JsonTextReader(new StreamReader(path)) { MaxDepth = 256 })
                    start = (JObject)JObject.Load(reader)["start"] ?? throw new InvalidOperationException(path + " has no start.");
                var fields = new List<(FieldInfo, object)>();
                foreach (JArray entry in start["movement"].OfType<JArray>())
                {
                    if ((string)entry[0] != "Movement" || LabBaseKept.Contains((string)entry[1])) continue;
                    FieldInfo field = AccessTools.Field(typeof(Movement), (string)entry[1]);
                    if (field == null || field.IsStatic || field.IsLiteral || field.IsInitOnly) continue;
                    if (LabBaseValue(field.FieldType, entry[2], out object value)) fields.Add((field, value));
                }
                based = (written, fields, start["colliders"] as JArray);
                _labBases[path] = based;
            }
            foreach (var (field, value) in based.fields) field.SetValue(player, value);
            BoxCollider2D[] colliders = player.GetComponents<BoxCollider2D>();
            if (based.colliders != null)
                for (int i = 0; i < colliders.Length && i < based.colliders.Count; i++)
                {
                    JArray c = (JArray)based.colliders[i];
                    colliders[i].offset = new Vector2((float)c[0], (float)c[1]);
                    colliders[i].size = new Vector2((float)c[2], (float)c[3]);
                    colliders[i].excludeLayers = (int)c[4];
                    colliders[i].enabled = (bool)c[5];
                }
        }

        /// <summary>A start trace's field value (EngineSimJson: numbers, "Infinity"/"NaN" strings, {x, y[, z]} vectors, enums as
        /// numbers) as the field's type; false for what a trace can't give back (object references, lists, nulls).</summary>
        static bool LabBaseValue(Type type, JToken token, out object value)
        {
            value = null;
            if (token == null || token.Type == JTokenType.Null) return false;
            if (type == typeof(float)) { value = token.Type == JTokenType.String ? float.Parse((string)token, CultureInfo.InvariantCulture) : (float)token; return true; }
            if (type == typeof(double)) { value = token.Type == JTokenType.String ? double.Parse((string)token, CultureInfo.InvariantCulture) : (double)token; return true; }
            if (type == typeof(int)) { value = (int)token; return true; }
            if (type == typeof(bool)) { value = (bool)token; return true; }
            if (type.IsEnum) { value = Enum.ToObject(type, (int)token); return true; }
            if (token is JObject v && v["$ref"] == null)
            {
                if (type == typeof(Vector2)) { value = new Vector2((float)v["x"], (float)v["y"]); return true; }
                if (type == typeof(Vector3)) { value = new Vector3((float)v["x"], (float)v["y"], (float?)v["z"] ?? 0f); return true; }
            }
            return false;
        }

        /// <summary>Removes the injected lab without putting anything back (the session goes on in another injection).</summary>
        LabSession DetachLab()
        {
            LabSession lab = _lab;
            _lab = null;
            if (lab.Root != null) DestroyImmediate(lab.Root);
            return lab;
        }

        /// <summary>What a lab session restores on Unload (the player's state before the lab, the unlocks, the course reset and
        /// respawn points), handed to the session that replaces it.</summary>
        static void CarryLabState(LabSession from, LabSession to)
        {
            to.PlayerBefore = from.PlayerBefore;
            to.SavedAbilities = from.SavedAbilities;
            to.CourseResetPointBefore = from.CourseResetPointBefore;
            to.RespawnPointBefore = from.RespawnPointBefore;
        }

        /// <summary>
        /// A demo's playback is over: the lab goes back to hand play (its hand floor and ghosts) with the player at its start and
        /// its unlocks, ready to try the tech; Unload then puts back what loading the lab (or starting the demo) changed.
        /// </summary>
        void ReturnToHandPlay()
        {
            LabSession demo = DetachLab();
            InjectLab(demo.Lab, true);
            CarryLabState(demo, _lab);
            PlaceAtLabStart();
            Logger.LogInfo("Lab map " + demo.Id + ": the demo ended, back to hand play at its start.");
        }

        /// <param name="hand">Hand play: the lab's <c>hand.shapes</c> go in too (a floor to stand on); a demo's and the solver's lab never has them.</param>
        /// <param name="playing">The lab demo a 1x playback plays ("" the main one), whose ghost the others run beside; null: none.</param>
        void InjectLab(LabMapDef lab, bool hand, string playing = null)
        {
            FindLabSortingCeiling();
            Movement player = FindWorldMovement();
            Vector2 origin = CurrentOrigin();
            (float ax, float ay) = (lab.anchor[0], lab.anchor[1]);
            var root = new GameObject("LabMap " + lab.id);
            root.transform.position = new Vector3(ax + origin.x, ay + origin.y, 0f);
            List<LabShapeDef> shapes = hand && lab.hand != null ? lab.shapes.Concat(lab.hand.shapes ?? new List<LabShapeDef>()).ToList() : lab.shapes;
            for (int i = 0; i < shapes.Count; i++)
            {
                LabShapeDef shape = shapes[i];
                var go = new GameObject(lab.id + " " + (i + 1) + " " + shape.kind);
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3(shape.at != null ? shape.at[0] : 0f, shape.at != null ? shape.at[1] : 0f, 0f);
                go.layer = LayerNumber(shape.layer);
                try { go.tag = string.IsNullOrEmpty(shape.tag) ? "Untagged" : shape.tag; }
                catch (Exception) { go.tag = "Untagged"; }
                switch (shape.kind)
                {
                    case "box":
                    {
                        var box = go.AddComponent<BoxCollider2D>();
                        box.size = new Vector2(shape.size[0], shape.size[1]);
                        box.isTrigger = shape.trigger;
                        break;
                    }
                    case "polygon":
                    {
                        var poly = go.AddComponent<PolygonCollider2D>();
                        poly.points = shape.points.Select(p => new Vector2(p[0], p[1])).ToArray();
                        poly.isTrigger = shape.trigger;
                        break;
                    }
                    case "edge":
                    {
                        var edge = go.AddComponent<EdgeCollider2D>();
                        edge.points = shape.points.Select(p => new Vector2(p[0], p[1])).ToArray();
                        edge.isTrigger = shape.trigger;
                        break;
                    }
                    default: throw new InvalidOperationException("A lab shape's kind is box, polygon or edge, not " + shape.kind + ".");
                }
                if (shape.spike) go.AddComponent<spikeScript>();
                LabShapeVisual(go.transform, shape, 1f);
            }
            _lab = new LabSession
            {
                Id = lab.id, Lab = lab, Root = root, Hand = hand,
                PlayerBefore = player != null ? SavestateService.Capture(player, "lab/" + lab.id + " before", MacroLibrary.CurrentSchemaVersion) : null,
                Scene = SceneManager.GetActiveScene().name,
            };
            if (player != null && (hand || playing != null)) BuildLabGhosts(lab, player, hand ? null : playing);
            Logger.LogInfo("Lab map " + lab.id + " injected at " + ax.ToString("0.#", CultureInfo.InvariantCulture) + "," + ay.ToString("0.#", CultureInfo.InvariantCulture)
                + " (" + shapes.Count + " shapes" + (hand ? ", for hand play" : "") + (_lab.Ghosts.Count > 0 ? ", " + _lab.Ghosts.Count + " ghost" + (_lab.Ghosts.Count == 1 ? "" : "s") : "")
                + "); autosave and the route gate are off until it is restored.");
        }

        static int LayerNumber(string layer)
        {
            if (int.TryParse(layer, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) return number;
            int named = LayerMask.NameToLayer(layer);
            if (named < 0) throw new InvalidOperationException("No Unity layer named " + layer + ".");
            return named;
        }

        static Material _labMaterial;
        static Sprite _labPixel;

        /// <summary>An unlit sprite material, so the lab stays visible with the game's 2D lights off (the breaker).</summary>
        static Material LabMaterial()
        {
            if (_labMaterial != null) return _labMaterial;
            Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
            if (shader != null) return _labMaterial = new Material(shader);
            SpriteRenderer any = FindAnyObjectByType<SpriteRenderer>();
            return _labMaterial = any != null ? any.sharedMaterial : null;
        }

        /// <summary>Sorting ranks under the lab's ceiling (<see cref="FindLabSortingCeiling"/>): ghosts on top, then the lab's shapes and
        /// paths, then the ghost lanes' see-through copies.</summary>
        const int LabGhostRank = 0, LabShapeRank = 1, LabLaneRank = 10;

        static int _labSortingLayer, _labSortingTop = 32000;

        /// <summary>
        /// Where the lab draws: above the level, below the game's own menus. The pause menu (pauseMenuScript, the settings
        /// screens under it) and the settings (SettingsScript) are Screen Space - Camera canvases, sorted with the sprites
        /// (pauseMenu: layer Default, order 10001), so the lab draws just under the lowest of them; with none, on the top
        /// layer's top order. Found at each injection, since a scene brings its own menu.
        /// </summary>
        static void FindLabSortingCeiling()
        {
            SortingLayer[] layers = SortingLayer.layers;
            _labSortingLayer = layers.Length > 0 ? layers[layers.Length - 1].id : 0;
            _labSortingTop = 32000;
            var menus = Resources.FindObjectsOfTypeAll<pauseMenuScript>().Select(m => m.gameObject)
                .Concat(Resources.FindObjectsOfTypeAll<SettingsScript>().Select(s => s.gameObject))
                .Where(go => go.scene.IsValid());
            int best = int.MaxValue;
            foreach (GameObject menu in menus)
                foreach (Canvas canvas in menu.GetComponentsInChildren<Canvas>(true).Concat(menu.GetComponentsInParent<Canvas>(true)))
                {
                    if (!(canvas.isRootCanvas || canvas.overrideSorting) || canvas.renderMode == RenderMode.ScreenSpaceOverlay) continue;
                    int rank = SortingLayer.GetLayerValueFromID(canvas.sortingLayerID) * 65536 + canvas.sortingOrder;
                    if (rank >= best) continue;
                    best = rank;
                    _labSortingLayer = canvas.sortingLayerID;
                    _labSortingTop = Mathf.Max(canvas.sortingOrder - 1, short.MinValue + LabLaneRank);
                }
        }

        /// <summary>Drawn above the level and under the game's menus, at <paramref name="rank"/> below the lab's ceiling.</summary>
        static void LabSorting(Renderer renderer, int rank = LabShapeRank)
        {
            renderer.sortingLayerID = _labSortingLayer;
            renderer.sortingOrder = _labSortingTop - rank;
        }

        /// <summary>
        /// A lab shape as drawn in game, under <paramref name="parent"/> (at the shape's own position): ground dark with a cyan
        /// outline, spikes red, triggers purple, moss green; <paramref name="alpha"/> below 1 for a ghost lane's see-through copy.
        /// </summary>
        static void LabShapeVisual(Transform parent, LabShapeDef shape, float alpha)
        {
            Color color = shape.spike || shape.layer == "Spikes" ? new Color(0.75f, 0.22f, 0.17f, alpha) : shape.trigger ? new Color(0.56f, 0.27f, 0.68f, alpha)
                : shape.tag == "Moss" ? new Color(0.15f, 0.68f, 0.38f, alpha) : new Color(0.10f, 0.14f, 0.20f, alpha);
            if (shape.kind == "box")
            {
                float w = shape.size[0] / 2f, h = shape.size[1] / 2f;
                LabBox(parent, Vector2.zero, new Vector2(shape.size[0], shape.size[1]), color);
                LabLine(parent, new[] { new Vector2(-w, -h), new Vector2(w, -h), new Vector2(w, h), new Vector2(-w, h) }, true, new Color(0.3f, 0.85f, 1f, alpha), 3f);
            }
            else LabLine(parent, shape.points.Select(p => new Vector2(p[0], p[1])).ToArray(), shape.kind == "polygon", color, 6f);
        }

        /// <summary>A filled box (a scaled 1-unit sprite) as a child of <paramref name="parent"/>, centred at <paramref name="at"/>.</summary>
        static void LabBox(Transform parent, Vector2 at, Vector2 size, Color color)
        {
            if (_labPixel == null)
            {
                var texture = new Texture2D(1, 1) { filterMode = FilterMode.Point };
                texture.SetPixel(0, 0, Color.white);
                texture.Apply();
                _labPixel = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            }
            var go = new GameObject("visual");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var sprite = go.AddComponent<SpriteRenderer>();
            sprite.sprite = _labPixel;
            sprite.color = color;
            Material material = LabMaterial();
            if (material != null) sprite.sharedMaterial = material;
            LabSorting(sprite);
        }

        /// <summary>A line through <paramref name="points"/> (local to <paramref name="parent"/>), closed when <paramref name="loop"/>.</summary>
        static void LabLine(Transform parent, Vector2[] points, bool loop, Color color, float width)
        {
            var go = new GameObject("visual");
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = loop;
            line.positionCount = points.Length;
            line.SetPositions(points.Select(p => new Vector3(p.x, p.y, 0f)).ToArray());
            line.startWidth = line.endWidth = width;
            line.startColor = line.endColor = color;
            Material material = LabMaterial();
            if (material != null) line.sharedMaterial = material;
            LabSorting(line);
        }

        /// <summary>
        /// Puts the player at the injected lab's start to play it by hand (its <c>hand.start</c>, standing, when it has one): the
        /// Movement reset to the start trace its main demo was solved from (<see cref="ResetToLabBase"/>), the lab category's
        /// unlocks (put back on restore, never autosaved while the lab is injected), then that start as a lab savestate restored
        /// in place (not kept on disk). A death or quick restart respawns there too until the lab is restored.
        /// </summary>
        void PlaceAtLabStart()
        {
            Movement player = FindWorldMovement() ?? throw new InvalidOperationException("No player in the world.");
            LabMapDef lab = _lab.Lab;
            CategoryDef category = Categories.Find(lab.category ?? default(Caps).Label);
            if (category == null) throw new InvalidOperationException("Lab map " + lab.id + " has an unknown category " + lab.category + ".");
            LabStartDef at = lab.hand?.start ?? lab.start;
            _engine.ClearInjectedInput(player);
            if (_lab.SavedAbilities == null) _lab.SavedAbilities = ApplyAbilities(player, category.Id);
            ResetToLabBase(player, (string)CachedLabJson(lab.id + ".demo.json")?["solvedIn"]);
            ApplyAbilities(player, category.Id);
            if (_lab.CourseResetPointBefore == null) _lab.CourseResetPointBefore = player.courseResetPoint;
            if (_lab.RespawnPointBefore == null) _lab.RespawnPointBefore = player.respawnPoint;
            Savestate start = PlacedSavestate(LabStartBody(lab, at));
            SavestateResult restored = SavestateService.Restore(player, start, MacroLibrary.CurrentSchemaVersion);
            if (!restored.Success) throw new InvalidOperationException("Lab start: " + restored.Message);
            LeaveCourses(player);
            // Movement.respawn puts the dot at respawnPoint (world frame) 12 units down.
            player.respawnPoint = new Vector2(lab.anchor[0] + (at.x ?? 0f), lab.anchor[1] + (at.y ?? 0f) + 12f);
            Physics2D.SyncTransforms();
            SnapCameraAtPlaybackStart(player);
            Logger.LogInfo("Player placed at lab " + lab.id + "'s " + (lab.hand?.start != null ? "hand-play start" : "start") + " with " + category.Label + " unlocks.");
        }

        /// <summary>A lab start as a <c>/api/savestates/place</c> body: the start relative to the lab's anchor, in the world frame.</summary>
        static JObject LabStartBody(LabMapDef lab, LabStartDef start)
        {
            return new JObject
            {
                ["name"] = "labmap-" + lab.id,
                ["x"] = lab.anchor[0] + (start.x ?? 0f),
                ["y"] = lab.anchor[1] + (start.y ?? 0f),
                ["vx"] = start.vx ?? 0f,
                ["vy"] = start.vy ?? 0f,
                ["facing"] = start.facing ?? 1f,
                ["fields"] = start.fields ?? new JObject(),
            };
        }

        /// <summary>Destroys the injected lab and puts back anything its session touched (the player's state, unlocks, course reset and respawn points).</summary>
        void RestoreLab(string why)
        {
            if (_lab == null) return;
            LabSession lab = _lab;
            _lab = null;
            if (lab.Root != null) DestroyImmediate(lab.Root);
            Movement player = FindWorldMovement();
            if (player != null)
            {
                if (lab.SavedAbilities != null)
                    foreach (var (field, value) in lab.SavedAbilities) SetMovementField(player, field, value);
                if (lab.CourseResetPointBefore != null) player.courseResetPoint = lab.CourseResetPointBefore.Value;
                if (lab.RespawnPointBefore != null) player.respawnPoint = lab.RespawnPointBefore.Value;
                if (lab.PlayerBefore != null)
                {
                    SavestateService.Restore(player, lab.PlayerBefore, MacroLibrary.CurrentSchemaVersion);
                    if (lab.SavedAbilities != null) { Physics2D.SyncTransforms(); SnapCameraAtPlaybackStart(player); }
                }
            }
            Logger.LogInfo("Lab map " + lab.Id + " restored (" + why + ").");
        }

        /// <summary>Restores the lab map on a scene change or at a playback's end (plugin Update).</summary>
        void PumpLabMap()
        {
            if (_lab == null) return;
            if (_lab.Ghosts.Count > 0) AnimateLabGhosts();
            if (!string.Equals(_lab.Scene, SceneManager.GetActiveScene().name, StringComparison.Ordinal))
            {
                RestoreLab("scene change");
                return;
            }
            bool busy = PlaybackActive || _pendingReplay != null || _pendingSilentMacro != null || TurboRunner.Active || _engine.IsRunning || _recorder.IsRecording;
            if (busy) { _lab.PlaybackSeen = true; return; }
            if (!_lab.PlaybackSeen) return;
            if (_lab.ReturnToHand) ReturnToHandPlay();
            else RestoreLab("playback end");
        }

        /// <summary>
        /// Installs a tech's lab demo (labmaps/&lt;id&gt;.demo.json's inputs, or labmaps/&lt;id&gt;.&lt;labDemo&gt;.demo.json for one of
        /// the lab's other demos) as its demo route. The route has no savestate: every playback builds its start from the lab
        /// (<see cref="PlaceLabDemoStart"/>: the demo's own <c>start</c> when it has one, else the lab's).
        /// </summary>
        void InstallLabDemo(TechDef tech, JObject body)
        {
            string id = Text(body, "labmap");
            if (id.Length == 0) id = tech.labmap ?? "";
            string labDemo = body["labDemo"] != null ? Text(body, "labDemo") : id == tech.labmap ? tech.labDemo ?? "" : "";
            LabMapDef lab = LoadLabMap(id);
            string demoFile = id + (labDemo.Length > 0 ? "." + labDemo : "") + ".demo.json";
            string demoPath = LabMapFile(demoFile) ?? throw new InvalidOperationException("Lab map " + id + " has no demo " + demoFile + ".");
            JObject demo = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(demoPath));
            List<SolverTick> ticks = demo["inputs"].ToObject<List<SolverTick>>();
            if (ticks == null || ticks.Count == 0) throw new InvalidOperationException("Lab map " + id + "'s demo has no inputs.");
            string start = Text(body, "start");
            if (start.Length == 0) start = tech.start ?? "";
            if (MatchTechStart(start).Length != start.Length || start.Length == 0)
                throw new InvalidOperationException("start is a course category prefix (1_WJ_DS) matching the lab map's category " + lab.category + ".");
            if (demo["start"] != null && !(demo["start"] is JObject)) throw new InvalidOperationException(demoFile + ": start is an object (the lab start's format).");
            tech.labmap = id;
            tech.labDemo = labDemo.Length > 0 ? labDemo : null;
            tech.start = start;
            MacroDefinition route = TechRoute(tech);
            if (route == null)
            {
                route = new MacroDefinition();
                _library.macros.Add(route);
                tech.routeId = route.id;
            }
            string replaced = route.savestateId;
            route.name = "tech/" + tech.name;
            route.savestateId = "";
            route.steps = ToSteps(ticks);
            SaveLibrary();
            DropSavestate(replaced);
            SaveTechs();
            Logger.LogInfo("Lab demo " + id + " installed for " + tech.name + ": " + ticks.Count + " input ticks from lab/" + id + ".");
        }
    }
}
