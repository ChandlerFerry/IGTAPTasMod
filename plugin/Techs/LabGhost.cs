using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        struct GhostPoint
        {
            public Vector2 At;
            public float Facing;
        }

        sealed class LabGhost
        {
            public Transform Root, Sprite;
            public List<GhostPoint> Path;
            public Vector2 Scale;
            public string Label, Badge;
            public Color Tint;
        }

        const int GhostHoldTicks = 25;

        static readonly Color[] GhostTints =
        {
            new Color(0.35f, 0.85f, 1f, 0.65f), new Color(0.3f, 0.95f, 0.4f, 0.65f),
            new Color(1f, 0.35f, 0.85f, 0.65f), new Color(1f, 0.95f, 0.2f, 0.65f), new Color(1f, 0.6f, 0.15f, 0.65f),
        };

        void BuildLabGhosts(LabMapDef lab, Movement player, string playing)
        {
            if (Hurtbox(player, _hitboxes) == null) return;
            if (playing != null && lab.ghosts == null) return;
            List<LabGhostDef> ghosts = lab.ghosts ?? new List<LabGhostDef> { new LabGhostDef() };
            for (int i = 0; i < ghosts.Count; i++)
            {
                string demo = ghosts[i].demo ?? "";
                if (playing != null && demo == playing) continue;
                List<GhostPoint> path = LabGhostPath(lab, demo);
                if (path == null) continue;
                string name = lab.id + " ghost " + (demo.Length > 0 ? demo : "demo");
                Transform lane = LabGhostLane(lab, name, ghosts[i].offset);
                Color tint = GhostTints[i % GhostTints.Length];
                var line = new GameObject(name + " path");
                line.transform.SetParent(lane, false);
                LabLine(line.transform, path.Select(p => p.At).ToArray(), false, new Color(tint.r, tint.g, tint.b, 0.4f), 3f);
                LabGhost ghost = BuildLabGhost(name, lane, player, path, tint, ghosts[i].label);
                ghost.Badge = ghosts[i].badge;
                _lab.Ghosts.Add(ghost);
            }
            _lab.GhostClock = 0f;
            if (_lab.Ghosts.Count > 0) AnimateLabGhosts();
        }

        Transform LabGhostLane(LabMapDef lab, string name, float[] offset)
        {
            if (offset == null || offset.Length < 2 || offset[0] == 0f && offset[1] == 0f) return _lab.Root.transform;
            var lane = new GameObject(name + " lane");
            lane.transform.SetParent(_lab.Root.transform, false);
            lane.transform.localPosition = new Vector3(offset[0], offset[1], 0f);
            foreach (LabShapeDef shape in lab.shapes)
            {
                var copy = new GameObject(shape.kind);
                copy.transform.SetParent(lane.transform, false);
                copy.transform.localPosition = new Vector3(shape.at != null ? shape.at[0] : 0f, shape.at != null ? shape.at[1] : 0f, 0f);
                copy.transform.localRotation = shape.Rotation();
                LabShapeVisual(copy.transform, shape, 0.55f);
            }
            foreach (Renderer renderer in lane.GetComponentsInChildren<Renderer>()) LabSorting(renderer, LabLaneRank);
            return lane.transform;
        }

        List<GhostPoint> LabGhostPath(LabMapDef lab, string demo)
        {
            string file = LabMapFile(lab.id + (demo.Length > 0 ? "." + demo : "") + ".demo.json");
            if (file == null) return null;
            JArray points = JObject.Parse(File.ReadAllText(file))["path"] as JArray;
            if (points == null || points.Count < 2) return null;
            if (points.Any(p => ((JArray)p).Count < 3))
            {
                Logger.LogWarning("Lab map " + lab.id + "'s " + (demo.Length > 0 ? demo + " " : "") + "demo path has no facing: rewrite it with `IGTAP.EngineSim lab replay ... --write-path`.");
                return null;
            }
            return points.Select(p => new GhostPoint { At = new Vector2((float)p[0], (float)p[1]), Facing = (float)p[2] }).ToList();
        }

        LabGhost BuildLabGhost(string name, Transform lane, Movement player, List<GhostPoint> path, Color tint, string label)
        {
            var root = new GameObject(name);
            root.transform.SetParent(lane, false);
            BoxCollider2D box = Hurtbox(player, _hitboxes);
            SpriteRenderer sprite = LabBox(root.transform, box.offset, box.size, tint);
            LabSorting(sprite, LabGhostRank);
            Vector3 playerScale = player.transform.lossyScale;
            return new LabGhost
            {
                Root = root.transform, Sprite = sprite.transform, Path = path, Label = label, Tint = tint,
                Scale = new Vector2(Mathf.Abs(playerScale.x), playerScale.y),
            };
        }

        void AnimateLabGhosts()
        {
            // A frozen frame counts as the game's freeze does: fixed-clock step in paced playback, wall clock in hand play.
            _lab.GhostClock += Time.timeScale > 0f ? Time.deltaTime : FixedFrameStep();
            float ticks = _lab.GhostClock / Time.fixedDeltaTime;
            int longest = int.MinValue;
            foreach (LabGhost g in _lab.Ghosts) longest = Mathf.Max(longest, g.Path.Count);
            float cycle = longest - 1 + GhostHoldTicks;
            int loop = (int)(ticks / cycle);
            foreach (LabGhost g in _lab.Ghosts) AnimateLabGhost(g, ticks - loop * cycle);
        }

        void AnimateLabGhost(LabGhost g, float at)
        {
            List<GhostPoint> path = g.Path;
            float t = Mathf.Min(at, path.Count - 1);
            int i = Mathf.Min((int)t, path.Count - 2);
            GhostPoint a = path[i], b = path[i + 1];
            Vector2 position = Vector2.Lerp(a.At, b.At, t - i);
            g.Root.localPosition = new Vector3(position.x, position.y, 0f);
            g.Root.localScale = new Vector3(g.Scale.x * (b.Facing < 0f ? -1f : 1f), g.Scale.y, 1f);
        }

        void DrawLabGhostLegend(float scale)
        {
            float pad = 10f * scale, swatch = 18f * scale, gap = 30f * scale;
            float height = Mathf.Max(swatch, _overlayText.CalcSize(LegendLineContent).y) + pad;
            // Sum widths in double, as Enumerable.Sum adds floats.
            _ghostLegendWidths.Clear();
            double sum = 0;
            foreach (LabGhost g in _lab.Ghosts)
            {
                if (string.IsNullOrEmpty(g.Label)) continue;
                OverlayMeasure.text = g.Label;
                float labelWidth = _overlayText.CalcSize(OverlayMeasure).x;
                _ghostLegendWidths.Add(labelWidth);
                sum += swatch + 8f * scale + labelWidth;
            }
            if (_ghostLegendWidths.Count == 0) return;
            float total = (float)sum + gap * (_ghostLegendWidths.Count - 1) + pad * 2f;
            float x = (Screen.width - total) / 2f, y = Screen.height - height - 16f * scale;
            Fill(new Rect(x, y, total, height), new Color(0f, 0f, 0f, 0.65f));
            x += pad;
            int item = 0;
            foreach (LabGhost g in _lab.Ghosts)
            {
                if (string.IsNullOrEmpty(g.Label)) continue;
                float width = _ghostLegendWidths[item++];
                Fill(new Rect(x, y + (height - swatch) / 2f, swatch, swatch), new Color(g.Tint.r, g.Tint.g, g.Tint.b, 1f));
                x += swatch + 8f * scale;
                Label(new Rect(x, y, width + 4f * scale, height), g.Label, _overlayText, Color.white);
                x += width + gap;
            }
        }

        static readonly GUIContent LegendLineContent = new GUIContent("Ag");
        readonly List<float> _ghostLegendWidths = new List<float>();

        bool LabGhostOverlayShown
        {
            get
            {
                if (_lab == null) return false;
                foreach (LabGhost g in _lab.Ghosts)
                    if (!string.IsNullOrEmpty(g.Label) || !string.IsNullOrEmpty(g.Badge)) return true;
                return false;
            }
        }

        GUIStyle _ghostBadgeStyle;
        static readonly Vector2[] BadgeOutline = { new Vector2(-1f, 0f), new Vector2(1f, 0f), new Vector2(0f, -1f), new Vector2(0f, 1f) };

        void DrawLabGhostBadges(float scale)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            if (_ghostBadgeStyle == null || _ghostBadgeStyle.fontSize != Mathf.RoundToInt(15 * scale))
                _ghostBadgeStyle = new GUIStyle(_overlayTitle) { fontSize = Mathf.RoundToInt(15 * scale), alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
            float w = 60f * scale, h = 22f * scale, o = Mathf.Max(1f, Mathf.Round(scale * 1.5f));
            foreach (LabGhost g in _lab.Ghosts)
            {
                if (string.IsNullOrEmpty(g.Badge) || g.Sprite == null) continue;
                Vector3 s = cam.WorldToScreenPoint(g.Sprite.position + new Vector3(0f, 14f * g.Root.lossyScale.y, 0f));
                if (s.z < 0f) continue;
                var at = new Rect(s.x - w / 2f, Screen.height - s.y - h / 2f, w, h);
                foreach (Vector2 d in BadgeOutline)
                    Label(new Rect(at.x + d.x * o, at.y + d.y * o, w, h), g.Badge, _ghostBadgeStyle, new Color(0f, 0f, 0f, 0.9f));
                Label(at, g.Badge, _ghostBadgeStyle, Color.white);
            }
        }
    }
}
