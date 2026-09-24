using System;
using System.Collections.Generic;

namespace IGTAPTasMod
{
    public enum MacroStepType { Move, Jump, Dash, Wait, WallJump, Bundle, RawInput }
    public enum InferenceConfidence { NotInferred, Low, Medium, High }
    public enum ActionEndCondition { Duration, FixedTicks, GroundContact, WallContact, WallCoyote, JumpApex, Falling, VelocityYAbove, VelocityYBelow, PositionXAbove, PositionXBelow, PositionYAbove, PositionYBelow, DashReady, JumpReady }
    public enum ActionTimeoutPolicy { AbortAttempt, ContinueToNextAction, RetryAction }

    [Serializable]
    public sealed class MacroStep
    {
        public string id = Guid.NewGuid().ToString("N");
        public bool enabled = true;
        public string label = "New action";
        public MacroStepType type = MacroStepType.Move;
        public int durationMs = 100;
        public int timeoutMs = 1200;
        public int direction;
        // Wall jumps have two independent controls: direction identifies the wall-to-wall
        // travel direction, while this is the horizontal input held after launch. 2 means
        // automatic behavior (hold the travel direction).
        public int wallJumpHoldDirection = 2;
        public string bundleId = "";
        public string bundleName = "";
        public Dictionary<string, string> bundleOverrides = new Dictionary<string, string>();
        public InferenceConfidence inferenceConfidence;
        public ActionEndCondition endCondition = ActionEndCondition.FixedTicks;
        public int durationTicks = 5;
        public int timeoutTicks = 60;
        public float conditionValue;
        public ActionTimeoutPolicy timeoutPolicy;
        // Exact recorded-input playback. RawInput is appended to the enum so existing
        // serialized numeric action types retain their meaning.
        public float rawMoveX;
        public float rawMoveY;
        public bool rawJumpPressed;
        public bool rawJumpHeld;
        public bool rawJumpReleased;
        public bool rawDashPressed;
        public bool rawDashHeld;

        public MacroStep Copy()
        {
            return new MacroStep { id = Guid.NewGuid().ToString("N"), enabled = enabled,
                label = label, type = type, durationMs = durationMs,
                timeoutMs = timeoutMs, direction = direction, wallJumpHoldDirection = wallJumpHoldDirection,
                bundleId = bundleId, bundleName = bundleName,
                bundleOverrides = new Dictionary<string, string>(bundleOverrides ?? new Dictionary<string, string>()),
                inferenceConfidence = inferenceConfidence, endCondition = endCondition,
                durationTicks = durationTicks, timeoutTicks = timeoutTicks,
                conditionValue = conditionValue, timeoutPolicy = timeoutPolicy,
                rawMoveX = rawMoveX, rawMoveY = rawMoveY,
                rawJumpPressed = rawJumpPressed, rawJumpHeld = rawJumpHeld,
                rawJumpReleased = rawJumpReleased, rawDashPressed = rawDashPressed,
                rawDashHeld = rawDashHeld };
        }
    }

