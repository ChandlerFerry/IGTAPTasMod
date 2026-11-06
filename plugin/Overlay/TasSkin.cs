using UnityEngine;
using UnityEngine.SceneManagement;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        Transform _tasSkin;
        SpriteRenderer _tasSkinFill, _tasSkinCore, _tasSkinShine, _tasSkinBadge;
        readonly SpriteRenderer[] _tasSkinEdges = new SpriteRenderer[4];
        Movement _tasSkinPlayer;
        Rigidbody2D _tasSkinBody;
        SpriteRenderer _tasSkinHidden, _tasSkinBodySprite;
        int _tasSkinSortLayer, _tasSkinSortOrder;
        Color _tasSkinColor;
        Vector2 _tasSkinSize, _tasSkinLastVelocity, _tasSkinSlosh, _tasSkinSloshSpeed;
        bool _tasSkinPlaced, _tasSkinOutline;

        static Sprite _skinGradient, _skinBlob, _skinBadge;

        /// <summary>The outline's width, drawn inside the bounds: its outer edge is the hurtbox's.</summary>
        const float SkinEdge = 2f;
        /// <summary>The slosh spring: its frequency (Hz), damping ratio, and the share of a velocity change it takes the other way.</summary>
        const float SloshHz = 5f, SloshDamping = 0.22f, SloshKick = 0.25f;
        /// <summary>A velocity change past this in one tick is a respawn or a rebase, not a motion: the slosh ignores it.</summary>
        const float SloshMaxKick = 3000f;

        /// <summary>
        /// The TAS skin (settings tasSkin): the player drawn as its 40×54 hurtbox, its edge exact on every frame, in the hurtbox
        /// colour (the spike-grace colour while the overlay counts a graze the player lives through). A jelly look lives inside
        /// it: a gel gradient, a lighter core that squashes, a shine that sloshes against the body's velocity changes, both kept
        /// inside the outline, and a "TAS" badge. In the player's scene, not under the player: <see cref="PlaceTasSkin"/> puts
        /// it on the hurtbox every frame, so it neither trails the interpolated transform nor mirrors with the facing. The body
        /// sprite is hidden (forceRenderingOff, which the game never writes); the ability indicators under it stay. Set again
        /// each tick, as the hurtbox may change. Its time is /api/perf's <c>skin</c>. With tasSkinOutline only the outline is drawn,
        /// in the hurtbox colour, over the game's own sprite: where the hurtbox is against the art a player knows.
        /// </summary>
        void ApplyTasSkin(Movement player)
        {
            double start = PerfMs(_perfClock);
            if (_settings.tasSkin) UpdateTasSkin(player);
            else RemoveTasSkin();
            _perfNow.skin += (float)(PerfMs(_perfClock) - start);
        }

        void RemoveTasSkin()
        {
            if (_tasSkin != null) Destroy(_tasSkin.gameObject);
            if (_tasSkinHidden != null) _tasSkinHidden.forceRenderingOff = false;
            _tasSkin = null;
            _tasSkinHidden = _tasSkinBodySprite = null;
            _tasSkinPlayer = null;
        }

        void UpdateTasSkin(Movement player)
        {
            if (_tasSkin == null || _tasSkinPlayer != player || _tasSkinOutline != _settings.tasSkinOutline)
            {
                RemoveTasSkin();
                if (!_tasSkinPlaced) Application.onBeforeRender += PlaceTasSkin;
                _tasSkinPlaced = true;
                _tasSkinOutline = _settings.tasSkinOutline;
                BuildTasSkin(player);
                _tasSkinBodySprite = player.animator != null ? player.animator.GetComponent<SpriteRenderer>() : null;
                if (_tasSkinBodySprite != null && !_tasSkinOutline)
                {
                    _tasSkinHidden = _tasSkinBodySprite;
                    _tasSkinHidden.forceRenderingOff = true;
                }
                _tasSkinSortOrder = int.MinValue;
            }
            SpriteRenderer body = _tasSkinBodySprite;
            if (body != null && (body.sortingLayerID != _tasSkinSortLayer || body.sortingOrder != _tasSkinSortOrder))
            {
                _tasSkinSortLayer = body.sortingLayerID;
                _tasSkinSortOrder = body.sortingOrder;
                // Back to front: the fill, the core, the badge, the shine, the outline.
                SortSkin(_tasSkinFill, body, 0);
                SortSkin(_tasSkinCore, body, 1);
                SortSkin(_tasSkinBadge, body, 2);
                SortSkin(_tasSkinShine, body, 3);
                foreach (SpriteRenderer edge in _tasSkinEdges) SortSkin(edge, body, 4);
            }
            Color color = _grazeTicks > 0 && !_grazeKilled ? GraceColor : HurtboxColor;
            if (color != _tasSkinColor)
            {
                _tasSkinColor = color;
                if (_tasSkinOutline) foreach (SpriteRenderer edge in _tasSkinEdges) edge.color = color;
                else _tasSkinFill.color = color;
            }
            BoxCollider2D box = Hurtbox(player, _hitboxes);
            _tasSkin.gameObject.SetActive(box != null);
            if (box == null) return;
            Vector2 size = box.bounds.size;
            if (size != _tasSkinSize)
            {
                _tasSkinSize = size;
                LayOutTasSkin(size);
            }
            // Inertia: the jelly inside keeps going the way the body was, so a velocity change kicks it the other way.
            Vector2 velocity = _tasSkinBody != null ? _tasSkinBody.linearVelocity : Vector2.zero;
            Vector2 change = velocity - _tasSkinLastVelocity;
            _tasSkinLastVelocity = velocity;
            if (change.magnitude < SloshMaxKick) _tasSkinSloshSpeed -= change * SloshKick;
        }

        static void SortSkin(Renderer renderer, SpriteRenderer body, int rank)
        {
            renderer.sortingLayerID = body.sortingLayerID;
            renderer.sortingOrder = body.sortingOrder + rank;
        }

        void BuildTasSkin(Movement player)
        {
            BuildSkinSprites();
            var root = new GameObject("TAS skin");
            SceneManager.MoveGameObjectToScene(root, player.gameObject.scene);
            _tasSkin = root.transform;
            _tasSkinPlayer = player;
            _tasSkinBody = player.GetComponent<Rigidbody2D>();
            _tasSkinLastVelocity = _tasSkinBody != null ? _tasSkinBody.linearVelocity : Vector2.zero;
            _tasSkinSlosh = _tasSkinSloshSpeed = Vector2.zero;
            _tasSkinFill = LabBox(_tasSkin, Vector2.zero, Vector2.one, _tasSkinColor = HurtboxColor, _skinGradient);
            _tasSkinCore = LabBox(_tasSkin, Vector2.zero, Vector2.one, new Color(1f, 1f, 1f, 0.22f), _skinBlob);
            _tasSkinShine = LabBox(_tasSkin, Vector2.zero, Vector2.one, new Color(1f, 1f, 1f, 0.8f), _skinBlob);
            // The outline and the badge: the fill's purple, darker but still purple.
            Color.RGBToHSV(HurtboxColor, out float hue, out float saturation, out _);
            Color edge = Color.HSVToRGB(hue, Mathf.Min(1f, saturation + 0.15f), 0.6f);
            for (int i = 0; i < 4; i++) _tasSkinEdges[i] = LabBox(_tasSkin, Vector2.zero, Vector2.one, edge);
            _tasSkinBadge = LabBox(_tasSkin, Vector2.zero, Vector2.one, new Color(edge.r, edge.g, edge.b, 0.85f), _skinBadge);
            // Read bottom to top, as the overlay's badge (rotated -90° on screen).
            _tasSkinBadge.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            if (_tasSkinOutline)
            {
                // Only the outline, in the fill's colour, over the sprite.
                foreach (SpriteRenderer strip in _tasSkinEdges) strip.color = _tasSkinColor;
                foreach (SpriteRenderer part in new[] { _tasSkinFill, _tasSkinCore, _tasSkinShine, _tasSkinBadge }) part.gameObject.SetActive(false);
            }
            _tasSkinSize = Vector2.zero;
        }

        /// <summary>
        /// The skin's two textures, made once, each a 1-unit sprite (64 px at 64 px a unit) scaled to size: a vertical gel
        /// gradient (white-tinted at the top, darker at the bottom, multiplied by the fill colour) and a soft round blob.
        /// </summary>
        static void BuildSkinSprites()
        {
            if (_skinGradient != null) return;
            const int n = 64;
            var gradient = new Texture2D(n, n) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var blob = new Texture2D(n, n) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
            {
                float t = y / (n - 1f);
                Color row = Color.Lerp(new Color(0.82f, 0.82f, 0.82f, 1f), Color.white, Mathf.SmoothStep(0f, 1f, t));
                for (int x = 0; x < n; x++)
                {
                    gradient.SetPixel(x, y, row);
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = 1f - Mathf.SmoothStep(0.35f, 1f, Mathf.Sqrt(dx * dx + dy * dy));
                    blob.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            gradient.Apply();
            blob.Apply();
            _skinGradient = Sprite.Create(gradient, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            _skinBlob = Sprite.Create(blob, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            _skinBadge = BadgeSprite();
        }

        /// <summary>"TAS" in 5×7 cells, a column apart, top row first.</summary>
        static readonly string[] BadgeGlyphs =
        {
            "##### .###. .####",
            "..#.. #...# #....",
            "..#.. #...# #....",
            "..#.. ##### .###.",
            "..#.. #...# ....#",
            "..#.. #...# ....#",
            "..#.. #...# ####.",
        };

        /// <summary>
        /// The badge drawn into its own texture, 1 unit wide: a pixel font, not a TextMesh on the built-in dynamic font the
        /// overlay's IMGUI text shares (its "T" never showed).
        /// </summary>
        static Sprite BadgeSprite()
        {
            int w = BadgeGlyphs[0].Length, h = BadgeGlyphs.Length;
            var texture = new Texture2D(w, h) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int row = 0; row < h; row++)
                for (int x = 0; x < w; x++)
                    texture.SetPixel(x, h - 1 - row, BadgeGlyphs[row][x] == '#' ? Color.white : Color.clear);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), w);
        }

        /// <summary>The fill on the whole box and the outline's four strips inside its edge.</summary>
        void LayOutTasSkin(Vector2 size)
        {
            float hw = size.x / 2f, hh = size.y / 2f, e = SkinEdge / 2f;
            _tasSkinFill.transform.localScale = new Vector3(size.x, size.y, 1f);
            SetSkinPart(_tasSkinEdges[0], new Vector2(0f, hh - e), new Vector2(size.x, SkinEdge));
            SetSkinPart(_tasSkinEdges[1], new Vector2(0f, -hh + e), new Vector2(size.x, SkinEdge));
            SetSkinPart(_tasSkinEdges[2], new Vector2(-hw + e, 0f), new Vector2(SkinEdge, size.y));
            SetSkinPart(_tasSkinEdges[3], new Vector2(hw - e, 0f), new Vector2(SkinEdge, size.y));
            // As DrawHitboxBadge fits it: along the box at most 0.9 of its height, across it at most 0.8 of its width.
            float aspect = BadgeGlyphs.Length / (float)BadgeGlyphs[0].Length;
            float length = Mathf.Min(0.9f * size.y, 0.8f * size.x / aspect);
            _tasSkinBadge.transform.localScale = new Vector3(length, length, 1f);
        }

        static void SetSkinPart(SpriteRenderer part, Vector2 at, Vector2 size)
        {
            part.transform.localPosition = at;
            part.transform.localScale = new Vector3(size.x, size.y, 1f);
        }

        /// <summary>
        /// Every rendered frame, after Update and LateUpdate (the step-up included): the skin onto the hurtbox's physics bounds,
        /// where the overlay draws it, not the player's interpolated transform, which trails them by the frame's lag; then the
        /// slosh a frame on, by the frame's game time (none in a dash freeze or a pause, so the jelly freezes with the game).
        /// </summary>
        void PlaceTasSkin()
        {
            double start = PerfMs(_perfClock);
            PlaceTasSkinNow();
            _perfNow.skin += (float)(PerfMs(_perfClock) - start);
        }

        void PlaceTasSkinNow()
        {
            if (_tasSkin == null || _tasSkinPlayer == null || !_tasSkin.gameObject.activeSelf) return;
            BoxCollider2D box = Hurtbox(_tasSkinPlayer, _hitboxes);
            if (box == null) return;
            Vector3 c = box.bounds.center;
            _tasSkin.position = new Vector3(c.x, c.y, _tasSkinPlayer.transform.position.z);
            StepSlosh(Time.deltaTime);
            DrawSlosh();
        }

        /// <summary>A damped spring back to rest, in steps of at most 1/240 s so a slow frame stays stable.</summary>
        void StepSlosh(float dt)
        {
            float w = 2f * Mathf.PI * SloshHz, k = w * w, damping = 2f * SloshDamping * w;
            dt = Mathf.Min(dt, 0.1f);
            while (dt > 0f)
            {
                float h = Mathf.Min(dt, 1f / 240f);
                _tasSkinSloshSpeed += (-k * _tasSkinSlosh - damping * _tasSkinSloshSpeed) * h;
                _tasSkinSlosh += _tasSkinSloshSpeed * h;
                dt -= h;
            }
        }

        /// <summary>
        /// The core and the shine where the slosh puts them, each clamped inside the outline: the core shifts half as far and
        /// squashes against a vertical slosh (wider as it flattens), the shine rides the slosh from its rest at the upper left.
        /// </summary>
        void DrawSlosh()
        {
            float innerW = _tasSkinSize.x / 2f - SkinEdge, innerH = _tasSkinSize.y / 2f - SkinEdge;
            if (innerW <= 0f || innerH <= 0f) return;
            Vector2 s = _tasSkinSlosh;

            float squash = Mathf.Clamp(s.y / innerH, -0.15f, 0.15f);
            Vector2 core = new Vector2(innerW * 1.7f * (1f - squash * 0.8f), innerH * 1.7f * (1f + squash));
            core = Vector2.Min(core, new Vector2(innerW * 2f, innerH * 2f));
            Vector2 coreAt = ClampInside(s * 0.5f, core, innerW, innerH);
            SetSkinPart(_tasSkinCore, coreAt, core);

            Vector2 shine = new Vector2(_tasSkinSize.x * 0.32f, _tasSkinSize.y * 0.18f);
            shine = Vector2.Min(shine, new Vector2(innerW * 2f, innerH * 2f));
            Vector2 rest = new Vector2(-innerW * 0.4f, innerH * 0.55f);
            SetSkinPart(_tasSkinShine, ClampInside(rest + s, shine, innerW, innerH), shine);
        }

        static Vector2 ClampInside(Vector2 at, Vector2 size, float innerW, float innerH)
        {
            float x = Mathf.Max(0f, innerW - size.x / 2f), y = Mathf.Max(0f, innerH - size.y / 2f);
            return new Vector2(Mathf.Clamp(at.x, -x, x), Mathf.Clamp(at.y, -y, y));
        }
    }
}
