using System.Globalization;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        Vector2 _velocityRun, _velocityMomentum, _velocityPlatform;
        float _velocityCap;
        bool _velocityDirty = true;
        string _velocityRatioText;
        readonly string[] _velocityText = new string[8];
        Vector2 _positionBeforeTick;
        const float MaxNudge = 100f, NudgeLingerSeconds = 1.5f;
        string _nudgeText;
        float _nudgeTime = float.NegativeInfinity;

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

        const float FpsWindowSeconds = 0.5f, SlowFrameSeconds = 0.01f;
        int _fpsFrames;
        float _fpsElapsed, _fpsWorst, _fpsShownWorst;
        string _fpsText = "";

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

        void DrawVelocityPanel(float screenScale)
        {
            float scale = PanelScale(screenScale);
            if (_velocityDirty) BuildVelocityText();
            float pad = 20 * scale, titleLine = 26 * scale, totalLine = 30 * scale, rowHeight = 26 * scale, barHeight = 12 * scale, width = PanelWidth * scale;
            bool platform = _velocityPlatform != Vector2.zero;
            int parts = platform ? 3 : 2;
            float bandHeight = 14 * scale + titleLine + 12 * scale;
            float height = bandHeight + 10 * scale + totalLine + 12 * scale + barHeight + 14 * scale + parts * rowHeight + 8 * scale + 1 + 8 * scale + 4 * rowHeight + 12 * scale;
            Rect panel = new Rect(Screen.width - width, 0f, width, height);
            Fill(panel, PanelBack);
            Fill(new Rect(panel.x, panel.y, width, bandHeight), PanelBand);
            Fill(new Rect(panel.x, panel.y, 1, panel.height), PanelLine);
            Fill(new Rect(panel.x, panel.yMax - 1, width, 1), PanelLine);
            Fill(new Rect(panel.x, panel.y + bandHeight, width, 1), PanelLine);

            float x = panel.x + pad, inner = width - 2 * pad, y = panel.y + 14 * scale;
            float xColumn = x + 160 * scale, yColumn = x + 280 * scale, columnWidth = 120 * scale, labelWidth = xColumn - x - 8 * scale;
            Label(new Rect(x, y, inner, titleLine), "VELOCITY", _overlayTitle, PanelText);
            Label(new Rect(x, y, inner, titleLine), _fpsText, _overlaySmallRight, _fpsShownWorst > SlowFrameSeconds ? KilledColor : GraceColor);

            y = panel.y + bandHeight + 10 * scale;
            Label(new Rect(xColumn, y, columnWidth, totalLine), _velocityText[6], _overlayTitle, PanelText);
            Label(new Rect(yColumn, y, columnWidth, totalLine), _velocityText[7], _overlayTitle, PanelText);

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
                if (i != 1) continue;
                float age = Time.unscaledTime - _nudgeTime;
                Label(new Rect(x, y, labelWidth, rowHeight), "NUDGE", _overlaySmall, MutedColor);
                if (age < NudgeLingerSeconds)
                    Label(new Rect(xColumn, y, 2 * columnWidth, rowHeight), _nudgeText, _overlayText, new Color(JumpColor.r, JumpColor.g, JumpColor.b, 1f - age / NudgeLingerSeconds));
                else Label(new Rect(xColumn, y, columnWidth, rowHeight), "0", _overlayText, MutedColor);
                y += rowHeight;
            }

            y += 8 * scale;
            Fill(new Rect(panel.x, y, width, 1), PanelLine);
            y += 1 + 8 * scale;
            bool locked = _playerBody != null && _playerBody.interpolation == RigidbodyInterpolation2D.None;
            Label(new Rect(x, y, labelWidth, rowHeight), "INTERPOLATION", _overlaySmall, MutedColor);
            Label(new Rect(xColumn, y, 2 * columnWidth, rowHeight), locked ? InterpolationOffText : "ON", _overlayText, locked ? GraceColor : MutedColor);
            y += rowHeight;
            Label(new Rect(x, y, labelWidth, rowHeight), "CHECKPOINTS", _overlaySmall, MutedColor);
            Label(new Rect(xColumn, y, 2 * columnWidth, rowHeight), CheckpointRespawns ? "ON" : "OFF", CheckpointRespawns ? _overlayTitle : _overlayText, CheckpointRespawns ? JumpColor : GraceColor);
            y += rowHeight;
            Label(new Rect(x, y, inner, rowHeight), "SEED (unoptimized) " + FullTasSeed.ToString(CultureInfo.InvariantCulture), _overlayText, PanelText);
        }

        /// <summary>The TAS holds interpolation off; the step-up gate keeps turbo's one lift per tick (MacroEngine.AllowStepUp).</summary>
        static readonly string InterpolationOffText = "OFF (" + MacroEngine.MaxStepUpLift.ToString(CultureInfo.InvariantCulture) + "u STEP CAP)";
    }
}
