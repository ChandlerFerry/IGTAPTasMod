using System;
using System.Globalization;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    // WorldPos = scene minus FloatingOrigin.currentOrigin; ScenePos = transforms/raycasts; CapturePos = capture's scene frame.
    public readonly record struct WorldPos(float x, float y)
    {
        public Vector2 AsVector2() { return new Vector2(x, y); }

        public override string ToString() { return "(" + x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture) + ")"; }
    }

    public readonly record struct ScenePos(float x, float y)
    {
        public static ScenePos From(Vector3 v) { return new ScenePos(v.x, v.y); }

        public Vector2 AsVector2() { return new Vector2(x, y); }

        public override string ToString() { return "(" + x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture) + ")"; }
    }

    public readonly record struct CapturePos(float x, float y)
    {
        public Vector2 AsVector2() { return new Vector2(x, y); }

        public override string ToString() { return "(" + x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture) + ")"; }
    }

    public readonly struct CaptureRect
    {
        public readonly CapturePos At;
        public readonly float W, H;

        public CaptureRect(CapturePos at, float w, float h) { At = at; W = w; H = h; }

        public float X0 { get { return At.x - W / 2f; } }
        public float Y0 { get { return At.y - H / 2f; } }
    }
}
