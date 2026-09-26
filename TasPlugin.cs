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
        private string _savestatesPath;
        private string _settingsPath;
        private StudioSettings _settings = new StudioSettings();
        private readonly List<RecordingDefinition> _recordings = new List<RecordingDefinition>();
        private readonly List<Savestate> _savestates = new List<Savestate>();
        private float _pendingCourseTime;
        private bool _pendingCourseWasTracking;
        private MacroDefinition _pendingReplay;
        private int _pendingReplayStartDelayTicks;
        private Rigidbody2D _playerBody;
        private bool _hasPreservedCourseResetPoint;
        private Vector2 _preservedCourseResetPoint;
        private CourseDef _runCourse;
        private int _brakeTicks = -1;
        private float _brakeAxis;
        const int MaxBrakeTicks = 150;
        private static readonly FieldInfo MovMomentumField = AccessTools.Field(typeof(Movement), "momentum");
        private static readonly FieldInfo MovOnGroundField = AccessTools.Field(typeof(Movement), "onGround");
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
            _savestatesPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "savestates");
            _settingsPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "settings.json");
            LoadSettings();
            LoadLibrary();
            LoadRecordings();
            LoadSavestates();
            LoadCourses();
            LoadFullTas();
            LoadTechs();
            _engine.AttemptEnded += OnAttemptEnded;
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
            PatchOptional(typeof(Movement), "FixedUpdate", nameof(BeforeMovementFixedUpdate), nameof(AfterMovementFixedUpdate));
            PatchOptional(typeof(Movement), "Update", null, nameof(AfterMovementUpdate));
            PatchOptional(typeof(courseScript), "FixedUpdate", nameof(BeforeOrderedFixedUpdate), null);
            PatchOptional(typeof(VmanScript), "FixedUpdate", nameof(BeforeOrderedFixedUpdate), null);
            TurboRunner.Patch(_harmony);
            PatchOptional(typeof(courseScript), "startTracking", nameof(BeforeCourseStart), nameof(AfterCourseStart));
            PatchOptional(typeof(courseScript), "stopTracking", nameof(BeforeCourseFinish), nameof(AfterCourseFinish));
            PatchOptional(typeof(Movement), "onDeath", null, nameof(AfterPlayerDeath));
            PatchOptional(typeof(Movement), "CheckForSuperJump", nameof(BeforeDashFrame), null);
            PatchOptional(typeof(Movement), "NormaliseInput", nameof(BeforeNormaliseInput), null);
            PatchOptional(typeof(Movement), "touchedSolidGround", nameof(BeforeWallTouch), null);
            PatchOptional(typeof(Movement), "touchedMossyGround", nameof(BeforeWallTouch), null);
            PatchOptional(typeof(Saveloader), "autosave", nameof(BlockSharedSave), null);
            PatchOptional(typeof(Saveloader), "manualSave", nameof(BlockSharedSave), null);
            PatchOptional(typeof(Saveloader), "delayedManualSave", nameof(BlockSharedSave), null);
            PatchOptional(typeof(Saveloader), "Awake", nameof(BeforeSaveloaderAwake), null);
            PatchOptional(typeof(upgradeBox), "DoBuyUpgrade", null, nameof(AfterBuyUpgrade));
            PatchOptional(typeof(Movement), "respawn", nameof(BeforeRespawn), null);
            PatchOptional(typeof(Movement), "ToggleCheckpointUse", null, nameof(AfterToggleCheckpointUse));
            PatchOptional(typeof(StepUpAnimationScript), "onSteppedUp", nameof(BeforeSteppedUp), null);
            PatchTechHooks();
            PatchVmanCreditsCrash();
            PatchWallClock();
            Logger.LogInfo("TAS Studio " + PluginVersion + " loaded. Use " + WebStudioServer.Url + ". F8 is emergency stop.");
        }

        private static bool BlockSharedSave()
        {
            if (Instance == null) return true;
            if (Instance._playbackSavedAbilities.Count != 0 || Instance._silentEvaluation || TurboRunner.Active || Instance._newGameSession) return false;
            if (Instance.SavedArea1State == null) return true;
            if (Instance._solverRunning || Instance.PlaybackActive || Instance._recorder.IsRecording) return false;
            Movement player = FindWorldMovement();
            if (player != null && player.courseResetPoint != Vector2.zero) return true;
            Instance.RestoreArea1State();
            return true;
        }

        // FakeCreditsControlScript.IE_DoVmanCredits ends a Vman run by crashing the game on purpose (Utils.ForceCrash), after
        // writing PlayerPrefs "VmanCoursesCompleteTemp" (+10 Vman score, added by the next VmanScript.Start). Every judge of
        // the VMAN run's last goal (the credits trigger) killed the game. Both calls come after the goal, so the route never
        // sees them; the crash goes, and so does the PlayerPrefs write, which without the crash would reach the next new
        // game's VmanScript in the same process and change its score.
        private void PatchVmanCreditsCrash()
        {
            MethodInfo coroutine = AccessTools.Method(typeof(FakeCreditsControlScript), "IE_DoVmanCredits");
            MethodInfo moveNext = coroutine == null ? null : AccessTools.EnumeratorMoveNext(coroutine);
            if (moveNext == null) { Logger.LogWarning("Optional hook unavailable: FakeCreditsControlScript.IE_DoVmanCredits"); return; }
            _harmony.Patch(moveNext, transpiler: new HarmonyMethod(AccessTools.Method(typeof(TasPlugin), nameof(DropVmanCreditsCrash))));
        }

        private static IEnumerable<CodeInstruction> DropVmanCreditsCrash(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo forceCrash = AccessTools.Method(typeof(UnityEngine.Diagnostics.Utils), "ForceCrash");
            MethodInfo setInt = AccessTools.Method(typeof(PlayerPrefs), "SetInt", new[] { typeof(string), typeof(int) });
            int dropped = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(forceCrash))
                {
                    dropped++;
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Pop).MoveLabelsFrom(instruction);
                }
                else if (instruction.Calls(setInt))
                {
                    dropped++;
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Pop).MoveLabelsFrom(instruction);
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Pop);
                }
                else yield return instruction;
            }
            if (dropped != 2) Log.LogWarning("Vman credits: expected ForceCrash and PlayerPrefs.SetInt, dropped " + dropped + " calls.");
        }

        // Three game scripts read the wall clock (Time.unscaledDeltaTime / unscaledTime), which Time.captureDeltaTime leaves
        // alone, so a fixed-clock probe still varied with real frame times:
        // - Movement.TimeStop's freeze (IE_TimeStop) counts unscaledDeltaTime: it froze for 3, 8 and 7 frames at 240 fps,
        //   each one a Movement.Update that can step up;
        // - cameraMover.LateUpdate eases the camera by unscaledDeltaTime, and the camera decides which clones are on screen
        //   (clonesLoD) and so how they pay;
        // - ObjectBobber.FixedUpdate bobs its object by unscaledTime.
        // Under the fixed clock they read the fixed clock's frame step and its running total instead (FixedFrameStep,
        // FixedUnscaledTime); otherwise the wall clock, as before. timer.Update (the play-time display) is left alone.
        private void PatchWallClock()
        {
            // The local iterator IE_TimeStop compiles to "<TimeStop>g__IE_TimeStop|…" on Movement or on its closure class.
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo coroutine = Array.Find(typeof(Movement).GetMethods(any), m => m.Name.Contains("IE_TimeStop"));
            foreach (Type nested in typeof(Movement).GetNestedTypes(BindingFlags.NonPublic))
                if (coroutine == null) coroutine = Array.Find(nested.GetMethods(any), m => m.Name.Contains("IE_TimeStop"));
            PatchWallClock("Movement.TimeStop.IE_TimeStop", coroutine == null ? null : AccessTools.EnumeratorMoveNext(coroutine));
            PatchWallClock("cameraMover.LateUpdate", AccessTools.Method(typeof(cameraMover), "LateUpdate"));
            PatchWallClock("ObjectBobber.FixedUpdate", AccessTools.Method(typeof(ObjectBobber), "FixedUpdate"));
        }

        private void PatchWallClock(string name, MethodInfo target)
        {
            if (target == null) { Logger.LogWarning("Optional hook unavailable: " + name); return; }
            _wallClockTarget = name;
            _harmony.Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(TasPlugin), nameof(ReadFixedClock))));
        }

        static string _wallClockTarget;

        private static IEnumerable<CodeInstruction> ReadFixedClock(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo unscaledDelta = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledDeltaTime));
            MethodInfo unscaledTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTime));
            MethodInfo step = AccessTools.Method(typeof(TasPlugin), nameof(FixedFrameStep));
            MethodInfo total = AccessTools.Method(typeof(TasPlugin), nameof(FixedUnscaledTime));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                MethodInfo with = instruction.Calls(unscaledDelta) ? step : instruction.Calls(unscaledTime) ? total : null;
                if (with == null) { yield return instruction; continue; }
                replaced++;
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, with).MoveLabelsFrom(instruction);
            }
            if (replaced != 1) Log.LogWarning(_wallClockTarget + ": expected one wall-clock read, replaced " + replaced + ".");
        }

        /// <summary>This frame's unscaled length: the fixed clock's step while it runs, else the wall clock's.</summary>
        static float FixedFrameStep()
        {
            return Instance != null && Instance.FixedClock ? Instance._clockFrame : Time.unscaledDeltaTime;
        }

        /// <summary>Unscaled time: the fixed clock's frames summed from its start while it runs, else the wall clock's.</summary>
        static float FixedUnscaledTime()
        {
            return Instance != null && Instance.FixedClock ? (float)Instance._clockTotal : Time.unscaledTime;
        }

        internal bool CheckpointRespawns { get { return _settings.checkpointRespawns; } }

        private static void BeforeRespawn(Movement __instance)
        {
            if (Instance != null) __instance.isRespawningAtCheckpoints = Instance.CheckpointRespawns;
        }

        private static void AfterToggleCheckpointUse(Movement __instance, ref bool __result)
        {
            if (Instance == null) return;
            __instance.isRespawningAtCheckpoints = Instance.CheckpointRespawns;
            __result = Instance.CheckpointRespawns;
        }

        private static bool BeforeMovementFixedUpdate(Movement __instance)
        {
            if (Instance == null) return true;
            if (!IsMenuPlayer(__instance))
            {
                Instance.LockInterpolation(__instance);
                Instance._engine.BeginStepUpWindow();
                __instance.isRespawningAtCheckpoints = Instance.CheckpointRespawns;
                Instance.OverlayBeforeTick(__instance);
            }
            CourseDef course = Instance._engine.IsRunning && !IsMenuPlayer(__instance) ? Instance.RunCourse : null;
            if (course != null && course.HasFinish && Instance.TouchesEndBox(__instance, course))
                Instance._engine.CompleteFromGame(course.IsTransition ? MacroEngine.TransitionFinishReason : MacroEngine.RewardFinishReason,
                    Instance._engine.PhysicsTicks * Time.fixedDeltaTime);
            if (!IsMenuPlayer(__instance)) Instance.FullTasAreaGoal(__instance);
            if (!IsMenuPlayer(__instance) && Instance._engine.TakeQuickRestart()) Instance.QuickRestart(__instance);
            if (!IsMenuPlayer(__instance) && !Instance._recorder.IsRecording)
            {
                int presses = Instance._engine.JumpPresses;
                Instance._engine.BeforeTick(__instance);
                if (Instance._engine.JumpPresses != presses) Instance.OverlayCaptureAxis(__instance);
            }
            if (Instance._pendingSilentMacro != null && !Instance._engine.IsRunning)
            {
                MacroDefinition macro = Instance._pendingSilentMacro;
                Instance._pendingSilentMacro = null;
                bool savestateRestored = Instance.RestoreRouteStart(macro);
                Movement movement = FindWorldMovement();
                // A tech demo's start (EngineSimTechTrace) runs as its playback does: from its savestate at once, no course finish needed.
                bool tech = Instance._techStart != null;
                Instance._engine.Run(macro, !(tech && savestateRestored), !tech, 0,
                    movement == null ? (Vector2?)null : movement.transform.position);
                return true;
            }
            if (Instance._newGamePhase == 3 && !IsMenuPlayer(__instance) && !TurboRunner.Active && Instance.NewGamePlayerAtRest(__instance))
                Instance.CaptureNewGameStart(__instance);
            if (Instance._fullTasPlay != null && !Instance._fullTasPlay.started && !Instance._fullTasPlay.waitingForNewGame && !Instance._engine.IsRunning
                && !IsMenuPlayer(__instance))
            {
                Instance.BeginFullTasPlayback(__instance);
                return true;
            }
            if (Instance._pendingReplay != null && !Instance._engine.IsRunning)
            {
                MacroDefinition macro = Instance._pendingReplay;
                int startDelayTicks = Instance._pendingReplayStartDelayTicks;
                Instance._pendingReplay = null;
                Instance._pendingReplayStartDelayTicks = 0;
                Movement movement = FindWorldMovement();
                Instance.ApplyCategoryAbilities(macro, movement);
                bool savestateRestored = Instance.RestoreRouteStart(macro);
                movement = FindWorldMovement();
                Instance.ApplyCategoryAbilities(macro, movement);
                Instance.ApplyTechAbilities(macro, movement);
                Vector2? synchronizationPosition = movement == null ? (Vector2?)null : movement.transform.position;
                Instance.SnapCameraForFixedClock(movement);
                Instance._engine.Run(macro, !savestateRestored, !Instance.IsTechDemo(macro), startDelayTicks, synchronizationPosition);
            }
            return true;
        }

        private static void AfterMovementFixedUpdate(Movement __instance)
        {
            if (Instance == null || IsMenuPlayer(__instance)) return;
            Instance._engine.CountPhysicsTick();
            if (!Instance._recorder.IsRecording) Instance._engine.AfterFixedTick(__instance);
            Instance.OverlayTick(__instance);
            Instance.ProbeAfterTick(__instance);
            RunDeferredFixedUpdates();
        }

        // Turbo (TurboRunner.Run) and the offline engine step a tick as Movement.FixedUpdate, then every courseScript's, then
        // VmanScript's. Unity's own order between them isn't fixed: after a new game it can run a course's first, which sets
        // courseResetPoint to its resetPoint before the player's quick restart reads it (a route whose quick restart falls on
        // the tick its start gate starts tracking then respawns elsewhere at 1x and dies). While a route plays at 1x, a course's
        // or Vman's FixedUpdate that comes before the player's this tick waits and runs right after it, in turbo's order.
        static readonly MethodInfo CourseFixedUpdateMethod = AccessTools.Method(typeof(courseScript), "FixedUpdate");
        static readonly MethodInfo VmanFixedUpdateMethod = AccessTools.Method(typeof(VmanScript), "FixedUpdate");
        static readonly List<MonoBehaviour> DeferredFixedUpdates = new List<MonoBehaviour>();
        static float _playerFixedTime = -1f;
        static bool _runningDeferred;

        private static bool BeforeOrderedFixedUpdate(MonoBehaviour __instance)
        {
            if (Instance == null || TurboRunner.Active || _runningDeferred || !Instance._engine.IsRunning || _playerFixedTime == Time.fixedTime) return true;
            if (!DeferredFixedUpdates.Contains(__instance)) DeferredFixedUpdates.Add(__instance);
            return false;
        }

        static void RunDeferredFixedUpdates()
        {
            if (TurboRunner.Active) return;
            _playerFixedTime = Time.fixedTime;
            if (DeferredFixedUpdates.Count == 0) return;
            _runningDeferred = true;
            try
            {
                var scripts = new List<MonoBehaviour>(DeferredFixedUpdates);
                foreach (bool courses in new[] { true, false })
                    foreach (MonoBehaviour script in scripts)
                        if (script != null && script.isActiveAndEnabled && script is courseScript == courses)
                            (courses ? CourseFixedUpdateMethod : VmanFixedUpdateMethod).Invoke(script, null);
            }
            finally
            {
                _runningDeferred = false;
                DeferredFixedUpdates.Clear();
            }
        }

        private static bool IsMenuPlayer(Movement movement)
        {
            return MovIsOnMainMenuField != null && MovIsOnMainMenuField.GetValue(movement) is bool menu && menu;
        }

        private static bool BeforePlayerInput(Movement __instance)
        {
            if (Instance == null) return true;
            Instance._recorder.FixedTick(__instance);
            if (Instance._recorder.IsRecording) return true;
            Instance._engine.FixedTick(__instance);
            Instance.OverlayCaptureAxis(__instance);
            if (Instance._brakeTicks >= 0 && !IsMenuPlayer(__instance)) Instance.Brake(__instance);
            return true;
        }

        private static void BeforeNormaliseInput(Movement __instance, ref Vector2 __0)
        {
            if (Instance == null || IsMenuPlayer(__instance) || Instance._recorder.IsRecording || !Instance._engine.IsRunning) return;
            __0 = Instance._engine.StickFor(__instance);
        }

        /// <summary>
        /// The step-up gate (MacroEngine.AllowStepUp), while the TAS holds the player body's interpolation off: a second lift
        /// between two physics ticks is undone and its animation skipped.
        /// </summary>
        private static bool BeforeSteppedUp(StepUpAnimationScript __instance, Vector3 __0)
        {
            if (Instance == null) return true;
            Movement player = __instance.GetComponentInParent<Movement>();
            if (player == null || IsMenuPlayer(player)) return true;
            if (!Instance.ProbeKeepsGameInterpolation(player) && !Instance._engine.AllowStepUp(player.transform, __0)) return false;
            Instance.TechSteppedUp(player);
            return true;
        }

        private static void BeforeWallTouch(Movement __instance, bool touchedGround)
        {
            if (Instance == null || IsMenuPlayer(__instance) || Instance._recorder.IsRecording) return;
            Instance._engine.WallTouch(__instance, touchedGround);
        }

        private void Brake(Movement movement)
        {
            if (_engine.IsRunning || ++_brakeTicks > MaxBrakeTicks) { _brakeTicks = -1; return; }
            float momentumX = ((Vector2)MovMomentumField.GetValue(movement)).x;
            if (_brakeTicks == 1) _brakeAxis = Mathf.Abs(momentumX) > 1f ? -Mathf.Sign(momentumX) : 0f;
            if (momentumX * _brakeAxis >= -1f) _brakeAxis = 0f;
            if (_brakeAxis == 0f && (bool)MovOnGroundField.GetValue(movement)
                && Mathf.Abs(movement.GetComponent<Rigidbody2D>().linearVelocity.x) <= 1f)
            {
                _brakeTicks = -1;
                return;
            }
            movement.MoveAxis = new Vector2(_brakeAxis, 0f);
        }

        CourseDef RunCourse { get { return _fullTas != null ? null : _silentEvaluation ? _solverTarget : _runCourse; } }

        private bool TouchesEndBox(Movement player, CourseDef course)
        {
            return TouchesBox(player, course.endX, course.endY, course.endW, course.endH);
        }

        private static bool TouchesBox(Movement player, float x, float y, float w, float h)
        {
            Vector2 origin = CurrentOrigin();
            Rect zone = new Rect(x - w / 2f + origin.x, y - h / 2f + origin.y, w, h);
            foreach (BoxCollider2D box in player.GetComponents<BoxCollider2D>())
            {
                if (!box.enabled) continue;
                Bounds bounds = box.bounds;
                if (new Rect(bounds.min.x, bounds.min.y, bounds.size.x, bounds.size.y).Overlaps(zone)) return true;
            }
            return false;
        }

        private static void BeforeDashFrame(Movement __instance)
        {
            if (Instance != null) Instance._recorder.DashFrame(__instance);
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

        private static void BeforeCourseStart(courseScript __instance)
        {
            if (Instance == null || __instance == null || !Instance._engine.IsRunning) return;
            Instance._fullTasStartWasTracking = CourseTrackingFlag.GetValue(__instance) is bool was && was;
            CourseDef course = Instance.RunCourse;
            if (course == null || course.toCourse <= 0 || __instance.courseNumber != course.toCourse) return;
            if (CourseTrackingFlag.GetValue(__instance) is bool tracking && tracking) return;
            Instance._engine.CompleteFromGame(MacroEngine.TransitionFinishReason, Instance._engine.PhysicsTicks * Time.fixedDeltaTime);
        }

        private static void AfterCourseStart(courseScript __instance)
        {
            if (Instance != null)
            {
                Instance._pendingCourseTime = 0f;
                if (__instance != null && Instance.OverlayListsRun) Instance._overlayCourse = __instance.courseNumber.ToString();
                Instance._recorder.MarkCourseStart();
                if (Instance._fullTas != null && __instance != null && Instance._engine.IsRunning && !Instance._fullTasStartWasTracking
                    && CourseTrackingFlag.GetValue(__instance) is bool tracking && tracking)
                    Instance.FullTasCourseStarted(__instance.courseNumber);
            }
            if (Instance != null && Instance._recorder.IsRecording) Log.LogInfo("Recording: authoritative course start detected");
        }

        private static void BeforeCourseFinish(courseScript __instance, bool savePositionData)
        {
            if (Instance == null) return;
            CourseDef target = Instance.RunCourse;
            bool timing = __instance != null && CourseTrackingField != null && CourseTrackingField.GetValue(__instance) is bool tracking && tracking;
            Instance._fullTasFinishCourse = Instance._fullTas != null && timing ? __instance.courseNumber : -1;
            Instance._pendingCourseWasTracking = timing && Instance._fullTas == null
                && (!Instance._silentEvaluation
                    || target != null && !target.HasFinish && !target.IsTransition && __instance.courseNumber == target.GameCourse);
            if (!savePositionData || __instance == null || CourseCurrentPathTimeField == null) return;
            object value = CourseCurrentPathTimeField.GetValue(__instance);
            Instance._pendingCourseTime = value is float ? (float)value : 0f;
        }

        private static void AfterCourseFinish(bool savePositionData)
        {
            if (Instance != null && savePositionData && Instance._fullTasFinishCourse >= 0) Instance.FullTasCourseFinished(Instance._fullTasFinishCourse);
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
            if (all == null || all.Length == 0) return null;
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

        const string CapabilitiesLockedError = "\"Don't touch my unlocks\" is on; turn it off to use this.";
        bool CapabilitiesLocked { get { return _settings.lockCapabilities; } }
        internal static bool UnlocksLocked { get { return Instance != null && Instance.CapabilitiesLocked; } }

        private static void SetMovementField(Movement movement, string name, object value)
        {
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

        static FloatingOrigin _floatingOrigin;

        internal static FloatingOrigin CurrentFloatingOrigin()
        {
            if (_floatingOrigin == null) _floatingOrigin = FindAnyObjectByType<FloatingOrigin>();
            return _floatingOrigin;
        }

        internal static Vector2 CurrentOrigin()
        {
            FloatingOrigin origin = CurrentFloatingOrigin();
            return origin == null ? Vector2.zero : (Vector2)origin.currentOrigin;
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
                            x = m.transform.position.x - CurrentOrigin().x,
                            y = m.transform.position.y - CurrentOrigin().y
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
                    SetMovementField(world, "isRespawningAtCheckpoints", CheckpointRespawns);
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
            if (_bootstrapRequest != null || _newGamePhase != 0)
            {
                request.StatusCode = 409;
                return new { ok = false, error = _newGamePhase != 0 ? "A new game is loading." : "A bootstrap is already in progress." };
            }
            _newGameSession = false;
            _newGameStart = null;
            _newGamePlayer = null;
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

        bool _newGameSession;
        int _newGamePhase;
        int _newGameFrame;
        WebStudioRequest _newGameRequest;
        readonly List<string> _newGameNotes = new List<string>();
        TurboRunner.Snapshot _newGameStart;
        Movement _newGamePlayer;
        bool _newGameVman;
        // A Full-TAS savestate start: the savestate (id) restored once the new game (or Vman world) is at rest; the start is
        // captured when the player is at rest again from there, with the unlocks the savestate carries.
        string _newGameSavestate;
        bool _newGameSavestateApplied;
        FullTasUnlocks _newGameUnlocks;
        Movement _vmanSetupPlayer;
        bool _vmanSetupQuiet;
        int _newGameRestTicks;
        // Physics ticks the player must stand still on the ground before the start is captured. A Vman world's player spawns
        // in the air and lands on a frame-rate-dependent tick; capturing mid-fall gave every load a different start. Past
        // this many grounded ticks every tick-driven threshold (dash boost window 6 × 1.4, platform momentum history 7) is
        // passed, so each load's start plays the same.
        const int NewGameRestTicks = 10;
        // The FloatingOrigin the start was captured at. Real-time play far from it rebases the world (FloatingOrigin.LateUpdate),
        // and a restore puts back only the player's scene position, which then lies somewhere else in the world: such a game
        // is no longer fresh, and the next Full-TAS run loads a new one.
        Vector2 _newGameOrigin;
        bool FreshGame { get { return _newGameStart != null && _newGamePlayer != null; } }
        bool FreshGameInWorld { get { return FreshGame && _newGamePlayer == FindWorldMovement() && CurrentOrigin() == _newGameOrigin; } }

        static readonly FieldInfo SaveloaderIsSaving = AccessTools.Field(typeof(Saveloader), "IsSaving");
        static readonly FieldInfo SaveloaderIsLoading = AccessTools.Field(typeof(Saveloader), "isLoading");

        private static void BeforeSaveloaderAwake(Saveloader __instance)
        {
            if (Instance == null || !Instance._newGameSession) return;
            SaveloaderIsSaving.SetValue(__instance, false);
            SaveloaderIsLoading.SetValue(__instance, false);
        }

        void StartNewGame(WebStudioRequest request, bool vman, string savestate)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_solverRunning) throw new InvalidOperationException("Stop the solver first.");
            LoadNewGame(request, vman, savestate);
        }

        void LoadNewGame(WebStudioRequest request, bool vman, string savestate)
        {
            if (_newGamePhase != 0) throw new InvalidOperationException("A new game is already loading.");
            if (_bootstrapRequest != null) throw new InvalidOperationException("A bootstrap is in progress.");
            if (savestate != null && _savestates.Find(s => s.id == savestate) == null) throw new InvalidOperationException("No savestate " + savestate + ".");
            _newGameSession = true;
            globalStats.difficultyLevel = 0;
            Time.timeScale = 1f;
            VmanScript.isCurrentlyVman = false;
            VmanScript.metaVman = false;
            VmanScript.vmanScore = 0.0;
            VmanScript.vmanTimeLeft = 0.0;
            VmanScript.VmanStatingUpgrades = null;
            SceneManager.LoadScene("Overworld");
            _newGameStart = null;
            _newGamePlayer = null;
            _newGameVman = vman;
            _newGameSavestate = savestate;
            _newGameSavestateApplied = false;
            _newGameUnlocks = default;
            _vmanSetupPlayer = null;
            _startState = null;
            _engineSimStartKey = null;
            _newGameNotes.Clear();
            _newGameFrame = Time.frameCount;
            _newGamePhase = 1;
            _newGameRequest = request;
            if (request != null) request.AsyncHandling = true;
        }

        void PumpNewGame()
        {
            if (_newGamePhase != 1 && _newGamePhase != 2 && _newGamePhase != 3 && _newGamePhase != 4 && _newGamePhase != 5) return;
            int frames = Time.frameCount - _newGameFrame;
            if (_newGamePhase == 4)
            {
                if (VmanScript.isCurrentlyVman)
                {
                    _newGameNotes.Add("the game bought the Vman start box");
                    EndVmanSetupJump();
                    _newGamePhase = 5;
                    _newGameFrame = Time.frameCount;
                }
                else if (frames > 300) { EndVmanSetupJump(); FailNewGame("The player under the Vman start box did not buy it within 300 frames."); }
                return;
            }
            if (_newGamePhase == 5)
            {
                Movement next = FindWorldMovement();
                if (next != null && next != _vmanSetupPlayer && SceneManager.GetActiveScene().name == "Overworld")
                {
                    _newGameNotes.Add("the Vman world loaded");
                    _vmanSetupPlayer = null;
                    _newGamePhase = 2;
                    _newGameFrame = Time.frameCount;
                }
                else if (frames > 900) FailNewGame("The Vman world did not load within 900 frames of the start box's purchase.");
                return;
            }
            if (frames > 600)
            {
                FailNewGame(_newGamePhase == 3 ? "The new game's player did not come to rest on the ground within 600 frames." : "The new game's player did not settle within 600 frames.");
                return;
            }
            if (_newGamePhase == 3) return;
            if (_newGamePhase == 1)
            {
                if (frames < 2 || SceneManager.GetActiveScene().name != "Overworld") return;
                _newGameNotes.Add("scene Overworld loaded");
                _newGamePhase = 2;
                _newGameFrame = Time.frameCount;
                return;
            }
            Movement player = FindWorldMovement();
            if (player == null || IsMenuPlayer(player)) return;
            if (!player.gameObject.activeInHierarchy)
            {
                if (frames < 5) return;
                if (player.transform.parent != null) player.transform.parent.gameObject.SetActive(true);
                player.gameObject.SetActive(true);
                _newGameNotes.Add("activated the world player");
                return;
            }
            if (frames < 10 || !ZonesSettled()) return;
            EnsureGameUnpaused();
            _newGameRestTicks = 0;
            _newGameFrame = Time.frameCount;
            _newGamePhase = 3;
        }

        bool NewGamePlayerAtRest(Movement player)
        {
            bool still = player.onGround && player.Velocity.x == 0f && player.cutsceneMode == Movement.cutsceneModes.none;
            _newGameRestTicks = still ? _newGameRestTicks + 1 : 0;
            return _newGameRestTicks > NewGameRestTicks;
        }

        void CaptureNewGameStart(Movement player)
        {
            if (_newGameVman && !VmanScript.isCurrentlyVman) { BuyVmanStart(player); return; }
            if (_newGameSavestate != null && !_newGameSavestateApplied) { ApplyNewGameSavestate(player); return; }
            _newGamePhase = 0;
            courseScript[] courses = FindObjectsByType<courseScript>(FindObjectsSortMode.None);
            bool quiet = _engine.Quiet;
            _engine.Quiet = true;
            _engine.Run(new MacroDefinition { name = "New game start", steps = { new MacroStep { type = MacroStepType.RawInput, durationTicks = 1 } } },
                false, true);
            UnityEngine.Random.InitState(FullTasSeed);
            _newGameStart = TurboRunner.Capture(player, courses, _engine.CaptureState(), 0, true);
            _engine.Discard();
            _engine.Quiet = quiet;
            _newGamePlayer = player;
            _newGameUnlocks = _newGameSavestate == null ? default : FullTasUnlocks.Of(player);
            _newGameOrigin = CurrentOrigin();
            if (_fullTasPlay != null) _fullTasPlay.waitingForNewGame = false;
            Vector2 position = (Vector2)player.transform.position - CurrentOrigin();
            Vector2 respawn = GetMovementField(player, "respawnPoint") is Vector2 r ? r : Vector2.zero;
            double cash = globalStats.currencyLookup != null && globalStats.currencyLookup.TryGetValue(globalStats.Currencies.Cash, out double c) ? c : 0.0;
            Logger.LogInfo("New game: player at " + position.ToString("0.###") + ", respawnPoint " + respawn.ToString("0.###") + ", Cash " + cash);
            WebStudioRequest request = _newGameRequest;
            _newGameRequest = null;
            if (request == null) return;
            request.Response = JsonConvert.SerializeObject(new {
                ok = true, position = new { x = position.x, y = position.y }, respawnPoint = new { x = respawn.x, y = respawn.y }, cash,
                unlocks = new { wallJump = GetMovementBool(player, "wallJumpUnlocked", false), dash = GetMovementBool(player, "dashUnlocked", false),
                    maxAirDashes = GetMovementField(player, "maxAirDashes"), doubleJump = GetMovementBool(player, "doubleJumpUnlocked", false),
                    maxAirJumps = GetMovementField(player, "maxAirJumps"), omniDash = GetMovementBool(player, "omniDashUnlocked", false) },
                vman = !VmanScript.isCurrentlyVman ? null : new { timeLeft = VmanScript.vmanTimeLeft,
                    omniDashBox = globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.VmanStartsWithOmniDash] },
                notes = _newGameNotes.ToArray() });
            request.Completed.Set();
        }

        void FailNewGame(string error)
        {
            _newGamePhase = 0;
            Logger.LogWarning("New game: " + error);
            DropPendingFullTasPlayback("the new game did not load: " + error);
            WebStudioRequest request = _newGameRequest;
            _newGameRequest = null;
            if (request == null) return;
            request.StatusCode = 500;
            request.Response = JsonConvert.SerializeObject(new { ok = false, error, notes = _newGameNotes.ToArray() });
            request.Completed.Set();
        }

        bool NewGameIs(FullTasRunDef run) { return _newGameVman == run.Vman && _newGameSavestate == run.savestate; }

        TurboRunner.Snapshot NewGameStart(FullTasRunDef run)
        {
            if (!FreshGameInWorld || !NewGameIs(run))
                throw new InvalidOperationException("A run from " + (run.Vman ? "a Vman speedrun" : "a new game") + (run.savestate == null ? "" : " and savestate " + run.savestate)
                    + " needs a fresh one: POST /api/session/newgame {" + (run.Vman ? "\"vman\": true" : "") + (run.Vman && run.savestate != null ? ", " : "")
                    + (run.savestate == null ? "" : "\"savestate\": \"" + run.savestate + "\"") + "} first.");
            return _newGameStart;
        }

        const int PlaybackFrameRate = 280;
        const float DefaultWalkSlowdown = 10f;
        bool _playbackPaced;
        float _savedMaximumDeltaTime;
        int _savedVSyncCount, _savedTargetFrameRate;
        float _walkSlowdown;
        bool _walkScaled;
        string _playbackSpeedText = "1x";

        void PacePlayback(bool on)
        {
            if (on != _playbackPaced)
            {
                _playbackPaced = on;
                if (on)
                {
                    _savedMaximumDeltaTime = Time.maximumDeltaTime;
                    _savedVSyncCount = QualitySettings.vSyncCount;
                    _savedTargetFrameRate = Application.targetFrameRate;
                    Time.maximumDeltaTime = Time.fixedDeltaTime;
                    _pacedFps = _probe != null && _probe.fps > 0 ? _probe.fps : PlaybackFrameRate;
                    _clockNext = 1f / _pacedFps;
                    _clockTotal = 0.0;
                }
                else
                {
                    Time.maximumDeltaTime = _savedMaximumDeltaTime;
                    QualitySettings.vSyncCount = _savedVSyncCount;
                    Application.targetFrameRate = _savedTargetFrameRate;
                    Time.captureDeltaTime = 0f;
                }
            }
            if (!_playbackPaced) return;
            // SettingsScript sets the player's own frame rate and vsync when a scene loads (a Full-TAS new game): hold ours.
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != _pacedFps) Application.targetFrameRate = _pacedFps;
            if (!FixedClock) return;
            // This frame ran on the step set last frame; the wall-clock readers (PatchWallClock) see it and its running total.
            _clockFrame = _clockNext;
            _clockTotal += _clockFrame;
            _clockNext = FixedClockStep(_pacedFps);
            Time.captureDeltaTime = _clockNext;
        }

        int _pacedFps;

        float _clockFrame, _clockNext;
        double _clockTotal;

        /// <summary>A 1x playback on the fixed clock (a probe's <c>fixedClock</c>): game time and freezes ignore the wall clock.</summary>
        bool FixedClock { get { return _playbackPaced && _probe != null && _probe.fixedClock; } }

        /// <summary>
        /// At a fixed-clock probe's start: the camera eased on the wall clock until now, so it starts settled on the player,
        /// and the clones it frames (clonesLoD) pay the same from the first tick.
        /// </summary>
        void SnapCameraForFixedClock(Movement movement)
        {
            if (_probe != null && _probe.fixedClock && movement != null) SavestateService.SnapCamera(movement.transform.position);
        }

        /// <summary>
        /// The next frame's game-time step (<c>Time.captureDeltaTime</c>) for a fixed clock: frames fall on a grid anchored to
        /// the last physics tick, half a frame off it (offset + k/fps), so every tick sees the same frame times whatever the
        /// wall clock does and no frame lands on a tick boundary. With interpolation on, the render position a FixedUpdate
        /// write starts from (the last frame's blend) is then the same every run. Re-anchored every frame, so the float
        /// step's rounding never accumulates. The step goes to the grid point after the one nearest now: a frame that lands a
        /// hair short of its grid point (the float step's rounding) would otherwise get a near-zero next step, an extra
        /// Movement.Update in that tick.
        /// </summary>
        static float FixedClockStep(int fps)
        {
            double frame = 1.0 / fps, offset = frame / 2, tick = Time.fixedDeltaTime;
            double since = Time.timeAsDouble - Time.fixedTimeAsDouble;
            double next = offset + (Math.Floor((since - offset) / frame + 0.5) + 1) * frame;
            if (next >= tick) next = tick + offset;
            return (float)(next - since);
        }

        void PaceWalk()
        {
            if (!PlaybackActive && _pendingReplay == null) _walkSlowdown = 0f;
            bool on = _walkSlowdown > 0f && _engine.IsRunning && !TurboRunner.Active;
            if (on)
            {
                if (!_walkScaled) _playbackSpeedText = (1f / _walkSlowdown).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "x";
                _walkScaled = true;
                if (Time.timeScale != 0f) Time.timeScale = globalStats.baseGameSpeed / _walkSlowdown;
            }
            else if (_walkScaled)
            {
                _walkScaled = false;
                _playbackSpeedText = "1x";
                if (Time.timeScale != 0f) Time.timeScale = globalStats.baseGameSpeed;
            }
        }

        private void Update()
        {
            PacePlayback(PlaybackActive && !TurboRunner.Active);
            PaceWalk();
            OverlayFrame();
            BreakerLightsFrame();
            if (_webServer != null) _webServer.Pump(HandleWebRequest);
            PumpSolver();
            PumpBootstrap();
            PumpNewGame();
            PumpOfflineBests();
            PumpFullTas();
            PumpFastForward();
            PumpProbe();
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f8Key.wasPressedThisFrame)
            {
                if (_recorder.IsRecording) StopRecording("Emergency stop");
                StopPlayback("Emergency stop");
                return;
            }
        }

        private void CaptureSavestate()
        {
            Movement movement = FindWorldMovement();
            if (movement == null) return;
            Savestate savestate = SavestateService.Capture(movement,
                (SelectedMacro == null ? "Section" : SelectedMacro.name) + " start", MacroLibrary.CurrentSchemaVersion);
            _savestates.Insert(0, savestate);
            WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(savestate.id) + ".json"), savestate, false);
        }

        private void RestoreSavestate(Savestate savestate)
        {
            Movement movement = FindWorldMovement();
            SavestateResult result = SavestateService.Restore(movement, savestate, MacroLibrary.CurrentSchemaVersion);
            if (!result.Success) Logger.LogError(result.Message); else Logger.LogInfo(result.Message);
        }

        private bool RestoreRouteStart(MacroDefinition route)
        {
            Movement movement = FindWorldMovement();
            if (movement != null) _engine.ClearInjectedInput(movement);
            CourseDef named = CourseOfRoute(route.name);
            _runCourse = named;
            EnsureArea1State(named);
            EnsureZone(named);
            Savestate savestate = string.IsNullOrEmpty(route.savestateId) ? null : _savestates.Find(c => c.id == route.savestateId);
            if (movement != null && savestate != null)
            {
                SavestateResult restored = SavestateService.Restore(movement, savestate, MacroLibrary.CurrentSchemaVersion);
                if (restored.Success)
                {
                    LeaveCourses(movement);
                    return true;
                }
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
                if (!_hasPreservedCourseResetPoint)
                {
                    Vector2 initialResetPoint = movement.courseResetPoint != Vector2.zero
                        ? movement.courseResetPoint - CurrentOrigin()
                        : movement.respawnPoint;
                    if (initialResetPoint != Vector2.zero)
                    {
                        _preservedCourseResetPoint = initialResetPoint;
                        _hasPreservedCourseResetPoint = true;
                    }
                }
                if (_runCourse == null && _hasPreservedCourseResetPoint)
                    _runCourse = _courses.Find(c => c.x == _preservedCourseResetPoint.x && c.y == _preservedCourseResetPoint.y);
                if (_hasPreservedCourseResetPoint) movement.courseResetPoint = _preservedCourseResetPoint + CurrentOrigin();
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
                    body.position = movement.transform.position;
                    body.linearVelocity = Vector2.zero;
                    body.angularVelocity = 0f;
                    Physics2D.SyncTransforms();
                }
            }
            else Logger.LogWarning("No savestate or quick restart was available.");
            return false;
        }

        /// <summary>
        /// A savestate holds only the player, so a course the last run left tracking would keep writing its reset point into
        /// courseResetPoint every tick, and a quick restart would land there instead of at the checkpoint. The game's own
        /// off-course state (stopTracking without its rewards): no course tracking, courseResetPoint zero.
        /// </summary>
        static void LeaveCourses(Movement movement)
        {
            foreach (courseScript course in FindObjectsByType<courseScript>(FindObjectsSortMode.None)) CourseTrackingField.SetValue(course, false);
            movement.courseResetPoint = Vector2.zero;
        }

        private CourseDef CourseOfRoute(string routeName)
        {
            Match courseName = Regex.Match(routeName ?? "", @"^([^_\s]+)[_ ]");
            if (!courseName.Success) return null;
            string token = courseName.Groups[1].Value;
            return int.TryParse(token, out int courseId) ? _courses.Find(c => c.id == courseId)
                : _courses.Find(c => string.Equals(c.name, token, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The player body's interpolation as the game set it, before the lock first changed it.</summary>
        RigidbodyInterpolation2D? _gameInterpolation;

        private void LockInterpolation(Movement movement)
        {
            if (_playerBody == null || _playerBody.gameObject != movement.gameObject) _playerBody = movement.GetComponent<Rigidbody2D>();
            if (_playerBody == null) return;
            if (_gameInterpolation == null) _gameInterpolation = _playerBody.interpolation;
            RigidbodyInterpolation2D wanted = ProbeKeepsGameInterpolation(movement) ? _gameInterpolation.Value : RigidbodyInterpolation2D.None;
            if (_playerBody.interpolation != wanted) _playerBody.interpolation = wanted;
        }

        private void DuplicateMacro()
        {
            MacroDefinition copy = JsonConvert.DeserializeObject<MacroDefinition>(JsonConvert.SerializeObject(SelectedMacro));
            copy.id = Guid.NewGuid().ToString("N"); copy.name += " copy"; _library.macros.Add(copy); _library.selectedIndex = _library.macros.Count - 1; SaveLibrary();
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

        private void CreateRouteFromRecording(RecordingDefinition recording, List<RawTickSample> samples = null)
        {
            MacroDefinition macro = new MacroDefinition { name = recording.name };
            foreach (MacroStep step in ExactInputCompressor.Compress(samples ?? recording.samples, recording.fixedDeltaMs))
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

        private void ReplaySelected(int startDelayTicks = 0, float walkSlowdown = 0f)
        {
            MacroDefinition macro = TechPlayable(SelectedMacro);
            if (macro == null) return;
            _walkSlowdown = walkSlowdown;
            Match fullTasRoute = FullTasRouteName.Match(macro.name ?? "");
            if (fullTasRoute.Success)
            {
                StartFullTasPlayback(FindFullTasRun(fullTasRoute.Groups[1].Value), macro);
                return;
            }
            EnsureGameUnpaused();
            _pendingReplay = walkSlowdown > 0f ? HumanVersionOf(macro) ?? macro : macro;
            _pendingReplayStartDelayTicks = Mathf.Max(0, startDelayTicks);
        }

        private readonly Dictionary<string, object> _playbackSavedAbilities = new Dictionary<string, object>();

        internal static readonly string[] CapsLadder = { "none", "wj", "wjds", "wjdsdj", "wjdsdjbs" };
        internal static int TopLevel { get { return CapsLadder.Length - 1; } }

        internal static int CapsLevel(string caps)
        {
            return Math.Max(0, Array.IndexOf(CapsLadder, caps));
        }

        internal static string CapsFor(string label)
        {
            return label.Replace("_", "").ToLowerInvariant();
        }

        internal static (string field, object value)[] CategoryAbilities(string caps)
        {
            int level = CapsLevel(caps);
            int dashes = level >= 2 ? 1 : 0;
            int jumps = level >= 3 ? 1 : 0;
            return new (string, object)[] { ("wallJumpUnlocked", level >= 1), ("dashUnlocked", level >= 2),
                ("maxAirDashes", dashes), ("airDashesLeft", dashes),
                ("doubleJumpUnlocked", level >= 3), ("maxAirJumps", jumps), ("airJumpsLeft", jumps),
                ("blockSwapUnlocked", level >= 4), ("omniDashUnlocked", false) };
        }

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
            Match category = Regex.Match(route.name ?? "", @"^([^_\s]+)_(NONE|WJ_DS_DJ_BS|WJ_DS_DJ|WJ_DS|WJ)(?: |$)");
            if (!category.Success || movement == null) return;
            string caps = CapsFor(category.Groups[2].Value);
            foreach (var (field, value) in ApplyAbilities(movement, caps))
                if (!_playbackSavedAbilities.ContainsKey(field)) _playbackSavedAbilities[field] = value;
        }

        private int SaveUnlockLevel(Movement movement)
        {
            Func<string, object> saved = field => _playbackSavedAbilities.TryGetValue(field, out object value) ? value : GetMovementField(movement, field);
            Func<string, bool> on = field => saved(field) is bool b && b;
            Func<string, bool> some = field => saved(field) is int n && n > 0;
            if (!on("wallJumpUnlocked")) return 0;
            if (!on("dashUnlocked") || !some("maxAirDashes")) return 1;
            if (!on("doubleJumpUnlocked") || !some("maxAirJumps")) return 2;
            if (!on("blockSwapUnlocked")) return 3;
            return 4;
        }

        private void RestorePlaybackAbilities()
        {
            Movement movement = FindWorldMovement();
            if (movement != null)
                foreach (KeyValuePair<string, object> ability in _playbackSavedAbilities) SetMovementField(movement, ability.Key, ability.Value);
            _playbackSavedAbilities.Clear();
        }

        private void OnAttemptEnded(bool success, string reason)
        {
            if (!_silentEvaluation) FillTechDemos();
            if (_fullTasPlay != null && _fullTasPlay.started && !_silentEvaluation)
            {
                FinishFullTasPlayback(success, reason);
                return;
            }
            RestorePlaybackAbilities();
            if (_silentEvaluation)
            {
                _silentFinished = success && MacroEngine.IsFinishReason(reason);
                _silentTime = _engine.AuthoritativeCompletionTime;
                return;
            }
            if (success && reason == MacroEngine.TransitionFinishReason) _brakeTicks = 0;
        }

        private MacroDefinition SelectedMacro { get { ClampSelections(); return _library.macros.Count == 0 ? null : _library.macros[_library.selectedIndex]; } }
        private void ClampSelections()
        {
            _library.selectedIndex = Mathf.Clamp(_library.selectedIndex, 0, Mathf.Max(0, _library.macros.Count - 1));
        }

        private static string TypeName(MacroStepType type) { return type == MacroStepType.WallJump ? "WALLJUMP" : type.ToString().ToUpperInvariant(); }

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

        private void LoadSavestates()
        {
            try
            {
                Directory.CreateDirectory(_savestatesPath);
                foreach (string path in Directory.GetFiles(_savestatesPath, "*.json"))
                {
                    try
                    {
                        Savestate savestate = JsonConvert.DeserializeObject<Savestate>(File.ReadAllText(path));
                        if (savestate == null || savestate.schemaVersion != Savestate.CurrentSchemaVersion || string.IsNullOrEmpty(savestate.id))
                            throw new InvalidDataException("Unsupported or incomplete savestate.");
                        _savestates.Add(savestate);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Savestate rejected " + Path.GetFileName(path) + ": " + ex.Message);
                        Quarantine(path);
                    }
                }
                _savestates.Sort((a, b) => string.CompareOrdinal(b.createdUtc, a.createdUtc));
            }
            catch (Exception ex) { Logger.LogError("Could not load savestates: " + ex); }
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
                if (route == null || string.IsNullOrEmpty(route.id) || route.steps == null)
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
                foreach (MacroStep step in route.steps) changed |= NormalizeStepFrameTiming(step, tickMs);
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
            if (request.Path.StartsWith("/api/fulltas/", StringComparison.Ordinal)) return HandleFullTas(request, body);
            if (request.Path.StartsWith("/api/techs/", StringComparison.Ordinal)) return HandleTechs(request.Path, body);
            if (request.Path.StartsWith("/api/probe/", StringComparison.Ordinal)) return HandleProbe(request.Path, body);
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
            if (request.Method == "GET" && request.Path == "/api/state") return BuildWebState();
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
            if (Regex.IsMatch(name ?? "", @"^[^_\s]+_(NONE|WJ_DS_DJ_BS|WJ_DS_DJ|WJ_DS|WJ) (farm )?[\d.]+$", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Category route names are reserved for routes that passed the solver's gate.");
        }

        private object BuildWebState()
        {
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
            List<object> recordings = new List<object>();
            foreach (RecordingDefinition recording in _recordings)
            {
                List<object> actions = new List<object>();
                foreach (MacroStep action in recording.semanticSteps)
                    actions.Add(new { label = action.label, type = TypeName(action.type), direction = action.direction,
                        wallJumpHoldDirection = action.wallJumpHoldDirection, durationTicks = action.durationTicks });
                recordings.Add(new { id = recording.id, name = recording.name, createdUtc = recording.createdUtc,
                    sampleCount = recording.samples.Count, actionCount = recording.semanticSteps.Count,
                    courseTimeSeconds = recording.courseTimeSeconds, actions = actions });
            }
            Movement player = FindWorldMovement();
            return new { connected = true, version = PluginVersion, engineStatus = _engine.Status,
                unlockLevel = player == null ? (int?)null : SaveUnlockLevel(player),
                paused = IsGamePaused(),
                capabilitiesLocked = _settings.lockCapabilities,
                overlayShown = _settings.showOverlay,
                breakerLightsForced = _settings.forceBreakerLights,
                checkpointRespawns = _settings.checkpointRespawns,
                running = PlaybackActive,
                humanPlayback = _engine.IsRunning && _engine.ActiveMacro.id == HumanPlaybackId,
                recording = _recorder.IsRecording, selectedRouteId = selected == null ? "" : selected.id,
                routes = routes, recordings = recordings, savestates = savestates,
                courses = BuildCourseWebState(),
                solver = BuildSolverWebState(), fullTas = BuildFullTasWebState(), techs = BuildTechWebState() };
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
            if (_solverRunning) StopSolver("Game closed");
            if (_recorder.IsRecording) StopRecording("Game closed");
            PacePlayback(false);
            SaveLibrary();
            SaveSettings();
            if (_webServer != null) _webServer.Dispose();
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
