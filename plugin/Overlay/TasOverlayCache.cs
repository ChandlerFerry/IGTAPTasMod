using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        RenderTexture _panelCache;
        bool _panelCaching, _panelCacheDirty = true;
        float _panelCacheFixedTime = float.NaN, _panelCacheDrawnAt = float.NegativeInfinity;
        int _panelCacheState = -1;
        string _panelCacheFps, _panelCacheClones, _panelCacheSpeed;
        Rect _nudgeRect, _debugPanel;
        Vector2 _debugTop;
        const float PanelCacheMaxAge = 0.25f;

        static readonly bool[] DebugLiveRows = { true, false, true, false, false, false, false, false, false, false, true };

        // Draw the base opaque into the cache and the cache at PanelBack's alpha: a premultiplied base would break GUI's blending.
        Color PanelBase { get { return _panelCaching ? new Color(PanelBack.r, PanelBack.g, PanelBack.b, 1f) : PanelBack; } }

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

        bool PadCountsChanged(Movement p)
        {
            return Mathf.Max(0, p.airDashesLeft) != _padCountLeft[0] || p.maxAirDashes != _padCountMax[0]
                || Mathf.Max(0, p.airJumpsLeft) != _padCountLeft[1] || p.maxAirJumps != _padCountMax[1]
                || Mathf.Max(0, p.wallJumpsLeft) != _padCountLeft[2] || p.maxWallJumps != _padCountMax[2];
        }

        Rect _panelCacheLeft, _panelCacheRight;

        void DrawPanelCacheRect(Rect r, int width, int height)
        {
            if (r.width > 0f) GUI.DrawTextureWithTexCoords(r, _panelCache, new Rect(r.x / width, 1f - r.yMax / height, r.width / width, r.height / height));
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
            PerfSection(8);
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
                    _panelCacheLeft = list ? new Rect(0f, 0f, PanelWidth * PanelScale(scale), height) : Rect.zero;
                    _panelCacheRight = velocity ? _velocityPanelRect : Rect.zero;
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
            PerfSection(10);
            Color saved = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, PanelBack.a);
            DrawPanelCacheRect(_panelCacheLeft, width, height);
            DrawPanelCacheRect(_panelCacheRight, width, height);
            GUI.color = saved;
            PerfSection(11);
            if (padLive)
            {
                float panelScale = PanelScale(scale);
                InputRow live = LiveRow(_overlayPlayer);
                if (list) DrawInputPad(InputPadRect(panelScale), live ?? _inputRows[_inputRows.Count - 1], panelScale);
                else if (live != null) DrawInputPad(InputPadRect(panelScale), live, panelScale);
            }
            PerfSection(12);
            if (!velocity) return;
            DrawNudge(_nudgeRect);
            DrawLiveDebugValues(PanelScale(scale));
            PerfSection(13);
        }
    }
}
