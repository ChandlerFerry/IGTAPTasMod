using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        // While a playback lists its ticks (OverlayListsRun) the input panel, the pad and the velocity panel are drawn into a
        // texture and the texture onto the screen. Drawn directly they were about 220 IMGUI elements in every rendered frame
        // (0.65 ms a frame at 1x, about a sixth of the 3.57 ms a 280 fps frame has), though they change once a physics tick
        // (every 5.6 frames). The texture is redrawn on a new tick, a changed debug row, a new FPS reading, or at the latest
        // PanelCacheMaxAge after the last draw (a setting changed with no tick); the few values that change on every frame
        // (POS, ULTRA, TIMESTOP: DebugLiveRows; the nudge's fade) are drawn over it each frame.
        RenderTexture _panelCache;
        bool _panelCaching, _panelCacheDirty = true, _panelCacheList, _panelCacheVelocity;
        float _panelCacheFixedTime = float.NaN, _panelCacheDrawnAt = float.NegativeInfinity;
        string _panelCacheFps;
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

        void DrawCachedPanels(float scale)
        {
            bool list = _inputRows.Count > 0, velocity = _overlayPlayer != null;
            if (!list && !velocity) return;
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
            if (_panelCacheDirty || Time.fixedTime != _panelCacheFixedTime || !ReferenceEquals(_fpsText, _panelCacheFps)
                || list != _panelCacheList || velocity != _panelCacheVelocity || now - _panelCacheDrawnAt >= PanelCacheMaxAge)
            {
                RenderTexture active = RenderTexture.active;
                RenderTexture.active = _panelCache;
                GL.Clear(false, true, Color.clear);
                _panelCaching = true;
                try
                {
                    if (list) DrawInputPanel(scale, null);
                    if (velocity) DrawVelocityPanel(scale);
                }
                finally
                {
                    _panelCaching = false;
                    RenderTexture.active = active;
                }
                _panelCacheDirty = false;
                _panelCacheFixedTime = Time.fixedTime;
                _panelCacheFps = _fpsText;
                _panelCacheList = list;
                _panelCacheVelocity = velocity;
                _panelCacheDrawnAt = now;
            }
            Color saved = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, PanelBack.a);
            GUI.DrawTexture(new Rect(0f, 0f, width, height), _panelCache);
            GUI.color = saved;
            if (!velocity) return;
            DrawNudge(_nudgeRect);
            DrawLiveDebugValues(PanelScale(scale));
        }
    }
}
