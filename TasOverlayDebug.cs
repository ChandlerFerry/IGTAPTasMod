using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        /// <summary>
        /// The velocity panel's DEBUG section, below SEED: one row per label, an x/y pair or one span of text. Nothing the
        /// input panel and pad (bottom left) already show: ticks, charges, GROUND/WALL/DASHING, the jump cut.
        /// </summary>
        static readonly string[] DebugLabels =
            { "TIMESTOP", "TIME", "POS", "FACING", "CONTROL", "DASH", "BUFFERED", "COYOTE", "WJ LOCK", "HYPER", "ULTRA" };
        readonly string[] _debugX = new string[DebugLabels.Length], _debugY = new string[DebugLabels.Length];
        readonly Color[] _debugColor = new Color[DebugLabels.Length];

        // Real time left in the running Movement.TimeStop freezes (timeScale 0, not paused); the longest one wins.
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
        }

        private static void BeforeOverlayTimeStop(float duration)
        {
            if (Instance != null) Instance._timeStopLeft = Mathf.Max(Instance._timeStopLeft, duration);
        }

        /// <summary>Once per rendered frame (OverlayFrame): runs the TimeStop countdown and rebuilds the section's text.</summary>
        void SampleDebug(Movement player)
        {
            bool paused = player.pauseMenu != null && player.pauseMenu.menuOpen, frozen = Time.timeScale == 0f && !paused;
            if (!frozen) _timeStopLeft = 0f;
            if (_settings.showOverlay) BuildDebugText(player, paused, frozen);
            if (frozen) _timeStopLeft = Mathf.Max(0f, _timeStopLeft - Time.unscaledDeltaTime);
        }

        void BuildDebugText(Movement player, bool paused, bool frozen)
        {
            for (int i = 0; i < _debugColor.Length; i++) { _debugY[i] = null; _debugColor[i] = PanelText; }
            _debugX[0] = frozen ? "ON · " + D2(_timeStopLeft) + "s left" : "OFF";
            _debugColor[0] = frozen ? JumpColor : MutedColor;
            _debugX[1] = "×" + D2(Time.timeScale) + (paused ? " · PAUSED" : "");

            Vector3 scene = player.transform.position;
            Pair(2, (Vector2)scene - CurrentOrigin());
            _debugX[3] = player.facingRight ? "→ right" : "← left";

            // cutsceneMode: the game drives the player (no stick) while it is not none.
            Movement.cutsceneModes mode = player.cutsceneMode;
            _debugX[4] = mode == Movement.cutsceneModes.none ? "player" : mode == Movement.cutsceneModes.deathFreeze ? "death freeze"
                : mode == Movement.cutsceneModes.longfall ? "long fall" : mode.ToString();
            if (mode == Movement.cutsceneModes.none) _debugColor[4] = MutedColor;
            else if (mode == Movement.cutsceneModes.deathFreeze) _debugColor[4] = KilledColor;

            int bounce = DebugFields.DashBounce(player);
            _debugX[5] = (player.dashActive ? "dashing, " + F0(DebugFields.DashFrames(player)) + "f left" : player.dashCooldown > 0f ? "cooldown " + D2(player.dashCooldown) + "s" : "ready")
                + (DebugFields.WaitingForDashInput(player) ? " · waiting for stick" : "") + (bounce > 0 ? " · bounce " + bounce + "f" : "")
                + " · last " + F0(player.framesSinceLastDash) + "f ago";

            bool jump = DebugFields.JumpBuffer(player), dash = DebugFields.DashBuffer(player);
            _debugX[6] = jump && dash ? "jump, dash" : jump ? "jump" : dash ? "dash" : "none";
            if (!(jump || dash)) _debugColor[6] = MutedColor;

            // The counters run on below zero; only an open window means anything.
            float ground = DebugFields.Coyote(player), wall = DebugFields.WallCoyote(player), platform = DebugFields.PlatformCoyote(player);
            string coyote = (ground > 0f ? "ground " + F0(ground) + "f  " : "") + (wall > 0f ? "wall " + F0(wall) + "f  " : "") + (platform > 0f ? "platform " + F0(platform) + "f" : "");
            _debugX[7] = coyote.Length > 0 ? coyote.TrimEnd() : "none";
            if (coyote.Length == 0) _debugColor[7] = MutedColor;
            float wjLock = player.wallJumpMovementLockRemaining;
            _debugX[8] = wjLock > 0f ? F0(wjLock) + "f left" : "none";
            if (wjLock <= 0f) _debugColor[8] = MutedColor;

            bool hyper = DebugFields.CanHyper(player);
            _debugX[9] = hyper ? "ready" : "no";
            _debugColor[9] = hyper ? GraceColor : MutedColor;
            // A hyper adds the pre-dash momentum when it starts over 4u below the last hyper or wall bounce (Movement.cs 1444);
            // lastHyperY starts at 1e8, before either has happened.
            float lastHyperY = DebugFields.LastHyperY(player), below = lastHyperY - 4f - scene.y;
            _debugX[10] = lastHyperY >= 1e7f ? "yes, no hyper yet" : below > 0f ? "yes, " + D1(below) + "u under the last" : "no, " + D1(-below) + "u too high";
            _debugColor[10] = below > 0f ? GraceColor : MutedColor;
        }

        void Pair(int row, Vector2 v)
        {
            _debugX[row] = "X " + D2(v.x);
            _debugY[row] = "Y " + D2(v.y);
        }

        static string D2(float v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        static string D1(float v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        static string F0(float v) { return v.ToString("0", CultureInfo.InvariantCulture); }

        static float DebugRowHeight(float scale) { return 22 * scale; }

        float DebugSectionHeight(float scale) { return 8 * scale + 1 + 8 * scale + DebugLabels.Length * DebugRowHeight(scale); }

        /// <summary>Draws the section from y (the row under SEED): a rule, then label | value (or X | Y) rows.</summary>
        void DrawDebugSection(Rect panel, float x, float y, float scale)
        {
            y += 8 * scale;
            Fill(new Rect(panel.x, y, panel.width, 1), PanelLine);
            y += 1 + 8 * scale;
            float row = DebugRowHeight(scale), valueX = x + 92 * scale, yX = valueX + 150 * scale;
            for (int i = 0; i < DebugLabels.Length; i++)
            {
                Label(new Rect(x, y, valueX - x, row), DebugLabels[i], _overlaySmall, MutedColor);
                if (_debugX[i] != null)
                {
                    if (_debugY[i] == null) Label(new Rect(valueX, y, panel.xMax - valueX, row), _debugX[i], _overlaySmall, _debugColor[i]);
                    else
                    {
                        Label(new Rect(valueX, y, yX - valueX, row), _debugX[i], _overlaySmall, _debugColor[i]);
                        Label(new Rect(yX, y, panel.xMax - yX, row), _debugY[i], _overlaySmall, _debugColor[i]);
                    }
                }
                y += row;
            }
        }
    }
}
