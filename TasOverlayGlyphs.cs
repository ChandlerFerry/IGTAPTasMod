using System.Collections.Generic;
using UnityEngine;

namespace IGTAPTasMod
{
    // Overlay text from a glyph atlas. IMGUI (Unity 6's TextCore) generates every new string it draws, ~2 KB of garbage and
    // ~15 µs each (GET /api/perf), and the overlay draws new numbers every tick and every frame. Each font's glyphs are drawn
    // once by IMGUI into a texture; a label is then one GL quad batch, with no text generation. What the atlas can't draw (a
    // glyph outside it, rich text, wrapping, an upper or lower alignment, a clipped label too wide, a rotated one) goes to
    // GUI.Label.
    public sealed partial class TasPlugin
    {
        const string GlyphExtras = "·→←↑↓—×–✗✓−▲▼●…°↗↘↙↖≈≤";
        static readonly int GlyphCount = 95 + GlyphExtras.Length;
        const int GlyphMargin = 3;

        sealed class GlyphAtlas
        {
            public Texture2D texture;
            public float lineHeight;
            public float[] advance;
            // Each glyph's cell in the atlas (pixels, top-left origin) and its texture coordinates.
            public Rect[] cell, uv;
        }

        static readonly Dictionary<long, GlyphAtlas> GlyphAtlases = new Dictionary<long, GlyphAtlas>();
        static readonly string[] GlyphStrings = BuildGlyphStrings();
        static readonly GUIContent GlyphMeasure = new GUIContent();
        static Material _glyphMaterial;
        static bool _glyphMaterialMissing;

        static string[] BuildGlyphStrings()
        {
            var strings = new string[GlyphCount];
            for (int i = 0; i < 95; i++) strings[i] = ((char)(32 + i)).ToString();
            for (int i = 0; i < GlyphExtras.Length; i++) strings[95 + i] = GlyphExtras[i].ToString();
            return strings;
        }

        static int GlyphIndex(char c)
        {
            if (c >= 32 && c <= 126) return c - 32;
            int extra = GlyphExtras.IndexOf(c);
            return extra < 0 ? -1 : 95 + extra;
        }

