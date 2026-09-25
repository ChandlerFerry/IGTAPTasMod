using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace IGTAPTasMod
{
    // What viewers see in the game while the studio's "Show TAS overlay" is on: the player's hurtbox, every spike's
    // collider, the spike grace count while the hurtbox is inside one, and on the left the inputs playback presses, one
    // row per run of identical physics ticks (newest last, like a TAS input file). Only real-time playback is listed;
    // solver and turbo runs are not.
    public sealed partial class TasPlugin
    {
        sealed class InputRow
        {
            // Set on the row that starts a route's inputs (a chain lists each segment's); such a row has no inputs.
            public string header;
            public int ticks;
            public string ticksText;
            // Movement.MoveAxis as the game used it: x 0, ±1 or 0.3..0.8 in size (a stick short of full), y 0 or ±1.
            public float x, y;
            public string stickText;
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
        GUIStyle _overlayTitle, _overlayText, _overlayTicksStyle, _overlayGraze;
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
                float x = _overlayAxis.x, y = _overlayAxis.y;
                bool jumpPress = _engine.JumpPresses != _overlayJumpPresses;
                bool dash = _engine.DashPresses != _overlayDashPresses;
                bool jump = _engine.JumpHeld || jumpPress;
                // A press starts a row; a dash is a tap, so its row is that one tick.
                InputRow row = _inputRows[_inputRows.Count - 1];
                if (row.header == null && !row.dash && !dash && !jumpPress && row.x == x && row.y == y && row.jump == jump) row.ticks++;
                else
                {
                    row = new InputRow { ticks = 1, x = x, y = y, stickText = StickText(x, y), jump = jump, dash = dash };
                    _inputRows.Add(row);
                    if (_inputRows.Count > MaxInputRows) _inputRows.RemoveAt(0);
                }
                row.ticksText = row.ticks.ToString();
            }
            _overlayJumpPresses = _engine.JumpPresses;
            _overlayDashPresses = _engine.DashPresses;
        }

        // "→", "← ↑", "→ 53%" (a stick short of full), "·" for no stick.
        static string StickText(float x, float y)
        {
            string text = x < 0f ? "←" : x > 0f ? "→" : "";
            if (x != 0f && Mathf.Abs(x) < 1f) text += " " + Mathf.RoundToInt(Mathf.Abs(x) * 100f) + "%";
            if (y != 0f) text += (text.Length > 0 ? " " : "") + (y > 0f ? "↑" : "↓");
            return text.Length > 0 ? text : "·";
        }

        private void OnGUI()
        {
            if (!_settings.showOverlay || Event.current.type != EventType.Repaint) return;
            float scale = Screen.height / 1080f;
            if (_overlayTitle == null || _overlayStyleHeight != Screen.height) BuildOverlayStyles(scale);
            // Update runs after the frame's physics steps, so this is the state after the last one.
            if (_overlayPlayer != null) SampleSpikes(_overlayPlayer, FixedStep());
            DrawSpikes(scale);
            DrawHitbox(scale);
            DrawGraze(scale);
            if (_inputRows.Count > 0) DrawInputPanel(scale);
        }

        void BuildOverlayStyles(float scale)
        {
            _overlayStyleHeight = Screen.height;
            _overlayTitle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(22 * scale), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _overlayTitle.normal.textColor = Color.white;
            _overlayText = new GUIStyle(_overlayTitle) { fontSize = Mathf.RoundToInt(18 * scale) };
            _overlayTicksStyle = new GUIStyle(_overlayText) { alignment = TextAnchor.MiddleRight };
            _overlayGraze = new GUIStyle(_overlayText) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
        }

        static readonly FieldInfo NormalColliderField = AccessTools.Field(typeof(Movement), "normalCollider");

        // The player has two boxes: Movement.normalCollider, the body that collides with Ground, and a smaller hurtbox
        // that collides with everything else (Default and Spikes: what spikes and gates touch). Only the hurtbox is drawn.
        static BoxCollider2D Hurtbox(Movement player, List<BoxCollider2D> buffer)
        {
            object body = NormalColliderField == null ? null : NormalColliderField.GetValue(player);
            player.GetComponents(buffer);
            foreach (BoxCollider2D box in buffer)
                if (box.enabled && !ReferenceEquals(box, body)) return box;
            return null;
        }

        void DrawHitbox(float scale)
        {
            Movement player = _overlayPlayer;
            Camera cam = Camera.main;
            if (player == null || cam == null) return;
            BoxCollider2D box = Hurtbox(player, _hitboxes);
            if (box == null) return;
            Rect r = ScreenRect(cam, box.bounds);
            Color color = _grazeTicks > 0 && !_grazeKilled ? GraceColor : HitboxColor;
            float line = Mathf.Max(1f, Mathf.Round(2 * scale));
            Fill(r, new Color(color.r, color.g, color.b, 0.22f));
            Fill(new Rect(r.xMin, r.yMin, r.width, line), color);
            Fill(new Rect(r.xMin, r.yMax - line, r.width, line), color);
            Fill(new Rect(r.xMin, r.yMin, line, r.height), color);
            Fill(new Rect(r.xMax - line, r.yMin, line, r.height), color);
        }

        // World bounds to a GUI rect (origin top left).
        static Rect ScreenRect(Camera cam, Bounds bounds)
        {
            Vector3 a = cam.WorldToScreenPoint(bounds.min), b = cam.WorldToScreenPoint(bounds.max);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Max(a.x, b.x), Screen.height - Mathf.Min(a.y, b.y));
        }

        // ---- Spikes ----
        // spikeScript: the first physics step the hurtbox overlaps a spike is its trigger's Enter, which spikeScript
        // ignores; each later step is a Stay that adds 1 to its counter, and the Stay that takes counter past
        // graceFrames (1 on every course) kills. Any Stay while dashing kills too (killPlayerOnDash). Exit resets
        // counter. So the hurtbox may sit inside spikes for graceFrames + 1 = 2 steps (1 while dashing): step 1 is the
        // Enter, step 2 the Stay with counter 1, and a third step is death.
        static readonly FieldInfo SpikeCounterField = AccessTools.Field(typeof(spikeScript), "counter");
        static readonly FieldInfo SpikeGraceField = AccessTools.Field(typeof(spikeScript), "graceFrames");
        static readonly FieldInfo SpikeOnDashField = AccessTools.Field(typeof(spikeScript), "killPlayerOnDash");
        static readonly FieldInfo SpikeKilledField = AccessTools.Field(typeof(spikeScript), "playerKilled");

        sealed class SpikeShape
        {
            public Vector2[] points;
            public bool closed;
            public Rect bounds;
        }

        sealed class SpikeCollider
        {
            public Collider2D collider;
            public spikeScript script;
            // Static colliders keep their shapes; a moving one rebuilds them every frame.
            public bool moving;
            public int shapeCount = -1;
            public readonly List<SpikeShape> shapes = new List<SpikeShape>();
        }

        const float SpikeScanSeconds = 1f;
        readonly List<SpikeCollider> _spikeColliders = new List<SpikeCollider>();
        readonly Dictionary<Collider2D, SpikeCollider> _spikeByCollider = new Dictionary<Collider2D, SpikeCollider>();
        readonly List<Collider2D> _colliderBuffer = new List<Collider2D>();
        readonly List<Collider2D> _overlaps = new List<Collider2D>();
        readonly PhysicsShapeGroup2D _shapeGroup = new PhysicsShapeGroup2D();
        readonly List<Vector2> _shapeVertices = new List<Vector2>();
        readonly List<Vector2> _screenPoints = new List<Vector2>();
        float _spikeScanTime = float.NegativeInfinity;
        Material _overlayMaterial;
        bool _overlayMaterialMissing;

        // The last sampled physics step: spikes the hurtbox is in, the step count of the graze (1 = Enter), how many
        // steps it can last, and whether a spike killed the player.
        long _spikeSampleStep = long.MinValue;
        readonly HashSet<spikeScript> _grazeSpikes = new HashSet<spikeScript>();
        Bounds _grazeHurtbox;
        int _grazeTicks, _grazeLimit;
        bool _grazeKilled, _grazeGrounded, _grazeDashing;
        // The graze that just ended, shown for GrazeLingerSeconds so viewers catch a 2-step graze (40 ms).
        const float GrazeLingerSeconds = 1.5f;
        int _lastGrazeTicks, _lastGrazeLimit;
        bool _lastGrazeKilled;
        float _lastGrazeTime = float.NegativeInfinity;

        // The physics step whose results are current: in FixedUpdate the one before Time.fixedTime's, elsewhere its own.
        static long FixedStep() { return (long)System.Math.Round(Time.fixedTimeAsDouble / Time.fixedDeltaTime); }

        // Movement.FixedUpdate prefix: the previous step's physics and trigger callbacks are done, this tick's movement
        // has not run. Sampled there too, so a frame that runs several steps misses none of a graze.
        void OverlayBeforeTick(Movement player)
        {
            if (_settings.showOverlay) SampleSpikes(player, FixedStep() - 1);
        }

        void SampleSpikes(Movement player, long step)
        {
            if (step == _spikeSampleStep) return;
            _spikeSampleStep = step;
            if (Time.unscaledTime - _spikeScanTime >= SpikeScanSeconds) ScanSpikes();
            int ticks = 0, limit = int.MaxValue;
            bool killed = false, dashing = player.dashActive;
            _grazeSpikes.Clear();
            BoxCollider2D hurtbox = Hurtbox(player, _hitboxes);
            if (hurtbox != null)
            {
                // The spikes a trigger overlap test finds now, with the hurtbox's own contact layers: what gets
                // Enter/Stay at this step.
                var filter = new ContactFilter2D { useTriggers = true, useLayerMask = true };
                filter.layerMask = (Physics2D.GetLayerCollisionMask(hurtbox.gameObject.layer) | hurtbox.includeLayers) & ~hurtbox.excludeLayers;
                hurtbox.Overlap(filter, _overlaps);
                foreach (Collider2D c in _overlaps)
                {
                    spikeScript spike = c.GetComponent<spikeScript>();
                    if (spike == null || !spike.enabled || !_grazeSpikes.Add(spike)) continue;
                    int counter = (int)SpikeCounterField.GetValue(spike);
                    ticks = Mathf.Max(ticks, counter + 1);
                    killed |= (bool)SpikeKilledField.GetValue(spike);
                    limit = Mathf.Min(limit, dashing && (bool)SpikeOnDashField.GetValue(spike) ? 1 : (int)SpikeGraceField.GetValue(spike) + 1);
                }
                _grazeHurtbox = hurtbox.bounds;
            }
            if (ticks == 0 && _grazeTicks > 0)
            {
                _lastGrazeTicks = _grazeTicks;
                _lastGrazeLimit = _grazeLimit;
                _lastGrazeKilled = _grazeKilled;
                _lastGrazeTime = Time.unscaledTime;
            }
            _grazeTicks = ticks;
            _grazeLimit = ticks > 0 ? limit : 0;
            _grazeKilled = killed;
            _grazeDashing = dashing;
            _grazeGrounded = player.onGround;
        }

        // Every enabled spike collider; a tilemap collider merged into a composite is drawn as the composite.
        void ScanSpikes()
        {
            _spikeScanTime = Time.unscaledTime;
            _spikeColliders.Clear();
            foreach (spikeScript spike in FindObjectsByType<spikeScript>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                spike.GetComponents(_colliderBuffer);
                foreach (Collider2D c in _colliderBuffer)
                {
                    if (!c.enabled || c.compositeOperation != Collider2D.CompositeOperation.None) continue;
                    if (!_spikeByCollider.TryGetValue(c, out SpikeCollider entry))
                        _spikeByCollider[c] = entry = new SpikeCollider { collider = c, script = spike };
                    Rigidbody2D body = c.attachedRigidbody;
                    entry.moving = body != null && body.bodyType != RigidbodyType2D.Static;
                    if (!entry.moving && entry.shapeCount != c.shapeCount) BuildShapes(entry);
                    _spikeColliders.Add(entry);
                }
            }
        }

        // The collider's physics shapes (Collider2D.GetShapes: what Box2D collides), in world space.
        void BuildShapes(SpikeCollider entry)
        {
            Collider2D c = entry.collider;
            entry.shapes.Clear();
            entry.shapeCount = c.shapeCount;
            int count = c.GetShapes(_shapeGroup);
            Matrix4x4 m = _shapeGroup.localToWorldMatrix;
            float radiusScale = Mathf.Max(Mathf.Abs(m.lossyScale.x), Mathf.Abs(m.lossyScale.y));
            for (int i = 0; i < count; i++)
            {
                PhysicsShape2D shape = _shapeGroup.GetShape(i);
                _shapeVertices.Clear();
                _shapeGroup.GetShapeVertices(i, _shapeVertices);
                for (int v = 0; v < _shapeVertices.Count; v++) _shapeVertices[v] = m.MultiplyPoint3x4(_shapeVertices[v]);
                Vector2[] points;
                bool closed = true;
                float radius = shape.radius * radiusScale;
                switch (shape.shapeType)
                {
                    case PhysicsShapeType2D.Circle:
                        points = RoundCap(_shapeVertices[0], _shapeVertices[0] + Vector2.right, radius, 0, 16);
                        break;
                    case PhysicsShapeType2D.Capsule:
                        Vector2[] a = RoundCap(_shapeVertices[0], _shapeVertices[1], radius, 1, 8), b = RoundCap(_shapeVertices[1], _shapeVertices[0], radius, 1, 8);
                        points = new Vector2[a.Length + b.Length];
                        a.CopyTo(points, 0);
                        b.CopyTo(points, a.Length);
                        break;
                    case PhysicsShapeType2D.Edges:
                        points = _shapeVertices.ToArray();
                        closed = false;
                        break;
                    default:
                        points = _shapeVertices.ToArray();
                        break;
                }
                if (points.Length == 0) continue;
                Vector2 lo = points[0], hi = points[0];
                foreach (Vector2 p in points) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
                entry.shapes.Add(new SpikeShape { points = points, closed = closed, bounds = Rect.MinMaxRect(lo.x, lo.y, hi.x, hi.y) });
            }
        }

        // Points around a circle at centre, facing away from toward: a half circle (half = 1) or the whole (half = 0).
        static Vector2[] RoundCap(Vector2 centre, Vector2 toward, float radius, int half, int segments)
        {
            Vector2 d = toward - centre;
            float start = Mathf.Atan2(d.y, d.x) + (half == 1 ? Mathf.PI / 2 : 0);
            float sweep = half == 1 ? Mathf.PI : 2 * Mathf.PI;
            int n = half == 1 ? segments + 1 : segments;
            var points = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float t = start + sweep * i / segments;
                points[i] = centre + radius * new Vector2(Mathf.Cos(t), Mathf.Sin(t));
            }
            return points;
        }

        Material OverlayMaterial()
        {
            if (_overlayMaterial != null || _overlayMaterialMissing) return _overlayMaterial;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                _overlayMaterialMissing = true;
                Log.LogWarning("TAS overlay: shader Hidden/Internal-Colored is missing; spikes are not drawn.");
                return null;
            }
            _overlayMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _overlayMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _overlayMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _overlayMaterial.SetInt("_Cull", (int)CullMode.Off);
            _overlayMaterial.SetInt("_ZWrite", 0);
            _overlayMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            return _overlayMaterial;
        }

        // Every spike shape on screen, filled and outlined; the shapes of a spike the hurtbox is in, near it, in the
        // grace colour (red once it killed).
        void DrawSpikes(float scale)
        {
            Camera cam = Camera.main;
            if (cam == null || _spikeColliders.Count == 0) return;
            Material material = OverlayMaterial();
            if (material == null) return;
            Vector3 v0 = cam.ViewportToWorldPoint(Vector3.zero), v1 = cam.ViewportToWorldPoint(Vector3.one);
            Rect view = Rect.MinMaxRect(Mathf.Min(v0.x, v1.x), Mathf.Min(v0.y, v1.y), Mathf.Max(v0.x, v1.x), Mathf.Max(v0.y, v1.y));
            Rect hurt = Rect.MinMaxRect(_grazeHurtbox.min.x - 1, _grazeHurtbox.min.y - 1, _grazeHurtbox.max.x + 1, _grazeHurtbox.max.y + 1);
            Color touched = _grazeKilled ? HitboxColor : GraceColor;
            float half = Mathf.Max(0.75f, scale);

            material.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
            GL.Begin(GL.QUADS);
            foreach (SpikeCollider entry in _spikeColliders)
            {
                if (entry.collider == null || !entry.collider.enabled || !entry.collider.gameObject.activeInHierarchy) continue;
                if (!view.Overlaps(BoundsRect(entry.collider.bounds))) continue;
                if (entry.moving) BuildShapes(entry);
                bool grazed = _grazeTicks > 0 && _grazeSpikes.Contains(entry.script);
                foreach (SpikeShape shape in entry.shapes)
                {
                    if (!view.Overlaps(shape.bounds)) continue;
                    Color color = grazed && hurt.Overlaps(shape.bounds) ? touched : SpikeColor;
                    _screenPoints.Clear();
                    foreach (Vector2 p in shape.points)
                    {
                        Vector3 s = cam.WorldToScreenPoint(p);
                        _screenPoints.Add(new Vector2(s.x, Screen.height - s.y));
                    }
                    if (shape.closed && _screenPoints.Count >= 3)
                    {
                        // Physics polygons are convex: a fan, each triangle as a quad with its last corner doubled.
                        GL.Color(new Color(color.r, color.g, color.b, 0.18f));
                        for (int i = 1; i + 1 < _screenPoints.Count; i++)
                        {
                            GL.Vertex(_screenPoints[0]);
                            GL.Vertex(_screenPoints[i]);
                            GL.Vertex(_screenPoints[i + 1]);
                            GL.Vertex(_screenPoints[i + 1]);
                        }
                    }
                    GL.Color(color);
                    int edges = shape.closed ? _screenPoints.Count : _screenPoints.Count - 1;
                    for (int i = 0; i < edges; i++)
                    {
                        Vector2 a = _screenPoints[i], b = _screenPoints[(i + 1) % _screenPoints.Count];
                        Vector2 d = b - a;
                        if (d.sqrMagnitude < 1e-6f) continue;
                        Vector2 n = new Vector2(-d.y, d.x).normalized * half;
                        GL.Vertex(a + n);
                        GL.Vertex(b + n);
                        GL.Vertex(b - n);
                        GL.Vertex(a - n);
                    }
                }
            }
            GL.End();
            GL.PopMatrix();
        }

        static Rect BoundsRect(Bounds b) { return Rect.MinMaxRect(b.min.x, b.min.y, b.max.x, b.max.y); }

        // Above the hurtbox: "SPIKE 1/2", "SPIKE 2/2" while inside a spike (steps so far / steps it can last), and for
        // GrazeLingerSeconds after it leaves, how long the graze lasted.
        void DrawGraze(float scale)
        {
            Movement player = _overlayPlayer;
            Camera cam = Camera.main;
            if (player == null || cam == null) return;
            string text;
            Color color;
            if (_grazeTicks > 0)
            {
                text = _grazeKilled ? "SPIKE ✗ " + _grazeTicks + "/" + _grazeLimit : "SPIKE " + _grazeTicks + "/" + _grazeLimit;
                if (_grazeDashing) text += " · DASH";
                if (_grazeGrounded) text += " · GROUNDED";
                color = _grazeKilled ? HitboxColor : GraceColor;
            }
            else if (Time.unscaledTime - _lastGrazeTime < GrazeLingerSeconds)
            {
                text = _lastGrazeKilled ? "SPIKE ✗" : "SPIKE GRAZE " + _lastGrazeTicks + "/" + _lastGrazeLimit + " SURVIVED";
                color = _lastGrazeKilled ? HitboxColor : GraceColor;
                color.a = 1f - (Time.unscaledTime - _lastGrazeTime) / GrazeLingerSeconds;
            }
            else return;
            BoxCollider2D box = Hurtbox(player, _hitboxes);
            if (box == null) return;
            Rect r = ScreenRect(cam, box.bounds);
            float width = 360 * scale, height = 28 * scale;
            Rect label = new Rect(r.center.x - width / 2, r.yMin - height - 6 * scale, width, height);
            Fill(new Rect(label.x + width / 2 - 130 * scale, label.y, 260 * scale, height), new Color(0f, 0f, 0f, 0.55f * color.a));
            Label(label, text, _overlayGraze, color);
        }

        static readonly Color HitboxColor = new Color(1f, 0.2f, 0.25f, 0.95f);
        static readonly Color SpikeColor = new Color(1f, 0.55f, 0.1f, 0.9f);
        static readonly Color GraceColor = new Color(0.35f, 1f, 0.45f, 0.95f);
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
                _overlayClock = _overlayTicks + " ticks = " + (_overlayTicks * Time.fixedDeltaTime).ToString("0.00") + " s  (1 tick = "
                    + Mathf.RoundToInt(Time.fixedDeltaTime * 1000f) + " ms)";
            float margin = 16 * scale, pad = 14 * scale, rowHeight = 26 * scale, width = 340 * scale;
            float headerHeight = pad + 24 * scale + 24 * scale + 26 * scale + 4 * scale;
            int fit = Mathf.Max(1, Mathf.FloorToInt((Screen.height - 2 * margin - headerHeight - pad) / rowHeight));
            int shown = Mathf.Min(fit, _inputRows.Count);
            Rect panel = new Rect(margin, margin, width, headerHeight + shown * rowHeight + pad);
            Fill(panel, new Color(0f, 0f, 0f, 0.7f));
            Fill(new Rect(panel.x, panel.y, 4 * scale, panel.height), HitboxColor);

            float x = panel.x + pad, y = panel.y + pad;
            Label(new Rect(x, y, width - 2 * pad, 24 * scale), _overlayClock, _overlayText, DimColor);
            float legend = y + 24 * scale, swatch = 12 * scale, swatchY = legend + 6 * scale;
            Fill(new Rect(x, swatchY, swatch, swatch), SpikeColor);
            Label(new Rect(x + 18 * scale, legend, 70 * scale, 24 * scale), "SPIKES", _overlayText, DimColor);
            Fill(new Rect(x + 88 * scale, swatchY, swatch, swatch), GraceColor);
            Label(new Rect(x + 106 * scale, legend, 80 * scale, 24 * scale), "GRACE", _overlayText, DimColor);

            // Columns: how many ticks the row's inputs are held, the stick, jump held, dash pressed.
            float stickX = x + 64 * scale, jumpX = x + 170 * scale, dashX = x + 240 * scale, columns = legend + 26 * scale;
            Fill(new Rect(panel.x + 4 * scale, columns + 25 * scale, width - 4 * scale, 1), new Color(1f, 1f, 1f, 0.25f));
            Label(new Rect(x, columns, 48 * scale, 24 * scale), "TICKS", _overlayTicksStyle, DimColor);
            Label(new Rect(stickX, columns, 100 * scale, 24 * scale), "STICK", _overlayText, DimColor);
            Label(new Rect(jumpX, columns, 70 * scale, 24 * scale), "JUMP", _overlayText, DimColor);
            Label(new Rect(dashX, columns, 70 * scale, 24 * scale), "DASH", _overlayText, DimColor);
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
                Label(new Rect(x, y, 48 * scale, rowHeight), row.ticksText, _overlayTicksStyle, Color.white);
                Label(new Rect(stickX, y, 100 * scale, rowHeight), row.stickText, _overlayText, row.x == 0f && row.y == 0f ? DimColor : Color.white);
                if (row.jump) Label(new Rect(jumpX, y, 70 * scale, rowHeight), "JUMP", _overlayText, JumpColor);
                if (row.dash) Label(new Rect(dashX, y, 70 * scale, rowHeight), "DASH", _overlayText, DashColor);
            }
        }
    }
}
