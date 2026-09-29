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
    public readonly record struct WorldPos(float x, float y)
    {
        /// <summary>Escape hatch for an API that takes a raw vector; the caller keeps the frame.</summary>
        public Vector2 AsVector2() { return new Vector2(x, y); }

        public override string ToString() { return "(" + x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture) + ")"; }
    }

    /// <summary>The scene frame now: transform positions and raycasts. See <see cref="WorldPos"/>.</summary>
    public readonly record struct ScenePos(float x, float y)
    {
        /// <summary>Escape hatch: a Unity API boundary's raw position (a transform, a raycast) is scene frame.</summary>
        public static ScenePos From(Vector3 v) { return new ScenePos(v.x, v.y); }

        /// <summary>Escape hatch for an API that takes a raw vector; the caller keeps the frame.</summary>
        public Vector2 AsVector2() { return new Vector2(x, y); }

        public override string ToString() { return "(" + x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture) + ")"; }
    }

    /// <summary>The capture's scene frame (<see cref="Simulation.Origin"/>): the trace's rects, goals and map cells. See
    /// <see cref="WorldPos"/>.</summary>
    public readonly record struct CapturePos(float x, float y)
    {
        /// <summary>Escape hatch for an API that takes a raw vector; the caller keeps the frame.</summary>
        public Vector2 AsVector2() { return new Vector2(x, y); }

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
    }
}
