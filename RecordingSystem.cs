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
        private static readonly FieldInfo JumpBuffer = AccessTools.Field(typeof(Movement), "jumpBuffer");
        private static readonly FieldInfo JumpCutBuffer = AccessTools.Field(typeof(Movement), "jumpCutBuffer");
        private static readonly FieldInfo DashBuffer = AccessTools.Field(typeof(Movement), "dashBuffer");
        private static readonly FieldInfo OnGround = AccessTools.Field(typeof(Movement), "onGround");
        private static readonly FieldInfo OnWall = AccessTools.Field(typeof(Movement), "OnWall");
        private static readonly FieldInfo WallCoyote = AccessTools.Field(typeof(Movement), "wallCoyoteFrames");
        private static readonly FieldInfo WallSide = AccessTools.Field(typeof(Movement), "wallCoyoteDir");
        private static readonly FieldInfo JumpAction = AccessTools.Field(typeof(Movement), "jumpAction");
        private static readonly FieldInfo DashAction = AccessTools.Field(typeof(Movement), "dashAction");
        private static readonly FieldInfo FramesSinceLastDash = AccessTools.Field(typeof(Movement), "framesSinceLastDash");
        private static readonly FieldInfo DashDirection = AccessTools.Field(typeof(Movement), "dashDirection");
        private static readonly FieldInfo FacingRight = AccessTools.Field(typeof(Movement), "facingRight");

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
            recording.semanticSteps = SemanticCompressor.Compress(recording.samples, recording.fixedDeltaMs);
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
            bool jump = Read<bool>(JumpBuffer, movement);
            bool cut = Read<bool>(JumpCutBuffer, movement);
            bool dash = Read<bool>(DashBuffer, movement);
            InputAction jumpAction = Read<InputAction>(JumpAction, movement);
            InputAction dashAction = Read<InputAction>(DashAction, movement);
            Vector3 position = movement.transform.position;
            Current.samples.Add(new RawTickSample {
                tick = _tick++,
                moveX = movement.MoveAxis.x, moveY = movement.MoveAxis.y,
                jumpPressed = jump && !_lastJump, jumpHeld = jumpAction != null ? jumpAction.IsPressed() : jump && !cut,
                jumpReleased = cut && !_lastCut, dashPressed = dash && !_lastDash,
                dashHeld = dashAction != null ? dashAction.IsPressed() : dash,
                positionX = position.x, positionY = position.y,
                originX = TasPlugin.CurrentOrigin().x, originY = TasPlugin.CurrentOrigin().y,
                onGround = Read<bool>(OnGround, movement), onWall = Read<bool>(OnWall, movement),
                wallCoyoteFrames = Read<float>(WallCoyote, movement), wallSide = Read<float>(WallSide, movement)
            });
            _lastJump = jump;
            _lastCut = cut;
            _lastDash = dash;
        }

        public void DashFrame(Movement movement)
        {
            if (!IsRecording || movement == null || Current.samples.Count == 0) return;
            bool jump = Read<bool>(JumpBuffer, movement);
            RawTickSample dash = Current.samples[Current.samples.Count - 1];
            if (jump && !_lastJump && dash.dashPressed && dash.dashJumpFrame == 0)
                dash.dashJumpFrame = Math.Max(1, (int)Read<float>(FramesSinceLastDash, movement));
            // The dash's first frame after its start: facing is still the way it started, dashDirection is the way the stick
            // held in its freeze turned it (a dash started with the stick at rest waits for one).
            if (dash.dashPressed && !dash.dashTurnX.HasValue && !dash.dashTurnY.HasValue && Read<float>(FramesSinceLastDash, movement) == 1f)
            {
                Vector2 turned = Read<Vector2>(DashDirection, movement);
                if (turned.y != 0f && dash.moveX == 0f && dash.moveY == 0f)
                {
                    // An omnidash aimed in its freeze: dashDirection is the normalized (x, ±1) NormaliseInput passed, so x is
                    // back as x / |y| and the vertical is full tilt, as NormaliseInput snaps it.
                    dash.dashTurnX = turned.x / Mathf.Abs(turned.y);
                    dash.dashTurnY = Mathf.Sign(turned.y);
                }
                else if (turned.y == 0f && turned.x != 0f && Mathf.Sign(turned.x) != (Read<bool>(FacingRight, movement) ? 1f : -1f))
                    dash.dashTurnX = Mathf.Sign(turned.x);
            }
            _lastJump = jump;
        }

        private static T Read<T>(FieldInfo field, object instance)
        {
            if (field == null) return default(T);
            object value = field.GetValue(instance);
            return value is T ? (T)value : default(T);
        }
    }

    internal static class SemanticCompressor
    {
        public static List<MacroStep> Compress(List<RawTickSample> samples, float fixedDeltaMs)
        {
            List<MacroStep> result = new List<MacroStep>();
            if (samples == null || samples.Count == 0) return result;
            float tickMs = fixedDeltaMs > 0f ? fixedDeltaMs : 20f;
            int i = 0;
            while (i < samples.Count)
            {
                RawTickSample sample = samples[i];
                int direction = Direction(sample.moveX);
                if (sample.jumpPressed)
                {
                    int end = FindJumpEndExclusive(samples, i);
                    bool directWall = sample.onWall || sample.wallCoyoteFrames > 0f;
                    bool wall = directWall || HasNearbyWall(samples, i);
                    int outputDirection = direction;
                    if (wall)
                    {
                        int wallDirection = Direction(sample.wallSide);
                        if (wallDirection != 0) outputDirection = -wallDirection;
                    }
                    result.Add(new MacroStep {
                        label = wall ? "Recorded walljump" : "Recorded jump",
                        type = wall ? MacroStepType.WallJump : MacroStepType.Jump,
                        direction = outputDirection, wallJumpHoldDirection = direction,
                        durationTicks = Math.Max(1, end - i),
                        durationMs = TicksToMs(Math.Max(1, end - i), tickMs),
                        timeoutTicks = Math.Max(1, Mathf.RoundToInt(1200f / tickMs)), timeoutMs = 1200,
                        endCondition = ActionEndCondition.FixedTicks
                    });
                    i = end;
                    continue;
                }
                if (sample.dashPressed)
                {
                    result.Add(new MacroStep { label = "Recorded dash", type = MacroStepType.Dash,
                        direction = direction, durationTicks = 1, durationMs = TicksToMs(1, tickMs),
                        endCondition = ActionEndCondition.FixedTicks });
                    i++;
                    continue;
                }
                int next = i + 1;
                while (next < samples.Count && !samples[next].jumpPressed && !samples[next].dashPressed
                    && Direction(samples[next].moveX) == direction) next++;
                result.Add(new MacroStep { label = direction == 0 ? "Recorded wait" : "Recorded move",
                    type = direction == 0 ? MacroStepType.Wait : MacroStepType.Move,
                    direction = direction, durationTicks = Math.Max(1, next - i),
                    durationMs = TicksToMs(next - i, tickMs), endCondition = ActionEndCondition.FixedTicks });
                i = next;
            }
            return MergeAdjacent(result);
        }

        private static int FindJumpEndExclusive(List<RawTickSample> samples, int start)
        {
            int initialDirection = Direction(samples[start].moveX);
            int limit = Math.Min(samples.Count, start + 21);
            for (int i = start + 1; i < limit; i++)
            {
                RawTickSample sample = samples[i];
                if (sample.jumpPressed || sample.dashPressed || sample.jumpReleased
                    || Direction(sample.moveX) != initialDirection)
                    return i;
            }
            return limit;
        }

        private static bool HasNearbyWall(List<RawTickSample> samples, int start)
        {
            int end = Math.Min(samples.Count - 1, start + 3);
            for (int i = start; i <= end; i++)
                if (samples[i].onWall || samples[i].wallCoyoteFrames > 0f) return true;
            return false;
        }

        private static List<MacroStep> MergeAdjacent(List<MacroStep> source)
        {
            List<MacroStep> result = new List<MacroStep>();
            foreach (MacroStep step in source)
            {
                MacroStep previous = result.Count == 0 ? null : result[result.Count - 1];
                if (previous != null && previous.type == step.type && previous.direction == step.direction
                    && (step.type == MacroStepType.Move || step.type == MacroStepType.Wait))
                {
                    previous.durationTicks += step.durationTicks;
                    previous.durationMs += step.durationMs;
                }
                else result.Add(step);
            }
            return result;
        }

        private static int Direction(float value) { return value < -0.25f ? -1 : value > 0.25f ? 1 : 0; }
        private static int TicksToMs(int ticks, float tickMs) { return Math.Max(1, Mathf.RoundToInt(ticks * tickMs)); }
    }

    internal static class ExactInputCompressor
    {
        public static List<MacroStep> Compress(List<RawTickSample> samples, float fixedDeltaMs)
        {
            List<MacroStep> result = new List<MacroStep>();
            if (samples == null || samples.Count == 0) return result;
            float tickMs = fixedDeltaMs > 0f ? fixedDeltaMs : 20f;
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
                    durationMs = Math.Max(1, Mathf.RoundToInt(frames * tickMs)),
                    endCondition = ActionEndCondition.FixedTicks,
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
