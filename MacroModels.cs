using System;
using System.Collections.Generic;

namespace IGTAPTasMod
{
    public enum MacroStepType { Move, Jump, Dash, Wait, WallJump, Bundle, RawInput }
    public enum ActionEndCondition { Duration, FixedTicks }

    [Serializable]
    public sealed class MacroStep
    {
        public string id;
        public bool enabled = true;
        public string label = "New action";

        public MacroStep() { id = Guid.NewGuid().ToString("N"); }

        /// <summary>A step with no id (null), for the offline engine's per-run steps (Simulation.ToSteps): MacroEngine never
        /// reads a step's id, and a Guid per merged input group per run is measurable in a search.</summary>
        internal MacroStep(bool noId) { }
        public MacroStepType type = MacroStepType.Move;
        /// <summary>Old routes' millisecond timing: read once to turn them into ticks (TasPlugin.NormalizeStepFrameTiming),
        /// never written.</summary>
        public int durationMs = 100;
        public int timeoutMs = 1200;
        public bool ShouldSerializedurationMs() { return false; }
        public bool ShouldSerializetimeoutMs() { return false; }
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
        /// <summary>On a dash step: the stick held in the dash's freeze, before the next tick (MacroEngine.StickFor). y only
        /// turns an omnidash.</summary>
        public float? dashTurnX, dashTurnY;
        public bool rawQuickRestart;
        /// <summary>
        /// Open the pause menu (pauseMenuScript.menuButtonPressed, the Pause button's) after this step's <c>pauseTick</c>-th
        /// physics tick (0: its first; a dash's locked ticks count) and <c>pauseFrame</c> Movement.Update frames of the tick
        /// after it (0: at once, in the tick), and close it after <c>pauseFrames</c> Movement.Update frames. Paused, time
        /// stands still but Update's step-up runs every frame (the pauselift). 1x playback opens the menu; turbo and the
        /// offline engine run the same frames (the tick's first frame repeated, or with a pauseFrame the frame before it,
        /// whose blend the pause holds).
        /// </summary>
        public int pauseFrames;
        public int pauseTick;
        public int pauseFrame;

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
        public string savestateId = "";
        public List<MacroStep> steps = new List<MacroStep>();
        public List<RouteMarker> markers;
        public int rerecords;
        /// <summary>A farm route's loop (README ### Rules 30): the input tick its loop starts on, after the lead's last finish
        /// (0: not a farm loop), and the laps in the loop (0 or 1: one).</summary>
        public int loopFrom, loopLaps;
        public bool ShouldSerializemarkers() { return markers != null && markers.Count > 0; }
        public bool ShouldSerializererecords() { return rerecords > 0; }
        public bool ShouldSerializeloopFrom() { return loopFrom > 0; }
        public bool ShouldSerializeloopLaps() { return loopLaps > 1; }
    }

    [Serializable]
    public sealed class RouteMarker
    {
        public int tick;
        public string note = "";
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
        /// <summary>A dash pressed with the stick at rest that the stick turned in its freeze: the stick that turned it.</summary>
        public float? dashTurnX, dashTurnY;
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
    }

    [Serializable]
    public sealed class StudioSettings
    {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion = CurrentSchemaVersion;
        public bool autoOpenWebStudio = true;
        public bool lockCapabilities;
        public bool showOverlay = true;
        public bool forceBreakerLights;
        public bool checkpointRespawns;
        public string fullTasGoalsRun = "";
        /// <summary>The selected route's id (the library's selectedIndex, saved without rewriting routes.json).</summary>
        public string selectedRouteId = "";
        /// <summary>1x playback without the 280 fps cap on real frames; the game time per frame stays on the fixed grid.</summary>
        public bool fastPlayback;
        /// <summary>The player drawn as its hurtbox (TasSkin.cs) instead of its sprite.</summary>
        public bool tasSkin = true;
    }
}
