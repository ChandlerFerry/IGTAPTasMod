using System;
using System.Collections.Generic;
using System.Reflection;
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
        private static readonly FieldInfo WaitingForDashInputField = AccessTools.Field(typeof(Movement), "WaitingForDashInput");

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
        private float? _pendingTurnX, _pendingTurnY;
        private bool _turnArmed;
        private float? _turnX, _turnY;
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
        private float _authoritativeCompletionTime;

        public bool IsRunning { get { return _steps != null; } }
        public int StepIndex { get { return _stepIndex; } }
        public long RunTicks { get { return _runTicks; } }
        public long PhysicsTicks { get { return _physicsTicks; } }
        public MacroDefinition ActiveMacro { get; private set; }
        public string ActiveMacroName { get { return _macroName; } }
        public string Status { get; private set; } = "Ready";
        public float AuthoritativeCompletionTime { get { return _authoritativeCompletionTime; } }
        public event Action<bool, string> AttemptEnded;
        public bool Quiet;
        public bool InputsExhausted { get { return _steps != null && _stepIndex >= _steps.Count; } }
        public Vector2 TickStick { get { return IsRunning ? _tickStick : Vector2.zero; } }

        /// <summary>
        /// The stick the game reads (Movement.NormaliseInput). Between a dash's start and the next FixedUpdate — at 1x the
        /// dash's TimeStop frames — a dash started with the stick at rest (no sideways stick without the omnidash) waits for one
        /// (<c>WaitingForDashInput</c>, Movement.Update 817-834): a step's <c>dashTurnX</c>/<c>dashTurnY</c> is the stick a
        /// player holds then, so the dash turns to it. The omnidash takes its vertical too, unsnapped: a sideways 0.3-0.4 aims it
        /// 17-22° off vertical, which no dash pressed on a tick can (getPlayerControlledInput sets x to 0 there).
        /// </summary>
        public Vector2 StickFor(Movement movement)
        {
            Vector2 stick = TickStick;
            if (_turnArmed && ReadBool(WaitingForDashInputField, movement))
            {
                if (_turnX.HasValue) stick.x = _turnX.Value;
                if (_turnY.HasValue) stick.y = _turnY.Value;
            }
            return stick;
        }

        /// <summary>The stick held in this tick's dash freeze (a turn armed by <see cref="AfterFixedTick"/>), else null.</summary>
        public Vector2? FreezeStick
        {
            get { return _turnArmed ? new Vector2(_turnX ?? TickStick.x, _turnY ?? TickStick.y) : (Vector2?)null; }
        }

        /// <summary>
        /// After Movement.FixedUpdate: a pressed dash's turn arms once the dash has started this tick waiting for a stick, for
        /// the frames up to the next tick (<see cref="BeforeTick"/> disarms it).
        /// </summary>
        public void AfterFixedTick(Movement movement)
        {
            if (!IsRunning || (!_pendingTurnX.HasValue && !_pendingTurnY.HasValue)) return;
            if (!string.Equals(ReadObject(CutsceneModeField, movement), "dash", StringComparison.OrdinalIgnoreCase)
                || ReadFloat(FramesSinceLastDashField, movement) != 0f) return;
            _turnArmed = ReadBool(WaitingForDashInputField, movement);
            _turnX = _pendingTurnX;
            _turnY = _pendingTurnY;
            _pendingTurnX = _pendingTurnY = null;
        }

        /// <summary>
        /// 1x playback's frame rate. The player body's interpolation stays on, as the game ships it, and playback runs on a
        /// fixed clock at this rate (TasPlugin.FixedClockStep); turbo and the offline engine model the same frames
        /// (<see cref="FrameGrid"/>): each tick's Movement.Update frames at their interpolation blends, a dash freeze's frames,
        /// and FixedUpdate reading the last frame's blend.
        /// </summary>
        public const int PlaybackFrameRate = 280;

        /// <summary>
        /// The fixed clock's frames between two physics ticks (TasPlugin.FixedClockStep): half a frame after the tick, then one
        /// every 1/fps while inside it. Alphas are the interpolation blends (frame time since the tick / 0.02).
        /// FrozenFrames counts a TimeStop's frames as its loop counts the fixed clock's steps (TasPlugin.PatchWallClock): the
        /// frame before the tick's, then the crossing, then a frame each.
        /// </summary>
        public sealed class FrameGrid
        {
            public static readonly FrameGrid Playback = new FrameGrid(PlaybackFrameRate);

            public readonly float[] Alphas;
            public float LastAlpha { get { return Alphas[Alphas.Length - 1]; } }
            readonly float _prevStep, _crossStep, _frozenStep;

            public FrameGrid(int fps)
            {
                double frame = 1.0 / fps, offset = frame / 2, tick = 0.02f;
                var alphas = new List<float>();
                for (double since = offset; since < tick; since += frame) alphas.Add((float)(since / tick));
                Alphas = alphas.ToArray();
                double last = offset + (Alphas.Length - 1) * frame;
                _crossStep = (float)(tick + offset - last);
                _prevStep = Alphas.Length >= 2 ? (float)frame : _crossStep;
                _frozenStep = Alphas.Length >= 2 ? (float)frame : (float)tick;
            }

            public int FrozenFrames(float duration)
            {
                float i = _prevStep;
                int frames = 0;
                while (i <= duration) { frames++; i += frames == 1 ? _crossStep : _frozenStep; }
                return frames;
            }
        }

        public void Run(MacroDefinition macro, bool synchronizeStart = false, bool requireCourseFinish = false,
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
            ActiveMacro = macro;
            _stepIndex = 0;
            _elapsedMs = 0f;
            _elapsedTicks = 0;
            _wallJumpTriggered = false;
            _rawJumpHeld = false;
            _dashJumpFrame = 0;
            _springDashFrame = 0;
            _springTicks = 0;
            _lockArmed = false;
            _pendingTurnX = _pendingTurnY = null;
            _turnArmed = false;
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
            _authoritativeCompletionTime = 0f;
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
            _turnArmed = false;
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
                _pendingTurnX = step.dashTurnX;
                _pendingTurnY = step.dashTurnY;
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
            _steps = null;
            _stepIndex = -1;
            _elapsedMs = 0f;
            _elapsedTicks = 0;
            Status = reason;
            if (!Quiet) TasPlugin.Log.LogInfo(reason);
            if (wasRunning && AttemptEnded != null) AttemptEnded(false, reason);
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
            _authoritativeCompletionTime = authoritativeTime > 0f ? authoritativeTime : 0f;
            _steps = null;
            _stepIndex = -1;
            Status = reason + ": " + _macroName;
            if (!Quiet) TasPlugin.Log.LogInfo(Status);
            if (AttemptEnded != null) AttemptEnded(true, reason);
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
            if (AttemptEnded != null) AttemptEnded(true, "Timeline complete");
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
