using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        static readonly string[] DebugLabels =
            { "TIMESTOP", "TIME", "POS", "FACING", "CONTROL", "DASH", "BUFFERED", "COYOTE", "WJ LOCK", "HYPER", "ULTRA" };
        readonly string[] _debugX = new string[DebugLabels.Length], _debugY = new string[DebugLabels.Length];
        readonly Color[] _debugColor = new Color[DebugLabels.Length];

        float _timeStopLeft;

        static class DebugFields
        {
            public static readonly AccessTools.FieldRef<Movement, bool> JumpBuffer = AccessTools.FieldRefAccess<Movement, bool>("jumpBuffer"),
                DashBuffer = AccessTools.FieldRefAccess<Movement, bool>("dashBuffer"), CanHyper = AccessTools.FieldRefAccess<Movement, bool>("canHyper"),
                WaitingForDashInput = AccessTools.FieldRefAccess<Movement, bool>("WaitingForDashInput");
            public static readonly AccessTools.FieldRef<Movement, float> Coyote = AccessTools.FieldRefAccess<Movement, float>("coyoteFrames"),
                WallCoyote = AccessTools.FieldRefAccess<Movement, float>("wallCoyoteFrames"), DashFrames = AccessTools.FieldRefAccess<Movement, float>("dashFramesRemaining"),
                LastHyperY = AccessTools.FieldRefAccess<Movement, float>("lastHyperY"), PlatformCoyote = AccessTools.FieldRefAccess<Movement, float>("movingPlatformCoyote");
            public static readonly AccessTools.FieldRef<Movement, int> DashBounce = AccessTools.FieldRefAccess<Movement, int>("DashBounceFrames");
            public static readonly AccessTools.FieldRef<Movement, bool> FinalCling = AccessTools.FieldRefAccess<Movement, bool>("isFinalWallClingActive");
            public static readonly AccessTools.FieldRef<Movement, Vector2> DashDirection = AccessTools.FieldRefAccess<Movement, Vector2>("dashDirection"),
                PreDashMomentum = AccessTools.FieldRefAccess<Movement, Vector2>("preDashMomentum"), WallJumpFacing = AccessTools.FieldRefAccess<Movement, Vector2>("wallJumpFacing");
        }

        private static void BeforeOverlayTimeStop(float duration)
        {
            if (Instance != null) Instance._timeStopLeft = Mathf.Max(Instance._timeStopLeft, duration);
        }

        void SampleDebug(Movement player)
        {
            bool paused = player.pauseMenu != null && player.pauseMenu.menuOpen, frozen = Time.timeScale == 0f && !paused;
            if (!frozen) _timeStopLeft = 0f;
            if (_settings.showOverlay) BuildDebugText(player, paused, frozen);
            if (frozen) _timeStopLeft = Mathf.Max(0f, _timeStopLeft - Time.unscaledDeltaTime);
        }

        readonly int[,] _debugInputs = new int[DebugLabels.Length, 6];
        bool _debugBuilt;

        bool DebugRowChanged(int row, float a, float b = 0f, float c = 0f, float d = 0f, float e = 0f, float f = 0f)
        {
            int ia = BitConverter.SingleToInt32Bits(a), ib = BitConverter.SingleToInt32Bits(b), ic = BitConverter.SingleToInt32Bits(c),
                id = BitConverter.SingleToInt32Bits(d), ie = BitConverter.SingleToInt32Bits(e), iff = BitConverter.SingleToInt32Bits(f);
            if (_debugBuilt && _debugInputs[row, 0] == ia && _debugInputs[row, 1] == ib && _debugInputs[row, 2] == ic
                && _debugInputs[row, 3] == id && _debugInputs[row, 4] == ie && _debugInputs[row, 5] == iff) return false;
            _debugInputs[row, 0] = ia; _debugInputs[row, 1] = ib; _debugInputs[row, 2] = ic;
            _debugInputs[row, 3] = id; _debugInputs[row, 4] = ie; _debugInputs[row, 5] = iff;
            _debugColor[row] = PanelText;
            if (!DebugLiveRows[row]) _panelCacheDirty = true;
            return true;
        }

        static float Flag(bool value) { return value ? 1f : 0f; }

        void BuildDebugText(Movement player, bool paused, bool frozen)
        {
            if (DebugRowChanged(0, Flag(frozen), frozen ? _timeStopLeft : 0f))
            {
                _debugX[0] = frozen ? "ON · " + D2(_timeStopLeft) + "s left" : "OFF";
                _debugColor[0] = frozen ? JumpColor : MutedColor;
            }
            if (DebugRowChanged(1, Time.timeScale, Flag(paused))) _debugX[1] = "×" + D2(Time.timeScale) + (paused ? " · PAUSED" : "");

            Vector3 scene = player.transform.position;
            Vector2 position = (Vector2)scene - CurrentOrigin();
            if (DebugRowChanged(2, position.x, position.y)) Pair(2, position);
            if (DebugRowChanged(3, Flag(player.facingRight))) _debugX[3] = player.facingRight ? "→ right" : "← left";

            Movement.cutsceneModes mode = player.cutsceneMode;
            if (DebugRowChanged(4, (int)mode))
            {
                _debugX[4] = mode == Movement.cutsceneModes.none ? "player" : mode == Movement.cutsceneModes.deathFreeze ? "death freeze"
                    : mode == Movement.cutsceneModes.longfall ? "long fall" : mode.ToString();
                if (mode == Movement.cutsceneModes.none) _debugColor[4] = MutedColor;
                else if (mode == Movement.cutsceneModes.deathFreeze) _debugColor[4] = KilledColor;
            }

            int bounce = DebugFields.DashBounce(player);
            bool waiting = DebugFields.WaitingForDashInput(player);
            if (DebugRowChanged(5, Flag(player.dashActive), DebugFields.DashFrames(player), player.dashCooldown, Flag(waiting), bounce, player.framesSinceLastDash))
                _debugX[5] = (player.dashActive ? "dashing, " + F0(DebugFields.DashFrames(player)) + "f left" : player.dashCooldown > 0f ? "cooldown " + D2(player.dashCooldown) + "s" : "ready")
                    + (waiting ? " · waiting for stick" : "") + (bounce > 0 ? " · bounce " + bounce + "f" : "")
                    + " · last " + F0(player.framesSinceLastDash) + "f ago";

            bool jump = DebugFields.JumpBuffer(player), dash = DebugFields.DashBuffer(player);
            if (DebugRowChanged(6, Flag(jump), Flag(dash)))
            {
                _debugX[6] = jump && dash ? "jump, dash" : jump ? "jump" : dash ? "dash" : "none";
                if (!(jump || dash)) _debugColor[6] = MutedColor;
            }

            float ground = DebugFields.Coyote(player), wall = DebugFields.WallCoyote(player), platform = DebugFields.PlatformCoyote(player);
            if (DebugRowChanged(7, ground, wall, platform))
            {
                string coyote = (ground > 0f ? "ground " + F0(ground) + "f  " : "") + (wall > 0f ? "wall " + F0(wall) + "f  " : "") + (platform > 0f ? "platform " + F0(platform) + "f" : "");
                _debugX[7] = coyote.Length > 0 ? coyote.TrimEnd() : "none";
                if (coyote.Length == 0) _debugColor[7] = MutedColor;
            }
            float wjLock = player.wallJumpMovementLockRemaining;
            if (DebugRowChanged(8, wjLock))
            {
                _debugX[8] = wjLock > 0f ? F0(wjLock) + "f left" : "none";
                if (wjLock <= 0f) _debugColor[8] = MutedColor;
            }

            bool hyper = DebugFields.CanHyper(player);
            if (DebugRowChanged(9, Flag(hyper)))
            {
                _debugX[9] = hyper ? "ready" : "no";
                _debugColor[9] = hyper ? GraceColor : MutedColor;
            }
            // A hyper adds pre-dash momentum when over 4u below the last hyper/wall bounce (Movement.cs 1444); lastHyperY starts 1e8.
            float lastHyperY = DebugFields.LastHyperY(player), below = lastHyperY - 4f - scene.y;
            if (DebugRowChanged(10, lastHyperY, scene.y))
            {
                _debugX[10] = lastHyperY >= 1e7f ? "yes, no hyper yet" : below > 0f ? "yes, " + D1(below) + "u under the last" : "no, " + D1(-below) + "u too high";
                _debugColor[10] = below > 0f ? GraceColor : MutedColor;
            }
            _debugBuilt = true;
        }

        int _pairXBits, _pairYBits;

        void Pair(int row, Vector2 v)
        {
            int x = BitConverter.SingleToInt32Bits(v.x), y = BitConverter.SingleToInt32Bits(v.y);
            if (_debugX[row] == null || x != _pairXBits) _debugX[row] = "X " + D2(v.x);
            if (_debugY[row] == null || y != _pairYBits) _debugY[row] = "Y " + D2(v.y);
            _pairXBits = x;
            _pairYBits = y;
        }

        static string D2(float v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        static string D1(float v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        static string F0(float v) { return v.ToString("0", CultureInfo.InvariantCulture); }

        static float DebugRowHeight(float scale) { return 22 * scale; }

        float DebugSectionHeight(float scale) { return 8 * scale + 1 + 8 * scale + DebugLabels.Length * DebugRowHeight(scale); }

        void DrawDebugSection(Rect panel, float x, float y, float scale)
        {
            y += 8 * scale;
            Fill(new Rect(panel.x, y, panel.width, 1), PanelLine);
            y += 1 + 8 * scale;
            _debugPanel = panel;
            _debugTop = new Vector2(x, y);
            float row = DebugRowHeight(scale);
            for (int i = 0; i < DebugLabels.Length; i++, y += row)
            {
                Label(new Rect(x, y, 92 * scale, row), DebugLabels[i], _overlaySmall, MutedColor);
                if (!DebugLiveRows[i]) DrawDebugValue(i, panel, x, y, scale);
            }
        }

        void DrawLiveDebugValues(float scale)
        {
            for (int i = 0; i < DebugLabels.Length; i++)
                if (DebugLiveRows[i]) DrawDebugValue(i, _debugPanel, _debugTop.x, _debugTop.y + i * DebugRowHeight(scale), scale);
        }

        void DrawDebugValue(int i, Rect panel, float x, float y, float scale)
        {
            if (_debugX[i] == null) return;
            float row = DebugRowHeight(scale), valueX = x + 92 * scale, yX = valueX + 150 * scale;
            if (_debugY[i] == null) Label(new Rect(valueX, y, panel.xMax - valueX, row), _debugX[i], _overlaySmall, _debugColor[i]);
            else
            {
                Label(new Rect(valueX, y, yX - valueX, row), _debugX[i], _overlaySmall, _debugColor[i]);
                Label(new Rect(yX, y, panel.xMax - yX, row), _debugY[i], _overlaySmall, _debugColor[i]);
            }
        }
    }
}
