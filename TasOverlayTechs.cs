using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    // The input list marks the ticks where a playback executes a named tech: a colored bar beside those rows, the name
    // written along it, and the run's techs under the clock. Every tech is read from the game's own state as it happens
    // (read-only hooks on the Movement methods that perform it); nothing is inferred from the route's inputs.
    public sealed partial class TasPlugin
    {
        enum Tech
        {
            FlipJump,          // this repo: a facing flip against a wall makes the ground rays hit mid-air (onGround while rising), and a ground jump follows
            CoyoteJump,        // a ground jump after the ground rays stopped hitting (coyoteFrames < maxCoyoteFrames)
            WallCoyoteJump,    // a wall jump after the wall rays stopped hitting (wallCoyoteFrames < maxWallCoyoteFrames)
            SuperJump,         // community "dash jump (the super)": Movement.CheckForSuperJump on a ground dash
            ReversedSuper,     // docs/human-input.md: the super jump's stick opposes the dash
            BackwardsDashJump, // docs/human-input.md: dash and jump on one tick, turned away from a wall right behind
            Hyper,             // the game's canHyper branch of CheckForSuperJump (a down-diagonal dash landing)
            Ultra,             // the hyper that adds the pre-dash momentum (the game logs "Did an ultra!")
            DashBounce,        // a ground jump inside DashBounceFrames (a straight-down dash onto the ground)
            Superbounce,       // docs/human-input.md: dash into a wall and jump, SpecialWallJump(0.6, …) (the game's dash boost)
            WallBounce,        // the game's wallBouncePossible: an up-diagonal dash into a wall and jump, SpecialWallJump(1, …)
            SpringDash,        // enginesim springDash: a dash that cancels a sideways spring's lock
            SpikeGrace,        // inside a spike for the ticks the spike's graceFrames allow
        }

        static readonly string[] TechNames =
        {
            "FLIP JUMP", "COYOTE JUMP", "WALL COYOTE JUMP", "SUPER JUMP", "REVERSED SUPER", "BACKWARDS DASH-JUMP", "HYPER", "ULTRA",
            "DASH BOUNCE", "SUPERBOUNCE", "WALL BOUNCE", "SPRING DASH", "SPIKE GRACE",
        };

        static readonly Color[] TechColors =
        {
            Hex(0xff3b3b, 1f), Hex(0xffc93c, 1f), Hex(0xa3e635, 1f), Hex(0x38bdf8, 1f), Hex(0x6366f1, 1f), Hex(0xc084fc, 1f), Hex(0xf472b6, 1f),
            Hex(0xfb923c, 1f), Hex(0xfde68a, 1f), Hex(0x10b981, 1f), Hex(0x2dd4bf, 1f), Hex(0xd4a373, 1f), Hex(0x86efac, 1f),
        };

        sealed class TechSpan
        {
            public Tech tech;
            public long start, end;
            public int lane;
        }

        const int TechLanes = 3;
        const int FlipWindowTicks = 4;
        const long NoTick = long.MinValue / 2;
        static readonly FieldInfo TechJumpBufferField = AccessTools.Field(typeof(Movement), "jumpBuffer");
        static readonly FieldInfo TechCoyoteField = AccessTools.Field(typeof(Movement), "coyoteFrames");
        static readonly FieldInfo TechWallCoyoteField = AccessTools.Field(typeof(Movement), "wallCoyoteFrames");
        static readonly FieldInfo TechDashBounceField = AccessTools.Field(typeof(Movement), "DashBounceFrames");
        static readonly FieldInfo TechCanHyperField = AccessTools.Field(typeof(Movement), "canHyper");
        static readonly FieldInfo TechDashDirectionField = AccessTools.Field(typeof(Movement), "dashDirection");
        static readonly FieldInfo TechPreDashMomentumField = AccessTools.Field(typeof(Movement), "preDashMomentum");
        static readonly FieldInfo TechLastHyperYField = AccessTools.Field(typeof(Movement), "lastHyperY");

        readonly List<TechSpan> _techSpans = new List<TechSpan>();
        readonly List<(Tech tech, long start)> _techPending = new List<(Tech, long)>();
        readonly int[] _techCounts = new int[TechNames.Length];
        readonly List<Tech> _techOrder = new List<Tech>();
        readonly RaycastHit2D[] _techHits = new RaycastHit2D[8];
        GUIStyle _overlayTech;

        // Before the tick (OverlayBeforeTick) and at the end of the previous one (OverlayTick).
        bool _techPreGround, _techPreDashing, _techPreSpring, _techWasSpring, _techFacing;
        float _techEndVelocityY;
        // Overlay ticks where the setup of a tech began.
        long _techLastFlip = NoTick, _techFlipStart = NoTick, _techDashStart = NoTick, _techSpringStart = NoTick, _techSpringCancel = NoTick;
        long _techLastGround = NoTick, _techLastWall = NoTick, _techBackwardsDash = NoTick, _techBackwardsStart = NoTick;
        // Inside a hooked call.
        bool _techInputJumpBuffer, _techInputGround, _techWallKick;
        float _techInputCoyote, _techInputWallCoyote;
        int _techInputAirJumps, _techInputWallJumps, _techInputDashBounce;
        bool _techSuperJumpBuffer, _techSuperHyper, _techSuperUltra;

        void PatchTechHooks()
        {
            PatchOptional(typeof(Movement), "getPlayerControlledInput", nameof(BeforeTechInput), nameof(AfterTechInput));
            PatchOptional(typeof(Movement), "CheckForSuperJump", nameof(BeforeTechSuperJump), nameof(AfterTechSuperJump));
            PatchOptional(typeof(Movement), "SpecialWallJump", nameof(BeforeTechWallKick), null);
        }

        static bool TechWatching(Movement player)
        {
            return Instance != null && Instance.OverlayListsRun && !IsMenuPlayer(player);
        }

        // The tick the hooks run in: OverlayTick counts it after FixedUpdate.
        long TechTick { get { return _overlayTicks + 1; } }

        static T TechRead<T>(FieldInfo field, Movement player)
        {
            return field != null && field.GetValue(player) is T value ? value : default(T);
        }

        private static void BeforeTechInput(Movement __instance)
        {
            if (!TechWatching(__instance)) return;
            TasPlugin p = Instance;
            p._techInputJumpBuffer = TechRead<bool>(TechJumpBufferField, __instance);
            p._techInputGround = __instance.onGround;
            p._techInputCoyote = TechRead<float>(TechCoyoteField, __instance);
            p._techInputWallCoyote = TechRead<float>(TechWallCoyoteField, __instance);
            p._techInputAirJumps = __instance.airJumpsLeft;
            p._techInputWallJumps = __instance.wallJumpsLeft;
            p._techInputDashBounce = TechRead<int>(TechDashBounceField, __instance);
            p._techWallKick = false;
        }

        private static void AfterTechInput(Movement __instance)
        {
            if (!TechWatching(__instance)) return;
            TasPlugin p = Instance;
            if (!p._techInputJumpBuffer || TechRead<bool>(TechJumpBufferField, __instance) || p._techWallKick) return;
            long tick = p.TechTick;
            if (__instance.wallJumpsLeft < p._techInputWallJumps)
            {
                if (p._techInputWallCoyote < __instance.maxWallCoyoteFrames) p.PendTech(Tech.WallCoyoteJump, p._techLastWall + 1);
            }
            else if (__instance.airJumpsLeft < p._techInputAirJumps) { }
            else if (p._techInputGround)
            {
                if (p._techInputDashBounce > 0) p.PendTech(Tech.DashBounce, p._techDashStart);
                else if (p._techInputCoyote < __instance.maxCoyoteFrames) p.PendTech(Tech.CoyoteJump, p._techLastGround + 1);
                else if (!p._techPreGround && p._techEndVelocityY > 0f && tick - p._techLastFlip <= FlipWindowTicks)
                    p.PendTech(Tech.FlipJump, p._techFlipStart);
            }
        }

        private static void BeforeTechSuperJump(Movement __instance)
        {
            if (!TechWatching(__instance)) return;
            TasPlugin p = Instance;
            p._techSuperJumpBuffer = TechRead<bool>(TechJumpBufferField, __instance);
            p._techSuperHyper = TechRead<bool>(TechCanHyperField, __instance);
            Vector2 dash = TechRead<Vector2>(TechDashDirectionField, __instance);
            // CheckForSuperJump lifts the player 2 units before it compares with lastHyperY.
            p._techSuperUltra = p._techSuperHyper && Mathf.Sign(TechRead<Vector2>(TechPreDashMomentumField, __instance).x) == Mathf.Sign(dash.x)
                && __instance.transform.position.y + 2f < TechRead<float>(TechLastHyperYField, __instance) - 4f;
        }

        private static void AfterTechSuperJump(Movement __instance)
        {
            if (!TechWatching(__instance)) return;
            TasPlugin p = Instance;
            if (!p._techSuperJumpBuffer || TechRead<bool>(TechJumpBufferField, __instance)) return;
            if (p._techSuperUltra) p.PendTech(Tech.Ultra, p._techDashStart);
            else if (p._techSuperHyper) p.PendTech(Tech.Hyper, p._techDashStart);
            else if (p._techBackwardsDash == p._techDashStart && p._techDashStart != NoTick) p.PendTech(Tech.BackwardsDashJump, p._techBackwardsStart);
            else
            {
                float stick = __instance.MoveAxis.x, dash = TechRead<Vector2>(TechDashDirectionField, __instance).x;
                p.PendTech(stick != 0f && dash != 0f && Mathf.Sign(stick) != Mathf.Sign(dash) ? Tech.ReversedSuper : Tech.SuperJump, p._techDashStart);
            }
        }

        private static void BeforeTechWallKick(Movement __instance, float __0)
        {
            if (!TechWatching(__instance)) return;
            TasPlugin p = Instance;
            p._techWallKick = true;
            p.PendTech(__0 >= 1f ? Tech.WallBounce : Tech.Superbounce, p._techDashStart);
        }

        void PendTech(Tech tech, long start)
        {
            _techPending.Add((tech, start));
        }

        void ResetTechs(Movement player)
        {
            _techSpans.Clear();
            _techPending.Clear();
            _techOrder.Clear();
            System.Array.Clear(_techCounts, 0, _techCounts.Length);
            _techLastFlip = _techFlipStart = _techDashStart = _techSpringStart = _techSpringCancel = NoTick;
            _techLastGround = _techLastWall = _techBackwardsDash = _techBackwardsStart = NoTick;
            _techWasSpring = false;
            _techFacing = player.facingRight;
            _techEndVelocityY = player.Velocity.y;
        }

        void TechBeforeTick(Movement player)
        {
            if (!OverlayListsRun) return;
            _techPreGround = player.onGround;
            _techPreDashing = player.dashActive;
            _techPreSpring = player.cutsceneMode == Movement.cutsceneModes.spring;
        }

        // After the tick's row is added: `tick` is its number.
        void TechAfterTick(Movement player, long tick, bool jumpPress, bool dashPress)
        {
            if (_techPreSpring && !_techWasSpring) _techSpringStart = tick;
            _techWasSpring = _techPreSpring;
            // The spring branch ends the lock on a buffered dash; the dash itself starts on the next tick's input.
            if (_techPreSpring && dashPress && player.cutsceneMode == Movement.cutsceneModes.none) _techSpringCancel = tick;
            if (player.facingRight != _techFacing)
            {
                if (tick - _techLastFlip > FlipWindowTicks) _techFlipStart = tick;
                _techLastFlip = tick;
                _techFacing = player.facingRight;
            }
            if (player.onGround && TechRead<float>(TechCoyoteField, player) >= player.maxCoyoteFrames) _techLastGround = tick;
            if (player.OnWall && TechRead<float>(TechWallCoyoteField, player) >= player.maxWallCoyoteFrames) _techLastWall = tick;

            if (player.dashActive && !_techPreDashing && player.framesSinceLastDash == 0f)
            {
                _techDashStart = tick;
                if (tick - _techSpringCancel <= 1) AddTech(Tech.SpringDash, _techSpringStart, tick);
                Vector2 dash = TechRead<Vector2>(TechDashDirectionField, player);
                // The dash went away from the facing it had before the turn, and the dash start's 20-unit shift back
                // put the player into the wall behind. The turn is 0-4 ticks: a flip on the dash tick is the 0-tick one.
                if (jumpPress && _techPreGround && dash.x != 0f && tick - _techLastFlip <= FlipWindowTicks && WallBehind(player, -Mathf.Sign(dash.x)))
                {
                    _techBackwardsDash = tick;
                    _techBackwardsStart = _techFlipStart;
                }
            }

            foreach (var (tech, start) in _techPending) AddTech(tech, start, tick);
            _techPending.Clear();

            if (_grazeTicks > 0 && !_grazeKilled) ExtendTech(Tech.SpikeGrace, tick);
            _techEndVelocityY = player.Velocity.y;
        }

        bool WallBehind(Movement player, float side)
        {
            int mask = Physics2D.GetLayerCollisionMask(player.gameObject.layer);
            var filter = new ContactFilter2D { useTriggers = false, useLayerMask = true, layerMask = mask };
            Rigidbody2D own = player.GetComponent<Rigidbody2D>();
            int count = Physics2D.Raycast(player.transform.position, new Vector2(side, 0f), filter, _techHits, 40f);
            for (int i = 0; i < count; i++)
                if (_techHits[i].rigidbody != own) return true;
            return false;
        }

        void ExtendTech(Tech tech, long tick)
        {
            for (int i = _techSpans.Count - 1; i >= 0; i--)
            {
                TechSpan span = _techSpans[i];
                if (span.end < tick - 1) break;
                if (span.tech == tech && span.end == tick - 1) { span.end = tick; return; }
            }
            AddTech(tech, tick, tick);
        }

        void AddTech(Tech tech, long start, long end)
        {
            int rows = InputRowSlots;
            if (start == NoTick || start > end || end - start > rows) start = end;
            if (start < 1) start = 1;
            var used = new bool[TechLanes];
            foreach (TechSpan other in _techSpans)
                if (other.end >= start && other.start <= end) used[other.lane] = true;
            int lane = 0;
            while (lane < TechLanes - 1 && used[lane]) lane++;
            _techSpans.Add(new TechSpan { tech = tech, start = start, end = end, lane = lane });
            if (_techCounts[(int)tech]++ == 0) _techOrder.Add(tech);
            // Spans that scrolled out of the list are gone for good.
            _techSpans.RemoveAll(s => s.end < end - rows);
        }

        string TechSummary(Tech tech)
        {
            int count = _techCounts[(int)tech];
            return count > 1 && tech != Tech.SpikeGrace ? TechNames[(int)tech] + " ×" + count : TechNames[(int)tech];
        }

        void DrawTechSummary(float x, float y, float width, float height, float scale)
        {
            float gap = 10 * scale, right = x + width;
            foreach (Tech tech in _techOrder)
            {
                string text = TechSummary(tech);
                OverlayMeasure.text = text;
                if (x + _overlaySmall.CalcSize(OverlayMeasure).x > right) { Segment(x, y, height, gap, "…", _overlaySmall, MutedColor); return; }
                x = Segment(x, y, height, gap, text, _overlaySmall, TechColors[(int)tech]);
            }
        }

        // Bars beside the rows: rows run newest (top, tick `newest`) to oldest, `rowHeight` apart from `top`.
        void DrawTechBars(float left, float top, float rowHeight, long newest, int shown, float scale)
        {
            if (_techSpans.Count == 0) return;
            if (_overlayTech == null || _overlayTech.fontSize != Mathf.RoundToInt(12 * scale))
                _overlayTech = new GUIStyle(_overlaySmall) { fontSize = Mathf.RoundToInt(12 * scale), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
            long oldest = newest - shown + 1;
            float pitch = 16 * scale, bar = 3 * scale;
            foreach (TechSpan span in _techSpans)
            {
                long end = System.Math.Min(span.end, newest), start = System.Math.Max(span.start, oldest);
                if (end < start) continue;
                float y0 = top + (newest - end) * rowHeight + 3 * scale, y1 = top + (newest - start + 1) * rowHeight - 3 * scale;
                float x = left + span.lane * pitch;
                Color color = TechColors[(int)span.tech];
                Fill(new Rect(x, y0, bar, y1 - y0), color);
                // The name reads bottom to top along the bar, as long as the bar lets it.
                Rect label = new Rect(0f, 0f, y1 - y0, pitch - bar);
                Vector2 pivot = new Vector2(x + bar + (pitch - bar) / 2f, (y0 + y1) / 2f);
                Matrix4x4 saved = GUI.matrix;
                GUIUtility.RotateAroundPivot(-90f, pivot);
                label.center = pivot;
                Label(label, TechNames[(int)span.tech], _overlayTech, color);
                GUI.matrix = saved;
            }
        }
    }
}
