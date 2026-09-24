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

        private List<MacroStep> _steps;
        private string _macroName;
        private int _stepIndex = -1;
        private float _elapsedMs;
        private int _elapsedTicks;
        private int _clearJumpCutTicks;
        private bool _wallJumpTriggered;
        private bool _rawJumpHeld;
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
        private int _furthestAction;
        private float _peakY;
        private float _finalX;
        private float _finalY;
        private float _minimumX;
        private float _maximumX;
        private bool _hasProgressSample;
        private string _startStateFingerprint;
        private float _authoritativeCompletionTime;
        private long _courseStartRunTick;
        private int _courseStartUnityFrame;
        private Vector2 _courseStartPosition;
        private readonly List<string> _earlyTrace = new List<string>();

        public bool IsRunning { get { return _steps != null; } }
        public bool IsWaitingForStable { get { return IsRunning && _waitingForStable; } }
        public int StepIndex { get { return _stepIndex; } }
        public long RunTicks { get { return _runTicks; } }
        public string ActiveMacroId { get; private set; }
        public string ActiveMacroName { get { return _macroName; } }
        public string Status { get; private set; } = "Ready";
        public int FurthestAction { get { return _furthestAction; } }
        public float PeakY { get { return _hasProgressSample ? _peakY : 0f; } }
        public float FinalX { get { return _hasProgressSample ? _finalX : 0f; } }
        public float FinalY { get { return _hasProgressSample ? _finalY : 0f; } }
        public float MinimumX { get { return _hasProgressSample ? _minimumX : 0f; } }
        public float MaximumX { get { return _hasProgressSample ? _maximumX : 0f; } }
        public string StartStateFingerprint { get { return _startStateFingerprint; } }
        public float AuthoritativeCompletionTime { get { return _authoritativeCompletionTime; } }
        public long CourseStartRunTick { get { return _courseStartRunTick; } }
        public int CourseStartUnityFrame { get { return _courseStartUnityFrame; } }
        public Vector2 CourseStartPosition { get { return _courseStartPosition; } }
        public string EarlyTrace { get { return string.Join(";", _earlyTrace.ToArray()); } }
        public float FirstJumpPositionOffsetX { get { return _firstJumpPositionOffsetX.GetValueOrDefault(); } }
        public float CourseStartPositionOffsetX { get { return _courseStartPositionOffsetX; } }
        public event Action<bool, string, int, long> AttemptEnded;

        public void Run(MacroDefinition macro, MacroLibrary library, bool synchronizeStart = false, bool requireCourseFinish = false,
            int startDelayTicks = 0, Vector2? synchronizationPosition = null, float? firstJumpPositionOffsetX = null)
        {
            if (macro == null || macro.steps == null || macro.steps.Count == 0)
            {
                Status = "Cannot run an empty macro";
                return;
            }
            _steps = Expand(macro.steps, library);
            NormalizeFrameTiming(_steps);
            _macroName = macro.name;
            ActiveMacroId = macro.id;
            _stepIndex = 0;
            _elapsedMs = 0f;
            _elapsedTicks = 0;
            _clearJumpCutTicks = 0;
            _wallJumpTriggered = false;
            _rawJumpHeld = false;
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
            _furthestAction = 0;
            _peakY = _minimumX = float.MaxValue;
            _maximumX = float.MinValue;
            _finalX = _finalY = 0f;
            _hasProgressSample = false;
            _startStateFingerprint = null;
            _authoritativeCompletionTime = 0f;
            _courseStartRunTick = -1;
            _courseStartUnityFrame = -1;
            _courseStartPosition = Vector2.zero;
            _earlyTrace.Clear();
            Status = synchronizeStart ? "Waiting for 3 stable ticks: " + macro.name : "Running: " + macro.name;
            TasPlugin.Log.LogInfo(Status);
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
            TasPlugin.Log.LogInfo(reason);
            if (wasRunning && AttemptEnded != null) AttemptEnded(false, reason, step, ticks);
        }

        public void ClearInjectedInput(Movement movement)
        {
            _clearJumpCutTicks = 0;
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
            TasPlugin.Log.LogInfo(Status);
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
            if (_clearJumpCutTicks > 0 && --_clearJumpCutTicks == 0)
                SetField(JumpCutBufferField, movement, false);

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
                SetField(DashBufferField, movement, true);

            _elapsedMs += Time.fixedDeltaTime * 1000f;
            _elapsedTicks++;
            if (ShouldEnd(movement, step))
                Advance(movement, step.type == MacroStepType.Jump);
            else if (step.endCondition != ActionEndCondition.Duration && step.endCondition != ActionEndCondition.FixedTicks
                && _elapsedTicks >= Mathf.Max(1, step.timeoutTicks))
                HandleTimeout(movement, step);
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
                {
                    SetField(JumpCutBufferField, movement, true);
                    _clearJumpCutTicks = 2;
                }
                if (step.rawDashPressed) SetField(DashBufferField, movement, true);
                _rawJumpHeld = step.rawJumpHeld;
            }
            _elapsedMs += Time.fixedDeltaTime * 1000f;
            _elapsedTicks++;
            if (_elapsedTicks >= Mathf.Max(1, step.durationTicks)) Advance(movement, false);
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
            if (releaseJump)
            {
                SetField(JumpCutBufferField, movement, true);
                _clearJumpCutTicks = 2;
            }
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
            TasPlugin.Log.LogInfo(Status);
            if (AttemptEnded != null) AttemptEnded(true, "Timeline complete", -1, _runTicks);
        }

        private static List<MacroStep> Expand(List<MacroStep> source, MacroLibrary library)
        {
            List<MacroStep> result = new List<MacroStep>();
            List<ParameterizedBundleDefinition> definitions = BuildBundleDefinitions(library);
            foreach (MacroStep step in source)
            {
                if (!step.enabled) continue;
                if (step.type != MacroStepType.Bundle)
                {
                    result.Add(step.Copy());
                    continue;
                }
                BundleDefinition found = library.bundles.Find(b => (!string.IsNullOrEmpty(step.bundleId) && b.id == step.bundleId) || b.name == step.bundleName);
                if (found == null) continue;
                BundleExpansionResult expansion = ParameterizedBundleExpander.Expand(found.id, definitions, step.bundleOverrides);
                if (!expansion.Success)
                {
                    TasPlugin.Log.LogError("Bundle expansion failed for " + found.name + ": " + string.Join("; ", expansion.Errors.ToArray()));
                    continue;
                }
                foreach (MacroStep child in expansion.Steps) if (child.enabled) result.Add(child);
            }
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

        private static List<ParameterizedBundleDefinition> BuildBundleDefinitions(MacroLibrary library)
        {
            List<ParameterizedBundleDefinition> result = new List<ParameterizedBundleDefinition>();
            foreach (BundleDefinition bundle in library.bundles)
            {
                ParameterizedBundleDefinition definition = new ParameterizedBundleDefinition {
                    id = bundle.id, name = bundle.name, version = bundle.version,
                    parameters = bundle.parameters ?? new List<BundleParameterDefinition>()
                };
                int actionCount = Mathf.Max(bundle.actions == null ? 0 : bundle.actions.Count, bundle.steps == null ? 0 : bundle.steps.Count);
                for (int i = 0; i < actionCount; i++)
                {
                    BundleActionTemplate template = bundle.actions != null && i < bundle.actions.Count
                        ? bundle.actions[i] : new BundleActionTemplate();
                    MacroStep step = bundle.steps != null && i < bundle.steps.Count ? bundle.steps[i] : template.step;
                    template.step = step;
                    if (step != null && step.type == MacroStepType.Bundle)
                    {
                        BundleDefinition nested = library.bundles.Find(b => (!string.IsNullOrEmpty(step.bundleId) && b.id == step.bundleId) || b.name == step.bundleName);
                        template.nestedBundleId = nested == null ? step.bundleId : nested.id;
                    }
                    definition.actions.Add(template);
                }
                result.Add(definition);
            }
            return result;
        }

        private static bool IsWallJumpAvailable(Movement movement)
        {
            return ReadBool(OnWallField, movement) || ReadFloat(WallCoyoteField, movement) > 0f;
        }

        private static void PressJump(Movement movement)
        {
            if (JumpCutBufferField == null || JumpBufferField == null) return;
            SetField(JumpCutBufferField, movement, false);
            SetField(JumpBufferField, movement, true);
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
            // the complete route, including its finish trigger.
            if (_runTicks < 8192)
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
                _peakY = position.y;
                _minimumX = _maximumX = position.x;
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
            _finalX = position.x;
            _finalY = position.y;
            _peakY = Mathf.Max(_peakY, position.y);
            _minimumX = Mathf.Min(_minimumX, position.x);
            _maximumX = Mathf.Max(_maximumX, position.x);
            int reached = _steps == null ? _furthestAction : Mathf.Clamp(_stepIndex + 1, 0, _steps.Count);
            _furthestAction = Math.Max(_furthestAction, reached);
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
            SetVectorMember(body, "linearVelocity", Vector2.zero);
            SetVectorMember(body, "velocity", Vector2.zero);
            SetFloatMember(body, "angularVelocity", 0f);
        }

        private void PinSynchronizationPosition(Movement movement)
        {
            if (!_hasSynchronizationPosition || movement == null) return;
            movement.transform.position = _synchronizationPosition;
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            if (body != null) body.position = _synchronizationPosition;
            Physics2D.SyncTransforms();
        }

        private static void SetVectorMember(object instance, string name, Vector2 value)
        {
            Type type = instance.GetType();
            FieldInfo field = AccessTools.Field(type, name);
            PropertyInfo property = field == null ? AccessTools.Property(type, name) : null;
            try
            {
                if (field != null && field.FieldType == typeof(Vector2)) field.SetValue(instance, value);
                else if (property != null && property.CanWrite && property.PropertyType == typeof(Vector2)) property.SetValue(instance, value, null);
            }
            catch { }
        }

        private static void SetFloatMember(object instance, string name, float value)
        {
            Type type = instance.GetType();
            FieldInfo field = AccessTools.Field(type, name);
            PropertyInfo property = field == null ? AccessTools.Property(type, name) : null;
            try
            {
                if (field != null && field.FieldType == typeof(float)) field.SetValue(instance, value);
                else if (property != null && property.CanWrite && property.PropertyType == typeof(float)) property.SetValue(instance, value, null);
            }
            catch { }
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
            Vector2 velocity = ReadVector(VelocityField, movement);
            Vector3 position = movement.transform.position;
            switch (step.endCondition)
            {
                case ActionEndCondition.FixedTicks: return _elapsedTicks >= Mathf.Max(1, step.durationTicks);
                case ActionEndCondition.GroundContact: return ReadBool(OnGroundField, movement);
                case ActionEndCondition.WallContact: return ReadBool(OnWallField, movement);
                case ActionEndCondition.WallCoyote: return ReadFloat(WallCoyoteField, movement) > 0f;
                case ActionEndCondition.JumpApex: return ReadBool(AtApexField, movement);
                case ActionEndCondition.Falling: return velocity.y < 0f;
                case ActionEndCondition.VelocityYAbove: return velocity.y >= step.conditionValue;
                case ActionEndCondition.VelocityYBelow: return velocity.y <= step.conditionValue;
                case ActionEndCondition.PositionXAbove: return position.x >= step.conditionValue;
                case ActionEndCondition.PositionXBelow: return position.x <= step.conditionValue;
                case ActionEndCondition.PositionYAbove: return position.y >= step.conditionValue;
                case ActionEndCondition.PositionYBelow: return position.y <= step.conditionValue;
                case ActionEndCondition.DashReady: return ReadFloat(DashCooldownField, movement) < 0f && ReadInt(AirDashesField, movement) > 0;
                case ActionEndCondition.JumpReady: return ReadBool(OnGroundField, movement) || ReadFloat(CoyoteField, movement) > 0f || ReadInt(AirJumpsField, movement) > 0;
                default: return _elapsedMs >= Mathf.Max(0, step.durationMs);
            }
        }

        private void HandleTimeout(Movement movement, MacroStep step)
        {
            if (step.timeoutPolicy == ActionTimeoutPolicy.ContinueToNextAction)
                Advance(movement, step.type == MacroStepType.Jump || step.type == MacroStepType.WallJump);
            else if (step.timeoutPolicy == ActionTimeoutPolicy.RetryAction)
            {
                _elapsedMs = 0f;
                _elapsedTicks = 0;
                _wallJumpTriggered = false;
            }
            else Fail(movement, "Condition timeout (" + step.endCondition + ")");
        }

        private static int ReadInt(FieldInfo field, object instance)
        {
            if (field == null) return 0;
            object value = field.GetValue(instance);
            return value is int ? (int)value : 0;
        }
    }
}
