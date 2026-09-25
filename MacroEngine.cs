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
        private static readonly FieldInfo DashFramesRemainingField = AccessTools.Field(typeof(Movement), "dashFramesRemaining");
        private static readonly FieldInfo FacingRightField = AccessTools.Field(typeof(Movement), "facingRight");
        private static readonly FieldInfo CutsceneModeField = AccessTools.Field(typeof(Movement), "cutsceneMode");
        private static readonly FieldInfo FramesSinceLastDashField = AccessTools.Field(typeof(Movement), "framesSinceLastDash");
        private static readonly FieldInfo CanHyperField = AccessTools.Field(typeof(Movement), "canHyper");
        private static readonly FieldInfo WallBouncePossibleField = AccessTools.Field(typeof(Movement), "wallBouncePossible");
        private static readonly FieldInfo HitDashResetterField = AccessTools.Field(typeof(Movement), "hitDashResetterWhileInCurrentDash");

        private List<MacroStep> _steps;
        private string _macroName;
        private int _stepIndex = -1;
        private float _elapsedMs;
        private int _elapsedTicks;
        private bool _wallJumpTriggered;
        private bool _rawJumpHeld;
        private int _dashJumpFrame;
        private Vector2 _dashJumpAxis;
        private int _springDashFrame;
        private int _springTicks;
        private bool _lockArmed;
        private float? _lockX, _lockY;
        private int _pressedStep = -1;
        private bool _tickLocked;
        private Vector2 _tickStick;
        private bool _waitingForStable;
        private int _stableTicks;
        private bool _hasSynchronizationPosition;
        private Vector2 _synchronizationPosition;
        private int _preRollTicksRemaining;
        private int _preRollTicksTotal;
        private long _runTicks;
        private long _physicsTicks;
        private bool _countEveryPhysicsTick;
        private int _quickRestartStep = -1;
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
        public long PhysicsTicks { get { return _physicsTicks; } }
        public string ActiveMacroId { get; private set; }
        public string ActiveMacroName { get { return _macroName; } }
        public string Status { get; private set; } = "Ready";
        public string StartStateFingerprint { get { return _startStateFingerprint; } }
        public float AuthoritativeCompletionTime { get { return _authoritativeCompletionTime; } }
        public long CourseStartRunTick { get { return _courseStartRunTick; } }
        public int CourseStartUnityFrame { get { return _courseStartUnityFrame; } }
        public Vector2 CourseStartPosition { get { return _courseStartPosition; } }
        public string EarlyTrace { get { return string.Join(";", _earlyTrace.ToArray()); } }
        public event Action<bool, string, int, long> AttemptEnded;
        public bool Quiet;
        public bool InputsExhausted { get { return _steps != null && _stepIndex >= _steps.Count; } }
        public Vector2 TickStick { get { return IsRunning ? _tickStick : Vector2.zero; } }

        public void Run(MacroDefinition macro, MacroLibrary library, bool synchronizeStart = false, bool requireCourseFinish = false,
            int startDelayTicks = 0, Vector2? synchronizationPosition = null)
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
            _springDashFrame = 0;
            _springTicks = 0;
            _lockArmed = false;
            _pressedStep = -1;
            _tickLocked = false;
            _tickStick = Vector2.zero;
            LastJumpPress = LastJumpRelease = LastDashPress = Never;
            JumpHeld = false;
            _waitingForStable = synchronizeStart;
            _stableTicks = 0;
            _hasSynchronizationPosition = synchronizationPosition.HasValue;
            _synchronizationPosition = synchronizationPosition.GetValueOrDefault();
            _preRollTicksRemaining = Mathf.Max(0, startDelayTicks);
            _preRollTicksTotal = _preRollTicksRemaining;
            _runTicks = 0;
            _physicsTicks = 0;
            _countEveryPhysicsTick = false;
            _quickRestartStep = -1;
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

        public void Resume(object[] state, List<MacroStep> remaining, int elapsedTicks)
        {
            FieldState.Restore(this, state);
            _steps = Expand(remaining);
            NormalizeFrameTiming(_steps);
            _stepIndex = 0;
            _elapsedMs = elapsedTicks * Time.fixedDeltaTime * 1000f;
            _elapsedTicks = elapsedTicks;
            _wallJumpTriggered = false;
            _finishWaitTicks = 0;
            _quickRestartStep = -1;
            _pressedStep = -1;
        }

        public bool TakeQuickRestart()
        {
            if (!IsRunning || _waitingForStable || _preRollTicksRemaining > 0 || _stepIndex < 0 || _stepIndex >= _steps.Count
                || _elapsedTicks != 0 || _quickRestartStep == _stepIndex) return false;
            MacroStep step = _steps[_stepIndex];
            if (step.type != MacroStepType.RawInput || !step.rawQuickRestart) return false;
            _quickRestartStep = _stepIndex;
            return true;
        }

        public void BeforeTick(Movement movement)
        {
            if (!IsRunning || _waitingForStable || _preRollTicksRemaining > 0) { _tickStick = Vector2.zero; return; }
            string mode = ReadObject(CutsceneModeField, movement);
            bool dash = string.Equals(mode, "dash", StringComparison.OrdinalIgnoreCase);
            bool dashEnds = dash && ReadBool(DashActiveField, movement) && ReadFloat(DashFramesRemainingField, movement) <= 0f;
            _tickLocked = !string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase) && !dashEnds;
            MacroStep due = _stepIndex >= 0 && _stepIndex < _steps.Count ? _steps[_stepIndex] : null;
            float nextY = due != null && due.type == MacroStepType.RawInput ? due.rawMoveY : 0f;
            bool dashJumped = false;
            if (dash && _dashJumpFrame > 0 && ReadFloat(FramesSinceLastDashField, movement) + 1f >= _dashJumpFrame)
            {
                _dashJumpFrame = 0;
                PressJump(movement);
                _rawJumpHeld = true;
                dashJumped = true;
            }
            if (_springDashFrame > 0 && string.Equals(mode, "spring", StringComparison.OrdinalIgnoreCase) && ++_springTicks >= _springDashFrame)
            {
                _springDashFrame = 0;
                PressDash(movement);
            }
            if (!_tickLocked) PressStep(movement);
            _tickStick = !_tickLocked ? new Vector2(0f, nextY)
                : _lockArmed ? new Vector2(_lockX ?? 0f, _lockY ?? nextY)
                : dashJumped ? _dashJumpAxis
                : new Vector2(0f, nextY);
        }

        public void WallTouch(Movement movement, bool touchedGround)
        {
            if (!IsRunning || touchedGround || !_tickLocked
                || !string.Equals(ReadObject(CutsceneModeField, movement), "dash", StringComparison.OrdinalIgnoreCase)) return;
            PressStep(movement);
        }

        private void PressStep(Movement movement)
        {
            if (_stepIndex < 0 || _stepIndex >= _steps.Count || _elapsedTicks != 0 || _pressedStep == _stepIndex) return;
            MacroStep step = _steps[_stepIndex];
            if (step.type != MacroStepType.RawInput) return;
            _pressedStep = _stepIndex;
            if (step.rawJumpPressed) PressJump(movement);
            if (step.rawJumpReleased || (_rawJumpHeld && !step.rawJumpHeld)) ReleaseJump(movement);
            if (step.rawDashPressed)
            {
                PressDash(movement);
                _dashJumpFrame = step.dashJumpFrame;
                _dashJumpAxis = ControllerAxis(step.rawMoveX, step.rawMoveY);
            }
            _rawJumpHeld = step.rawJumpHeld;
        }

        public void RebaseInputs()
        {
            _runTicks = 0;
            _countEveryPhysicsTick = true;
        }

        public void Discard()
        {
            _steps = null;
            _stepIndex = -1;
            _elapsedMs = 0f;
            _elapsedTicks = 0;
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
            JumpHeld = false;
            if (movement == null) return;
            SetDirection(movement, 0);
            SetField(JumpBufferField, movement, false);
            SetField(JumpCutBufferField, movement, false);
            SetField(DashBufferField, movement, false);
        }

        internal const string RewardFinishReason = "Reward collected";
        internal const string TransitionFinishReason = "Next course started";

        internal const string FullTasFinishReason = "Full-TAS goals reached";

        internal static bool IsFinishReason(string reason)
        {
            return reason == "Course finish detected" || reason == RewardFinishReason || reason == TransitionFinishReason
                || reason == FullTasFinishReason;
        }

        public void CountPhysicsTick()
        {
            if (IsRunning && (_runTicks > 0 || _countEveryPhysicsTick)) _physicsTicks++;
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



        public void FixedTick(Movement movement)
        {
            if (!IsRunning)
                return;
            if (_waitingForStable)
            {
                NormalizeQuickRestartState(movement);
                PinSynchronizationPosition(movement);
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
            movement.MoveAxis = ControllerAxis(step.rawMoveX, step.rawMoveY);
            _springDashFrame = entering ? step.springDashFrame : 0;
            _springTicks = 0;
            _lockArmed = entering && (step.lockMoveX.HasValue || step.lockMoveY.HasValue);
            _lockX = step.lockMoveX;
            _lockY = step.lockMoveY;
            if (entering) PressStep(movement);
            _elapsedMs += Time.fixedDeltaTime * 1000f;
            _elapsedTicks++;
            if (_elapsedTicks >= Mathf.Max(1, step.durationTicks)) Advance(movement, false);
        }


        private static int WallJumpHoldDirection(MacroStep step)
        {
            return step.wallJumpHoldDirection >= -1 && step.wallJumpHoldDirection <= 1
                ? step.wallJumpHoldDirection : step.direction;
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

        public int Clock;
        public int LastJumpPress = Never, LastJumpRelease = Never, LastDashPress = Never;
        const int Never = -1000000;
        public int JumpPresses, DashPresses;
        public bool JumpHeld;

        private void PressJump(Movement movement)
        {
            if (JumpCutBufferField == null || JumpBufferField == null) return;
            LastJumpPress = Clock;
            JumpPresses++;
            JumpHeld = true;
            SetField(JumpBufferField, movement, true);
            movement.CancelInvoke("cancelJumpBuffer");
            movement.CancelInvoke("cancelJumpCutBuffer");
            SetField(JumpCutBufferField, movement, false);
            bool dashing = string.Equals(ReadObject(CutsceneModeField, movement), "dash", StringComparison.OrdinalIgnoreCase);
            movement.Invoke("cancelJumpBuffer", dashing ? 0.2f : 0.12f);
        }

        private void ReleaseJump(Movement movement)
        {
            LastJumpRelease = Clock;
            JumpHeld = false;
            SetField(JumpCutBufferField, movement, true);
            movement.CancelInvoke("cancelJumpCutBuffer");
            movement.Invoke("cancelJumpCutBuffer", 0.26f);
        }

        private void PressDash(Movement movement)
        {
            LastDashPress = Clock;
            DashPresses++;
            SetField(DashBufferField, movement, true);
            movement.CancelInvoke("cancelDashBuffer");
            movement.Invoke("cancelDashBuffer", 0.12f);
        }

        private static void SetDirection(Movement movement, int direction)
        {
            movement.MoveAxis = new Vector2(Mathf.Clamp(direction, -1, 1), 0f);
        }

        public static Vector2 ControllerAxis(float x, float y)
        {
            if ((double)x > 0.8) x = 1f;
            else if ((double)x < -0.8) x = -1f;
            else if ((double)x > 0.4 && (double)Mathf.Abs(y) > 0.3) x = 1f;
            else if ((double)x < -0.4 && (double)Mathf.Abs(y) > 0.3) x = -1f;
            else if ((double)Mathf.Abs(x) < 0.3) x = 0f;
            y = (double)y > 0.4 ? 1f : (double)y < -0.4 ? -1f : 0f;
            return new Vector2(x, y);
        }

        private void CaptureProgress(Movement movement)
        {
            Vector3 position = movement.transform.position;
            Vector2 velocity = ReadVector(VelocityField, movement);
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
            SetField(DashCooldownField, movement, -1f);
            SetField(CanHyperField, movement, false);
            SetField(WallBouncePossibleField, movement, false);
            SetField(HitDashResetterField, movement, false);

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
