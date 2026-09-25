using System;
using System.Collections.Generic;

namespace IGTAPTasMod
{
    // Bundle is kept in the enum so existing serialized numeric action types retain their meaning.
    public enum MacroStepType { Move, Jump, Dash, Wait, WallJump, Bundle, RawInput }
    // ponytail: only FixedTicks is produced; Duration survives for load-time migration.
    public enum ActionEndCondition { Duration, FixedTicks }
    public enum ActionTimeoutPolicy { AbortAttempt }

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
        public ActionEndCondition endCondition = ActionEndCondition.FixedTicks;
        public int durationTicks = 5;
        public int timeoutTicks = 60;
        // Exact recorded-input playback. RawInput is appended to the enum so existing
        // serialized numeric action types retain their meaning.
        public float rawMoveX;
        public float rawMoveY;
        public bool rawJumpPressed;
        public bool rawJumpHeld;
        public bool rawJumpReleased;
        public bool rawDashPressed;
        public bool rawDashHeld;
        // With rawDashPressed: press jump on this frame of the dash it starts (1 = first dash frame). The engine
        // gets no ticks during a dash, so this is the only way to jump mid-dash (a late super jump). 0 = off.
        public int dashJumpFrame;
        // The game's quick restart (the QuickReset button: Movement.respawn(true)) at the start of this step's first tick,
        // before Movement.FixedUpdate. Full-TAS routes choose where to restart.
        public bool rawQuickRestart;

        public MacroStep Copy()
        {
            return new MacroStep { id = Guid.NewGuid().ToString("N"), enabled = enabled,
                label = label, type = type, durationMs = durationMs,
                timeoutMs = timeoutMs, direction = direction, wallJumpHoldDirection = wallJumpHoldDirection,
                endCondition = endCondition,
                durationTicks = durationTicks, timeoutTicks = timeoutTicks,
                rawMoveX = rawMoveX, rawMoveY = rawMoveY,
                rawJumpPressed = rawJumpPressed, rawJumpHeld = rawJumpHeld,
                rawJumpReleased = rawJumpReleased, rawDashPressed = rawDashPressed,
                rawDashHeld = rawDashHeld, dashJumpFrame = dashJumpFrame, rawQuickRestart = rawQuickRestart };
        }
    }

    [Serializable]
    public sealed class MacroDefinition
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "New macro";
        public string hotkey = "";
        public bool hotkeyCtrl;
        public bool hotkeyShift;
        public bool hotkeyAlt;
        public string checkpointId = "";
        public List<MacroStep> steps = new List<MacroStep>();
        public TimelineDocument timeline = new TimelineDocument();
    }

    [Serializable]
    public sealed class MacroLibrary
    {
        public const int CurrentSchemaVersion = 8;
        public int schemaVersion = CurrentSchemaVersion;
        public int selectedIndex;
        public List<MacroDefinition> macros = new List<MacroDefinition>();
    }

    [Serializable]
    public sealed class RawTickSample
    {
        public long tick;
        public float moveX;
        public float moveY;
        public bool jumpPressed;
        public bool jumpHeld;
        public bool jumpReleased;
        public bool dashPressed;
        public bool dashHeld;
        // On a dash press: the dash frame (Movement.framesSinceLastDash) the jump was pressed in, 0 = none. The game takes
        // no input on dash frames, so a jump there (a super jump) shows up in no sample of its own (MacroStep.dashJumpFrame).
        public int dashJumpFrame;
        public float positionX;
        public float positionY;
        // FloatingOrigin.currentOrigin on this tick: the absolute position (courses.json's frame) is position - origin.
        public float originX;
        public float originY;
        public float velocityX;
        public float velocityY;
        public bool onGround;
        public bool onWall;
        public float wallCoyoteFrames;
        public float wallSide;
    }

    [Serializable]
    public sealed class RecordingDefinition
    {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion = CurrentSchemaVersion;
        public string id = Guid.NewGuid().ToString("N");
        public string name = "Recording";
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
        public bool autoOpenWebStudio = true;
        // Ethical runs: the plugin never changes the player's unlocks (wall jump, dash, ...), and tools that need to refuse.
        public bool lockCapabilities;
        // The in-game overlay for viewers: the player's hurtbox, spikes and their grace, the player's velocity, and the inputs playback gave on the last 30 ticks (TasOverlay.cs).
        public bool showOverlay = true;
    }
}
