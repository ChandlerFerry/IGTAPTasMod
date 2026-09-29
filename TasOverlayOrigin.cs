using System.Collections.Generic;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        // The Float Ultra overlay, drawn while a demo of a `floatingOrigin` tech plays. FloatingOrigin keeps the player within
        // Threshold of scene (0, 0), the sphere's centre. Past the edge it moves the world back by the player's scene
        // position, so the player's scene y drops by its height above the sphere's centre line. lastHyperY (the height the
        // game's ultra check compares a hyper against) is a scene number the move leaves alone. So a chained hyper whose
        // air crosses the edge ABOVE the centre line lands that far under lastHyperY: a float ultra. Crossing BELOW the line
        // raises the player's scene y over lastHyperY instead: no float ultra.
        FloatingOrigin _originOverlayInstance;
        Vector2 _originOverlaySeen;
        int _originOverlayRebases, _originOverlayFloats;
        Vector2 _originOverlayShift;
        float _originOverlayRebaseAt = float.NegativeInfinity, _originOverlayFloatAt = float.NegativeInfinity;
        // Armed: the last rebase came from above the centre line and lastHyperY hasn't been written since.
        bool _originOverlayArmed;
        float _originOverlayArmedHyperY;
        // Every sphere centre (world frame) this playback saw, the first the one it started in, and the dot's path.
        readonly List<Vector2> _originOverlayCenters = new List<Vector2>();
        readonly List<Vector2> _originOverlayTrail = new List<Vector2>();
        const int OriginTrailPoints = 6000;
        const float OriginTrailStep = 40f;
        // The minimap's world (courses.json frame): the Overworld's floors span about x -10400..34900, y -4600..27300.
        static readonly Rect OriginMapWorld = Rect.MinMaxRect(-11000f, -6000f, 36000f, 28500f);
        const int OriginSphereSegments = 4096;
        const float OriginSphereBand = 90f, OriginFlashSeconds = 3f;
        static readonly Color OriginAboveColor = Hex(0x4ade80, 1f);
        static readonly Color OriginBelowColor = Hex(0xf87171, 1f);
        static readonly Color OriginMoveColor = Hex(0xfacc15, 1f);
        static readonly Color OriginFloatColor = Hex(0x22d3ee, 1f);

        bool OriginOverlayShown
        {
            get
            {
                MacroDefinition route = _engine.IsRunning ? _engine.ActiveMacro : null;
                return route != null && TechOfRoute(route) is TechDef tech && tech.floatingOrigin;
            }
        }

        void ResetOriginOverlay()
        {
            _originOverlayRebases = -1;
        }

        void DrawOriginOverlay(float scale)
        {
            Camera cam = Camera.main;
            FloatingOrigin floating = CurrentFloatingOrigin();
            Movement player = _overlayPlayer;
            if (cam == null || floating == null || player == null) return;
            Vector2 origin = floating.currentOrigin;
            float lastHyper = TechRead<float>(TechLastHyperYField, player);
            int floats = _techCounts[(int)Tech.FloatUltra];
            if (!ReferenceEquals(floating, _originOverlayInstance) || _originOverlayRebases < 0)
            {
                _originOverlayInstance = floating;
                _originOverlayRebases = 0;
                _originOverlayFloats = floats;
                _originOverlayRebaseAt = _originOverlayFloatAt = float.NegativeInfinity;
                _originOverlayArmed = false;
                _originOverlayCenters.Clear();
                _originOverlayCenters.Add(-origin);
                _originOverlayTrail.Clear();
            }
            else if (origin != _originOverlaySeen)
            {
                _originOverlayRebases++;
                _originOverlayShift = origin - _originOverlaySeen;
                _originOverlayRebaseAt = Time.unscaledTime;
                _originOverlayCenters.Add(-origin);
                // The world moved by minus the dot's scene position: a negative y means the dot was above the centre line.
                _originOverlayArmed = _originOverlayShift.y < 0f;
                _originOverlayArmedHyperY = lastHyper;
            }
            _originOverlaySeen = origin;
            if (_originOverlayArmed && lastHyper != _originOverlayArmedHyperY) _originOverlayArmed = false;
            if (floats != _originOverlayFloats)
            {
                _originOverlayFloats = floats;
                _originOverlayFloatAt = Time.unscaledTime;
            }
            Vector2 world = (Vector2)player.transform.position - origin;
            if (_originOverlayTrail.Count == 0 || (world - _originOverlayTrail[_originOverlayTrail.Count - 1]).sqrMagnitude > OriginTrailStep * OriginTrailStep)
            {
                if (_originOverlayTrail.Count >= OriginTrailPoints) _originOverlayTrail.RemoveAt(0);
                _originOverlayTrail.Add(world);
            }

            Vector3 scene = player.transform.position;
            Vector3 reference = floating.ReferenceObject != null ? floating.ReferenceObject.position : scene;
            bool sideways = floating.Use2DDistance;
            if (sideways) reference.y = 0f;
            float distance = reference.magnitude, threshold = floating.Threshold;
            bool above = scene.y > 0f;
            bool moved = Time.unscaledTime - _originOverlayRebaseAt < OriginFlashSeconds;
            bool floated = Time.unscaledTime - _originOverlayFloatAt < OriginFlashSeconds;

            DrawOriginSphere(cam, threshold, sideways, scale);
            DrawOriginCentreLine(cam, scene, origin, scale);

            float labelHeight = 22 * scale;
            BoxCollider2D box = Hurtbox(player, _hitboxes);
            if (box != null)
            {
                Rect r = ScreenRect(cam, box.bounds);
                Label(new Rect(r.xMax + 8 * scale, r.yMin - labelHeight, 520 * scale, labelHeight),
                    (above ? "ABOVE the line +" : "BELOW the line −") + Mathf.Abs(scene.y).ToString("#,0"), _overlayText, above ? OriginAboveColor : OriginBelowColor);
                Label(new Rect(r.xMax + 8 * scale, r.yMin, 520 * scale, labelHeight), "x " + world.x.ToString("#,0") + "  y " + world.y.ToString("#,0"), _overlaySmall, PanelText);
            }

            DrawOriginPanel(scale, scene, world, distance, threshold, sideways, above, moved, floated);
            DrawOriginMap(scale, world, threshold, sideways);
        }

        void DrawOriginPanel(float scale, Vector3 scene, Vector2 world, float distance, float threshold, bool sideways, bool above, bool moved, bool floated)
        {
            float width = 820 * scale, row = 24 * scale, big = 30 * scale, pad = 10 * scale;
            // Under the Vman clock (top centre).
            var panel = new Rect((Screen.width - width) / 2f, 120 * scale, width, row * 4 + big + pad * 2);
            Fill(panel, PanelBack);
            float x = panel.x + pad, y = panel.y + pad, inner = width - pad * 2;
            Label(new Rect(x, y, inner, row), "FLOAT ULTRA", _overlayText, OriginFloatColor);
            Label(new Rect(x, y, inner, row), "sphere edge in " + Mathf.Max(0f, threshold - distance).ToString("#,0") + " u", _overlaySmallRight,
                distance > threshold ? OriginMoveColor : PanelText);
            y += row;
            var bar = new Rect(x, y + row * 0.3f, inner, row * 0.4f);
            Fill(bar, PanelBand);
            Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(distance / threshold), bar.height), above ? OriginAboveColor : OriginBelowColor);
            y += row;

            string status, detail;
            Color color;
            if (floated)
            {
                status = "FLOAT ULTRA!";
                detail = "the chained hyper landed " + (-_originOverlayShift.y).ToString("#,0") + " u under lastHyperY, so the game added back the pre-dash momentum";
                color = OriginFloatColor;
            }
            else if (moved)
            {
                bool up = _originOverlayShift.y < 0f;
                status = "THE SPHERE MOVED: world " + (up ? "down " : "up ") + Mathf.Abs(_originOverlayShift.y).ToString("#,0") + " u";
                detail = up ? "crossed ABOVE the line: you are now that far under lastHyperY, the next chained hyper is a FLOAT ULTRA"
                    : "crossed BELOW the line: you are now that far over lastHyperY, no float ultra from this one";
                color = up ? OriginAboveColor : OriginBelowColor;
            }
            else if (_originOverlayArmed)
            {
                status = "ARMED: hyper now for a FLOAT ULTRA";
                detail = "lastHyperY is " + (-_originOverlayShift.y).ToString("#,0") + " u above you since the sphere moved";
                color = OriginFloatColor;
            }
            else if (sideways)
            {
                status = "THIS SPHERE MOVES ONLY SIDEWAYS";
                detail = "Use2DDistance: crossing never changes your height, so no float ultras here";
                color = MutedColor;
            }
            else
            {
                status = (above ? "ABOVE THE LINE: crossing here → FLOAT ULTRA" : "BELOW THE LINE: crossing here → no float ultra");
                detail = "hyper, cross the sphere's edge in its air, hyper again: the world drops by your height above the centre line";
                color = above ? OriginAboveColor : OriginBelowColor;
            }
            Label(new Rect(x, y, inner, big), status, _overlayTitle, color);
            y += big;
            Label(new Rect(x, y, inner, row), detail, _overlaySmall, PanelText);
            y += row;
            Label(new Rect(x, y, inner, row), "from the centre (scene): x " + scene.x.ToString("#,0") + ", y " + scene.y.ToString("#,0")
                + "   ·   world: x " + world.x.ToString("#,0") + ", y " + world.y.ToString("#,0") + "   ·   sphere moves: " + _originOverlayRebases, _overlaySmall, MutedColor);
        }

        /// <summary>
        /// The sphere's centre line (scene y 0) across the view, green above and red below; off screen, where it is and which
        /// side the dot is on.
        /// </summary>
        void DrawOriginCentreLine(Camera cam, Vector3 scene, Vector2 origin, float scale)
        {
            float line = Mathf.Max(2f, Mathf.Round(3 * scale)), labelHeight = 22 * scale;
            float lineY = Screen.height - cam.WorldToScreenPoint(new Vector3(scene.x, 0f, scene.z)).y;
            if (lineY >= 0f && lineY <= Screen.height)
            {
                Fill(new Rect(0f, lineY - line, Screen.width, line), OriginAboveColor);
                Fill(new Rect(0f, lineY, Screen.width, line), OriginBelowColor);
                float left = PanelWidth * PanelScale(scale) + 20 * scale;
                Label(new Rect(left, lineY - labelHeight - line, 1000 * scale, labelHeight), "▲ ABOVE the sphere's centre line: crossing the edge up here gives a float ultra",
                    _overlayText, OriginAboveColor);
                Label(new Rect(left, lineY + line, 1000 * scale, labelHeight), "▼ BELOW the line: crossing down here gives none", _overlayText, OriginBelowColor);
            }
            else
            {
                bool lineBelow = lineY > Screen.height;
                string text = "sphere's centre line " + Mathf.Abs(scene.y).ToString("#,0") + " u " + (lineBelow ? "below ↓" : "above ↑");
                Label(new Rect(PanelWidth * PanelScale(scale) + 20 * scale, lineBelow ? Screen.height - labelHeight - 8 * scale : 8 * scale, 1000 * scale, labelHeight), text,
                    _overlayText, lineBelow ? OriginAboveColor : OriginBelowColor);
            }
            Vector3 centre = cam.WorldToScreenPoint(Vector3.zero);
            var at = new Vector2(centre.x, Screen.height - centre.y);
            if (at.x < 0f || at.y < 0f || at.x > Screen.width || at.y > Screen.height) return;
            float arm = 18 * scale;
            Fill(new Rect(at.x - arm, at.y - 1f, arm * 2, 2f), OriginMoveColor);
            Fill(new Rect(at.x - 1f, at.y - arm, 2f, arm * 2), OriginMoveColor);
            Label(new Rect(at.x + arm, at.y + 4f, 520 * scale, labelHeight), "the sphere's centre", _overlaySmall, OriginMoveColor);
        }

        /// <summary>
        /// The world minimap: every sphere this playback was in (green upper half, red lower half, the current one bright),
        /// where the dot crossed each (the next centre), the dot's path and the courses, in the courses.json frame.
        /// </summary>
        void DrawOriginMap(float scale, Vector2 dot, float threshold, bool sideways)
        {
            Material material = OverlayMaterial();
            if (material == null) return;
            float width = 470f * scale, height = width * OriginMapWorld.height / OriginMapWorld.width, row = 20f * scale;
            // Bottom right: the velocity panel holds the top right.
            var map = new Rect(Screen.width - width - 16f * scale, Screen.height - height - 16f * scale, width, height);
            Fill(new Rect(map.x, map.y - row * 2, map.width, map.height + row * 2), PanelBack);
            Label(new Rect(map.x + 6f * scale, map.y - row * 2, map.width - 12f * scale, row), "THE SPHERE MOVES WITH YOU (" + threshold.ToString("#,0") + " u)", _overlaySmall, OriginMoveColor);
            Label(new Rect(map.x + 6f * scale, map.y - row, map.width - 12f * scale, row), "green half: cross → float ultra   red half: no   ● crossings", _overlaySmall, PanelText);
            float unitsPerPixel = OriginMapWorld.width / width;
            material.SetPass(0);
            GL.PushMatrix();
            GL.Viewport(new Rect(map.x, Screen.height - map.yMax, map.width, map.height));
            GL.LoadPixelMatrix(OriginMapWorld.xMin, OriginMapWorld.xMax, OriginMapWorld.yMin, OriginMapWorld.yMax);
            GL.Begin(GL.LINES);
            int last = _originOverlayCenters.Count - 1;
            for (int i = 0; i <= last; i++)
            {
                float alpha = i == last ? 1f : 0.3f;
                Vector2 c = _originOverlayCenters[i];
                if (sideways)
                {
                    GL.Color(new Color(OriginBelowColor.r, OriginBelowColor.g, OriginBelowColor.b, alpha));
                    MapLine(new Vector2(c.x - threshold, OriginMapWorld.yMin), new Vector2(c.x - threshold, OriginMapWorld.yMax));
                    MapLine(new Vector2(c.x + threshold, OriginMapWorld.yMin), new Vector2(c.x + threshold, OriginMapWorld.yMax));
                    continue;
                }
                for (int s = 0; s < 256; s++)
                {
                    float a0 = s * Mathf.PI / 128f, a1 = (s + 1) * Mathf.PI / 128f;
                    Color side = s < 128 ? OriginAboveColor : OriginBelowColor;
                    GL.Color(new Color(side.r, side.g, side.b, alpha));
                    MapLine(c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * threshold, c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * threshold);
                }
                if (i == last)
                {
                    GL.Color(new Color(PanelText.r, PanelText.g, PanelText.b, 0.5f));
                    MapLine(c - new Vector2(threshold, 0f), c + new Vector2(threshold, 0f));
                }
                if (i > 0)
                {
                    GL.Color(OriginMoveColor);
                    MapLine(_originOverlayCenters[i - 1], c);
                }
            }
            GL.Color(new Color(PanelText.r, PanelText.g, PanelText.b, 0.8f));
            for (int i = 1; i < _originOverlayTrail.Count; i++) MapLine(_originOverlayTrail[i - 1], _originOverlayTrail[i]);
            GL.End();
            GL.Begin(GL.QUADS);
            for (int i = 1; i <= last; i++)
            {
                Vector2 c = _originOverlayCenters[i];
                MapDot(c, 5f * unitsPerPixel, c.y > _originOverlayCenters[i - 1].y ? OriginAboveColor : OriginBelowColor);
            }
            MapDot(dot, 4f * unitsPerPixel, HurtboxColor);
            GL.End();
            GL.PopMatrix();
            GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
            foreach (CourseDef course in _courses)
            {
                if (course.IsTransition || course.overgrown || (course.name ?? "").StartsWith("bonus", System.StringComparison.Ordinal)
                    || !OriginMapWorld.Contains(new Vector2(course.x, course.y))) continue;
                Vector2 at = new Vector2(map.x + (course.x - OriginMapWorld.xMin) / unitsPerPixel, map.yMax - (course.y - OriginMapWorld.yMin) / unitsPerPixel);
                Fill(new Rect(at.x - 2f, at.y - 2f, 4f, 4f), MutedColor);
                Label(new Rect(at.x + 4f, at.y - row / 2f, 60f * scale, row), "C" + course.id, _overlaySmall, MutedColor);
            }
        }

        static void MapLine(Vector2 a, Vector2 b)
        {
            GL.Vertex3(a.x, a.y, 0f);
            GL.Vertex3(b.x, b.y, 0f);
        }

        static void MapDot(Vector2 at, float half, Color color)
        {
            GL.Color(color);
            GL.Vertex3(at.x - half, at.y - half, 0f); GL.Vertex3(at.x + half, at.y - half, 0f);
            GL.Vertex3(at.x + half, at.y + half, 0f); GL.Vertex3(at.x - half, at.y + half, 0f);
        }

        /// <summary>The sphere's edge around scene (0, 0) where it is in view, green above the centre line and red below, with a faint band outside.</summary>
        void DrawOriginSphere(Camera cam, float threshold, bool sideways, float scale)
        {
            Material material = OverlayMaterial();
            if (material == null || float.IsInfinity(threshold)) return;
            Vector3 v0 = cam.ViewportToWorldPoint(Vector3.zero), v1 = cam.ViewportToWorldPoint(Vector3.one);
            Rect view = Rect.MinMaxRect(Mathf.Min(v0.x, v1.x) - OriginSphereBand, Mathf.Min(v0.y, v1.y) - OriginSphereBand,
                Mathf.Max(v0.x, v1.x) + OriginSphereBand, Mathf.Max(v0.y, v1.y) + OriginSphereBand);
            float half = Mathf.Max(1.5f, 2.5f * scale);
            Rect pixels = cam.pixelRect;
            material.SetPass(0);
            GL.PushMatrix();
            GL.Viewport(pixels);
            GL.LoadPixelMatrix(pixels.xMin, pixels.xMax, Screen.height - pixels.yMin, Screen.height - pixels.yMax);
            GL.Begin(GL.QUADS);
            if (sideways)
            {
                foreach (float side in new[] { -1f, 1f })
                {
                    float x = side * threshold;
                    if (x < view.xMin || x > view.xMax) continue;
                    OriginQuad(cam, new Vector2(x, view.yMin), new Vector2(x, view.yMax), new Vector2(x + side * OriginSphereBand, view.yMax),
                        new Vector2(x + side * OriginSphereBand, view.yMin), OriginBelowColor, half);
                }
            }
            else
            {
                float step = 2f * Mathf.PI / OriginSphereSegments, outer = threshold + OriginSphereBand;
                for (int i = 0; i < OriginSphereSegments; i++)
                {
                    float a0 = i * step, a1 = a0 + step;
                    var d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
                    var d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                    Vector2 p0 = d0 * threshold, p1 = d1 * threshold;
                    if (!view.Contains(p0) && !view.Contains(p1)) continue;
                    OriginQuad(cam, p0, p1, d1 * outer, d0 * outer, p0.y + p1.y > 0f ? OriginAboveColor : OriginBelowColor, half);
                }
            }
            GL.End();
            GL.PopMatrix();
            GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
        }

        /// <summary>One piece of the sphere: the edge a–b as a solid line, the band a–b–c–d outside it faint.</summary>
        static void OriginQuad(Camera cam, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color, float half)
        {
            Vector2 sa = OriginScreen(cam, a), sb = OriginScreen(cam, b), sc = OriginScreen(cam, c), sd = OriginScreen(cam, d);
            GL.Color(new Color(color.r, color.g, color.b, 0.16f));
            GL.Vertex(sa); GL.Vertex(sb); GL.Vertex(sc); GL.Vertex(sd);
            Vector2 edge = sb - sa;
            if (edge.sqrMagnitude < 1e-6f) return;
            Vector2 n = new Vector2(-edge.y, edge.x).normalized * half;
            GL.Color(color);
            GL.Vertex(sa + n); GL.Vertex(sb + n); GL.Vertex(sb - n); GL.Vertex(sa - n);
        }

        static Vector2 OriginScreen(Camera cam, Vector2 scene)
        {
            Vector3 s = cam.WorldToScreenPoint(scene);
            return new Vector2(s.x, Screen.height - s.y);
        }
    }
}
