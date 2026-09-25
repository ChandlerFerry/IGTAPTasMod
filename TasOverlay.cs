using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        sealed class InputRow
        {
            public string tickText;
            public float x, y;
            public string stickText;
            public bool jump, jumpPress, dash;
        }

        const int MaxInputRows = 30;
        readonly List<InputRow> _inputRows = new List<InputRow>();
        readonly List<BoxCollider2D> _hitboxes = new List<BoxCollider2D>();
        Movement _overlayPlayer;
        Vector2 _overlayAxis;
        long _overlayLastTick;
        long _overlayTicks;
        string _overlayRoute;
        string _overlayCourse = "—";
        string _overlayClock;
        int _overlayJumpPresses, _overlayDashPresses;
        GUIStyle _overlayTitle, _overlayTitleRight, _overlayText, _overlayTicksStyle, _overlaySmall, _overlaySmallRight, _overlayGraze;
        int _overlayStyleHeight;

        int _ticksSinceFrame;
        int _droppedFrames, _worstFrameTicks;
        long _lastDropTick;
        const float DroppedFramesLingerSeconds = 10f;
        float _droppedFramesShownUntil;

        void OverlayFrame()
        {
            CountFrame();
            int ticks = _ticksSinceFrame;
            _ticksSinceFrame = 0;
            if (ticks <= 1 || !OverlayListsRun) return;
            if (_droppedFrames == 0)
                Log.LogWarning("Playback is dropping frames: " + ticks + " physics ticks in one frame at tick " + _engine.PhysicsTicks
                    + ". Update-driven game state (clone pay) may differ from the route's; the overlay counts every drop.");
            _droppedFrames += ticks - 1;
            _worstFrameTicks = Mathf.Max(_worstFrameTicks, ticks);
            _lastDropTick = _engine.PhysicsTicks;
            _droppedFramesShownUntil = Time.unscaledTime + DroppedFramesLingerSeconds;
        }

        string DroppedFramesText()
        {
            return _droppedFrames == 0 ? "" : " · " + _droppedFrames + " dropped frame" + (_droppedFrames == 1 ? "" : "s")
                + " (up to " + _worstFrameTicks + " ticks in one frame, last at tick " + _lastDropTick + ")";
        }

        bool OverlayListsRun { get { return _engine.IsRunning && !_engine.Quiet && !_silentEvaluation && !TurboRunner.Active; } }

        void OverlayCaptureAxis(Movement player)
        {
            if (_engine.IsRunning) _overlayAxis = player.MoveAxis;
        }

        void OverlayTick(Movement player)
        {
            _overlayPlayer = player;
            if (!OverlayListsRun) _overlayLastTick = 0;
            else if (_engine.PhysicsTicks != _overlayLastTick)
            {
                if (_overlayLastTick == 0 || _engine.PhysicsTicks < _overlayLastTick || !ReferenceEquals(_engine.ActiveMacroName, _overlayRoute))
                {
                    _inputRows.Clear();
                    _overlayTicks = 0;
                    _droppedFrames = _worstFrameTicks = 0;
                    _lastDropTick = 0;
                    _ticksSinceFrame = 0;
                    _overlayRoute = _engine.ActiveMacroName;
                    _overlayCourse = _runCourse != null ? _runCourse.Token : "—";
                }
                _overlayLastTick = _engine.PhysicsTicks;
                _overlayTicks++;
                _ticksSinceFrame++;
                _overlayClock = null;
                InputRow row;
                if (_inputRows.Count < MaxInputRows) row = new InputRow();
                else
                {
                    row = _inputRows[0];
                    _inputRows.RemoveAt(0);
                }
                row.tickText = _overlayTicks.ToString();
                row.x = _overlayAxis.x;
                row.y = _overlayAxis.y;
                row.stickText = StickText(row.x, row.y);
                row.jumpPress = _engine.JumpPresses != _overlayJumpPresses;
                row.jump = _engine.JumpHeld || row.jumpPress;
                row.dash = _engine.DashPresses != _overlayDashPresses;
                _inputRows.Add(row);
            }
            _overlayJumpPresses = _engine.JumpPresses;
            _overlayDashPresses = _engine.DashPresses;
            SampleVelocity(player);
        }

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
            if (_overlayPlayer != null) SampleSpikes(_overlayPlayer, FixedStep());
            DrawSpikes(scale);
            DrawHitbox(scale);
            DrawGraze(scale);
            if (_inputRows.Count > 0) DrawInputPanel(scale);
            if (_overlayPlayer != null) DrawVelocityPanel(scale);
            if (_droppedFrames > 0 && (OverlayListsRun || Time.unscaledTime < _droppedFramesShownUntil)) DrawDroppedFrames(scale);
        }

        void DrawDroppedFrames(float scale)
        {
            string text = "DROPPING FRAMES · " + _droppedFrames + " tick" + (_droppedFrames == 1 ? "" : "s") + " without a frame · worst "
                + _worstFrameTicks + " ticks/frame · last at tick " + _lastDropTick;
            float width = 760 * scale, height = 40 * scale;
            Rect box = new Rect((Screen.width - width) / 2f, 16 * scale, width, height);
            Fill(box, new Color(0.55f, 0f, 0f, 0.85f));
            Label(new Rect(box.x + 14 * scale, box.y, width - 28 * scale, height), text, _overlayText, Color.white);
        }

        void BuildOverlayStyles(float scale)
        {
            _overlayStyleHeight = Screen.height;
            float panelScale = PanelScale(scale);
            _overlayTitle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(20 * panelScale), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, wordWrap = false };
            _overlayTitle.normal.textColor = Color.white;
            _overlayTitleRight = new GUIStyle(_overlayTitle) { alignment = TextAnchor.MiddleRight };
            _overlayText = new GUIStyle(_overlayTitle) { fontSize = Mathf.RoundToInt(18 * panelScale), fontStyle = FontStyle.Normal };
            _overlayTicksStyle = new GUIStyle(_overlayText) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Overflow };
            _overlaySmall = new GUIStyle(_overlayTitle) { fontSize = Mathf.RoundToInt(14 * panelScale) };
            _overlaySmallRight = new GUIStyle(_overlaySmall) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Overflow };
            _overlayGraze = new GUIStyle(_overlayTitle) { fontSize = Mathf.RoundToInt(18 * scale), alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
        }

        const float PanelWidth = 440f;
        static float PanelScale(float scale) { return Mathf.Min(scale, 1f); }

        static readonly FieldInfo NormalColliderField = AccessTools.Field(typeof(Movement), "normalCollider");

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

        static Rect ScreenRect(Camera cam, Bounds bounds)
        {
            Vector3 a = cam.WorldToScreenPoint(bounds.min), b = cam.WorldToScreenPoint(bounds.max);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Max(a.x, b.x), Screen.height - Mathf.Min(a.y, b.y));
        }

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
            public bool moving;
            public int shapeCount = -1;
            public Bounds bounds;
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

        long _spikeSampleStep = long.MinValue;
        readonly HashSet<spikeScript> _grazeSpikes = new HashSet<spikeScript>();
        Bounds _grazeHurtbox;
        int _grazeTicks, _grazeLimit;
        bool _grazeKilled, _grazeGrounded, _grazeDashing;
        const float GrazeLingerSeconds = 1.5f;
        int _lastGrazeTicks, _lastGrazeLimit;
        bool _lastGrazeKilled;
        float _lastGrazeTime = float.NegativeInfinity;

        static long FixedStep() { return (long)System.Math.Round(Time.fixedTimeAsDouble / Time.fixedDeltaTime); }

        void OverlayBeforeTick(Movement player)
        {
            _positionBeforeTick = player.transform.position;
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
                    if (!entry.moving && (entry.shapeCount != c.shapeCount || entry.bounds != c.bounds)) BuildShapes(entry);
                    _spikeColliders.Add(entry);
                }
            }
        }

        void BuildShapes(SpikeCollider entry)
        {
            Collider2D c = entry.collider;
            entry.shapes.Clear();
            entry.shapeCount = c.shapeCount;
            entry.bounds = c.bounds;
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

            Rect pixels = cam.pixelRect;
            material.SetPass(0);
            GL.PushMatrix();
            GL.Viewport(pixels);
            GL.LoadPixelMatrix(pixels.xMin, pixels.xMax, Screen.height - pixels.yMin, Screen.height - pixels.yMax);
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
            GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
        }

        static Rect BoundsRect(Bounds b) { return Rect.MinMaxRect(b.min.x, b.min.y, b.max.x, b.max.y); }

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
                text = _lastGrazeKilled ? "SPIKE ✗" : "SPIKE GRACE " + _lastGrazeTicks + "/" + _lastGrazeLimit;
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
        static readonly Color TasColor = Hex(0xffa3b4, 1f);
        static readonly Color WallJumpColor = Hex(0x5bf08a, 1f);
        static readonly Color DashCapColor = Hex(0x6cc8ff, 1f);
        static readonly Color AirJumpColor = Hex(0xffd84d, 1f);
        static readonly Color BlockSwapColor = Hex(0xffab70, 1f);
        static readonly Color OmniDashColor = Hex(0xdab3ff, 1f);
        static readonly Color PanelBack = Hex(0x0d1a2d, 0.92f);
        static readonly Color PanelBand = Hex(0x12243d, 0.95f);
        static readonly Color PanelStripe = Hex(0x12243d, 0.45f);
        static readonly Color PanelLine = Hex(0x203653, 1f);
        static readonly Color PanelCurrent = Hex(0x1976ff, 0.32f);
        static readonly Color PanelText = Hex(0xf5f9ff, 1f);
        static readonly Color MutedColor = Hex(0x8fa8c4, 1f);
        static readonly Color JumpColor = Hex(0xffbf5b, 1f);
        static readonly Color HeldColor = Hex(0xffbf5b, 0.5f);
        static readonly Color DashColor = Hex(0x48b9ff, 1f);

        static Color Hex(int rgb, float alpha)
        {
            return new Color((rgb >> 16 & 0xff) / 255f, (rgb >> 8 & 0xff) / 255f, (rgb & 0xff) / 255f, alpha);
        }

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

        static readonly GUIContent OverlayMeasure = new GUIContent();

        static float Segment(float x, float y, float height, float gap, string text, GUIStyle style, Color color)
        {
            OverlayMeasure.text = text;
            float width = style.CalcSize(OverlayMeasure).x;
            Label(new Rect(x, y, width, height), text, style, color);
            return x + width + gap;
        }

        void DrawPanelTitle(float x, float y, float height, float scale)
        {
            float gap = 6 * scale;
            x = Segment(x, y, height, gap, "TAS", _overlayTitle, TasColor);
            x = Segment(x, y, height, gap, "-", _overlayTitle, MutedColor);
            x = Segment(x, y, height, gap, _overlayCourse, _overlayTitle, PanelText);
            Movement p = _overlayPlayer;
            if (p == null) return;
            x = Segment(x, y, height, gap, "-", _overlayTitle, MutedColor);
            bool any = false;
            if (p.wallJumpUnlocked) { x = Segment(x, y, height, gap, "WJ", _overlayTitle, WallJumpColor); any = true; }
            if (p.dashUnlocked) { x = Segment(x, y, height, gap, "DS", _overlayTitle, DashCapColor); any = true; }
            if (p.doubleJumpUnlocked) { x = Segment(x, y, height, gap, "AJ", _overlayTitle, AirJumpColor); any = true; }
            if (p.blockSwapUnlocked) { x = Segment(x, y, height, gap, "OR", _overlayTitle, BlockSwapColor); any = true; }
            if (p.omniDashUnlocked) { x = Segment(x, y, height, gap, "OM", _overlayTitle, OmniDashColor); any = true; }
            if (!any) Segment(x, y, height, gap, "NONE", _overlayTitle, MutedColor);
        }

        void DrawInputPanel(float screenScale)
        {
            float scale = PanelScale(screenScale);
            if (_overlayClock == null)
                _overlayClock = _overlayTicks + " ticks · " + (_overlayTicks * Time.fixedDeltaTime).ToString("0.00") + " s";
            float pad = 20 * scale, rowHeight = 30 * scale, titleLine = 26 * scale, clockLine = 22 * scale, columnLine = 28 * scale, width = PanelWidth * scale;
            float bandHeight = 14 * scale + titleLine + clockLine + 12 * scale;
            float headerHeight = bandHeight + columnLine + 1;
            int fit = Mathf.Max(1, Mathf.FloorToInt((Screen.height - headerHeight - 12 * scale) / rowHeight));
            int shown = Mathf.Min(fit, _inputRows.Count);
            Rect panel = new Rect(0f, 0f, width, headerHeight + shown * rowHeight + 12 * scale);
            Fill(panel, PanelBack);
            Fill(new Rect(panel.x, panel.y, width, bandHeight), PanelBand);
            Fill(new Rect(panel.xMax - 1, panel.y, 1, panel.height), PanelLine);
            Fill(new Rect(panel.x, panel.yMax - 1, width, 1), PanelLine);

            float x = panel.x + pad, y = panel.y + 14 * scale, inner = width - 2 * pad;
            DrawPanelTitle(x, y, titleLine, scale);
            Label(new Rect(x, y + titleLine, inner, clockLine), _overlayClock, _overlaySmall, MutedColor);
            y = panel.y + bandHeight;
            Fill(new Rect(panel.x, y, width, 1), PanelLine);

            float tickWidth = 56 * scale, stickX = x + 84 * scale, jumpX = x + 200 * scale, dashX = x + 290 * scale;
            Label(new Rect(x, y, tickWidth, columnLine), "TICK", _overlaySmallRight, MutedColor);
            Label(new Rect(stickX, y, 110 * scale, columnLine), "STICK", _overlaySmall, MutedColor);
            Label(new Rect(jumpX, y, 80 * scale, columnLine), "JUMP", _overlaySmall, MutedColor);
            Label(new Rect(dashX, y, 80 * scale, columnLine), "DASH", _overlaySmall, MutedColor);
            Fill(new Rect(panel.x, y + columnLine, width, 1), PanelLine);
            y = panel.y + headerHeight;

            bool live = _overlayLastTick != 0;
            for (int i = _inputRows.Count - 1, n = 0; i >= _inputRows.Count - shown; i--, n++, y += rowHeight)
            {
                InputRow row = _inputRows[i];
                Rect band = new Rect(panel.x, y, width - 1, rowHeight);
                if (live && n == 0) Fill(band, PanelCurrent);
                else if (n % 2 == 1) Fill(band, PanelStripe);
                Label(new Rect(x, y, tickWidth, rowHeight), row.tickText, _overlayTicksStyle, n == 0 ? PanelText : MutedColor);
                Label(new Rect(stickX, y, 110 * scale, rowHeight), row.stickText, _overlayText, row.x == 0f && row.y == 0f ? MutedColor : PanelText);
                if (row.jump) Label(new Rect(jumpX, y, 80 * scale, rowHeight), row.jumpPress ? "JUMP" : "held", _overlayText, row.jumpPress ? JumpColor : HeldColor);
                if (row.dash) Label(new Rect(dashX, y, 80 * scale, rowHeight), "DASH", _overlayText, DashColor);
            }
        }
    }
}
