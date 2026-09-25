using System.Collections.Generic;
using UnityEngine;

namespace IGTAPTasMod
{
    // What viewers see in the game while the studio's "Show TAS overlay" is on: the player's hitbox, and on the left
    // the inputs playback presses, one row per run of identical physics ticks (newest last, like a TAS input file).
    // Only real-time playback is listed; solver and turbo runs are not.
    public sealed partial class TasPlugin
    {
        sealed class InputRow
        {
            // Set on the row that starts a route's inputs (a chain lists each segment's); such a row has no inputs.
            public string header;
            public int ticks;
            public string ticksText;
            public int x, y;
            public bool jump, dash;
        }

        const int MaxInputRows = 500;
        readonly List<InputRow> _inputRows = new List<InputRow>();
        readonly List<BoxCollider2D> _hitboxes = new List<BoxCollider2D>();
        Movement _overlayPlayer;
        // The move axis playback set on the last tick it gave input (dash ticks take none and keep it).
        Vector2 _overlayAxis;
        // PhysicsTicks of the last listed tick; 0 while no playback runs.
        long _overlayLastTick;
        long _overlayTicks;
        string _overlayRoute;
        string _overlayClock;
        int _overlayJumpPresses, _overlayDashPresses;
        GUIStyle _overlayTitle, _overlayText, _overlayTicksStyle;
        int _overlayStyleHeight;

        bool OverlayListsRun { get { return _engine.IsRunning && !_engine.Quiet && !_silentEvaluation && !TurboRunner.Active; } }

        // Movement.getPlayerControlledInput prefix and the dash-jump press: the axis playback just set.
        void OverlayCaptureAxis(Movement player)
        {
            if (_engine.IsRunning) _overlayAxis = player.MoveAxis;
        }

        // After every physics tick of the world's player (Movement.FixedUpdate postfix), dash ticks included.
        void OverlayTick(Movement player)
        {
            _overlayPlayer = player;
            if (!OverlayListsRun) _overlayLastTick = 0;
            else if (_engine.PhysicsTicks != _overlayLastTick)
            {
                // A run starts listing on its first timed tick. A chain's next segment may start in the same tick its
                // previous one finished, so a new route or a restarted tick count also starts one.
                if (_overlayLastTick == 0 || _engine.PhysicsTicks < _overlayLastTick || !ReferenceEquals(_engine.ActiveMacroName, _overlayRoute))
                {
                    if (_chainRun == null || _chainRun.index == 0) { _inputRows.Clear(); _overlayTicks = 0; }
                    _overlayRoute = _engine.ActiveMacroName;
                    _inputRows.Add(new InputRow { header = "▸ " + (_overlayRoute ?? "") });
                }
                _overlayLastTick = _engine.PhysicsTicks;
                _overlayTicks++;
                _overlayClock = null;
                int x = OverlayAxis(_overlayAxis.x), y = OverlayAxis(_overlayAxis.y);
                bool jumpPress = _engine.JumpPresses != _overlayJumpPresses;
                bool dash = _engine.DashPresses != _overlayDashPresses;
                bool jump = _engine.JumpHeld || jumpPress;
                // A press starts a row; a dash is a tap, so its row is that one tick.
                InputRow row = _inputRows[_inputRows.Count - 1];
                if (row.header == null && !row.dash && !dash && !jumpPress && row.x == x && row.y == y && row.jump == jump) row.ticks++;
                else
                {
                    row = new InputRow { ticks = 1, x = x, y = y, jump = jump, dash = dash };
                    _inputRows.Add(row);
                    if (_inputRows.Count > MaxInputRows) _inputRows.RemoveAt(0);
                }
                row.ticksText = row.ticks.ToString();
            }
            _overlayJumpPresses = _engine.JumpPresses;
            _overlayDashPresses = _engine.DashPresses;
        }

        static int OverlayAxis(float value) { return value > 0.5f ? 1 : value < -0.5f ? -1 : 0; }

        private void OnGUI()
        {
            if (!_settings.showOverlay || Event.current.type != EventType.Repaint) return;
            float scale = Screen.height / 1080f;
            if (_overlayTitle == null || _overlayStyleHeight != Screen.height) BuildOverlayStyles(scale);
            DrawHitbox(scale);
            if (_inputRows.Count > 0) DrawInputPanel(scale);
        }

        void BuildOverlayStyles(float scale)
        {
            _overlayStyleHeight = Screen.height;
            _overlayTitle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(22 * scale), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _overlayTitle.normal.textColor = Color.white;
            _overlayText = new GUIStyle(_overlayTitle) { fontSize = Mathf.RoundToInt(18 * scale) };
            _overlayTicksStyle = new GUIStyle(_overlayText) { alignment = TextAnchor.MiddleRight };
        }

        static readonly System.Reflection.FieldInfo NormalColliderField = HarmonyLib.AccessTools.Field(typeof(Movement), "normalCollider");

