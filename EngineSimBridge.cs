using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    // Ground truth for the offline engine host (enginesim/): the loaded world exactly as Unity's physics sees it,
    // the solver's course-start state, and full per-tick state traces of turbo runs. Read-only for the game except
    // that /trace runs the route in turbo exactly like the solver does.
    public sealed partial class TasPlugin
    {
        string EngineSimDir { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "enginesim"); } }
        // Set while /api/enginesim/trace runs: called after every turbo tick of Evaluate.
        Action<Movement> _engineSimTickHook;

        object HandleEngineSim(WebStudioRequest request, JObject body)
        {
            Directory.CreateDirectory(EngineSimDir);
            if (request.Path == "/api/enginesim/world")
            {
                string path = Path.Combine(EngineSimDir, (Text(body, "name") ?? "world") + ".json");
                int objects = EngineSimExport.WriteWorld(path);
                return new { ok = true, path = path, objects = objects };
            }
            if (request.Path == "/api/enginesim/raycast")
                return new { ok = true, hits = EngineSimExport.Raycasts((JArray)body["rays"]) };
            if (request.Path == "/api/enginesim/trace")
                return EngineSimTrace(request, body);
            if (request.Path == "/api/enginesim/submit")
                return EngineSimSubmit(request, body);
            request.StatusCode = 404;
            return new { ok = false, error = "Unknown enginesim endpoint." };
        }

        // Sets the solver's course/category and makes sure its start state exists. Returns an error response or null.
        object PrepareEngineSimRun(WebStudioRequest request, JObject body, out List<SolverTick> inputs)
        {
            inputs = null;
            if (CapabilitiesLocked)
            {
                request.StatusCode = 409;
                return new { ok = false, error = CapabilitiesLockedError };
            }
            if (_solverRunning || _engine.IsRunning || _recorder.IsRecording)
            {
                request.StatusCode = 409;
                return new { ok = false, error = "Stop the solver, playback and recording first." };
            }
            int course = Number(body, "course", 0);
            string caps = Text(body, "capabilities") ?? "wjdash";
            if (_courses.All(c => c.id != course)) throw new InvalidOperationException("Unknown course " + course + ".");
            if (!CapabilityLabels.ContainsKey(caps)) throw new InvalidOperationException("Unknown capabilities.");
            CheckCategory(course, caps);
            _solverCourse = course;
            _solverCaps = caps;
            _solverBest = float.PositiveInfinity;
            inputs = body["ticks"] == null ? new List<SolverTick>() : body["ticks"].ToObject<List<SolverTick>>();
            string routeId = Text(body, "routeId");
            if (!string.IsNullOrEmpty(routeId))
            {
                MacroDefinition route = _library.macros.Find(m => m.id == routeId);
                if (route == null || !route.steps.All(s => s.type == MacroStepType.RawInput))
                    throw new InvalidOperationException("Route " + routeId + " is missing or not raw input.");
                inputs = ToTicks(route.steps);
            }
            _bestArrival.Clear();
            if (Truthy(body, "freshStart") || _engineSimStartKey != course + "_" + caps || CurrentOrigin() != _startOrigin) _startState = null;
            if (_startState == null && !CourseLoaded())
            {
                request.StatusCode = 409;
                return new { ok = false, error = "Course " + course + " is loading; retry in a moment." };
            }
            if (_startState == null)
            {
                // The solver's first run captures the start state; every later run resumes from it.
                Evaluate(new List<SolverTick> { new SolverTick() }, null, null, null, false, out _, out _, out _);
                if (_startState == null) throw new InvalidOperationException("The start state was not captured.");
                _engineSimStartKey = course + "_" + caps;
            }
            return null;
        }

        // An offline solver's candidate: {course, capabilities, ticks}. Replayed in turbo from the start state exactly as
        // the in-game solver judges runs; a timed finish faster than the category's best route is saved as the new route.
        object EngineSimSubmit(WebStudioRequest request, JObject body)
        {
            object error = PrepareEngineSimRun(request, body, out List<SolverTick> inputs);
            if (error != null) return error;
            return Judge(inputs);
        }

        // Turbo replay of prepared inputs; saved through the gate (SaveSolverRoute) when it beats the category's best.
        JudgeResult Judge(List<SolverTick> inputs)
        {
            bool finished = Evaluate(inputs, null, null, null, false, out float time, out int finishTick, out _);
            ParkAtStart();
            float best = CategoryBest(SolverCategory);
            var result = new JudgeResult { finished = finished, time = time, finishTick = finishTick,
                previousBest = float.IsInfinity(best) ? (float?)null : best };
            if (finished && time < best - .0001f)
            {
                List<SolverTick> route = inputs.Take(finishTick + 1).Select(k => k.Clone()).ToList();
                result.saved = SaveSolverRoute(route, time);
                if (result.saved) { _solverBest = time; _solverBestInputs = route; }
                else result.rejected = _solverLog[_solverLog.Count - 1];
            }
            return result;
        }

        // Back to the course start after a turbo run: the real frames until the next run then load the start's zone
        // (and apply any FloatingOrigin shift) instead of wherever the run ended.
        void ParkAtStart()
        {
            RestoreRouteStart(new MacroDefinition { name = _courses.Find(c => c.id == _solverCourse).Token + "_" });
        }

        sealed class JudgeResult
        {
            public bool ok = true, finished, saved;
            public float time;
            public int finishTick;
            public float? previousBest;
            public string rejected;
        }

        // `IGTAP.EngineSim solve` writes every offline best to enginesim/offline-best/<course>_<caps>-<time>.json and
        // submits it only while the game runs. Once the game is in the world and idle, the fastest waiting file per
        // category that beats the saved route is judged here like a submit (its own category and the others, as solve
        // does), so solves run with the game closed still reach the studio. Judged or superseded files move to judged/.
        static readonly Regex OfflineBestFile = new Regex(@"^(\d+)_([a-z]+)-([\d.]+)\.json$");
        float _offlineBestCheckAt;
        // "<file>|<caps>" already judged; a category whose course is still loading is retried on a later pump.
        readonly HashSet<string> _offlineJudged = new HashSet<string>();

        void PumpOfflineBests()
        {
            if (Time.unscaledTime < _offlineBestCheckAt) return;
            _offlineBestCheckAt = Time.unscaledTime + 5f;
            string dir = Path.Combine(EngineSimDir, "offline-best");
            if (CapabilitiesLocked || _solverRunning || _engine.IsRunning || _recorder.IsRecording || _bootstrapRequest != null
                || _webServer == null || !Directory.Exists(dir)) return;
            Movement player = FindWorldMovement();
            if (player == null || MovIsOnMainMenuField != null && MovIsOnMainMenuField.GetValue(player) is bool menu && menu) return;
            var groups = Directory.GetFiles(dir, "*.json")
                .Select(path => (path, match: OfflineBestFile.Match(Path.GetFileName(path))))
                .Where(f => f.match.Success && CapabilityLabels.ContainsKey(f.match.Groups[2].Value))
                .Select(f => (f.path, course: int.Parse(f.match.Groups[1].Value), caps: f.match.Groups[2].Value,
                    time: float.Parse(f.match.Groups[3].Value, CultureInfo.InvariantCulture)))
                .Where(f => _courses.Any(c => c.id == f.course))
                .GroupBy(f => (f.course, f.caps));
            string judged = Path.Combine(dir, "judged");
            foreach (var group in groups)
            {
                var files = group.OrderBy(f => f.time).ToList();
                CourseDef course = _courses.Find(c => c.id == group.Key.course);
                float best = CategoryBest(course.Token + "_" + CapabilityLabels[group.Key.caps]);
                bool started = _offlineJudged.Any(k => k.StartsWith(files[0].path + "|", StringComparison.Ordinal));
                if ((started || files[0].time < best - .0001f) && CapsLevel(group.Key.caps) >= course.MinLevel)
                {
                    // Its own category first, then the course's others (a route may hold up in a wider category too).
                    string next = new[] { group.Key.caps }.Concat(CapabilityLabels.Keys.Where(k => k != group.Key.caps && CapsLevel(k) >= course.MinLevel))
                        .FirstOrDefault(caps => !_offlineJudged.Contains(files[0].path + "|" + caps));
                    if (next != null)
                    {
                        _offlineBestCheckAt = Time.unscaledTime + 1f;
                        string line = "Offline best " + Path.GetFileName(files[0].path) + " as " + CapabilityLabels[next] + ": ";
                        try
                        {
                            object error = PrepareEngineSimRun(new WebStudioRequest(), new JObject { ["course"] = group.Key.course, ["capabilities"] = next }, out _);
                            if (error != null) return;
                            List<SolverTick> inputs = JObject.Parse(File.ReadAllText(files[0].path))["ticks"].ToObject<List<SolverTick>>();
                            JudgeResult r = Judge(inputs);
                            line += (r.finished ? "turbo " + r.time.ToString("0.00", CultureInfo.InvariantCulture) + "s" : "no finish in turbo")
                                + (r.saved ? ", saved" : r.rejected != null ? ", " + r.rejected : "");
                        }
                        catch (Exception ex) { line += "error " + ex.Message; }
                        _offlineJudged.Add(files[0].path + "|" + next);
                        _solverLog.Add(line);
                        Logger.LogInfo(line);
                        return;
                    }
                }
                Directory.CreateDirectory(judged);
                foreach (var f in files)
                {
                    string target = Path.Combine(judged, Path.GetFileName(f.path));
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(f.path, target);
                }
                return;
            }
        }

        float CategoryBest(string category)
        {
            float best = float.PositiveInfinity;
            foreach (MacroDefinition m in _library.macros)
            {
                Match g = CategoryRoute.Match(m.name ?? "");
                if (g.Success && string.Equals(g.Groups[1].Value, category, StringComparison.OrdinalIgnoreCase))
                    best = Math.Min(best, float.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture));
            }
            return best;
        }

        // {course, capabilities, ticks: [{x, held, press, release, dash, dashJump}] or routeId, name, world, freshStart} ->
        // trace file with the start state (what every solver run resumes from) and the full state after every
        // stepped tick; with world: true also the world export as that run saw it.
        object EngineSimTrace(WebStudioRequest request, JObject body)
        {
            object error = PrepareEngineSimRun(request, body, out List<SolverTick> inputs);
            if (error != null) return error;
            int course = _solverCourse;
            string caps = _solverCaps;
            string name = Text(body, "name") ?? ("trace-" + course + "_" + caps);
            string path = Path.Combine(EngineSimDir, name + ".json");
            // The world as this run sees it: zones load in real frames, so export it with no frame in between.
            if (Truthy(body, "world")) EngineSimExport.WriteWorld(Path.Combine(EngineSimDir, name + ".world.json"));
            Movement player = FindWorldMovement();
            bool finished;
            float time;
            int finishTick;
            Vector2 end;
            using (var writer = new StreamWriter(path))
            {
                var json = new EngineSimJson(writer);
                JsonTextWriter w = json.W;
                w.WriteStartObject();
                w.WritePropertyName("course"); w.WriteValue(course);
                CourseDef finishCourse = _courses.Find(c => c.id == course);
                if (finishCourse != null && finishCourse.HasFinish)
                {
                    // The bonus course's reward box in this run's world coordinates, so offline runs finish exactly
                    // where this one did.
                    Vector2 origin = CurrentOrigin();
                    w.WritePropertyName("finish"); w.WriteStartArray();
                    json.Float(finishCourse.endX + origin.x); json.Float(finishCourse.endY + origin.y);
                    json.Float(finishCourse.endW); json.Float(finishCourse.endH);
                    w.WriteEndArray();
                }
                w.WritePropertyName("capabilities"); w.WriteValue(caps);
                w.WritePropertyName("fixedDeltaTime"); json.Float(Time.fixedDeltaTime);
                w.WritePropertyName("inputs"); w.WriteRawValue(JsonConvert.SerializeObject(inputs));
                w.WritePropertyName("start"); EngineSimExport.WriteSnapshot(json, _startState, player, false);
                w.WritePropertyName("ticks");
                w.WriteStartArray();
                _engineSimTickHook = p =>
                {
                    var snapshot = TurboRunner.Capture(p, _solverCourses, _engine.CaptureState(), (int)_engine.RunTicks);
                    EngineSimExport.WriteSnapshot(json, snapshot, p, true);
                };
                try { finished = Evaluate(inputs, null, null, null, false, out time, out finishTick, out end); }
                finally { _engineSimTickHook = null; }
                ParkAtStart();
                w.WriteEndArray();
                w.WritePropertyName("result");
                w.WriteStartObject();
                w.WritePropertyName("finished"); w.WriteValue(finished);
                w.WritePropertyName("time"); json.Float(time);
                w.WritePropertyName("finishTick"); w.WriteValue(finishTick);
                w.WritePropertyName("endX"); json.Float(end.x);
                w.WritePropertyName("endY"); json.Float(end.y);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            return new { ok = true, path = path, finished = finished, time = time, finishTick = finishTick };
        }

        string _engineSimStartKey;

        static bool Truthy(JObject body, string key) { return body[key] != null && body[key].Type == JTokenType.Boolean && (bool)body[key]; }
    }

    internal static class EngineSimExport
    {
        const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public static void WriteSnapshot(EngineSimJson json, TurboRunner.Snapshot s, Movement player, bool live)
        {
            JsonTextWriter w = json.W;
            w.WriteStartObject();
            w.WritePropertyName("tick"); w.WriteValue(s.tick);
            w.WritePropertyName("movement"); WriteFieldValues(json, typeof(Movement), s.movement);
            w.WritePropertyName("engine"); WriteFieldValues(json, typeof(MacroEngine), s.engine);
            w.WritePropertyName("courses");
            w.WriteStartArray();
            foreach (var (course, fields) in s.courses)
            {
                w.WriteStartObject();
                w.WritePropertyName("id"); w.WriteValue(course == null ? 0 : course.GetInstanceID());
                w.WritePropertyName("fields"); WriteFieldValues(json, typeof(courseScript), fields);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("position"); json.Value(s.position, 0);
            w.WritePropertyName("bodyPosition"); json.Value(s.bodyPosition, 0);
            w.WritePropertyName("velocity"); json.Value(s.velocity, 0);
            w.WritePropertyName("rotation"); json.Float(s.rotation);
            w.WritePropertyName("angularVelocity"); json.Float(s.angularVelocity);
            w.WritePropertyName("colliders");
            w.WriteStartArray();
            foreach (var c in s.colliders)
            {
                w.WriteStartArray();
                json.Float(c.offset.x); json.Float(c.offset.y); json.Float(c.size.x); json.Float(c.size.y);
                w.WriteValue(c.exclude); w.WriteValue(c.enabled);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WritePropertyName("pending");
            w.WriteStartArray();
            // The player's timers (offline restores them onto the player).
            foreach (var (due, target, method) in s.pending)
                if (target == player) { w.WriteStartArray(); w.WriteValue(due); w.WriteValue(method); w.WriteEndArray(); }
            w.WriteEndArray();
            if (player != null)
            {
                // Not in the snapshot, but read by the game (facing via localScale, death via rotation).
                w.WritePropertyName("transformRotation"); json.Value(player.transform.rotation, 0);
                w.WritePropertyName("localScale"); json.Value(player.transform.localScale, 0);
                if (live)
                {
                    w.WritePropertyName("contacts");
                    WriteContacts(json, player.GetComponent<Rigidbody2D>());
                }
            }
            w.WriteEndObject();
        }

        static readonly ContactPoint2D[] ContactBuffer = new ContactPoint2D[64];

        static void WriteContacts(EngineSimJson json, Rigidbody2D body)
        {
            JsonTextWriter w = json.W;
            w.WriteStartArray();
            int count = body == null ? 0 : body.GetContacts(ContactBuffer);
            for (int i = 0; i < count; i++)
            {
                ContactPoint2D c = ContactBuffer[i];
                w.WriteStartArray();
                w.WriteValue(c.collider == null ? 0 : c.collider.GetInstanceID());
                w.WriteValue(c.otherCollider == null ? 0 : c.otherCollider.GetInstanceID());
                json.Float(c.point.x); json.Float(c.point.y); json.Float(c.normal.x); json.Float(c.normal.y);
                json.Float(c.separation); json.Float(c.normalImpulse); json.Float(c.tangentImpulse);
                w.WriteValue(c.enabled);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        static void WriteFieldValues(EngineSimJson json, Type type, object[] values)
        {
            FieldInfo[] fields = FieldState.For(type);
            JsonTextWriter w = json.W;
            w.WriteStartArray();
            for (int i = 0; i < fields.Length && i < values.Length; i++)
            {
                w.WriteStartArray();
                w.WriteValue(fields[i].DeclaringType.FullName);
                w.WriteValue(fields[i].Name);
                json.Value(values[i], 0);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        public static object[] Raycasts(JArray rays)
        {
            var hits = new object[rays.Count];
            for (int i = 0; i < rays.Count; i++)
            {
                var r = (JArray)rays[i];
                RaycastHit2D hit = Physics2D.Raycast(new Vector2((float)r[0], (float)r[1]), new Vector2((float)r[2], (float)r[3]),
                    (float)r[4], (int)r[5]);
                hits[i] = hit.collider == null ? null : new object[] {
                    hit.collider.GetInstanceID(), G9(hit.point.x), G9(hit.point.y), G9(hit.normal.x), G9(hit.normal.y),
                    G9(hit.distance), G9(hit.fraction) };
            }
            return hits;
        }

        static string G9(float f) { return f.ToString("G9", CultureInfo.InvariantCulture); }

        // Every scene GameObject (active or not) with its transform, colliders' physics shapes, bodies, and the fields of
        // every script; plus the global 2D physics settings.
        public static int WriteWorld(string path)
        {
            var gameObjects = new List<GameObject>();
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go != null && go.scene.IsValid() && (go.hideFlags & HideFlags.HideAndDontSave) == 0) gameObjects.Add(go);
            var materials = new Dictionary<int, PhysicsMaterial2D>();
            using (var writer = new StreamWriter(path))
            {
                var json = new EngineSimJson(writer);
                JsonTextWriter w = json.W;
                w.WriteStartObject();
                w.WritePropertyName("schema"); w.WriteValue(1);
                w.WritePropertyName("unityVersion"); w.WriteValue(Application.unityVersion);
                w.WritePropertyName("createdUtc"); w.WriteValue(DateTime.UtcNow.ToString("o"));
                w.WritePropertyName("time");
                w.WriteStartObject();
                w.WritePropertyName("fixedDeltaTime"); json.Float(Time.fixedDeltaTime);
                w.WritePropertyName("timeScale"); json.Float(Time.timeScale);
                w.WritePropertyName("maximumDeltaTime"); json.Float(Time.maximumDeltaTime);
                w.WriteEndObject();
                w.WritePropertyName("physics2d"); WriteStaticProperties(json, typeof(Physics2D));
                w.WritePropertyName("layerCollisionMasks");
                w.WriteStartArray();
                for (int i = 0; i < 32; i++) w.WriteValue(Physics2D.GetLayerCollisionMask(i));
                w.WriteEndArray();
                w.WritePropertyName("layerNames");
                w.WriteStartArray();
                for (int i = 0; i < 32; i++) w.WriteValue(LayerMask.LayerToName(i));
                w.WriteEndArray();
                w.WritePropertyName("objects");
                w.WriteStartArray();
                foreach (GameObject go in gameObjects) WriteGameObject(json, go, materials);
                w.WriteEndArray();
                w.WritePropertyName("materials");
                w.WriteStartArray();
                foreach (PhysicsMaterial2D m in materials.Values)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("id"); w.WriteValue(m.GetInstanceID());
                    w.WritePropertyName("name"); w.WriteValue(m.name);
                    w.WritePropertyName("props"); WriteProperties(json, m);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WritePropertyName("statics"); WriteGameStatics(json);
                w.WriteEndObject();
            }
            return gameObjects.Count;
        }

        static void WriteGameObject(EngineSimJson json, GameObject go, Dictionary<int, PhysicsMaterial2D> materials)
        {
            JsonTextWriter w = json.W;
            Transform t = go.transform;
            w.WriteStartObject();
            w.WritePropertyName("id"); w.WriteValue(go.GetInstanceID());
            w.WritePropertyName("name"); w.WriteValue(go.name);
            w.WritePropertyName("scene"); w.WriteValue(go.scene.name);
            w.WritePropertyName("parent"); w.WriteValue(t.parent == null ? 0 : t.parent.gameObject.GetInstanceID());
            w.WritePropertyName("siblingIndex"); w.WriteValue(t.GetSiblingIndex());
            w.WritePropertyName("activeSelf"); w.WriteValue(go.activeSelf);
            w.WritePropertyName("activeInHierarchy"); w.WriteValue(go.activeInHierarchy);
            w.WritePropertyName("layer"); w.WriteValue(go.layer);
            w.WritePropertyName("tag"); w.WriteValue(go.tag);
            w.WritePropertyName("transformId"); w.WriteValue(t.GetInstanceID());
            w.WritePropertyName("position"); json.Value(t.position, 0);
            w.WritePropertyName("rotation"); json.Value(t.rotation, 0);
            w.WritePropertyName("lossyScale"); json.Value(t.lossyScale, 0);
            w.WritePropertyName("localPosition"); json.Value(t.localPosition, 0);
            w.WritePropertyName("localRotation"); json.Value(t.localRotation, 0);
            w.WritePropertyName("localScale"); json.Value(t.localScale, 0);
            w.WritePropertyName("components");
            w.WriteStartArray();
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                w.WriteStartObject();
                w.WritePropertyName("id"); w.WriteValue(c.GetInstanceID());
                w.WritePropertyName("type"); w.WriteValue(c.GetType().FullName);
                w.WritePropertyName("assembly"); w.WriteValue(c.GetType().Assembly.GetName().Name);
                if (c is Behaviour b) { w.WritePropertyName("enabled"); w.WriteValue(b.enabled); }
                if (c is MonoBehaviour mb && c.GetType().Assembly == typeof(Movement).Assembly)
                {
                    w.WritePropertyName("fields");
                    WriteAllFields(json, mb);
                }
                else if (c is Collider2D col)
                {
                    if (col.sharedMaterial != null) materials[col.sharedMaterial.GetInstanceID()] = col.sharedMaterial;
                    w.WritePropertyName("props"); WriteProperties(json, col);
                    w.WritePropertyName("shapes"); WriteShapes(json, col);
                    WriteColliderPaths(json, col);
                }
                else if (c is Rigidbody2D rb)
                {
                    if (rb.sharedMaterial != null) materials[rb.sharedMaterial.GetInstanceID()] = rb.sharedMaterial;
                    w.WritePropertyName("props"); WriteProperties(json, rb);
                }
                else if (c is Effector2D || c is Joint2D)
                {
                    w.WritePropertyName("props"); WriteProperties(json, c);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        static void WriteAllFields(EngineSimJson json, MonoBehaviour target)
        {
            JsonTextWriter w = json.W;
            w.WriteStartArray();
            for (Type t = target.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(InstanceFields))
                {
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    object value;
                    try { value = f.GetValue(target); }
                    catch (Exception) { continue; }
                    w.WriteStartArray();
                    w.WriteValue(t.FullName);
                    w.WriteValue(f.Name);
                    json.Value(value, 0);
                    w.WriteEndArray();
                }
            w.WriteEndArray();
        }

        static readonly List<Vector2> ShapeVertices = new List<Vector2>();

        static void WriteShapes(EngineSimJson json, Collider2D collider)
        {
            JsonTextWriter w = json.W;
            var group = new PhysicsShapeGroup2D();
            int count;
            try { count = collider.GetShapes(group); }
            catch (Exception) { w.WriteNull(); return; }
            w.WriteStartObject();
            w.WritePropertyName("localToWorld");
            Matrix4x4 m = group.localToWorldMatrix;
            w.WriteStartArray();
            for (int i = 0; i < 16; i++) json.Float(m[i]);
            w.WriteEndArray();
            w.WritePropertyName("list");
            w.WriteStartArray();
            for (int i = 0; i < count; i++)
            {
                PhysicsShape2D shape = group.GetShape(i);
                ShapeVertices.Clear();
                group.GetShapeVertices(i, ShapeVertices);
                w.WriteStartObject();
                w.WritePropertyName("type"); w.WriteValue(shape.shapeType.ToString());
                w.WritePropertyName("radius"); json.Float(shape.radius);
                w.WritePropertyName("vertices");
                w.WriteStartArray();
                foreach (Vector2 v in ShapeVertices) { json.Float(v.x); json.Float(v.y); }
                w.WriteEndArray();
                w.WritePropertyName("useAdjacentStart"); w.WriteValue(shape.useAdjacentStart);
                w.WritePropertyName("useAdjacentEnd"); w.WriteValue(shape.useAdjacentEnd);
                w.WritePropertyName("adjacentStart"); json.Value(shape.adjacentStart, 0);
                w.WritePropertyName("adjacentEnd"); json.Value(shape.adjacentEnd, 0);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        static void WriteColliderPaths(EngineSimJson json, Collider2D collider)
        {
            JsonTextWriter w = json.W;
            var points = new List<Vector2>();
            if (collider is CompositeCollider2D composite)
            {
                w.WritePropertyName("paths");
                w.WriteStartArray();
                for (int p = 0; p < composite.pathCount; p++)
                {
                    points.Clear();
                    composite.GetPath(p, points);
                    w.WriteStartArray();
                    foreach (Vector2 v in points) { json.Float(v.x); json.Float(v.y); }
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            else if (collider is PolygonCollider2D polygon)
            {
                w.WritePropertyName("paths");
                w.WriteStartArray();
                for (int p = 0; p < polygon.pathCount; p++)
                {
                    points.Clear();
                    polygon.GetPath(p, points);
                    w.WriteStartArray();
                    foreach (Vector2 v in points) { json.Float(v.x); json.Float(v.y); }
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            else if (collider is EdgeCollider2D edge)
            {
                w.WritePropertyName("paths");
                w.WriteStartArray();
                w.WriteStartArray();
                foreach (Vector2 v in edge.points) { json.Float(v.x); json.Float(v.y); }
                w.WriteEndArray();
                w.WriteEndArray();
            }
        }

        static bool SimpleType(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4)
                || t == typeof(Quaternion) || t == typeof(Color) || t == typeof(Rect) || t == typeof(Bounds) || t == typeof(LayerMask)
                || t == typeof(Vector2Int) || typeof(UnityEngine.Object).IsAssignableFrom(t);
        }

        static void WriteProperties(EngineSimJson json, object target)
        {
            JsonTextWriter w = json.W;
            w.WriteStartObject();
            foreach (PropertyInfo p in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || !SimpleType(p.PropertyType)) continue;
                if (p.IsDefined(typeof(ObsoleteAttribute), true)) continue;
                if (p.Name == "gameObject" || p.Name == "transform" || p.Name == "hideFlags") continue;
                object value;
                try { value = p.GetValue(target, null); }
                catch (Exception) { continue; }
                w.WritePropertyName(p.Name);
                json.Value(value, 0);
            }
            w.WriteEndObject();
        }

        static void WriteStaticProperties(EngineSimJson json, Type type)
        {
            JsonTextWriter w = json.W;
            w.WriteStartObject();
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Static | BindingFlags.Public))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || !SimpleType(p.PropertyType)) continue;
                if (p.IsDefined(typeof(ObsoleteAttribute), true)) continue;
                object value;
                try { value = p.GetValue(null, null); }
                catch (Exception) { continue; }
                w.WritePropertyName(p.Name);
                json.Value(value, 0);
            }
            w.WriteEndObject();
        }

        // Static fields of game classes (singleton instances, global flags such as pauseMenu.menuOpen).
        static void WriteGameStatics(EngineSimJson json)
        {
            JsonTextWriter w = json.W;
            Type[] gameTypes;
            try { gameTypes = typeof(Movement).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { gameTypes = ex.Types.Where(t => t != null).ToArray(); }
            w.WriteStartArray();
            foreach (Type type in gameTypes)
            {
                if (type.IsGenericTypeDefinition) continue;
                FieldInfo[] fields;
                try { fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch (Exception) { continue; }
                foreach (FieldInfo f in fields)
                {
                    if (f.IsLiteral || typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    object value;
                    try { value = f.GetValue(null); }
                    catch (Exception) { continue; }
                    w.WriteStartArray();
                    w.WriteValue(type.FullName);
                    w.WriteValue(f.Name);
                    json.Value(value, 0);
                    w.WriteEndArray();
                }
            }
            // Closed generic singletons: Singleton<T>.instance for every T the game uses.
            Type singleton = typeof(Movement).Assembly.GetType("Singleton`1");
            if (singleton != null)
                foreach (Type type in gameTypes)
                {
                    if (type.IsAbstract || type.IsGenericTypeDefinition || type.BaseType == null || !type.BaseType.IsGenericType
                        || type.BaseType.GetGenericTypeDefinition() != singleton) continue;
                    foreach (FieldInfo f in type.BaseType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        object value;
                        try { value = f.GetValue(null); }
                        catch (Exception) { continue; }
                        w.WriteStartArray();
                        w.WriteValue("Singleton`1[" + type.FullName + "]");
                        w.WriteValue(f.Name);
                        json.Value(value, 0);
                        w.WriteEndArray();
                    }
                }
            w.WriteEndArray();
        }
    }

    // Streams values with exact floats (G9 round-trips every float) and Unity objects as {"$ref": instanceId}.
    internal sealed class EngineSimJson
    {
        public readonly JsonTextWriter W;
        readonly HashSet<object> _visiting = new HashSet<object>(ReferenceComparer.Instance);

        public EngineSimJson(TextWriter writer) { W = new JsonTextWriter(writer) { Formatting = Formatting.None }; }

        public void Float(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) W.WriteValue(f.ToString(CultureInfo.InvariantCulture));
            else W.WriteRawValue(f.ToString("G9", CultureInfo.InvariantCulture));
        }

        public void Value(object value, int depth)
        {
            if (value == null) { W.WriteNull(); return; }
            if (value is UnityEngine.Object unityObject)
            {
                if (unityObject == null) { W.WriteNull(); return; }
                W.WriteStartObject();
                W.WritePropertyName("$ref"); W.WriteValue(unityObject.GetInstanceID());
                W.WriteEndObject();
                return;
            }
            Type type = value.GetType();
            if (value is float f) { Float(f); return; }
            if (value is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) W.WriteValue(d.ToString(CultureInfo.InvariantCulture));
                else W.WriteRawValue(d.ToString("G17", CultureInfo.InvariantCulture));
                return;
            }
            if (type.IsEnum) { W.WriteValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); return; }
            if (value is Delegate || value is Type || value is MemberInfo || value is IntPtr || value is UIntPtr || value is Coroutine
                || value is IEnumerator || value is System.Threading.WaitHandle)
            { W.WriteNull(); return; }
            if (value is string || type.IsPrimitive) { W.WriteValue(value); return; }
            if (value is LayerMask mask) { W.WriteValue(mask.value); return; }
            if (depth > 8 || (!type.IsValueType && !_visiting.Add(value))) { W.WriteNull(); return; }
            try
            {
                if (value is IDictionary dictionary)
                {
                    W.WriteStartObject();
                    W.WritePropertyName("$dict");
                    W.WriteStartArray();
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        W.WriteStartArray();
                        Value(entry.Key, depth + 1);
                        Value(entry.Value, depth + 1);
                        W.WriteEndArray();
                    }
                    W.WriteEndArray();
                    W.WriteEndObject();
                    return;
                }
                if (value is IEnumerable sequence)
                {
                    W.WriteStartArray();
                    foreach (object item in sequence) Value(item, depth + 1);
                    W.WriteEndArray();
                    return;
                }
                // Plain classes only from the game; anything else (threads, handles, library internals) is not game state.
                if (!type.IsValueType && type.Assembly != typeof(Movement).Assembly) { W.WriteNull(); return; }
                W.WriteStartObject();
                for (Type t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
                    foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                        W.WritePropertyName(t == type ? field.Name : t.Name + "::" + field.Name);
                        object fieldValue;
                        try { fieldValue = field.GetValue(value); }
                        catch (Exception) { W.WriteNull(); continue; }
                        Value(fieldValue, depth + 1);
                    }
                W.WriteEndObject();
            }
            finally
            {
                if (!type.IsValueType) _visiting.Remove(value);
            }
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }
    }
}
