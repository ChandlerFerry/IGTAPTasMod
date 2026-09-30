using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    internal sealed class MacroEngine
    {
        // Movement's private fields the engine reads and writes every tick, as typed refs (FieldInfo.GetValue/SetValue box);
        // its public ones it uses directly.
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

        // Left out of CaptureState (FieldState.For): a resume always brings the steps it plays.
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
        // A step's pause (MacroStep.pauseFrames): physics ticks still to run before it opens, its length in frames, and the
        // Update frames of its tick before it opens.
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
            if (_turnArmed && WaitingForDashInput(movement))
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
            if (movement.cutsceneMode != Movement.cutsceneModes.dash || movement.framesSinceLastDash != 0f) return;
            _turnArmed = WaitingForDashInput(movement);
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
            /// <summary>The game time of the frame that crosses a physics tick: Time.deltaTime of the frame whose coroutines resume after it.</summary>
            public float CrossStep { get { return _crossStep; } }
            readonly float _prevStep, _crossStep, _frozenStep;

            /// <summary>Frame k's Time.deltaTime (the crossing frame's, then 1/fps each; the tick's frames add up to 0.02). A
            /// freeze's or pause's repeats of a frame run at 0: timeScale is 0 until it lifts.</summary>
            public float Step(int k) { return k == 0 ? _updateCross : _prevStep; }

            /// <summary>
            /// The crossing frame's Time.deltaTime as 1x reads it. 1x's ticks come 0.0199999929 s apart on the fixed clock, not
            /// 0.02f, so that frame advances 0.00214285006 s (a 280 fps probe's frame times; clone pointers and spawn timers
            /// then match 1x to the bit), 6.6e-9 short of <see cref="CrossStep"/>. Other rates keep CrossStep.
            /// </summary>
            readonly float _updateCross;

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
                _updateCross = fps == PlaybackFrameRate ? BitConverter.Int32BitsToSingle(0x3b0c6f0f) : _crossStep;
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
            ResumeOwned(state, Expand(remaining), elapsedTicks);
        }

        /// <summary>Resume on a caller-owned, already-filtered step list: skips Expand's per-step Copy (its
        /// Guid.NewGuid id is never read by the engine). Exact where the list holds only enabled non-Bundle steps
        /// already, so Expand's filter is a no-op and Copy's clone carries no state beyond the id.</summary>
        public void ResumeOwned(object[] state, List<MacroStep> steps, int elapsedTicks)
        {
            FieldState.Restore(this, state);
            _steps = steps;
            _stepIndex = 0;
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

        /// <summary>
        /// After each physics tick: the frames to hold the pause menu open when a step's pause falls due on this tick, else 0,
        /// and in <paramref name="afterFrames"/> the tick's Update frames that run before it opens (MacroStep.pauseFrame).
        /// 1x playback opens the menu for them; turbo and the offline engine run that many Update frames at the blend the
        /// pause holds (TurboRunner.Pause, Simulation's InterpolatedFrames).
        /// </summary>
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

        /// <summary>
        /// The Update frames of a tick's frame grid (<paramref name="frameCount"/> of them) each one runs again: the dash
        /// freeze's <paramref name="frozen"/> on the first frame, and a pause of <paramref name="pauseFrames"/> after
        /// <paramref name="pauseAfter"/> frames. Opened in the tick (0) it holds the first frame, whose Update it is, so that
        /// frame runs pauseFrames times in all (a freeze there overlaps it: the longer counts). Opened after frame k's Update
        /// it holds frame k's blend for pauseFrames more Updates (past the tick's last frame: the last).
        /// </summary>
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

        /// <summary>
        /// Every <see cref="RewardRefreshTicks"/> physics ticks of a run: the courses' UpdateReward. The game repeats it every 5 s
        /// from a random phase (courseScript.Start's InvokeRepeating), and a course's clones pay its reward as last updated; a
        /// Full-TAS run cancels that timer and its host (the plugin, the offline engine) runs it here, on one phase for 1x, turbo
        /// and the engine.
        /// </summary>
        public Action RefreshRewards;
        public const int RewardRefreshTicks = 250;

        public void CountPhysicsTick()
        {
            if (!IsRunning || !(_runTicks > 0 || _countEveryPhysicsTick)) return;
            _physicsTicks++;
            if (_physicsTicks % RewardRefreshTicks == 0) RefreshRewards?.Invoke();
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

        private static bool IsWallJumpAvailable(Movement movement)
        {
            return movement.OnWall || WallCoyoteFrames(movement) > 0f;
        }

        public int Clock;
        public int LastJumpPress = Never, LastJumpRelease = Never, LastDashPress = Never;
        const int Never = -1000000;
        public int JumpPresses, DashPresses;
        public bool JumpHeld;

        private void PressJump(Movement movement)
        {
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
            JumpHeld = false;
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

        /// <summary>
        /// FloatingOrigin moved every root object back by <paramref name="by"/> (MoveRootTransforms): the start pin is a scene
        /// position, so it moves with the world. Left in place, a rebase during the stable-tick wait (a route whose course
        /// start lies past Threshold from the origin the replay left) pins the player where the course was, over nothing: it
        /// never lands, and each frame rebases again.
        /// </summary>
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