    [Serializable]
    public sealed class MacroDefinition
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "New macro";
        public bool favorite;
        public string hotkey = "";
        public bool hotkeyCtrl;
        public bool hotkeyShift;
        public bool hotkeyAlt;
        public string checkpointId = "";
        public List<MacroStep> steps = new List<MacroStep>();
        public TimelineDocument timeline = new TimelineDocument();
    }

    [Serializable]
    public sealed class BundleDefinition
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "New bundle";
        public int version = 1;
        public List<BundleParameterDefinition> parameters = new List<BundleParameterDefinition>();
        public List<BundleActionTemplate> actions = new List<BundleActionTemplate>();
        public List<MacroStep> steps = new List<MacroStep>();
    }

    [Serializable]
    public sealed class MacroLibrary
    {
        // favorite is an additive, backward-compatible field; keep schema 8 so existing
        // route libraries deserialize with favorite=false instead of being quarantined.
        public const int CurrentSchemaVersion = 8;
        public int schemaVersion = CurrentSchemaVersion;
        public int selectedIndex;
        public int selectedBundleIndex;
        public List<MacroDefinition> macros = new List<MacroDefinition>();
        public List<BundleDefinition> bundles = new List<BundleDefinition>();

        public static MacroLibrary CreateDefault()
        {
            MacroDefinition macro = new MacroDefinition { name = "Walljump route" };
            macro.steps.Add(new MacroStep { label = "Run right", type = MacroStepType.Move, durationTicks = 20, direction = 1 });
            macro.steps.Add(new MacroStep { label = "Ground jump right", type = MacroStepType.Jump, durationTicks = 18, direction = 1 });
            macro.steps.Add(new MacroStep { label = "Walljump right", type = MacroStepType.WallJump, durationTicks = 20, timeoutTicks = 60, direction = 1 });
            macro.steps.Add(new MacroStep { label = "Walljump left", type = MacroStepType.WallJump, durationTicks = 20, timeoutTicks = 60, direction = -1 });
            macro.steps.Add(new MacroStep { label = "Hop delay", type = MacroStepType.Wait, durationTicks = 1, direction = -1 });
            macro.steps.Add(new MacroStep { label = "Little hop left", type = MacroStepType.Jump, durationTicks = 2, direction = -1 });
            macro.steps.Add(new MacroStep { label = "Rollout left", type = MacroStepType.Move, durationTicks = 25, direction = -1 });

            BundleDefinition right = new BundleDefinition { name = "Walljump right" };
            right.steps.Add(new MacroStep { label = "Walljump right", type = MacroStepType.WallJump, durationTicks = 20, timeoutTicks = 60, direction = 1 });
            BundleDefinition left = new BundleDefinition { name = "Walljump left" };
            left.steps.Add(new MacroStep { label = "Walljump left", type = MacroStepType.WallJump, durationTicks = 20, timeoutTicks = 60, direction = -1 });

            MacroLibrary library = new MacroLibrary();
            library.macros.Add(macro);
            library.bundles.Add(right);
            library.bundles.Add(left);
            return library;
        }
    }

    [Serializable]
    public sealed class RawTickSample
    {
        public long tick;
        public float fixedTime;
        public float unscaledTime;
        public float moveX;
        public float moveY;
        public bool jumpPressed;
        public bool jumpHeld;
        public bool jumpReleased;
        public bool dashPressed;
        public bool dashHeld;
        public float positionX;
        public float positionY;
        public float velocityX;
        public float velocityY;
        public float momentumX;
        public float momentumY;
        public bool onGround;
        public bool onWall;
        public float wallCoyoteFrames;
        public float wallSide;
        public int airJumpsLeft;
        public int airDashesLeft;
        public bool dashActive;
        public bool atJumpApex;
        public bool isDead;
        public string movementMode;
    }

    [Serializable]
    public sealed class RecordingDefinition
    {
        // favorite is optional and defaults to false for existing recordings.
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion = CurrentSchemaVersion;
        public string id = Guid.NewGuid().ToString("N");
        public string name = "Recording";
        public bool favorite;
        public string createdUtc = DateTime.UtcNow.ToString("o");
        public float fixedDeltaMs;
        public string stopReason = "Manual stop";
        public long courseStartTick = -1;
        public float courseTimeSeconds;
        public List<RawTickSample> samples = new List<RawTickSample>();
        public List<MacroStep> semanticSteps = new List<MacroStep>();
    }

    [Serializable]
    public sealed class AttemptResult
    {
        public const int CurrentSchemaVersion = 6;
        public int schemaVersion = CurrentSchemaVersion;
        public string id = Guid.NewGuid().ToString("N");
        public string routeId;
        public string routeName;
        public string timestampUtc = DateTime.UtcNow.ToString("o");
        public bool success;
        public long completionTicks;
        public float completionTime;
        public string timingSource;
        public long courseStartRunTick = -1;
        public int courseStartUnityFrame = -1;
        public float courseStartX;
        public float courseStartY;
        public string startStateFingerprint;
        public string earlyTrace;
        public float firstJumpPositionOffsetX;
        public float courseStartPositionOffsetX;
        public bool deterministicPhysics;
        public int failureAction = -1;
        public string failureReason;
    }

    [Serializable]
    public sealed class StudioSettings
    {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion = CurrentSchemaVersion;
        public float windowX = 62f;
        public float windowY = 24f;
        public float windowWidth = 1040f;
        public float windowHeight = 700f;
        public string selectedView = "routes";
        public bool ghostVisible = true;
        public bool autoOpenWebStudio = true;
    }
}
