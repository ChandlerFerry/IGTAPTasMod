using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    internal sealed class MacroEngine
    {
        private static readonly AccessTools.FieldRef<Movement, bool> JumpBuffer = AccessTools.FieldRefAccess<Movement, bool>("jumpBuffer");
        private static readonly AccessTools.FieldRef<Movement, bool> JumpCutBuffer = AccessTools.FieldRefAccess<Movement, bool>("jumpCutBuffer");
        private static readonly AccessTools.FieldRef<Movement, bool> DashBuffer = AccessTools.FieldRefAccess<Movement, bool>("dashBuffer");
        private static readonly AccessTools.FieldRef<Movement, bool> IsDead = AccessTools.FieldRefAccess<Movement, bool>("isDead");
        private static readonly AccessTools.FieldRef<Movement, bool> CanHyper = AccessTools.FieldRefAccess<Movement, bool>("canHyper");
        private static readonly AccessTools.FieldRef<Movement, bool> WallBouncePossible = AccessTools.FieldRefAccess<Movement, bool>("wallBouncePossible");
        private static readonly AccessTools.FieldRef<Movement, bool> WaitingForDashInput = AccessTools.FieldRefAccess<Movement, bool>("WaitingForDashInput");
        private static readonly AccessTools.FieldRef<Movement, float> WallCoyoteFrames = AccessTools.FieldRefAccess<Movement, float>("wallCoyoteFrames");
        private static readonly AccessTools.FieldRef<Movement, float> DashFramesRemaining = AccessTools.FieldRefAccess<Movement, float>("dashFramesRemaining");
        private static readonly AccessTools.FieldRef<Movement, float> LastAppliedMomentum = AccessTools.FieldRefAccess<Movement, float>("lastAppliedMomentum");

        // Left out of CaptureState: a resume always brings the steps it plays.
        private List<MacroStep> _steps;
        private string _macroName;
        private int _stepIndex = -1;
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
        private int _pauseTicksLeft, _pauseFrames, _pauseFrame;
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
        private long _heldFrames;
        private bool _countEveryPhysicsTick;
        private int _quickRestartStep = -1;
        private bool _requireCourseFinish;
        private int _finishWaitTicks;
        private float _authoritativeCompletionTime;

        public bool IsRunning { get { return _steps != null; } }
        public int StepIndex { get { return _stepIndex; } }
        public long RunTicks { get { return _runTicks; } }
        public long PhysicsTicks { get { return _physicsTicks; } }
        public long HeldFrames { get { return _heldFrames; } }
        public MacroDefinition ActiveMacro { get; private set; }
        public string ActiveMacroName { get { return _macroName; } }
        public string Status { get; private set; } = "Ready";
        public float AuthoritativeCompletionTime { get { return _authoritativeCompletionTime; } }
        public event Action<bool, string> AttemptEnded;
        public bool Quiet;
        // Human class replay (RULES.md 54): jump press with both buttons down or a part-tilt stick ends the run.
        public bool HumanClass;
        public bool InputsExhausted { get { return _steps != null && _stepIndex >= _steps.Count; } }
        // Dash/spring ticks take no input (Movement 1596-1599): inputs wait through them; the 20-tick run-out excludes them.
        public bool TickLocked { get { return _tickLocked; } }
        public Vector2 TickStick { get { return IsRunning ? _tickStick : Vector2.zero; } }
        public bool TickStickFromLockY { get { return _tickLocked && _lockArmed && _lockY.HasValue; } }

        // A dash at rest waits for a stick in its freeze (Movement.Update 817-834); dashTurnX/Y is it (RULES.md exception 1).
        public Vector2 StickFor(Movement movement)
        {
            Vector2 stick = TickStick;
            if (_turnArmed && WaitingForDashInput(movement))
            {
                if (_turnX.HasValue) stick.x = _turnX.Value;
                if (_turnY.HasValue) stick.y = _turnY.Value;
            }
            return stick;
        }

        public Vector2? FreezeStick
        {
            get { return _turnArmed ? new Vector2(_turnX ?? TickStick.x, _turnY ?? TickStick.y) : (Vector2?)null; }
        }

        public void AfterFixedTick(Movement movement)
        {
            if (!IsRunning || (!_pendingTurnX.HasValue && !_pendingTurnY.HasValue)) return;
            if (movement.cutsceneMode != Movement.cutsceneModes.dash || movement.framesSinceLastDash != 0f) return;
            _turnArmed = WaitingForDashInput(movement);
            _turnX = _pendingTurnX;
            _turnY = _pendingTurnY;
            _pendingTurnX = _pendingTurnY = null;
        }

        public const int PlaybackFrameRate = 280;

        // Fixed-clock frames between ticks: restart each tick through the first, then run on across ticks (RULES.md 8).
        public sealed class FrameGrid
        {
            public static readonly FrameGrid Playback = new FrameGrid(PlaybackFrameRate);

            // 1x measured: ticks 0.0199999929 s apart, deltaTime 7e-9 s short; these steps make a 0.05 s freeze hold 13 frames.
            const double TickOnClock = 0.0199999929;
            const int ClockFrame280 = 0x3b6a0ebf, ClockCross280 = 0x3b6a0e9d, ClockRestart280 = 0x3b0c6f2a;

            public sealed class Span
            {
                public readonly float[] Alphas;
                public float LastAlpha { get { return Alphas[Alphas.Length - 1]; } }
                public readonly double First, Last;

                internal Span(double first, double frame, double tick)
                {
                    var alphas = new List<float>();
                    for (int k = 0; first + k * frame < tick; k++) alphas.Add((float)((first + k * frame) / tick));
                    Alphas = alphas.ToArray();
                    First = first;
                    Last = first + (Alphas.Length - 1) * frame;
                }
            }

            public readonly double Frame;
            readonly Span[] _cycle;
            readonly float _frameStep, _restartCross, _clockFrame, _clockRestart, _clockRestartFrozen;
            readonly float[] _cross, _clockCross, _clockFrozen;

            public Span Frames(long ticks) { return _cycle[Phase(ticks)]; }

            public static long NextTicks(long ticks) { return ticks >= 1 ? ticks + 1 : 0; }

            int Phase(long ticks) { return ticks <= 1 ? 0 : (int)((ticks - 1) % _cycle.Length); }

            public float Step(long ticks, int k) { return k > 0 ? _frameStep : ticks <= 1 ? _restartCross : _cross[Phase(ticks)]; }

            public FrameGrid(int fps)
            {
                Frame = 1.0 / fps;
                bool measured = fps == PlaybackFrameRate;
                double tick = 0.02f, clock = measured ? TickOnClock : tick;
                int tickUnits = 2 * fps, cycle = 50 / Gcd(fps, 50);
                _cycle = new Span[cycle];
                for (int p = 0; p < cycle; p++)
                {
                    int start = p * tickUnits, j = (start + 50) / 100;
                    _cycle[p] = new Span((50 + 100 * j - start) / (100.0 * fps), Frame, tick);
                }
                _frameStep = (float)Frame;
                _clockFrame = measured ? BitConverter.Int32BitsToSingle(ClockFrame280) : _frameStep;
                _cross = new float[cycle];
                _clockCross = new float[cycle];
                _clockFrozen = new float[cycle];
                for (int p = 0; p < cycle; p++)
                {
                    Span before = _cycle[(p + cycle - 1) % cycle], span = _cycle[p], after = _cycle[(p + 1) % cycle];
                    _cross[p] = (float)(clock + span.First - before.Last);
                    _clockCross[p] = measured ? BitConverter.Int32BitsToSingle(ClockCross280) : (float)(tick + span.First - before.Last);
                    _clockFrozen[p] = span.Alphas.Length >= 2 ? _clockFrame : (float)(tick + after.First - span.First);
                }
                Span restart = _cycle[0];
                _restartCross = (float)(clock + restart.First - restart.Last);
                _clockRestart = measured ? BitConverter.Int32BitsToSingle(ClockRestart280) : (float)(tick + restart.First - restart.Last);
                _clockRestartFrozen = restart.Alphas.Length >= 2 ? _clockFrame : (float)tick;
            }

            static int Gcd(int a, int b) { return b == 0 ? a : Gcd(b, a % b); }

            float ClockCross(long ticks) { return ticks <= 1 ? _clockRestart : _clockCross[Phase(ticks)]; }

            public int FrozenFrames(long ticks, float duration)
            {
                long before = ticks <= 1 ? 0 : ticks - 1;
                float i = Frames(before).Alphas.Length >= 2 ? _clockFrame : ClockCross(before);
                float cross = ClockCross(ticks), frozen = ticks <= 1 ? _clockRestartFrozen : _clockFrozen[Phase(ticks)];
                int frames = 0;
                while (i <= duration) { frames++; i += frames == 1 ? cross : frozen; }
                return frames;
            }
        }

        public long GridTicks { get { return IsRunning ? _physicsTicks : 0; } }

        public void Run(MacroDefinition macro, bool synchronizeStart = false, bool requireCourseFinish = false,
            int startDelayTicks = 0, Vector2? synchronizationPosition = null)
        {
            if (macro == null || macro.steps == null || macro.steps.Count == 0)
            {
                Status = "Cannot run an empty macro";
                return;
            }
            _steps = Expand(macro.steps);
            _macroName = macro.name;
            ActiveMacro = macro;
            _stepIndex = 0;
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
            _pauseFrames = 0;
            _tickLocked = false;
            _tickStick = Vector2.zero;
            LastJumpPress = LastJumpRelease = LastDashPress = Never;
            JumpHeld = false;
            JumpButtonsDown = 0;
            _waitingForStable = synchronizeStart;
            _stableTicks = 0;
            _hasSynchronizationPosition = synchronizationPosition.HasValue;
            _synchronizationPosition = synchronizationPosition.GetValueOrDefault();
            _preRollTicksRemaining = Mathf.Max(0, startDelayTicks);
            _preRollTicksTotal = _preRollTicksRemaining;
            _runTicks = 0;
            _physicsTicks = 0;
            _heldFrames = 0;
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
            ResumeOwned(state, Expand(remaining), elapsedTicks);
        }

        // Caller-owned list skips Expand's Copy: exact only if it holds enabled non-Bundle steps already (the id is never read).
        public void ResumeOwned(object[] state, List<MacroStep> steps, int elapsedTicks)
        {
            FieldState.Restore(this, state);
            bool atStep = elapsedTicks == 0 && _stepIndex >= 0 && _elapsedTicks == 0;
            bool pressed = atStep && _pressedStep == _stepIndex, restarted = atStep && _quickRestartStep == _stepIndex;
            _steps = steps;
            _stepIndex = 0;
            _elapsedTicks = elapsedTicks;
            _wallJumpTriggered = false;
            _finishWaitTicks = 0;
            _pressedStep = pressed ? 0 : -1;
            _quickRestartStep = restarted ? 0 : -1;
        }

        public void ResumeAt(object[] state, List<MacroStep> steps) { ResumeOwned(state, steps, 0); }

        public bool StartsInputNextTick
        {
            get { return IsRunning && _runTicks == 0 && _preRollTicksRemaining == 0 && (!_waitingForStable || _stableTicks >= 2); }
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
            Movement.cutsceneModes mode = movement.cutsceneMode;
            bool dash = mode == Movement.cutsceneModes.dash;
            bool dashEnds = dash && movement.dashActive && DashFramesRemaining(movement) <= 0f;
            _tickLocked = mode != Movement.cutsceneModes.none && !dashEnds;
            MacroStep due = _stepIndex >= 0 && _stepIndex < _steps.Count ? _steps[_stepIndex] : null;
            float nextY = due != null && due.type == MacroStepType.RawInput ? due.rawMoveY : 0f;
            bool dashJumped = false;
            if (dash && _dashJumpFrame > 0 && movement.framesSinceLastDash + 1f >= _dashJumpFrame)
            {
                _dashJumpFrame = 0;
                PressJump(movement);
                _rawJumpHeld = true;
                dashJumped = true;
            }
            if (_springDashFrame > 0 && mode == Movement.cutsceneModes.spring && ++_springTicks >= _springDashFrame)
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
            if (!IsRunning || touchedGround || !_tickLocked || movement.cutsceneMode != Movement.cutsceneModes.dash) return;
            PressStep(movement);
        }

        private void PressStep(Movement movement)
        {
            if (_stepIndex < 0 || _stepIndex >= _steps.Count || _elapsedTicks != 0 || _pressedStep == _stepIndex) return;
            MacroStep step = _steps[_stepIndex];
            if (step.type != MacroStepType.RawInput) return;
            _pressedStep = _stepIndex;
            if (step.pauseFrames > 0)
            {
                _pauseFrames = step.pauseFrames;
                _pauseTicksLeft = step.pauseTick;
                _pauseFrame = step.pauseFrame;
            }
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

        // A pause opens after the tick: 1x holds the menu; turbo and the engine run the same Update frames at the held blend.
        public int TakePause(out int afterFrames)
        {
            afterFrames = 0;
            if (!IsRunning || _pauseFrames <= 0) return 0;
            if (_pauseTicksLeft-- > 0) return 0;
            int frames = _pauseFrames;
            afterFrames = _pauseFrame;
            _pauseFrames = 0;
            return frames;
        }

        // Stands in for JiggleDropScript.OnBecameVisible (no rendered frame offline): camera on player, camSize 550, 16:9, orb 64.
        public static bool OrbOnCamera(Vector2 orb, Vector2 player)
        {
            const float halfHeight = 550f, halfWidth = halfHeight * 16f / 9f, reach = 64f;
            return Math.Abs(orb.x - player.x) <= halfWidth + reach && Math.Abs(orb.y - player.y) <= halfHeight + reach;
        }

        // Frame repeats: a dash freeze repeats the first frame, a pause the frame it opens on; overlapping holds count the longer.
        public static int FrameRepeats(int k, int frameCount, int frozen, int pauseFrames, int pauseAfter)
        {
            int repeats = k == 0 ? frozen : 0;
            if (pauseFrames <= 0) return repeats;
            if (pauseAfter <= 0) return k == 0 ? Math.Max(repeats, pauseFrames - 1) : repeats;
            return k == Math.Min(pauseAfter, frameCount) - 1 ? repeats + pauseFrames : repeats;
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
            _elapsedTicks = 0;
        }

        public void Abort(string reason = "Stopped")
        {
            bool wasRunning = IsRunning;
            _steps = null;
            _stepIndex = -1;
            _elapsedTicks = 0;
            Status = reason;
            if (!Quiet) TasPlugin.Log.LogInfo(reason);
            if (wasRunning && AttemptEnded != null) AttemptEnded(false, reason);
        }

        public void ClearInjectedInput(Movement movement)
        {
            _rawJumpHeld = false;
            JumpHeld = false;
            JumpButtonsDown = 0;
            if (movement == null) return;
            SetDirection(movement, 0);
            JumpBuffer(movement) = false;
            JumpCutBuffer(movement) = false;
            DashBuffer(movement) = false;
        }

        internal const string RewardFinishReason = "Reward collected";
        internal const string TransitionFinishReason = "Next course started";

        internal const string FullTasFinishReason = "Full-TAS goals reached";

        internal static bool IsFinishReason(string reason)
        {
            return reason == "Course finish detected" || reason == RewardFinishReason || reason == TransitionFinishReason
                || reason == FullTasFinishReason;
        }

        // Full-TAS reward refresh (RULES.md exception 10): the 5 s UpdateReward timer is off, hosts run it here on one phase.
        public Action RefreshRewards;
        public const int RewardRefreshTicks = 250;

        public void CountPhysicsTick()
        {
            if (!IsRunning || !(_runTicks > 0 || _countEveryPhysicsTick)) return;
            _physicsTicks++;
            if (_physicsTicks % RewardRefreshTicks == 0) RefreshRewards?.Invoke();
        }

        public void CountHeldFrames(int frames)
        {
            if (frames <= 0 || !IsRunning || !(_runTicks > 0 || _countEveryPhysicsTick)) return;
            _heldFrames += frames;
        }

        public static float LiveSeconds(long ticks, long heldFrames)
        {
            return (float)(ticks * 0.02 + heldFrames / (double)PlaybackFrameRate);
        }

        // Live time in 1/1400 s (a tick 28, a held frame 5) for exact ordering.
        public static long LiveUnits(long ticks, long heldFrames)
        {
            return ticks * (1400 / 50) + heldFrames * (1400 / PlaybackFrameRate);
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
                bool stable = !IsDead(movement) && movement.onGround && movement.cutsceneMode == Movement.cutsceneModes.none;
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

            _elapsedTicks++;
            if (ShouldEnd(movement, step))
                Advance(movement, step.type == MacroStepType.Jump);
        }

        private void RunWallJump(Movement movement, MacroStep step)
        {
            if (!_wallJumpTriggered)
            {
                SetDirection(movement, -step.direction);
                _elapsedTicks++;
                if (IsWallJumpAvailable(movement))
                {
                    PressJump(movement);
                    SetDirection(movement, WallJumpHoldDirection(step));
                    _wallJumpTriggered = true;
                    _elapsedTicks = 0;
                }
                else if (_elapsedTicks >= Mathf.Max(1, step.timeoutTicks))
                {
                    Fail(movement, "Walljump timeout");
                }
                return;
            }

            SetDirection(movement, WallJumpHoldDirection(step));
            _elapsedTicks++;
            if (ShouldEnd(movement, step))
                Advance(movement, true);
        }

        private void RunRawInput(Movement movement, MacroStep step, bool entering)
        {
            if (entering && HumanClass && (PartTilt(ControllerAxis(step.rawMoveX, step.rawMoveY).x) || PartTilt(step.lockMoveX)
                || PartTilt(step.lockMoveY) || PartTilt(step.dashTurnX) || PartTilt(step.dashTurnY)))
            {
                Fail(movement, "A part-tilt stick in a human class");
                return;
            }
            movement.MoveAxis = ControllerAxis(step.rawMoveX, step.rawMoveY);
            _springDashFrame = entering ? step.springDashFrame : 0;
            _springTicks = 0;
            _lockArmed = entering && (step.lockMoveX.HasValue || step.lockMoveY.HasValue);
            _lockX = step.lockMoveX;
            _lockY = step.lockMoveY;
            if (entering) PressStep(movement);
            _elapsedTicks++;
            if (_elapsedTicks >= Mathf.Max(1, step.durationTicks)) Advance(movement, false);
        }

        private static bool PartTilt(float? v)
        {
            return v.HasValue && v.Value != 0f && Mathf.Abs(v.Value) != 1f;
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
            _elapsedTicks = 0;
            _wallJumpTriggered = false;
            // The last step's input stands for its whole tick: FixedTick finishes on the next one (Finish puts the stick at rest).
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

        private static bool IsWallJumpAvailable(Movement movement)
        {
            return movement.OnWall || WallCoyoteFrames(movement) > 0f;
        }

        public int Clock;
        public int LastJumpPress = Never, LastJumpRelease = Never, LastDashPress = Never;
        const int Never = -1000000;
        public int JumpPresses, DashPresses;
        public int JumpReleases;
        public bool JumpHeld;
        public int JumpButtonsDown;

        private void PressJump(Movement movement)
        {
            if (JumpButtonsDown >= 2 && HumanClass)
            {
                Abort("A third jump press while both jump buttons are down at action " + (_stepIndex + 1));
                return;
            }
            JumpButtonsDown = Math.Min(2, JumpButtonsDown + 1);
            LastJumpPress = Clock;
            JumpPresses++;
            JumpHeld = true;
            JumpBuffer(movement) = true;
            movement.CancelInvoke("cancelJumpBuffer");
            movement.CancelInvoke("cancelJumpCutBuffer");
            JumpCutBuffer(movement) = false;
            bool dashing = movement.cutsceneMode == Movement.cutsceneModes.dash;
            movement.Invoke("cancelJumpBuffer", dashing ? 0.2f : 0.12f);
        }

        private void ReleaseJump(Movement movement)
        {
            LastJumpRelease = Clock;
            JumpReleases++;
            JumpHeld = false;
            JumpButtonsDown = 0;
            JumpCutBuffer(movement) = true;
            movement.CancelInvoke("cancelJumpCutBuffer");
            movement.Invoke("cancelJumpCutBuffer", 0.26f);
        }

        private void PressDash(Movement movement)
        {
            LastDashPress = Clock;
            DashPresses++;
            DashBuffer(movement) = true;
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
            movement.Velocity = Vector2.zero;
            movement.momentum = Vector2.zero;
            movement.movingPlatformVelocity = Vector2.zero;
            LastAppliedMomentum(movement) = 0f;
            movement.OnWall = false;
            WallCoyoteFrames(movement) = 0f;
            movement.wallJumpMovementLockRemaining = -1f;
            movement.wallJumpDirection = 0f;
            JumpBuffer(movement) = false;
            JumpCutBuffer(movement) = false;
            DashBuffer(movement) = false;
            movement.dashActive = false;
            movement.dashCooldown = -1f;
            CanHyper(movement) = false;
            WallBouncePossible(movement) = false;
            movement.hitDashResetterWhileInCurrentDash = false;

            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            if (body != null) { body.linearVelocity = Vector2.zero; body.angularVelocity = 0f; }
        }

        private void PinSynchronizationPosition(Movement movement)
        {
            if (!_hasSynchronizationPosition || movement == null) return;
            movement.transform.position = _synchronizationPosition;
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            if (body != null) body.position = _synchronizationPosition;
            Physics2D.SyncTransforms();
        }

        // FloatingOrigin moves the start pin with the world; left put, a rebase in the stable-tick wait repeats every frame.
        public void WorldMoved(Vector2 by)
        {
            if (_hasSynchronizationPosition) _synchronizationPosition -= by;
        }

        private bool ShouldEnd(Movement movement, MacroStep step)
        {
            return _elapsedTicks >= Mathf.Max(1, step.durationTicks);
        }

    }
}
