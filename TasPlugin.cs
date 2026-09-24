using System;
using System.Collections.Generic;
using System.IO;
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
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed partial class TasPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.deafwave.igtap.tas";
        public const string PluginName = "IGTAP TAS Studio";
        public const string PluginVersion = "7.0.0";

        internal static TasPlugin Instance;
        internal static ManualLogSource Log;

        private readonly MacroEngine _engine = new MacroEngine();
        private readonly InputRecorder _recorder = new InputRecorder();
        private WebStudioServer _webServer;
        private Harmony _harmony;
        private MacroLibrary _library;
        private string _libraryPath;
        private string _recordingsPath;
        private string _resultsPath;
        private string _checkpointsPath;
        private string _settingsPath;
        private StudioSettings _settings = new StudioSettings();
        private readonly List<RecordingDefinition> _recordings = new List<RecordingDefinition>();
        private readonly List<AttemptResult> _results = new List<AttemptResult>();
        private readonly List<SectionCheckpoint> _checkpoints = new List<SectionCheckpoint>();
        private float _pendingCourseTime;
        private bool _pendingCourseWasTracking;
        private MacroDefinition _pendingReplay;
        private int _pendingReplayStartDelayTicks;
        private Rigidbody2D _deterministicBody;
        private RigidbodyInterpolation2D _savedBodyInterpolation;
        private bool _hasPreservedCourseResetPoint;
        private Vector2 _preservedCourseResetPoint;
        // The course the running route starts at (resolved from its name in RestoreRouteStart); a bonus course's
        // run finishes on its reward box instead of an end gate.
        private CourseDef _runCourse;
        private static readonly FieldInfo CourseCurrentPathTimeField = AccessTools.Field(typeof(courseScript), "currentPathTime");
        private static readonly FieldInfo CourseTrackingField = AccessTools.Field(typeof(courseScript), "tracking");

        private WebStudioRequest _bootstrapRequest;
        private int _bootstrapPhase;
        private JObject _bootstrapBody;
        private readonly List<string> _bootstrapNotes = new List<string>();

        private static readonly FieldInfo MovIsOnMainMenuField = AccessTools.Field(typeof(Movement), "isOnMainMenu");


        private void Awake()
        {
            Instance = this;
            Log = Logger;
            _libraryPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "routes.json");
            _recordingsPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "recordings");
            _resultsPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "results");
            _checkpointsPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "checkpoints");
            _settingsPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "settings.json");
            LoadSettings();
            LoadLibrary();
            LoadRecordings();
            LoadResults();
            LoadCheckpoints();
            LoadCourses();
            _engine.AttemptEnded += SaveAttemptResult;
            Application.runInBackground = true;
            _webServer = new WebStudioServer();
            try
            {
                _webServer.Start(Path.Combine(Paths.PluginPath, "IGTAPTasWeb"));
                Logger.LogInfo("Web Studio listening at " + WebStudioServer.Url);
                if (_settings.autoOpenWebStudio) Application.OpenURL(WebStudioServer.Url);
            }
            catch (Exception ex) { Logger.LogError("Could not start Web Studio: " + ex); }
            _harmony = new Harmony(PluginGuid);
            MethodInfo target = AccessTools.Method(typeof(Movement), "getPlayerControlledInput");
            MethodInfo prefix = AccessTools.Method(typeof(TasPlugin), nameof(BeforePlayerInput));
            if (target == null || prefix == null)
            {
                Logger.LogError("Movement.getPlayerControlledInput was not found.");
                return;
            }
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            PatchOptional(typeof(Movement), "FixedUpdate", nameof(BeforeMovementFixedUpdate), null);
            TurboRunner.Patch(_harmony);
            PatchOptional(typeof(courseScript), "startTracking", null, nameof(AfterCourseStart));
            PatchOptional(typeof(courseScript), "stopTracking", nameof(BeforeCourseFinish), nameof(AfterCourseFinish));
            PatchOptional(typeof(Movement), "onDeath", null, nameof(AfterPlayerDeath));
            PatchOptional(typeof(Movement), "CheckForSuperJump", nameof(BeforeDashFrame), null);
            PatchOptional(typeof(Saveloader), "autosave", nameof(BlockSharedSave), null);
            PatchOptional(typeof(Saveloader), "manualSave", nameof(BlockSharedSave), null);
            PatchOptional(typeof(Saveloader), "delayedManualSave", nameof(BlockSharedSave), null);
            Logger.LogInfo("TAS Studio " + PluginVersion + " loaded. Use " + WebStudioServer.Url + ". F8 is emergency stop.");
        }

        // The game's saves run only when nothing of ours is applied to the player: never while a category route's
        // unlocks (playback) or a turbo/solver run are in effect, so those can't end up in the user's save. Live edits
        // from /api/session/setup are the user's own and save normally.
        private static bool BlockSharedSave()
        {
            return Instance == null || (Instance._playbackSavedAbilities.Count == 0 && !Instance._silentEvaluation && !TurboRunner.Active);
        }

        private static bool BeforeMovementFixedUpdate()
        {
            if (Instance == null) return true;
            if (Instance._pendingSilentMacro != null && !Instance._engine.IsRunning)
            {
                MacroDefinition macro = Instance._pendingSilentMacro;
                Instance._pendingSilentMacro = null;
                Instance.RestoreRouteStart(macro);
                Movement movement = FindWorldMovement();
                Instance.BeginDeterministicPhysics(movement);
                Instance._engine.Run(macro, Instance._library, true, true, 0,
                    movement == null ? (Vector2?)null : movement.transform.position);
                return true;
            }
            if (Instance._pendingReplay != null && !Instance._engine.IsRunning)
            {
                MacroDefinition macro = Instance._pendingReplay;
                int startDelayTicks = Instance._pendingReplayStartDelayTicks;
                Instance._pendingReplay = null;
                Instance._pendingReplayStartDelayTicks = 0;
                bool checkpointRestored = Instance.RestoreRouteStart(macro);
                Movement movement = FindWorldMovement();
                Instance.ApplyCategoryAbilities(macro, movement);
                Vector2? synchronizationPosition = movement == null ? (Vector2?)null : movement.transform.position;
                Instance.BeginDeterministicPhysics(movement);
                Instance._engine.Run(macro, Instance._library, !checkpointRestored, true, startDelayTicks, synchronizationPosition);
            }
            return true;
        }

        private static bool BeforePlayerInput(Movement __instance)
        {
            if (Instance == null) return true;
            // Movement.FixedUpdate calls getPlayerControlledInput at
            // scratch_movement.txt:1151, AFTER its r1-r13 raycasts (907-953)
            // and OnWall logic (1002-1067) and BEFORE the jump/wall-jump
            // consumption (1655-1745). This prefix therefore observes the exact
            // OnWall / wallCoyoteFrames / probe state the jump code will read.
            Instance._recorder.FixedTick(__instance);
            if (Instance._recorder.IsRecording) return true;
            // Before the tick's inputs: the same world state the recorder samples. A bonus run ends on its
            // reward box instead of an end gate.
            if (Instance._engine.IsRunning && Instance.TryRewardFinish(__instance)) return true;
            Instance._engine.FixedTick(__instance);
            // This method is the movement executor, not a device sampler. The prefix
            // supplies MoveAxis and input buffers; the original must run to consume
            // them and apply movement, jumping, walljumping, and dashing.
            return true;
        }

        // The bonus-area analogue of an end gate: a course with a reward box (endX/endY/endW/endH) has no
        // gates, and its run finishes when the player touches the box's trigger. No in-game timer exists for
        // such a run, so its time is the run's elapsed ticks, the same clock every other tick is judged on.
        private bool TryRewardFinish(Movement player)
        {
            CourseDef course = _silentEvaluation ? _courses.Find(c => c.id == _solverCourse) : _runCourse;
            if (player == null || course == null || !course.HasFinish) return false;
            Vector2 origin = CurrentOrigin();
            Rect zone = new Rect(course.endX - course.endW / 2f + origin.x, course.endY - course.endH / 2f + origin.y, course.endW, course.endH);
            foreach (BoxCollider2D box in player.GetComponents<BoxCollider2D>())
            {
                if (!box.enabled) continue;
                Bounds bounds = box.bounds;
                if (new Rect(bounds.min.x, bounds.min.y, bounds.size.x, bounds.size.y).Overlaps(zone))
                {
                    _engine.CompleteFromGame(MacroEngine.RewardFinishReason, _engine.RunTicks * Time.fixedDeltaTime);
                    return true;
                }
            }
            return false;
        }

        private static void BeforeDashFrame(Movement __instance)
        {
            if (Instance != null && !Instance._recorder.IsRecording) Instance._engine.DashFrame(__instance);
        }

        private void PatchOptional(Type type, string methodName, string prefixName, string postfixName)
        {
            MethodInfo target = AccessTools.Method(type, methodName);
            MethodInfo prefix = string.IsNullOrEmpty(prefixName) ? null : AccessTools.Method(typeof(TasPlugin), prefixName);
            MethodInfo postfix = string.IsNullOrEmpty(postfixName) ? null : AccessTools.Method(typeof(TasPlugin), postfixName);
            if (target == null || (!string.IsNullOrEmpty(prefixName) && prefix == null) || (!string.IsNullOrEmpty(postfixName) && postfix == null))
            { Logger.LogWarning("Optional hook unavailable: " + type.Name + "." + methodName); return; }
            _harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(prefix),
                postfix: postfix == null ? null : new HarmonyMethod(postfix));
        }

        private static void AfterCourseStart()
        {
            if (Instance != null)
            {
                Instance._pendingCourseTime = 0f;
                Movement movement = FindWorldMovement();
                Instance._engine.MarkCourseStart(movement);
                Instance._recorder.MarkCourseStart();
            }
            if (Instance != null && Instance._recorder.IsRecording) Log.LogInfo("Recording: authoritative course start detected");
        }

        private static void BeforeCourseFinish(courseScript __instance, bool savePositionData)
        {
            if (Instance == null) return;
            // stopTracking only finishes a course it is timing; an end gate reached without the start gate is not a run.
            Instance._pendingCourseWasTracking = __instance != null && CourseTrackingField != null
                && CourseTrackingField.GetValue(__instance) is bool tracking && tracking
                // A solver run is scored on its own course; finishing another course on the way is not a finish.
                && (!Instance._silentEvaluation || __instance.courseNumber == Instance._solverCourse);
            if (!savePositionData || __instance == null || CourseCurrentPathTimeField == null) return;
            object value = CourseCurrentPathTimeField.GetValue(__instance);
            Instance._pendingCourseTime = value is float ? (float)value : 0f;
        }

        private static void AfterCourseFinish(bool savePositionData)
        {
            if (Instance == null || !savePositionData || !Instance._pendingCourseWasTracking) return;
            Instance._recorder.MarkCourseFinish(Instance._pendingCourseTime);
            if (Instance._recorder.IsRecording) Instance.StopRecording("Course finish detected");
            Instance._engine.CompleteFromGame("Course finish detected", Instance._pendingCourseTime);
        }

        private static void AfterPlayerDeath(Movement __instance)
        {
            if (Instance == null) return;
            if (Instance._recorder.IsRecording) Instance.StopRecording("Player death");
            if (Instance._engine.IsRunning)
            {
                int action = Instance._engine.StepIndex + 1;
                Instance._engine.ClearInjectedInput(__instance);
                Instance._engine.Abort("Player death at action " + action);
            }
        }

        private static Movement FindWorldMovement()
        {
            Movement[] all = FindObjectsByType<Movement>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all == null || all.Length == 0) return FindWorldMovement();
            Movement fallback = null;
            foreach (Movement m in all)
            {
                if (m == null) continue;
                bool onMenu = MovIsOnMainMenuField != null && MovIsOnMainMenuField.GetValue(m) is bool flag && flag;
                if (!onMenu) return m;
                if (fallback == null) fallback = m;
            }
            return fallback ?? all[0];
        }

        private static object GetMovementField(Movement movement, string name)
        {
            try
            {
                FieldInfo f = AccessTools.Field(typeof(Movement), name);
                return f == null ? null : f.GetValue(movement);
            }
            catch { return null; }
        }

        const string CapabilitiesLockedError = "DO NOT MODIFY CAPABILITIES is on; turn it off to use this.";
        bool CapabilitiesLocked { get { return _settings.lockCapabilities; } }

        private static void SetMovementField(Movement movement, string name, object value)
        {
            // Every unlock change goes through here; with the lock on the save's own unlocks are never touched.
            if (Instance != null && Instance.CapabilitiesLocked && (name.EndsWith("Unlocked") || name == "maxAirDashes" || name == "airDashesLeft" || name == "maxAirJumps" || name == "airJumpsLeft")) return;
            try
            {
                FieldInfo f = AccessTools.Field(typeof(Movement), name);
                if (f != null) f.SetValue(movement, value);
            }
            catch { }
        }

        private static bool GetMovementBool(Movement movement, string name, bool fallback)
        {
            object v = GetMovementField(movement, name);
            return v is bool b ? b : fallback;
        }

        // Course coordinates (courses.json, respawnPoint) are absolute. FloatingOrigin moves every root object by the
        // player's position once the player is Threshold (17500) from the origin, keeping the total in currentOrigin, so
        // a world position is absolute + currentOrigin (Course 4 starts past the threshold). courseResetPoint is world.
        static FloatingOrigin _floatingOrigin;

        internal static Vector2 CurrentOrigin()
        {
            if (_floatingOrigin == null) _floatingOrigin = FindAnyObjectByType<FloatingOrigin>();
            return _floatingOrigin == null ? Vector2.zero : (Vector2)_floatingOrigin.currentOrigin;
        }

        private static void ApplyRestartGeometry(Movement movement)
        {
            Vector2 reset = movement.courseResetPoint != Vector2.zero ? movement.courseResetPoint : movement.respawnPoint + CurrentOrigin();
            movement.transform.position = (Vector3)(reset + new Vector2(0f, -12f));
            try { movement.onGround = true; } catch { }
            try { movement.Velocity = Vector2.zero; } catch { }
            try { movement.momentum = Vector2.zero; } catch { }
            try { movement.movingPlatformVelocity = Vector2.zero; } catch { }
            SetMovementField(movement, "isDead", false);
            try
            {
                BoxCollider2D col = GetMovementField(movement, "normalCollider") as BoxCollider2D;
                if (col != null)
                {
                    if (GetMovementField(movement, "defaultColliderSize") is Vector2 s) col.size = s;
                    if (GetMovementField(movement, "defaultColliderOffset") is Vector2 o) col.offset = o;
                }
            }
            catch { }
            try
            {
                FieldInfo cutsceneField = AccessTools.Field(typeof(Movement), "cutsceneMode");
                if (cutsceneField != null) cutsceneField.SetValue(movement, Enum.Parse(cutsceneField.FieldType, "none"));
            }
            catch { }
        }

        private void DisableSharedAutosave()
        {
            try
            {
                object saveloader = FindAnyObjectByType<Saveloader>();
                if (saveloader == null) { _bootstrapNotes.Add("no Saveloader found"); return; }
                FieldInfo isSaving = AccessTools.Field(saveloader.GetType(), "IsSaving");
                if (isSaving != null)
                {
                    isSaving.SetValue(saveloader, false);
                    _bootstrapNotes.Add("disabled Saveloader autosave (shared Savedata must stay untouched)");
                }
                MonoBehaviour mb = saveloader as MonoBehaviour;
                if (mb != null) mb.CancelInvoke();
            }
            catch (Exception ex) { _bootstrapNotes.Add("autosave disable failed: " + ex.Message); }
        }

        private void PumpBootstrap()
        {
            if (_bootstrapRequest == null || _bootstrapPhase == 0) return;
            try
            {
                if (_bootstrapPhase == 1)
                {
                    _bootstrapPhase = 2;
                    return;
                }
                if (_bootstrapPhase == 2)
                {
                    DisableSharedAutosave();
                    string sceneName = SceneManager.GetActiveScene().name;
                    _bootstrapNotes.Add("active scene: " + sceneName);
                    Movement[] all = FindObjectsByType<Movement>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                    if (all == null || all.Length == 0)
                    {
                        FailBootstrap("No Movement instance after scene load.");
                        return;
                    }
                    var inventory = new List<object>();
                    int menuCount = 0;
                    Movement world = null;
                    foreach (Movement m in all)
                    {
                        if (m == null) continue;
                        bool onMenu = GetMovementBool(m, "isOnMainMenu", false);
                        if (onMenu) menuCount++;
                        inventory.Add(new
                        {
                            name = m.name,
                            active = m.gameObject.activeInHierarchy,
                            isOnMainMenu = onMenu,
                            hasCam = GetMovementField(m, "cam") != null,
                            hasCollider = GetMovementField(m, "normalCollider") != null,
                            x = m.transform.position.x,
                            y = m.transform.position.y
                        });
                        if (!onMenu && world == null) world = m;
                    }
                    if (world == null) world = FindWorldMovement();
                    if (world == null) { FailBootstrap("No usable Movement after scene load.", inventory); return; }

                    SetMovementField(world, "isOnMainMenu", false);
                    if (world.transform.parent != null) world.transform.parent.gameObject.SetActive(true);
                    world.gameObject.SetActive(true);

                    if (GetMovementField(world, "cam") == null)
                    {
                        try
                        {
                            object camMover = FindAnyObjectByType<cameraMover>();
                            if (camMover != null) { SetMovementField(world, "cam", camMover); _bootstrapNotes.Add("assigned cam from scene cameraMover"); }
                            else _bootstrapNotes.Add("still no cameraMover in scene");
                        }
                        catch (Exception ex) { _bootstrapNotes.Add("cam lookup failed: " + ex.Message); }
                    }

                    string folder = _bootstrapBody != null ? Text(_bootstrapBody, "folder") : null;
                    if (string.IsNullOrEmpty(folder)) folder = "/Savedata";
                    try
                    {
                        MethodInfo loadMethod = AccessTools.Method(typeof(Movement), "load");
                        if (loadMethod != null) { loadMethod.Invoke(world, new object[] { folder }); _bootstrapNotes.Add("loaded " + folder); }
                    }
                    catch (Exception ex) { _bootstrapNotes.Add("load failed: " + ex.Message); }

                    float rx = _bootstrapBody != null && _bootstrapBody["respawnPointX"] != null ? (float)_bootstrapBody["respawnPointX"] : 4783.43359f;
                    float ry = _bootstrapBody != null && _bootstrapBody["respawnPointY"] != null ? (float)_bootstrapBody["respawnPointY"] : 1878.2251f;
                    SetMovementField(world, "respawnPoint", new Vector2(rx, ry));
                    SetMovementField(world, "courseResetPoint", Vector2.zero);
                    SetMovementField(world, "isRespawningAtCheckpoints", false);
                    SetMovementField(world, "init", false);

                    try
                    {
                        object zoneLoader = FindAnyObjectByType<ZoneLoader>();
                        if (zoneLoader == null) zoneLoader = AccessTools.TypeByName("ZoneLoader") != null
                            ? typeof(ZoneLoader).GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null)
                            : null;
                        if (zoneLoader != null)
                        {
                            MethodInfo loadZone = AccessTools.Method(zoneLoader.GetType(), "LoadZone", new[] { typeof(int), typeof(bool) });
                            if (loadZone != null)
                            {
                                loadZone.Invoke(zoneLoader, new object[] { 1, true });
                                _bootstrapNotes.Add("ZoneLoader.LoadZone(1, force)");
                            }
                        }
                        else _bootstrapNotes.Add("no ZoneLoader instance");
                    }
                    catch (Exception ex) { _bootstrapNotes.Add("LoadZone failed: " + ex.Message); }

                    EnsureGameUnpaused();

                    bool respawnRan = false;
                    try
                    {
                        MethodInfo respawnMethod = AccessTools.Method(typeof(Movement), "respawn");
                        if (respawnMethod != null) { respawnMethod.Invoke(world, new object[] { true }); respawnRan = true; _bootstrapNotes.Add("respawn(true) completed"); }
                    }
                    catch (Exception ex)
                    {
                        _bootstrapNotes.Add("respawn(true) threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                    }
                    if (!respawnRan) { ApplyRestartGeometry(world); _bootstrapNotes.Add("applied respawn geometry manually"); }

                    Vector2 pos = world.transform.position;
                    Rigidbody2D body2d = world.GetComponent<Rigidbody2D>();
                    if (body2d != null) { body2d.position = pos; body2d.linearVelocity = Vector2.zero; }
                    Physics2D.SyncTransforms();

                    int colliderCount = FindObjectsByType<Collider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                    Vector2 respawnPoint = GetMovementField(world, "respawnPoint") is Vector2 rpv ? rpv : Vector2.zero;
                    Vector2 courseResetPoint = GetMovementField(world, "courseResetPoint") is Vector2 cr ? cr : Vector2.zero;
                    FinishBootstrap(new
                    {
                        ok = true,
                        scene = sceneName,
                        movementCount = all.Length,
                        menuCount = menuCount,
                        movements = inventory,
                        colliderCount = colliderCount,
                        notes = _bootstrapNotes.ToArray(),
                        position = new { x = pos.x, y = pos.y },
                        expectedOrdinaryPose = new { x = 4783.434, y = 1866.225 },
                        poseMatches = Math.Abs(pos.x - 4783.434f) < 0.02 && Math.Abs(pos.y - 1866.225f) < 0.02,
                        respawnPoint = new { x = respawnPoint.x, y = respawnPoint.y },
                        courseResetPoint = new { x = courseResetPoint.x, y = courseResetPoint.y },
                        onGround = GetMovementBool(world, "onGround", false),
                        facingRight = world.facingRight,
                        wallJumpUnlocked = GetMovementBool(world, "wallJumpUnlocked", false),
                        dashUnlocked = GetMovementBool(world, "dashUnlocked", false),
                        isDead = GetMovementBool(world, "isDead", false)
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                FailBootstrap(ex.ToString());
            }
        }

        private void FinishBootstrap(object payload)
        {
            WebStudioRequest req = _bootstrapRequest;
            _bootstrapRequest = null;
            _bootstrapPhase = 0;
            _bootstrapBody = null;
            _bootstrapNotes.Clear();
            if (req == null) return;
            req.Response = JsonConvert.SerializeObject(payload);
            req.Completed.Set();
        }

        private void FailBootstrap(string error, object extra = null)
        {
            WebStudioRequest req = _bootstrapRequest;
            _bootstrapRequest = null;
            _bootstrapPhase = 0;
            _bootstrapBody = null;
            var notes = new List<string>(_bootstrapNotes);
            _bootstrapNotes.Clear();
            if (req == null) return;
            req.StatusCode = 500;
            req.Response = JsonConvert.SerializeObject(new { ok = false, error = error, notes = notes, extra = extra });
            req.Completed.Set();
        }

        private object BootstrapOracle(WebStudioRequest request, JObject body)
        {
            if (_bootstrapRequest != null)
            {
                request.StatusCode = 409;
                return new { ok = false, error = "A bootstrap is already in progress." };
            }
            _bootstrapNotes.Clear();
            string sceneName = SceneManager.GetActiveScene().name;
            bool hasCamera = FindAnyObjectByType<cameraMover>() != null;
            _bootstrapNotes.Add("requested from scene: " + sceneName);

            if (!hasCamera)
            {
                _bootstrapNotes.Add("no cameraMover; loading Overworld scene");
                try { SceneManager.LoadScene("Overworld"); }
                catch (Exception ex)
                {
                    request.StatusCode = 500;
                    return new { ok = false, error = "LoadScene(Overworld) failed: " + ex, notes = _bootstrapNotes };
                }
                _bootstrapRequest = request;
                _bootstrapBody = body;
                request.AsyncHandling = true;
                _bootstrapPhase = 1;
                return null;
            }

            _bootstrapRequest = request;
            _bootstrapBody = body;
            request.AsyncHandling = true;
            _bootstrapPhase = 2;
            return null;
        }

        private void Update()
        {
            if (_webServer != null) _webServer.Pump(HandleWebRequest);
            PumpSolver();
            PumpBootstrap();
            PumpOfflineBests();
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f8Key.wasPressedThisFrame)
            {
                if (_recorder.IsRecording) StopRecording("Emergency stop");
                if (_engine.IsRunning) _engine.Abort("Emergency stop");
                return;
            }


            foreach (MacroDefinition macro in _library.macros)
            {
                if (HotkeyPressed(keyboard, macro))
                {
                    BeginDeterministicPhysics(FindWorldMovement());
                    // Hotkey playback continues from here, not from a course start: no course may finish it.
                    _runCourse = null;
                    _engine.Run(macro, _library);
                    break;
                }
            }
        }

        private static bool HotkeyPressed(Keyboard keyboard, MacroDefinition macro)
        {
            Key key;
            if (string.IsNullOrEmpty(macro.hotkey) || !Enum.TryParse(macro.hotkey, true, out key)) return false;
            if (!keyboard[key].wasPressedThisFrame) return false;
            bool ctrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            bool alt = keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed;
            return (!macro.hotkeyCtrl || ctrl) && (!macro.hotkeyShift || shift) && (!macro.hotkeyAlt || alt);
        }

        private void OnGUI()
        {
            // Website-only UI. Intentionally draw nothing in the game.
        }

        private void CaptureCheckpoint()
        {
            Movement movement = FindWorldMovement();
            if (movement == null) return;
            SectionCheckpoint checkpoint = SectionCheckpointService.Capture(movement,
                (SelectedMacro == null ? "Section" : SelectedMacro.name) + " start", MacroLibrary.CurrentSchemaVersion);
            _checkpoints.Insert(0, checkpoint);
            WriteJsonAtomic(Path.Combine(_checkpointsPath, SafeId(checkpoint.id) + ".json"), checkpoint, false);
        }

        private void RestoreCheckpoint(SectionCheckpoint checkpoint)
        {
            Movement movement = FindWorldMovement();
            CheckpointOperationResult result = SectionCheckpointService.Restore(movement, checkpoint, MacroLibrary.CurrentSchemaVersion);
            if (!result.Success) Logger.LogError(result.Message); else Logger.LogInfo(result.Message);
        }

        private bool RestoreRouteStart(MacroDefinition route)
        {
            Movement movement = FindWorldMovement();
            if (movement != null) _engine.ClearInjectedInput(movement);
            // A route named "<course>_..." or "<course> ..." always starts at that course, whatever was played before.
            // The course is its id ("2_WJ ...") or its name ("bonus1_WJ ...").
            Match courseName = Regex.Match(route.name ?? "", @"^([^_\s]+)[_ ]");
            CourseDef named = null;
            if (courseName.Success)
            {
                string token = courseName.Groups[1].Value;
                named = int.TryParse(token, out int courseId) ? _courses.Find(c => c.id == courseId)
                    : _courses.Find(c => string.Equals(c.name, token, StringComparison.OrdinalIgnoreCase));
            }
            _runCourse = named;
            SectionCheckpoint checkpoint = string.IsNullOrEmpty(route.checkpointId) ? null : _checkpoints.Find(c => c.id == route.checkpointId);
            if (movement != null && checkpoint != null)
            {
                CheckpointOperationResult restored = SectionCheckpointService.Restore(movement, checkpoint, MacroLibrary.CurrentSchemaVersion);
                if (restored.Success) return true;
                Logger.LogWarning(restored.Message + "; using quick restart.");
            }
            MethodInfo respawn = AccessTools.Method(typeof(Movement), "respawn");
            if (named != null)
            {
                _preservedCourseResetPoint = new Vector2(named.x, named.y);
                _hasPreservedCourseResetPoint = true;
            }
            if (movement != null && respawn != null)
            {
                // Courses use courseResetPoint as a one-shot override and may replace it
                // with later checkpoints during a run. Capture the legitimate course start
                // once for this plugin session, then reuse it for every replay/optimizer
                // trial instead of rereading the mutable field.
                if (!_hasPreservedCourseResetPoint)
                {
                    // Some courses leave courseResetPoint at zero and keep the real
                    // start in respawnPoint until the player reaches a later checkpoint.
                    // Capture whichever legitimate reset target the game currently owns
                    // before the first automated attempt can overwrite it.
                    Vector2 initialResetPoint = movement.courseResetPoint != Vector2.zero
                        ? movement.courseResetPoint - CurrentOrigin()
                        : movement.respawnPoint;
                    if (initialResetPoint != Vector2.zero)
                    {
                        _preservedCourseResetPoint = initialResetPoint;
                        _hasPreservedCourseResetPoint = true;
                    }
                }
                // A run whose start is exactly a course start (turbo's "start" override picks it the same way)
                // is scored on that course: its end gate, or its reward box when it has one.
                if (_runCourse == null && _hasPreservedCourseResetPoint)
                    _runCourse = _courses.Find(c => c.x == _preservedCourseResetPoint.x && c.y == _preservedCourseResetPoint.y);
                if (_hasPreservedCourseResetPoint) movement.courseResetPoint = _preservedCourseResetPoint + CurrentOrigin();
                // A death schedules deathRespawn after a delay. If it survives into the
                // next trial it resets the player in the middle of that attempt.
                movement.CancelInvoke("deathRespawn");
                movement.CancelInvoke("respawn");
                bool respawnRan = false;
                try
                {
                    respawn.Invoke(movement, new object[] { true });
                    respawnRan = true;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("respawn(true) failed (" + (ex.InnerException != null ? ex.InnerException.Message : ex.Message) + "); applying restart geometry directly.");
                }
                if (!respawnRan) ApplyRestartGeometry(movement);
                if (_hasPreservedCourseResetPoint) movement.courseResetPoint = _preservedCourseResetPoint + CurrentOrigin();
                Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    // Movement.respawn teleports only the Transform. Keep the physics body
                    // at that exact point so the next simulation does not resolve a stale
                    // body/transform pair differently depending on frame scheduling.
                    body.position = movement.transform.position;
                    body.linearVelocity = Vector2.zero;
                    body.angularVelocity = 0f;
                    Physics2D.SyncTransforms();
                }
            }
            else Logger.LogWarning("No checkpoint or quick restart was available.");
            return false;
        }

        private void BeginDeterministicPhysics(Movement movement)
        {
            Rigidbody2D body = movement == null ? null : movement.GetComponent<Rigidbody2D>();
            if (body == null || body == _deterministicBody) return;
            EndDeterministicPhysics();
            _deterministicBody = body;
            _savedBodyInterpolation = body.interpolation;
            // Movement.FixedUpdate performs jump nudges through Transform.position.
            // With interpolation enabled that property is a render-time pose between
            // fixed states, so the nudge feeds render phase back into physics (including X).
            // None exposes the authoritative body pose to the game during TAS playback.
            body.interpolation = RigidbodyInterpolation2D.None;
        }

        private void EndDeterministicPhysics()
        {
            if (_deterministicBody == null) return;
            try { _deterministicBody.interpolation = _savedBodyInterpolation; }
            catch (Exception ex) { Logger.LogWarning("Could not restore Rigidbody2D interpolation: " + ex.Message); }
            _deterministicBody = null;
        }

        private void DuplicateMacro()
        {
            MacroDefinition copy = JsonConvert.DeserializeObject<MacroDefinition>(JsonConvert.SerializeObject(SelectedMacro));
            copy.id = Guid.NewGuid().ToString("N"); copy.name += " copy"; copy.hotkey = ""; copy.favorite = false; _library.macros.Add(copy); _library.selectedIndex = _library.macros.Count - 1; SaveLibrary();
        }

        private void StartRecording()
        {
            EnsureGameUnpaused();
            if (_engine.IsRunning) _engine.Abort("Recording started");
            string name = (SelectedMacro == null ? "Run" : SelectedMacro.name) + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            _recorder.Start(name);
            Logger.LogInfo("Fixed-tick recording started: " + name);
        }

        private void StopRecording(string reason)
        {
            RecordingDefinition recording = _recorder.Stop(reason);
            if (recording == null) return;
            _recordings.Insert(0, recording);
            SaveRecording(recording);
            Logger.LogInfo("Recording saved: " + recording.samples.Count + " physics frames, " + recording.semanticSteps.Count + " actions");
        }

        private void CreateRouteFromRecording(RecordingDefinition recording)
        {
            MacroDefinition macro = new MacroDefinition { name = recording.name };
            foreach (MacroStep step in ExactInputCompressor.Compress(recording.samples, recording.fixedDeltaMs))
                macro.steps.Add(step);
            _library.macros.Add(macro);
            _library.selectedIndex = _library.macros.Count - 1;
            SaveLibrary();
        }

        private void DeleteRecording(RecordingDefinition recording)
        {
            _recordings.Remove(recording);
            string path = Path.Combine(_recordingsPath, SafeId(recording.id) + ".json");
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Logger.LogError("Could not delete recording: " + ex.Message); }
        }

        private void ReplaySelected(int startDelayTicks = 0)
        {
            MacroDefinition macro = SelectedMacro;
            if (macro == null) return;
            EnsureGameUnpaused();
            // Web/UI commands arrive during rendered Update frames. Queue the reset and
            // arming until the prefix of Movement's next full fixed tick.
            _pendingReplay = macro;
            _pendingReplayStartDelayTicks = Mathf.Max(0, startDelayTicks);
        }

        // A category route ("<course>_NO_WJ|WJ|WJDASH|WJ_DASH_AJ <time>") plays back with that category's unlocks, like
        // the solver judged it; the save's own unlocks come back when the attempt ends.
        private readonly Dictionary<string, object> _playbackSavedAbilities = new Dictionary<string, object>();

        // The category ladder: none < wj < wjdash < wjdashaj (wall jump, dash, air jump).
        internal static int CapsLevel(string caps)
        {
            return caps == "wjdashaj" ? 3 : caps == "wjdash" ? 2 : caps == "wj" ? 1 : 0;
        }

        // A category route label ("WJ_DASH_AJ") back to the solver's caps key ("wjdashaj").
        internal static string CapsFor(string label)
        {
            return label == "NO_WJ" ? "none" : label == "WJ_DASH_AJ" ? "wjdashaj" : label.ToLowerInvariant();
        }

        // What a category means in game fields. Dash comes with one air dash and air jump with one air jump (the
        // game's own course 4 start pairs them that way); the counters start full, the ground refills them after that.
        internal static (string field, object value)[] CategoryAbilities(string caps)
        {
            int level = CapsLevel(caps);
            int dashes = level >= 2 ? 1 : 0;
            int jumps = level >= 3 ? 1 : 0;
            return new (string, object)[] { ("wallJumpUnlocked", level >= 1), ("dashUnlocked", level >= 2),
                ("maxAirDashes", dashes), ("airDashesLeft", dashes),
                ("doubleJumpUnlocked", level >= 3), ("maxAirJumps", jumps), ("airJumpsLeft", jumps) };
        }

        // Sets a category's abilities; returns the previous values so they can be put back.
        internal static List<(string field, object value)> ApplyAbilities(Movement movement, string caps)
        {
            var saved = new List<(string, object)>();
            foreach (var (field, value) in CategoryAbilities(caps))
            {
                saved.Add((field, GetMovementField(movement, field)));
                SetMovementField(movement, field, value);
            }
            return saved;
        }

        private void ApplyCategoryAbilities(MacroDefinition route, Movement movement)
        {
            Match category = Regex.Match(route.name ?? "", @"^([^_\s]+)_(NO_WJ|WJDASH|WJ_DASH_AJ|WJ)(?: |$)");
            if (!category.Success || movement == null) return;
            string caps = CapsFor(category.Groups[2].Value);
            foreach (var (field, value) in ApplyAbilities(movement, caps))
                if (!_playbackSavedAbilities.ContainsKey(field)) _playbackSavedAbilities[field] = value;
        }

        private void RestorePlaybackAbilities()
        {
            Movement movement = FindWorldMovement();
            if (movement != null)
                foreach (KeyValuePair<string, object> ability in _playbackSavedAbilities) SetMovementField(movement, ability.Key, ability.Value);
            _playbackSavedAbilities.Clear();
        }

        private void SaveAttemptResult(bool success, string reason, int action, long ticks)
        {
            RestorePlaybackAbilities();
            if (_silentEvaluation)
            {
                EndDeterministicPhysics();
                _silentFinished = success && (reason == "Course finish detected" || reason == MacroEngine.RewardFinishReason);
                _silentTime = _engine.AuthoritativeCompletionTime;
                return;
            }
            bool hasGameTime = success && _engine.AuthoritativeCompletionTime > 0f;
            AttemptResult result = new AttemptResult {
                routeId = _engine.ActiveMacroId,
                routeName = _engine.ActiveMacroName,
                success = success,
                completionTicks = ticks,
                completionTime = hasGameTime ? _engine.AuthoritativeCompletionTime : ticks * Time.fixedDeltaTime,
                timingSource = hasGameTime ? (reason == MacroEngine.RewardFinishReason ? "Run ticks to reward" : "In-game course timer") : "Macro elapsed time",
                courseStartRunTick = _engine.CourseStartRunTick,
                courseStartUnityFrame = _engine.CourseStartUnityFrame,
                courseStartX = _engine.CourseStartPosition.x,
                courseStartY = _engine.CourseStartPosition.y,
                startStateFingerprint = _engine.StartStateFingerprint,
                earlyTrace = _engine.EarlyTrace,
                deterministicPhysics = _deterministicBody != null,
                failureAction = action,
                failureReason = success ? null : reason
            };
            _results.Insert(0, result);
            string path = Path.Combine(_resultsPath, result.timestampUtc.Replace(":", "-") + "-" + result.id + ".json");
            WriteJsonAtomic(path, result, false);
            EndDeterministicPhysics();
        }

        private MacroDefinition SelectedMacro { get { ClampSelections(); return _library.macros.Count == 0 ? null : _library.macros[_library.selectedIndex]; } }
        private void ClampSelections()
        {
            _library.selectedIndex = Mathf.Clamp(_library.selectedIndex, 0, Mathf.Max(0, _library.macros.Count - 1));
        }

        private static string TypeName(MacroStepType type) { return type == MacroStepType.WallJump ? "WALLJUMP" : type.ToString().ToUpperInvariant(); }
        private static string HotkeyText(MacroDefinition m)
        {
            if (string.IsNullOrEmpty(m.hotkey)) return "No hotkey";
            return (m.hotkeyCtrl ? "CTRL + " : "") + (m.hotkeyShift ? "SHIFT + " : "") + (m.hotkeyAlt ? "ALT + " : "") + m.hotkey.ToUpperInvariant();
        }

        private void LoadLibrary()
        {
            if (!File.Exists(_libraryPath))
            {
                _library = new MacroLibrary();
                SaveLibrary();
                return;
            }
            try
            {
                MacroLibrary loaded = JsonConvert.DeserializeObject<MacroLibrary>(File.ReadAllText(_libraryPath));
                ValidateLibrary(loaded);
                _library = loaded;
                if (NormalizeLibraryFrameTiming(_library) | PruneSlowerRoutes()) SaveLibrary();
            }
            catch (Exception ex)
            {
                Logger.LogError("Routes file rejected; starting with the current schema: " + ex.Message);
                Quarantine(_libraryPath);
                _library = new MacroLibrary();
                SaveLibrary();
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    StudioSettings loaded = JsonConvert.DeserializeObject<StudioSettings>(File.ReadAllText(_settingsPath));
                    if (loaded == null || loaded.schemaVersion != StudioSettings.CurrentSchemaVersion)
                        throw new InvalidDataException("Unsupported settings schema.");
                    _settings = loaded;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Settings rejected: " + ex.Message);
                Quarantine(_settingsPath);
                _settings = new StudioSettings();
            }
        }

        private void SaveSettings()
        {
            WriteJsonAtomic(_settingsPath, _settings, true);
        }

        private void LoadRecordings()
        {
            try
            {
                Directory.CreateDirectory(_recordingsPath);
                foreach (string path in Directory.GetFiles(_recordingsPath, "*.json"))
                {
                    try
                    {
                        RecordingDefinition recording = JsonConvert.DeserializeObject<RecordingDefinition>(File.ReadAllText(path));
                        ValidateRecording(recording);
                        // Raw samples are the source of truth. Re-run semantic inference so
                        // recordings made before 6.2 gain idle trimming and correct walljump directions.
                        InputRecorder.Reprocess(recording);
                        _recordings.Add(recording);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Recording rejected " + Path.GetFileName(path) + ": " + ex.Message);
                        Quarantine(path);
                    }
                }
                _recordings.Sort((a, b) => string.CompareOrdinal(b.createdUtc, a.createdUtc));
            }
            catch (Exception ex) { Logger.LogError("Could not load recordings: " + ex); }
        }

        private void LoadResults()
        {
            try
            {
                Directory.CreateDirectory(_resultsPath);
                foreach (string path in Directory.GetFiles(_resultsPath, "*.json"))
                {
                    try
                    {
                        // Oracle/turbo evaluations and their diagnostics are fidelity data, not studio attempts.
                        if (path.EndsWith(".diagnostics.json", StringComparison.OrdinalIgnoreCase)) continue;
                        string text = File.ReadAllText(path);
                        if (text.Contains("oracle-eval-")) continue;
                        AttemptResult result = JsonConvert.DeserializeObject<AttemptResult>(text);
                        if (result == null || result.schemaVersion != AttemptResult.CurrentSchemaVersion || string.IsNullOrEmpty(result.id))
                            throw new InvalidDataException("Unsupported or incomplete result.");
                        _results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Result rejected " + Path.GetFileName(path) + ": " + ex.Message);
                        Quarantine(path);
                    }
                }
                _results.Sort((a, b) => string.CompareOrdinal(b.timestampUtc, a.timestampUtc));
            }
            catch (Exception ex) { Logger.LogError("Could not load results: " + ex); }
        }

        private void LoadCheckpoints()
        {
            try
            {
                Directory.CreateDirectory(_checkpointsPath);
                foreach (string path in Directory.GetFiles(_checkpointsPath, "*.json"))
                {
                    try
                    {
                        SectionCheckpoint checkpoint = JsonConvert.DeserializeObject<SectionCheckpoint>(File.ReadAllText(path));
                        if (checkpoint == null || checkpoint.schemaVersion != SectionCheckpoint.CurrentSchemaVersion || string.IsNullOrEmpty(checkpoint.id))
                            throw new InvalidDataException("Unsupported or incomplete checkpoint.");
                        _checkpoints.Add(checkpoint);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Checkpoint rejected " + Path.GetFileName(path) + ": " + ex.Message);
                        Quarantine(path);
                    }
                }
                _checkpoints.Sort((a, b) => string.CompareOrdinal(b.createdUtc, a.createdUtc));
            }
            catch (Exception ex) { Logger.LogError("Could not load checkpoints: " + ex); }
        }

        private void SaveRecording(RecordingDefinition recording)
        {
            WriteJsonAtomic(Path.Combine(_recordingsPath, SafeId(recording.id) + ".json"), recording, false);
        }

        private static void ValidateLibrary(MacroLibrary library)
        {
            if (library == null) throw new InvalidDataException("File contains no library.");
            if (library.schemaVersion != MacroLibrary.CurrentSchemaVersion)
                throw new InvalidDataException("Unsupported schema " + library.schemaVersion + "; expected " + MacroLibrary.CurrentSchemaVersion + ".");
            if (library.macros == null)
                throw new InvalidDataException("Required route collection is missing.");
            foreach (MacroDefinition route in library.macros)
                if (route == null || string.IsNullOrEmpty(route.id) || route.steps == null || route.timeline == null || route.timeline.actions == null)
                    throw new InvalidDataException("A route is incomplete.");
        }

        private static void ValidateRecording(RecordingDefinition recording)
        {
            if (recording == null) throw new InvalidDataException("File contains no recording.");
            if (recording.schemaVersion != RecordingDefinition.CurrentSchemaVersion)
                throw new InvalidDataException("Unsupported schema " + recording.schemaVersion + "; expected " + RecordingDefinition.CurrentSchemaVersion + ".");
            if (string.IsNullOrEmpty(recording.id) || recording.samples == null || recording.semanticSteps == null)
                throw new InvalidDataException("Recording is incomplete.");
        }

        private static bool NormalizeLibraryFrameTiming(MacroLibrary library)
        {
            bool changed = false;
            int tickMs = Mathf.Max(1, Mathf.RoundToInt(Time.fixedDeltaTime * 1000f));
            foreach (MacroDefinition route in library.macros)
            {
                foreach (MacroStep step in route.steps) changed |= NormalizeStepFrameTiming(step, tickMs);
                foreach (TimelineAction action in route.timeline.actions)
                    if (action != null && action.step != null) changed |= NormalizeStepFrameTiming(action.step, tickMs);
            }
            return changed;
        }

        private static bool NormalizeStepFrameTiming(MacroStep step, int tickMs)
        {
            if (step == null) return false;
            bool changed = false;
            if (step.endCondition == ActionEndCondition.Duration)
            {
                step.durationTicks = Mathf.Max(1, Mathf.RoundToInt(step.durationMs / (float)tickMs));
                step.endCondition = ActionEndCondition.FixedTicks;
                changed = true;
            }
            else if (step.endCondition == ActionEndCondition.FixedTicks && step.durationTicks < 1)
            {
                step.durationTicks = 1;
                changed = true;
            }
            int durationMs = Mathf.Max(1, step.durationTicks) * tickMs;
            if (step.durationMs != durationMs) { step.durationMs = durationMs; changed = true; }
            if (step.timeoutTicks < 1)
            {
                step.timeoutTicks = Mathf.Max(1, Mathf.RoundToInt(step.timeoutMs / (float)tickMs));
                changed = true;
            }
            int timeoutMs = step.timeoutTicks * tickMs;
            if (step.timeoutMs != timeoutMs) { step.timeoutMs = timeoutMs; changed = true; }
            return changed;
        }

        private static void Quarantine(string path)
        {
            if (!File.Exists(path)) return;
            string rejected = path + ".rejected-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Move(path, rejected); }
            catch (Exception ex) { if (Log != null) Log.LogError("Could not quarantine " + path + ": " + ex.Message); }
        }

        private void SaveLibrary()
        {
            WriteJsonAtomic(_libraryPath, _library, true);
        }

        private void WriteJsonAtomic(string path, object value, bool keepBackup)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.Indented));
                if (File.Exists(path))
                {
                    string backup = keepBackup ? path + ".bak" : null;
                    File.Replace(temporary, path, backup);
                }
                else File.Move(temporary, path);
            }
            catch (Exception ex) { Logger.LogError("Could not save " + path + ": " + ex); }
        }

        private static string SafeId(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new InvalidDataException("A stable ID is required.");
            foreach (char invalid in Path.GetInvalidFileNameChars()) id = id.Replace(invalid, '_');
            return id;
        }

        private object HandleWebRequest(WebStudioRequest request)
        {
            JObject body = string.IsNullOrWhiteSpace(request.Body) ? new JObject() : JObject.Parse(request.Body);
            if (request.Path.StartsWith("/api/enginesim/", StringComparison.Ordinal)) return HandleEngineSim(request, body);
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
                StartSolver(Number(body, "course", 0), Text(body, "capabilities"));
                return new { ok = true };
            }
            if (request.Path == "/api/solver/stop")
            {
                StopSolver("Stopped");
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
            if (request.Path == "/api/session/setup")
            {
                // Live changes to the running game (never the save file): {"start": {x, y},
                // "abilities": {"dashUnlocked": true, "maxAirDashes": 1, "airDashesLeft": 1, ...}}. Replies with the fields as set.
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
                            : ability.Value.Type == JTokenType.Integer ? (object)(int)ability.Value : (float)ability.Value;
                        SetMovementField(player, ability.Name, value);
                        applied[ability.Name] = GetMovementField(player, ability.Name);
                    }
                return new { ok = true, abilities = applied };
            }
            if (request.Path == "/api/oracle/bootstrap")
            {
                return BootstrapOracle(request, body);
            }
            if (request.Method == "GET" && request.Path == "/api/state") return BuildWebState();
            if (request.Path == "/api/simulation/export")
            {
                Movement movement = FindWorldMovement();
                SimulationModelExport model = SimulationModelExtractor.Capture(movement, MacroLibrary.CurrentSchemaVersion);
                string directory = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "simulation");
                string path = SimulationModelExtractor.Save(model, directory);
                return new { ok = true, path = path, scalarCount = model.movement.Count + model.rigidbody.Count,
                    colliderCount = model.colliders.Count, model = model };
            }
            if (request.Path == "/api/simulation/import-candidate")
            {
                return ImportOfflineCandidate(body);
            }
            if (request.Method != "POST") throw new InvalidOperationException("Unsupported request method.");

            if (request.Path == "/api/routes/select")
            {
                string id = Text(body, "routeId");
                int index = _library.macros.FindIndex(r => r.id == id);
                if (index < 0) throw new InvalidOperationException("Route not found.");
                _library.selectedIndex = index;
                SaveLibrary();
                return new { ok = true };
            }
            if (request.Path == "/api/routes/run")
            {
                if (SelectedMacro == null) throw new InvalidOperationException("Select a route first.");
                ReplaySelected(Number(body, "startDelayTicks", 0));
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
                if (route.favorite) throw new InvalidOperationException("Unfavorite this route before renaming it.");
                if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Route name cannot be empty.");
                RejectCategoryName(name);
                route.name = name;
                SaveLibrary();
                return new { ok = true };
            }
            if (request.Path == "/api/routes/favorite")
            {
                MacroDefinition route = _library.macros.Find(r => r.id == Text(body, "routeId"));
                if (route == null) throw new InvalidOperationException("Route not found.");
                route.favorite = body["favorite"] == null ? !route.favorite : (bool)body["favorite"];
                SaveLibrary();
                return new { ok = true, favorite = route.favorite };
            }
            if (request.Path == "/api/routes/delete")
            {
                if (_engine.IsRunning)
                    throw new InvalidOperationException("Stop playback before deleting a route.");
                string id = Text(body, "routeId");
                int index = _library.macros.FindIndex(r => r.id == id);
                if (index < 0) throw new InvalidOperationException("Route not found.");
                if (_library.macros[index].favorite) throw new InvalidOperationException("Favorited routes cannot be deleted. Unfavorite it first.");
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
                _pendingReplay = null;
                _pendingReplayStartDelayTicks = 0;
                if (_engine.IsRunning) _engine.Abort("Stopped from Web Studio");
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
                if (recording.favorite) throw new InvalidOperationException("Favorited recordings cannot be deleted. Unfavorite it first.");
                DeleteRecording(recording);
                return new { ok = true };
            }
            if (request.Path == "/api/recordings/rename")
            {
                RecordingDefinition recording = _recordings.Find(r => r.id == Text(body, "recordingId"));
                string name = Text(body, "name").Trim();
                if (recording == null) throw new InvalidOperationException("Recording not found.");
                if (recording.favorite) throw new InvalidOperationException("Unfavorite this recording before renaming it.");
                if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Recording name cannot be empty.");
                recording.name = name;
                SaveRecording(recording);
                return new { ok = true };
            }
            if (request.Path == "/api/recordings/favorite")
            {
                RecordingDefinition recording = _recordings.Find(r => r.id == Text(body, "recordingId"));
                if (recording == null) throw new InvalidOperationException("Recording not found.");
                recording.favorite = body["favorite"] == null ? !recording.favorite : (bool)body["favorite"];
                SaveRecording(recording);
                return new { ok = true, favorite = recording.favorite };
            }
            if (request.Path == "/api/actions/split")
            {
                MacroDefinition route = SelectedMacro;
                int index = Number(body, "actionIndex", -1);
                if (route == null || index < 0 || index >= route.steps.Count) throw new InvalidOperationException("Action not found.");
                if (route.favorite) throw new InvalidOperationException("Favorited routes are read-only. Unfavorite it before editing.");
                MacroStep action = route.steps[index];
                if (action.rawJumpPressed || action.rawJumpReleased || action.rawDashPressed)
                    throw new InvalidOperationException("Split input blocks cannot contain button edges.");
                int firstTicks = Number(body, "firstDurationTicks", 1);
                if (firstTicks < 1 || firstTicks >= action.durationTicks)
                    throw new InvalidOperationException("Split point must be inside the action duration.");
                MacroStep first = action.Copy();
                MacroStep second = action.Copy();
                first.durationTicks = firstTicks;
                first.durationMs = Mathf.RoundToInt(firstTicks * Time.fixedDeltaTime * 1000f);
                second.durationTicks = action.durationTicks - firstTicks;
                second.durationMs = Mathf.RoundToInt(second.durationTicks * Time.fixedDeltaTime * 1000f);
                if (body["firstRawMoveX"] != null)
                    first.rawMoveX = Mathf.Clamp(FloatNumber(body, "firstRawMoveX", first.rawMoveX), -1f, 1f);
                if (body["firstRawMoveY"] != null)
                    first.rawMoveY = Mathf.Clamp(FloatNumber(body, "firstRawMoveY", first.rawMoveY), -1f, 1f);
                first.label = action.label + " (split 1)";
                second.label = action.label + " (split 2)";
                route.steps[index] = first;
                route.steps.Insert(index + 1, second);
                route.timeline = TimelineDocument.FromSequentialSteps(route.steps, Time.fixedDeltaTime * 1000f);
                SaveLibrary();
                return new { ok = true };
            }
            if (request.Path == "/api/actions/update")
            {
                MacroDefinition route = SelectedMacro;
                int index = Number(body, "actionIndex", -1);
                if (route == null || index < 0 || index >= route.steps.Count) throw new InvalidOperationException("Action not found.");
                if (route.favorite) throw new InvalidOperationException("Favorited routes are read-only. Unfavorite it before editing.");
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
                    action.durationMs = Mathf.RoundToInt(action.durationTicks * Time.fixedDeltaTime * 1000f);
                    action.endCondition = ActionEndCondition.FixedTicks;
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
                route.timeline = TimelineDocument.FromSequentialSteps(route.steps, Time.fixedDeltaTime * 1000f);
                SaveLibrary();
                return new { ok = true };
            }
            if (request.Path == "/api/checkpoints/capture")
            {
                CaptureCheckpoint();
                return new { ok = true };
            }
            if (request.Path == "/api/checkpoints/restore")
            {
                SectionCheckpoint checkpoint = _checkpoints.Find(c => c.id == Text(body, "checkpointId"));
                if (checkpoint == null) throw new InvalidOperationException("Checkpoint not found.");
                RestoreCheckpoint(checkpoint);
                return new { ok = true };
            }
            if (request.Path == "/api/checkpoints/assign")
            {
                if (SelectedMacro == null) throw new InvalidOperationException("Select a route first.");
                if (SelectedMacro.favorite) throw new InvalidOperationException("Favorited routes are read-only. Unfavorite it before changing the start state.");
                string id = Text(body, "checkpointId");
                if (!string.IsNullOrEmpty(id) && _checkpoints.Find(c => c.id == id) == null) throw new InvalidOperationException("Checkpoint not found.");
                SelectedMacro.checkpointId = id;
                SaveLibrary();
                return new { ok = true };
            }
            throw new InvalidOperationException("Unknown Web Studio command: " + request.Path);
        }

        // AGENTS.md rule 4: a category route name ("<course>_<CAPS> <time>") only comes from the gate (SaveSolverRoute).
        private static void RejectCategoryName(string name)
        {
            if (Regex.IsMatch(name ?? "", @"^[^_\s]+_(NO_WJ|WJDASH|WJ_DASH_AJ|WJ) [\d.]+$", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Category route names are reserved for routes that passed the solver's gate.");
        }

        private object ImportOfflineCandidate(JObject body)
        {
            MacroDefinition candidate = BuildOfflineCandidate(body);
            RejectCategoryName(candidate.name);
            _library.macros.Add(candidate);
            _library.selectedIndex = _library.macros.Count - 1;
            SaveLibrary();
            return new { ok = true, routeId = candidate.id, name = candidate.name, actionCount = candidate.steps.Count };
        }

        private MacroDefinition BuildOfflineCandidate(JObject body)
        {
            string routeId = Text(body, "routeId");
            MacroDefinition source = _library.macros.Find(r => r.id == routeId);
            if (source == null) throw new InvalidOperationException("Source route not found.");
            MacroDefinition candidate = JsonConvert.DeserializeObject<MacroDefinition>(JsonConvert.SerializeObject(source));
            candidate.id = Guid.NewGuid().ToString("N");
            candidate.name = string.IsNullOrWhiteSpace(Text(body, "name")) ? source.name + " [offline candidate]" : Text(body, "name");
            candidate.hotkey = "";
            candidate.favorite = false;
            JArray durations = body["durations"] as JArray;
            if (durations == null || durations.Count != candidate.steps.Count)
                throw new InvalidOperationException("Candidate duration count does not match the source route.");
            for (int i = 0; i < candidate.steps.Count; i++)
            {
                candidate.steps[i].durationTicks = Math.Max(1, (int)durations[i]);
                candidate.steps[i].durationMs = Mathf.RoundToInt(candidate.steps[i].durationTicks * Time.fixedDeltaTime * 1000f);
                candidate.steps[i].endCondition = ActionEndCondition.FixedTicks;
            }
            JArray rawMoveX = body["rawMoveX"] as JArray;
            if (rawMoveX != null)
            {
                if (rawMoveX.Count != candidate.steps.Count)
                    throw new InvalidOperationException("Candidate analog-input count does not match the source route.");
                for (int i = 0; i < candidate.steps.Count; i++)
                    candidate.steps[i].rawMoveX = Mathf.Clamp((float)rawMoveX[i], -1f, 1f);
            }
            JArray noJump = body["noJump"] as JArray;
            if (noJump != null)
            {
                if (noJump.Count != candidate.steps.Count)
                    throw new InvalidOperationException("Candidate jump-removal count does not match the source route.");
                for (int i = 0; i < candidate.steps.Count; i++)
                    if ((bool)noJump[i])
                        candidate.steps[i].rawJumpPressed = candidate.steps[i].rawJumpHeld = candidate.steps[i].rawJumpReleased = false;
            }
            InsertJump(candidate, Number(body, "hopStep", -1), Number(body, "hopTick", -1), Number(body, "hopHold", 0));
            candidate.timeline = TimelineDocument.FromSequentialSteps(candidate.steps, Time.fixedDeltaTime * 1000f);
            return candidate;
        }

        private static void InsertJump(MacroDefinition route, int actionIndex, int requestedTick, int requestedHoldTicks)
        {
            if (route == null || actionIndex < 0 || actionIndex >= route.steps.Count || requestedTick < 0) return;
            MacroStep source = route.steps[actionIndex];
            if (source.type != MacroStepType.RawInput || source.durationTicks < 3) return;
            int jumpTick = Math.Max(0, Math.Min(source.durationTicks - 2, requestedTick));
            int holdTicks = Math.Max(0, Math.Min(source.durationTicks - jumpTick - 1, requestedHoldTicks));
            int suffixTicks = source.durationTicks - jumpTick - holdTicks - 1;
            List<MacroStep> replacement = new List<MacroStep>();
            if (jumpTick > 0) replacement.Add(RawSegment(source, jumpTick, false, false, false, "before hop"));
            if (holdTicks == 0)
                replacement.Add(RawSegment(source, 1, true, false, true, "inserted micro-hop"));
            else
            {
                replacement.Add(RawSegment(source, holdTicks, true, true, false, "inserted hop"));
                replacement.Add(RawSegment(source, 1, false, false, true, "hop release"));
            }
            if (suffixTicks > 0) replacement.Add(RawSegment(source, suffixTicks, false, false, false, "after hop"));
            route.steps.RemoveAt(actionIndex);
            route.steps.InsertRange(actionIndex, replacement);
        }

        private static MacroStep RawSegment(MacroStep source, int ticks, bool pressed, bool held, bool released, string suffix)
        {
            MacroStep step = source.Copy();
            step.label = source.label + " (" + suffix + ")";
            step.durationTicks = Math.Max(1, ticks);
            step.durationMs = Math.Max(1, (int)Math.Round(step.durationTicks * Time.fixedDeltaTime * 1000f));
            step.endCondition = ActionEndCondition.FixedTicks;
            step.rawJumpPressed = pressed;
            step.rawJumpHeld = held;
            step.rawJumpReleased = released;
            return step;
        }

        private object BuildWebState()
        {
            MacroDefinition selected = SelectedMacro;
            List<object> routes = new List<object>();
            foreach (MacroDefinition route in _library.macros)
            {
                List<object> actions = new List<object>();
                foreach (MacroStep action in route.steps) actions.Add(new {
                    id = action.id, label = action.label, type = TypeName(action.type), direction = action.direction,
                    wallJumpHoldDirection = action.wallJumpHoldDirection,
                    durationTicks = action.durationTicks, durationMs = action.durationMs,
                    rawMoveX = action.rawMoveX, rawMoveY = action.rawMoveY,
                    rawJumpPressed = action.rawJumpPressed, rawJumpHeld = action.rawJumpHeld,
                    rawJumpReleased = action.rawJumpReleased,
                    rawDashPressed = action.rawDashPressed, rawDashHeld = action.rawDashHeld,
                    dashJumpFrame = action.dashJumpFrame, enabled = action.enabled, endCondition = action.endCondition.ToString()
                });
                routes.Add(new { id = route.id, name = route.name, favorite = route.favorite, hotkey = HotkeyText(route), checkpointId = route.checkpointId, actions = actions });
            }
            List<object> checkpoints = new List<object>();
            foreach (SectionCheckpoint checkpoint in _checkpoints)
                checkpoints.Add(new { id = checkpoint.id, name = checkpoint.name, createdUtc = checkpoint.createdUtc,
                    assigned = selected != null && selected.checkpointId == checkpoint.id });
            List<object> recordings = new List<object>();
            foreach (RecordingDefinition recording in _recordings)
            {
                List<object> actions = new List<object>();
                foreach (MacroStep action in recording.semanticSteps)
                    actions.Add(new { label = action.label, type = TypeName(action.type), direction = action.direction,
                        wallJumpHoldDirection = action.wallJumpHoldDirection, durationTicks = action.durationTicks,
                        durationMs = action.durationMs });
                recordings.Add(new { id = recording.id, name = recording.name, favorite = recording.favorite, createdUtc = recording.createdUtc,
                    stopReason = recording.stopReason, sampleCount = recording.samples.Count,
                    actionCount = recording.semanticSteps.Count, courseTimeSeconds = recording.courseTimeSeconds,
                    courseStartTick = recording.courseStartTick, actions = actions });
            }
            List<object> results = new List<object>();
            foreach (AttemptResult result in _results)
                if (selected != null && result.routeId == selected.id)
                    results.Add(new { id = result.id, success = result.success, completionTime = result.completionTime,
                        completionTicks = result.completionTicks, timingSource = result.timingSource,
                        courseStartRunTick = result.courseStartRunTick, courseStartUnityFrame = result.courseStartUnityFrame,
                        courseStartX = result.courseStartX, courseStartY = result.courseStartY,
                        startStateFingerprint = result.startStateFingerprint,
                        earlyTrace = result.earlyTrace,
                        firstJumpPositionOffsetX = result.firstJumpPositionOffsetX,
                        courseStartPositionOffsetX = result.courseStartPositionOffsetX,
                        deterministicPhysics = result.deterministicPhysics,
                        failureReason = result.failureReason, timestampUtc = result.timestampUtc });

            Movement player = FindWorldMovement();
            return new { connected = true, version = PluginVersion, engineStatus = _engine.Status,
                // Read-only: the live unlocks, to check that playback and the tools put the save's back.
                unlocks = player == null ? null : new { wallJump = GetMovementBool(player, "wallJumpUnlocked", false),
                    dash = GetMovementBool(player, "dashUnlocked", false), maxAirDashes = GetMovementField(player, "maxAirDashes"),
                    airDashesLeft = GetMovementField(player, "airDashesLeft"),
                    airJump = GetMovementBool(player, "doubleJumpUnlocked", false), maxAirJumps = GetMovementField(player, "maxAirJumps") },
                paused = IsGamePaused(),
                capabilitiesLocked = _settings.lockCapabilities,
                running = _engine.IsRunning,
                recording = _recorder.IsRecording, selectedRouteId = selected == null ? "" : selected.id,
                routes = routes, recordings = recordings, checkpoints = checkpoints, results = results,
                courses = BuildCourseWebState(),
                solver = BuildSolverWebState() };
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

        private static bool IsGamePaused()
        {
            Movement movement = FindWorldMovement();
            if (movement == null) return Time.timeScale == 0f;
            FieldInfo pauseMenuField = AccessTools.Field(typeof(Movement), "pauseMenu");
            object pauseMenu = pauseMenuField == null ? null : pauseMenuField.GetValue(movement);
            FieldInfo menuOpen = pauseMenu == null ? null : AccessTools.Field(pauseMenu.GetType(), "menuOpen");
            return menuOpen != null && (bool)menuOpen.GetValue(pauseMenu);
        }

        private static void EnsureGameUnpaused()
        {
            Movement movement = FindWorldMovement();
            if (movement == null) return;
            FieldInfo pauseMenuField = AccessTools.Field(typeof(Movement), "pauseMenu");
            object pauseMenu = pauseMenuField == null ? null : pauseMenuField.GetValue(movement);
            FieldInfo menuOpen = pauseMenu == null ? null : AccessTools.Field(pauseMenu.GetType(), "menuOpen");
            if (menuOpen != null && (bool)menuOpen.GetValue(pauseMenu))
            {
                MethodInfo toggle = AccessTools.Method(pauseMenu.GetType(), "menuButtonPressed");
                if (toggle != null) toggle.Invoke(pauseMenu, null);
            }
        }

        private void OnDestroy()
        {
            if (_recorder.IsRecording) StopRecording("Game closed");
            EndDeterministicPhysics();
            SaveLibrary();
            SaveSettings();
            if (_webServer != null) _webServer.Dispose();
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
