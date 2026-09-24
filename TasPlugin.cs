using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace IGTAPTasMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class TasPlugin : BaseUnityPlugin
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
        private Process _offlineProcess;
        private string _offlineOutputPath;
        private string _offlineRouteId;
        private string _offlineRouteName;
        private string _offlineError;
        private StudioSettings _settings = new StudioSettings();
        private readonly List<RecordingDefinition> _recordings = new List<RecordingDefinition>();
        private readonly List<AttemptResult> _results = new List<AttemptResult>();
        private readonly List<SectionCheckpoint> _checkpoints = new List<SectionCheckpoint>();
        private GhostPathRenderer _ghostRenderer;
        private Rect _window = new Rect(62f, 24f, 1040f, 700f);
        private Vector2 _libraryScroll;
        private Vector2 _timelineScroll;
        private bool _showEditor;
        private bool _editingBundles;
        private bool _viewingRecordings;
        private bool _viewingResults;
        private bool _viewingCheckpoints;
        private bool _viewingSettings;
        private int _selectedRecordingIndex;
        private int _selectedStep = -1;
        private float _pendingCourseTime;
        private MacroDefinition _pendingReplay;
        private int _pendingReplayStartDelayTicks;
        private float? _pendingReplayFirstJumpPositionOffsetX;
        private float _pendingReplayCourseStartPositionOffsetX;
        private Rigidbody2D _deterministicBody;
        private RigidbodyInterpolation2D _savedBodyInterpolation;
        private bool _hasPreservedCourseResetPoint;
        private Vector2 _preservedCourseResetPoint;
        private static readonly FieldInfo CourseCurrentPathTimeField = AccessTools.Field(typeof(courseScript), "currentPathTime");
        private MacroDefinition _bindingMacro;
        private TimelineEditorState _timelineEditor;
        private string _timelineRouteId;
        private string _dragActionId;
        private bool _dragResize;
        private int _dragTickOffset;

        private WebStudioRequest _activeOracleRequest;
        private MacroDefinition _pendingOracleMacro;
        private bool _isOracleEvaluation;
        private int _oracleMaxTicks = 1500;
        private bool _oracleIncludeDiagnostics;
        private List<object> _oracleTrace;
        private List<object> _oracleDiagnostics;
        private string _oracleResultId;
        private string _oracleRouteName;
        private Vector2? _oracleStartPose;
        private int _oracleGroundLayerValue;
        private int _oracleBlockingLayerValue;
        private bool _oracleMasksCaptured;
        private List<OracleBatchCandidate> _oracleBatchQueue;
        private List<object> _oracleBatchResults;
        private int _oracleBatchIndex;
        private string _oracleBatchCurrentName;
        private bool _oracleStopRequested;
        private WebStudioRequest _bootstrapRequest;
        private int _bootstrapPhase;
        private JObject _bootstrapBody;
        private readonly List<string> _bootstrapNotes = new List<string>();

        private sealed class OracleBatchCandidate
        {
            public string name;
            public string error;
            public List<MacroStep> steps;
        }

        private static FieldInfo _groundRaycastLayerField;
        private static readonly FieldInfo MovIsOnMainMenuField = AccessTools.Field(typeof(Movement), "isOnMainMenu");
        private static FieldInfo _blockingGroundRaycastLayerField;
        private static readonly FieldInfo MovCoyoteField = AccessTools.Field(typeof(Movement), "coyoteFrames");
        private static readonly FieldInfo MovWallCoyoteField = AccessTools.Field(typeof(Movement), "wallCoyoteFrames");
        private static readonly FieldInfo MovJumpBufferField = AccessTools.Field(typeof(Movement), "jumpBuffer");
        private static readonly FieldInfo MovJumpCutBufferField = AccessTools.Field(typeof(Movement), "jumpCutBuffer");
        private static readonly FieldInfo MovNormalColliderField = AccessTools.Field(typeof(Movement), "normalCollider");

        private GUIStyle _windowStyle, _panelStyle, _headerStyle, _labelStyle;
        private GUIStyle _buttonStyle, _selectedStyle, _runStyle, _dangerStyle;
        private GUIStyle _fieldStyle, _stepStyle, _selectedStepStyle, _tabStyle, _activeTabStyle;
        private Texture2D _solidNavy;

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
            _engine.AttemptEnded += SaveAttemptResult;
            Application.runInBackground = true;
            _webServer = new WebStudioServer();
            try
            {
                _webServer.Start(Path.Combine(Paths.PluginPath, "IGTAPTasWeb"));
                Logger.LogInfo("Web Studio listening at " + WebStudioServer.Url);
                if (_settings.autoOpenWebStudio && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IGTAP_ORACLE"))) Application.OpenURL(WebStudioServer.Url);
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
            PatchOptional(typeof(Movement), "FixedUpdate", nameof(BeforeMovementFixedUpdate), nameof(AfterMovementFixedUpdate));
            PatchOptional(typeof(courseScript), "startTracking", null, nameof(AfterCourseStart));
            PatchOptional(typeof(courseScript), "stopTracking", nameof(BeforeCourseFinish), nameof(AfterCourseFinish));
            PatchOptional(typeof(Movement), "onDeath", null, nameof(AfterPlayerDeath));
            PatchOptional(typeof(Saveloader), "autosave", nameof(BlockSharedSave), null);
            PatchOptional(typeof(Saveloader), "manualSave", nameof(BlockSharedSave), null);
            PatchOptional(typeof(Saveloader), "delayedManualSave", nameof(BlockSharedSave), null);
            Logger.LogInfo("TAS Studio " + PluginVersion + " loaded. Use " + WebStudioServer.Url + ". F8 is emergency stop.");
        }

        private static bool BlockSharedSave()
        {
            return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IGTAP_ORACLE"));
        }

        private static bool BeforeMovementFixedUpdate()
        {
            if (Instance == null) return true;
            if (Instance._pendingOracleMacro != null && !Instance._engine.IsRunning)
            {
                MacroDefinition macro = Instance._pendingOracleMacro;
                Instance._pendingOracleMacro = null;
                Instance.RestoreRouteStart(macro);
                Movement movement = FindWorldMovement();
                Vector2? synchronizationPosition = movement == null ? (Vector2?)null : movement.transform.position;
                Instance._oracleStartPose = movement == null ? (Vector2?)null : (Vector2?)movement.transform.position;
                Instance.BeginDeterministicPhysics(movement);
                Instance._engine.Run(macro, Instance._library, true, true, 0, synchronizationPosition, null);
                Instance._engine.SetCourseStartPositionOffset(0f);
                return true;
            }
            if (Instance._pendingReplay != null && !Instance._engine.IsRunning)
            {
                MacroDefinition macro = Instance._pendingReplay;
                int startDelayTicks = Instance._pendingReplayStartDelayTicks;
                float? firstJumpPositionOffsetX = Instance._pendingReplayFirstJumpPositionOffsetX;
                float courseStartPositionOffsetX = Instance._pendingReplayCourseStartPositionOffsetX;
                Instance._pendingReplay = null;
                Instance._pendingReplayStartDelayTicks = 0;
                Instance._pendingReplayFirstJumpPositionOffsetX = null;
                Instance._pendingReplayCourseStartPositionOffsetX = 0f;
                bool checkpointRestored = Instance.RestoreRouteStart(macro);
                Movement movement = FindWorldMovement();
                Vector2? synchronizationPosition = movement == null ? (Vector2?)null : movement.transform.position;
                Instance.BeginDeterministicPhysics(movement);
                Instance._engine.Run(macro, Instance._library, !checkpointRestored, true, startDelayTicks,
                    synchronizationPosition, firstJumpPositionOffsetX);
                Instance._engine.SetCourseStartPositionOffset(courseStartPositionOffsetX);
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
            if (Instance._isOracleEvaluation && Instance._engine.IsRunning && __instance != null)
                Instance.RecordOracleTick(__instance);
            Instance._recorder.FixedTick(__instance);
            if (Instance._recorder.IsRecording) return true;
            Instance._engine.FixedTick(__instance);
            // This method is the movement executor, not a device sampler. The prefix
            // supplies MoveAxis and input buffers; the original must run to consume
            // them and apply movement, jumping, walljumping, and dashing.
            return true;
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
                Instance._engine.ApplyCourseStartPositionOffset(movement);
                Instance._recorder.MarkCourseStart();
            }
            if (Instance != null && Instance._recorder.IsRecording) Log.LogInfo("Recording: authoritative course start detected");
        }

        private static void BeforeCourseFinish(courseScript __instance, bool savePositionData)
        {
            if (Instance == null || !savePositionData || __instance == null || CourseCurrentPathTimeField == null) return;
            object value = CourseCurrentPathTimeField.GetValue(__instance);
            Instance._pendingCourseTime = value is float ? (float)value : 0f;
        }

        private static void AfterCourseFinish(bool savePositionData)
        {
            if (Instance == null || !savePositionData) return;
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

        private static void AfterMovementFixedUpdate(Movement __instance)
        {
            if (Instance == null || __instance == null) return;
            if (Instance._isOracleEvaluation && Instance._engine.IsRunning)
            {
                if (Instance._engine.RunTicks > Instance._oracleMaxTicks)
                {
                    Instance._engine.Abort("Oracle tick budget exceeded");
                }
            }
        }

        private void RecordOracleTick(Movement movement)
        {
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            Vector2 tPos = movement.transform.position;
            Vector2 bPos = body != null ? body.position : tPos;
            Vector2 vel = movement.Velocity;
            Vector2 bVel = body != null ? body.linearVelocity : vel;

            long tick = _engine.RunTicks;
            if (_oracleTrace != null)
            {
                _oracleTrace.Add(new
                {
                    tick = tick,
                    x = tPos.x,
                    y = tPos.y,
                    bodyX = bPos.x,
                    bodyY = bPos.y,
                    vx = vel.x,
                    vy = vel.y,
                    linearVx = bVel.x,
                    linearVy = bVel.y,
                    grounded = movement.onGround,
                    onWall = movement.OnWall,
                    facingRight = movement.facingRight
                });
            }

            if (_oracleIncludeDiagnostics && _oracleDiagnostics != null)
            {
                Vector2 v1, v2, v3, v4, v5, v6, v7;
                Vector2 wallFacing;
                if (movement.facingRight)
                {
                    v1 = (Vector2)movement.transform.position + new Vector2(-15f, -18f);
                    v2 = (Vector2)movement.transform.position + new Vector2(-9f, -18f);
                    v3 = (Vector2)movement.transform.position + new Vector2(25f, -18f);
                    v4 = (Vector2)movement.transform.position + new Vector2(21f, -18f);
                    v5 = (Vector2)movement.transform.position + new Vector2(6f, -18f);
                    v6 = (Vector2)movement.transform.position + new Vector2(15f, 19f);
                    v7 = (Vector2)movement.transform.position + new Vector2(15f, -18f);
                    wallFacing = Vector2.right;
                }
                else
                {
                    v1 = (Vector2)movement.transform.position + new Vector2(15f, -18f);
                    v2 = (Vector2)movement.transform.position + new Vector2(9f, -18f);
                    v3 = (Vector2)movement.transform.position + new Vector2(-25f, -18f);
                    v4 = (Vector2)movement.transform.position + new Vector2(-21f, -18f);
                    v5 = (Vector2)movement.transform.position + new Vector2(-6f, -18f);
                    v6 = (Vector2)movement.transform.position + new Vector2(-15f, 19f);
                    v7 = (Vector2)movement.transform.position + new Vector2(-15f, -18f);
                    wallFacing = Vector2.left;
                }

                LayerMask groundLayer = GetMovementLayerMask(movement, "groundRaycastLayer");
                LayerMask blockingLayer = GetMovementLayerMask(movement, "blockingGroundRaycastLayer");
                if (!_oracleMasksCaptured)
                {
                    _oracleGroundLayerValue = groundLayer.value;
                    _oracleBlockingLayerValue = blockingLayer.value;
                    _oracleMasksCaptured = true;
                }

                float num = (movement.currentlyTouchedMovingPlatform != null || movement.dashActive) ? 3f : 0f;

                RaycastHit2D r1 = Physics2D.Raycast(v1, Vector2.down, 22f, groundLayer);
                RaycastHit2D r2 = Physics2D.Raycast(v2, Vector2.down, 22f, blockingLayer);
                RaycastHit2D r3 = Physics2D.Raycast(v3, Vector2.down, 22f, groundLayer);
                RaycastHit2D r4 = Physics2D.Raycast(v4, Vector2.down, 22f, blockingLayer);
                RaycastHit2D r5 = Physics2D.Raycast(v5, Vector2.down, 22f, groundLayer);
                RaycastHit2D r6 = Physics2D.Raycast(v1, Vector2.down, 5f, groundLayer);
                RaycastHit2D r7 = Physics2D.Raycast(v3, Vector2.down, 5f, groundLayer);
                RaycastHit2D r8 = Physics2D.Raycast(v1, Vector2.down, -25f, groundLayer);
                RaycastHit2D r9 = Physics2D.Raycast(v3, Vector2.down, -25f, groundLayer);

                RaycastHit2D r10 = Physics2D.Raycast(v7, wallFacing, 12f + num, groundLayer);
                RaycastHit2D r11 = Physics2D.Raycast(v6, wallFacing, 12f + num, groundLayer);
                RaycastHit2D r12 = Physics2D.Raycast(v7, wallFacing, 12f + num, blockingLayer);
                RaycastHit2D r13 = Physics2D.Raycast(v6, wallFacing, 12f + num, blockingLayer);

                // Movement.Update probes (scratch_movement.txt:767-801). These are a
                // separate set from the FixedUpdate r1-r13 probes and are the only
                // wall signals that can arm a wall jump while r10/r11 both miss and
                // OnWall is false (native Course 2 tick 154 of the 905 route).
                float updY = 10f;
                float dashBonus = 0f;
                object cutscene = GetMovementField(movement, "cutsceneMode");
                if (cutscene != null && cutscene.ToString() == "dash") { dashBonus = 2f; updY += 10f; }
                Vector2 u1Origin, u2Origin, u3Origin, u4Origin;
                if (movement.facingRight)
                {
                    u1Origin = (Vector2)movement.transform.position + new Vector2(24f, updY);
                    u2Origin = (Vector2)movement.transform.position + new Vector2(27f + dashBonus, updY);
                    u3Origin = (Vector2)movement.transform.position + new Vector2(15f, -18f);
                    u4Origin = (Vector2)movement.transform.position + new Vector2(15f, 19f);
                }
                else
                {
                    u1Origin = (Vector2)movement.transform.position + new Vector2(-24f, updY);
                    u2Origin = (Vector2)movement.transform.position + new Vector2(-27f - dashBonus, updY);
                    u3Origin = (Vector2)movement.transform.position + new Vector2(-15f, -18f);
                    u4Origin = (Vector2)movement.transform.position + new Vector2(-15f, 19f);
                }
                RaycastHit2D u1 = Physics2D.Raycast(u1Origin, wallFacing, 6f + dashBonus, groundLayer);
                RaycastHit2D u2 = Physics2D.Raycast(u2Origin, Vector2.down, 33f + updY, groundLayer);
                RaycastHit2D u3 = Physics2D.Raycast(u3Origin, wallFacing, 14f, groundLayer);
                RaycastHit2D u4 = Physics2D.Raycast(u4Origin, wallFacing, 14f, groundLayer);

                BoxCollider2D normCol = MovNormalColliderField != null ? (BoxCollider2D)MovNormalColliderField.GetValue(movement) : null;
                _oracleDiagnostics.Add(new
                {
                    tick = tick,
                    x = tPos.x,
                    y = tPos.y,
                    bodyX = bPos.x,
                    bodyY = bPos.y,
                    vx = vel.x,
                    vy = vel.y,
                    linearVx = bVel.x,
                    linearVy = bVel.y,
                    groundLayer = groundLayer.value,
                    blockingLayer = blockingLayer.value,
                    onGround = movement.onGround,
                    onWall = movement.OnWall,
                    coyoteFrames = MovCoyoteField != null ? (float)MovCoyoteField.GetValue(movement) : 0f,
                    wallCoyoteFrames = MovWallCoyoteField != null ? (float)MovWallCoyoteField.GetValue(movement) : 0f,
                    wallJumpsLeft = movement.wallJumpsLeft,
                    facingRight = movement.facingRight,
                    jumpBuffer = MovJumpBufferField != null && (bool)MovJumpBufferField.GetValue(movement),
                    jumpCutBuffer = MovJumpCutBufferField != null && (bool)MovJumpCutBufferField.GetValue(movement),
                    normalColliderSize = normCol != null ? $"{normCol.size.x},{normCol.size.y}" : "",
                    normalColliderOffset = normCol != null ? $"{normCol.offset.x},{normCol.offset.y}" : "",
                    r1 = SummarizeHit(r1),
                    r2 = SummarizeHit(r2),
                    r3 = SummarizeHit(r3),
                    r4 = SummarizeHit(r4),
                    r5 = SummarizeHit(r5),
                    r6 = SummarizeHit(r6),
                    r7 = SummarizeHit(r7),
                    r8 = SummarizeHit(r8),
                    r9 = SummarizeHit(r9),
                    r10 = SummarizeHit(r10),
                    r11 = SummarizeHit(r11),
                    r12 = SummarizeHit(r12),
                    r13 = SummarizeHit(r13),
                    u1 = SummarizeHit(u1),
                    u2 = SummarizeHit(u2),
                    u3 = SummarizeHit(u3),
                    u4 = SummarizeHit(u4),
                    wallProbe = (!movement.onGround && !movement.dashActive) ? SummarizeHit(u3) : SummarizeHit(u1)
                });
            }
        }

        private static object SummarizeHit(RaycastHit2D hit)
        {
            if (hit.collider == null) return null;
            return new
            {
                distance = hit.distance,
                point = new { x = hit.point.x, y = hit.point.y },
                collider = hit.collider.name,
                tag = hit.collider.tag,
                layer = hit.collider.gameObject.layer
            };
        }

        private static LayerMask GetMovementLayerMask(Movement movement, string fieldName)
        {
            try
            {
                if (fieldName == "groundRaycastLayer")
                {
                    if (_groundRaycastLayerField == null)
                        _groundRaycastLayerField = AccessTools.Field(typeof(Movement), "groundRaycastLayer");
                    if (_groundRaycastLayerField != null)
                        return (LayerMask)_groundRaycastLayerField.GetValue(movement);
                }
                else if (fieldName == "blockingGroundRaycastLayer")
                {
                    if (_blockingGroundRaycastLayerField == null)
                        _blockingGroundRaycastLayerField = AccessTools.Field(typeof(Movement), "blockingGroundRaycastLayer");
                    if (_blockingGroundRaycastLayerField != null)
                        return (LayerMask)_blockingGroundRaycastLayerField.GetValue(movement);
                }
            }
            catch { }
            return 0;
        }

        private bool StartOracleEvaluation(WebStudioRequest request, JObject body)
        {
            List<MacroStep> steps = null;
            string routeId = Text(body, "routeId");
            if (!string.IsNullOrEmpty(routeId))
            {
                MacroDefinition existing = _library.macros.Find(m => m.id == routeId);
                if (existing != null)
                {
                    steps = new List<MacroStep>();
                    foreach (MacroStep s in existing.steps) steps.Add(s.Copy());
                }
            }
            if (steps == null && body["actions"] != null)
            {
                steps = ConvertActionsToSteps(body["actions"]);
            }
            if (steps == null && body["frames"] != null)
            {
                steps = ConvertFramesToSteps(body["frames"]);
            }
            if (steps == null && body["steps"] != null)
            {
                steps = body["steps"].ToObject<List<MacroStep>>();
            }
            if (steps == null || steps.Count == 0)
            {
                request.StatusCode = 400;
                request.Response = JsonConvert.SerializeObject(new { ok = false, error = "No actions, frames, steps, or valid routeId found." });
                request.Completed.Set();
                return false;
            }

            if (_engine.IsRunning)
            {
                _engine.Abort("Aborted for new oracle evaluation");
            }

            _activeOracleRequest = request;
            request.AsyncHandling = true;
            _isOracleEvaluation = true;
            _oracleMaxTicks = body["maxTicks"] != null ? (int)body["maxTicks"] : 1500;
            _oracleIncludeDiagnostics = body["diagnostic"] != null && (bool)body["diagnostic"];
            _oracleTrace = new List<object>();
            _oracleDiagnostics = new List<object>();

            MacroDefinition evalMacro = new MacroDefinition
            {
                id = "oracle-eval-" + Guid.NewGuid().ToString("N"),
                name = "OracleEvaluation",
                steps = steps
            };

            EnsureGameUnpaused();
            _oracleResultId = Guid.NewGuid().ToString("N");
            _oracleRouteName = evalMacro.name;
            _oracleStartPose = null;
            _oracleMasksCaptured = false;
            _pendingOracleMacro = evalMacro;
            return true;
        }

        private static string ClassifyOracleStatus(bool success, string reason)
        {
            if (success) return "complete";
            if (string.IsNullOrEmpty(reason)) return "error";
            if (reason.IndexOf("tick budget", StringComparison.OrdinalIgnoreCase) >= 0) return "timeout";
            if (reason.IndexOf("death", StringComparison.OrdinalIgnoreCase) >= 0) return "death";
            return "error";
        }

        private string PersistOracleDiagnostics(string resultId, string routeName, long ticks, bool success)
        {
            if (_oracleDiagnostics == null || _oracleDiagnostics.Count == 0) return null;
            try
            {
                string path = Path.Combine(_resultsPath, DateTime.UtcNow.ToString("o").Replace(":", "-") + "-" + resultId + ".diagnostics.json");
                var dump = new
                {
                    schemaVersion = 1,
                    resultId = resultId,
                    routeName = routeName,
                    success = success,
                    completionTicks = ticks,
                    tickConvention = "native tick 0 is pre-input; stateTrace Tick n follows frame n-1",
                    coordinate = "Unity transform position (earlyTrace columns 2-3)",
                    masks = new { groundRaycastLayer = _oracleGroundLayerValue, blockingGroundRaycastLayer = _oracleBlockingLayerValue },
                    startPose = _oracleStartPose.HasValue ? (object)new { x = _oracleStartPose.Value.x, y = _oracleStartPose.Value.y } : null,
                    diagnostics = _oracleDiagnostics
                };
                WriteJsonAtomic(path, dump, false);
                return path;
            }
            catch (Exception ex) { if (Log != null) Log.LogError("Could not save oracle diagnostics: " + ex.Message); return null; }
        }

        private string PersistOracleResult(string resultId, string routeName, bool success, string reason, int action,
            long ticks, float time, Vector2 finalPos, string diagnosticsPath)
        {
            try
            {
                string path = Path.Combine(_resultsPath, DateTime.UtcNow.ToString("o").Replace(":", "-") + "-" + resultId + ".json");
                var result = new
                {
                    schemaVersion = 1,
                    id = resultId,
                    routeId = "oracle-eval-" + resultId,
                    routeName = routeName,
                    timestampUtc = DateTime.UtcNow.ToString("o"),
                    success = success,
                    completionTicks = ticks,
                    completionTime = time,
                    timingSource = "Oracle evaluation",
                    courseStartRunTick = _engine.CourseStartRunTick,
                    courseStartX = _oracleStartPose.HasValue ? _oracleStartPose.Value.x : 0f,
                    courseStartY = _oracleStartPose.HasValue ? _oracleStartPose.Value.y : 0f,
                    startStateFingerprint = _engine.StartStateFingerprint,
                    earlyTrace = _engine.EarlyTrace,
                    firstJumpPositionOffsetX = 0f,
                    courseStartPositionOffsetX = 0f,
                    deterministicPhysics = _deterministicBody != null,
                    failureAction = action,
                    failureReason = success ? null : reason,
                    finalPosition = new { x = finalPos.x, y = finalPos.y },
                    diagnosticsPath = diagnosticsPath
                };
                WriteJsonAtomic(path, result, false);
                return path;
            }
            catch (Exception ex) { if (Log != null) Log.LogError("Could not save oracle result: " + ex.Message); return null; }
        }

        private void CompleteOracleEvaluation(bool success, string reason, int action, long ticks)
        {
            Movement movement = FindWorldMovement();
            Vector2 finalPos = movement != null ? (Vector2)movement.transform.position : Vector2.zero;
            bool hasGameTime = success && _engine.AuthoritativeCompletionTime > 0f;
            float time = hasGameTime ? _engine.AuthoritativeCompletionTime : ticks * Time.fixedDeltaTime;
            string resultId = string.IsNullOrEmpty(_oracleResultId) ? Guid.NewGuid().ToString("N") : _oracleResultId;
            string routeName = string.IsNullOrEmpty(_oracleRouteName) ? "OracleEvaluation" : _oracleRouteName;
            string status = ClassifyOracleStatus(success, reason);
            long courseStartRunTick = _engine.CourseStartRunTick;
            string fingerprint = _engine.StartStateFingerprint;
            string earlyTrace = _engine.EarlyTrace;
            Vector2? startPose = _oracleStartPose;
            List<object> trace = _oracleTrace;
            List<object> diagnostics = _oracleIncludeDiagnostics ? _oracleDiagnostics : null;

            string diagnosticsPath = PersistOracleDiagnostics(resultId, routeName, ticks, success);
            string resultPath = PersistOracleResult(resultId, routeName, success, reason, action, ticks, time, finalPos, diagnosticsPath);

            _isOracleEvaluation = false;
            _oracleTrace = null;
            _oracleDiagnostics = null;
            _oracleStartPose = null;

            if (_oracleBatchQueue != null)
            {
                _oracleBatchResults.Add(new
                {
                    name = _oracleBatchCurrentName,
                    status = status,
                    completionTicks = success ? ticks : -1,
                    deathTick = success ? -1 : ticks,
                    completionTime = time,
                    error = success ? null : reason,
                    failureAction = action,
                    startPose = startPose.HasValue ? (object)new { x = startPose.Value.x, y = startPose.Value.y } : null,
                    finalPosition = new { x = finalPos.x, y = finalPos.y },
                    courseStartRunTick = courseStartRunTick,
                    startStateFingerprint = fingerprint,
                    trace = trace,
                    diagnostics = diagnostics,
                    resultPath = resultPath,
                    diagnosticsPath = diagnosticsPath
                });
                _oracleBatchIndex++;
                StartNextOracleBatchCandidate();
                return;
            }

            var resultObj = new
            {
                ok = true,
                success = success,
                status = status,
                completionTicks = ticks,
                completionTime = time,
                deathTick = success ? -1 : ticks,
                failureAction = action,
                failureReason = success ? null : reason,
                finalPosition = new { x = finalPos.x, y = finalPos.y },
                courseStartRunTick = courseStartRunTick,
                startPose = startPose.HasValue ? (object)new { x = startPose.Value.x, y = startPose.Value.y } : null,
                resultPath = resultPath,
                diagnosticsPath = diagnosticsPath,
                trace = trace,
                diagnostics = diagnostics
            };

            if (_activeOracleRequest != null)
            {
                _activeOracleRequest.Response = JsonConvert.SerializeObject(resultObj);
                _activeOracleRequest.StatusCode = 200;
                _activeOracleRequest.Completed.Set();
                _activeOracleRequest = null;
            }
        }

        private bool StartOracleBatch(WebStudioRequest request, JObject body)
        {
            JArray candidatesToken = body["candidates"] as JArray;
            if (candidatesToken == null || candidatesToken.Count == 0)
            {
                request.StatusCode = 400;
                request.Response = JsonConvert.SerializeObject(new { ok = false, error = "No candidates found." });
                request.Completed.Set();
                return false;
            }
            if (candidatesToken.Count > 64)
            {
                request.StatusCode = 400;
                request.Response = JsonConvert.SerializeObject(new { ok = false, error = "Candidate limit is 64." });
                request.Completed.Set();
                return false;
            }

            List<OracleBatchCandidate> queue = new List<OracleBatchCandidate>();
            for (int i = 0; i < candidatesToken.Count; i++)
            {
                JObject c = candidatesToken[i] as JObject;
                OracleBatchCandidate candidate = new OracleBatchCandidate();
                candidate.name = c != null && c["name"] != null ? (string)c["name"] : "candidate-" + i;
                bool hasActions = c != null && c["actions"] != null;
                bool hasFrames = c != null && c["frames"] != null;
                if (c == null) candidate.error = "Candidate entry is not an object.";
                else if (hasActions && hasFrames) candidate.error = "Candidate must use either actions or frames, not both.";
                else if (!hasActions && !hasFrames) candidate.error = "Candidate has neither actions nor frames.";
                else
                {
                    try
                    {
                        candidate.steps = hasActions ? ConvertActionsToSteps(c["actions"]) : ConvertFramesToSteps(c["frames"]);
                        if (candidate.steps == null || candidate.steps.Count == 0) candidate.error = "Candidate produced no input steps.";
                    }
                    catch (Exception ex) { candidate.error = ex.Message; }
                }
                queue.Add(candidate);
            }

            if (_engine.IsRunning) _engine.Abort("Aborted for new oracle batch");

            _activeOracleRequest = request;
            request.AsyncHandling = true;
            _oracleBatchQueue = queue;
            _oracleBatchResults = new List<object>();
            _oracleBatchIndex = 0;
            _oracleStopRequested = false;
            _oracleMaxTicks = body["maxTicks"] != null ? (int)body["maxTicks"] : 1500;
            _oracleIncludeDiagnostics = body["diagnostic"] != null && (bool)body["diagnostic"];
            StartNextOracleBatchCandidate();
            return true;
        }

        private void StartNextOracleBatchCandidate()
        {
            if (_oracleStopRequested || _oracleBatchQueue == null || _oracleBatchIndex >= _oracleBatchQueue.Count)
            {
                FinishOracleBatch(_oracleStopRequested ? "stopped" : "ok");
                return;
            }
            OracleBatchCandidate candidate = _oracleBatchQueue[_oracleBatchIndex];
            if (candidate.error != null)
            {
                _oracleBatchResults.Add(new
                {
                    name = candidate.name,
                    status = "error",
                    completionTicks = -1,
                    deathTick = -1,
                    completionTime = 0f,
                    error = candidate.error,
                    failureAction = -1,
                    startPose = (object)null,
                    finalPosition = (object)null,
                    courseStartRunTick = -1L,
                    startStateFingerprint = (string)null,
                    trace = (object)null,
                    diagnostics = (object)null,
                    resultPath = (string)null,
                    diagnosticsPath = (string)null
                });
                _oracleBatchIndex++;
                StartNextOracleBatchCandidate();
                return;
            }

            _isOracleEvaluation = true;
            _oracleBatchCurrentName = candidate.name;
            _oracleResultId = Guid.NewGuid().ToString("N");
            _oracleRouteName = candidate.name;
            _oracleStartPose = null;
            _oracleMasksCaptured = false;
            _oracleTrace = new List<object>();
            _oracleDiagnostics = new List<object>();

            MacroDefinition evalMacro = new MacroDefinition
            {
                id = "oracle-batch-" + Guid.NewGuid().ToString("N"),
                name = candidate.name,
                steps = candidate.steps
            };

            EnsureGameUnpaused();
            _pendingOracleMacro = evalMacro;
        }

        private void FinishOracleBatch(string status)
        {
            if (_activeOracleRequest != null)
            {
                _activeOracleRequest.Response = JsonConvert.SerializeObject(new
                {
                    ok = true,
                    status = status,
                    engine = _engine.Status,
                    completed = _oracleBatchResults != null ? _oracleBatchResults.Count : 0,
                    total = _oracleBatchQueue != null ? _oracleBatchQueue.Count : 0,
                    results = _oracleBatchResults
                });
                _activeOracleRequest.StatusCode = 200;
                _activeOracleRequest.Completed.Set();
                _activeOracleRequest = null;
            }
            _isOracleEvaluation = false;
            _oracleTrace = null;
            _oracleDiagnostics = null;
            _oracleStartPose = null;
            _oracleBatchQueue = null;
            _oracleBatchResults = null;
            _oracleBatchIndex = 0;
            _oracleBatchCurrentName = null;
            _oracleStopRequested = false;
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

        private static void SetMovementField(Movement movement, string name, object value)
        {
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

        private static void ApplyRestartGeometry(Movement movement)
        {
            Vector2 reset = movement.courseResetPoint != Vector2.zero ? movement.courseResetPoint : movement.respawnPoint;
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
                        notes = _bootstrapNotes,
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

        private static List<MacroStep> ConvertActionsToSteps(JToken actionsToken)
        {
            JArray actions = actionsToken as JArray;
            if (actions == null || actions.Count == 0) return null;
            List<MacroStep> steps = new List<MacroStep>();
            foreach (JToken token in actions)
            {
                JObject action = token as JObject;
                if (action == null) continue;
                int durationTicks = Math.Max(1, (int?)action["durationTicks"] ?? 1);
                float moveX = Mathf.Clamp((float?)action["rawMoveX"] ?? 0f, -1f, 1f);
                steps.Add(new MacroStep
                {
                    label = (string)action["label"] ?? "Oracle input",
                    type = MacroStepType.RawInput,
                    direction = moveX > 0f ? 1 : moveX < 0f ? -1 : 0,
                    durationTicks = durationTicks,
                    durationMs = durationTicks * 20,
                    timeoutTicks = Math.Max(durationTicks, 60),
                    timeoutMs = Math.Max(durationTicks, 60) * 20,
                    endCondition = ActionEndCondition.FixedTicks,
                    rawMoveX = moveX,
                    rawMoveY = Mathf.Clamp((float?)action["rawMoveY"] ?? 0f, -1f, 1f),
                    rawJumpPressed = (bool?)action["rawJumpPressed"] ?? false,
                    rawJumpHeld = (bool?)action["rawJumpHeld"] ?? false,
                    rawJumpReleased = (bool?)action["rawJumpReleased"] ?? false,
                    rawDashPressed = (bool?)action["rawDashPressed"] ?? false,
                    rawDashHeld = (bool?)action["rawDashHeld"] ?? false
                });
            }
            return steps;
        }

        private static List<MacroStep> ConvertFramesToSteps(JToken framesToken)
        {
            JArray frames = framesToken as JArray;
            if (frames == null || frames.Count == 0) return null;
            List<MacroStep> steps = new List<MacroStep>();
            for (int i = 0; i < frames.Count; i++)
            {
                JObject f = frames[i] as JObject;
                if (f == null) continue;
                float moveX = Mathf.Clamp((float?)(f["X"] ?? f["x"] ?? f["moveX"] ?? 0f) ?? 0f, -1f, 1f);
                bool press = (bool?)(f["Press"] ?? f["press"] ?? f["jumpPressed"] ?? false) ?? false;
                bool held = (bool?)(f["Held"] ?? f["held"] ?? f["jumpHeld"] ?? false) ?? false;
                bool release = (bool?)(f["Release"] ?? f["release"] ?? f["jumpReleased"] ?? false) ?? false;
                bool dash = (bool?)(f["DashPress"] ?? f["dashPress"] ?? f["dashPressed"] ?? false) ?? false;

                steps.Add(new MacroStep
                {
                    label = "Frame " + i,
                    type = MacroStepType.RawInput,
                    direction = moveX > 0f ? 1 : moveX < 0f ? -1 : 0,
                    durationTicks = 1,
                    durationMs = 20,
                    timeoutTicks = 60,
                    timeoutMs = 1200,
                    endCondition = ActionEndCondition.FixedTicks,
                    rawMoveX = moveX,
                    rawJumpPressed = press,
                    rawJumpHeld = held,
                    rawJumpReleased = release,
                    rawDashPressed = dash
                });
            }
            return steps;
        }

        private void Update()
        {
            if (_webServer != null) _webServer.Pump(HandleWebRequest);
            PumpBootstrap();
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f8Key.wasPressedThisFrame)
            {
                if (_recorder.IsRecording) StopRecording("Emergency stop");
                if (_engine.IsRunning) _engine.Abort("Emergency stop");
                return;
            }

            if (_bindingMacro != null)
            {
                CaptureHotkey(keyboard);
                return;
            }
            if (_showEditor) return;

            foreach (MacroDefinition macro in _library.macros)
            {
                if (HotkeyPressed(keyboard, macro))
                {
                    BeginDeterministicPhysics(FindWorldMovement());
                    _engine.Run(macro, _library);
                    break;
                }
            }
        }

        private void CaptureHotkey(Keyboard keyboard)
        {
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                _bindingMacro = null;
                return;
            }
            foreach (KeyControl key in keyboard.allKeys)
            {
                if (!key.wasPressedThisFrame || IsModifier(key.keyCode)) continue;
                _bindingMacro.hotkey = key.keyCode.ToString();
                _bindingMacro.hotkeyCtrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
                _bindingMacro.hotkeyShift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                _bindingMacro.hotkeyAlt = keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed;
                _bindingMacro = null;
                SaveLibrary();
                return;
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

        private static bool IsModifier(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift
                || key == Key.LeftAlt || key == Key.RightAlt;
        }

        private void OnGUI()
        {
            // Website-only UI. Intentionally draw nothing in the game.
        }

        private void DrawWindow(int id)
        {
            GUI.DrawTexture(new Rect(0f, 0f, _window.width, _window.height), _solidNavy, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(0f, 5f, _window.width, 25f), "IGTAP TAS STUDIO", _headerStyle);
            GUILayout.Space(13f);
            GUILayout.BeginHorizontal();
            DrawLibraryPanel();
            GUILayout.Space(10f);
            DrawTimelinePanel();
            GUILayout.Space(10f);
            DrawInspectorPanel();
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(_engine.Status, _labelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("SAVE", _buttonStyle, GUILayout.Width(90f), GUILayout.Height(32f))) SaveLibrary();
            if (GUILayout.Button("CLOSE", _buttonStyle, GUILayout.Width(90f), GUILayout.Height(32f))) { _showEditor = false; SaveSettings(); }
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 34f));
        }

        private void DrawLibraryPanel()
        {
            GUILayout.BeginVertical(_panelStyle, GUILayout.Width(220f), GUILayout.ExpandHeight(true));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("ROUTES", IsRouteView ? _activeTabStyle : _tabStyle)) SetView("routes");
            if (GUILayout.Button("BUNDLES", _editingBundles ? _activeTabStyle : _tabStyle)) SetView("bundles");
            if (GUILayout.Button("RECORD", _viewingRecordings ? _activeTabStyle : _tabStyle)) SetView("recordings");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("RESULTS", _viewingResults ? _activeTabStyle : _tabStyle)) SetView("results");
            if (GUILayout.Button("CHECKPOINTS", _viewingCheckpoints ? _activeTabStyle : _tabStyle)) SetView("checkpoints");
            if (GUILayout.Button("SETTINGS", _viewingSettings ? _activeTabStyle : _tabStyle)) SetView("settings");
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
            _libraryScroll = GUILayout.BeginScrollView(_libraryScroll);
            if (_viewingRecordings) DrawRecordingList(); else if (_editingBundles) DrawBundleList(); else DrawMacroList();
            GUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            if (!_viewingRecordings && !_viewingResults && GUILayout.Button(_editingBundles ? "+ NEW BUNDLE" : "+ NEW ROUTE", _runStyle, GUILayout.Height(36f))) AddDefinition();
            GUILayout.EndVertical();
        }

        private void DrawRecordingList()
        {
            for (int i = 0; i < _recordings.Count; i++)
            {
                RecordingDefinition recording = _recordings[i];
                string text = recording.name + "\n" + recording.samples.Count + " physics frames • " + recording.semanticSteps.Count + " actions";
                if (GUILayout.Button(text, i == _selectedRecordingIndex ? _selectedStyle : _buttonStyle, GUILayout.Height(52f))) _selectedRecordingIndex = i;
            }
        }

        private void DrawMacroList()
        {
            for (int i = 0; i < _library.macros.Count; i++)
            {
                MacroDefinition macro = _library.macros[i];
                if (GUILayout.Button(macro.name + "\n" + HotkeyText(macro), i == _library.selectedIndex ? _selectedStyle : _buttonStyle, GUILayout.Height(52f)))
                { _library.selectedIndex = i; _selectedStep = -1; }
            }
        }

        private void DrawBundleList()
        {
            for (int i = 0; i < _library.bundles.Count; i++)
            {
                if (GUILayout.Button(_library.bundles[i].name, i == _library.selectedBundleIndex ? _selectedStyle : _buttonStyle, GUILayout.Height(42f)))
                { _library.selectedBundleIndex = i; _selectedStep = -1; }
            }
        }

        private void DrawTimelinePanel()
        {
            GUILayout.BeginVertical(_panelStyle, GUILayout.Width(430f), GUILayout.ExpandHeight(true));
            if (_viewingResults)
            {
                DrawResultsView();
                GUILayout.EndVertical();
                return;
            }
            if (_viewingCheckpoints)
            {
                DrawCheckpointsView();
                GUILayout.EndVertical();
                return;
            }
            if (_viewingSettings)
            {
                DrawSettingsView();
                GUILayout.EndVertical();
                return;
            }
            GUILayout.Label(_viewingRecordings ? "RECORDING PREVIEW" : _editingBundles ? "BUNDLE ACTIONS" : "ROUTE TIMELINE", _headerStyle);
            if (_viewingRecordings)
            {
                DrawRecordingPreview();
                GUILayout.EndVertical();
                return;
            }
            if (!_editingBundles && SelectedMacro != null)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("▶ RUN", _runStyle, GUILayout.Height(38f))) { BeginDeterministicPhysics(FindWorldMovement()); _engine.Run(SelectedMacro, _library); _showEditor = false; }
                if (GUILayout.Button("↻ REPLAY", _runStyle, GUILayout.Height(38f))) ReplaySelected();
                if (GUILayout.Button("DUPLICATE", _buttonStyle, GUILayout.Height(38f))) DuplicateMacro();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (!_recorder.IsRecording && GUILayout.Button("● RECORD RUN", _dangerStyle, GUILayout.Height(34f))) StartRecording();
                if (_recorder.IsRecording && GUILayout.Button("■ STOP & CONVERT", _dangerStyle, GUILayout.Height(34f))) StopRecording("Manual stop", true);
                GUILayout.EndHorizontal();
                DrawVisualTimeline();
            }

            List<MacroStep> steps = CurrentSteps;
            _timelineScroll = GUILayout.BeginScrollView(_timelineScroll);
            if (steps != null)
            {
                for (int i = 0; i < steps.Count; i++) DrawTimelineRow(steps, i);
            }
            GUILayout.EndScrollView();
            GUILayout.Space(5f);
            GUILayout.Label("ADD ACTION", _headerStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("MOVE", _buttonStyle)) AddStep(MacroStepType.Move);
            if (GUILayout.Button("JUMP", _buttonStyle)) AddStep(MacroStepType.Jump);
            if (GUILayout.Button("WALLJUMP", _buttonStyle)) AddStep(MacroStepType.WallJump);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("DASH", _buttonStyle)) AddStep(MacroStepType.Dash);
            if (GUILayout.Button("WAIT", _buttonStyle)) AddStep(MacroStepType.Wait);
            if (_library.bundles.Count > 0 && GUILayout.Button("BUNDLE", _buttonStyle)) AddStep(MacroStepType.Bundle);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void DrawRecordingPreview()
        {
            RecordingDefinition recording = SelectedRecording;
            if (recording == null) { GUILayout.Label("No recordings yet. Select a route and choose RECORD RUN.", _labelStyle); return; }
            GUILayout.Label(recording.name, _headerStyle);
            GUILayout.Label(recording.samples.Count + " physics frames • " + (recording.samples.Count * recording.fixedDeltaMs / 1000f).ToString("0.000") + " s\nStopped: " + recording.stopReason, _labelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("CREATE ROUTE", _runStyle, GUILayout.Height(36f))) CreateRouteFromRecording(recording);
            if (GUILayout.Button("SHOW GHOST", _buttonStyle, GUILayout.Height(36f))) ShowRecordingGhost(recording);
            if (GUILayout.Button("DELETE", _dangerStyle, GUILayout.Height(36f))) DeleteRecording(recording);
            GUILayout.EndHorizontal();
            _timelineScroll = GUILayout.BeginScrollView(_timelineScroll);
            for (int i = 0; i < recording.semanticSteps.Count; i++)
                GUILayout.Label((i + 1) + "  " + recording.semanticSteps[i].label + "\n    " + Summary(recording.semanticSteps[i]), _stepStyle, GUILayout.Height(52f));
            GUILayout.EndScrollView();
        }

        private void DrawResultsView()
        {
            GUILayout.Label("ATTEMPT RESULTS", _headerStyle);
            MacroDefinition route = SelectedMacro;
            if (route == null) { GUILayout.Label("Select or create a route.", _labelStyle); return; }
            _timelineScroll = GUILayout.BeginScrollView(_timelineScroll);
            int shown = 0;
            foreach (AttemptResult result in _results)
            {
                if (result.routeId != route.id) continue;
                string outcome = result.success ? "SUCCESS" : "FAILED";
                string detail = result.success ? result.completionTime.ToString("0.000") + " s" : result.failureReason;
                GUILayout.Label(outcome + "  •  " + result.completionTicks + " physics frames\n" + detail, result.success ? _selectedStyle : _stepStyle, GUILayout.Height(48f));
                shown++;
            }
            if (shown == 0) GUILayout.Label("No attempts recorded for this route.", _labelStyle);
            GUILayout.EndScrollView();
        }

        private void DrawCheckpointsView()
        {
            GUILayout.Label("SECTION CHECKPOINTS", _headerStyle);
            MacroDefinition route = SelectedMacro;
            if (GUILayout.Button("CAPTURE CURRENT STATE", _runStyle, GUILayout.Height(38f))) CaptureCheckpoint();
            _timelineScroll = GUILayout.BeginScrollView(_timelineScroll);
            for (int checkpointIndex = 0; checkpointIndex < _checkpoints.Count; checkpointIndex++)
            {
                SectionCheckpoint checkpoint = _checkpoints[checkpointIndex];
                bool assigned = route != null && route.checkpointId == checkpoint.id;
                GUILayout.BeginVertical(assigned ? _selectedStyle : _stepStyle);
                GUILayout.Label(checkpoint.name + (assigned ? "  •  ROUTE START" : ""), _labelStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("RESTORE", _buttonStyle)) RestoreCheckpoint(checkpoint);
                if (route != null && GUILayout.Button(assigned ? "UNASSIGN" : "USE AS START", _buttonStyle))
                { route.checkpointId = assigned ? "" : checkpoint.id; SaveLibrary(); }
                if (GUILayout.Button("DELETE", _dangerStyle)) { DeleteCheckpoint(checkpoint); break; }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.Space(4f);
            }
            GUILayout.EndScrollView();
        }

        private void DrawSettingsView()
        {
            GUILayout.Label("SETTINGS", _headerStyle);
            GUILayout.Label("EMERGENCY STOP\nF8\n\nPERSISTENCE\n" + Path.GetDirectoryName(_settingsPath) + "\n\nAll action timing uses deterministic Unity physics frames. Unsupported schemas fail forward into quarantined .rejected files.", _labelStyle);
            bool visible = _ghostRenderer != null && _ghostRenderer.Visible;
            if (GUILayout.Button(visible ? "HIDE GHOST PATHS" : "SHOW GHOST PATHS", _buttonStyle, GUILayout.Height(36f)))
            {
                if (_ghostRenderer != null) _ghostRenderer.Visible = !visible;
                _settings.ghostVisible = !visible;
                SaveSettings();
            }
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

        private void DeleteCheckpoint(SectionCheckpoint checkpoint)
        {
            _checkpoints.Remove(checkpoint);
            foreach (MacroDefinition route in _library.macros) if (route.checkpointId == checkpoint.id) route.checkpointId = "";
            string path = Path.Combine(_checkpointsPath, SafeId(checkpoint.id) + ".json");
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Logger.LogError(ex); }
            SaveLibrary();
        }

        private bool RestoreRouteStart(MacroDefinition route)
        {
            Movement movement = FindWorldMovement();
            if (movement != null) _engine.ClearInjectedInput(movement);
            SectionCheckpoint checkpoint = string.IsNullOrEmpty(route.checkpointId) ? null : _checkpoints.Find(c => c.id == route.checkpointId);
            if (movement != null && checkpoint != null)
            {
                CheckpointOperationResult restored = SectionCheckpointService.Restore(movement, checkpoint, MacroLibrary.CurrentSchemaVersion);
                if (restored.Success) return true;
                Logger.LogWarning(restored.Message + "; using quick restart.");
            }
            MethodInfo respawn = AccessTools.Method(typeof(Movement), "respawn");
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
                        ? movement.courseResetPoint
                        : movement.respawnPoint;
                    if (initialResetPoint != Vector2.zero)
                    {
                        _preservedCourseResetPoint = initialResetPoint;
                        _hasPreservedCourseResetPoint = true;
                    }
                }
                if (_hasPreservedCourseResetPoint) movement.courseResetPoint = _preservedCourseResetPoint;
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
                if (_hasPreservedCourseResetPoint) movement.courseResetPoint = _preservedCourseResetPoint;
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

        private void ShowRecordingGhost(RecordingDefinition recording)
        {
            if (_ghostRenderer == null) _ghostRenderer = GhostPathRenderer.Create();
            _ghostRenderer.SetReference(GhostTrace.FromSamples(recording.samples, SelectedMacro == null ? null : SelectedMacro.id, recording.id));
            _ghostRenderer.Visible = true;
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

        private void DrawTimelineRow(List<MacroStep> steps, int index)
        {
            MacroStep step = steps[index];
            GUILayout.BeginHorizontal();
            string text = (step.enabled ? "" : "[DISABLED] ") + (index + 1) + "   " + step.label + "\n      " + Summary(step);
            if (GUILayout.Button(text, index == _selectedStep ? _selectedStepStyle : _stepStyle, GUILayout.Height(58f))) _selectedStep = index;
            GUILayout.BeginVertical(GUILayout.Width(34f));
            if (GUILayout.Button("↑", _buttonStyle, GUILayout.Width(32f)) && index > 0) { Swap(steps, index, index - 1); _selectedStep = index - 1; }
            if (GUILayout.Button("↓", _buttonStyle, GUILayout.Width(32f)) && index < steps.Count - 1) { Swap(steps, index, index + 1); _selectedStep = index + 1; }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(3f);
        }

        private void DrawVisualTimeline()
        {
            TimelineEditorState editor = EnsureTimelineEditor();
            if (editor == null) return;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("UNDO", editor.CanUndo ? _buttonStyle : _tabStyle, GUILayout.Width(58f))) { editor.Undo(); CommitTimeline(); }
            if (GUILayout.Button("REDO", editor.CanRedo ? _buttonStyle : _tabStyle, GUILayout.Width(58f))) { editor.Redo(); CommitTimeline(); }
            if (GUILayout.Button("−", _buttonStyle, GUILayout.Width(30f))) editor.ZoomBy(0.8f, 200f);
            GUILayout.Label(editor.PixelsPerTick.ToString("0.0") + " px/tick", _labelStyle, GUILayout.Width(76f));
            if (GUILayout.Button("+", _buttonStyle, GUILayout.Width(30f))) editor.ZoomBy(1.25f, 200f);
            if (GUILayout.Button("←", _buttonStyle, GUILayout.Width(30f))) editor.PanByPixels(80f);
            if (GUILayout.Button("→", _buttonStyle, GUILayout.Width(30f))) editor.PanByPixels(-80f);
            GUILayout.EndHorizontal();

            Rect canvas = GUILayoutUtility.GetRect(410f, 96f, GUILayout.ExpandWidth(true));
            GUI.Box(canvas, GUIContent.none, _stepStyle);
            Event current = Event.current;
            List<TimelineAction> actions = editor.Document.actions;
            actions.Sort(TimelineDocument.CompareActions);
            for (int i = 0; i < actions.Count; i++)
            {
                TimelineAction action = actions[i];
                float x = canvas.x + editor.PixelAtTick(action.startTick);
                float width = Mathf.Max(28f, action.durationTicks * editor.PixelsPerTick);
                Rect block = new Rect(x, canvas.y + 9f + (i % 2) * 38f, width, 32f);
                if (block.xMax < canvas.x || block.x > canvas.xMax) continue;
                GUI.Box(block, action.step.label, action.id == editor.SelectedActionId ? _selectedStyle : _buttonStyle);
                if (current.type == EventType.MouseDown && current.button == 0 && block.Contains(current.mousePosition))
                {
                    editor.SelectedActionId = action.id;
                    _selectedStep = i;
                    _dragActionId = action.id;
                    _dragResize = current.mousePosition.x >= block.xMax - 8f;
                    _dragTickOffset = Mathf.RoundToInt(editor.TickAtPixel(current.mousePosition.x - canvas.x)) - action.startTick;
                    editor.BeginInteraction(_dragResize ? "Resize action" : "Move action");
                    current.Use();
                }
            }
            if (!string.IsNullOrEmpty(_dragActionId) && current.type == EventType.MouseDrag)
            {
                TimelineAction action = editor.Document.Find(_dragActionId);
                int tick = Mathf.RoundToInt(editor.TickAtPixel(current.mousePosition.x - canvas.x));
                if (_dragResize) editor.PreviewResize(_dragActionId, action.startTick, tick);
                else editor.PreviewMove(_dragActionId, tick - _dragTickOffset);
                current.Use();
            }
            if (!string.IsNullOrEmpty(_dragActionId) && current.type == EventType.MouseUp)
            {
                editor.CommitInteraction();
                _dragActionId = null;
                CommitTimeline();
                current.Use();
            }
        }

        private TimelineEditorState EnsureTimelineEditor()
        {
            MacroDefinition route = SelectedMacro;
            if (route == null) return null;
            if (_timelineEditor != null && _timelineRouteId == route.id) return _timelineEditor;
            if (route.timeline == null || route.timeline.actions == null || route.timeline.actions.Count == 0)
                route.timeline = TimelineDocument.FromSequentialSteps(route.steps, Time.fixedDeltaTime * 1000f);
            _timelineEditor = new TimelineEditorState(route.timeline);
            _timelineRouteId = route.id;
            return _timelineEditor;
        }

        private void CommitTimeline()
        {
            MacroDefinition route = SelectedMacro;
            if (route == null || _timelineEditor == null) return;
            route.timeline = _timelineEditor.Document.Copy();
            route.steps = route.timeline.ToSequentialSteps(Time.fixedDeltaTime * 1000f);
            SaveLibrary();
        }

        private TimelineAction SelectedTimelineAction()
        {
            TimelineEditorState editor = EnsureTimelineEditor();
            if (editor == null) return null;
            List<TimelineAction> ordered = new List<TimelineAction>(editor.Document.actions);
            ordered.Sort(TimelineDocument.CompareActions);
            return _selectedStep >= 0 && _selectedStep < ordered.Count ? ordered[_selectedStep] : null;
        }

        private void SplitSelectedTimelineAction()
        {
            TimelineAction action = SelectedTimelineAction();
            if (action == null || action.durationTicks < 2) return;
            _timelineEditor.Split(action.id, action.startTick + action.durationTicks / 2);
            CommitTimeline();
        }

        private void MergeSelectedTimelineAction()
        {
            TimelineAction action = SelectedTimelineAction();
            if (action == null) return;
            List<TimelineAction> ordered = new List<TimelineAction>(_timelineEditor.Document.actions);
            ordered.Sort(TimelineDocument.CompareActions);
            int index = ordered.FindIndex(a => a.id == action.id);
            if (index >= 0 && index + 1 < ordered.Count && TimelineEditorState.CanMerge(action, ordered[index + 1]))
            { _timelineEditor.Merge(action.id, ordered[index + 1].id); CommitTimeline(); }
        }

        private void DrawInspectorPanel()
        {
            GUILayout.BeginVertical(_panelStyle, GUILayout.Width(330f), GUILayout.ExpandHeight(true));
            GUILayout.Label("INSPECTOR", _headerStyle);
            if (_viewingResults || _viewingCheckpoints || _viewingSettings)
            {
                GUILayout.Label("Select a route to inspect it in the browser studio.", _labelStyle);
                GUILayout.EndVertical();
                return;
            }
            if (_viewingRecordings)
            {
                GUILayout.Label("Raw samples are preserved on disk. CREATE ROUTE copies the semantic actions, so the recording remains unchanged.", _labelStyle);
                GUILayout.Space(12f);
                GUILayout.Label("F8 always stops recording or replay, including while this window is open.", _labelStyle);
                GUILayout.EndVertical();
                return;
            }
            if (_editingBundles) DrawDefinitionName(SelectedBundle); else DrawMacroHeader();

            List<MacroStep> steps = CurrentSteps;
            if (steps == null || _selectedStep < 0 || _selectedStep >= steps.Count)
            {
                GUILayout.Space(20f);
                GUILayout.Label("Select an action in the timeline to edit it.", _labelStyle);
                GUILayout.FlexibleSpace();
                DrawDeleteDefinition();
                GUILayout.EndVertical();
                return;
            }

            MacroStep step = steps[_selectedStep];
            GUILayout.Space(10f);
            GUILayout.Label("ACTION NAME", _headerStyle);
            string label = GUILayout.TextField(step.label, _fieldStyle, GUILayout.Height(30f));
            if (label != step.label) { step.label = label; SaveLibrary(); }
            GUILayout.Space(10f);
            GUILayout.Label("ACTION TYPE", _headerStyle);
            DrawTypeButtons(step);

            if (step.type == MacroStepType.Bundle)
                DrawBundlePicker(step);
            else
                DrawActionFields(step);

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(step.enabled ? "DISABLE" : "ENABLE", _buttonStyle, GUILayout.Height(32f)))
            {
                TimelineAction action = _editingBundles ? null : SelectedTimelineAction();
                if (action != null) { _timelineEditor.SetEnabled(action.id, !action.enabled); CommitTimeline(); }
                else { step.enabled = !step.enabled; SaveLibrary(); }
            }
            if (GUILayout.Button("DUPLICATE", _buttonStyle, GUILayout.Height(32f)))
            {
                TimelineAction action = _editingBundles ? null : SelectedTimelineAction();
                if (action != null) { _timelineEditor.Duplicate(action.id); CommitTimeline(); }
                else { MacroStep copy = step.Copy(); steps.Insert(_selectedStep + 1, copy); _selectedStep++; SaveLibrary(); }
            }
            GUILayout.EndHorizontal();
            if (!_editingBundles)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("SPLIT HALF", _buttonStyle, GUILayout.Height(30f))) SplitSelectedTimelineAction();
                if (GUILayout.Button("MERGE NEXT", _buttonStyle, GUILayout.Height(30f))) MergeSelectedTimelineAction();
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button("DELETE ACTION", _dangerStyle, GUILayout.Height(36f)))
            {
                TimelineAction action = _editingBundles ? null : SelectedTimelineAction();
                if (action != null) { _timelineEditor.Delete(action.id); CommitTimeline(); }
                else { steps.RemoveAt(_selectedStep); SaveLibrary(); }
                _selectedStep = Mathf.Clamp(_selectedStep, 0, steps.Count - 1);
            }
            GUILayout.EndVertical();
        }

        private void DrawMacroHeader()
        {
            MacroDefinition macro = SelectedMacro;
            if (macro == null) return;
            string name = GUILayout.TextField(macro.name, _fieldStyle, GUILayout.Height(32f));
            if (name != macro.name) { macro.name = name; SaveLibrary(); }
            GUILayout.Space(8f);
            GUILayout.Label("HOTKEY", _headerStyle);
            string bindText = _bindingMacro == macro ? "PRESS A KEY…  (ESC CANCELS)" : HotkeyText(macro);
            if (GUILayout.Button(bindText, _bindingMacro == macro ? _selectedStyle : _buttonStyle, GUILayout.Height(38f))) _bindingMacro = macro;
            if (!string.IsNullOrEmpty(macro.hotkey) && GUILayout.Button("CLEAR HOTKEY", _buttonStyle))
            { macro.hotkey = ""; macro.hotkeyCtrl = macro.hotkeyShift = macro.hotkeyAlt = false; SaveLibrary(); }
        }

        private void DrawDefinitionName(BundleDefinition bundle)
        {
            if (bundle == null) return;
            string name = GUILayout.TextField(bundle.name, _fieldStyle, GUILayout.Height(32f));
            if (name != bundle.name) { RenameBundle(bundle, name); SaveLibrary(); }
        }

        private void DrawTypeButtons(MacroStep step)
        {
            MacroStepType[] types = new[] { MacroStepType.Move, MacroStepType.Jump, MacroStepType.WallJump, MacroStepType.Dash, MacroStepType.Wait, MacroStepType.Bundle };
            foreach (MacroStepType type in types)
                if (GUILayout.Button(TypeName(type), step.type == type ? _selectedStyle : _buttonStyle, GUILayout.Height(29f))) { step.type = type; SaveLibrary(); }
        }

        private void DrawActionFields(MacroStep step)
        {
            GUILayout.Space(10f);
            GUILayout.Label("END CONDITION", _headerStyle);
            if (GUILayout.Button(EndConditionText(step.endCondition), _buttonStyle, GUILayout.Height(32f)))
            { step.endCondition = NextEnum(step.endCondition); SaveLibrary(); }
            if (step.endCondition == ActionEndCondition.Duration || step.endCondition == ActionEndCondition.FixedTicks)
            {
                GUILayout.Label(step.type == MacroStepType.WallJump ? "JUMP HOLD (PHYSICS FRAMES)" : "DURATION (PHYSICS FRAMES)", _headerStyle);
                DrawIntField(value => { step.durationTicks = Mathf.Max(1, value); step.endCondition = ActionEndCondition.FixedTicks; }, step.durationTicks);
            }
            else
            {
                if (ConditionUsesValue(step.endCondition))
                {
                    GUILayout.Label("THRESHOLD", _headerStyle);
                    DrawFloatField(value => step.conditionValue = value, step.conditionValue);
                }
                GUILayout.Label("TIMEOUT (PHYSICS FRAMES)", _headerStyle);
                DrawIntField(value => step.timeoutTicks = value, step.timeoutTicks);
                if (GUILayout.Button("ON TIMEOUT: " + step.timeoutPolicy.ToString().ToUpperInvariant(), _buttonStyle, GUILayout.Height(30f)))
                { step.timeoutPolicy = NextEnum(step.timeoutPolicy); SaveLibrary(); }
            }
            if (step.type == MacroStepType.WallJump)
            {
                GUILayout.Label("WALL SEARCH TIMEOUT (PHYSICS FRAMES)", _headerStyle);
                DrawIntField(value => step.timeoutTicks = Mathf.Max(1, value), step.timeoutTicks);
            }
            GUILayout.Space(10f);
            GUILayout.Label(step.type == MacroStepType.WallJump ? "JUMP DIRECTION" : "DIRECTION", _headerStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("← LEFT", step.direction == -1 ? _selectedStyle : _buttonStyle, GUILayout.Height(36f))) { step.direction = -1; SaveLibrary(); }
            if (GUILayout.Button("• NONE", step.direction == 0 ? _selectedStyle : _buttonStyle, GUILayout.Height(36f))) { step.direction = 0; SaveLibrary(); }
            if (GUILayout.Button("RIGHT →", step.direction == 1 ? _selectedStyle : _buttonStyle, GUILayout.Height(36f))) { step.direction = 1; SaveLibrary(); }
            GUILayout.EndHorizontal();
            if (step.type == MacroStepType.WallJump)
                GUILayout.Label("Approach direction is automatic. The jump fires only when the game reports wall contact or coyote time.", _labelStyle);
            if (_editingBundles && GUILayout.Button("EXPOSE DURATION PARAMETER", _buttonStyle, GUILayout.Height(30f))) ExposeDurationParameter(step);
        }

        private void DrawBundlePicker(MacroStep step)
        {
            GUILayout.Space(10f);
            GUILayout.Label("REUSABLE BUNDLE", _headerStyle);
            foreach (BundleDefinition bundle in _library.bundles)
                if (GUILayout.Button(bundle.name, step.bundleName == bundle.name ? _selectedStyle : _buttonStyle, GUILayout.Height(34f)))
                { step.bundleId = bundle.id; step.bundleName = bundle.name; step.label = bundle.name; SaveLibrary(); }
            BundleDefinition selected = _library.bundles.Find(b => b.id == step.bundleId || b.name == step.bundleName);
            if (selected != null && selected.parameters != null)
            {
                foreach (BundleParameterDefinition parameter in selected.parameters)
                {
                    string value;
                    if (step.bundleOverrides == null) step.bundleOverrides = new Dictionary<string, string>();
                    if (!step.bundleOverrides.TryGetValue(parameter.name, out value)) value = parameter.defaultValue;
                    GUILayout.Label(parameter.name.ToUpperInvariant(), _headerStyle);
                    string changed = GUILayout.TextField(value ?? "", _fieldStyle, GUILayout.Height(28f));
                    if (changed != value) { step.bundleOverrides[parameter.name] = changed; SaveLibrary(); }
                }
            }
        }

        private void ExposeDurationParameter(MacroStep step)
        {
            BundleDefinition bundle = SelectedBundle;
            if (bundle == null) return;
            string name = "duration" + (_selectedStep + 1) + "Frames";
            if (bundle.parameters.Exists(p => p.name == name)) return;
            bundle.parameters.Add(new BundleParameterDefinition { name = name, type = BundleParameterType.Integer, defaultValue = step.durationTicks.ToString() });
            EnsureBundleTemplates(bundle);
            if (_selectedStep >= 0 && _selectedStep < bundle.actions.Count)
                bundle.actions[_selectedStep].bindings.Add(new BundleParameterBinding { target = "durationTicks", expression = "$" + name });
            bundle.version++;
            SaveLibrary();
        }

        private void EnsureBundleTemplates(BundleDefinition bundle)
        {
            if (bundle.actions == null) bundle.actions = new List<BundleActionTemplate>();
            if (bundle.actions.Count == bundle.steps.Count) return;
            bundle.actions.Clear();
            foreach (MacroStep item in bundle.steps)
            {
                BundleActionTemplate template = new BundleActionTemplate { step = item.Copy() };
                if (item.type == MacroStepType.Bundle)
                {
                    BundleDefinition nested = _library.bundles.Find(b => b.id == item.bundleId || b.name == item.bundleName);
                    template.nestedBundleId = nested == null ? item.bundleId : nested.id;
                }
                bundle.actions.Add(template);
            }
        }

        private void DrawIntField(Action<int> setter, int value)
        {
            string text = GUILayout.TextField(value.ToString(), _fieldStyle, GUILayout.Height(30f));
            int parsed;
            if (int.TryParse(text, out parsed) && parsed >= 0 && parsed != value) { setter(parsed); SaveLibrary(); }
        }

        private void DrawFloatField(Action<float> setter, float value)
        {
            string text = GUILayout.TextField(value.ToString("0.###"), _fieldStyle, GUILayout.Height(30f));
            float parsed;
            if (float.TryParse(text, out parsed) && !Mathf.Approximately(parsed, value)) { setter(parsed); SaveLibrary(); }
        }

        private static T NextEnum<T>(T value) where T : struct
        {
            Array values = Enum.GetValues(typeof(T));
            int index = Array.IndexOf(values, value);
            return (T)values.GetValue((index + 1) % values.Length);
        }

        private static bool ConditionUsesValue(ActionEndCondition condition)
        {
            return condition == ActionEndCondition.VelocityYAbove || condition == ActionEndCondition.VelocityYBelow
                || condition == ActionEndCondition.PositionXAbove || condition == ActionEndCondition.PositionXBelow
                || condition == ActionEndCondition.PositionYAbove || condition == ActionEndCondition.PositionYBelow;
        }

        private static string EndConditionText(ActionEndCondition condition)
        {
            return condition.ToString().ToUpperInvariant() + "  (CLICK TO CHANGE)";
        }

        private void DrawDeleteDefinition()
        {
            if (GUILayout.Button(_editingBundles ? "DELETE BUNDLE" : "DELETE ROUTE", _dangerStyle, GUILayout.Height(36f)))
            {
                if (_editingBundles && SelectedBundle != null)
                {
                    string name = SelectedBundle.name;
                    string bundleId = SelectedBundle.id;
                    _library.bundles.Remove(SelectedBundle);
                    foreach (MacroDefinition macro in _library.macros)
                        macro.steps.RemoveAll(s => s.type == MacroStepType.Bundle && (s.bundleId == bundleId || s.bundleName == name));
                    foreach (BundleDefinition parent in _library.bundles)
                        parent.steps.RemoveAll(s => s.type == MacroStepType.Bundle && (s.bundleId == bundleId || s.bundleName == name));
                }
                else if (SelectedMacro != null) _library.macros.Remove(SelectedMacro);
                ClampSelections(); _selectedStep = -1; SaveLibrary();
            }
        }

        private void AddDefinition()
        {
            if (_editingBundles) { _library.bundles.Add(new BundleDefinition()); _library.selectedBundleIndex = _library.bundles.Count - 1; }
            else { _library.macros.Add(new MacroDefinition()); _library.selectedIndex = _library.macros.Count - 1; }
            _selectedStep = -1; SaveLibrary();
        }

        private void AddStep(MacroStepType type)
        {
            List<MacroStep> steps = CurrentSteps;
            if (steps == null) return;
            MacroStep step = new MacroStep { type = type, label = TypeName(type) };
            if (type == MacroStepType.WallJump) { step.durationTicks = 20; step.timeoutTicks = 60; step.direction = 1; }
            if (type == MacroStepType.Bundle && _library.bundles.Count > 0) { step.bundleId = _library.bundles[0].id; step.bundleName = _library.bundles[0].name; step.label = step.bundleName; }
            steps.Add(step); _selectedStep = steps.Count - 1; SaveLibrary();
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
            _showEditor = false;
            Logger.LogInfo("Fixed-tick recording started: " + name);
        }

        private void StopRecording(string reason, bool showEditor = false)
        {
            RecordingDefinition recording = _recorder.Stop(reason);
            if (recording == null) return;
            _recordings.Insert(0, recording);
            _selectedRecordingIndex = 0;
            SaveRecording(recording);
            _viewingRecordings = true;
            _editingBundles = false;
            _showEditor = showEditor;
            Logger.LogInfo("Recording saved: " + recording.samples.Count + " physics frames, " + recording.semanticSteps.Count + " actions");
        }

        private void CreateRouteFromRecording(RecordingDefinition recording)
        {
            MacroDefinition macro = new MacroDefinition { name = recording.name };
            foreach (MacroStep step in ExactInputCompressor.Compress(recording.samples, recording.fixedDeltaMs))
                macro.steps.Add(step);
            _library.macros.Add(macro);
            _library.selectedIndex = _library.macros.Count - 1;
            _viewingRecordings = false;
            _editingBundles = false;
            _selectedStep = -1;
            SaveLibrary();
        }

        private void DeleteRecording(RecordingDefinition recording)
        {
            _recordings.Remove(recording);
            _selectedRecordingIndex = Mathf.Clamp(_selectedRecordingIndex, 0, Mathf.Max(0, _recordings.Count - 1));
            string path = Path.Combine(_recordingsPath, SafeId(recording.id) + ".json");
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Logger.LogError("Could not delete recording: " + ex.Message); }
        }

        private void ReplaySelected(int startDelayTicks = 0, float? firstJumpPositionOffsetX = null,
            float courseStartPositionOffsetX = 0f)
        {
            MacroDefinition macro = SelectedMacro;
            if (macro == null) return;
            EnsureGameUnpaused();
            // Web/UI commands arrive during rendered Update frames. Queue the reset and
            // arming until the prefix of Movement's next full fixed tick.
            _pendingReplay = macro;
            _pendingReplayStartDelayTicks = Mathf.Max(0, startDelayTicks);
            _pendingReplayFirstJumpPositionOffsetX = firstJumpPositionOffsetX;
            _pendingReplayCourseStartPositionOffsetX = courseStartPositionOffsetX;
            _showEditor = false;
        }

        private void SaveAttemptResult(bool success, string reason, int action, long ticks)
        {
            if (_isOracleEvaluation)
            {
                EndDeterministicPhysics();
                CompleteOracleEvaluation(success, reason, action, ticks);
                return;
            }
            bool hasGameTime = success && _engine.AuthoritativeCompletionTime > 0f;
            AttemptResult result = new AttemptResult {
                routeId = _engine.ActiveMacroId,
                routeName = _engine.ActiveMacroName,
                success = success,
                completionTicks = ticks,
                completionTime = hasGameTime ? _engine.AuthoritativeCompletionTime : ticks * Time.fixedDeltaTime,
                timingSource = hasGameTime ? "In-game course timer" : "Macro elapsed time",
                courseStartRunTick = _engine.CourseStartRunTick,
                courseStartUnityFrame = _engine.CourseStartUnityFrame,
                courseStartX = _engine.CourseStartPosition.x,
                courseStartY = _engine.CourseStartPosition.y,
                startStateFingerprint = _engine.StartStateFingerprint,
                earlyTrace = _engine.EarlyTrace,
                firstJumpPositionOffsetX = _engine.FirstJumpPositionOffsetX,
                courseStartPositionOffsetX = _engine.CourseStartPositionOffsetX,
                deterministicPhysics = _deterministicBody != null,
                failureAction = action,
                failureReason = success ? null : reason
            };
            _results.Insert(0, result);
            string path = Path.Combine(_resultsPath, result.timestampUtc.Replace(":", "-") + "-" + result.id + ".json");
            WriteJsonAtomic(path, result, false);
            EndDeterministicPhysics();
        }

        private void RenameBundle(BundleDefinition bundle, string newName)
        {
            string old = bundle.name; bundle.name = newName;
            foreach (MacroDefinition macro in _library.macros)
                foreach (MacroStep step in macro.steps)
                    if (step.type == MacroStepType.Bundle && step.bundleName == old) { step.bundleName = newName; step.label = newName; }
        }

        private List<MacroStep> CurrentSteps { get { return _viewingRecordings ? null : _editingBundles ? (SelectedBundle == null ? null : SelectedBundle.steps) : (SelectedMacro == null ? null : SelectedMacro.steps); } }
        private MacroDefinition SelectedMacro { get { ClampSelections(); return _library.macros.Count == 0 ? null : _library.macros[_library.selectedIndex]; } }
        private BundleDefinition SelectedBundle { get { ClampSelections(); return _library.bundles.Count == 0 ? null : _library.bundles[_library.selectedBundleIndex]; } }
        private RecordingDefinition SelectedRecording { get { return _recordings.Count == 0 ? null : _recordings[Mathf.Clamp(_selectedRecordingIndex, 0, _recordings.Count - 1)]; } }
        private bool IsRouteView { get { return !_editingBundles && !_viewingRecordings && !_viewingResults && !_viewingCheckpoints && !_viewingSettings; } }

        private void SetView(string view)
        {
            _editingBundles = view == "bundles";
            _viewingRecordings = view == "recordings";
            _viewingResults = view == "results";
            _viewingCheckpoints = view == "checkpoints";
            _viewingSettings = view == "settings";
            _settings.selectedView = view;
            if (view != "optimizer") _selectedStep = -1;
        }

        private void ClampSelections()
        {
            _library.selectedIndex = Mathf.Clamp(_library.selectedIndex, 0, Mathf.Max(0, _library.macros.Count - 1));
            _library.selectedBundleIndex = Mathf.Clamp(_library.selectedBundleIndex, 0, Mathf.Max(0, _library.bundles.Count - 1));
        }

        private static void Swap(List<MacroStep> steps, int a, int b) { MacroStep t = steps[a]; steps[a] = steps[b]; steps[b] = t; Instance.SaveLibrary(); }
        private static string TypeName(MacroStepType type) { return type == MacroStepType.WallJump ? "WALLJUMP" : type.ToString().ToUpperInvariant(); }
        private static string DirectionText(int d) { return d < 0 ? "left" : d > 0 ? "right" : "neutral"; }
        private static string Summary(MacroStep s)
        {
            string confidence = s.inferenceConfidence == InferenceConfidence.NotInferred ? "" : "  •  " + s.inferenceConfidence.ToString().ToUpperInvariant() + " CONFIDENCE";
            if (s.type == MacroStepType.Bundle) return "BUNDLE  •  " + (string.IsNullOrEmpty(s.bundleName) ? "not selected" : s.bundleName);
            if (s.type == MacroStepType.WallJump) return "WALLJUMP " + DirectionText(s.direction).ToUpperInvariant() + "  •  hold " + s.durationTicks + " frames  •  wall-aware" + confidence;
            return TypeName(s.type) + " " + DirectionText(s.direction).ToUpperInvariant() + "  •  " + s.durationTicks + " frames" + confidence;
        }
        private static string HotkeyText(MacroDefinition m)
        {
            if (string.IsNullOrEmpty(m.hotkey)) return "No hotkey";
            return (m.hotkeyCtrl ? "CTRL + " : "") + (m.hotkeyShift ? "SHIFT + " : "") + (m.hotkeyAlt ? "ALT + " : "") + m.hotkey.ToUpperInvariant();
        }

        private void LoadLibrary()
        {
            if (!File.Exists(_libraryPath))
            {
                _library = MacroLibrary.CreateDefault();
                SaveLibrary();
                return;
            }
            try
            {
                MacroLibrary loaded = JsonConvert.DeserializeObject<MacroLibrary>(File.ReadAllText(_libraryPath));
                ValidateLibrary(loaded);
                _library = loaded;
                if (NormalizeLibraryFrameTiming(_library)) SaveLibrary();
            }
            catch (Exception ex)
            {
                Logger.LogError("Routes file rejected; starting with the current schema: " + ex.Message);
                Quarantine(_libraryPath);
                _library = MacroLibrary.CreateDefault();
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
            _window = new Rect(_settings.windowX, _settings.windowY, _settings.windowWidth, _settings.windowHeight);
            SetView(string.IsNullOrEmpty(_settings.selectedView) ? "routes" : _settings.selectedView);
        }

        private void SaveSettings()
        {
            _settings.windowX = _window.x;
            _settings.windowY = _window.y;
            _settings.windowWidth = _window.width;
            _settings.windowHeight = _window.height;
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
                        AttemptResult result = JsonConvert.DeserializeObject<AttemptResult>(File.ReadAllText(path));
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
            if (library.macros == null || library.bundles == null)
                throw new InvalidDataException("Required route or bundle collection is missing.");
            foreach (MacroDefinition route in library.macros)
                if (route == null || string.IsNullOrEmpty(route.id) || route.steps == null || route.timeline == null || route.timeline.actions == null)
                    throw new InvalidDataException("A route is incomplete.");
            foreach (BundleDefinition bundle in library.bundles)
                if (bundle == null || string.IsNullOrEmpty(bundle.id) || bundle.steps == null || bundle.parameters == null || bundle.actions == null)
                    throw new InvalidDataException("A bundle is incomplete.");
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
            foreach (BundleDefinition bundle in library.bundles)
                foreach (MacroStep step in bundle.steps) changed |= NormalizeStepFrameTiming(step, tickMs);
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
            if (request.Method == "GET" && request.Path == "/api/oracle/status")
            {
                return new
                {
                    ok = true,
                    evaluating = _isOracleEvaluation,
                    runTicks = _engine.IsRunning ? _engine.RunTicks : 0,
                    status = _engine.Status,
                    batch = _oracleBatchQueue != null,
                    batchIndex = _oracleBatchIndex,
                    batchTotal = _oracleBatchQueue != null ? _oracleBatchQueue.Count : 0,
                    batchName = _oracleBatchCurrentName
                };
            }
            if (request.Path == "/api/oracle/stop")
            {
                if (_oracleBatchQueue != null) _oracleStopRequested = true;
                if (_isOracleEvaluation && _engine.IsRunning)
                {
                    _engine.Abort("Oracle evaluation stopped by request");
                }
                else if (_oracleBatchQueue != null)
                {
                    FinishOracleBatch("stopped");
                }
                return new { ok = true };
            }
            if (request.Path == "/api/oracle/evaluate")
            {
                if (_activeOracleRequest != null)
                {
                    request.StatusCode = 409;
                    return new { ok = false, error = "An oracle evaluation is already in progress." };
                }
                StartOracleEvaluation(request, body);
                return null;
            }
            if (request.Path == "/api/oracle/evaluate-batch")
            {
                if (_activeOracleRequest != null)
                {
                    request.StatusCode = 409;
                    return new { ok = false, error = "An oracle evaluation is already in progress." };
                }
                StartOracleBatch(request, body);
                return null;
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
            if (request.Path == "/api/simulation/import-plan")
            {
                return ImportAutonomousPlan(body);
            }
            if (request.Method == "GET" && request.Path == "/api/offline/report")
            {
                string report = string.IsNullOrEmpty(_offlineOutputPath) ? null : Path.Combine(_offlineOutputPath, "report.html");
                if (report == null || !File.Exists(report)) throw new InvalidOperationException("Offline report is not ready.");
                return new { ok = true, html = File.ReadAllText(report) };
            }
            if (request.Method != "POST") throw new InvalidOperationException("Unsupported request method.");

            if (request.Path == "/api/offline/start")
            {
                StartOfflineOptimization(body);
                return new { ok = true };
            }
            if (request.Path == "/api/offline/stop")
            {
                if (_offlineProcess != null && !_offlineProcess.HasExited)
                {
                    _offlineProcess.Kill();
                    _offlineError = "Offline search stopped by user.";
                }
                return new { ok = true };
            }
            if (request.Path == "/api/offline/import")
            {
                int rank = Math.Max(1, Number(body, "rank", 1));
                string path = string.IsNullOrEmpty(_offlineOutputPath) ? null : Path.Combine(_offlineOutputPath, "candidate-" + rank.ToString("00") + ".json");
                if (path == null || !File.Exists(path)) throw new InvalidOperationException("Offline candidate is not ready.");
                return ImportOfflineCandidate(JObject.Parse(File.ReadAllText(path)));
            }

            if (request.Path == "/api/routes/select")
            {
                string id = Text(body, "routeId");
                int index = _library.macros.FindIndex(r => r.id == id);
                if (index < 0) throw new InvalidOperationException("Route not found.");
                _library.selectedIndex = index;
                _timelineEditor = null;
                _timelineRouteId = null;
                SaveLibrary();
                return new { ok = true };
            }
            if (request.Path == "/api/routes/run")
            {
                if (SelectedMacro == null) throw new InvalidOperationException("Select a route first.");
                float? firstJumpPositionOffsetX = body["firstJumpPositionOffsetX"] == null
                    ? (float?)null : FloatNumber(body, "firstJumpPositionOffsetX", 0f);
                ReplaySelected(Number(body, "startDelayTicks", 0), firstJumpPositionOffsetX,
                    FloatNumber(body, "courseStartPositionOffsetX", 0f));
                return new { ok = true };
            }
            if (request.Path == "/api/routes/duplicate")
            {
                if (SelectedMacro == null) throw new InvalidOperationException("Select a route first.");
                DuplicateMacro();
                _timelineEditor = null;
                _timelineRouteId = null;
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
                _timelineEditor = null;
                _timelineRouteId = null;
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
                _pendingReplayFirstJumpPositionOffsetX = null;
                _pendingReplayCourseStartPositionOffsetX = 0f;
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
                _timelineEditor = null;
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
                if (body["enabled"] != null) action.enabled = (bool)body["enabled"];
                route.timeline = TimelineDocument.FromSequentialSteps(route.steps, Time.fixedDeltaTime * 1000f);
                _timelineEditor = null;
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

        private void StartOfflineOptimization(JObject body)
        {
            if (_offlineProcess != null && !_offlineProcess.HasExited)
                throw new InvalidOperationException("An offline search is already running.");
            if (_offlineProcess != null) { _offlineProcess.Dispose(); _offlineProcess = null; }
            MacroDefinition route = _library.macros.Find(item => item.id == Text(body, "routeId"));
            if (route == null) route = SelectedMacro;
            if (route == null) throw new InvalidOperationException("Select a route first.");

            Movement movement = FindWorldMovement();
            SimulationModelExport model = SimulationModelExtractor.Capture(movement, MacroLibrary.CurrentSchemaVersion);
            string simulationDirectory = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "simulation");
            string modelPath = SimulationModelExtractor.Save(model, simulationDirectory);
            string enginePath = Path.Combine(Paths.PluginPath, "IGTAPFastSim", "IGTAP.FastSim.dll");
            if (!File.Exists(enginePath)) throw new InvalidOperationException("Offline engine is not installed. Rebuild the plugin once.");

            _offlineOutputPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "offline", DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
            Directory.CreateDirectory(_offlineOutputPath);
            _offlineRouteId = route.id;
            _offlineRouteName = route.name;
            _offlineError = null;
            int radius = Mathf.Clamp(Number(body, "radius", 8), 0, 20);
            int budget = Mathf.Clamp(Number(body, "budget", 2000000), 1, 1000000000);
            int beam = Mathf.Clamp(Number(body, "beam", 1000), 1, 100000);
            int top = Mathf.Clamp(Number(body, "top", 20), 1, 1000);
            string recording = null;
            try
            {
                string[] recordings = Directory.GetFiles(_recordingsPath, "*.json");
                if (recordings.Length > 0)
                {
                    Array.Sort(recordings, (left, right) => File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
                    recording = recordings[0];
                }
            }
            catch (Exception ex) { Logger.LogWarning("Could not select an offline calibration recording: " + ex.Message); }

            string resultPath = null;
            try
            {
                string[] results = Directory.GetFiles(_resultsPath, "*.json");
                Array.Sort(results, (left, right) => File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
                foreach (string path in results)
                {
                    JObject result = JObject.Parse(File.ReadAllText(path));
                    if ((string)result["routeId"] == route.id && (bool?)result["success"] == true
                        && (bool?)result["deterministicPhysics"] == true && !string.IsNullOrWhiteSpace((string)result["earlyTrace"]))
                    { resultPath = path; break; }
                }
            }
            catch (Exception ex) { Logger.LogWarning("Could not select an authoritative result trace: " + ex.Message); }

            string arguments = QuoteProcessArgument(enginePath) + " suite " + QuoteProcessArgument(modelPath) + " "
                + QuoteProcessArgument(_libraryPath) + " " + QuoteProcessArgument(route.name)
                + " --out " + QuoteProcessArgument(_offlineOutputPath)
                + " --radius " + radius + " --max " + budget + " --beam " + beam + " --top " + top;
            if (!string.IsNullOrEmpty(recording)) arguments += " --recording " + QuoteProcessArgument(recording);
            if (!string.IsNullOrEmpty(resultPath)) arguments += " --result " + QuoteProcessArgument(resultPath);
            ProcessStartInfo start = new ProcessStartInfo("dotnet", arguments) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Paths.PluginPath
            };
            try { _offlineProcess = Process.Start(start); }
            catch (Exception ex) { _offlineError = ex.Message; throw new InvalidOperationException("Could not start offline engine: " + ex.Message); }
            if (_offlineProcess == null) throw new InvalidOperationException("Could not start offline engine.");
        }

        private object ImportOfflineCandidate(JObject body)
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
            _library.macros.Add(candidate);
            _library.selectedIndex = _library.macros.Count - 1;
            _timelineEditor = null;
            _timelineRouteId = null;
            SaveLibrary();
            return new { ok = true, routeId = candidate.id, name = candidate.name, actionCount = candidate.steps.Count };
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

        private object ImportAutonomousPlan(JObject body)
        {
            JArray actions = body["actions"] as JArray;
            if (actions == null || actions.Count == 0)
                throw new InvalidOperationException("The autonomous plan does not contain any actions.");

            MacroDefinition route = new MacroDefinition {
                id = Guid.NewGuid().ToString("N"),
                name = string.IsNullOrWhiteSpace(Text(body, "name")) ? "Autonomous route" : Text(body, "name"),
                hotkey = "",
                favorite = false
            };
            foreach (JToken token in actions)
            {
                JObject action = token as JObject;
                if (action == null) throw new InvalidOperationException("An autonomous action is malformed.");
                int durationTicks = Math.Max(1, (int?)action["durationTicks"] ?? 1);
                float moveX = Mathf.Clamp((float?)action["rawMoveX"] ?? 0f, -1f, 1f);
                route.steps.Add(new MacroStep {
                    label = (string)action["label"] ?? "Autonomous input",
                    type = MacroStepType.RawInput,
                    enabled = true,
                    durationTicks = durationTicks,
                    durationMs = Mathf.RoundToInt(durationTicks * Time.fixedDeltaTime * 1000f),
                    endCondition = ActionEndCondition.FixedTicks,
                    direction = moveX > 0f ? 1 : moveX < 0f ? -1 : 0,
                    rawMoveX = moveX,
                    rawJumpPressed = (bool?)action["rawJumpPressed"] ?? false,
                    rawJumpHeld = (bool?)action["rawJumpHeld"] ?? false,
                    rawJumpReleased = (bool?)action["rawJumpReleased"] ?? false,
                    rawDashPressed = (bool?)action["rawDashPressed"] ?? false,
                    rawDashHeld = (bool?)action["rawDashHeld"] ?? false
                });
            }
            route.timeline = TimelineDocument.FromSequentialSteps(route.steps, Time.fixedDeltaTime * 1000f);
            _library.macros.Add(route);
            _library.selectedIndex = _library.macros.Count - 1;
            _timelineEditor = null;
            _timelineRouteId = null;
            SaveLibrary();
            return new { ok = true, routeId = route.id, name = route.name, actionCount = route.steps.Count };
        }

        private object BuildOfflineWebState()
        {
            bool running = _offlineProcess != null && !_offlineProcess.HasExited;
            int? exitCode = _offlineProcess != null && !running ? (int?)_offlineProcess.ExitCode : null;
            string summaryPath = string.IsNullOrEmpty(_offlineOutputPath) ? null : Path.Combine(_offlineOutputPath, "summary.json");
            JObject summary = null;
            if (!running && summaryPath != null && File.Exists(summaryPath))
            {
                try { summary = JObject.Parse(File.ReadAllText(summaryPath)); }
                catch (Exception ex) { _offlineError = "Could not read offline results: " + ex.Message; }
            }
            string status = running ? "Running" : summary != null ? "Completed"
                : exitCode.HasValue && exitCode.Value != 0 ? "Failed" : "Idle";
            return new { status = status, running = running, exitCode = exitCode,
                routeId = _offlineRouteId, routeName = _offlineRouteName, error = _offlineError,
                hasReport = summary != null, summary = summary };
        }

        private static string QuoteProcessArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
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
                    enabled = action.enabled, endCondition = action.endCondition.ToString()
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

            return new { connected = true, version = PluginVersion, engineStatus = _engine.Status,
                paused = IsGamePaused(),
                running = _engine.IsRunning,
                recording = _recorder.IsRecording, selectedRouteId = selected == null ? "" : selected.id,
                routes = routes, recordings = recordings, checkpoints = checkpoints, results = results,
                offline = BuildOfflineWebState() };
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

        private void EnsureStyles()
        {
            if (_windowStyle != null) return;
            Color navy = new Color(0.025f, 0.075f, 0.16f, 1f), panel = new Color(0.04f, 0.13f, 0.27f, 1f);
            Color blue = new Color(0.05f, 0.36f, 0.78f, 1f), bright = new Color(0.08f, 0.55f, 1f, 1f);
            Color field = new Color(0.018f, 0.06f, 0.13f, 1f), danger = new Color(0.68f, 0.10f, 0.16f, 1f);
            _solidNavy = Texture(navy);
            _windowStyle = Style(GUI.skin.window, navy, Color.white, 16, TextAnchor.UpperCenter);
            _windowStyle.padding = new RectOffset(14, 14, 34, 12);
            _panelStyle = Style(GUI.skin.box, panel, Color.white, 13, TextAnchor.MiddleLeft);
            _panelStyle.padding = new RectOffset(10, 10, 10, 10);
            _headerStyle = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(0.45f, 0.78f, 1f) }, fontSize = 13, fontStyle = FontStyle.Bold };
            _labelStyle = new GUIStyle(GUI.skin.label) { normal = { textColor = Color.white }, fontSize = 13, wordWrap = true };
            _buttonStyle = Style(GUI.skin.button, new Color(0.07f, 0.20f, 0.39f, 1f), Color.white, 12, TextAnchor.MiddleCenter);
            _selectedStyle = Style(GUI.skin.button, blue, Color.white, 12, TextAnchor.MiddleCenter);
            _runStyle = Style(GUI.skin.button, bright, Color.white, 13, TextAnchor.MiddleCenter);
            _dangerStyle = Style(GUI.skin.button, danger, Color.white, 12, TextAnchor.MiddleCenter);
            _fieldStyle = Style(GUI.skin.textField, field, Color.white, 13, TextAnchor.MiddleLeft);
            _fieldStyle.padding = new RectOffset(8, 8, 4, 4);
            _stepStyle = Style(GUI.skin.button, new Color(0.035f, 0.105f, 0.22f, 1f), Color.white, 13, TextAnchor.MiddleLeft);
            _stepStyle.padding = new RectOffset(12, 8, 5, 5);
            _selectedStepStyle = Style(_stepStyle, blue, Color.white, 13, TextAnchor.MiddleLeft);
            _tabStyle = Style(GUI.skin.button, new Color(0.025f, 0.09f, 0.19f, 1f), Color.white, 12, TextAnchor.MiddleCenter);
            _activeTabStyle = Style(GUI.skin.button, bright, Color.white, 12, TextAnchor.MiddleCenter);
        }

        private static GUIStyle Style(GUIStyle source, Color background, Color text, int size, TextAnchor alignment)
        {
            GUIStyle style = new GUIStyle(source) { fontSize = size, alignment = alignment };
            Texture2D texture = Texture(background);
            style.border = new RectOffset(0, 0, 0, 0);
            style.normal.background = texture; style.hover.background = texture; style.active.background = texture;
            style.focused.background = texture; style.normal.textColor = text; style.hover.textColor = text;
            style.active.textColor = text; style.focused.textColor = text;
            return style;
        }

        private static Texture2D Texture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixel(0, 0, color);
            texture.Apply(false, true);
            return texture;
        }

        private void OnDestroy()
        {
            if (_recorder.IsRecording) StopRecording("Game closed");
            EndDeterministicPhysics();
            SaveLibrary();
            SaveSettings();
            if (_webServer != null) _webServer.Dispose();
            if (_offlineProcess != null) _offlineProcess.Dispose();
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
