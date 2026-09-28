using System;
using System.Globalization;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    /// <summary>
    /// The three coordinate frames, as distinct types so a mix-up is a compile error (README "Coordinates").
    /// <see cref="WorldPos"/>: the game world's frame (the scene position minus FloatingOrigin.currentOrigin) — courses.json,
    /// the --via/--avoid boxes, the at: goals as they are given, the studio's readouts. The one frame a FloatingOrigin rebase
    /// does not move.
    /// <see cref="ScenePos"/>: the scene frame now (transform positions, raycasts) — a rebase moves everything in it.
    /// <see cref="CapturePos"/>: the scene frame at the trace's capture (<see cref="Simulation.Origin"/>) — the trace's rects,
    /// goals and map cells.
    /// Conversions go through the simulation that owns the frames (ToScene/ToWorld/ToCapture/FromCapture below); the only
    /// escapes to raw vectors are <see cref="AsVector2"/> and <see cref="ScenePos.From"/>, at Unity API boundaries (which are
    /// scene frame).
    /// </summary>
    public readonly struct WorldPos : IEquatable<WorldPos>
    {
        public readonly float x, y;

        public WorldPos(float x, float y) { this.x = x; this.y = y; }

        /// <summary>Escape hatch for an API that takes a raw vector; the caller keeps the frame.</summary>
        public Vector2 AsVector2() { return new Vector2(x, y); }

        public bool Equals(WorldPos o) { return x == o.x && y == o.y; }
        public override bool Equals(object o) { return o is WorldPos p && Equals(p); }
        public override int GetHashCode() { return HashCode.Combine(x, y); }
        public static bool operator ==(WorldPos a, WorldPos b) { return a.Equals(b); }
        public static bool operator !=(WorldPos a, WorldPos b) { return !a.Equals(b); }
        public override string ToString() { return "(" + x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture) + ")"; }
    }

    /// <summary>The scene frame now: transform positions and raycasts. See <see cref="WorldPos"/>.</summary>
    public readonly struct ScenePos : IEquatable<ScenePos>
    {
        public readonly float x, y;

        public ScenePos(float x, float y) { this.x = x; this.y = y; }

        /// <summary>Escape hatch: a Unity API boundary's raw position (a transform, a raycast) is scene frame.</summary>
        public static ScenePos From(Vector3 v) { return new ScenePos(v.x, v.y); }

        /// <summary>Escape hatch for an API that takes a raw vector; the caller keeps the frame.</summary>
        public Vector2 AsVector2() { return new Vector2(x, y); }

        public bool Equals(ScenePos o) { return x == o.x && y == o.y; }
        public override bool Equals(object o) { return o is ScenePos p && Equals(p); }
        public override int GetHashCode() { return HashCode.Combine(x, y); }
        public static bool operator ==(ScenePos a, ScenePos b) { return a.Equals(b); }
        public static bool operator !=(ScenePos a, ScenePos b) { return !a.Equals(b); }
        public override string ToString() { return "(" + x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture) + ")"; }
    }

    /// <summary>The capture's scene frame (<see cref="Simulation.Origin"/>): the trace's rects, goals and map cells. See
    /// <see cref="WorldPos"/>.</summary>
    public readonly struct CapturePos : IEquatable<CapturePos>
    {
        public readonly float x, y;

        public CapturePos(float x, float y) { this.x = x; this.y = y; }

        /// <summary>Escape hatch for an API that takes a raw vector; the caller keeps the frame.</summary>
        public Vector2 AsVector2() { return new Vector2(x, y); }

        public bool Equals(CapturePos o) { return x == o.x && y == o.y; }
        public override bool Equals(object o) { return o is CapturePos p && Equals(p); }
        public override int GetHashCode() { return HashCode.Combine(x, y); }
        public static bool operator ==(CapturePos a, CapturePos b) { return a.Equals(b); }
        public static bool operator !=(CapturePos a, CapturePos b) { return !a.Equals(b); }
        public override string ToString() { return "(" + x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture) + ")"; }
    }

    /// <summary>One rect in the capture frame: an at: goal's rect or the finish box (the trace header's rects).</summary>
    public readonly struct CaptureRect
    {
        public readonly CapturePos At;
        public readonly float W, H;

        public CaptureRect(CapturePos at, float w, float h) { At = at; W = w; H = h; }

        public float X0 { get { return At.x - W / 2f; } }
        public float Y0 { get { return At.y - H / 2f; } }

        public bool Contains(CapturePos p) { return p.x >= X0 && p.x <= X0 + W && p.y >= Y0 && p.y <= Y0 + H; }
    }

    /// <summary>Where an at: goal's rect sits: the trace header's rect, in the capture frame (FullTasGoal keeps floats for
    /// the game, which reads the same fields in the world frame).</summary>
    static class GoalFrames
    {
        public static CaptureRect AtRect(this FullTasGoal goal) { return new CaptureRect(new CapturePos(goal.x, goal.y), goal.w, goal.h); }
    }
}
