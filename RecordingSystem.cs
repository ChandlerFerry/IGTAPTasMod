using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IGTAPTasMod
{
    internal sealed class InputRecorder
    {
        // Read every tick while recording: typed refs, as FieldInfo.GetValue boxes each read.
        private static readonly AccessTools.FieldRef<Movement, bool> JumpBuffer = AccessTools.FieldRefAccess<Movement, bool>("jumpBuffer");
        private static readonly AccessTools.FieldRef<Movement, bool> JumpCutBuffer = AccessTools.FieldRefAccess<Movement, bool>("jumpCutBuffer");
        private static readonly AccessTools.FieldRef<Movement, bool> DashBuffer = AccessTools.FieldRefAccess<Movement, bool>("dashBuffer");
        private static readonly AccessTools.FieldRef<Movement, float> WallCoyote = AccessTools.FieldRefAccess<Movement, float>("wallCoyoteFrames");
        private static readonly AccessTools.FieldRef<Movement, float> WallSide = AccessTools.FieldRefAccess<Movement, float>("wallCoyoteDir");
        private static readonly AccessTools.FieldRef<Movement, InputAction> JumpAction = AccessTools.FieldRefAccess<Movement, InputAction>("jumpAction");
        private static readonly AccessTools.FieldRef<Movement, InputAction> DashAction = AccessTools.FieldRefAccess<Movement, InputAction>("dashAction");
        private static readonly AccessTools.FieldRef<Movement, Vector2> DashDirection = AccessTools.FieldRefAccess<Movement, Vector2>("dashDirection");

        private bool _lastJump;
        private bool _lastCut;
        private bool _lastDash;
        private long _tick;

        public RecordingDefinition Current { get; private set; }
        public bool IsRecording { get { return Current != null; } }

        public void Start(string name)
        {
            Current = new RecordingDefinition { name = name, fixedDeltaMs = Time.fixedDeltaTime * 1000f };
            _lastJump = _lastCut = _lastDash = false;
            _tick = 0;
        }

        /// <summary>
        /// Recording that starts mid-run (a TAS re-record): the buffers the run's own presses left are no edge of this
        /// recording's, so they count as already seen.
        /// </summary>
        public void SeedEdges(Movement movement)
        {
            if (movement == null) return;
            _lastJump = JumpBuffer(movement);
            _lastCut = JumpCutBuffer(movement);
            _lastDash = DashBuffer(movement);
        }

        public RecordingDefinition Stop(string reason)
        {
            RecordingDefinition result = Current;
            Current = null;
            if (result == null) return null;
            result.stopReason = reason;
            Reprocess(result);
            return result;
        }

        public void MarkCourseStart()
        {
            if (Current != null) Current.courseStartTick = _tick;
        }

        public void MarkCourseFinish(float courseTimeSeconds)
        {
            if (Current != null && courseTimeSeconds > 0f) Current.courseTimeSeconds = courseTimeSeconds;
        }

        public static void Reprocess(RecordingDefinition recording)
        {
            if (recording == null) return;
            int removed = TrimLeadingIdle(recording.samples);
            if (recording.courseStartTick >= 0) recording.courseStartTick = Math.Max(0, recording.courseStartTick - removed);
        }

        private static int TrimLeadingIdle(List<RawTickSample> samples)
        {
            if (samples == null || samples.Count == 0) return 0;
            int firstInput = 0;
            while (firstInput < samples.Count)
            {
                RawTickSample sample = samples[firstInput];
                if (Mathf.Abs(sample.moveX) > 0.25f || Mathf.Abs(sample.moveY) > 0.25f
                    || sample.jumpPressed || sample.jumpHeld || sample.dashPressed || sample.dashHeld) break;
                firstInput++;
            }
            if (firstInput >= samples.Count) { int count = samples.Count; samples.Clear(); return count; }
            if (firstInput > 0) samples.RemoveRange(0, firstInput);
            for (int i = 0; i < samples.Count; i++) samples[i].tick = i;
            return firstInput;
        }

        public void FixedTick(Movement movement)
        {
            if (!IsRecording || movement == null) return;
            bool jump = JumpBuffer(movement);
            bool cut = JumpCutBuffer(movement);
            bool dash = DashBuffer(movement);
            InputAction jumpAction = JumpAction(movement);
            InputAction dashAction = DashAction(movement);
            Vector2 origin = TasPlugin.CurrentOrigin();
            Vector3 position = movement.transform.position;
            Current.samples.Add(new RawTickSample {
                tick = _tick++,
                moveX = movement.MoveAxis.x, moveY = movement.MoveAxis.y,
                jumpPressed = jump && !_lastJump, jumpHeld = jumpAction != null ? jumpAction.IsPressed() : jump && !cut,
                jumpReleased = cut && !_lastCut, dashPressed = dash && !_lastDash,
                dashHeld = dashAction != null ? dashAction.IsPressed() : dash,
                positionX = position.x, positionY = position.y,
                originX = origin.x, originY = origin.y,
                onGround = movement.onGround, onWall = movement.OnWall,
                wallCoyoteFrames = WallCoyote(movement), wallSide = WallSide(movement)
            });
            _lastJump = jump;
            _lastCut = cut;
            _lastDash = dash;
        }

        public void DashFrame(Movement movement)
        {
            if (!IsRecording || movement == null || Current.samples.Count == 0) return;
            bool jump = JumpBuffer(movement);
            RawTickSample dash = Current.samples[Current.samples.Count - 1];
            if (jump && !_lastJump && dash.dashPressed && dash.dashJumpFrame == 0)
                dash.dashJumpFrame = Math.Max(1, (int)movement.framesSinceLastDash);
            // The dash's first frame after its start: facing is still the way it started, dashDirection is the way the stick
            // held in its freeze turned it (a dash started with the stick at rest waits for one).
            if (dash.dashPressed && !dash.dashTurnX.HasValue && !dash.dashTurnY.HasValue && movement.framesSinceLastDash == 1f)
            {
                Vector2 turned = DashDirection(movement);
                if (turned.y != 0f && dash.moveX == 0f && dash.moveY == 0f)
                {
                    // An omnidash aimed in its freeze: dashDirection is the normalized (x, ±1) NormaliseInput passed, so x is
                    // back as x / |y| and the vertical is full tilt, as NormaliseInput snaps it.
                    dash.dashTurnX = turned.x / Mathf.Abs(turned.y);
                    dash.dashTurnY = Mathf.Sign(turned.y);
                }
                else if (turned.y == 0f && turned.x != 0f && Mathf.Sign(turned.x) != (movement.facingRight ? 1f : -1f))
                    dash.dashTurnX = Mathf.Sign(turned.x);
            }
            _lastJump = jump;
        }
    }

    internal static class ExactInputCompressor
    {
        public static List<MacroStep> Compress(List<RawTickSample> samples)
        {
            List<MacroStep> result = new List<MacroStep>();
            if (samples == null || samples.Count == 0) return result;
            int start = 0;
            while (start < samples.Count)
            {
                RawTickSample first = samples[start];
                int end = start + 1;
                while (end < samples.Count && SameInput(first, samples[end])) end++;
                int frames = end - start;
                result.Add(new MacroStep {
                    label = Label(first), type = MacroStepType.RawInput,
                    direction = Direction(first.moveX),
                    durationTicks = frames,
                    rawMoveX = first.moveX, rawMoveY = first.moveY,
                    rawJumpPressed = first.jumpPressed, rawJumpHeld = first.jumpHeld,
                    rawJumpReleased = first.jumpReleased,
                    rawDashPressed = first.dashPressed, rawDashHeld = first.dashHeld,
                    dashJumpFrame = first.dashPressed ? first.dashJumpFrame : 0,
                    dashTurnX = first.dashPressed ? first.dashTurnX : null,
                    dashTurnY = first.dashPressed ? first.dashTurnY : null
                });
                start = end;
            }
            return result;
        }

        private static bool SameInput(RawTickSample left, RawTickSample right)
        {
            return Mathf.Approximately(left.moveX, right.moveX)
                && Mathf.Approximately(left.moveY, right.moveY)
                && left.jumpPressed == right.jumpPressed
                && left.jumpHeld == right.jumpHeld
                && left.jumpReleased == right.jumpReleased
                && left.dashPressed == right.dashPressed
                && left.dashHeld == right.dashHeld
                && left.dashJumpFrame == right.dashJumpFrame
                && left.dashTurnX == right.dashTurnX
                && left.dashTurnY == right.dashTurnY;
        }

        private static string Label(RawTickSample sample)
        {
            string movement = sample.moveX < -0.01f ? "left" : sample.moveX > 0.01f ? "right" : "neutral";
            if (sample.jumpPressed && sample.jumpReleased) return "Exact " + movement + " jump press/release";
            if (sample.jumpPressed) return "Exact " + movement + " jump press";
            if (sample.jumpReleased) return "Exact " + movement + " jump release";
            if (sample.dashPressed) return "Exact " + movement + " dash press";
            if (sample.jumpHeld) return "Exact " + movement + " jump held";
            return "Exact " + movement;
        }

        private static int Direction(float value) { return value < -0.25f ? -1 : value > 0.25f ? 1 : 0; }
    }
}
