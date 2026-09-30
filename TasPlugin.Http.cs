using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        private object HandleWebRequest(WebStudioRequest request)
        {
            // Any command can change what GET /api/state/static returns.
            if (request.Method != "GET") BumpWebState();
            JObject body = string.IsNullOrWhiteSpace(request.Body) ? new JObject() : JObject.Parse(request.Body);
            // An avoid box drawn on a course's search map edits forced-paths.json (a new partial file, not the engine bridge).
            if (request.Path == "/api/enginesim/avoid") return HandleCourseAvoid(body);
            if (request.Path.StartsWith("/api/enginesim/", StringComparison.Ordinal)) return HandleEngineSim(request, body);
            if (request.Path.StartsWith("/api/fulltas/", StringComparison.Ordinal)) return HandleFullTas(request, body);
            if (request.Path.StartsWith("/api/techs/", StringComparison.Ordinal)) return HandleTechs(request.Path, body);
            if (request.Path.StartsWith("/api/probe/", StringComparison.Ordinal)) return HandleProbe(request.Path, body);
            if (request.Path.StartsWith("/api/labmap/", StringComparison.Ordinal)) return HandleLabMap(request.Path, body);
            if (request.Path.StartsWith("/api/tas/", StringComparison.Ordinal)) return HandleTas(request, body);
            if (request.Method == "GET" && request.Path == "/api/oracle/status")
            {
                return new
                {
                    ok = true,
                    runTicks = _engine.IsRunning ? _engine.RunTicks : 0,
                    status = _engine.Status
                };
            }
            if (request.Path == "/api/solver/start")
            {
                StartSolver(body);
                return new { ok = true };
            }
            // What start would run, started nothing (the studio's command preview, README ### Rules 48: agents check with this).
            if (request.Path == "/api/solver/preview") return PreviewSolver(body);
            if (request.Path == "/api/solver/waypoints") return EngineSimWaypoints(request, body);
            if (request.Path == "/api/solver/stop")
            {
                StopSolver("Stopped");
                return new { ok = true };
            }
            if (request.Path == "/api/solver/skip")
            {
                SkipSolveJob();
                return new { ok = true };
            }
            if (request.Path == "/api/courses/add-from-recording")
                return AddCourseFromLastRecording();
            if (request.Path == "/api/settings/lock-capabilities")
            {
                _settings.lockCapabilities = Truthy(body, "locked");
                SaveSettings();
                return new { ok = true, locked = _settings.lockCapabilities };
            }
            if (request.Path == "/api/settings/overlay")
            {
                _settings.showOverlay = Truthy(body, "shown");
                SaveSettings();
                return new { ok = true, shown = _settings.showOverlay };
            }
            if (request.Path == "/api/settings/breaker-lights")
            {
                _settings.forceBreakerLights = Truthy(body, "on");
                SaveSettings();
                return new { ok = true, on = _settings.forceBreakerLights };
            }
            if (request.Path == "/api/settings/fast-playback")
            {
                _settings.fastPlayback = Truthy(body, "on");
                SaveSettings();
                return new { ok = true, on = _settings.fastPlayback };
            }
            if (request.Path == "/api/settings/checkpoint-respawns")
            {
                if (PlaybackActive || _solverRunning || _fullTasProcess != null || _recorder.IsRecording)
                    throw new InvalidOperationException("Stop playback, recording and solving before switching checkpoint respawns.");
                _settings.checkpointRespawns = Truthy(body, "on");
                SaveSettings();
                Movement player = FindWorldMovement();
                if (player != null) player.isRespawningAtCheckpoints = _settings.checkpointRespawns;
                return new { ok = true, on = _settings.checkpointRespawns };
            }
            if (request.Path == "/api/settings/fulltas-goals")
            {
                string run = Text(body, "name") ?? "";
                if (run.Length > 0) FindFullTasRun(run);
                _settings.fullTasGoalsRun = run;
                SaveSettings();
                return new { ok = true, name = _settings.fullTasGoalsRun };
            }
            if (request.Path == "/api/session/setup")
            {
                if (body["abilities"] != null && CapabilitiesLocked) throw new InvalidOperationException(CapabilitiesLockedError);
                Movement player = FindWorldMovement();
                if (body["start"] is JObject setupStart)
                {
                    _preservedCourseResetPoint = new Vector2((float)setupStart["x"], (float)setupStart["y"]);
                    _hasPreservedCourseResetPoint = true;
                }
                var applied = new Dictionary<string, object>();
                if (body["abilities"] is JObject setupAbilities && player != null)
                    foreach (JProperty ability in setupAbilities.Properties())
                    {
                        object value = ability.Value.Type == JTokenType.Boolean ? (object)(bool)ability.Value
                            : ability.Value.Type == JTokenType.Integer ? (object)(int)ability.Value
                            : ability.Value is JObject v ? (object)new Vector2((float)v["x"], (float)v["y"]) : (float)ability.Value;
                        SetMovementField(player, ability.Name, value);
                        object set = GetMovementField(player, ability.Name);
                        applied[ability.Name] = set is Vector2 p ? new { x = p.x, y = p.y } : set;
                    }
                var currencies = new Dictionary<string, double>();
                if (body["currencies"] is JObject setupCurrencies)
                    foreach (JProperty currency in setupCurrencies.Properties())
                    {
                        var key = (globalStats.Currencies)Enum.Parse(typeof(globalStats.Currencies), currency.Name, true);
                        globalStats.currencyLookup[key] = (double)currency.Value;
                        currencies[key.ToString()] = globalStats.currencyLookup[key];
                    }
                bool? blueActive = null;
                if (body["blueActive"] != null)
                {
                    colouredBlockSwapper swapper = FindAnyObjectByType<colouredBlockSwapper>(FindObjectsInactive.Include)
                        ?? throw new InvalidOperationException("No colouredBlockSwapper in the scene.");
                    swapper.swapBlocks((bool)body["blueActive"]);
                    blueActive = swapper.isBlueActive;
                }
                return new { ok = true, abilities = applied, currencies, blueActive };
            }
            if (request.Path == "/api/oracle/bootstrap")
            {
                return BootstrapOracle(request, body);
            }
            if (request.Path == "/api/session/newgame")
            {
                StartNewGame(request, Truthy(body, "vman"), string.IsNullOrEmpty(Text(body, "savestate")) ? null : Text(body, "savestate"));
                return null;
            }
            if (request.Method == "GET" && request.Path == "/api/state/live") return BuildLiveWebState();
            if (request.Method == "GET" && request.Path == "/api/state/static") return BuildStaticWebState();
            if (request.Method == "GET" && request.Path == "/api/state") return BuildWebState();
            if (request.Method != "POST") throw new InvalidOperationException("Unsupported request method.");

            if (request.Path == "/api/routes/select")
            {
                string id = Text(body, "routeId");
                int index = _library.macros.FindIndex(r => r.id == id);
                if (index < 0) throw new InvalidOperationException("Route not found.");
                if (index != _library.selectedIndex)
                {
                    _library.selectedIndex = index;
                    SaveSelection();
                }
                return new { ok = true };
            }
            if (request.Path == "/api/routes/run")
            {
                if (SelectedMacro == null) throw new InvalidOperationException("Select a route first.");
                ReplaySelected(Number(body, "startDelayTicks", 0));
                return new { ok = true };
            }
            if (request.Path == "/api/routes/walk")
            {
                if (SelectedMacro == null) throw new InvalidOperationException("Select a route first.");
                ReplaySelected(Number(body, "startDelayTicks", 0), Mathf.Clamp(FloatNumber(body, "slowdown", DefaultWalkSlowdown), 1f, 1000f));
                return new { ok = true };
            }
            if (request.Path == "/api/routes/duplicate")
            {
                if (SelectedMacro == null) throw new InvalidOperationException("Select a route first.");
                DuplicateMacro();
                return new { ok = true, routeId = SelectedMacro.id };
            }
            if (request.Path == "/api/routes/rename")
            {
                string id = Text(body, "routeId");
                string name = Text(body, "name").Trim();
                MacroDefinition route = _library.macros.Find(r => r.id == id);
                if (route == null) throw new InvalidOperationException("Route not found.");
                if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Route name cannot be empty.");
                RejectCategoryName(name);
                route.name = name;
                SaveLibrary();
                return new { ok = true };
            }
            if (request.Path == "/api/routes/delete")
            {
                if (PlaybackActive)
                    throw new InvalidOperationException("Stop playback before deleting a route.");
                string id = Text(body, "routeId");
                int index = _library.macros.FindIndex(r => r.id == id);
                if (index < 0) throw new InvalidOperationException("Route not found.");
                _library.macros.RemoveAt(index);
                _library.selectedIndex = Mathf.Clamp(_library.selectedIndex > index ? _library.selectedIndex - 1 : _library.selectedIndex,
                    0, Mathf.Max(0, _library.macros.Count - 1));
                SaveLibrary();
                return new { ok = true };
            }
            if (request.Path == "/api/game/resume")
            {
                EnsureGameUnpaused();
                return new { ok = true };
            }
            if (request.Path == "/api/routes/stop")
            {
                StopPlayback("Stopped from Web Studio");
                if (_recorder.IsRecording) StopRecording("Stopped from Web Studio");
                return new { ok = true };
            }
            if (request.Path == "/api/record/start")
            {
                if (_recorder.IsRecording) throw new InvalidOperationException("Recording is already active.");
                StartRecording();
                return new { ok = true };
            }
            if (request.Path == "/api/record/stop")
            {
                StopRecording("Stopped from Web Studio");
                return new { ok = true };
            }
            if (request.Path == "/api/recordings/create-transition")
            {
                RecordingDefinition recording = _recordings.Find(r => r.id == Text(body, "recordingId"));
                if (recording == null) throw new InvalidOperationException("Recording not found.");
                return AddTransitionFromRecording(recording);
            }
            if (request.Path == "/api/recordings/create-route")
            {
                RecordingDefinition recording = _recordings.Find(r => r.id == Text(body, "recordingId"));
                if (recording == null) throw new InvalidOperationException("Recording not found.");
                CreateRouteFromRecording(recording);
                SaveLibrary();
                return new { ok = true };
            }
            if (request.Path == "/api/recordings/delete")
            {
                RecordingDefinition recording = _recordings.Find(r => r.id == Text(body, "recordingId"));
                if (recording == null) throw new InvalidOperationException("Recording not found.");
                DeleteRecording(recording);
                return new { ok = true };
            }
            if (request.Path == "/api/recordings/rename")
            {
                RecordingDefinition recording = _recordings.Find(r => r.id == Text(body, "recordingId"));
                string name = Text(body, "name").Trim();
                if (recording == null) throw new InvalidOperationException("Recording not found.");
                if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Recording name cannot be empty.");
                recording.name = name;
                SaveRecording(recording);
                return new { ok = true };
            }
            if (request.Path == "/api/actions/update")
            {
                MacroDefinition route = SelectedMacro;
                int index = Number(body, "actionIndex", -1);
                if (route == null || index < 0 || index >= route.steps.Count) throw new InvalidOperationException("Action not found.");
                MacroStep action = route.steps[index];
                if (body["label"] != null) action.label = Text(body, "label");
                if (body["type"] != null)
                {
                    MacroStepType parsed;
                    string requestedType = Text(body, "type").Replace("_", "");
                    if (!Enum.TryParse(requestedType, true, out parsed) || parsed == MacroStepType.Bundle)
                        throw new InvalidOperationException("Unsupported action type: " + requestedType);
                    action.type = parsed;
                }
                if (body["durationTicks"] != null)
                {
                    action.durationTicks = Math.Max(1, Number(body, "durationTicks", action.durationTicks));
                }
                if (body["direction"] != null) action.direction = Mathf.Clamp(Number(body, "direction", action.direction), -1, 1);
                if (body["rawMoveX"] != null) action.rawMoveX = Mathf.Clamp(FloatNumber(body, "rawMoveX", action.rawMoveX), -1f, 1f);
                if (body["rawMoveY"] != null) action.rawMoveY = Mathf.Clamp(FloatNumber(body, "rawMoveY", action.rawMoveY), -1f, 1f);
                if (body["rawJumpPressed"] != null) action.rawJumpPressed = (bool)body["rawJumpPressed"];
                if (body["rawJumpHeld"] != null) action.rawJumpHeld = (bool)body["rawJumpHeld"];
                if (body["rawJumpReleased"] != null) action.rawJumpReleased = (bool)body["rawJumpReleased"];
                if (body["rawDashPressed"] != null) action.rawDashPressed = (bool)body["rawDashPressed"];
                if (body["rawDashHeld"] != null) action.rawDashHeld = (bool)body["rawDashHeld"];
                if (body["dashJumpFrame"] != null) action.dashJumpFrame = Math.Max(0, Number(body, "dashJumpFrame", 0));
                if (body["enabled"] != null) action.enabled = (bool)body["enabled"];
                SaveLibrary();
                return new { ok = true };
            }
            if (request.Path == "/api/savestates/capture")
            {
                CaptureSavestate();
                return new { ok = true };
            }
            if (request.Path == "/api/savestates/restore")
            {
                Savestate savestate = _savestates.Find(c => c.id == Text(body, "savestateId"));
                if (savestate == null) throw new InvalidOperationException("Savestate not found.");
                RestoreSavestate(savestate);
                return new { ok = true };
            }
            if (request.Path == "/api/savestates/place")
            {
                Savestate placed = PlaceSavestate(body);
                return new { ok = true, id = placed.id, name = placed.name };
            }
            if (request.Path == "/api/savestates/assign")
            {
                if (SelectedMacro == null) throw new InvalidOperationException("Select a route first.");
                string id = Text(body, "savestateId");
                if (!string.IsNullOrEmpty(id) && _savestates.Find(c => c.id == id) == null) throw new InvalidOperationException("Savestate not found.");
                SelectedMacro.savestateId = id;
                SaveLibrary();
                return new { ok = true };
            }
            throw new InvalidOperationException("Unknown Web Studio command: " + request.Path);
        }

        private static void RejectCategoryName(string name)
        {
            if (MatchCategoryRoute(name).Success || MatchFarmRoute(name).Success)
                throw new InvalidOperationException("Category route names are reserved for routes that passed the solver's gate.");
        }

        // The studio polls GET /api/state/live (the small, fast-changing part) and fetches GET /api/state/static (routes,
        // recordings, savestates, courses, Full-TAS runs, techs) again only when the live part's staticVersion changes
        // (_webStateVersion). It changes with every command (any request but a GET), every write through WriteJsonAtomic,
        // and within a second of anything else the static part reads (WebStateStamp). GET /api/state is both parts in one,
        // for tools.
        void BumpWebState() { _webStateVersion++; }

        long WebStateVersion()
        {
            if (Time.unscaledTime >= _webStateCheckAt)
            {
                _webStateCheckAt = Time.unscaledTime + 1f;
                long stamp = WebStateStamp();
                if (stamp != _webStateStamp)
                {
                    _webStateStamp = stamp;
                    BumpWebState();
                }
            }
            return _webStateVersion;
        }

        /// <summary>
        /// What the static part reads that changes without a command or a plugin write: the files the offline solver and the
        /// lab tools write (Full-TAS bests, closest attempts, faster ways, traces, lab maps and demos, forced-paths.json),
        /// fulltas.json edited by hand, the lists' sizes, playback, recording and solving starting or ending, the scene, and
        /// the bonus reward boxes' purchases (the courses' hints).
        /// </summary>
        long WebStateStamp()
        {
            long h = 17;
            void Add(long value) { h = h * 31 + value; }
            void AddFile(string path) { Add(File.GetLastWriteTimeUtc(path).Ticks); }
            void AddDirectory(string dir)
            {
                if (!Directory.Exists(dir)) { Add(0); return; }
                foreach (FileInfo file in new DirectoryInfo(dir).GetFiles("*.json"))
                {
                    Add(file.Name.GetHashCode());
                    Add(file.LastWriteTimeUtc.Ticks);
                    Add(file.Length);
                }
            }
            Add(PlaybackActive ? 1 : 0);
            Add(_recorder.IsRecording ? 1 : 0);
            Add(_solverRunning ? 1 : 0);
            Add(_fullTasProcess != null ? 1 : 0);
            Add((int)SceneManager.GetActiveScene().handle);
            Add(_library.macros.Count);
            Add(_library.selectedIndex);
            Add(_recordings.Count);
            Add(_savestates.Count);
            Add(_techs.Count);
            Add(_fullTasRuns.Count);
            Add(_fullTasSplits.Count);
            foreach (upgradeBox box in _bonusRewardBoxes.Values) Add(box != null ? box.TimesUsed : -1);
            // The files not while a route plays: the directory scans (hundreds of lab files) would cost 1x playback's paced
            // frames once a second. What changed on disk meanwhile shows at the first check after it.
            if (PlaybackActive) return h;
            AddFile(FullTasPath);
            AddFile(FullTasSplitsPath);
            AddDirectory(Path.Combine(EngineSimDir, "fulltas"));
            foreach (FullTasRunDef run in _fullTasRuns) Add(File.Exists(FullTasTracePath(run.name)) ? 1 : 0);
            AddDirectory(LabMapsDir);
            if (ForcedPathsFile != null) AddFile(ForcedPathsFile);
            return h;
        }

        /// <summary>A caps object (the web API's): what a set of unlocks has.</summary>
        static object CapsWebState(Caps caps)
        {
            return new { wallJump = caps.WallJump, dashes = caps.Dashes, airJumps = caps.AirJumps,
                blockSwap = caps.BlockSwap, orbs = caps.Orbs, zip = caps.Zip, omni = caps.Omni };
        }

        private object BuildLiveWebState()
        {
            MacroDefinition selected = SelectedMacro;
            Movement player = FindWorldMovement();
            return new { connected = true, version = PluginVersion, staticVersion = WebStateVersion(), engineStatus = _engine.Status,
                saveUnlocks = player == null ? null : CapsWebState(SaveUnlocks(player)),
                paused = IsGamePaused(player),
                capabilitiesLocked = _settings.lockCapabilities,
                overlayShown = _settings.showOverlay,
                breakerLightsForced = _settings.forceBreakerLights,
                checkpointRespawns = _settings.checkpointRespawns,
                fastPlayback = _settings.fastPlayback,
                running = PlaybackActive,
                humanPlayback = _engine.IsRunning && _engine.ActiveMacro.id == HumanPlaybackId,
                recording = _recorder.IsRecording, selectedRouteId = selected == null ? "" : selected.id,
                solver = BuildSolverWebState(), fullTas = BuildFullTasLiveWebState(), techs = BuildTechLiveWebState(), tas = BuildTasLiveState() };
        }

        private object BuildStaticWebState()
        {
            long version = WebStateVersion();
            MacroDefinition selected = SelectedMacro;
            List<object> routes = new List<object>();
            foreach (MacroDefinition route in _library.macros)
            {
                List<object> actions = new List<object>();
                if (route == selected)
                    foreach (MacroStep action in route.steps) actions.Add(new {
                        id = action.id, label = action.label, type = TypeName(action.type), direction = action.direction,
                        wallJumpHoldDirection = action.wallJumpHoldDirection,
                        durationTicks = action.durationTicks, rawMoveX = action.rawMoveX,
                        rawJumpPressed = action.rawJumpPressed, rawJumpHeld = action.rawJumpHeld,
                        rawJumpReleased = action.rawJumpReleased,
                        rawDashPressed = action.rawDashPressed, rawDashHeld = action.rawDashHeld,
                        enabled = action.enabled
                    });
                routes.Add(new { id = route.id, name = route.name, actionCount = route.steps.Count, actions = actions });
            }
            List<object> savestates = new List<object>();
            foreach (Savestate savestate in _savestates)
                savestates.Add(new { id = savestate.id, name = savestate.name, createdUtc = savestate.createdUtc,
                    assigned = selected != null && selected.savestateId == savestate.id });
            // Every recording's exact inputs (the steps Create route makes): the Recordings tab lists them as its summary.
            List<object> recordings = new List<object>();
            foreach (RecordingDefinition recording in _recordings)
            {
                List<object> actions = new List<object>();
                foreach (MacroStep action in ExactInputCompressor.Compress(recording.samples))
                    actions.Add(new { label = action.label, durationTicks = action.durationTicks });
                recordings.Add(new { id = recording.id, name = recording.name, createdUtc = recording.createdUtc,
                    sampleCount = recording.samples.Count, actionCount = actions.Count,
                    courseTimeSeconds = recording.courseTimeSeconds, actions = actions });
            }
            return new { staticVersion = version, categories = Categories.All.Select(c => new { id = c.Id, label = c.Label, caps = CapsWebState(c.Caps) }).ToArray(),
                routes = routes, recordings = recordings, savestates = savestates,
                courses = BuildCourseWebState(), fullTas = new { runs = BuildFullTasRunsWebState() }, techs = BuildTechStaticWebState() };
        }

        private object BuildWebState()
        {
            JObject state = JObject.FromObject(BuildStaticWebState());
            state.Merge(JObject.FromObject(BuildLiveWebState()));
            return state;
        }

        private static string Text(JObject value, string name)
        {
            JToken token = value[name];
            return token == null || token.Type == JTokenType.Null ? "" : token.ToString();
        }

        private static int Number(JObject value, string name, int fallback)
        {
            JToken token = value[name];
            int result;
            return token != null && int.TryParse(token.ToString(), out result) ? result : fallback;
        }

        private static float FloatNumber(JObject value, string name, float fallback)
        {
            JToken token = value[name];
            float result;
            return token != null && float.TryParse(token.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result) ? result : fallback;
        }
    }
}
