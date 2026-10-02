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
    /// touched (the player included). A tech's lab demo (the Tech tab's Play, <see cref="PlayTechLab"/>) injects, plays from
    /// the lab start (the lab savestate <c>place</c> mechanism) and, at a 1x playback's end, leaves the player in the lab's
    /// hand play at its start (Unload puts back what loading it changed); a scene change restores, and the save gate and
    /// autosave refuse while a lab map is injected.
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
            /// <summary>A box's rotation about z in degrees, counter-clockwise: its collider turns with it, and a spring's
            /// transform.up (180 launches down).</summary>
            public float rotation;
            /// <summary>The game's SpringScript with these serialized fields (the shape is its trigger).</summary>
            public LabSpringDef spring;
            /// <summary>A coloured block ("blue" or "orange"): on only while its colour is the active one; swapBlocks toggles it
            /// with the scene's coloured tilemaps (<see cref="AfterSwapBlocks"/>), as the offline engine does (LabMap.Inject).</summary>
            public string colour;

            /// <summary>The shape object's rotation, the same float quaternion as the offline engine's (LabShape.Rotation).</summary>
            public Quaternion Rotation()
            {
                if (rotation == 0f) return Quaternion.identity;
                double half = rotation * Math.PI / 360.0;
                return new Quaternion(0f, 0f, (float)Math.Sin(half), (float)Math.Cos(half));
            }
        }

        /// <summary>A lab spring's SpringScript fields (the Course 5 ceiling spring's: upForce 70, strength 300, movementLock 0.25).</summary>
        public sealed class LabSpringDef
        {
            public float upForce = 70f;
            public float strength;
            public float movementLock = 0.25f;
        }

        public sealed class LabStartDef
        {
            public float? x, y, vx, vy, mx, my, facing;
            /// <summary>The coloured blocks' state at the start (colouredBlockSwapper.isBlueActive); null keeps the game's.</summary>
            public bool? blueActive;
            public JObject fields;
        }

        public sealed class LabMapDef
        {
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

        /// <summary>Lab files already checked against the plugin's embedded copy this session (<see cref="LabMapFile"/>).</summary>
        readonly HashSet<string> _labFilesSeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A lab map (or one of its solved demos) in the config's labmaps/. The plugin's embedded labmaps/ (the repo's, built in)
        /// are the source of truth: the first time a session asks for a file it has, a missing or different config copy is
        /// written over, so a redeploy brings every repo lab and demo in. A file only the config has is left as it is.
        /// </summary>
        string LabMapFile(string fileName)
        {
            string path = Path.Combine(LabMapsDir, fileName);
            if (!_labFilesSeeded.Add(fileName)) return File.Exists(path) ? path : null;
            string seeded;
            using (Stream seed = Assembly.GetExecutingAssembly().GetManifestResourceStream("IGTAPTasMod.labmaps." + fileName))
            {
                if (seed == null) return File.Exists(path) ? path : null;
                using (var reader = new StreamReader(seed)) seeded = reader.ReadToEnd();
            }
            if (File.Exists(path) && File.ReadAllText(path) == seeded) return path;
            Directory.CreateDirectory(LabMapsDir);
            if (File.Exists(path)) Logger.LogInfo("labmaps/" + fileName + ": the config copy differs from the plugin's; replaced with the plugin's.");
            File.WriteAllText(path + ".tmp", seeded);
            ReplaceAtomic(path + ".tmp", path);
            return path;
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
            foreach (LabShapeDef shape in lab.shapes.Concat(lab.hand?.shapes ?? new List<LabShapeDef>()))
            {
                if (shape.rotation != 0f && shape.kind != "box") throw new InvalidOperationException("Lab map " + id + ": only a box has a rotation.");
                if (shape.spring != null && !shape.trigger) throw new InvalidOperationException("Lab map " + id + ": a spring is a trigger (SpringScript acts on OnTriggerStay2D).");
                if (shape.colour != null && shape.colour != "blue" && shape.colour != "orange") throw new InvalidOperationException("Lab map " + id + ": a shape's colour is blue or orange, not " + shape.colour + ".");
            }
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
        readonly CachedFiles<LabMapDef> _labMapCache = new CachedFiles<LabMapDef>();
        readonly CachedFiles<JObject> _labJsonCache = new CachedFiles<JObject>();

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
            return path == null ? LoadLabMap(id) : _labMapCache.Get(path, _ => LoadLabMap(id));
        }

        /// <summary>A lab demo's JSON (labmaps/&lt;name&gt;), parsed once per version of its file; null: no such demo. For reading only.</summary>
        JObject CachedLabJson(string fileName)
        {
            string path = LabMapFile(fileName);
            return path == null ? null : _labJsonCache.Get(path, p => JsonConvert.DeserializeObject<JObject>(File.ReadAllText(p)));
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
                    bool place = Truthy(body, "place");
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
            SavestateResult placed = SavestateService.Restore(player, PlacedSavestate(LabStartBody(lab, start)));
            if (!placed.Success) throw new InvalidOperationException("Lab start: " + placed.Message);
            ApplyLabBlocks(player, start);
        }

        /// <summary>The lab start's coloured blocks (<c>blueActive</c>): the game's own swap, which the lab's coloured shapes follow.</summary>
        static void ApplyLabBlocks(Movement player, LabStartDef start)
        {
            if (start.blueActive is bool blue) SetMovementField(player, BlueActiveField, blue);
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

        readonly CachedFiles<(List<(FieldInfo field, object value)> fields, JArray colliders)> _labBases =
            new CachedFiles<(List<(FieldInfo, object)>, JArray)>();

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
            var based = _labBases.Get(path, _ =>
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
                return (fields, start["colliders"] as JArray);
            });
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
            LabColours.Clear();
            colouredBlockSwapper swapper = FindAnyObjectByType<colouredBlockSwapper>(FindObjectsInactive.Include);
            bool blueActive = swapper == null || swapper.isBlueActive;
            if (shapes.Any(s => s.colour != null)) PatchLabColours();
            for (int i = 0; i < shapes.Count; i++)
            {
                LabShapeDef shape = shapes[i];
                var go = new GameObject(lab.id + " " + (i + 1) + " " + shape.kind);
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3(shape.at != null ? shape.at[0] : 0f, shape.at != null ? shape.at[1] : 0f, 0f);
                go.transform.localRotation = shape.Rotation();
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
                if (shape.colour != null)
                {
                    Collider2D collider = go.GetComponent<Collider2D>();
                    bool blue = shape.colour == "blue";
                    LabColours.Add((collider, blue));
                    collider.enabled = blue == blueActive;
                }
                if (shape.spike) go.AddComponent<spikeScript>();
                if (shape.spring != null)
                {
                    // The game's own spring, its serialized fields as a scene spring carries them (no Animator: OnTriggerStay2D
                    // skips the animation and the sound).
                    var spring = go.AddComponent<SpringScript>();
                    LabSpringUpForce.SetValue(spring, shape.spring.upForce);
                    LabSpringStrength.SetValue(spring, shape.spring.strength);
                    LabSpringMovementLock.SetValue(spring, shape.spring.movementLock);
                }
                LabShapeVisual(go.transform, shape, 1f);
            }
            _lab = new LabSession
            {
                Id = lab.id, Lab = lab, Root = root, Hand = hand,
                PlayerBefore = player != null ? SavestateService.Capture(player, "lab/" + lab.id + " before") : null,
                Scene = SceneManager.GetActiveScene().name,
            };
            if (player != null && (hand || playing != null)) BuildLabGhosts(lab, player, hand ? null : playing);
            Logger.LogInfo("Lab map " + lab.id + " injected at " + ax.ToString("0.#", CultureInfo.InvariantCulture) + "," + ay.ToString("0.#", CultureInfo.InvariantCulture)
                + " (" + shapes.Count + " shapes" + (hand ? ", for hand play" : "") + (_lab.Ghosts.Count > 0 ? ", " + _lab.Ghosts.Count + " ghost" + (_lab.Ghosts.Count == 1 ? "" : "s") : "")
                + "); autosave and the route gate are off until it is restored.");
        }

        /// <summary>The injected lab's coloured shapes' colliders and colours (blue: true).</summary>
        static readonly List<(Collider2D collider, bool blue)> LabColours = new List<(Collider2D, bool)>();
        bool _labColoursPatched;

        /// <summary>swapBlocks's postfix, patched when a lab with coloured shapes is first injected.</summary>
        void PatchLabColours()
        {
            if (_labColoursPatched) return;
            _labColoursPatched = true;
            _harmony.Patch(AccessTools.Method(typeof(colouredBlockSwapper), "swapBlocks"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TasPlugin), nameof(AfterSwapBlocks))));
        }

        /// <summary>A block swap toggles the lab's coloured shapes with the scene's coloured tilemaps.</summary>
        static void AfterSwapBlocks(bool BlueActive)
        {
            foreach (var (collider, blue) in LabColours)
                if (collider != null) collider.enabled = blue == BlueActive;
        }

        static readonly FieldInfo LabSpringUpForce = AccessTools.Field(typeof(SpringScript), "upForce"),
            LabSpringStrength = AccessTools.Field(typeof(SpringScript), "strength"),
            LabSpringMovementLock = AccessTools.Field(typeof(SpringScript), "movementLock");

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
        /// A lab shape as drawn in game, under <paramref name="parent"/> (at the shape's own position and rotation): ground dark
        /// with a cyan outline, spikes red, springs yellow with an arrow along their launch (transform.up), other triggers
        /// purple, moss green; <paramref name="alpha"/> below 1 for a ghost lane's see-through copy.
        /// </summary>
        static void LabShapeVisual(Transform parent, LabShapeDef shape, float alpha)
        {
            Color color = shape.spike || shape.layer == "Spikes" ? new Color(0.75f, 0.22f, 0.17f, alpha) : shape.spring != null ? new Color(1f, 0.84f, 0.31f, alpha)
                : shape.colour == "blue" ? new Color(0.20f, 0.45f, 0.95f, alpha) : shape.colour == "orange" ? new Color(0.95f, 0.55f, 0.15f, alpha)
                : shape.trigger ? new Color(0.56f, 0.27f, 0.68f, alpha) : shape.tag == "Moss" ? new Color(0.15f, 0.68f, 0.38f, alpha) : new Color(0.10f, 0.14f, 0.20f, alpha);
            if (shape.kind == "box")
            {
                float w = shape.size[0] / 2f, h = shape.size[1] / 2f;
                LabBox(parent, Vector2.zero, new Vector2(shape.size[0], shape.size[1]), color);
                LabLine(parent, new[] { new Vector2(-w, -h), new Vector2(w, -h), new Vector2(w, h), new Vector2(-w, h) }, true, new Color(0.3f, 0.85f, 1f, alpha), 3f);
                if (shape.spring != null)
                {
                    // The launch direction, local up (the parent carries the rotation): out of the far side, in the spring's colour.
                    float tip = h + 40f, head = Mathf.Min(w, 16f);
                    LabLine(parent, new[] { new Vector2(0f, h), new Vector2(0f, tip) }, false, color, 6f);
                    LabLine(parent, new[] { new Vector2(-head, tip - head), new Vector2(0f, tip), new Vector2(head, tip - head) }, false, color, 6f);
                }
            }
            else LabLine(parent, shape.points.Select(p => new Vector2(p[0], p[1])).ToArray(), shape.kind == "polygon", color, 6f);
        }

        /// <summary>A filled box (a scaled 1-unit sprite) as a child of <paramref name="parent"/>, centred at <paramref name="at"/>.</summary>
        static SpriteRenderer LabBox(Transform parent, Vector2 at, Vector2 size, Color color, Sprite image = null)
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
            sprite.sprite = image != null ? image : _labPixel;
            sprite.color = color;
            Material material = LabMaterial();
            if (material != null) sprite.sharedMaterial = material;
            LabSorting(sprite);
            return sprite;
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
            if (_lab.SavedAbilities == null) _lab.SavedAbilities = ApplyAbilities(player, category.Id, null);
            ResetToLabBase(player, (string)CachedLabJson(lab.id + ".demo.json")?["solvedIn"]);
            ApplyAbilities(player, category.Id, null);
            if (_lab.CourseResetPointBefore == null) _lab.CourseResetPointBefore = player.courseResetPoint;
            if (_lab.RespawnPointBefore == null) _lab.RespawnPointBefore = player.respawnPoint;
            Savestate start = PlacedSavestate(LabStartBody(lab, at));
            SavestateResult restored = SavestateService.Restore(player, start);
            if (!restored.Success) throw new InvalidOperationException("Lab start: " + restored.Message);
            ApplyLabBlocks(player, at);
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
            LabColours.Clear();
            Movement player = FindWorldMovement();
            if (player != null)
            {
                if (lab.SavedAbilities != null)
                    foreach (var (field, value) in lab.SavedAbilities) SetMovementField(player, field, value);
                if (lab.CourseResetPointBefore != null) player.courseResetPoint = lab.CourseResetPointBefore.Value;
                if (lab.RespawnPointBefore != null) player.respawnPoint = lab.RespawnPointBefore.Value;
                if (lab.PlayerBefore != null)
                {
                    SavestateService.Restore(player, lab.PlayerBefore);
                    if (lab.SavedAbilities != null) { Physics2D.SyncTransforms(); SnapCameraAtPlaybackStart(player); }
                }
            }
            Logger.LogInfo("Lab map " + lab.Id + " restored (" + why + ").");
        }

        // The active scene's name by its handle: Scene.name makes a new string on every call, and PumpLabMap compares it every
        // frame. A loaded scene keeps its handle and name; another scene (a reload of the same one too) has another handle.
        int _labSceneHandle;
        string _labSceneName;

        string ActiveSceneName()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (_labSceneName == null || scene.handle != _labSceneHandle)
            {
                _labSceneHandle = scene.handle;
                _labSceneName = scene.name;
            }
            return _labSceneName;
        }

        /// <summary>Restores the lab map on a scene change or at a playback's end (plugin Update).</summary>
        void PumpLabMap()
        {
            if (_lab == null) return;
            if (_lab.Ghosts.Count > 0) AnimateLabGhosts();
            if (!string.Equals(_lab.Scene, ActiveSceneName(), StringComparison.Ordinal))
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
    }
}
