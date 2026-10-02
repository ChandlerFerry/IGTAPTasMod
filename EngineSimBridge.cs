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
                return body["tech"] != null ? EngineSimTechTrace(request, body) : body["fulltas"] != null ? FullTasTraceRequest(request, body) : EngineSimTrace(request, body);
            if (request.Path == "/api/enginesim/submit")
                return EngineSimSubmit(request, body);
            if (request.Path == "/api/enginesim/map")
                return EngineSimMap(request, body);
            if (request.Path == "/api/enginesim/ghost")
                return EngineSimGhost(request, body);
            if (request.Path == "/api/enginesim/slack")
                return EngineSimSlack(request, body);
            if (request.Path == "/api/enginesim/ultras")
                return EngineSimUltras(request, body);
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
            if (WorldNotLoaded() is string notLoaded)
            {
                request.StatusCode = 409;
                return new { ok = false, error = notLoaded };
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
            // Start states are per category (a submit of another category must not discard this one's: the solver's Confirm
            // judges every other category before the target's), and the target's is the one its trace was written from. A
            // FloatingOrigin move keeps them (a restore puts the world back at the snapshot's origin, TurboRunner.PutOrigin):
            // a far course's runs and restarts rebase, and each recapture there dropped the trace's start. A fresh start recaptures all.
            // A cached start only judges: a run it would save is judged again from a start captured now (Judge).
            // A snapshot doesn't hold which zone is loaded or the Area 1 state's scenery: after a run on a course in another
            // zone or Area 1 state (the offline-best pump judges every course's files in turn), a cached start would run in
            // the wrong world, and a good route would not finish. The start is captured again then, which loads the course's world.
            if (Truthy(body, "freshStart")) _startStates.Clear();
            _startKey = key;
            if (!ZoneReady(_solverTarget) || !Area1StateReady(_solverTarget)) _startStates.Remove(key);
            _startFresh = !_startStates.TryGetValue(key, out TurboRunner.Snapshot cached);
            if (_startFresh)
            {
                _startState = null;
                if (!CourseLoaded())
                {
                    request.StatusCode = 409;
                    return new { ok = false, error = "Course " + _solverTarget.Token + " is loading; retry in a moment." };
                }
                Evaluate(new List<SolverTick> { new SolverTick() }, out _, out _, out _);
                if (_startState == null)
                {
                    Logger.LogWarning("Start state of " + key + " not captured: " + _captureFailure);
                    throw new InvalidOperationException("The start state was not captured (" + _captureFailure + ").");
                }
                _startStates[key] = _startState;
            }
            else _startState = cached;
            return null;
        }

        string SelectSolverTarget(JObject body)
        {
            int course = Number(body, "course", 0);
            string caps = Text(body, "capabilities");
            CourseDef def = _courses.Find(c => c.id == course) ?? throw new InvalidOperationException("Unknown course " + course + ".");
            if (!Categories.IsCategory(caps)) throw new InvalidOperationException("Unknown capabilities.");
            CheckCategory(course, caps);
            _solverTarget = def;
            _solverCaps = caps;
            return course + "_" + caps;
        }

        /// <summary>The gate for a submitted route (`/api/enginesim/submit {course, capabilities, ticks | routeId, loopFrom?,
        /// loopLaps?}`): a farm loop (`loopFrom`, or a saved farm route's) is judged as the farm (<see cref="JudgeLoop"/>), any
        /// other as the record.</summary>
        object EngineSimSubmit(WebStudioRequest request, JObject body)
        {
            object error = PrepareEngineSimRun(request, body, out List<SolverTick> inputs);
            if (error != null) return error;
            string routeId = Text(body, "routeId");
            MacroDefinition saved = string.IsNullOrEmpty(routeId) ? null : _library.macros.Find(m => m.id == routeId);
            int loopFrom = saved != null ? saved.loopFrom : Number(body, "loopFrom", 0), loopLaps = saved != null ? saved.loopLaps : Number(body, "loopLaps", 1);
            JudgeResult result = loopFrom > 0 ? JudgeLoop(inputs, loopFrom, loopLaps) : Judge(inputs);
            if (result != null) return result;
            request.StatusCode = 409;
            return new { ok = false, error = RecaptureError };
        }

        object EngineSimPlay(JObject body)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_solverRunning) throw new InvalidOperationException("Stop the solver first.");
            int course = Number(body, "course", 0);
            string caps = Text(body, "capabilities");
            CourseDef def = _courses.Find(c => c.id == course) ?? throw new InvalidOperationException("Unknown course " + course + ".");
            if (!Categories.IsCategory(caps)) throw new InvalidOperationException("Unknown capabilities.");
            string category = def.Token + "_" + Caps.Parse(caps).Label;
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

        // The studio's ghost compare: two routes' per-tick positions, per-cell first arrivals and physical state
        // fingerprints (position, Velocity, momentum, body velocity, ground/wall, dash state, jumps/dashes left; no
        // input or buffer fields), from the map JSON the engine's `map` wrote for this course and category (or a
        // Full-TAS run), plus the first tick their bodies part (the first differing per-tick fingerprint). Read-only:
        // the file on disk.
        object EngineSimGhost(WebStudioRequest request, JObject body)
        {
            string mapPath;
            string fullTas = Text(body, "fulltas");
            if (fullTas != null)
            {
                if (!Regex.IsMatch(fullTas, "^[A-Za-z0-9%-]+$"))
                {
                    request.StatusCode = 400;
                    return new { ok = false, error = "Unknown Full-TAS run '" + fullTas + "'." };
                }
                mapPath = Path.Combine(EngineSimDir, "map-fulltas-" + fullTas + ".json");
            }
            else
            {
                int id = Number(body, "course", 0);
                string caps = Text(body, "capabilities");
                CourseDef course = _courses.FirstOrDefault(c => c.id == id);
                if (course == null || !course.HasCategory(caps))
                {
                    request.StatusCode = 400;
                    return new { ok = false, error = "Unknown course " + id + " or capabilities '" + caps + "'." };
                }
                mapPath = Path.Combine(EngineSimDir, "map-c" + course.Token + "-" + caps + ".json");
            }
            if (!File.Exists(mapPath))
            {
                request.StatusCode = 404;
                return new { ok = false, error = "No map for this route yet: draw it with Show map first." };
            }
            JObject map;
            try { map = JObject.Parse(File.ReadAllText(mapPath)); }
            catch (Exception ex)
            {
                request.StatusCode = 500;
                return new { ok = false, error = Path.GetFileName(mapPath) + ": " + ex.Message };
            }
            JToken routes = map["routes"];
            JToken a = routes == null ? null : routes.FirstOrDefault(r => (string)r["name"] == Text(body, "a"));
            JToken b = routes == null ? null : routes.FirstOrDefault(r => (string)r["name"] == Text(body, "b"));
            if (a == null || b == null)
            {
                request.StatusCode = 404;
                return new { ok = false, error = "Route " + (a == null ? Text(body, "a") : Text(body, "b")) + " is not on this map: redraw it." };
            }
            if (a["state"] == null || b["state"] == null || a["cells"] == null || b["cells"] == null)
            {
                request.StatusCode = 409;
                return new { ok = false, error = "This map predates the ghost compare: press Redraw." };
            }
            JArray sa = (JArray)a["state"], sb = (JArray)b["state"];
            int common = Math.Min(sa.Count, sb.Count), diverge = -1;
            for (int i = 0; i < common; i++)
                if ((int)sa[i] != (int)sb[i]) { diverge = i; break; }
            object mark = null;
            if (diverge >= 0)
            {
                // The first tick the states differ; where one run simply ended first, its last tick's position.
                JArray points = (JArray)(diverge < common ? a["points"] : (sa.Count > sb.Count ? a["points"] : b["points"]));
                int at = diverge * 2;
                mark = new
                {
                    tick = diverge,
                    x = at + 1 < points.Count ? (float)points[at] : (float)points[points.Count - 2],
                    y = at + 1 < points.Count ? (float)points[at + 1] : (float)points[points.Count - 1],
                };
            }
            return new { ok = true, cell = (float?)map["explored"]?["cell"] ?? 16f, a, b, diverge = mark };
        }

        // The humanize slack report of a route's human version (`<best>-human.slack.json`, written by
        // `IGTAP.EngineSim humanize`) with the human version's input ticks for the TAS tab's roll. Read-only.
        object EngineSimSlack(WebStudioRequest request, JObject body)
        {
            string name = Text(body, "name") ?? "";
            Match g = MatchCategoryRoute(name);
            bool farm = !g.Success && (g = MatchFarmRoute(name)).Success;
            if (!g.Success)
            {
                request.StatusCode = 404;
                return new { ok = false, error = "No human version for " + name + ": only a category or farm route has one." };
            }
            CourseDef course = _courses.Find(c => string.Equals(c.Token, g.Groups["token"].Value, StringComparison.OrdinalIgnoreCase));
            if (course != null)
            {
                string caps = CapsOf(g.Groups["label"].Value).Id;
                float routeTime = float.Parse(g.Groups["time"].Value, CultureInfo.InvariantCulture);
                foreach (var file in OfflineBestFiles(course.id, caps, HumanSlackFile))
                {
                    JObject slack;
                    try { slack = JObject.Parse(File.ReadAllText(file.path)); }
                    catch (Exception) { continue; }
                    bool same = farm ? LoopLapText(slack) == FarmSeconds(routeTime)
                        : slack["time"] != null && ((float)slack["time"]).ToString("0.00", CultureInfo.InvariantCulture) == routeTime.ToString("0.00", CultureInfo.InvariantCulture);
                    if (!same) continue;
                    string humanPath = file.path.Substring(0, file.path.Length - ".slack.json".Length) + ".json";
                    JArray ticks = null;
                    if (File.Exists(humanPath))
                        try { ticks = (JArray)JObject.Parse(File.ReadAllText(humanPath))["ticks"]; } catch (Exception) { }
                    return new { ok = true, file = Path.GetFileName(file.path), human = Path.GetFileName(humanPath), slack, ticks };
                }
            }
            request.StatusCode = 404;
            return new { ok = false, error = "No slack report for " + name + " yet: humanize writes one next to every best." };
        }

        IEnumerable<(string path, Match match)> OfflineBestFiles(int course, string caps, Regex pattern)
        {
            string root = Path.Combine(EngineSimDir, "offline-best");
            return new[] { root, Path.Combine(root, "judged") }.Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, course + "_" + caps + "-*.json"))
                .Select(p => (path: p, match: pattern.Match(Path.GetFileName(p))))
                .Where(f => f.match.Success)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f.path));
        }

        MacroDefinition HumanVersionOf(MacroDefinition route)
        {
            Match g = MatchCategoryRoute(route.name);
            bool farm = !g.Success && (g = MatchFarmRoute(route.name)).Success;
            if (!g.Success) return null;
            CourseDef def = _courses.Find(c => string.Equals(c.Token, g.Groups["token"].Value, StringComparison.OrdinalIgnoreCase));
            if (def == null) return null;
            float routeTime = float.Parse(g.Groups["time"].Value, CultureInfo.InvariantCulture);
            string caps = CapsOf(g.Groups["label"].Value).Id;
            foreach (var file in OfflineBestFiles(def.id, caps, HumanBestFile))
            {
                JObject json;
                try { json = JObject.Parse(File.ReadAllText(file.path)); }
                catch (Exception) { continue; }
                bool same = farm ? LoopLapText(json) == FarmSeconds(routeTime)
                    : json["time"] != null && ((float)json["time"]).ToString("0.00", CultureInfo.InvariantCulture) == routeTime.ToString("0.00", CultureInfo.InvariantCulture);
                if (!same) continue;
                return new MacroDefinition { id = HumanPlaybackId, name = route.name + " human", steps = ToSteps(json["ticks"].ToObject<List<SolverTick>>()) };
            }
            return null;
        }

        const string RecaptureError = "A new best: its start is loading again from a quick restart before the save; retry in a moment.";

        /// <summary>
        /// Turbo replay of a candidate, saved (SaveSolverRoute) when it beats the record. Null when it would be saved but its
        /// start came from the cache: the cached start is dropped and the caller retries, so the replay that saves starts from a
        /// quick restart captured for it, never from a snapshot an earlier game state left.
        /// </summary>
        JudgeResult Judge(List<SolverTick> inputs)
        {
            bool finished = Evaluate(inputs, out float time, out int finishTick, out _);
            long runTicks = _engine.PhysicsTicks;
            float best = CategoryBest(SolverCategory);
            ParkAtStart();
            var result = new JudgeResult { finished = finished, time = time, finishTick = finishTick, runTicks = runTicks,
                previousBest = float.IsInfinity(best) ? (float?)null : best };
            if (!finished || time >= best - .0001f) return result;
            if (!_startFresh)
            {
                _startStates.Remove(_startKey);
                _startState = null;
                return null;
            }
            result.saved = SaveSolverRoute(inputs.Take(finishTick + 1).Select(k => k.Clone()).ToList(), time);
            if (!result.saved) result.rejected = _solverLog[_solverLog.Count - 1];
            return result;
        }

        /// <summary>
        /// Turbo replay of a farm loop (<see cref="EvaluateLoop"/>), saved (SaveFarmLoop) when its steady cycle beats the farm
        /// route's. Null as <see cref="Judge"/>'s: a new best from a cached start is judged again from a fresh one.
        /// </summary>
        JudgeResult JudgeLoop(List<SolverTick> inputs, int loopFrom, int laps)
        {
            LoopResult loop = EvaluateLoop(inputs, loopFrom, laps);
            double farmBest = FarmTracked ? FarmBest(SolverCategory) : double.NegativeInfinity;
            ParkAtStart();
            var result = new JudgeResult { finished = loop.steady, loop = loop, previousFarm = double.IsInfinity(farmBest) ? (double?)null : farmBest };
            if (!loop.steady || loop.lap >= farmBest - 1e-6) return result;
            if (!_startFresh)
            {
                _startStates.Remove(_startKey);
                _startState = null;
                return null;
            }
            result.farmSaved = SaveSolverRoute(inputs.Select(k => k.Clone()).ToList(), 0f, loopFrom, laps, loop.live);
            if (!result.farmSaved) result.rejected = _solverLog[_solverLog.Count - 1];
            return result;
        }

        void ParkAtStart()
        {
            if (_startState != null) TurboRunner.PutOrigin(_startState.origin);
            RestoreRouteStart(new MacroDefinition { name = _solverTarget.Token + "_" });
        }

        sealed class JudgeResult
        {
            public bool ok = true, finished, saved, farmSaved;
            public float time;
            public int finishTick;
            public long runTicks;
            public float? previousBest;
            /// <summary>A farm loop's judging: its replay, and the farm route's lap time it was held to (live seconds).</summary>
            public LoopResult loop;
            public double? previousFarm;
            public string rejected;
        }

        static readonly Regex OfflineBestFile = new Regex(@"^(\d+)_([a-z0-9]+)-([\d.]+)\.json$");
        /// <summary>A farm loop's offline best (the engine's FullTas.WriteLoopBest): its lap time in the name, its loop in the file.</summary>
        static readonly Regex OfflineLoopFile = new Regex(@"^(\d+)_([a-z0-9]+)-loop-([\d.]+)\.json$");

        /// <summary>A farm loop file's lap time as a farm route name has it (null: not a farm loop).</summary>
        static string LoopLapText(JObject loop)
        {
            long? live = (long?)loop["loopLive"];
            return live == null ? null : FarmSeconds(live.Value / 1400.0 / Math.Max(1, (int?)loop["loopLaps"] ?? 1));
        }

        static readonly Regex HumanBestFile = new Regex(@"^(\d+)_([a-z0-9]+)-([\d.]+)-human\.json$");
        static readonly Regex HumanSlackFile = new Regex(@"^(\d+)_([a-z0-9]+)-([\d.]+)-human\.slack\.json$");
        const string HumanPlaybackId = "offline-best-human";
        float _offlineBestCheckAt;
        readonly HashSet<string> _offlineJudged = new HashSet<string>();
        // The files with at least one judging done (_offlineJudged's keys are "<path>|<label>").
        readonly HashSet<string> _offlineJudgedPaths = new HashSet<string>();
        // Each offline best's runTicks by its path and write time: the pump reads them every 5 s, and a file doesn't change once
        // the solver wrote it.
        readonly CachedFiles<long> _offlineRunTicks = new CachedFiles<long>();

        sealed class OfflineBest
        {
            public string path, group;
            /// <summary>The record's time; a farm loop's lap time.</summary>
            public float time;
            public long runTicks = long.MaxValue;
            public bool farm;
            /// <summary>A farm loop: judged for the farm only.</summary>
            public bool loop;
            public List<(string label, JObject body, string category)> judgings;
        }

        /// <summary>Whether <see cref="ReadOfflineBest"/> can make anything of this file name (pool thread: no game state).</summary>
        static bool OfflineBestActionable(string file, HashSet<int> courseIds, HashSet<string> runs)
        {
            Match m = OfflineBestFile.Match(file);
            if (!m.Success) m = OfflineLoopFile.Match(file);
            if (m.Success) return int.TryParse(m.Groups[1].Value, out int id) && courseIds.Contains(id) && Categories.IsCategory(m.Groups[2].Value);
            m = OfflineFullTasBestFile.Match(file);
            return m.Success && runs.Contains(m.Groups[1].Value);
        }

        OfflineBest ReadOfflineBest(string path, string solving)
        {
            string file = Path.GetFileName(path);
            Match m = OfflineBestFile.Match(file);
            bool loop = !m.Success && (m = OfflineLoopFile.Match(file)).Success;
            if (m.Success)
            {
                int id = int.Parse(m.Groups[1].Value);
                string caps = m.Groups[2].Value;
                CourseDef course = _courses.Find(c => c.id == id);
                if (course == null || !Categories.IsCategory(caps) || id + "_" + caps == solving || loop && (course.IsBonus || course.IsTransition)) return null;
                var judgings = !course.HasCategory(caps) ? new List<(string, JObject, string)>() : CategoryJudgings(course, id, caps);
                float time = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (loop) return new OfflineBest { path = path, group = id + "_" + caps, time = time, farm = true, loop = true, judgings = judgings };
                long runTicks;
                try { runTicks = _offlineRunTicks.Get(path, p => (long?)JObject.Parse(File.ReadAllText(p))["runTicks"] ?? long.MaxValue); }
                catch (Exception) { return null; }
                return new OfflineBest { path = path, group = id + "_" + caps, time = time, runTicks = runTicks, farm = !course.IsBonus && !course.IsTransition, judgings = judgings };
            }
            m = OfflineFullTasBestFile.Match(file);
            if (m.Success)
            {
                FullTasRunDef run = _fullTasRuns.Find(r => string.Equals(r.name, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                if (run == null) return null;
                return new OfflineBest { path = path, group = "fulltas-" + run.name, time = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    judgings = new List<(string, JObject, string)> { (default(Caps).Label, new JObject { ["fulltas"] = run.name }, FullTasCategory(run.name)) } };
            }
            return null;
        }

        /// <summary>An offline best's judgings: its own category first, then every other category the target runs (registry order).</summary>
        static List<(string label, JObject body, string category)> CategoryJudgings(CourseDef course, int id, string caps)
        {
            var judgings = new List<(string, JObject, string)>();
            foreach (string other in new[] { caps }.Concat(course.CategoryIds.Where(k => k != caps)))
            {
                string label = Categories.Find(other).Label;
                judgings.Add((label, new JObject { ["course"] = id, ["capabilities"] = other }, course.Token + "_" + label));
            }
            return judgings;
        }

        int _offlineWaiting;
        // The offline bests waiting, counted on a pool thread (the directory listing, beside the offline solver's own writes,
        // could stall a paced frame every 5 s): -1 while that count runs, which _offlineCounting is set for. Only files the
        // pump can act on count (a known course and category, or a Full-TAS run still in fulltas.json): an orphan stays in
        // the folder unjudged, and counting it ran the whole pass, 3.4 ms and 400 KB on the main thread, every 5 s.
        int _offlineCount = -1;
        string[] _offlineFiles = new string[0];
        bool _offlineCounting;

        void PumpOfflineBests()
        {
            if (!_offlineCounting)
            {
                // Not while a route plays or records: it can't judge then, and the directory scan (hundreds of files) would
                // cost 1x playback's paced frames. The count is taken again after.
                if (Time.unscaledTime < _offlineBestCheckAt || PlaybackActive || _recorder.IsRecording) return;
                _offlineBestCheckAt = Time.unscaledTime + 5f;
                string scanned = Path.Combine(EngineSimDir, "offline-best");
                var courseIds = new HashSet<int>(_courses.Select(c => c.id));
                var runs = new HashSet<string>(_fullTasRuns.Select(r => r.name), StringComparer.OrdinalIgnoreCase);
                _offlineCount = -1;
                _offlineCounting = true;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    string[] files = new string[0];
                    try
                    {
                        if (Directory.Exists(scanned))
                            files = Directory.GetFiles(scanned, "*.json").Where(p => OfflineBestActionable(Path.GetFileName(p), courseIds, runs)).ToArray();
                    }
                    // A directory that went mid-scan: none waiting until the next count.
                    catch (Exception) { }
                    System.Threading.Volatile.Write(ref _offlineFiles, files);
                    System.Threading.Volatile.Write(ref _offlineCount, files.Length);
                });
                return;
            }
            int waiting = System.Threading.Volatile.Read(ref _offlineCount);
            if (waiting < 0) return;
            _offlineCounting = false;
            _offlineWaiting = waiting;
            if (_offlineWaiting == 0 || PlaybackActive || _recorder.IsRecording) return;
            string dir = Path.Combine(EngineSimDir, "offline-best");
            if (CapabilitiesLocked || _solverRunning && _solveProcess == null || _bootstrapRequest != null || _webServer == null) return;
            Movement player = FindWorldMovement();
            if (player == null || IsMenuPlayer(player)) return;
            string solving = !_solverRunning ? null : _solveCourse + "_" + _solveCaps;
            string[] bestFiles = System.Threading.Volatile.Read(ref _offlineFiles);
            var listed = new HashSet<string>(bestFiles);
            foreach (string gone in _offlineRunTicks.Keys.Where(p => !listed.Contains(p)).ToList()) _offlineRunTicks.Remove(gone);
            var groups = bestFiles.Select(f => ReadOfflineBest(f, solving)).Where(f => f != null).GroupBy(f => f.group);
            string judged = Path.Combine(dir, "judged");
            foreach (var group in groups)
            {
                if (group.Key == solving || group.Key == FullTasSolvingGroup) continue;
                var files = group.OrderBy(f => f.time).ThenBy(f => f.runTicks).ToList();
                // The record from a plain best, the farm from a farm loop (README ### Rules 30): a plain best is no farm loop.
                OfflineBest fastest = files.FirstOrDefault(f => !f.loop), fewest = files.FirstOrDefault(f => f.loop);
                OfflineBest any = fastest ?? fewest;
                bool judgeable = any.judgings.Count > 0;
                float best = judgeable ? CategoryBest(any.judgings[0].category) : 0f;
                double farmBest = judgeable && any.farm ? FarmBest(any.judgings[0].category) : double.NegativeInfinity;
                OfflineBest first = null;
                (string label, JObject body, string category) next = default;
                foreach (var (file, worth) in new[] { (fastest, fastest != null && fastest.time <= best + .0001f), (fewest, fewest != null && fewest.time < farmBest - 1e-6) })
                {
                    if (file == null || !worth && !_offlineJudgedPaths.Contains(file.path)) continue;
                    next = file.judgings.FirstOrDefault(j => !_offlineJudged.Contains(file.path + "|" + j.label));
                    if (next.body != null) { first = file; break; }
                }
                if (first != null)
                {
                    FullTasRunDef fullTasRun = next.body["fulltas"] == null ? null
                        : _fullTasRuns.Find(r => string.Equals(r.name, (string)next.body["fulltas"], StringComparison.OrdinalIgnoreCase));
                    if (fullTasRun != null && !FullTasCanJudge(fullTasRun)) continue;
                    _offlineBestCheckAt = Time.unscaledTime + 1f;
                    string line = "Offline best " + Path.GetFileName(first.path) + " as " + next.category + ": ";
                    try
                    {
                        if (next.body["fulltas"] != null) line += JudgeFullTasFile(first.path, (string)next.body["fulltas"]);
                        else
                        {
                            object error = PrepareEngineSimRun(new WebStudioRequest(), next.body, out _);
                            if (error != null) return;
                            JObject json = JObject.Parse(File.ReadAllText(first.path));
                            List<SolverTick> inputs = json["ticks"].ToObject<List<SolverTick>>();
                            JudgeResult r = first.loop ? JudgeLoop(inputs, (int?)json["loopFrom"] ?? 0, (int?)json["loopLaps"] ?? 1) : Judge(inputs);
                            if (r == null) return; // a new best from a cached start: judged again from a fresh one on the next pump
                            if (first.loop)
                                line += "turbo " + r.loop + (r.previousFarm is double farm ? ", farm route's " + FarmSeconds(farm) + "s" : "")
                                    + (r.farmSaved ? ", saved as farm" : "") + (r.rejected != null ? ", " + r.rejected : "");
                            else
                                line += (r.finished ? "turbo " + r.time.ToString("0.00", CultureInfo.InvariantCulture) + "s (run " + r.runTicks + " ticks)" : "no finish in turbo")
                                    + (r.saved ? ", saved" : "") + (r.rejected != null ? ", " + r.rejected : "");
                        }
                    }
                    catch (Exception ex) { line += "error " + ex.Message; }
                    _offlineJudged.Add(first.path + "|" + next.label);
                    _offlineJudgedPaths.Add(first.path);
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
                Match g = MatchCategoryRoute(m.name);
                if (g.Success && string.Equals(g.Groups["category"].Value, category, StringComparison.OrdinalIgnoreCase))
                    best = Math.Min(best, float.Parse(g.Groups["time"].Value, CultureInfo.InvariantCulture));
            }
            return best;
        }

        /// <summary>The category's farm route's lap time in live seconds (+infinity: none yet).</summary>
        double FarmBest(string category)
        {
            double best = double.PositiveInfinity;
            foreach (MacroDefinition m in _library.macros)
            {
                Match g = MatchFarmRoute(m.name);
                if (g.Success && m.loopFrom > 0 && string.Equals(g.Groups["category"].Value, category, StringComparison.OrdinalIgnoreCase))
                    best = Math.Min(best, double.Parse(g.Groups["time"].Value, CultureInfo.InvariantCulture));
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
            Movement player = FindWorldMovement();
            if (Truthy(body, "world"))
            {
                // The world as the run starts it, not as the live game has it: the category's abilities are applied while it
                // is written (below block swap the coloured blocks start unswapped, README ### Rules 13), so a trace's world
                // agrees with the state its run and the judge start from.
                List<(string field, object value)> exportAbilities = player == null ? null : ApplyAbilities(player, caps, finishCourse);
                try { EngineSimExport.WriteWorld(Path.Combine(EngineSimDir, name + ".world.json")); }
                finally
                {
                    if (exportAbilities != null) foreach (var (field, value) in exportAbilities) SetMovementField(player, field, value);
                }
            }
            bool finished;
            float time;
            int finishTick;
            Vector2 end;
            string temporary = path + ".tmp";
            using (var writer = new StreamWriter(temporary))
            {
                var json = new EngineSimJson(writer);
                JsonTextWriter w = json.W;
                w.WriteStartObject();
                WriteTraceTarget(json, finishCourse, course, caps, inputs);
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
            TasPlugin.ReplaceAtomic(temporary, path);
            return new { ok = true, path = path, finished = finished, time = time, finishTick = finishTick };
        }

        /// <summary>What a trace's run is scored on: the course (-1: none), its reward box or next course, unlocks, inputs.</summary>
        void WriteTraceTarget(EngineSimJson json, CourseDef finishCourse, int course, string caps, List<SolverTick> inputs)
        {
            JsonTextWriter w = json.W;
            w.WritePropertyName("course"); w.WriteValue(course);
            if (finishCourse != null && finishCourse.GameCourse != course) { w.WritePropertyName("gameCourse"); w.WriteValue(finishCourse.GameCourse); }
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
            w.WritePropertyName("checkpointRespawns"); w.WriteValue(CheckpointRespawns);
            w.WritePropertyName("fixedDeltaTime"); json.Float(Time.fixedDeltaTime);
            w.WritePropertyName("inputs"); w.WriteRawValue(JsonConvert.SerializeObject(inputs));
        }

        /// <summary>
        /// A tech's lab demo in turbo from the start its playback uses (`tech-<id>.json` and its world, for `replay`): the lab
        /// injected and the player at the demo's lab start, the lab category's unlocks plus the tech's omnidirectional dash;
        /// `lab` picks one of the tech's labs (else its first). The demo starts inside a tick, as its playback does, so the
        /// trace's start is the state after that first tick (its `tick` says which input comes next). A lab start is off every
        /// course: no course finish ends it (course -1), as its turbo run has none.
        /// </summary>
        object EngineSimTechTrace(WebStudioRequest request, JObject body)
        {
            TechDef tech = TechLabDemo(FindTech(Text(body, "tech")), body["lab"] == null ? null : Text(body, "lab"), out List<SolverTick> inputs);
            string name = "tech-" + tech.id;
            string[] parts = tech.start.Split(new[] { '_' }, 2);
            CourseDef def = _courses.Find(c => string.Equals(c.Token, parts[0], StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("No course " + parts[0] + " for " + tech.name + "'s demo.");
            if (PlaybackActive || _recorder.IsRecording || _newGamePhase != 0)
            {
                request.StatusCode = 409;
                return new { ok = false, error = "Stop playback and recording, and let a new game finish loading, first." };
            }
            if (WorldNotLoaded() is string notLoaded)
            {
                request.StatusCode = 409;
                return new { ok = false, error = notLoaded };
            }
            SelectSolverTarget(new JObject { ["course"] = def.id, ["capabilities"] = CapsOf(parts[1]).Id });
            if (CapabilitiesLocked)
            {
                request.StatusCode = 409;
                return new { ok = false, error = CapabilitiesLockedError };
            }
            if (!CourseLoaded())
            {
                request.StatusCode = 409;
                return new { ok = false, error = "Course " + def.Token + " is loading; retry in a moment." };
            }
            string path = Path.Combine(EngineSimDir, name + ".json"), worldPath = Path.Combine(EngineSimDir, name + ".world.json");
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot first = null;
            var startJson = new StringWriter();
            bool finished;
            float time;
            int finishTick, samples = 0;
            string temporary = path + ".tmp";
            using (var writer = new StreamWriter(temporary))
            {
                // Snapshots read the player's live transform and contacts, so each is written on its tick; the header after the run.
                var json = new EngineSimJson(writer);
                JsonTextWriter w = json.W;
                w.WriteStartObject();
                w.WritePropertyName("ticks");
                w.WriteStartArray();
                _techStart = tech;
                ClearEngineSimStarts();
                _engineSimTickHook = p =>
                {
                    if (first != null)
                    {
                        EngineSimExport.WriteSnapshot(json, TurboRunner.Capture(p, _solverCourses, _engine.CaptureState(), (int)_engine.RunTicks, false, false), p, true);
                        samples++;
                        return;
                    }
                    // The start carries the economy (clone income, boxes), as a category trace's start does.
                    TurboRunner.Snapshot snapshot = first = TurboRunner.Capture(p, _solverCourses, _engine.CaptureState(), (int)_engine.RunTicks);
                    var head = new EngineSimJson(startJson);
                    EngineSimExport.WriteSnapshot(head, snapshot, p, false);
                    head.W.Flush();
                    EngineSimExport.WriteWorld(worldPath);
                };
                try { finished = Evaluate(inputs, out time, out finishTick, out _); }
                finally
                {
                    _engineSimTickHook = null;
                    _techStart = null;
                    ClearEngineSimStarts();
                    // The turbo run starts and ends inside this request, where PumpLabMap never sees it busy: the next Update restores the lab.
                    if (_lab != null) _lab.PlaybackSeen = true;
                }
                if (first == null) throw new InvalidOperationException(tech.name + "'s demo ran no tick in turbo.");
                Vector2 end = player.transform.position;
                TurboRunner.PutOrigin(first.origin);
                ParkAtStart();
                w.WriteEndArray();
                WriteTraceTarget(json, null, -1, _solverCaps, inputs);
                w.WritePropertyName("tech"); w.WriteValue(tech.id);
                w.WritePropertyName("start"); w.WriteRawValue(startJson.ToString());
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
            TasPlugin.ReplaceAtomic(temporary, path);
            return new { ok = true, path, finished, time, finishTick, ticks = samples };
        }

        readonly Dictionary<string, TurboRunner.Snapshot> _startStates = new Dictionary<string, TurboRunner.Snapshot>();
        /// <summary>The last PrepareEngineSimRun's category key, and whether it captured its start just then.</summary>
        string _startKey;
        bool _startFresh;

        /// <summary>Every cached start state goes: a new game or a tech demo's own start captures its own.</summary>
        void ClearEngineSimStarts() { _startStates.Clear(); _startState = null; }

        static bool Truthy(JObject body, string key) { return body[key] != null && body[key].Type == JTokenType.Boolean && (bool)body[key]; }
    }

    internal static class EngineSimExport
    {
        const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // MacroEngine's step list is the whole route being played (17k entries on a Full-TAS run): nothing reads it back from
        // a trace, and written with every tick it made a Full-TAS trace 3.8 GB, past what the offline engine can read.
        static readonly FieldInfo EngineSteps = typeof(MacroEngine).GetField("_steps", InstanceFields);

        static void WriteRandom(EngineSimJson json, UnityEngine.Random.State state)
        {
            JsonTextWriter w = json.W;
            w.WriteStartArray();
            foreach (int word in CourseStart.Words(state)) w.WriteValue(word);
            w.WriteEndArray();
        }

        public static void WriteSnapshot(EngineSimJson json, TurboRunner.Snapshot s, Movement player, bool live)
        {
            JsonTextWriter w = json.W;
            w.WriteStartObject();
            w.WritePropertyName("tick"); w.WriteValue(s.tick);
            w.WritePropertyName("random"); WriteRandom(json, s.random);
            w.WritePropertyName("movement"); WriteFieldValues(json, typeof(Movement), s.movement);
            w.WritePropertyName("engine"); WriteFieldValues(json, FieldState.For(typeof(MacroEngine)), s.engine, EngineSteps);
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
            w.WritePropertyName("origin"); json.Value(s.origin, 0);
            w.WritePropertyName("rebasing"); w.WriteValue(s.rebasing);
            w.WritePropertyName("stepFrom"); json.Value(s.stepFrom, 0);
            w.WritePropertyName("stepTo"); json.Value(s.stepTo, 0);
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
            if (s.vman != null && s.vman.active)
            {
                w.WritePropertyName("vman"); w.WriteStartObject();
                w.WritePropertyName("timeLeft"); w.WriteValue(s.vman.timeLeft);
                w.WritePropertyName("score"); w.WriteValue(s.vman.score);
                w.WritePropertyName("blockEnding"); w.WriteValue(s.vman.blockEnding);
                w.WriteEndObject();
            }
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

        static void WriteFieldValues(EngineSimJson json, FieldInfo[] fields, object[] values, FieldInfo skip = null)
        {
            JsonTextWriter w = json.W;
            w.WriteStartArray();
            for (int i = 0; i < fields.Length && i < values.Length; i++)
            {
                if (fields[i] == skip) continue;
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

        /// <summary>
        /// The world as a run starts it. Every spike is cleared first (spikeScript <c>counter</c> 0, <c>playerKilled</c> off): a
        /// run that ended in a spike leaves them set in the live game until an OnTriggerExit2D that a turbo restore mutes, and
        /// no start is inside a spike (a turbo start snapshot restores them cleared too, TurboRunner.Snapshot.spikes).
        /// </summary>
        public static int WriteWorld(string path)
        {
            TurboRunner.ClearSpikes();
            var gameObjects = new List<GameObject>();
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go != null && go.scene.IsValid() && (go.hideFlags & HideFlags.HideAndDontSave) == 0) gameObjects.Add(go);
            var materials = new Dictionary<int, PhysicsMaterial2D>();
            Dictionary<CompositeCollider2D, PhysicsShapeGroup2D> sourceShapes = ColouredBlockSourceShapes();
            string temporary = path + ".tmp";
            using (var writer = new StreamWriter(temporary))
            {
                var json = new EngineSimJson(writer);
                JsonTextWriter w = json.W;
                w.WriteStartObject();
                w.WritePropertyName("schema"); w.WriteValue(1);
                w.WritePropertyName("unityVersion"); w.WriteValue(Application.unityVersion);
                w.WritePropertyName("createdUtc"); w.WriteValue(DateTime.UtcNow.ToString("o"));
                w.WritePropertyName("activeScene"); w.WriteValue(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
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
            TasPlugin.ReplaceAtomic(temporary, path);
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
            try { count = collider.isActiveAndEnabled ? collider.GetShapes(group) : OffShapes(collider, group); }
            catch (Exception) { json.W.WriteNull(); return; }
            WriteShapeGroup(json, group, count);
        }

        // Unity has no shapes for a collider that is off (its GameObject inactive, as in a zone the ZoneLoader hasn't
        // loaded, or the component disabled), yet the game can turn it on mid-run and the engine then needs its geometry.
        // A copy on a live probe at the same pose gives Unity's own shapes, relative to a body at the attached body's pose
        // as GetShapes gives them. Composites, tilemaps and composite sources aren't copied (coloured blocks export
        // sourceShapes); they keep no shapes.
        static int OffShapes(Collider2D collider, PhysicsShapeGroup2D group)
        {
            if (!(collider is BoxCollider2D || collider is CapsuleCollider2D || collider is CircleCollider2D || collider is PolygonCollider2D
                || collider is EdgeCollider2D) || collider.compositeOperation != Collider2D.CompositeOperation.None) return 0;
            Rigidbody2D body = collider.attachedRigidbody;
            Transform t = collider.transform;
            var root = new GameObject("EngineSimShapeProbe");
            try
            {
                GameObject probe = root;
                if (body != null)
                {
                    root.transform.SetPositionAndRotation(body.transform.position, body.transform.rotation);
                    root.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
                    probe = new GameObject("EngineSimShapeProbeCollider");
                    probe.transform.SetParent(root.transform, false);
                }
                probe.transform.SetPositionAndRotation(t.position, t.rotation);
                probe.transform.localScale = t.lossyScale;
                switch (collider)
                {
                    case BoxCollider2D box:
                    {
                        BoxCollider2D copy = probe.AddComponent<BoxCollider2D>();
                        copy.size = box.size;
                        copy.edgeRadius = box.edgeRadius;
                        copy.offset = box.offset;
                        return copy.GetShapes(group);
                    }
                    case CapsuleCollider2D capsule:
                    {
                        CapsuleCollider2D copy = probe.AddComponent<CapsuleCollider2D>();
                        copy.size = capsule.size;
                        copy.direction = capsule.direction;
                        copy.offset = capsule.offset;
                        return copy.GetShapes(group);
                    }
                    case CircleCollider2D circle:
                    {
                        CircleCollider2D copy = probe.AddComponent<CircleCollider2D>();
                        copy.radius = circle.radius;
                        copy.offset = circle.offset;
                        return copy.GetShapes(group);
                    }
                    case PolygonCollider2D polygon:
                    {
                        PolygonCollider2D copy = probe.AddComponent<PolygonCollider2D>();
                        var points = new List<Vector2>();
                        copy.pathCount = polygon.pathCount;
                        for (int p = 0; p < polygon.pathCount; p++)
                        {
                            points.Clear();
                            polygon.GetPath(p, points);
                            copy.SetPath(p, points);
                        }
                        copy.useDelaunayMesh = polygon.useDelaunayMesh;
                        copy.offset = polygon.offset;
                        return copy.GetShapes(group);
                    }
                    case EdgeCollider2D edge:
                    {
                        EdgeCollider2D copy = probe.AddComponent<EdgeCollider2D>();
                        copy.points = edge.points;
                        copy.edgeRadius = edge.edgeRadius;
                        copy.useAdjacentStartPoint = edge.useAdjacentStartPoint;
                        copy.useAdjacentEndPoint = edge.useAdjacentEndPoint;
                        copy.adjacentStartPoint = edge.adjacentStartPoint;
                        copy.adjacentEndPoint = edge.adjacentEndPoint;
                        copy.offset = edge.offset;
                        return copy.GetShapes(group);
                    }
                    default: return 0;
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
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
