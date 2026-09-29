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
        /// Hand play's ghost: a see-through copy of the player's own animated sprite (the Sprite child: its SpriteRenderer and
        /// Animator, every script, collider and particle system stripped) replaying the demo's path on a loop at the physics
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
            public float Start;
            public int Loop = -1, Anim = -1;
            public Squash Squash;
            public float SquashStart;
            public Vector2 SquashFrom, LastScale = Vector2.one;
        }

        enum Squash { None, Jump, Land }

        /// <summary>Ticks the ghost holds its last position before the loop starts over.</summary>
        const int GhostHoldTicks = 25;

        static readonly Color GhostTint = new Color(0.55f, 0.9f, 1f, 0.5f);

        /// <summary>
        /// Builds the ghost from the solved demo's path (labmaps/&lt;id&gt;.demo.json <c>path</c>: [x, y, facing, animation,
        /// animator speed] per point, from <c>lab solve</c> or <c>lab replay --write-path</c>) and draws the path as a faint line.
        /// Nothing to collide with: sprites only.
        /// </summary>
        void BuildLabGhost(LabMapDef lab, Movement player)
        {
            string file = LabMapFile(lab.id + ".demo.json");
            if (file == null || player.animator == null) return;
            JArray points = JObject.Parse(File.ReadAllText(file))["path"] as JArray;
            if (points == null || points.Count < 2) return;
            if (points.Any(p => ((JArray)p).Count < 5))
            {
                Logger.LogWarning("Lab map " + lab.id + "'s demo path has no animation states: rewrite it with `IGTAP.EngineSim lab replay ... --write-path`.");
                return;
            }
            var path = points.Select(p => new GhostPoint
            {
                At = new Vector2((float)p[0], (float)p[1]), Facing = (float)p[2], Anim = (int)(float)p[3], Speed = (float)p[4],
            }).ToList();
            var line = new GameObject(lab.id + " ghost path");
            line.transform.SetParent(_lab.Root.transform, false);
            LabLine(line.transform, path.Select(p => p.At).ToArray(), false, new Color(1f, 1f, 1f, 0.35f), 3f);

            var root = new GameObject(lab.id + " ghost");
            root.transform.SetParent(_lab.Root.transform, false);
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
                renderer.color = GhostTint;
                Material material = LabMaterial();
                if (material != null) renderer.sharedMaterial = material;
                LabSorting(renderer);
            }
            Animator animator = visual.GetComponent<Animator>();
            animator.fireEvents = false;
            animator.applyRootMotion = false;
            holder.SetActive(true);
            Vector3 playerScale = player.transform.lossyScale;
            _lab.Ghost = new LabGhost
            {
                Root = root.transform, Sprite = visual.transform, Animator = animator, Path = path,
                Scale = new Vector2(Mathf.Abs(playerScale.x), playerScale.y), Start = Time.time,
            };
            AnimateLabGhost();
        }

        /// <summary>
        /// The ghost at its place along the path (one point per physics tick, interpolated, held at the end, looped), with the
        /// tick's Animator state and speed, and the squash-and-stretch Movement gives the sprite on a jump, a landing and a fall.
        /// </summary>
        void AnimateLabGhost()
        {
            LabGhost g = _lab.Ghost;
            List<GhostPoint> path = g.Path;
            float ticks = (Time.time - g.Start) / Time.fixedDeltaTime;
            float cycle = path.Count - 1 + GhostHoldTicks;
            int loop = (int)(ticks / cycle);
            float t = Mathf.Min(ticks - loop * cycle, path.Count - 1);
            int i = Mathf.Min((int)t, path.Count - 2);
            GhostPoint a = path[i], b = path[i + 1];
            Vector2 at = Vector2.Lerp(a.At, b.At, t - i);
            g.Root.localPosition = new Vector3(at.x, at.y, 0f);
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
            float offset = 0f, since = Time.time - g.SquashStart;
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

        static void StartSquash(LabGhost g, Squash kind)
        {
            g.Squash = kind;
            g.SquashStart = Time.time;
            g.SquashFrom = g.LastScale;
        }
    }
}
