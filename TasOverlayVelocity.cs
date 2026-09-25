using System.Globalization;
using UnityEngine;

namespace IGTAPTasMod
{
    // The overlay's top right panel: the player's velocity after each physics tick, split the way Movement.FixedUpdate
    // builds it (body.linearVelocity = Velocity + momentum + movingPlatformVelocity). Velocity.x is capped at
    // runSpeed * 10; anything faster is momentum (dashes, wall bounces, hypers), which the bar shows past its cap mark.
    // Below: moves no velocity explains. Movement.FixedUpdate also moves the player by writing Transform.position (the
    // ledge step-up, the wall jump's 1-unit hop); with the body's interpolation on that read is a render-time pose, so
    // the game's own nudges would depend on frame timing. Playback turns interpolation off (BeginDeterministicPhysics).
    public sealed partial class TasPlugin
    {
        Vector2 _velocityRun, _velocityMomentum, _velocityPlatform;
        float _velocityCap;
        // Set each tick; the panel's strings are rebuilt on the next repaint.
        bool _velocityDirty = true;
        string _velocityRatioText;
        // x then y text for each of RUN, MOMENTUM, PLATFORM, TOTAL.
        readonly string[] _velocityText = new string[8];
        // OverlayBeforeTick: where Movement.FixedUpdate started. What it moved the transform by is a nudge; a bigger
        // jump than MaxNudge is a teleport (respawn, a route start), not a nudge.
        Vector2 _positionBeforeTick;
        const float MaxNudge = 100f, NudgeLingerSeconds = 1.5f;
        string _nudgeText;
        float _nudgeTime = float.NegativeInfinity;

        // OverlayTick: the velocity the tick handed to physics, and any nudge it made.
        void SampleVelocity(Movement player)
        {
            _velocityRun = player.Velocity;
            _velocityMomentum = player.momentum;
            _velocityPlatform = player.movingPlatformVelocity;
            _velocityCap = player.runSpeed * 10f;
            _velocityDirty = true;
            Vector2 nudge = (Vector2)player.transform.position - _positionBeforeTick;
            if (nudge.sqrMagnitude > 1e-4f && nudge.sqrMagnitude < MaxNudge * MaxNudge)
            {
                _nudgeText = (nudge.x != 0f ? Axis(nudge.x, "←", "→") + "  " : "") + (nudge.y != 0f ? Axis(nudge.y, "↓", "↑") : "");
                _nudgeTime = Time.unscaledTime;
            }
        }

        bool InterpolationLocked { get { return _deterministicBody != null && _deterministicBody.interpolation == RigidbodyInterpolation2D.None; } }

        // Frame rate over the last FpsWindowSeconds, and its longest frame: a delayed Invoke that must land before the
        // next tick needs a frame at least every 10 ms (see PlaybackFrameRate), so a slow frame shows red.
        const float FpsWindowSeconds = 0.5f, SlowFrameSeconds = 0.01f;
        int _fpsFrames;
        float _fpsElapsed, _fpsWorst, _fpsShownWorst;
        string _fpsText = "";

        // TasPlugin.Update, once per rendered frame.
        void CountFrame()
        {
            float dt = Time.unscaledDeltaTime;
            _fpsFrames++;
            _fpsElapsed += dt;
            _fpsWorst = Mathf.Max(_fpsWorst, dt);
            if (_fpsElapsed < FpsWindowSeconds) return;
            _fpsText = Mathf.RoundToInt(_fpsFrames / _fpsElapsed) + " FPS · " + (_fpsWorst * 1000f).ToString("0.0") + " ms";
            _fpsShownWorst = _fpsWorst;
            _fpsFrames = 0;
            _fpsElapsed = _fpsWorst = 0f;
        }

        static string Axis(float v, string negative, string positive)
        {
            int n = Mathf.RoundToInt(v);
            return n == 0 ? "0" : (n < 0 ? negative : positive) + " " + Mathf.Abs(n).ToString("N0");
        }

        void SetVelocityText(int part, Vector2 v)
        {
            _velocityText[2 * part] = Axis(v.x, "←", "→");
            _velocityText[2 * part + 1] = Axis(v.y, "↓", "↑");
        }

        void BuildVelocityText()
        {
            _velocityDirty = false;
            Vector2 total = _velocityRun + _velocityMomentum + _velocityPlatform;
            SetVelocityText(0, _velocityRun);
            SetVelocityText(1, _velocityMomentum);
            SetVelocityText(2, _velocityPlatform);
            SetVelocityText(3, total);
            _velocityRatioText = _velocityCap > 0f ? (Mathf.Abs(total.x) / _velocityCap).ToString("0.00") + "×" : "";
        }

        static readonly string[] VelocityPartNames = { "RUN", "MOMENTUM", "PLATFORM" };

