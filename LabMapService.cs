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
    /// savestate <c>place</c> mechanism) and restores at playback end; a scene change restores too, and the save gate and
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
        }

        /// <summary>A lab's hand-play setup: a floor to stand on (<c>shapes</c>, added to the lab's own) and where you stand (<c>start</c>).</summary>
        public sealed class LabHandDef
        {
            public List<LabShapeDef> shapes = new List<LabShapeDef>();
            public LabStartDef start;
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
            /// <summary>Set when the player was put at the lab start (<c>place</c>): the unlocks, course reset point and respawn point it replaced.</summary>
            public List<(string field, object value)> SavedAbilities;
            public Vector2? CourseResetPointBefore, RespawnPointBefore;
            /// <summary>Hand play with a solved demo: the ghost replaying its path on a loop (<see cref="LabGhost"/>).</summary>
            public LabGhost Ghost;
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

        /// <summary>Every lab map's id: the config folder's files and the plugin's embedded seeds (solved demos excluded).</summary>
        internal List<string> LabMapIds()
        {
            const string prefix = "IGTAPTasMod.labmaps.";
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (resource.StartsWith(prefix, StringComparison.Ordinal) && resource.EndsWith(".json", StringComparison.Ordinal) && !resource.EndsWith(".demo.json", StringComparison.Ordinal))
                    ids.Add(resource.Substring(prefix.Length, resource.Length - prefix.Length - ".json".Length));
            if (Directory.Exists(LabMapsDir))
                foreach (string file in Directory.GetFiles(LabMapsDir, "*.json"))
                    if (!file.EndsWith(".demo.json", StringComparison.Ordinal)) ids.Add(Path.GetFileNameWithoutExtension(file));
            return ids.ToList();
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

        /// <summary>The lab map of a tech's demo is injected before its start is restored (RestoreRouteStart), for a 1x
        /// playback and for the tech trace's turbo run alike.</summary>
        internal void LabMapEnsureFor(MacroDefinition route)
        {
            TechDef tech = _techStart ?? (route == null ? null : _techs.Find(t => t.routeId == route.id));
            if (tech == null || string.IsNullOrEmpty(tech.labmap)) return;
            if (_lab != null && _lab.Id == tech.labmap && !_lab.Hand) return;
            if (_lab != null) RestoreLab(_lab.Id == tech.labmap ? "the demo plays without the hand-play floor" : "another lab map");
            InjectLab(LoadLabMap(tech.labmap), false);
        }

        /// <param name="hand">Hand play: the lab's <c>hand.shapes</c> go in too (a floor to stand on); a demo's and the solver's lab never has them.</param>
        void InjectLab(LabMapDef lab, bool hand)
        {
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
                Color color = shape.spike || shape.layer == "Spikes" ? new Color(0.75f, 0.22f, 0.17f) : shape.trigger ? new Color(0.56f, 0.27f, 0.68f)
                    : shape.tag == "Moss" ? new Color(0.15f, 0.68f, 0.38f) : new Color(0.10f, 0.14f, 0.20f);
                if (shape.kind == "box")
                {
                    float w = shape.size[0] / 2f, h = shape.size[1] / 2f;
                    LabBox(go.transform, Vector2.zero, new Vector2(shape.size[0], shape.size[1]), color);
                    LabLine(go.transform, new[] { new Vector2(-w, -h), new Vector2(w, -h), new Vector2(w, h), new Vector2(-w, h) }, true, new Color(0.3f, 0.85f, 1f), 3f);
                }
                else LabLine(go.transform, shape.points.Select(p => new Vector2(p[0], p[1])).ToArray(), shape.kind == "polygon", color, 6f);
            }
            _lab = new LabSession
            {
                Id = lab.id, Lab = lab, Root = root, Hand = hand,
                PlayerBefore = player != null ? SavestateService.Capture(player, "lab/" + lab.id + " before", MacroLibrary.CurrentSchemaVersion) : null,
                Scene = SceneManager.GetActiveScene().name,
            };
            if (hand && player != null) BuildLabGhost(lab, player);
            Logger.LogInfo("Lab map " + lab.id + " injected at " + ax.ToString("0.#", CultureInfo.InvariantCulture) + "," + ay.ToString("0.#", CultureInfo.InvariantCulture)
                + " (" + shapes.Count + " shapes" + (hand ? ", for hand play" + (_lab.Ghost != null ? " with its demo's ghost" : "") : "") + "); autosave and the route gate are off until it is restored.");
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

        /// <summary>Drawn above the level: the top sorting layer, the top order.</summary>
        static void LabSorting(Renderer renderer)
        {
            SortingLayer[] layers = SortingLayer.layers;
            if (layers.Length > 0) renderer.sortingLayerID = layers[layers.Length - 1].id;
            renderer.sortingOrder = 32000;
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
        /// Puts the player at the injected lab's start to play it by hand (its <c>hand.start</c>, standing, when it has one): the lab
        /// category's unlocks (put back on restore, never autosaved while the lab is injected), then that start as a lab savestate
        /// restored in place (not kept on disk). A death or quick restart respawns there too until the lab is restored.
        /// </summary>
        void PlaceAtLabStart()
        {
            Movement player = FindWorldMovement() ?? throw new InvalidOperationException("No player in the world.");
            LabMapDef lab = _lab.Lab;
            string caps = CapsFor(lab.category ?? "NONE");
            if (Array.IndexOf(CapsLadder, caps) < 0) throw new InvalidOperationException("Lab map " + lab.id + " has an unknown category " + lab.category + ".");
            LabStartDef at = lab.hand?.start ?? lab.start;
            _engine.ClearInjectedInput(player);
            if (_lab.SavedAbilities == null) _lab.SavedAbilities = ApplyAbilities(player, caps);
            else ApplyAbilities(player, caps);
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
            Logger.LogInfo("Player placed at lab " + lab.id + "'s " + (lab.hand?.start != null ? "hand-play start" : "start") + " with " + lab.category + " unlocks.");
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
            if (_lab.Ghost != null) AnimateLabGhost();
            if (!string.Equals(_lab.Scene, SceneManager.GetActiveScene().name, StringComparison.Ordinal))
            {
                RestoreLab("scene change");
                return;
            }
            bool busy = PlaybackActive || _pendingReplay != null || _pendingSilentMacro != null || TurboRunner.Active || _engine.IsRunning || _recorder.IsRecording;
            if (busy) { _lab.PlaybackSeen = true; return; }
            if (_lab.PlaybackSeen) RestoreLab("playback end");
        }

        /// <summary>
        /// Installs a tech's lab demo (labmaps/&lt;id&gt;.demo.json's inputs, or labmaps/&lt;id&gt;.&lt;labDemo&gt;.demo.json for one of
        /// the lab's other demos) as its demo route, with its start placed by the lab savestate <c>place</c> mechanism (its id is
        /// the route's savestate: every playback starts there): the demo's own <c>start</c> when it has one, else the lab's.
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
            if (TechStart.Match(start).Length != start.Length || start.Length == 0)
                throw new InvalidOperationException("start is a course category prefix (1_WJ_DS) matching the lab map's category " + lab.category + ".");
            LabStartDef demoStart = demo["start"] is JObject own ? own.ToObject<LabStartDef>() : lab.start;
            Savestate placed = PlaceSavestate(LabStartBody(lab, demoStart));
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
            route.savestateId = placed.id;
            route.steps = ToSteps(ticks);
            SaveLibrary();
            DropSavestate(replaced);
            SaveTechs();
            Logger.LogInfo("Lab demo " + id + " installed for " + tech.name + ": " + ticks.Count + " input ticks from lab/" + id + ".");
        }
    }
}
