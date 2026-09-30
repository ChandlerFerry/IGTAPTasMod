using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        // The input panel, the pad and the velocity panel are drawn into a texture and the texture onto the screen. Drawn
        // directly they were about 220 IMGUI elements in every rendered frame (0.65 ms a frame at 1x, about a sixth of the
        // 3.57 ms a 280 fps frame has), though they change once a physics tick (every 5.6 frames). The texture is redrawn
        // after a tick (OverlayTick), a changed debug row, a new tech, a change of anything else it shows that no tick brings
        // (the FPS reading, the clones, the settings, the unlocks, the pad's counts and flags: PanelCacheState), or at the
        // latest PanelCacheMaxAge after the last draw. What changes on every frame is drawn over it each frame: POS, ULTRA and
        // TIMESTOP (DebugLiveRows), the nudge's fade and, with no route listing its ticks (hand play, turbo), the pad, which
        // shows the live buttons (LiveRow).
        RenderTexture _panelCache;
        bool _panelCaching, _panelCacheDirty = true;
        float _panelCacheFixedTime = float.NaN, _panelCacheDrawnAt = float.NegativeInfinity;
        int _panelCacheState = -1;
        string _panelCacheFps, _panelCacheClones, _panelCacheSpeed;
        Rect _nudgeRect, _debugPanel;
        Vector2 _debugTop;
        const float PanelCacheMaxAge = 0.25f;

        /// <summary>The debug rows whose values change on every rendered frame (TIMESTOP's countdown, POS and ULTRA follow the
        /// interpolated body): drawn live over a cached panel.</summary>
        static readonly bool[] DebugLiveRows = { true, false, true, false, false, false, false, false, false, false, true };

        // The panels' base is drawn opaque into the cache and the cache drawn at PanelBack's alpha, so every layer on the base
        // keeps its colour. (Drawn at its own alpha into a clear texture, the base would be stored premultiplied and GUI's
        // straight-alpha draw would darken it again.)
        Color PanelBase { get { return _panelCaching ? new Color(PanelBack.r, PanelBack.g, PanelBack.b, 1f) : PanelBack; } }

        /// <summary>
        /// The flags the cached panels show that can change between ticks (a command, a setting, a respawn or a step-up in
        /// Movement.Update): which panels there are, the settings, the unlocks and, when the pad is cached, its player flags.
        /// </summary>
        int PanelCacheState(bool list, bool velocity, bool padLive)
        {
            int state = (list ? 1 : 0) | (velocity ? 2 : 0) | (padLive ? 4 : 0) | (CheckpointRespawns ? 8 : 0) | (_walkScaled ? 16 : 0)
                | (_overlayLastTick != 0 || _overlayManual && OverlayListsManual ? 32 : 0);
            Movement p = _overlayPlayer;
            if (p == null) return state;
            state |= (p.wallJumpUnlocked ? 64 : 0) | (p.dashUnlocked ? 128 : 0) | (p.doubleJumpUnlocked ? 256 : 0) | (p.blockSwapUnlocked ? 512 : 0)
                | (p.omniDashUnlocked ? 1024 : 0);
            if (list && !padLive)
                state |= (p.onGround ? 2048 : 0) | (p.OnWall ? 4096 : 0) | (p.dashActive ? 8192 : 0) | (GroundType(p) == 1 ? 16384 : 0);
            return state;
        }

        /// <summary>The pad's charges differ from the ones it was last drawn with (DrawInputPad keeps them).</summary>
        bool PadCountsChanged(Movement p)
        {
            return Mathf.Max(0, p.airDashesLeft) != _padCountLeft[0] || p.maxAirDashes != _padCountMax[0]
                || Mathf.Max(0, p.airJumpsLeft) != _padCountLeft[1] || p.maxAirJumps != _padCountMax[1]
                || Mathf.Max(0, p.wallJumpsLeft) != _padCountLeft[2] || p.maxWallJumps != _padCountMax[2];
        }

        void DrawCachedPanels(float scale)
        {
            bool list = _inputRows.Count > 0, velocity = _overlayPlayer != null;
            if (!list && !velocity) return;
            bool padLive = !OverlayListsRun;
            int width = Screen.width, height = Screen.height;
            if (_panelCache == null || _panelCache.width != width || _panelCache.height != height || !_panelCache.IsCreated())
            {
                if (_panelCache != null)
                {
                    _panelCache.Release();
                    Destroy(_panelCache);
                }
                _panelCache = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "TAS overlay panels" };
                _panelCache.Create();
                _panelCacheDirty = true;
            }
            float now = Time.unscaledTime;
            int state = PanelCacheState(list, velocity, padLive);
            if (_panelCacheDirty || Time.fixedTime != _panelCacheFixedTime || state != _panelCacheState || !ReferenceEquals(_fpsText, _panelCacheFps)
                || !ReferenceEquals(_clonesText, _panelCacheClones) || !ReferenceEquals(_playbackSpeedText, _panelCacheSpeed)
                || list && !padLive && velocity && PadCountsChanged(_overlayPlayer) || now - _panelCacheDrawnAt >= PanelCacheMaxAge)
            {
                RenderTexture active = RenderTexture.active;
                RenderTexture.active = _panelCache;
                GL.Clear(false, true, Color.clear);
                _panelCaching = true;
                try
                {
                    if (list) DrawInputPanel(scale, !padLive);
                    if (velocity) DrawVelocityPanel(scale);
                }
                finally
                {
                    _panelCaching = false;
                    RenderTexture.active = active;
                }
                _panelCacheDirty = false;
                _panelCacheFixedTime = Time.fixedTime;
                _panelCacheState = state;
                _panelCacheFps = _fpsText;
                _panelCacheClones = _clonesText;
                _panelCacheSpeed = _playbackSpeedText;
                _panelCacheDrawnAt = now;
            }
            Color saved = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, PanelBack.a);
            GUI.DrawTexture(new Rect(0f, 0f, width, height), _panelCache);
            GUI.color = saved;
            if (padLive)
            {
                // No route lists its ticks: the pad shows the buttons held now (or, with no input actions, the newest tick).
                float panelScale = PanelScale(scale);
                InputRow live = LiveRow(_overlayPlayer);
                if (list) DrawInputPad(InputPadRect(panelScale), live ?? _inputRows[_inputRows.Count - 1], panelScale);
                else if (live != null) DrawInputPad(InputPadRect(panelScale), live, panelScale);
            }
            if (!velocity) return;
            DrawNudge(_nudgeRect);
            DrawLiveDebugValues(PanelScale(scale));
        }
    }
}
