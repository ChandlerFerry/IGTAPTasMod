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
            if (request.Path == "/api/enginesim/raycast")
                return new { ok = true, hits = EngineSimExport.Raycasts((JArray)body["rays"]) };
            if (request.Path == "/api/enginesim/trace")
                return body["fulltas"] != null ? FullTasTraceRequest(request, body) : EngineSimTrace(request, body);
            if (request.Path == "/api/enginesim/submit")
                return EngineSimSubmit(request, body);
            if (request.Path == "/api/enginesim/map")
                return EngineSimMap(request, body);
            if (request.Path == "/api/enginesim/play")
                return EngineSimPlay(body);
            request.StatusCode = 404;
            return new { ok = false, error = "Unknown enginesim endpoint." };
        }

        // Sets the solver's course/category and makes sure its start state exists. Returns an error
        // response or null.
        object PrepareEngineSimRun(WebStudioRequest request, JObject body, out List<SolverTick> inputs)
        {
            inputs = null;
            if (PlaybackActive || _recorder.IsRecording)
            {
                request.StatusCode = 409;
                return new { ok = false, error = "Stop playback and recording first." };
            }
            if (_newGamePhase != 0)
            {
                request.StatusCode = 409;
                return new { ok = false, error = "A new game is loading; retry in a moment." };
            }
            string key = SelectSolverTarget(body);
            if (CapabilitiesLocked)
            {
                request.StatusCode = 409;
                return new { ok = false, error = CapabilitiesLockedError };
            }
            inputs = body["ticks"] == null ? new List<SolverTick>() : body["ticks"].ToObject<List<SolverTick>>();
            string routeId = Text(body, "routeId");
            if (!string.IsNullOrEmpty(routeId))
            {
                MacroDefinition route = _library.macros.Find(m => m.id == routeId);
                if (route == null || !route.steps.All(s => s.type == MacroStepType.RawInput))
                    throw new InvalidOperationException("Route " + routeId + " is missing or not raw input.");
                inputs = ToTicks(route.steps);
            }
            if (Truthy(body, "freshStart") || _engineSimStartKey != key || CurrentOrigin() != _startOrigin) _startState = null;
            if (_startState == null && !CourseLoaded())
            {
                request.StatusCode = 409;
                return new { ok = false, error = "Course " + _solverTarget.Token + " is loading; retry in a moment." };
            }
            if (_startState == null)
            {
                _engineSimStartKey = null;
                // The solver's first run captures the start state; every later run resumes from it.
                Evaluate(new List<SolverTick> { new SolverTick() }, out _, out _, out _);
                if (_startState == null) throw new InvalidOperationException("The start state was not captured.");
                _engineSimStartKey = key;
            }
            return null;
        }

        // {course, capabilities}: what every turbo run is made for. Returns the key of its start state.
        string SelectSolverTarget(JObject body)
        {
            int course = Number(body, "course", 0);
            string caps = Text(body, "capabilities");
            CourseDef def = _courses.Find(c => c.id == course) ?? throw new InvalidOperationException("Unknown course " + course + ".");
            if (!CapabilityLabels.ContainsKey(caps)) throw new InvalidOperationException("Unknown capabilities.");
            CheckCategory(course, caps);
            _solverTarget = def;
            _solverCaps = caps;
            return course + "_" + caps;
        }

        // An offline solver's candidate: {course, capabilities, ticks}. Replayed in turbo from the start state exactly as
        // the in-game solver judges runs; a timed finish faster than the category's best route is saved as the new route.
        object EngineSimSubmit(WebStudioRequest request, JObject body)
        {
            object error = PrepareEngineSimRun(request, body, out List<SolverTick> inputs);
            if (error != null) return error;
            return Judge(inputs);
        }

        // {course, capabilities}: plays the category's newest offline best (offline-best/, judged/ too: the latest solve's
        // best, not an older solve's faster one found on physics since fixed) in real time from the course start with the
        // category's unlocks, like a saved route, though turbo may have rejected it: to watch where the game and the
        // offline engine part ways. Nothing is saved.
        object EngineSimPlay(JObject body)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_solverRunning) throw new InvalidOperationException("Stop the solver first.");
            int course = Number(body, "course", 0);
            string caps = Text(body, "capabilities");
            CourseDef def = _courses.Find(c => c.id == course) ?? throw new InvalidOperationException("Unknown course " + course + ".");
            if (caps == null || !CapabilityLabels.ContainsKey(caps)) throw new InvalidOperationException("Unknown capabilities.");
            string category = def.Token + "_" + CapabilityLabels[caps];
            string root = Path.Combine(EngineSimDir, "offline-best");
            // Moving a file to judged/ keeps its write time: newest is when the solve found it.
            var newest = new[] { root, Path.Combine(root, "judged") }.Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, course + "_" + caps + "-*.json"))
                .Select(p => (path: p, match: OfflineBestFile.Match(Path.GetFileName(p))))
                .Where(f => f.match.Success)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f.path))
                .FirstOrDefault();
            if (newest.path == null) throw new InvalidOperationException("No offline best for " + category + ".");
            List<SolverTick> inputs = JObject.Parse(File.ReadAllText(newest.path))["ticks"].ToObject<List<SolverTick>>();
            // The category route name gives the course start and the unlocks (RestoreRouteStart, ApplyCategoryAbilities).
            var route = new MacroDefinition { id = "offline-best", name = category + " offline " + newest.match.Groups[3].Value, steps = ToSteps(inputs) };
            route.timeline = TimelineDocument.FromSequentialSteps(route.steps, Time.fixedDeltaTime * 1000f);
            EnsureGameUnpaused();
            _pendingReplay = route;
            _pendingReplayStartDelayTicks = 0;
            return new { ok = true, name = route.name, file = Path.GetFileName(newest.path) };
        }

        // Turbo replay of prepared inputs; saved through the gate (SaveSolverRoute) when it beats the category's best:
        // faster on the timer, or as fast in fewer physics ticks from the run start (a shorter flying-start setup).
        JudgeResult Judge(List<SolverTick> inputs)
        {
            bool finished = Evaluate(inputs, out float time, out int finishTick, out _);
            long runTicks = _engine.PhysicsTicks;
            string category = SolverCategory;
            float best = CategoryBest(category);
            bool tie = finished && Mathf.Abs(time - best) <= .0001f;
            long bestRunTicks = tie ? SavedRouteRunTicks(category, best) : long.MaxValue;
            ParkAtStart();
            var result = new JudgeResult { finished = finished, time = time, finishTick = finishTick, runTicks = runTicks,
                previousBest = float.IsInfinity(best) ? (float?)null : best,
                previousRunTicks = bestRunTicks == long.MaxValue ? (long?)null : bestRunTicks };
            if (finished && (time < best - .0001f || tie && runTicks < bestRunTicks))
            {
                List<SolverTick> route = inputs.Take(finishTick + 1).Select(k => k.Clone()).ToList();
                result.saved = SaveSolverRoute(route, time);
                if (!result.saved) result.rejected = _solverLog[_solverLog.Count - 1];
            }
            return result;
        }

        // Back to the course start after a turbo run: the real frames until the next run then load the start's zone
        // (and apply any FloatingOrigin shift) instead of wherever the run ended.
        void ParkAtStart()
        {
            RestoreRouteStart(new MacroDefinition { name = _solverTarget.Token + "_" });
        }

        sealed class JudgeResult
        {
            public bool ok = true, finished, saved;
            public float time;
            public int finishTick;
            // Physics ticks from the run start through the finish, and the saved route's when this one ties its time.
            public long runTicks;
            public float? previousBest;
            public long? previousRunTicks;
            public string rejected;
        }

        // `IGTAP.EngineSim solve` writes every offline best to enginesim/offline-best/<course>_<caps>-<time>.json and
        // submits it only while the game runs. Once the game is in the world and not busy (a studio solve counts
        // as busy only while it starts a target; its running process's own category waits for that process, which submits
        // its bests itself), the fastest waiting file per category that beats
        // or ties the saved route is judged here like a submit (a course's in its own category and the others, as solve
        // does), so solves run with the game closed still reach the studio. Judged or superseded
        // files move to judged/. A Full-TAS run's (fulltas-<run>-<time>.json) goes through the Full-TAS gate; its group waits
        // while no fresh new game is at its start.
        static readonly Regex OfflineBestFile = new Regex(@"^(\d+)_([a-z]+)-([\d.]+)\.json$");
        float _offlineBestCheckAt;
        // "<file>|<label>" already judged; a category whose course is still loading is retried on a later pump.
        readonly HashSet<string> _offlineJudged = new HashSet<string>();

        // A waiting offline best: its group (one category), time, and the submits that judge it (label,
        // body, category route name), its own first.
        sealed class OfflineBest
        {
            public string path, group;
            public float time;
            public List<(string label, JObject body, string category)> judgings;
        }

        OfflineBest ReadOfflineBest(string path)
        {
            string file = Path.GetFileName(path);
            Match m = OfflineBestFile.Match(file);
            if (m.Success)
            {
                int id = int.Parse(m.Groups[1].Value);
                string caps = m.Groups[2].Value;
                CourseDef course = _courses.Find(c => c.id == id);
                if (course == null || !CapabilityLabels.ContainsKey(caps)) return null;
                // Its own category first, then the course's others (a route may hold up in a wider category too); none when
                // the course doesn't have its own.
                return new OfflineBest { path = path, group = id + "_" + caps, time = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    judgings = !course.HasCategory(caps) ? new List<(string, JObject, string)>()
                        : new[] { caps }.Concat(CapabilityLabels.Keys.Where(k => k != caps && course.HasCategory(k)))
                            .Select(k => (CapabilityLabels[k], new JObject { ["course"] = id, ["capabilities"] = k }, course.Token + "_" + CapabilityLabels[k])).ToList() };
            }
            m = OfflineFullTasBestFile.Match(file);
            if (m.Success)
            {
                FullTasRunDef run = _fullTasRuns.Find(r => string.Equals(r.name, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                if (run == null) return null;
                return new OfflineBest { path = path, group = "fulltas-" + run.name, time = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    judgings = new List<(string, JObject, string)> { (CapabilityLabels["none"], new JObject { ["fulltas"] = run.name }, FullTasCategory(run.name)) } };
            }
            return null;
        }

        // How many offline bests wait to be judged (the Solve panel's "To validate").
        int _offlineWaiting;

        void PumpOfflineBests()
        {
            if (Time.unscaledTime < _offlineBestCheckAt) return;
            _offlineBestCheckAt = Time.unscaledTime + 5f;
            string dir = Path.Combine(EngineSimDir, "offline-best");
            _offlineWaiting = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Length : 0;
            if (_offlineWaiting == 0) return;
            // The solver needs the game only while it starts a target (parks the player, captures the start state in turbo);
            // once its `solve` process runs, the game just answers its submits, and files are judged between them.
            if (CapabilitiesLocked || _solverRunning && _solveProcess == null || PlaybackActive || _recorder.IsRecording
                || _bootstrapRequest != null || _webServer == null) return;
            Movement player = FindWorldMovement();
            if (player == null || MovIsOnMainMenuField != null && MovIsOnMainMenuField.GetValue(player) is bool menu && menu) return;
            var groups = Directory.GetFiles(dir, "*.json").Select(ReadOfflineBest).Where(f => f != null).GroupBy(f => f.group);
            string judged = Path.Combine(dir, "judged");
            // The running solve submits its own bests; its group waits until the process ends (then catches what it missed).
            string solving = !_solverRunning ? null : _solveCourse + "_" + _solveCaps;
            foreach (var group in groups)
            {
                if (group.Key == solving || group.Key == FullTasSolvingGroup) continue;
                var files = group.OrderBy(f => f.time).ToList();
                OfflineBest first = files[0];
                // A course file whose own category the course doesn't have is only moved away.
                bool judgeable = first.judgings.Count > 0;
                float best = judgeable ? CategoryBest(first.judgings[0].category) : 0f;
                bool started = _offlineJudged.Any(k => k.StartsWith(first.path + "|", StringComparison.Ordinal));
                // As fast as the saved route is judged too: it may get there in fewer physics ticks from the run start.
                if (judgeable && (started || first.time <= best + .0001f))
                {
                    var next = first.judgings.FirstOrDefault(j => !_offlineJudged.Contains(first.path + "|" + j.label));
                    if (next.body != null)
                    {
                        if (next.body["fulltas"] != null && !FullTasCanJudge()) continue;
                        _offlineBestCheckAt = Time.unscaledTime + 1f;
                        string line = "Offline best " + Path.GetFileName(first.path) + " as " + next.category + ": ";
                        try
                        {
                            if (next.body["fulltas"] != null) line += JudgeFullTasFile(first.path, (string)next.body["fulltas"]);
                            else
                            {
                                object error = PrepareEngineSimRun(new WebStudioRequest(), next.body, out _);
                                if (error != null) return;
                                List<SolverTick> inputs = JObject.Parse(File.ReadAllText(first.path))["ticks"].ToObject<List<SolverTick>>();
                                JudgeResult r = Judge(inputs);
                                line += (r.finished ? "turbo " + r.time.ToString("0.00", CultureInfo.InvariantCulture) + "s (run " + r.runTicks + " ticks"
                                        + (r.previousRunTicks is long was ? ", saved route's " + was : "") + ")" : "no finish in turbo")
                                    + (r.saved ? ", saved" : r.rejected != null ? ", " + r.rejected : "");
                            }
                        }
                        catch (Exception ex) { line += "error " + ex.Message; }
                        _offlineJudged.Add(first.path + "|" + next.label);
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

        // The whole run (physics ticks from the run start through the finish) of the category's saved route(s) timed
        // `time`, replayed in turbo: what an equally fast route must beat. long.MaxValue when none replays in that time.
        long SavedRouteRunTicks(string category, float time)
        {
            long fewest = long.MaxValue;
            foreach (MacroDefinition m in _library.macros.ToList())
            {
                Match g = CategoryRoute.Match(m.name ?? "");
                if (!g.Success || !string.Equals(g.Groups[1].Value, category, StringComparison.OrdinalIgnoreCase)
                    || Mathf.Abs(float.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture) - time) > .0001f) continue;
                if (Evaluate(ToTicks(m.steps), out float replayed, out _, out _) && Mathf.Abs(replayed - time) <= .0001f)
                    fewest = Math.Min(fewest, _engine.PhysicsTicks);
            }
            return fewest;
        }

        // {course, capabilities; ticks: [{x, held, press, release, dash, dashJump, reset}] or routeId, name,
        // world, freshStart} -> trace file with the start state (what every solver run resumes from) and the full state after
        // every stepped tick; with world: true also the world export as that run saw it. {fulltas: <run>, ...}: FullTasTrace.
        object EngineSimTrace(WebStudioRequest request, JObject body)
        {
            object error = PrepareEngineSimRun(request, body, out List<SolverTick> inputs);
            if (error != null) return error;
            CourseDef finishCourse = _solverTarget;
            int course = finishCourse.id;
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
                if (finishCourse != null && finishCourse.HasFinish)
                {
                    // The bonus course's reward box or a transition's end box, in this run's world coordinates, so offline
                    // runs finish exactly where this one did.
                    Vector2 origin = CurrentOrigin();
                    w.WritePropertyName("finish"); w.WriteStartArray();
                    json.Float(finishCourse.endX + origin.x); json.Float(finishCourse.endY + origin.y);
                    json.Float(finishCourse.endW); json.Float(finishCourse.endH);
                    w.WriteEndArray();
                }
                // A transition (Simulation.SetTarget): timed in physics ticks, and ends at its end box or at the start gate
                // of course `finishStart`.
                if (finishCourse != null && finishCourse.IsTransition)
                {
                    w.WritePropertyName("transition"); w.WriteValue(true);
                    if (finishCourse.toCourse > 0) { w.WritePropertyName("finishStart"); w.WriteValue(finishCourse.toCourse); }
                }
                w.WritePropertyName("capabilities"); w.WriteValue(caps);
                w.WritePropertyName("fixedDeltaTime"); json.Float(Time.fixedDeltaTime);
                w.WritePropertyName("inputs"); w.WriteRawValue(JsonConvert.SerializeObject(inputs));
                w.WritePropertyName("start"); EngineSimExport.WriteSnapshot(json, _startState, player, false);
                w.WritePropertyName("ticks");
                w.WriteStartArray();
                _engineSimTickHook = p =>
                {
                    var snapshot = TurboRunner.Capture(p, _solverCourses, _engine.CaptureState(), (int)_engine.RunTicks, false, false);
                    EngineSimExport.WriteSnapshot(json, snapshot, p, true);
                };
                try { finished = Evaluate(inputs, out time, out finishTick, out end); }
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

        // UnityEngine.Random.State's four private ints, in declaration order (s0, s1, s2, s3 in UnityEngine.CoreModule).
        static readonly FieldInfo[] RandomStateFields = typeof(UnityEngine.Random.State).GetFields(InstanceFields);

        // A snapshot's RNG state ("random": [s0, s1, s2, s3]) — the start state's at the start, each tick's after it.
        static void WriteRandom(EngineSimJson json, UnityEngine.Random.State state)
        {
            object boxed = state;
            JsonTextWriter w = json.W;
            w.WriteStartArray();
            foreach (FieldInfo field in RandomStateFields) w.WriteValue((int)field.GetValue(boxed));
            w.WriteEndArray();
        }

        public static void WriteSnapshot(EngineSimJson json, TurboRunner.Snapshot s, Movement player, bool live)
        {
            JsonTextWriter w = json.W;
            w.WriteStartObject();
            w.WritePropertyName("tick"); w.WriteValue(s.tick);
            w.WritePropertyName("random"); WriteRandom(json, s.random);
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
            if (s.economy != null)
            {
                // What the offline buy rule reads (enginesim Shop): every currency, and every upgrade box's own state.
                w.WritePropertyName("economy"); w.WriteStartObject();
                w.WritePropertyName("currencies"); w.WriteStartArray();
                if (s.economy.currencies != null)
                    foreach (KeyValuePair<globalStats.Currencies, double> c in s.economy.currencies)
                    {
                        w.WriteStartArray(); w.WriteValue((int)c.Key); w.WriteValue(c.Value); w.WriteEndArray();
                    }
                w.WriteEndArray();
                w.WritePropertyName("boxes"); w.WriteStartArray();
                foreach (var (box, fields) in s.economy.boxes)
                {
                    if (box == null) continue;
                    w.WriteStartObject();
                    w.WritePropertyName("id"); w.WriteValue(box.GetInstanceID());
                    w.WritePropertyName("fields"); WriteFieldValues(json, TurboRunner.EconomyFields(typeof(upgradeBox)), fields);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                // The native offline economy (enginesim Economy): the global and every course's local upgrades, what scales
                // a reward, and every course's clones (their own state; `path`: the clone path's length, the live one:
                // the snapshot doesn't hold the path, only a new best replaces it).
                if (s.economy.globalUpgrades != null) { w.WritePropertyName("globalUpgrades"); json.Value(s.economy.globalUpgrades, 0); }
                globalStats stats = Singleton<globalStats>.Instance;
                if (stats != null)
                {
                    w.WritePropertyName("gameSpeed"); json.Value(stats.GameSpeed, 0);
                    w.WritePropertyName("vmanMult"); json.Value(stats.vmanMult, 0);
                }
                w.WritePropertyName("locals"); w.WriteStartArray();
                foreach (var (script, upgrades) in s.economy.locals)
                {
                    if (script == null || upgrades == null) continue;
                    w.WriteStartObject();
                    w.WritePropertyName("id"); w.WriteValue(script.GetInstanceID());
                    w.WritePropertyName("upgrades"); json.Value(upgrades, 0);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WritePropertyName("clones"); w.WriteStartArray();
                foreach (var (clones, fields, _) in s.economy.clones)
                {
                    if (clones == null) continue;
                    w.WriteStartObject();
                    w.WritePropertyName("id"); w.WriteValue(clones.GetInstanceID());
                    w.WritePropertyName("path"); w.WriteValue(clones.clonePath == null ? 0 : clones.clonePath.Length);
                    w.WritePropertyName("fields"); WriteFieldValues(json, TurboRunner.EconomyFields(typeof(clonesScript)), fields);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            if (player != null)
            {
                // Not in the snapshot, but read by the game (facing via localScale, death via rotation).
                w.WritePropertyName("transformRotation"); json.Value(player.transform.rotation, 0);
                w.WritePropertyName("localScale"); json.Value(player.transform.localScale, 0);
                if (live)
                {
                    w.WritePropertyName("contacts");
                    WriteContacts(json, player.GetComponent<Rigidbody2D>());
                    // After the tick's clone payouts and purchases (enginesim Shop.Cash).
                    w.WritePropertyName("cash"); w.WriteValue(globalStats.currencyLookup[globalStats.Currencies.Cash]);
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
            WriteFieldValues(json, FieldState.For(type), values);
        }

        static void WriteFieldValues(EngineSimJson json, FieldInfo[] fields, object[] values)
        {
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
        // every script; plus the global 2D physics settings. The coloured blocks' composites also carry "sourceShapes":
        // their geometry with their tilemap colliders enabled (a trip breaker or a block swap turns the other colour's
        // on at run time; the offline engine swaps to it).
        public static int WriteWorld(string path)
        {
            var gameObjects = new List<GameObject>();
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go != null && go.scene.IsValid() && (go.hideFlags & HideFlags.HideAndDontSave) == 0) gameObjects.Add(go);
            var materials = new Dictionary<int, PhysicsMaterial2D>();
            Dictionary<CompositeCollider2D, PhysicsShapeGroup2D> sourceShapes = ColouredBlockSourceShapes();
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
                foreach (GameObject go in gameObjects) WriteGameObject(json, go, materials, sourceShapes);
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

        // The colouredBlockSwapper's composites (one per colour object, fed by its TilemapCollider2D) with their sources
        // enabled: the sources that are off are switched on for this frame only, the composites regenerated and read,
        // then the sources are switched back off and the composites regenerated again. No physics step runs in
        // between, so no contact or callback sees it; the composites must end as they started (checked).
        static Dictionary<CompositeCollider2D, PhysicsShapeGroup2D> ColouredBlockSourceShapes()
        {
            var result = new Dictionary<CompositeCollider2D, PhysicsShapeGroup2D>();
            colouredBlockSwapper swapper = UnityEngine.Object.FindAnyObjectByType<colouredBlockSwapper>(FindObjectsInactive.Include);
            if (swapper == null) return result;
            var composites = new List<(CompositeCollider2D composite, Collider2D[] sources, int paths, int points, int shapes)>();
            foreach (string colour in new[] { "orange", "blue" })
                foreach (GameObject o in (GameObject[])typeof(colouredBlockSwapper).GetField(colour, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(swapper) ?? new GameObject[0])
                {
                    CompositeCollider2D composite = o == null ? null : o.GetComponent<CompositeCollider2D>();
                    if (composite == null) continue;
                    Collider2D[] sources = o.GetComponents<Collider2D>()
                        .Where(c => c != composite && c.compositeOperation != Collider2D.CompositeOperation.None).ToArray();
                    composites.Add((composite, sources, composite.pathCount, composite.pointCount, composite.shapeCount));
                }
            var switchedOn = new List<Collider2D>();
            try
            {
                foreach (var c in composites)
                    foreach (Collider2D source in c.sources)
                        if (!source.enabled) { source.enabled = true; switchedOn.Add(source); }
                foreach (var c in composites)
                {
                    foreach (Collider2D source in c.sources) source.GetType().GetMethod("ProcessTilemapChanges", Type.EmptyTypes)?.Invoke(source, null);
                    c.composite.GenerateGeometry();
                    var group = new PhysicsShapeGroup2D();
                    c.composite.GetShapes(group);
                    result[c.composite] = group;
                }
            }
            finally
            {
                foreach (Collider2D source in switchedOn) source.enabled = false;
                foreach (var c in composites)
                {
                    foreach (Collider2D source in c.sources) source.GetType().GetMethod("ProcessTilemapChanges", Type.EmptyTypes)?.Invoke(source, null);
                    c.composite.GenerateGeometry();
                }
            }
            foreach (var c in composites)
                if (c.composite.pathCount != c.paths || c.composite.pointCount != c.points || c.composite.shapeCount != c.shapes)
                    throw new InvalidOperationException("World export: " + c.composite.name + "'s composite didn't come back as it was ("
                        + c.paths + "/" + c.points + "/" + c.shapes + " paths/points/shapes, now " + c.composite.pathCount + "/"
                        + c.composite.pointCount + "/" + c.composite.shapeCount + ").");
            return result;
        }

        static void WriteGameObject(EngineSimJson json, GameObject go, Dictionary<int, PhysicsMaterial2D> materials,
            Dictionary<CompositeCollider2D, PhysicsShapeGroup2D> sourceShapes)
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
                    if (col is CompositeCollider2D composite && sourceShapes.TryGetValue(composite, out PhysicsShapeGroup2D group))
                    {
                        w.WritePropertyName("sourceShapes");
                        WriteShapeGroup(json, group, group.shapeCount);
                    }
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
            var group = new PhysicsShapeGroup2D();
            int count;
            try { count = collider.GetShapes(group); }
            catch (Exception) { json.W.WriteNull(); return; }
            WriteShapeGroup(json, group, count);
        }

        static void WriteShapeGroup(EngineSimJson json, PhysicsShapeGroup2D group, int count)
        {
            JsonTextWriter w = json.W;
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