        // The player has two boxes: Movement.normalCollider, the body that collides with Ground (41×72, 41×54 while
        // dashing), and a smaller hurtbox that collides with Default and Spikes (what spikes and gates touch).
        void DrawHitbox(float scale)
        {
            Movement player = _overlayPlayer;
            Camera cam = Camera.main;
            if (player == null || cam == null) return;
            object body = NormalColliderField == null ? null : NormalColliderField.GetValue(player);
            player.GetComponents(_hitboxes);
            float line = Mathf.Max(1f, Mathf.Round(2 * scale));
            foreach (BoxCollider2D box in _hitboxes)
            {
                if (!box.enabled) continue;
                Bounds bounds = box.bounds;
                Vector3 a = cam.WorldToScreenPoint(bounds.min), b = cam.WorldToScreenPoint(bounds.max);
                Rect r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Max(a.x, b.x), Screen.height - Mathf.Min(a.y, b.y));
                bool isBody = ReferenceEquals(box, body);
                Color color = isBody ? BodyColor : HitboxColor;
                if (!isBody) Fill(r, new Color(1f, 0.15f, 0.2f, 0.22f));
                Fill(new Rect(r.xMin, r.yMin, r.width, line), color);
                Fill(new Rect(r.xMin, r.yMax - line, r.width, line), color);
                Fill(new Rect(r.xMin, r.yMin, line, r.height), color);
                Fill(new Rect(r.xMax - line, r.yMin, line, r.height), color);
            }
        }

        static readonly Color HitboxColor = new Color(1f, 0.2f, 0.25f, 0.95f);
        static readonly Color BodyColor = new Color(0.3f, 1f, 0.45f, 0.95f);
        static readonly Color JumpColor = new Color(1f, 0.85f, 0.3f);
        static readonly Color DashColor = new Color(0.35f, 0.85f, 1f);
        static readonly Color DimColor = new Color(1f, 1f, 1f, 0.55f);

        static void Fill(Rect r, Color color)
        {
            Color saved = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = saved;
        }

        static void Label(Rect r, string text, GUIStyle style, Color color)
        {
            Color saved = GUI.contentColor;
            GUI.contentColor = color;
            GUI.Label(r, text, style);
            GUI.contentColor = saved;
        }

        void DrawInputPanel(float scale)
        {
            if (_overlayClock == null)
                _overlayClock = _overlayTicks + " ticks · " + (_overlayTicks * Time.fixedDeltaTime).ToString("0.000") + " s";
            float margin = 16 * scale, pad = 14 * scale, rowHeight = 24 * scale, width = 320 * scale;
            float headerHeight = pad + 30 * scale + 24 * scale + 24 * scale + 8 * scale;
            int fit = Mathf.Max(1, Mathf.FloorToInt((Screen.height - 2 * margin - headerHeight - pad) / rowHeight));
            int shown = Mathf.Min(fit, _inputRows.Count);
            Rect panel = new Rect(margin, margin, width, headerHeight + shown * rowHeight + pad);
            Fill(panel, new Color(0f, 0f, 0f, 0.62f));
            Fill(new Rect(panel.x, panel.y, 4 * scale, panel.height), HitboxColor);

            float x = panel.x + pad, y = panel.y + pad;
            Label(new Rect(x, y, width - 2 * pad, 30 * scale), "TAS INPUTS", _overlayTitle, Color.white);
            Label(new Rect(x, y + 30 * scale, width - 2 * pad, 24 * scale), _overlayClock, _overlayText, DimColor);
            float legend = y + 54 * scale, swatch = 12 * scale, swatchY = legend + 6 * scale;
            Fill(new Rect(x, swatchY, swatch, swatch), BodyColor);
            Label(new Rect(x + 18 * scale, legend, 90 * scale, 24 * scale), "BODY", _overlayText, DimColor);
            Fill(new Rect(x + 100 * scale, swatchY, swatch, swatch), HitboxColor);
            Label(new Rect(x + 118 * scale, legend, 170 * scale, 24 * scale), "HURTBOX", _overlayText, DimColor);
            y = panel.y + headerHeight;

            bool live = _overlayLastTick != 0;
            for (int i = _inputRows.Count - shown; i < _inputRows.Count; i++, y += rowHeight)
            {
                InputRow row = _inputRows[i];
                if (row.header != null)
                {
                    Label(new Rect(x, y, width - 2 * pad, rowHeight), row.header, _overlayText, DimColor);
                    continue;
                }
                if (live && i == _inputRows.Count - 1) Fill(new Rect(panel.x + 4 * scale, y, width - 4 * scale, rowHeight), new Color(1f, 1f, 1f, 0.14f));
                Label(new Rect(x, y, 48 * scale, rowHeight), row.ticksText, _overlayTicksStyle, DimColor);
                float k = x + 64 * scale;
                if (row.x < 0) Label(new Rect(k, y, 24 * scale, rowHeight), "←", _overlayText, Color.white);
                if (row.x > 0) Label(new Rect(k + 24 * scale, y, 24 * scale, rowHeight), "→", _overlayText, Color.white);
                if (row.y > 0) Label(new Rect(k + 48 * scale, y, 24 * scale, rowHeight), "↑", _overlayText, Color.white);
                if (row.y < 0) Label(new Rect(k + 72 * scale, y, 24 * scale, rowHeight), "↓", _overlayText, Color.white);
                if (row.jump) Label(new Rect(k + 104 * scale, y, 60 * scale, rowHeight), "JUMP", _overlayText, JumpColor);
                if (row.dash) Label(new Rect(k + 164 * scale, y, 60 * scale, rowHeight), "DASH", _overlayText, DashColor);
            }
        }
    }
}