        static Material GlyphMaterial()
        {
            if (_glyphMaterial != null || _glyphMaterialMissing) return _glyphMaterial;
            Shader shader = Shader.Find("GUI/Text Shader");
            if (shader == null) { _glyphMaterialMissing = true; return null; }
            return _glyphMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        static GlyphAtlas Atlas(GUIStyle style)
        {
            Font font = style.font != null ? style.font : GUI.skin.font;
            long key = (long)(font != null ? font.GetInstanceID() : 0) << 32 | (long)style.fontSize << 8 | (long)style.fontStyle;
            if (GlyphAtlases.TryGetValue(key, out GlyphAtlas atlas)) return atlas;
            return GlyphAtlases[key] = BuildGlyphAtlas(style);
        }

        /// <summary>
        /// Draws each glyph once with IMGUI, white on black, into a screen-sized render texture (where IMGUI draws 1:1, as
        /// the panel cache does) and keeps the red channel, the glyph's coverage, as the atlas's alpha. Null if it doesn't fit.
        /// </summary>
        static GlyphAtlas BuildGlyphAtlas(GUIStyle style)
        {
            var draw = new GUIStyle(style) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(), clipping = TextClipping.Overflow, wordWrap = false, richText = false, contentOffset = Vector2.zero };
            draw.normal.background = null;
            draw.normal.textColor = Color.white;
            var atlas = new GlyphAtlas { advance = new float[GlyphCount], cell = new Rect[GlyphCount], uv = new Rect[GlyphCount] };
            GlyphMeasure.text = "xx";
            float pair = draw.CalcSize(GlyphMeasure).x;
            GlyphMeasure.text = "Xg";
            atlas.lineHeight = draw.CalcSize(GlyphMeasure).y;
            int cellHeight = Mathf.CeilToInt(atlas.lineHeight) + 2, x = 0, y = 0, width = 0;
            for (int i = 0; i < GlyphCount; i++)
            {
                // The advance between neighbours, without the style's padding or a space's trim.
                GlyphMeasure.text = "x" + GlyphStrings[i] + "x";
                atlas.advance[i] = draw.CalcSize(GlyphMeasure).x - pair;
                GlyphMeasure.text = GlyphStrings[i];
                int w = Mathf.CeilToInt(draw.CalcSize(GlyphMeasure).x) + 2 * GlyphMargin;
                if (x + w > Screen.width) { x = 0; y += cellHeight; }
                atlas.cell[i] = new Rect(x, y, w, cellHeight);
                x += w;
                width = Mathf.Max(width, x);
            }
            int height = y + cellHeight;
            if (height > Screen.height || width == 0) return null;
            RenderTexture target = RenderTexture.GetTemporary(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture active = RenderTexture.active;
            Color color = GUI.color, content = GUI.contentColor;
            try
            {
                RenderTexture.active = target;
                GL.Clear(false, true, Color.black);
                GUI.color = GUI.contentColor = Color.white;
                for (int i = 0; i < GlyphCount; i++)
                    GUI.Label(new Rect(atlas.cell[i].x + GlyphMargin, atlas.cell[i].y + 1, atlas.cell[i].width, atlas.lineHeight), GlyphStrings[i], draw);
                atlas.texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
                    { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                atlas.texture.ReadPixels(new Rect(0, Screen.height - height, width, height), 0, 0, false);
            }
            finally
            {
                RenderTexture.active = active;
                GUI.color = color;
                GUI.contentColor = content;
                RenderTexture.ReleaseTemporary(target);
            }
            Color32[] pixels = atlas.texture.GetPixels32();
            for (int k = 0; k < pixels.Length; k++) pixels[k] = new Color32(255, 255, 255, pixels[k].r);
            atlas.texture.SetPixels32(pixels);
            atlas.texture.Apply(false, true);
            for (int i = 0; i < GlyphCount; i++)
            {
                Rect c = atlas.cell[i];
                atlas.uv[i] = new Rect(c.x / width, 1f - c.yMax / height, c.width / width, c.height / height);
            }
            return atlas;
        }

        /// <summary>The text's width from the atlas, or -1 if the atlas can't draw it.</summary>
        static float GlyphWidth(string text, GUIStyle style)
        {
            if (string.IsNullOrEmpty(text) || style.wordWrap || style.richText && text.IndexOf('<') >= 0) return -1f;
            GlyphAtlas atlas = Atlas(style);
            if (atlas == null) return -1f;
            float width = 0f;
            for (int k = 0; k < text.Length; k++)
            {
                int i = GlyphIndex(text[k]);
                if (i < 0) return -1f;
                width += atlas.advance[i];
            }
            return width + style.padding.horizontal;
        }

        /// <summary>Draws a label as IMGUI would from the atlas; false if it can't, and nothing is drawn.</summary>
        static bool GlyphLabel(Rect r, string text, GUIStyle style, Color color)
        {
            // GL ignores GUI.matrix: a rotated label (the hitbox badge, a tech's name) goes to GUI.Label.
            if (!GUI.matrix.isIdentity) return false;
            TextAnchor align = style.alignment;
            if (align != TextAnchor.MiddleLeft && align != TextAnchor.MiddleCenter && align != TextAnchor.MiddleRight) return false;
            float width = GlyphWidth(text, style);
            Material material = width < 0f ? null : GlyphMaterial();
            if (material == null) return false;
            GlyphAtlas atlas = Atlas(style);
            RectOffset pad = style.padding;
            width -= pad.horizontal;
            float left = r.x + pad.left + style.contentOffset.x, right = r.xMax - pad.right + style.contentOffset.x;
            if (style.clipping == TextClipping.Clip && width > right - left + 0.5f) return false;
            float x = align == TextAnchor.MiddleLeft ? left : align == TextAnchor.MiddleRight ? right - width : (left + right - width) / 2f;
            float y = Mathf.Round(r.y + pad.top + style.contentOffset.y + (r.height - pad.vertical - atlas.lineHeight) / 2f) - 1f;
            material.mainTexture = atlas.texture;
            material.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, Screen.width, Screen.height, 0f);
            GL.Begin(GL.QUADS);
            // IMGUI's colours are gamma; GL.Color is taken as is, so in a linear project it is converted as IMGUI converts.
            Color tint = GUI.color * color * style.normal.textColor;
            GL.Color(QualitySettings.activeColorSpace == ColorSpace.Linear ? tint.linear : tint);
            for (int k = 0; k < text.Length; k++)
            {
                int i = GlyphIndex(text[k]);
                if (text[k] != ' ')
                {
                    Rect c = atlas.cell[i], uv = atlas.uv[i];
                    float px = Mathf.Round(x) - GlyphMargin;
                    GL.TexCoord2(uv.xMin, uv.yMax); GL.Vertex3(px, y, 0f);
                    GL.TexCoord2(uv.xMax, uv.yMax); GL.Vertex3(px + c.width, y, 0f);
                    GL.TexCoord2(uv.xMax, uv.yMin); GL.Vertex3(px + c.width, y + c.height, 0f);
                    GL.TexCoord2(uv.xMin, uv.yMin); GL.Vertex3(px, y + c.height, 0f);
                }
                x += atlas.advance[i];
            }
            GL.End();
            GL.PopMatrix();
            return true;
        }
    }
}
