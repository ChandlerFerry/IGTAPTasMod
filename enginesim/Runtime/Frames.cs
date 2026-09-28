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

    /// <summary>
    /// Conversions between the frames. The float math is exactly what the raw properties and the call sites being migrated
    /// already did (the same operations in the same order), so no result changes. The raw origins are read only here.
    /// TEMPORARY SHAPE: these move onto <see cref="Simulation"/> (with typed PlayerWorld, PlayerAtCapture and origin
    /// properties) when its file's owner is done, and this class goes away.
    /// </summary>
    static class FrameConversions
    {
        public static WorldPos PlayerWorldPos(this Simulation sim) { Vector3 p = sim.PlayerWorld; return new WorldPos(p.x, p.y); }

        public static CapturePos PlayerAtCapturePos(this Simulation sim) { Vector3 p = sim.PlayerAtCapture; return new CapturePos(p.x, p.y); }

        /// <summary>The world frame's origin in the scene frame now: FloatingOrigin.currentOrigin.</summary>
        public static ScenePos WorldOriginInScene(this Simulation sim) { Vector3 o = sim.CurrentOrigin; return new ScenePos(o.x, o.y); }

        /// <summary>The world frame's origin in the capture's frame: FloatingOrigin.currentOrigin at the capture.</summary>
        public static CapturePos WorldOriginInCapture(this Simulation sim) { Vector3 o = sim.Origin; return new CapturePos(o.x, o.y); }

        /// <summary>A world point in the scene frame now (scene = world + FloatingOrigin.currentOrigin).</summary>
        public static ScenePos ToScene(this Simulation sim, WorldPos w)
        {
            Vector3 o = sim.CurrentOrigin;
            return new ScenePos(w.x + o.x, w.y + o.y);
        }

        /// <summary>A scene point now in the world frame (world = scene - FloatingOrigin.currentOrigin).</summary>
        public static WorldPos ToWorld(this Simulation sim, ScenePos s)
        {
            Vector3 o = sim.CurrentOrigin;
            return new WorldPos(s.x - o.x, s.y - o.y);
        }

        /// <summary>A capture point in the world frame (world = capture - the capture's origin).</summary>
        public static WorldPos ToWorld(this Simulation sim, CapturePos c)
        {
            Vector3 o = sim.Origin;
            return new WorldPos(c.x - o.x, c.y - o.y);
        }

        /// <summary>A world point in the capture's frame (capture = world + the capture's origin).</summary>
        public static CapturePos ToCapture(this Simulation sim, WorldPos w)
        {
            Vector3 o = sim.Origin;
            return new CapturePos(w.x + o.x, w.y + o.y);
        }

        /// <summary>A scene point now in the capture's frame (what <see cref="Simulation.PlayerAtCapture"/> does).</summary>
        public static CapturePos ToCapture(this Simulation sim, ScenePos s)
        {
            if (!sim.Rebased) return new CapturePos(s.x, s.y);
            Vector3 o = sim.CurrentOrigin, origin = sim.Origin;
            return new CapturePos(s.x - o.x + origin.x, s.y - o.y + origin.y);
        }

        /// <summary>A capture point in the scene frame now (what the goal check's Now(x, y) does).</summary>
        public static ScenePos FromCapture(this Simulation sim, CapturePos c)
        {
            if (!sim.Rebased) return new ScenePos(c.x, c.y);
            Vector3 o = sim.CurrentOrigin, origin = sim.Origin;
            return new ScenePos(c.x - origin.x + o.x, c.y - origin.y + o.y);
        }

        /// <summary>Where an at: goal's rect sits: the trace header's rect, in the capture frame.</summary>
        public static CaptureRect AtRect(this FullTasGoal goal) { return new CaptureRect(new CapturePos(goal.x, goal.y), goal.w, goal.h); }

        /// <summary>TEMPORARY: <see cref="OfflineSolver.Box"/> takes a <see cref="WorldPos"/> once its file's owner is done.</summary>
        public static bool Contains(this OfflineSolver.Box box, WorldPos p) { return p.x >= box.X0 && p.x <= box.X1 && p.y >= box.Y0 && p.y <= box.Y1; }
    }
}
