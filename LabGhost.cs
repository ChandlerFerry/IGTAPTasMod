using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        /// <summary>One point of a demo's ghost path: after a physics tick (or a tick's worth of frames where time stood still).</summary>
        struct GhostPoint
        {
            public Vector2 At;
            public float Facing;
            /// <summary>Movement's Animator "Animation" state (0 idle, 1 run, 2 fall, 3 wall, 5 rise, 6 dash, 7 death) and its speed.</summary>
            public int Anim;
            public float Speed;
        }

        /// <summary>
        /// A lab ghost: a see-through copy of the player's own animated sprite (the Sprite child: its SpriteRenderer and
        /// Animator, every script, collider and particle system stripped) replaying one demo's path on a loop at the physics
        /// rate. The path carries the Animator state the game set on every tick, so the ghost runs, jumps, clings and dashes
        /// with the player's clips; the jump, landing and fall squash-and-stretch follow Movement's own formulas
        /// (JumpingSquashStretch, LandingSquashStretch, FallingSquashStretch), which the offline engine doesn't run.
        /// </summary>
        sealed class LabGhost
        {
            public Transform Root, Sprite;
            public Animator Animator;
            public List<GhostPoint> Path;
            public Vector2 Scale;
            /// <summary>The lab's <c>ghosts</c> label (in the on-screen key beside its colour), its badge (drawn on its head), and its colour.</summary>
            public string Label, Badge;
            public Color Tint;
            public int Loop = -1, Anim = -1;
            public Squash Squash;
            public float SquashStart;
            public Vector2 SquashFrom, LastScale = Vector2.one;
        }

        enum Squash { None, Jump, Land }

        /// <summary>Ticks the ghosts hold their last positions before the loop starts over (after the longest path).</summary>
        const int GhostHoldTicks = 25;

        /// <summary>The ghosts' colours in the lab's <c>ghosts</c> order (the studio preview's): cyan, green, magenta, yellow, orange.</summary>
        static readonly Color[] GhostTints =
        {
            new Color(0.35f, 0.85f, 1f, 0.65f), new Color(0.3f, 0.95f, 0.4f, 0.65f),
            new Color(1f, 0.35f, 0.85f, 0.65f), new Color(1f, 0.95f, 0.2f, 0.65f), new Color(1f, 0.6f, 0.15f, 0.65f),
        };

        static Material _ghostMaterial;

        /// <summary>
        /// A flat-colour silhouette material: the sprite's own alpha filled with the renderer's colour (the built-in GUI/Text
        /// shader), so each ghost reads as its colour, not as a dark sprite with a faint tint. Without it, the lab's unlit one.
        /// </summary>
        static Material GhostMaterial()
        {
            if (_ghostMaterial != null) return _ghostMaterial;
            Shader shader = Shader.Find("GUI/Text Shader");
            return _ghostMaterial = shader != null ? new Material(shader) : LabMaterial();
        }

        /// <summary>
        /// Builds the lab's ghosts (its <c>ghosts</c>, else the main demo alone) from their demos' paths (labmaps/&lt;id&gt;[.&lt;demo&gt;].demo.json
        /// <c>path</c>: [x, y, facing, animation, animator speed] per point, from <c>lab solve</c> or <c>lab replay --write-path</c>),
        /// each drawn as a faint line in its colour, in its lane (<see cref="LabGhostDef.offset"/>: a see-through copy of the lab's
        /// shapes, drawn behind the ghosts). In a demo's playback (<paramref name="playing"/>: that demo, "" the main one) only a
        /// lab that lists <c>ghosts</c> gets them, all but the one being played. Nothing to collide with: sprites only.
        /// </summary>
        void BuildLabGhosts(LabMapDef lab, Movement player, string playing)
        {
            if (player.animator == null) return;
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

        /// <summary>
        /// A ghost's lane: the lab itself, or (an <c>offset</c>) a root that far from it holding a see-through copy of the lab's
        /// shapes with no colliders, sorted under the ghosts and paths.
        /// </summary>
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

        /// <summary>One demo's ghost path, or null (no demo file, no path, or one from before the animation fields).</summary>
        List<GhostPoint> LabGhostPath(LabMapDef lab, string demo)
        {
            string file = LabMapFile(lab.id + (demo.Length > 0 ? "." + demo : "") + ".demo.json");
            if (file == null) return null;
            JArray points = JObject.Parse(File.ReadAllText(file))["path"] as JArray;
            if (points == null || points.Count < 2) return null;
            if (points.Any(p => ((JArray)p).Count < 5))
            {
                Logger.LogWarning("Lab map " + lab.id + "'s " + (demo.Length > 0 ? demo + " " : "") + "demo path has no animation states: rewrite it with `IGTAP.EngineSim lab replay ... --write-path`.");
                return null;
            }
            return points.Select(p => new GhostPoint
            {
                At = new Vector2((float)p[0], (float)p[1]), Facing = (float)p[2], Anim = (int)(float)p[3], Speed = (float)p[4],
            }).ToList();
        }

        LabGhost BuildLabGhost(string name, Transform lane, Movement player, List<GhostPoint> path, Color tint, string label)
        {
            var root = new GameObject(name);
            root.transform.SetParent(lane, false);
            // Cloned under an inactive holder, so none of the copied scripts wakes up before it is stripped.
            var holder = new GameObject("ghost holder");
            holder.SetActive(false);
            holder.transform.SetParent(root.transform, false);
            GameObject visual = Instantiate(player.animator.gameObject, holder.transform, false);
            visual.name = "ghost sprite";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            foreach (MonoBehaviour script in visual.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(script);
            // Only the body: the ability indicators under it stay off (the Sprite child has no collider, sound or particles).
            foreach (Transform child in visual.transform) child.gameObject.SetActive(false);
            var renderer = visual.GetComponent<SpriteRenderer>();
            if (renderer != null)
            {
                renderer.color = tint;
                Material material = GhostMaterial();
                if (material != null) renderer.sharedMaterial = material;
                LabSorting(renderer, LabGhostRank);
            }
            Animator animator = visual.GetComponent<Animator>();
            animator.fireEvents = false;
            animator.applyRootMotion = false;
            holder.SetActive(true);
            Vector3 playerScale = player.transform.lossyScale;
            return new LabGhost
            {
                Root = root.transform, Sprite = visual.transform, Animator = animator, Path = path, Label = label, Tint = tint,
                Scale = new Vector2(Mathf.Abs(playerScale.x), playerScale.y),
            };
        }

        /// <summary>
        /// Every ghost at its place on the shared clock: 1x play time, which keeps running while time stands still (a dash
        /// freeze, a pause) because the paths carry a point per tick's worth of those frames. One loop is the longest path
        /// plus <see cref="GhostHoldTicks"/>, so the ghosts start over together; a shorter one holds its end until then.
        /// </summary>
        void AnimateLabGhosts()
        {
            // A frozen frame (TimeStop, a pause) counts as the frame the game's own freeze counts: the fixed clock's step in a
            // paced playback, so the ghosts stay beside the player whatever the wall clock does; the wall clock's in hand play.
            _lab.GhostClock += Time.timeScale > 0f ? Time.deltaTime : FixedFrameStep();
            float ticks = _lab.GhostClock / Time.fixedDeltaTime;
            float cycle = _lab.Ghosts.Max(g => g.Path.Count) - 1 + GhostHoldTicks;
            int loop = (int)(ticks / cycle);
            foreach (LabGhost g in _lab.Ghosts) AnimateLabGhost(g, ticks - loop * cycle, loop);
        }

        /// <summary>
        /// One ghost at <paramref name="at"/> ticks into its path (interpolated, held at the end), with the tick's Animator state
        /// and speed, and the squash-and-stretch Movement gives the sprite on a jump, a landing and a fall.
        /// </summary>
        void AnimateLabGhost(LabGhost g, float at, int loop)
        {
            List<GhostPoint> path = g.Path;
            float t = Mathf.Min(at, path.Count - 1);
            int i = Mathf.Min((int)t, path.Count - 2);
            GhostPoint a = path[i], b = path[i + 1];
            Vector2 position = Vector2.Lerp(a.At, b.At, t - i);
            g.Root.localPosition = new Vector3(position.x, position.y, 0f);
            g.Root.localScale = new Vector3(g.Scale.x * (b.Facing < 0f ? -1f : 1f), g.Scale.y, 1f);
            if (loop != g.Loop)
            {
                g.Loop = loop;
                g.Anim = -1;
                g.Squash = Squash.None;
                g.LastScale = Vector2.one;
            }
            if (b.Anim != g.Anim)
            {
                int before = g.Anim;
                g.Animator.SetInteger("Animation", b.Anim);
                // A jump (ground, wall or air) turns the state to rising; a landing turns falling, rising or a dash into idle or run.
                if (b.Anim == 5 && before >= 0 && before != 5) StartSquash(g, Squash.Jump);
                else if ((b.Anim == 0 || b.Anim == 1) && (before == 2 || before == 5 || before == 6)) StartSquash(g, Squash.Land);
                g.Anim = b.Anim;
            }
            g.Animator.speed = b.Speed;

            Vector2 scale = Vector2.one;
            float offset = 0f, since = _lab.GhostClock - g.SquashStart;
            if (g.Squash == Squash.Jump)
            {
                // JumpingSquashStretch: from (0.78, 1.19) back to 1 at 8.2 a second.
                float k = since * 8.2f;
                if (k < 1f) scale = Vector2.Lerp(new Vector2(0.78f, 1.19f), Vector2.one, k);
                else g.Squash = Squash.None;
            }
            else if (g.Squash == Squash.Land)
            {
                // LandingSquashStretch: to (y × 1.2, x × 0.9) of the scale it lands with and back, faster the more it stretched.
                Vector2 from = g.SquashFrom, to = new Vector2(from.y * 1.2f, from.x * 0.9f);
                float k = since * (2f + 60f * (from.y - 0.9f));
                if (k < 1f) scale = Vector2.Lerp(from, to, k);
                else if (k < 2f) scale = Vector2.Lerp(to, Vector2.one, k - 1f);
                else g.Squash = Squash.None;
                offset = (scale.y - 1f) * 50f;
            }
            if (g.Squash == Squash.None && b.Anim == 2)
            {
                // FallingSquashStretch: stretched by the fall speed past 300.
                float vy = (b.At.y - a.At.y) / Time.fixedDeltaTime;
                float stretch = Mathf.Sqrt(Mathf.Max(-vy - 300f, 0f)) * 0.0028f;
                scale = new Vector2(1f - stretch, 1f + stretch);
            }
            g.Sprite.localScale = new Vector3(scale.x, scale.y, 1f);
            g.Sprite.localPosition = new Vector3(0f, offset, 0f);
            g.LastScale = scale;
        }

        void StartSquash(LabGhost g, Squash kind)
        {
            g.Squash = kind;
            g.SquashStart = _lab.GhostClock;
            g.SquashFrom = g.LastScale;
        }

        /// <summary>
        /// The ghosts' key: one row at the bottom centre of the screen, a swatch in each labelled ghost's colour and its label
        /// (the lab's <c>ghosts</c>), so the ghosts themselves carry only their colour and nothing covers them.
        /// </summary>
        void DrawLabGhostLegend(float scale)
        {
            float pad = 10f * scale, swatch = 18f * scale, gap = 30f * scale;
            float height = Mathf.Max(swatch, _overlayText.CalcSize(new GUIContent("Ag")).y) + pad;
            var items = _lab.Ghosts.Where(g => !string.IsNullOrEmpty(g.Label))
                .Select(g => (g, width: _overlayText.CalcSize(new GUIContent(g.Label)).x)).ToList();
            if (items.Count == 0) return;
            float total = items.Sum(i => swatch + 8f * scale + i.width) + gap * (items.Count - 1) + pad * 2f;
            float x = (Screen.width - total) / 2f, y = Screen.height - height - 16f * scale;
            Fill(new Rect(x, y, total, height), new Color(0f, 0f, 0f, 0.65f));
            x += pad;
            foreach (var (g, width) in items)
            {
                Fill(new Rect(x, y + (height - swatch) / 2f, swatch, swatch), new Color(g.Tint.r, g.Tint.g, g.Tint.b, 1f));
                x += swatch + 8f * scale;
                Label(new Rect(x, y, width + 4f * scale, height), g.Label, _overlayText, Color.white);
                x += width + gap;
            }
        }

        bool LabGhostOverlayShown { get { return _lab != null && _lab.Ghosts.Any(g => !string.IsNullOrEmpty(g.Label) || !string.IsNullOrEmpty(g.Badge)); } }

        GUIStyle _ghostBadgeStyle;

        /// <summary>
        /// Each ghost's badge (the lab's <c>ghosts</c>, "KB" for a keyboard's inputs) on its head, bold white with a dark outline,
        /// following the sprite through its squash and stretch.
        /// </summary>
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
                Vector3 s = cam.WorldToScreenPoint(g.Sprite.position + new Vector3(0f, 14f * g.Sprite.lossyScale.y, 0f));
                if (s.z < 0f) continue;
                var at = new Rect(s.x - w / 2f, Screen.height - s.y - h / 2f, w, h);
                foreach (Vector2 d in new[] { new Vector2(-o, 0f), new Vector2(o, 0f), new Vector2(0f, -o), new Vector2(0f, o) })
                    Label(new Rect(at.x + d.x, at.y + d.y, w, h), g.Badge, _ghostBadgeStyle, new Color(0f, 0f, 0f, 0.9f));
                Label(at, g.Badge, _ghostBadgeStyle, Color.white);
            }
        }
    }
}
