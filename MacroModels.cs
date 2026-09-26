using System;
using System.Collections.Generic;

namespace IGTAPTasMod
{
    public enum MacroStepType { Move, Jump, Dash, Wait, WallJump, Bundle, RawInput }
    public enum ActionEndCondition { Duration, FixedTicks }

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
        public int wallJumpHoldDirection = 2;
        public ActionEndCondition endCondition = ActionEndCondition.FixedTicks;
        public int durationTicks = 5;
        public int timeoutTicks = 60;
        public float rawMoveX;
        public float rawMoveY;
        public bool rawJumpPressed;
        public bool rawJumpHeld;
        public bool rawJumpReleased;
        public bool rawDashPressed;
        public bool rawDashHeld;
        public int dashJumpFrame;
        public int springDashFrame;
        public float? lockMoveX, lockMoveY;
        public bool rawQuickRestart;

        public MacroStep Copy()
        {
            MacroStep copy = (MacroStep)MemberwiseClone();
            copy.id = Guid.NewGuid().ToString("N");
            return copy;
        }
    }

    [Serializable]
    public sealed class MacroDefinition
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "New macro";
        public string checkpointId = "";
        public List<MacroStep> steps = new List<MacroStep>();
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
        public int dashJumpFrame;
        public float positionX;
        public float positionY;
        public float originX;
        public float originY;
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
        public int failureAction = -1;
        public string failureReason;
    }

    [Serializable]
    public sealed class StudioSettings
    {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion = CurrentSchemaVersion;
        public bool autoOpenWebStudio = true;
        public bool lockCapabilities;
        public bool showOverlay = true;
    }
}
