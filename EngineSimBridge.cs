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
    public sealed partial class TasPlugin
    {
        string EngineSimDir { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "enginesim"); } }
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
                Evaluate(new List<SolverTick> { new SolverTick() }, out _, out _, out _);
                if (_startState == null) throw new InvalidOperationException("The start state was not captured.");
                _engineSimStartKey = key;
            }
            return null;
        }

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

        object EngineSimSubmit(WebStudioRequest request, JObject body)
        {
            object error = PrepareEngineSimRun(request, body, out List<SolverTick> inputs);
            if (error != null) return error;
            return Judge(inputs);
        }

        object EngineSimPlay(JObject body)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_solverRunning) throw new InvalidOperationException("Stop the solver first.");
            int course = Number(body, "course", 0);
            string caps = Text(body, "capabilities");
            CourseDef def = _courses.Find(c => c.id == course) ?? throw new InvalidOperationException("Unknown course " + course + ".");
            if (caps == null || !CapabilityLabels.ContainsKey(caps)) throw new InvalidOperationException("Unknown capabilities.");
            string category = def.Token + "_" + CapabilityLabels[caps];
            bool human = Truthy(body, "human");
            var newest = OfflineBestFiles(course, caps, human ? HumanBestFile : OfflineBestFile).FirstOrDefault();
            if (newest.path == null) throw new InvalidOperationException("No offline best" + (human ? " with a human version" : "") + " for " + category + ".");
            List<SolverTick> inputs = JObject.Parse(File.ReadAllText(newest.path))["ticks"].ToObject<List<SolverTick>>();
            var route = new MacroDefinition { id = human ? HumanPlaybackId : "offline-best", name = category + (human ? " human " : " offline ") + newest.match.Groups[3].Value, steps = ToSteps(inputs) };
            EnsureGameUnpaused();
            _pendingReplay = route;
            _pendingReplayStartDelayTicks = 0;
            return new { ok = true, name = route.name, file = Path.GetFileName(newest.path) };
        }

        // offline-best and offline-best/judged files of one course and category matching `pattern`, newest first.
        IEnumerable<(string path, Match match)> OfflineBestFiles(int course, string caps, Regex pattern)
        {
            string root = Path.Combine(EngineSimDir, "offline-best");
            return new[] { root, Path.Combine(root, "judged") }.Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, course + "_" + caps + "-*.json"))
                .Select(p => (path: p, match: pattern.Match(Path.GetFileName(p))))
                .Where(f => f.match.Success)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f.path));
        }

        // The newest human version as fast as a saved route: the same game time for a category route, the same physics
        // ticks for a farm route. Null when there is none.
        MacroDefinition HumanVersionOf(MacroDefinition route)
        {
            Match g = CategoryRoute.Match(route.name ?? "");
            bool farm = !g.Success && (g = FarmRoute.Match(route.name ?? "")).Success;
            if (!g.Success) return null;
            string[] category = g.Groups[1].Value.Split(new[] { '_' }, 2);
            CourseDef def = _courses.Find(c => string.Equals(c.Token, category[0], StringComparison.OrdinalIgnoreCase));
            if (def == null) return null;
            float routeTime = float.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture);
            string caps = CapsFor(category[1]);
            foreach (var file in OfflineBestFiles(def.id, caps, HumanBestFile))
            {
                JObject json;
                try { json = JObject.Parse(File.ReadAllText(file.path)); }
                catch (Exception) { continue; }
                bool same = farm ? (long?)json["runTicks"] == FarmTicks(routeTime)
                    : json["time"] != null && ((float)json["time"]).ToString("0.00", CultureInfo.InvariantCulture) == routeTime.ToString("0.00", CultureInfo.InvariantCulture);
                if (!same) continue;
                return new MacroDefinition { id = HumanPlaybackId, name = route.name + " human", steps = ToSteps(json["ticks"].ToObject<List<SolverTick>>()) };
            }
            return null;
        }

        JudgeResult Judge(List<SolverTick> inputs)
        {
            bool finished = Evaluate(inputs, out float time, out int finishTick, out _);
            long runTicks = _engine.PhysicsTicks;
            string category = SolverCategory;
            float best = CategoryBest(category);
            long farmBest = FarmTracked ? FarmBestTicks(category) : long.MaxValue;
            ParkAtStart();
            var result = new JudgeResult { finished = finished, time = time, finishTick = finishTick, runTicks = runTicks,
                previousBest = float.IsInfinity(best) ? (float?)null : best,
                previousFarmTicks = farmBest == long.MaxValue ? (long?)null : farmBest };
            if (!finished) return result;
            List<SolverTick> route = inputs.Take(finishTick + 1).Select(k => k.Clone()).ToList();
            if (time < best - .0001f)
            {
                result.saved = SaveSolverRoute(route, time);
                if (!result.saved) result.rejected = _solverLog[_solverLog.Count - 1];
            }
            if (FarmTracked && runTicks < farmBest)
            {
                result.farmSaved = SaveSolverRoute(route, time, runTicks);
                if (!result.farmSaved) result.rejected = (result.rejected == null ? "" : result.rejected + "; ") + _solverLog[_solverLog.Count - 1];
            }
            return result;
        }

        void ParkAtStart()
        {
            RestoreRouteStart(new MacroDefinition { name = _solverTarget.Token + "_" });
        }

        sealed class JudgeResult
        {
            public bool ok = true, finished, saved, farmSaved;
            public float time;
            public int finishTick;
            public long runTicks;
            public float? previousBest;
            public long? previousFarmTicks;
            public string rejected;
        }

        static readonly Regex OfflineBestFile = new Regex(@"^(\d+)_([a-z]+)-([\d.]+)\.json$");
        // enginesim humanize's same-time versions of a best: played for practice, never judged.
        static readonly Regex HumanBestFile = new Regex(@"^(\d+)_([a-z]+)-([\d.]+)-human\.json$");
        // The playback id of a human version: the studio colors the run so it is never mistaken for the best.
        const string HumanPlaybackId = "offline-best-human";
        float _offlineBestCheckAt;
        readonly HashSet<string> _offlineJudged = new HashSet<string>();

        sealed class OfflineBest
        {
            public string path, group;
            public float time;
            public long runTicks = long.MaxValue;
            public bool farm;
            public List<(string label, JObject body, string category)> judgings;
        }

        // `solving`: the group a running solve keeps rewriting; its files aren't opened, so the solve's writes never meet a read.
        OfflineBest ReadOfflineBest(string path, string solving)
        {
            string file = Path.GetFileName(path);
            Match m = OfflineBestFile.Match(file);
            if (m.Success)
            {
                int id = int.Parse(m.Groups[1].Value);
                string caps = m.Groups[2].Value;
                CourseDef course = _courses.Find(c => c.id == id);
                if (course == null || !CapabilityLabels.ContainsKey(caps) || id + "_" + caps == solving) return null;
                JObject json;
                try { json = JObject.Parse(File.ReadAllText(path)); }
                catch (Exception) { return null; }
                return new OfflineBest { path = path, group = id + "_" + caps, time = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    runTicks = (long?)json["runTicks"] ?? long.MaxValue, farm = !course.IsBonus && !course.IsTransition,
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

        int _offlineWaiting;

        void PumpOfflineBests()
        {
            if (Time.unscaledTime < _offlineBestCheckAt) return;
            _offlineBestCheckAt = Time.unscaledTime + 5f;
            string dir = Path.Combine(EngineSimDir, "offline-best");
            _offlineWaiting = !Directory.Exists(dir) ? 0 : Directory.GetFiles(dir, "*.json")
                .Count(p => OfflineBestFile.IsMatch(Path.GetFileName(p)) || OfflineFullTasBestFile.IsMatch(Path.GetFileName(p)));
            if (_offlineWaiting == 0) return;
            if (CapabilitiesLocked || _solverRunning && _solveProcess == null || PlaybackActive || _recorder.IsRecording
                || _bootstrapRequest != null || _webServer == null) return;
            Movement player = FindWorldMovement();
            if (player == null || MovIsOnMainMenuField != null && MovIsOnMainMenuField.GetValue(player) is bool menu && menu) return;
            string solving = !_solverRunning ? null : _solveCourse + "_" + _solveCaps;
            var groups = Directory.GetFiles(dir, "*.json").Select(f => ReadOfflineBest(f, solving)).Where(f => f != null).GroupBy(f => f.group);
            string judged = Path.Combine(dir, "judged");
            foreach (var group in groups)
            {
                if (group.Key == solving || group.Key == FullTasSolvingGroup) continue;
                var files = group.OrderBy(f => f.time).ThenBy(f => f.runTicks).ToList();
                OfflineBest fastest = files[0], fewest = files.OrderBy(f => f.runTicks).First();
                bool judgeable = fastest.judgings.Count > 0;
                float best = judgeable ? CategoryBest(fastest.judgings[0].category) : 0f;
                long farmBest = judgeable && fastest.farm ? FarmBestTicks(fastest.judgings[0].category) : long.MinValue;
                OfflineBest first = null;
                (string label, JObject body, string category) next = default;
                foreach (var (file, worth) in new[] { (fastest, fastest.time <= best + .0001f), (fewest, fewest.runTicks < farmBest) })
                {
                    if (!worth && !_offlineJudged.Any(k => k.StartsWith(file.path + "|", StringComparison.Ordinal))) continue;
                    next = file.judgings.FirstOrDefault(j => !_offlineJudged.Contains(file.path + "|" + j.label));
                    if (next.body != null) { first = file; break; }
                }
                if (first != null)
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
                                    + (r.previousFarmTicks is long farm ? ", farm route's " + farm : "") + ")" : "no finish in turbo")
                                + (r.saved ? ", saved" : "") + (r.farmSaved ? ", saved as farm" : "") + (r.rejected != null ? ", " + r.rejected : "");
                        }
                    }
                    catch (Exception ex) { line += "error " + ex.Message; }
                    _offlineJudged.Add(first.path + "|" + next.label);
                    Logger.LogInfo(line);
                    return;
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

        long FarmBestTicks(string category)
        {
            long best = long.MaxValue;
            foreach (MacroDefinition m in _library.macros)
            {
                Match g = FarmRoute.Match(m.name ?? "");
                if (g.Success && string.Equals(g.Groups[1].Value, category, StringComparison.OrdinalIgnoreCase))
                    best = Math.Min(best, FarmTicks(float.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture)));
            }
            return best;
        }

        object EngineSimTrace(WebStudioRequest request, JObject body)
        {
            object error = PrepareEngineSimRun(request, body, out List<SolverTick> inputs);
            if (error != null) return error;
            CourseDef finishCourse = _solverTarget;
            int course = finishCourse.id;
            string caps = _solverCaps;
            string name = Text(body, "name") ?? ("trace-" + course + "_" + caps);
            string path = Path.Combine(EngineSimDir, name + ".json");
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
                    Vector2 origin = CurrentOrigin();
                    w.WritePropertyName("finish"); w.WriteStartArray();
                    json.Float(finishCourse.endX + origin.x); json.Float(finishCourse.endY + origin.y);
                    json.Float(finishCourse.endW); json.Float(finishCourse.endH);
                    w.WriteEndArray();
                }
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

        static readonly FieldInfo[] RandomStateFields = typeof(UnityEngine.Random.State).GetFields(InstanceFields);

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
            foreach (var (due, target, method) in s.pending)
                if (target == player) { w.WriteStartArray(); w.WriteValue(due); w.WriteValue(method); w.WriteEndArray(); }
            w.WriteEndArray();
            if (s.economy != null)
            {
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
                w.WritePropertyName("transformRotation"); json.Value(player.transform.rotation, 0);
                w.WritePropertyName("localScale"); json.Value(player.transform.localScale, 0);
                if (live)
                {
                    w.WritePropertyName("contacts");
                    WriteContacts(json, player.GetComponent<Rigidbody2D>());
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