        // Top right corner of the screen, the input panel's width and palette: the total, a bar of horizontal speed
        // against the run cap, and what the total is made of.
        void DrawVelocityPanel(float screenScale)
        {
            float scale = PanelScale(screenScale);
            if (_velocityDirty) BuildVelocityText();
            float pad = 20 * scale, titleLine = 26 * scale, totalLine = 30 * scale, rowHeight = 26 * scale, barHeight = 12 * scale, width = PanelWidth * scale;
            bool platform = _velocityPlatform != Vector2.zero;
            int parts = platform ? 3 : 2;
            float bandHeight = 14 * scale + titleLine + 12 * scale;
            float height = bandHeight + 10 * scale + totalLine + 12 * scale + barHeight + 14 * scale + parts * rowHeight + 8 * scale + 1 + 8 * scale + 2 * rowHeight + rowHeight + 12 * scale;
            Rect panel = new Rect(Screen.width - width, 0f, width, height);
            Fill(panel, PanelBack);
            Fill(new Rect(panel.x, panel.y, width, bandHeight), PanelBand);
            Fill(new Rect(panel.x, panel.y, 1, panel.height), PanelLine);
            Fill(new Rect(panel.x, panel.yMax - 1, width, 1), PanelLine);
            Fill(new Rect(panel.x, panel.y + bandHeight, width, 1), PanelLine);

            float x = panel.x + pad, inner = width - 2 * pad, y = panel.y + 14 * scale;
            // Labels get everything left of the x column; "INTERPOLATION" is the widest.
            float xColumn = x + 160 * scale, yColumn = x + 280 * scale, columnWidth = 120 * scale, labelWidth = xColumn - x - 8 * scale;
            Label(new Rect(x, y, inner, titleLine), "VELOCITY", _overlayTitle, PanelText);
            Label(new Rect(x, y, inner, titleLine), _fpsText, _overlaySmallRight, _fpsShownWorst > SlowFrameSeconds ? HitboxColor : GraceColor);

            y = panel.y + bandHeight + 10 * scale;
            Label(new Rect(xColumn, y, columnWidth, totalLine), _velocityText[6], _overlayTitle, PanelText);
            Label(new Rect(yColumn, y, columnWidth, totalLine), _velocityText[7], _overlayTitle, PanelText);

            // Horizontal speed: blue up to the run cap (the white mark), amber for what momentum adds past it.
            y += totalLine + 12 * scale;
            float ratioWidth = 64 * scale, barWidth = inner - ratioWidth - 8 * scale;
            float top = Mathf.Max(_velocityCap * 2f, 1f);
            float speed = Mathf.Abs((_velocityRun + _velocityMomentum + _velocityPlatform).x);
            float capX = barWidth * Mathf.Clamp01(_velocityCap / top), fill = barWidth * Mathf.Clamp01(speed / top);
            Fill(new Rect(x, y, barWidth, barHeight), PanelBand);
            Fill(new Rect(x, y, Mathf.Min(fill, capX), barHeight), DashColor);
            if (fill > capX) Fill(new Rect(x + capX, y, fill - capX, barHeight), JumpColor);
            Fill(new Rect(x + capX, y - 3 * scale, Mathf.Max(1f, 2 * scale), barHeight + 6 * scale), PanelText);
            Label(new Rect(x + barWidth + 8 * scale, y - 8 * scale, ratioWidth, barHeight + 16 * scale), _velocityRatioText, _overlaySmallRight, speed > _velocityCap ? JumpColor : MutedColor);

            y += barHeight + 14 * scale;
            for (int i = 0; i < 3; i++)
            {
                if (i == 2 && !platform) continue;
                Color color = i == 1 && _velocityMomentum != Vector2.zero ? JumpColor : MutedColor;
                Label(new Rect(x, y, labelWidth, rowHeight), VelocityPartNames[i], _overlaySmall, MutedColor);
                Label(new Rect(xColumn, y, columnWidth, rowHeight), _velocityText[2 * i], _overlayText, color);
                Label(new Rect(yColumn, y, columnWidth, rowHeight), _velocityText[2 * i + 1], _overlayText, color);
                y += rowHeight;
            }

            // Position moves outside velocity: interpolation locked off during playback, and the last nudge (step-up, wall-jump hop).
            y += 8 * scale;
            Fill(new Rect(panel.x, y, width, 1), PanelLine);
            y += 1 + 8 * scale;
            bool locked = InterpolationLocked;
            Label(new Rect(x, y, labelWidth, rowHeight), "INTERPOLATION", _overlaySmall, MutedColor);
            Label(new Rect(xColumn, y, 2 * columnWidth, rowHeight), locked ? "OFF (locked)" : "ON", _overlayText, locked ? GraceColor : MutedColor);
            y += rowHeight;
            float age = Time.unscaledTime - _nudgeTime;
            Label(new Rect(x, y, labelWidth, rowHeight), "NUDGE", _overlaySmall, MutedColor);
            if (age < NudgeLingerSeconds)
                Label(new Rect(xColumn, y, 2 * columnWidth, rowHeight), _nudgeText, _overlayText, new Color(JumpColor.r, JumpColor.g, JumpColor.b, 1f - age / NudgeLingerSeconds));
            else Label(new Rect(xColumn, y, columnWidth, rowHeight), "0", _overlayText, MutedColor);

            // The fixed Full-TAS seed, read only (FullTasPlugin.FullTasSeed).
            y += rowHeight;
            Label(new Rect(x, y, inner, rowHeight), "SEED (unoptimized) " + FullTasSeed.ToString(CultureInfo.InvariantCulture), _overlayText, PanelText);
        }

    }
}
