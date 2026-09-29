using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        enum Tech
        {
            GcWallClip,
            CoyoteJump,
            WallCoyoteJump,
            SuperJump,
            ReversedSuper,
            BackwardsDashJump,
            Wavedash,
            Ultra,
            DashBounce,
            WaveBounce,
            Superbounce,
            WallBounce,
            SpringDash,
            SpikeGrace,
            UltraBonus,
            SuperGroundBounce,
            WallDash,
            InwallWallBounce,
            FloatUltra,
            FreezeAim,
            StoredWallJump,
            HighSuper,
            SuperbounceDoubleJump,
            BoostedDoubleJump,
            Pauselift,
            DashCoyoteJump,
            CheckpointCoyoteJump,
            ZipIntangible,
            KbWallClip,
            FinalClingFloat,
        }

        static readonly string[] TechNames =
        {
            "GC WALL CLIP", "COYOTE JUMP", "WALL COYOTE JUMP", "SUPER JUMP", "REVERSED SUPER", "BACKWARDS DASH-JUMP", "HYPER", "CHAINED HYPER",
            "GROUND BOUNCE", "HYPER GROUND BOUNCE", "SUPERBOUNCE", "WALL BOUNCE", "SPRING DASH", "SPIKE GRACE", "ULTRA", "SUPER GROUND BOUNCE", "WALL DASH",
            "INWALL WALLBOUNCE", "FLOAT ULTRA", "FREEZE AIM", "STORED WALL JUMP", "HIGH SUPER", "SUPERBOUNCE DOUBLE JUMP", "BOOSTED DOUBLE JUMP",
            "PAUSELIFT", "DASH COYOTE JUMP", "CHECKPOINT COYOTE JUMP", "ZIP INTANGIBLE", "KB WALL CLIP", "FINAL-CLING FLOAT",
        };

        static readonly Color[] TechColors =
        {
            Hex(0xff3b3b, 1f), Hex(0xffc93c, 1f), Hex(0xa3e635, 1f), Hex(0x38bdf8, 1f), Hex(0x6366f1, 1f), Hex(0xc084fc, 1f), Hex(0xf472b6, 1f),
            Hex(0xfb923c, 1f), Hex(0xfde68a, 1f), Hex(0xe879f9, 1f), Hex(0x10b981, 1f), Hex(0x2dd4bf, 1f), Hex(0x60a5fa, 1f), Hex(0xd4a373, 1f),
            Hex(0x86efac, 1f), Hex(0xf43f5e, 1f), Hex(0xfacc15, 1f), Hex(0x94a3b8, 1f), Hex(0x22d3ee, 1f), Hex(0xa78bfa, 1f), Hex(0x84cc16, 1f),
            Hex(0x7dd3fc, 1f), Hex(0x34d399, 1f), Hex(0xf9a8d4, 1f), Hex(0xef4444, 1f), Hex(0xfcd34d, 1f), Hex(0xbef264, 1f), Hex(0x818cf8, 1f),
            Hex(0xfb7185, 1f), Hex(0x67e8f9, 1f),
        };

        sealed class TechSpan
        {
            public Tech tech;
            public long start, end;
            public long input;
            public long savestateInput;
            public Savestate savestate;
            public int lane;
        }

        const int TechLanes = 3;
        const int FlipWindowTicks = 4;
        /// <summary>The most ticks between two climbs of one inwall chain, and between its last climb and the wallbounce.</summary>
        const int InwallChainTicks = 6;
        const int TechDemoLeadTicks = 25;
        const int TechStateTicks = 256;
        const long NoTick = long.MinValue / 2;
        static readonly FieldInfo TechJumpBufferField = AccessTools.Field(typeof(Movement), "jumpBuffer");
        static readonly FieldInfo TechCoyoteField = AccessTools.Field(typeof(Movement), "coyoteFrames");
        static readonly FieldInfo TechWallCoyoteField = AccessTools.Field(typeof(Movement), "wallCoyoteFrames");
        static readonly FieldInfo TechDashBounceField = AccessTools.Field(typeof(Movement), "DashBounceFrames");
        static readonly FieldInfo TechCanHyperField = AccessTools.Field(typeof(Movement), "canHyper");
        static readonly FieldInfo TechDashDirectionField = AccessTools.Field(typeof(Movement), "dashDirection");
        static readonly FieldInfo TechPreDashMomentumField = AccessTools.Field(typeof(Movement), "preDashMomentum");
        static readonly FieldInfo TechLastHyperYField = AccessTools.Field(typeof(Movement), "lastHyperY");
        static readonly FieldInfo TechFinalClingField = AccessTools.Field(typeof(Movement), "isFinalWallClingActive");
        static readonly FieldInfo TechWallJumpFacingField = AccessTools.Field(typeof(Movement), "wallJumpFacing");

        readonly List<TechSpan> _techSpans = new List<TechSpan>();
        readonly List<TechSpan> _techSeen = new List<TechSpan>();
        MacroDefinition _techSeenRoute;
        bool _techSeenFilled;
        readonly List<(Tech tech, long start)> _techPending = new List<(Tech, long)>();
        // The last TechStateTicks input ticks' states, oldest at _techStateHead (a ring: a new one replaces the oldest when full).
        readonly (long input, Savestate state)[] _techStates = new (long, Savestate)[TechStateTicks];
        int _techStateHead, _techStateCount;
        readonly int[] _techCounts = new int[TechNames.Length];
        readonly List<Tech> _techOrder = new List<Tech>();
        readonly RaycastHit2D[] _techHits = new RaycastHit2D[8];
        GUIStyle _overlayTech;

        bool _techPreGround, _techPreDashing, _techPreSpring, _techWasSpring, _techFacing, _techDashAtLastGround, _techPauselifted;
        float _techEndVelocityY, _techFlipStick;
        long _techLastFlip = NoTick, _techFlipStart = NoTick, _techDashStart = NoTick, _techSpringStart = NoTick, _techSpringCancel = NoTick;
        long _techLastGround = NoTick, _techLastWall = NoTick, _techBackwardsDash = NoTick, _techBackwardsStart = NoTick;
        long _techLastSuperbounce = NoTick, _techRespawnTick = NoTick;
        bool _techWaveAir, _techDashInWave, _techSuperAir, _techDashInSuper;
        long _techWaveStart = NoTick, _techSuperStart = NoTick;
        long _techInwallStart = NoTick, _techInwallLast = NoTick;
        bool _techInputJumpBuffer, _techInputGround, _techInputOnWall, _techWallKick;
        float _techInputCoyote, _techInputWallCoyote, _techInputVelocityY;
        int _techInputAirJumps, _techInputWallJumps, _techInputDashBounce;
        bool _techSuperJumpBuffer, _techSuperHyper, _techSuperUltra, _techSuperFloat;
        // lastHyperY is in the scene frame, which a FloatingOrigin rebase moves: the origin's y when it was last written.
        float _techLastHyperY, _techLastHyperOriginY;

        void PatchTechHooks()
        {
            PatchOptional(typeof(Movement), "getPlayerControlledInput", nameof(BeforeTechInput), nameof(AfterTechInput));
            PatchOptional(typeof(Movement), "CheckForSuperJump", nameof(BeforeTechSuperJump), nameof(AfterTechSuperJump));
            PatchOptional(typeof(Movement), "SpecialWallJump", nameof(BeforeTechWallKick), null);
            PatchOptional(typeof(PlatformMover), "MakeIntangible", nameof(BeforeTechIntangible), null);
        }

        /// <summary>Techs are listed for a route's 1x playback and for manual play once its session started (the list's rows).</summary>
        bool TechListing { get { return OverlayListsRun || _overlayManual && OverlayListsManual; } }

        static bool TechWatching(Movement player)
        {
            return Instance != null && player != null && Instance.TechListing && !IsMenuPlayer(player);
        }

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
            p._techInputOnWall = __instance.OnWall;
            p._techInputCoyote = TechRead<float>(TechCoyoteField, __instance);
            p._techInputWallCoyote = TechRead<float>(TechWallCoyoteField, __instance);
            p._techInputAirJumps = __instance.airJumpsLeft;
            p._techInputWallJumps = __instance.wallJumpsLeft;
            p._techInputDashBounce = TechRead<int>(TechDashBounceField, __instance);
            p._techInputVelocityY = __instance.Velocity.y;
            p._techWallKick = false;
        }

        private static void AfterTechInput(Movement __instance)
        {
            if (!TechWatching(__instance)) return;
            TasPlugin p = Instance;
            if (!p._techInputJumpBuffer || TechRead<bool>(TechJumpBufferField, __instance) || p._techWallKick) return;
            long tick = p.TechTick;
            // The last cling's no-gravity flag still on at a jump: only a refill of the wall jumps (a Ground floor or wall, a
            // spring, an orb) after that cling keeps it past the air branch, so the jump rises with no gravity until its 0.7 s end.
            if (TechRead<bool>(TechFinalClingField, __instance)) p.PendTech(Tech.FinalClingFloat, tick);
            if (__instance.wallJumpsLeft < p._techInputWallJumps)
            {
                // Off no wall: OnWall off with a count a landing kept (the ground branch clears OnWall but never counts
                // wallCoyoteFrames down) is the stored wall jump; OnWall held with the count below its max (the rays missed
                // this tick) is the wall coyote jump.
                if (!p._techInputOnWall && p._techInputWallCoyote > 0f) p.PendTech(Tech.StoredWallJump, p._techLastWall + 1);
                else if (p._techInputWallCoyote < __instance.maxWallCoyoteFrames) p.PendTech(Tech.WallCoyoteJump, p._techLastWall + 1);
            }
            else if (__instance.airJumpsLeft < p._techInputAirJumps)
            {
                // A double jump at Velocity.y 200 or more fires only after a jump that wasn't a normal one (a hyper, a bounce,
                // a spring, an up-dash's end, a wall kick: the 700 gate), and it keeps half the speed it finds (745 + v/2).
                if (p._techInputVelocityY >= 200f)
                {
                    if (tick - p._techLastSuperbounce <= 2) p.PendTech(Tech.SuperbounceDoubleJump, p._techLastSuperbounce);
                    else p.PendTech(Tech.BoostedDoubleJump, tick);
                }
            }
            else if (p._techInputGround)
            {
                if (p._techInputDashBounce > 0)
                {
                    if (p._techDashInWave) p.PendTech(Tech.WaveBounce, p._techWaveStart);
                    else if (p._techDashInSuper) p.PendTech(Tech.SuperGroundBounce, p._techSuperStart);
                    else p.PendTech(Tech.DashBounce, p._techDashStart);
                }
                else if (p._techInputCoyote < __instance.maxCoyoteFrames)
                {
                    // A respawn sets onGround but keeps the coyote count: no full ground tick since it is a checkpoint's
                    // mid-air jump; a dash that was still on at the last ground tick carried the dot off the ledge.
                    if (p._techRespawnTick != NoTick && p._techLastGround <= p._techRespawnTick) p.PendTech(Tech.CheckpointCoyoteJump, p._techRespawnTick + 1);
                    else if (p._techDashAtLastGround) p.PendTech(Tech.DashCoyoteJump, p._techDashStart);
                    else p.PendTech(Tech.CoyoteJump, p._techLastGround + 1);
                }
                else if (!p._techPreGround && p._techEndVelocityY > 0f && tick - p._techLastFlip <= FlipWindowTicks)
                    p.PendTech(p._techFlipStick >= 1f ? Tech.KbWallClip : Tech.GcWallClip, p._techFlipStart);
            }
        }

        private static void BeforeTechSuperJump(Movement __instance)
        {
            if (!TechWatching(__instance)) return;
            TasPlugin p = Instance;
            p._techSuperJumpBuffer = TechRead<bool>(TechJumpBufferField, __instance);
            p._techSuperHyper = TechRead<bool>(TechCanHyperField, __instance);
            float lifted = __instance.transform.position.y + 2f, lastHyper = TechRead<float>(TechLastHyperYField, __instance);
            p._techSuperUltra = Mathf.Sign(TechRead<Vector2>(TechPreDashMomentumField, __instance).x) == Mathf.Sign(TechRead<Vector2>(TechDashDirectionField, __instance).x)
                && lifted < lastHyper - 4f;
            // A float ultra: the second hyper of a chain passes the game's check only because a rebase moved the dot since the
            // first one wrote lastHyperY: in the world frame it is not 4 lower.
            p._techSuperFloat = p._techSuperUltra && p._techDashInWave && lifted - CurrentOrigin().y >= lastHyper - p._techLastHyperOriginY - 4f;
        }

        private static void AfterTechSuperJump(Movement __instance)
        {
            if (!TechWatching(__instance)) return;
            TasPlugin p = Instance;
            if (!p._techSuperJumpBuffer || TechRead<bool>(TechJumpBufferField, __instance)) return;
            if (p._techSuperHyper)
            {
                if (p._techDashInWave) p.PendTech(Tech.Ultra, p._techWaveStart);
                else
                {
                    p.PendTech(Tech.Wavedash, p._techDashStart);
                    p._techWaveStart = p._techDashStart;
                }
                p._techWaveAir = true;
                if (p._techSuperUltra) p.PendTech(p._techSuperFloat ? Tech.FloatUltra : Tech.UltraBonus, p._techDashStart);
            }
            else if (p._techBackwardsDash == p._techDashStart && p._techDashStart != NoTick) p.PendTech(Tech.BackwardsDashJump, p._techBackwardsStart);
            else
            {
                Vector2 dash = TechRead<Vector2>(TechDashDirectionField, __instance);
                float stick = __instance.MoveAxis.x;
                // An up-diagonal omnidash still on the ground's coyote ticks: the super from as high as the dash got.
                p.PendTech(dash.y > 0f ? Tech.HighSuper
                    : stick != 0f && dash.x != 0f && Mathf.Sign(stick) != Mathf.Sign(dash.x) ? Tech.ReversedSuper : Tech.SuperJump, p._techDashStart);
                p._techSuperAir = true;
                p._techSuperStart = p._techDashStart;
            }
        }

        private static void BeforeTechWallKick(Movement __instance, float __0)
        {
            if (!TechWatching(__instance)) return;
            TasPlugin p = Instance;
            p._techWallKick = true;
            if (p.TechTick - p._techInwallLast <= InwallChainTicks)
            {
                p.PendTech(Tech.InwallWallBounce, p._techInwallStart);
                p._techInwallLast = NoTick;
            }
            else if (__0 >= 1f) p.PendTech(Tech.WallBounce, p._techDashStart);
            else
            {
                p.PendTech(Tech.Superbounce, p._techDashStart);
                p._techLastSuperbounce = p.TechTick;
            }
            // getPlayerControlledInput's dash boost off a count a landing kept (OnWall off, wallCoyoteFrames above 0): the
            // stored superbounce.
            if (!__instance.OnWall && TechRead<float>(TechWallCoyoteField, __instance) > 0f) p.PendTech(Tech.StoredWallJump, p._techLastWall + 1);
        }

        /// <summary>PlatformMover.MakeIntangible: a jump or wall jump touching a zip mover, or within its 7 coyote ticks of one.</summary>
        private static void BeforeTechIntangible()
        {
            TasPlugin p = Instance;
            if (p == null || !TechWatching(p._overlayPlayer)) return;
            p.PendTech(Tech.ZipIntangible, p.TechTick);
        }

        /// <summary>
        /// A step-up (BeforeSteppedUp, in a Movement.Update frame after this tick). An inwall climb is one whose
        /// forward rays looked the other way from the dot (wallJumpFacing, set at the top of the tick, against facingRight):
        /// a dash turned into the wall. The wallbounce that follows is the Inwall wallbounce; a climb with the pause menu
        /// open (no FixedUpdate runs, the step-up still does) is the Pauselift, marked once per pause.
        /// </summary>
        void TechSteppedUp(Movement player)
        {
            if (!TechWatching(player)) return;
            float rays = TechRead<Vector2>(TechWallJumpFacingField, player).x;
            if (rays == 0f || rays > 0f == player.facingRight) return;
            long tick = _overlayTicks;
            if (tick - _techInwallLast > InwallChainTicks) _techInwallStart = tick;
            _techInwallLast = tick;
            if (player.pauseMenu != null && player.pauseMenu.menuOpen && !_techPauselifted)
            {
                _techPauselifted = true;
                AddTech(Tech.Pauselift, _techInwallStart, tick);
            }
        }

        void PendTech(Tech tech, long start)
        {
            _techPending.Add((tech, start));
        }

        void ResetTechs(Movement player)
        {
            _techSpans.Clear();
            // The Tech tab's playback list (demos are cut from it) is the last route's; manual play leaves it alone.
            if (OverlayListsRun)
            {
                _techSeen.Clear();
                _techSeenRoute = _engine.ActiveMacro;
                _techSeenFilled = false;
            }
            _techPending.Clear();
            _techOrder.Clear();
            System.Array.Clear(_techCounts, 0, _techCounts.Length);
            _techLastFlip = _techFlipStart = _techDashStart = _techSpringStart = _techSpringCancel = NoTick;
            _techLastGround = _techLastWall = _techBackwardsDash = _techBackwardsStart = NoTick;
            _techLastSuperbounce = _techRespawnTick = NoTick;
            _techWasSpring = _techWaveAir = _techDashInWave = _techSuperAir = _techDashInSuper = false;
            _techDashAtLastGround = _techPauselifted = false;
            _techFlipStick = 0f;
            _techWaveStart = _techInwallStart = _techInwallLast = NoTick;
            _techFacing = player.facingRight;
            _techEndVelocityY = player.Velocity.y;
            _techLastHyperY = TechRead<float>(TechLastHyperYField, player);
            _techLastHyperOriginY = CurrentOrigin().y;
        }

        void TechBeforeTick(Movement player)
        {
            if (!TechListing) return;
            _techPreGround = player.onGround;
            _techPreDashing = player.dashActive;
            _techPreSpring = player.cutsceneMode == Movement.cutsceneModes.spring;
            // A demo's own savestate comes from a route's playback only (FillTechDemos); manual play keeps none.
            if (OverlayListsRun) CaptureTechState(player);
        }

        void CaptureTechState(Movement player)
        {
            long input = _engine.RunTicks;
            if (_techStateCount > 0)
            {
                long last = TechStateAt(_techStateCount - 1).input;
                if (last > input) _techStateHead = _techStateCount = 0;
                else if (last == input) _techStateCount--;
            }
            bool clean = !player.dashActive && player.cutsceneMode == Movement.cutsceneModes.none;
            var state = (input, clean ? SavestateService.Capture(player, "tech", MacroLibrary.CurrentSchemaVersion) : null);
            if (_techStateCount < TechStateTicks) _techStates[(_techStateHead + _techStateCount++) % TechStateTicks] = state;
            else
            {
                _techStates[_techStateHead] = state;
                _techStateHead = (_techStateHead + 1) % TechStateTicks;
            }
        }

        /// <summary>The i-th oldest captured state.</summary>
        (long input, Savestate state) TechStateAt(int i) { return _techStates[(_techStateHead + i) % TechStateTicks]; }

        void TechAfterTick(Movement player, long tick, bool jumpPress, bool dashPress)
        {
            _techPauselifted = false;
            if (_techPreSpring && !_techWasSpring) _techSpringStart = tick;
            _techWasSpring = _techPreSpring;
            if (_techPreSpring && dashPress && player.cutsceneMode == Movement.cutsceneModes.none) _techSpringCancel = tick;
            if (player.facingRight != _techFacing)
            {
                if (tick - _techLastFlip > FlipWindowTicks)
                {
                    _techFlipStart = tick;
                    _techFlipStick = Mathf.Abs(player.MoveAxis.x);
                }
                _techLastFlip = tick;
                _techFacing = player.facingRight;
            }
            if (player.onGround && TechRead<float>(TechCoyoteField, player) >= player.maxCoyoteFrames)
            {
                _techLastGround = tick;
                _techDashAtLastGround = player.dashActive;
            }
            if (player.OnWall && TechRead<float>(TechWallCoyoteField, player) >= player.maxWallCoyoteFrames) _techLastWall = tick;

            if (player.dashActive && !_techPreDashing && player.framesSinceLastDash == 0f)
            {
                _techDashStart = tick;
                _techDashInWave = _techWaveAir;
                _techDashInSuper = _techSuperAir;
                if (tick - _techSpringCancel <= 1) AddTech(Tech.SpringDash, _techSpringStart, tick);
                Vector2 dash = TechRead<Vector2>(TechDashDirectionField, player);
                // Only a dash from a full wall cling keeps x at ±1 next to a vertical part (the cling sets x after the normalize).
                if (Mathf.Abs(dash.x) == 1f && dash.y != 0f) AddTech(Tech.WallDash, tick, tick);
                if (jumpPress && _techPreGround && dash.x != 0f && tick - _techLastFlip <= FlipWindowTicks && WallBehind(player, -Mathf.Sign(dash.x)))
                {
                    _techBackwardsDash = tick;
                    _techBackwardsStart = _techFlipStart;
                }
            }
            // An omnidash the stick aimed in its freeze (MacroEngine.StickFor): Movement.Update doesn't snap a sideways 0.3-0.4
            // to 0 as a dash pressed on a tick does, so it goes steeper than the 8 ways (|x| under the diagonal's 0.707).
            if (player.dashActive && player.framesSinceLastDash == 1f && tick - _techDashStart == 1)
            {
                Vector2 aimed = TechRead<Vector2>(TechDashDirectionField, player);
                if (aimed.x != 0f && aimed.y != 0f && Mathf.Abs(aimed.x) < 0.7f) AddTech(Tech.FreezeAim, _techDashStart, tick);
            }
            if (player.onGround && !player.dashActive) _techWaveAir = _techSuperAir = false;

            foreach (var (tech, start) in _techPending) AddTech(tech, start, tick);
            _techPending.Clear();

            if (_grazeTicks > 0 && !_grazeKilled) ExtendTech(Tech.SpikeGrace, tick);
            _techEndVelocityY = player.Velocity.y;
            float lastHyper = TechRead<float>(TechLastHyperYField, player);
            if (lastHyper != _techLastHyperY)
            {
                _techLastHyperY = lastHyper;
                _techLastHyperOriginY = CurrentOrigin().y;
            }
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
                if (span.tech == tech && span.end == tick - 1) { span.end = tick; span.input = _engine.RunTicks; return; }
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
            long input = _engine.RunTicks, from = System.Math.Max(0, input - (end - start) - TechDemoLeadTicks);
            var span = new TechSpan { tech = tech, start = start, end = end, input = input, lane = lane };
            for (int i = _techStateCount - 1; i >= 0; i--)
            {
                (long stateInput, Savestate state) = TechStateAt(i);
                if (stateInput <= from && state != null)
                {
                    span.savestateInput = stateInput;
                    span.savestate = state;
                    break;
                }
            }
            _techSpans.Add(span);
            if (OverlayListsRun) _techSeen.Add(span);
            if (_techCounts[(int)tech]++ == 0) _techOrder.Add(tech);
            _techSpans.RemoveAll(s => s.end < end - rows);
        }

        string TechSummary(Tech tech)
        {
            int count = _techCounts[(int)tech];
            return count > 1 && tech != Tech.SpikeGrace ? TechLabel(tech) + " ×" + count : TechLabel(tech);
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
                Rect label = new Rect(0f, 0f, y1 - y0, pitch - bar);
                Vector2 pivot = new Vector2(x + bar + (pitch - bar) / 2f, (y0 + y1) / 2f);
                Matrix4x4 saved = GUI.matrix;
                GUIUtility.RotateAroundPivot(-90f, pivot);
                label.center = pivot;
                Label(label, TechLabel(span.tech), _overlayTech, color);
                GUI.matrix = saved;
            }
        }
    }
}
