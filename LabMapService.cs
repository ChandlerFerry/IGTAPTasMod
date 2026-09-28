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
        }

        sealed class LabSession
        {
            public string Id;
            public LabMapDef Lab;
            public GameObject Root;
            public Savestate PlayerBefore;
            public string Scene;
            public bool PlaybackSeen;
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

        object HandleLabMap(string path, JObject body)
        {
            switch (path)
            {
                case "/api/labmap/inject":
                {
                    string id = Text(body, "id");
                    if (_lab != null) RestoreLab("another lab map");
                    InjectLab(LoadLabMap(id));
                    return new { ok = true, id, objects = _lab.Root.transform.childCount, anchor = _lab.Lab.anchor, scene = _lab.Scene };
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
            if (_lab != null && _lab.Id == tech.labmap) return;
            if (_lab != null) RestoreLab("another lab map");
            InjectLab(LoadLabMap(tech.labmap));
        }

        void InjectLab(LabMapDef lab)
        {
            Movement player = FindWorldMovement();
            Vector2 origin = CurrentOrigin();
            (float ax, float ay) = (lab.anchor[0], lab.anchor[1]);
            var root = new GameObject("LabMap " + lab.id);
            root.transform.position = new Vector3(ax + origin.x, ay + origin.y, 0f);
            for (int i = 0; i < lab.shapes.Count; i++)
            {
                LabShapeDef shape = lab.shapes[i];
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
            }
            _lab = new LabSession
            {
                Id = lab.id, Lab = lab, Root = root,
                PlayerBefore = player != null ? SavestateService.Capture(player, "lab/" + lab.id + " before", MacroLibrary.CurrentSchemaVersion) : null,
                Scene = SceneManager.GetActiveScene().name,
            };
            Logger.LogInfo("Lab map " + lab.id + " injected at " + ax.ToString("0.#", CultureInfo.InvariantCulture) + "," + ay.ToString("0.#", CultureInfo.InvariantCulture)
                + " (" + lab.shapes.Count + " shapes); autosave and the route gate are off until it is restored.");
        }

        static int LayerNumber(string layer)
        {
            if (int.TryParse(layer, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) return number;
            int named = LayerMask.NameToLayer(layer);
            if (named < 0) throw new InvalidOperationException("No Unity layer named " + layer + ".");
            return named;
        }

        /// <summary>Destroys the injected lab and puts back anything its session touched (the player's state).</summary>
        void RestoreLab(string why)
        {
            if (_lab == null) return;
            LabSession lab = _lab;
            _lab = null;
            if (lab.Root != null) DestroyImmediate(lab.Root);
            if (lab.PlayerBefore != null)
            {
                Movement player = FindWorldMovement();
                if (player != null) SavestateService.Restore(player, lab.PlayerBefore, MacroLibrary.CurrentSchemaVersion);
            }
            Logger.LogInfo("Lab map " + lab.Id + " restored (" + why + ").");
        }

        /// <summary>Restores the lab map on a scene change or at a playback's end (plugin Update).</summary>
        void PumpLabMap()
        {
            if (_lab == null) return;
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
        /// Installs a tech's lab demo (labmaps/&lt;id&gt;.demo.json's inputs) as its demo route, with the lab start placed by
        /// the lab savestate <c>place</c> mechanism (its id is the route's savestate: every playback starts at the lab start).
        /// </summary>
        void InstallLabDemo(TechDef tech, JObject body)
        {
            string id = Text(body, "labmap");
            if (id.Length == 0) id = tech.labmap ?? "";
            LabMapDef lab = LoadLabMap(id);
            string demoPath = LabMapFile(id + ".demo.json") ?? throw new InvalidOperationException("Lab map " + id + " has no solved demo (run `IGTAP.EngineSim lab solve` first).");
            JObject demo = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(demoPath));
            List<SolverTick> ticks = demo["inputs"].ToObject<List<SolverTick>>();
            if (ticks == null || ticks.Count == 0) throw new InvalidOperationException("Lab map " + id + "'s demo has no inputs.");
            string start = Text(body, "start");
            if (start.Length == 0) start = tech.start ?? "";
            if (TechStart.Match(start).Length != start.Length || start.Length == 0)
                throw new InvalidOperationException("start is a course category prefix (1_WJ_DS) matching the lab map's category " + lab.category + ".");
            Savestate placed = PlaceSavestate(new JObject
            {
                ["name"] = "labmap-" + id,
                ["x"] = lab.anchor[0] + (lab.start.x ?? 0f),
                ["y"] = lab.anchor[1] + (lab.start.y ?? 0f),
                ["vx"] = lab.start.vx ?? 0f,
                ["vy"] = lab.start.vy ?? 0f,
                ["facing"] = lab.start.facing ?? 1f,
                ["fields"] = lab.start.fields ?? new JObject(),
            });
            tech.labmap = id;
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
