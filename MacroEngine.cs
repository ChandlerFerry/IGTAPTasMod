using System;
using System.Collections.Generic;
using System.Reflection;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    internal sealed class MacroEngine
    {
        private static readonly FieldInfo OnWallField = AccessTools.Field(typeof(Movement), "OnWall");
        private static readonly FieldInfo WallCoyoteField = AccessTools.Field(typeof(Movement), "wallCoyoteFrames");
        private static readonly FieldInfo WallJumpLockField = AccessTools.Field(typeof(Movement), "wallJumpMovementLockRemaining");
        private static readonly FieldInfo WallJumpDirectionField = AccessTools.Field(typeof(Movement), "wallJumpDirection");
        private static readonly FieldInfo JumpBufferField = AccessTools.Field(typeof(Movement), "jumpBuffer");
        private static readonly FieldInfo DashBufferField = AccessTools.Field(typeof(Movement), "dashBuffer");
        private static readonly FieldInfo JumpCutBufferField = AccessTools.Field(typeof(Movement), "jumpCutBuffer");
        private static readonly FieldInfo OnGroundField = AccessTools.Field(typeof(Movement), "onGround");
        private static readonly FieldInfo IsDeadField = AccessTools.Field(typeof(Movement), "isDead");
        private static readonly FieldInfo VelocityField = AccessTools.Field(typeof(Movement), "Velocity");
        private static readonly FieldInfo MomentumField = AccessTools.Field(typeof(Movement), "momentum");
        private static readonly FieldInfo MovingPlatformVelocityField = AccessTools.Field(typeof(Movement), "movingPlatformVelocity");
        private static readonly FieldInfo LastAppliedMomentumField = AccessTools.Field(typeof(Movement), "lastAppliedMomentum");
        private static readonly FieldInfo AtApexField = AccessTools.Field(typeof(Movement), "atJumpApex");
        private static readonly FieldInfo CoyoteField = AccessTools.Field(typeof(Movement), "coyoteFrames");
        private static readonly FieldInfo DashCooldownField = AccessTools.Field(typeof(Movement), "dashCooldown");
        private static readonly FieldInfo AirDashesField = AccessTools.Field(typeof(Movement), "airDashesLeft");
        private static readonly FieldInfo AirJumpsField = AccessTools.Field(typeof(Movement), "airJumpsLeft");
        private static readonly FieldInfo DashActiveField = AccessTools.Field(typeof(Movement), "dashActive");
        private static readonly FieldInfo FacingRightField = AccessTools.Field(typeof(Movement), "facingRight");
        private static readonly FieldInfo CutsceneModeField = AccessTools.Field(typeof(Movement), "cutsceneMode");
        private static readonly FieldInfo FramesSinceLastDashField = AccessTools.Field(typeof(Movement), "framesSinceLastDash");

        private List<MacroStep> _steps;
        private string _macroName;
        private int _stepIndex = -1;
        private float _elapsedMs;
        private int _elapsedTicks;
        private bool _wallJumpTriggered;
        private bool _rawJumpHeld;
        private int _dashJumpFrame;
        private Vector2 _dashJumpAxis;
        private bool _waitingForStable;
        private int _stableTicks;
        private bool _hasSynchronizationPosition;
        private Vector2 _synchronizationPosition;
        private float? _firstJumpPositionOffsetX;
        private bool _firstJumpPositionAdjusted;
        private float _courseStartPositionOffsetX;
        private bool _courseStartPositionAdjusted;
        private int _preRollTicksRemaining;
        private int _preRollTicksTotal;
        private long _runTicks;
        private bool _requireCourseFinish;
        private int _finishWaitTicks;
        private bool _hasProgressSample;
        private string _startStateFingerprint;
        private float _authoritativeCompletionTime;
        private long _courseStartRunTick;
        private int _courseStartUnityFrame;
        private Vector2 _courseStartPosition;
        private readonly List<string> _earlyTrace = new List<string>();

        public bool IsRunning { get { return _steps != null; } }
        public int StepIndex { get { return _stepIndex; } }
        public long RunTicks { get { return _runTicks; } }
        public string ActiveMacroId { get; private set; }
        public string ActiveMacroName { get { return _macroName; } }
        public string Status { get; private set; } = "Ready";
        public string StartStateFingerprint { get { return _startStateFingerprint; } }
        public float AuthoritativeCompletionTime { get { return _authoritativeCompletionTime; } }
        public long CourseStartRunTick { get { return _courseStartRunTick; } }
        public int CourseStartUnityFrame { get { return _courseStartUnityFrame; } }
        public Vector2 CourseStartPosition { get { return _courseStartPosition; } }
        public string EarlyTrace { get { return string.Join(";", _earlyTrace.ToArray()); } }
        public float FirstJumpPositionOffsetX { get { return _firstJumpPositionOffsetX.GetValueOrDefault(); } }
        public float CourseStartPositionOffsetX { get { return _courseStartPositionOffsetX; } }
        public event Action<bool, string, int, long> AttemptEnded;
        // Solver runs happen dozens of times a second; their start/end lines would flood the log.
        public bool Quiet;
        public bool InputsExhausted { get { return _steps != null && _stepIndex >= _steps.Count; } }

        public void Run(MacroDefinition macro, MacroLibrary library, bool synchronizeStart = false, bool requireCourseFinish = false,
            int startDelayTicks = 0, Vector2? synchronizationPosition = null, float? firstJumpPositionOffsetX = null)
        {
            if (macro == null || macro.steps == null || macro.steps.Count == 0)
            {
                Status = "Cannot run an empty macro";
                return;
            }
            _steps = Expand(macro.steps);
            NormalizeFrameTiming(_steps);
            _macroName = macro.name;
            ActiveMacroId = macro.id;
            _stepIndex = 0;
            _elapsedMs = 0f;
            _elapsedTicks = 0;
            _wallJumpTriggered = false;
            _rawJumpHeld = false;
            _dashJumpFrame = 0;
            _waitingForStable = synchronizeStart;
            _stableTicks = 0;
            _hasSynchronizationPosition = synchronizationPosition.HasValue;
            _synchronizationPosition = synchronizationPosition.GetValueOrDefault();
            _firstJumpPositionOffsetX = firstJumpPositionOffsetX;
            _firstJumpPositionAdjusted = false;
            _courseStartPositionOffsetX = 0f;
            _courseStartPositionAdjusted = false;
            _preRollTicksRemaining = Mathf.Max(0, startDelayTicks);
            _preRollTicksTotal = _preRollTicksRemaining;
            _runTicks = 0;
            _requireCourseFinish = requireCourseFinish;
            _finishWaitTicks = 0;
            _hasProgressSample = false;
            _startStateFingerprint = null;
            _authoritativeCompletionTime = 0f;
            _courseStartRunTick = -1;
            _courseStartUnityFrame = -1;
            _courseStartPosition = Vector2.zero;
            _earlyTrace.Clear();
            Status = synchronizeStart ? "Waiting for 3 stable ticks: " + macro.name : "Running: " + macro.name;
            if (!Quiet) TasPlugin.Log.LogInfo(Status);
        }

        public object[] CaptureState() { return FieldState.Capture(this); }

        // Continues a captured run with new remaining inputs, as if they had followed the captured prefix all along.
        public void Resume(object[] state, List<MacroStep> remaining)
        {
            FieldState.Restore(this, state);
            _steps = Expand(remaining);
            NormalizeFrameTiming(_steps);
            _stepIndex = 0;
            _elapsedMs = 0f;
            _elapsedTicks = 0;
            _wallJumpTriggered = false;
            _finishWaitTicks = 0;
        }

        public void Abort(string reason = "Stopped")
        {
            bool wasRunning = IsRunning;
            int step = _stepIndex;
            long ticks = _runTicks;
            _steps = null;
            _stepIndex = -1;
            _elapsedMs = 0f;
            _elapsedTicks = 0;
            Status = reason;
            if (!Quiet) TasPlugin.Log.LogInfo(reason);
            if (wasRunning && AttemptEnded != null) AttemptEnded(false, reason, step, ticks);
        }

        public void ClearInjectedInput(Movement movement)
        {
            _rawJumpHeld = false;
            if (movement == null) return;
            SetDirection(movement, 0);
            SetField(JumpBufferField, movement, false);
            SetField(JumpCutBufferField, movement, false);
            SetField(DashBufferField, movement, false);
        }

        public void CompleteFromGame(string reason = "Course finish detected", float authoritativeTime = 0f)
        {
            if (!IsRunning) return;
            long ticks = _runTicks;
            _authoritativeCompletionTime = authoritativeTime > 0f ? authoritativeTime : 0f;
            _steps = null;
            _stepIndex = -1;
            Status = reason + ": " + _macroName;
            if (!Quiet) TasPlugin.Log.LogInfo(Status);
            if (AttemptEnded != null) AttemptEnded(true, reason, -1, ticks);
        }

        public void MarkCourseStart(Movement movement)
        {
            if (!IsRunning || _courseStartRunTick >= 0) return;
            _courseStartRunTick = _runTicks;
            _courseStartUnityFrame = Time.frameCount;
            if (movement != null) _courseStartPosition = movement.transform.position;
        }

        public void SetCourseStartPositionOffset(float offsetX)
        {
            _courseStartPositionOffsetX = offsetX;
        }

        public void ApplyCourseStartPositionOffset(Movement movement)
        {
            if (_courseStartPositionAdjusted || movement == null || Mathf.Approximately(_courseStartPositionOffsetX, 0f)) return;
            _courseStartPositionAdjusted = true;
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            Vector2 position = body == null ? (Vector2)movement.transform.position : body.position;
            position.x += _courseStartPositionOffsetX;
            Vector3 transformPosition = movement.transform.position;
            transformPosition.x = position.x;
            transformPosition.y = position.y;
            movement.transform.position = transformPosition;
            if (body != null) body.position = position;
            Physics2D.SyncTransforms();
        }

        public void FixedTick(Movement movement)
        {
            if (!IsRunning)
                return;
            if (_waitingForStable)
            {
                NormalizeQuickRestartState(movement);
                PinSynchronizationPosition(movement);
                // Quick restart preserves movement state from the previous attempt. Clear it
                // throughout the synchronization window, then begin from three healthy ticks.
                bool stable = !ReadBool(IsDeadField, movement)
                    && ReadBool(OnGroundField, movement)
                    && string.Equals(ReadObject(CutsceneModeField, movement), "none", StringComparison.OrdinalIgnoreCase);
                _stableTicks = stable ? _stableTicks + 1 : 0;
                Status = "Synchronizing start: " + _stableTicks + "/3 stable ticks";
                if (_stableTicks < 3) return;
                _waitingForStable = false;
                Status = "Running: " + _macroName;
            }
            if (_preRollTicksRemaining > 0)
            {
                NormalizeQuickRestartState(movement);
                PinSynchronizationPosition(movement);
                _preRollTicksRemaining--;
                Status = "Start alignment: " + (_preRollTicksTotal - _preRollTicksRemaining) + "/" + _preRollTicksTotal + " ticks";
                return;
            }
            CaptureProgress(movement);
            _runTicks++;
            if (_stepIndex < 0 || _stepIndex >= _steps.Count)
            {
                Finish(movement);
                return;
            }

            MacroStep step = _steps[_stepIndex];
            bool entering = _elapsedTicks == 0;

            if (step.type == MacroStepType.WallJump)
            {
                RunWallJump(movement, step);
                return;
            }
            if (step.type == MacroStepType.RawInput)
            {
                RunRawInput(movement, step, entering);
                return;
            }

            SetDirection(movement, step.direction);
            if (entering && step.type == MacroStepType.Jump)
                PressJump(movement);
            else if (entering && step.type == MacroStepType.Dash)
                PressDash(movement);

            _elapsedMs += Time.fixedDeltaTime * 1000f;
            _elapsedTicks++;
            if (ShouldEnd(movement, step))
                Advance(movement, step.type == MacroStepType.Jump);
        }

        private void RunWallJump(Movement movement, MacroStep step)
        {
            if (!_wallJumpTriggered)
            {
                SetDirection(movement, -step.direction);
                _elapsedMs += Time.fixedDeltaTime * 1000f;
                _elapsedTicks++;
                if (IsWallJumpAvailable(movement))
                {
                    PressJump(movement);
                    SetDirection(movement, WallJumpHoldDirection(step));
                    _wallJumpTriggered = true;
                    _elapsedMs = 0f;
                    _elapsedTicks = 0;
                }
                else if (_elapsedTicks >= Mathf.Max(1, step.timeoutTicks))
                {
                    Fail(movement, "Walljump timeout");
                }
                return;
            }

            SetDirection(movement, WallJumpHoldDirection(step));
            _elapsedMs += Time.fixedDeltaTime * 1000f;
            _elapsedTicks++;
            if (ShouldEnd(movement, step))
                Advance(movement, true);
        }

        private void RunRawInput(Movement movement, MacroStep step, bool entering)
        {
            movement.MoveAxis = new Vector2(step.rawMoveX, step.rawMoveY);
            if (entering)
            {
                if (step.rawJumpPressed)
                {
                    ApplyFirstJumpPositionOffset(movement);
                    PressJump(movement);
                }
                if (step.rawJumpReleased || (_rawJumpHeld && !step.rawJumpHeld))
                    ReleaseJump(movement);
                if (step.rawDashPressed)
                {
                    PressDash(movement);
                    _dashJumpFrame = step.dashJumpFrame;
                    _dashJumpAxis = new Vector2(step.rawMoveX, step.rawMoveY);
                }
                _rawJumpHeld = step.rawJumpHeld;
            }
            _elapsedMs += Time.fixedDeltaTime * 1000f;
            _elapsedTicks++;
            if (_elapsedTicks >= Mathf.Max(1, step.durationTicks)) Advance(movement, false);
        }

        // Called once per dash frame (Movement.CheckForSuperJump prefix), before the game reads jumpBuffer.
        public void DashFrame(Movement movement)
        {
            if (!IsRunning || _dashJumpFrame <= 0 || ReadFloat(FramesSinceLastDashField, movement) < _dashJumpFrame) return;
            _dashJumpFrame = 0;
            movement.MoveAxis = _dashJumpAxis;
            PressJump(movement);
            _rawJumpHeld = true;
        }

        private static int WallJumpHoldDirection(MacroStep step)
        {
            return step.wallJumpHoldDirection >= -1 && step.wallJumpHoldDirection <= 1
                ? step.wallJumpHoldDirection : step.direction;
        }

        private void ApplyFirstJumpPositionOffset(Movement movement)
        {
            if (_firstJumpPositionAdjusted || !_firstJumpPositionOffsetX.HasValue || movement == null) return;
            _firstJumpPositionAdjusted = true;
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            Vector2 position = body == null ? (Vector2)movement.transform.position : body.position;
            position.x += _firstJumpPositionOffsetX.Value;
            Vector3 transformPosition = movement.transform.position;
            transformPosition.x = position.x;
            transformPosition.y = position.y;
            movement.transform.position = transformPosition;
            if (body != null) body.position = position;
            Physics2D.SyncTransforms();
        }

        private void Advance(Movement movement, bool releaseJump)
        {
            if (releaseJump) ReleaseJump(movement);
            _stepIndex++;
            _elapsedMs = 0f;
            _elapsedTicks = 0;
            _wallJumpTriggered = false;
            if (_stepIndex >= _steps.Count)
                Finish(movement);
        }

        private void Fail(Movement movement, string reason)
        {
            int number = _stepIndex + 1;
            SetDirection(movement, 0);
            Abort(reason + " at step " + number + " in " + _macroName);
        }

        private void Finish(Movement movement)
        {
            SetDirection(movement, 0);
            if (_requireCourseFinish)
            {
                _finishWaitTicks++;
                Status = "Timeline complete; waiting for course finish: " + _macroName;
                if (_finishWaitTicks < 250) return;
                Fail(movement, "Course finish not detected");
                return;
            }
            string name = _macroName;
            _steps = null;
            _stepIndex = -1;
            Status = "Complete: " + name;
            if (!Quiet) TasPlugin.Log.LogInfo(Status);
            if (AttemptEnded != null) AttemptEnded(true, "Timeline complete", -1, _runTicks);
        }

        private static List<MacroStep> Expand(List<MacroStep> source)
        {
            List<MacroStep> result = new List<MacroStep>();
            foreach (MacroStep step in source)
                if (step.enabled && step.type != MacroStepType.Bundle) result.Add(step.Copy());
            return result;
        }

        private static void NormalizeFrameTiming(List<MacroStep> steps)
        {
            int tickMs = Mathf.Max(1, Mathf.RoundToInt(Time.fixedDeltaTime * 1000f));
            foreach (MacroStep step in steps)
            {
                if (step.endCondition == ActionEndCondition.Duration)
                {
                    step.durationTicks = Mathf.Max(1, Mathf.RoundToInt(step.durationMs / (float)tickMs));
                    step.endCondition = ActionEndCondition.FixedTicks;
                }
                else if (step.endCondition == ActionEndCondition.FixedTicks)
                    step.durationTicks = Mathf.Max(1, step.durationTicks);
                step.durationMs = step.durationTicks * tickMs;
                step.timeoutTicks = step.timeoutTicks > 0
                    ? step.timeoutTicks
                    : Mathf.Max(1, Mathf.RoundToInt(step.timeoutMs / (float)tickMs));
                step.timeoutMs = step.timeoutTicks * tickMs;
            }
        }

        private static bool IsWallJumpAvailable(Movement movement)
        {
            return ReadBool(OnWallField, movement) || ReadFloat(WallCoyoteField, movement) > 0f;
        }

        // Presses and releases do exactly what Movement.Update does for a real one, including the buffer expiry timers
        // (a jump press stays buffered 0.12 s, 0.2 s mid-dash; a dash press 0.12 s; a release's jump cut 0.26 s).
        private static void PressJump(Movement movement)
        {
            if (JumpCutBufferField == null || JumpBufferField == null) return;
            SetField(JumpBufferField, movement, true);
            movement.CancelInvoke("cancelJumpBuffer");
            movement.CancelInvoke("cancelJumpCutBuffer");
            SetField(JumpCutBufferField, movement, false);
            bool dashing = string.Equals(ReadObject(CutsceneModeField, movement), "dash", StringComparison.OrdinalIgnoreCase);
            movement.Invoke("cancelJumpBuffer", dashing ? 0.2f : 0.12f);
        }

        private static void ReleaseJump(Movement movement)
        {
            SetField(JumpCutBufferField, movement, true);
            movement.CancelInvoke("cancelJumpCutBuffer");
            movement.Invoke("cancelJumpCutBuffer", 0.26f);
        }

        private static void PressDash(Movement movement)
        {
            SetField(DashBufferField, movement, true);
            movement.CancelInvoke("cancelDashBuffer");
            movement.Invoke("cancelDashBuffer", 0.12f);
        }

        private static void SetDirection(Movement movement, int direction)
        {
            movement.MoveAxis = new Vector2(Mathf.Clamp(direction, -1, 1), 0f);
        }

        private void CaptureProgress(Movement movement)
        {
            Vector3 position = movement.transform.position;
            Vector2 velocity = ReadVector(VelocityField, movement);
            // Keep the whole normal route, not just its opening. This trace is the
            // authoritative calibration/diagnostic input for the offline simulator.
            // Course 2 and later routes span many screens and routinely exceed
            // 512 fixed ticks. Retain enough authoritative samples to calibrate
            // the complete route, including its finish trigger. Quiet (solver) runs never persist it.
            if (!Quiet && _runTicks < 8192)
            {
                Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
                Vector2 bodyPosition = body == null ? (Vector2)position : body.position;
                _earlyTrace.Add(string.Join(",", new[] {
                    _runTicks.ToString(CultureInfo.InvariantCulture),
                    position.x.ToString("0.###", CultureInfo.InvariantCulture),
                    position.y.ToString("0.###", CultureInfo.InvariantCulture),
                    bodyPosition.x.ToString("0.###", CultureInfo.InvariantCulture),
                    bodyPosition.y.ToString("0.###", CultureInfo.InvariantCulture),
                    velocity.x.ToString("0.###", CultureInfo.InvariantCulture),
                    velocity.y.ToString("0.###", CultureInfo.InvariantCulture)
                }));
            }
            if (!_hasProgressSample)
            {
                _hasProgressSample = true;
                _startStateFingerprint = string.Join("|", new[] {
                    position.x.ToString("0.###", CultureInfo.InvariantCulture),
                    position.y.ToString("0.###", CultureInfo.InvariantCulture),
                    velocity.x.ToString("0.###", CultureInfo.InvariantCulture),
                    velocity.y.ToString("0.###", CultureInfo.InvariantCulture),
                    ReadBool(OnGroundField, movement) ? "G1" : "G0",
                    ReadBool(OnWallField, movement) ? "W1" : "W0",
                    ReadFloat(WallCoyoteField, movement) > 0f ? "WC1" : "WC0",
                    ReadBool(FacingRightField, movement) ? "F1" : "F0",
                    "WL" + ReadFloat(WallJumpLockField, movement).ToString("0.###", CultureInfo.InvariantCulture),
                    "WD" + ReadFloat(WallJumpDirectionField, movement).ToString("0.###", CultureInfo.InvariantCulture)
                });
            }
        }

        private static void NormalizeQuickRestartState(Movement movement)
        {
            SetDirection(movement, 0);
            SetField(VelocityField, movement, Vector2.zero);
            SetField(MomentumField, movement, Vector2.zero);
            SetField(MovingPlatformVelocityField, movement, Vector2.zero);
            SetField(LastAppliedMomentumField, movement, 0f);
            SetField(OnWallField, movement, false);
            SetField(WallCoyoteField, movement, 0f);
            SetField(WallJumpLockField, movement, -1f);
            SetField(WallJumpDirectionField, movement, 0f);
            SetField(JumpBufferField, movement, false);
            SetField(JumpCutBufferField, movement, false);
            SetField(DashBufferField, movement, false);
            SetField(DashActiveField, movement, false);

            Component body = movement.GetComponent("Rigidbody2D");
            if (body == null) return;
            Rigidbody2D rb = body as Rigidbody2D;
            if (rb != null) { rb.linearVelocity = Vector2.zero; rb.velocity = Vector2.zero; rb.angularVelocity = 0f; }
        }

        private void PinSynchronizationPosition(Movement movement)
        {
            if (!_hasSynchronizationPosition || movement == null) return;
            movement.transform.position = _synchronizationPosition;
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            if (body != null) body.position = _synchronizationPosition;
            Physics2D.SyncTransforms();
        }



        private static bool ReadBool(FieldInfo field, object instance)
        {
            return field != null && field.GetValue(instance) is bool && (bool)field.GetValue(instance);
        }

        private static float ReadFloat(FieldInfo field, object instance)
        {
            if (field == null) return 0f;
            object value = field.GetValue(instance);
            return value is float ? (float)value : 0f;
        }

        private static Vector2 ReadVector(FieldInfo field, object instance)
        {
            if (field == null) return Vector2.zero;
            object value = field.GetValue(instance);
            return value is Vector2 ? (Vector2)value : Vector2.zero;
        }

        private static string ReadObject(FieldInfo field, object instance)
        {
            object value = field == null ? null : field.GetValue(instance);
            return value == null ? "" : value.ToString();
        }

        private static void SetField(FieldInfo field, object instance, object value)
        {
            if (field != null) field.SetValue(instance, value);
        }

        private bool ShouldEnd(Movement movement, MacroStep step)
        {
            return step.endCondition == ActionEndCondition.FixedTicks
                ? _elapsedTicks >= Mathf.Max(1, step.durationTicks)
                : _elapsedMs >= Mathf.Max(0, step.durationMs);
        }

    }
}
