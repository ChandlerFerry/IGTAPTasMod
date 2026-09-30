using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
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
        // What _nudgeText was built from: each axis rounded (Axis), int.MinValue for an axis left out (0).
        int _nudgeTextX = int.MaxValue, _nudgeTextY = int.MaxValue;

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
                int x = nudge.x != 0f ? Mathf.RoundToInt(nudge.x) : int.MinValue, y = nudge.y != 0f ? Mathf.RoundToInt(nudge.y) : int.MinValue;
                if (x != _nudgeTextX || y != _nudgeTextY)
                {
                    _nudgeTextX = x;
                    _nudgeTextY = y;
                    _nudgeText = (nudge.x != 0f ? Axis(nudge.x, "←", "→") + "  " : "") + (nudge.y != 0f ? Axis(nudge.y, "↓", "↑") : "");
                }
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

        // Clones on screen: the on-screen course (clonesScript.onScreen; one off screen keeps its active clones) with the most, its
        // clones bought (cloneCount) and the most seen at once since its count last fell to 0 (a new best clears them).
        static readonly AccessTools.FieldRef<clonesScript, List<GameObject>> OverlayActiveClones = AccessTools.FieldRefAccess<clonesScript, List<GameObject>>("activeClones");
        static readonly AccessTools.FieldRef<clonesScript, int> OverlayCloneCount = AccessTools.FieldRefAccess<clonesScript, int>("cloneCount");
        static readonly AccessTools.FieldRef<clonesScript, courseScript> OverlayCloneCourse = AccessTools.FieldRefAccess<clonesScript, courseScript>("course");
        clonesScript[] _overlayClones;
        int _overlayClonesGeneration;
        clonesScript _peakClones;
        int _peakCloneCount;
        string _clonesText = "";
        // What _clonesText was built from (the course, the clones on screen, bought, the peak): built again only on a change.
        courseScript _clonesTextCourse;
        int _clonesTextCourseNumber, _clonesTextActive = -1, _clonesTextBought, _clonesTextPeak;

        void SampleClones()
        {
            // The courses' clone scripts are found once per scene load or unload (the game creates none at run time), inactive
            // ones too; one on an inactive object counts as absent, as a find that leaves those out would have it.
            int generation = SceneGeneration;
            if (_overlayClones == null || generation != _overlayClonesGeneration)
            {
                _overlayClonesGeneration = generation;
                _overlayClones = FindObjectsByType<clonesScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            }
            clonesScript best = null;
            int active = 0;
            foreach (clonesScript c in _overlayClones)
            {
                int n = c == null || !c.onScreen || !c.gameObject.activeInHierarchy ? 0 : OverlayActiveClones(c)?.Count ?? 0;
                if (n > active) { best = c; active = n; }
            }
            if (best == null) { _peakClones = null; _peakCloneCount = 0; _clonesText = ""; _clonesTextActive = -1; return; }
            if (!ReferenceEquals(best, _peakClones)) { _peakClones = best; _peakCloneCount = 0; }
            _peakCloneCount = Mathf.Max(_peakCloneCount, active);
            courseScript course = OverlayCloneCourse(best);
            int bought = OverlayCloneCount(best), number = course != null ? course.courseNumber : int.MinValue;
            if (ReferenceEquals(course, _clonesTextCourse) && number == _clonesTextCourseNumber && active == _clonesTextActive && bought == _clonesTextBought
                && _peakCloneCount == _clonesTextPeak) return;
            _clonesTextCourse = course;
            _clonesTextCourseNumber = number;
            _clonesTextActive = active;
            _clonesTextBought = bought;
            _clonesTextPeak = _peakCloneCount;
            _clonesText = (course != null ? "C" + course.courseNumber + "  " : "") + active + " / " + bought + "   (peak " + _peakCloneCount + ")";
        }

        static string Axis(float v, string negative, string positive)
        {
            int n = Mathf.RoundToInt(v);
            return n == 0 ? "0" : (n < 0 ? negative : positive) + " " + Mathf.Abs(n).ToString("N0");
        }

        // The whole units each _velocityText was built from (Axis rounds), and the ratio _velocityRatioText shows: a tick
        // builds a text again only when what it reads changed.
        readonly int[] _velocityRounded = new int[8];
        float _velocityRatio = float.NaN;

        void SetVelocityText(int part, Vector2 v)
        {
            SetAxisText(2 * part, v.x, "←", "→");
            SetAxisText(2 * part + 1, v.y, "↓", "↑");
        }

        void SetAxisText(int slot, float v, string negative, string positive)
        {
            int n = Mathf.RoundToInt(v);
            if (_velocityText[slot] != null && n == _velocityRounded[slot]) return;
            _velocityRounded[slot] = n;
            _velocityText[slot] = Axis(v, negative, positive);
        }

        void BuildVelocityText()
        {
            _velocityDirty = false;
            Vector2 total = _velocityRun + _velocityMomentum + _velocityPlatform;
            SetVelocityText(0, _velocityRun);
            SetVelocityText(1, _velocityMomentum);
            SetVelocityText(2, _velocityPlatform);
            SetVelocityText(3, total);
            if (!(_velocityCap > 0f)) _velocityRatioText = "";
            else
            {
                float ratio = Mathf.Abs(total.x) / _velocityCap;
                if (_velocityRatioText == null || _velocityRatioText.Length == 0 || ratio != _velocityRatio)
                {
                    _velocityRatio = ratio;
                    _velocityRatioText = ratio.ToString("0.00") + "×";
                }
            }
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
            float height = bandHeight + 10 * scale + totalLine + 12 * scale + barHeight + 14 * scale + parts * rowHeight + 8 * scale + 1 + 8 * scale + 5 * rowHeight + 12 * scale
                + DebugSectionHeight(scale);
            Rect panel = new Rect(Screen.width - width, 0f, width, height);
            Fill(panel, PanelBase);
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
                Label(new Rect(x, y, labelWidth, rowHeight), "NUDGE", _overlaySmall, MutedColor);
                // Its value fades on the wall clock: the panel is cached (DrawCachedPanels), which draws it live over the cache.
                _nudgeRect = new Rect(xColumn, y, 2 * columnWidth, rowHeight);
                y += rowHeight;
            }

            y += 8 * scale;
            Fill(new Rect(panel.x, y, width, 1), PanelLine);
            y += 1 + 8 * scale;
            Label(new Rect(x, y, labelWidth, rowHeight), "INTERPOLATION", _overlaySmall, MutedColor);
            Label(new Rect(xColumn, y, 2 * columnWidth, rowHeight), InterpolationText, _overlayText, MutedColor);
            y += rowHeight;
            Label(new Rect(x, y, labelWidth, rowHeight), "CHECKPOINTS", _overlaySmall, MutedColor);
            Label(new Rect(xColumn, y, 2 * columnWidth, rowHeight), CheckpointRespawns ? "ON" : "OFF", CheckpointRespawns ? _overlayTitle : _overlayText, CheckpointRespawns ? JumpColor : MutedColor);
            y += rowHeight;
            Label(new Rect(x, y, labelWidth, rowHeight), "CLONES", _overlaySmall, MutedColor);
            Label(new Rect(xColumn, y, 2 * columnWidth, rowHeight), _clonesText, _overlayText, MutedColor);
            y += rowHeight;
            Label(new Rect(x, y, labelWidth, rowHeight), "SEED (unoptimized)", _overlaySmall, MutedColor);
            Label(new Rect(xColumn, y, 2 * columnWidth, rowHeight), SeedText, _overlayText, MutedColor);
            DrawDebugSection(panel, x, y + rowHeight, scale);
        }

        void DrawNudge(Rect r)
        {
            float age = Time.unscaledTime - _nudgeTime;
            if (age < NudgeLingerSeconds)
                Label(r, _nudgeText, _overlayText, new Color(JumpColor.r, JumpColor.g, JumpColor.b, 1f - age / NudgeLingerSeconds));
            else Label(new Rect(r.x, r.y, r.width / 2f, r.height), "0", _overlayText, MutedColor);
        }

        /// <summary>Interpolation stays on; playback runs on the fixed clock that turbo and enginesim model (MacroEngine.FrameGrid).</summary>
        static readonly string InterpolationText = "ON (" + MacroEngine.PlaybackFrameRate.ToString(CultureInfo.InvariantCulture) + " FPS CLOCK)";
        static readonly string SeedText = FullTasSeed.ToString(CultureInfo.InvariantCulture);
    }
}
