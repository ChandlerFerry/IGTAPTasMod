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
    public sealed partial class TasPlugin
    {
        private static bool BlockSharedSave()
        {
            if (Instance == null) return true;
            if (Instance._playbackSavedAbilities.Count != 0 || Instance._silentEvaluation || TurboRunner.Active || Instance._newGameSession || Instance.LabInjected) return false;
            if (Instance.SavedArea1State == null && !Instance.TreeSaved) return true;
            if (Instance._solverRunning || Instance.PlaybackActive || Instance._recorder.IsRecording) return false;
            Movement player = FindWorldMovement();
            if (player != null && player.courseResetPoint != Vector2.zero) return !Instance.TreeSaved;
            Instance.RestoreArea1State();
            Instance.RestoreTree();
            return true;
        }

        // Drops IE_DoVmanCredits' ForceCrash and PlayerPrefs write; both come after the goal (RULES.md exception 3).
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

        // Playback counts fixed-clock frames, not wall clock, in TimeStop's freeze, camera easing, ObjectBobber (exception 2).
        static class PresentationDraw
        {
            internal static void Before(out UnityEngine.Random.State? __state)
            {
                __state = Instance != null && Instance._fullTas != null ? UnityEngine.Random.state : (UnityEngine.Random.State?)null;
            }

            internal static void After(UnityEngine.Random.State? __state)
            {
                if (__state.HasValue) UnityEngine.Random.state = __state.Value;
            }
        }

        private void PatchWallClock()
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo coroutine = Array.Find(typeof(Movement).GetMethods(any), m => m.Name.Contains("IE_TimeStop"));
            foreach (Type nested in typeof(Movement).GetNestedTypes(BindingFlags.NonPublic))
                if (coroutine == null) coroutine = Array.Find(nested.GetMethods(any), m => m.Name.Contains("IE_TimeStop"));
            PatchWallClock("Movement.TimeStop.IE_TimeStop", coroutine == null ? null : AccessTools.EnumeratorMoveNext(coroutine));
            PatchWallClock("cameraMover.LateUpdate", AccessTools.Method(typeof(cameraMover), "LateUpdate"));
            PatchWallClock("ObjectBobber.FixedUpdate", AccessTools.Method(typeof(ObjectBobber), "FixedUpdate"));
            _harmony.Patch(AccessTools.Method(typeof(TeleporterScript), "manualUpdate"),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(TasPlugin), nameof(ReadTeleporterStick))));
        }

        static readonly AccessTools.FieldRef<TeleporterScript, Movement> TeleporterPlayer = AccessTools.FieldRefAccess<TeleporterScript, Movement>("player");

        // TeleporterScript.manualUpdate reads the Move action directly; during a run it must read the run's stick.
        private static IEnumerable<CodeInstruction> ReadTeleporterStick(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo stick = AccessTools.Method(typeof(TasPlugin), nameof(TeleporterStick));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (!(instruction.operand is MethodInfo m && m.Name == nameof(InputAction.ReadValue) && m.DeclaringType == typeof(InputAction)))
                {
                    yield return instruction;
                    continue;
                }
                replaced++;
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, stick);
            }
            if (replaced != 1) Log.LogWarning("TeleporterScript.manualUpdate: expected one Move read, replaced " + replaced + ".");
        }

        private static Vector2 TeleporterStick(InputAction action, TeleporterScript teleporter)
        {
            Movement player = TeleporterPlayer(teleporter);
            if (Instance != null && Instance._engine.IsRunning && player != null && !IsMenuPlayer(player)) return Instance._engine.StickFor(player);
            return action.ReadValue<Vector2>();
        }

        private void PatchWallClock(string name, MethodInfo target)
        {
            if (target == null) { Logger.LogWarning("Optional hook unavailable: " + name); return; }
            _wallClockTarget = name;
            _harmony.Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(TasPlugin), nameof(ReadFixedClock))));
        }

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

        private static void BeforeRespawn(Movement __instance, bool __0)
        {
            if (Instance == null) return;
            __instance.isRespawningAtCheckpoints = Instance.CheckpointRespawns;
            if (!IsMenuPlayer(__instance)) Instance.OverlayRespawned(__0);
        }

        private static void BeforeMoveRootTransforms(FloatingOrigin __instance) { TurboRunner.RecordPristineRoots(__instance); }

        private static void AfterMoveRootTransforms(Vector3 __0)
        {
            if (Instance != null) Instance._engine.WorldMoved(__0);
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
                TurboRunner.BeforeHandoffTick(__instance);
                Instance.KeepInterpolation(__instance);
                __instance.isRespawningAtCheckpoints = Instance.CheckpointRespawns;
                Instance.OverlayBeforeTick(__instance);
            }
            CourseDef course = Instance._engine.IsRunning && !IsMenuPlayer(__instance) ? Instance.RunCourse : null;
            if (course != null && course.HasFinish && (course.IsTransition || Instance.BonusRewardBox(course, out _) == null)
                && Instance.TouchesEndBox(__instance, course))
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
                if (!Instance.TreeStartReady(Instance._pendingSilentMacro)) return true;
                MacroDefinition macro = Instance._pendingSilentMacro;
                Instance._pendingSilentMacro = null;
                bool savestateRestored = Instance.RestoreRouteStart(macro);
                Movement movement = FindWorldMovement();
                bool tech = Instance._techStart != null;
                Instance._engine.Run(macro, !(tech && savestateRestored), !tech, 0,
                    movement == null ? (Vector2?)null : TurboRunner.BlendRead(movement.transform));
                return true;
            }
            if (Instance._newGamePhase == 3 && !IsMenuPlayer(__instance) && !TurboRunner.Active && Instance.NewGamePlayerAtRest(__instance)
                && !MoverResetting())
                Instance.CaptureNewGameStart(__instance);
            if (Instance._fullTasPlay != null && !Instance._fullTasPlay.started && !Instance._fullTasPlay.waitingForNewGame && !Instance._engine.IsRunning
                && !IsMenuPlayer(__instance))
            {
                Instance.BeginFullTasPlayback(__instance);
                return true;
            }
            // Start a replay only once playback is on the fixed clock, else early ticks run back to back and lose a freeze's turn.
            if (Instance._pendingReplay != null && !Instance._engine.IsRunning && Instance._playbackPaced)
            {
                if (!Instance.TreeStartReady(Instance._pendingReplay)) return true;
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
                Vector2? synchronizationPosition = movement == null ? (Vector2?)null : TurboRunner.BlendRead(movement.transform);
                Instance.SnapCameraAtPlaybackStart(movement);
                Instance._engine.Run(macro, !savestateRestored, !Instance.IsTechDemo(macro), startDelayTicks, synchronizationPosition);
            }
            return true;
        }

        private static void AfterMovementFixedUpdate(Movement __instance)
        {
            if (Instance == null || IsMenuPlayer(__instance)) return;
            Instance._engine.CountPhysicsTick();
            Instance.TasAfterTick(__instance);
            if (!Instance._recorder.IsRecording) Instance._engine.AfterFixedTick(__instance);
            Instance.OverlayTick(__instance);
            Instance.ProbeAfterTick(__instance);
            int pause = Instance._engine.TakePause(out int afterFrames);
            if (TurboRunner.Active) TurboRunner.Pause(pause, afterFrames);
            else Instance.OpenPlaybackPause(__instance, pause, afterFrames);
            RunDeferredFixedUpdates();
            TurboRunner.AfterHandoffTick(__instance);
        }

        private static bool BeforeOrderedFixedUpdate(MonoBehaviour __instance)
        {
            if (Instance == null || TurboRunner.Active || _runningDeferred || !Instance._engine.IsRunning || _playerFixedTime == Time.fixedTime) return true;
            if (DeferredQueued.Add(__instance)) DeferredFixedUpdates.Add(__instance);
            return false;
        }

        static void RunDeferredFixedUpdates()
        {
            if (TurboRunner.Active) return;
            _playerFixedTime = Time.fixedTime;
            if (DeferredFixedUpdates.Count == 0) return;
            if (_courseFixedUpdate == null)
            {
                _courseFixedUpdate = AccessTools.MethodDelegate<Action<courseScript>>(CourseFixedUpdateMethod);
                _vmanFixedUpdate = AccessTools.MethodDelegate<Action<VmanScript>>(VmanFixedUpdateMethod);
            }
            _runningDeferred = true;
            try
            {
                DeferredRunning.Clear();
                DeferredRunning.AddRange(DeferredFixedUpdates);
                foreach (MonoBehaviour script in DeferredRunning)
                    if (script != null && script.isActiveAndEnabled && script is courseScript course) _courseFixedUpdate(course);
                foreach (MonoBehaviour script in DeferredRunning)
                    if (script != null && script.isActiveAndEnabled && script is VmanScript vman) _vmanFixedUpdate(vman);
            }
            finally
            {
                _runningDeferred = false;
                DeferredFixedUpdates.Clear();
                DeferredQueued.Clear();
                DeferredRunning.Clear();
            }
        }

        private static bool BeforePlayerInput(Movement __instance)
        {
            if (Instance == null) return true;
            Instance._recorder.FixedTick(__instance);
            if (Instance._recorder.IsRecording) return true;
            Instance._engine.FixedTick(__instance);
            if (Instance.NewGameLoading && !Instance._engine.IsRunning && !IsMenuPlayer(__instance)) Instance._engine.ClearInjectedInput(__instance);
            Instance.OverlayCaptureAxis(__instance);
            if (Instance._brakeTicks >= 0 && !IsMenuPlayer(__instance)) Instance.Brake(__instance);
            return true;
        }

        private static void BeforeNormaliseInput(Movement __instance, ref Vector2 __0)
        {
            if (Instance == null || IsMenuPlayer(__instance) || Instance._recorder.IsRecording) return;
            if (Instance._engine.IsRunning) __0 = Instance._engine.StickFor(__instance);
            else if (Instance.NewGameLoading) __0 = Vector2.zero;
        }

        private static void BeforeSteppedUp(StepUpAnimationScript __instance)
        {
            if (Instance == null) return;
            Movement player = __instance.GetComponentInParent<Movement>();
            if (player == null || IsMenuPlayer(player)) return;
            Instance.TechSteppedUp(player);
            Instance.TrainerSteppedUp(player);
        }

        private static void BeforeWallTouch(Movement __instance, bool touchedGround)
        {
            if (Instance == null || IsMenuPlayer(__instance) || Instance._recorder.IsRecording) return;
            Instance._engine.WallTouch(__instance, touchedGround);
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
            Instance._fullTasStartWasTracking = CourseTracking(__instance);
            CourseDef course = Instance.RunCourse;
            if (course == null || course.toCourse <= 0 || __instance.courseNumber != course.toCourse) return;
            if (CourseTracking(__instance)) return;
            Instance._engine.CompleteFromGame(MacroEngine.TransitionFinishReason, Instance._engine.PhysicsTicks * Time.fixedDeltaTime);
        }

        private static void AfterCourseStart(courseScript __instance)
        {
            if (Instance != null)
            {
                Instance._pendingCourseTime = 0f;
                if (__instance != null && (Instance.OverlayListsRun || Instance._overlayManual && Instance.OverlayListsManual))
                    Instance._overlayCourse = __instance.courseNumber.ToString();
                Instance._recorder.MarkCourseStart();
                if (Instance._fullTas != null && __instance != null && Instance._engine.IsRunning && !Instance._fullTasStartWasTracking
                    && CourseTracking(__instance))
                    Instance.FullTasCourseStarted(__instance.courseNumber);
            }
            if (Instance != null && Instance._recorder.IsRecording) Log.LogInfo("Recording: authoritative course start detected");
        }

        private static void BeforeCourseFinish(courseScript __instance, bool savePositionData)
        {
            if (Instance == null) return;
            CourseDef target = Instance.RunCourse;
            bool timing = __instance != null && CourseTracking(__instance);
            Instance._fullTasFinishCourse = Instance._fullTas != null && timing ? __instance.courseNumber : -1;
            Instance._pendingCourseWasTracking = timing && Instance._fullTas == null
                && (!Instance._silentEvaluation
                    || target != null && !target.HasFinish && !target.IsTransition && __instance.courseNumber == target.GameCourse);
            if (!savePositionData || __instance == null) return;
            Instance._pendingCourseTime = CourseCurrentPathTime(__instance);
        }

        private static void AfterCourseFinish(bool savePositionData)
        {
            if (Instance != null && savePositionData && Instance._fullTasFinishCourse >= 0) Instance.FullTasCourseFinished(Instance._fullTasFinishCourse);
            if (Instance == null || !savePositionData || !Instance._pendingCourseWasTracking) return;
            if (Instance._loopFinishes != null)
            {
                Instance._loopFinishes.Add((Instance._engine.RunTicks, Instance._engine.PhysicsTicks, Instance._engine.HeldFrames));
                return;
            }
            if (Instance._farmPlayback && !Instance._silentEvaluation) return;
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

        private static void BeforeSaveloaderAwake(Saveloader __instance)
        {
            if (Instance == null || !Instance._newGameSession) return;
            SaveloaderIsSaving.SetValue(__instance, false);
            SaveloaderIsLoading.SetValue(__instance, false);
        }
    }
}
