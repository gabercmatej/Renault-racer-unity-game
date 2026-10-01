using UnityEngine;
using UnityEngine.UI;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The shared building blocks of the code-built UI: fonts, a few generated shapes,
    /// and helpers that create non-interactive Images and Texts.
    ///
    /// Phase 10 adds the real art (<see cref="UiArt"/>, one atlas) and the UI typeface.
    /// The generated shapes stay: they are the fallback when the atlas is missing, and
    /// SpeedLines and the scrims still use the plain box.
    ///
    /// The copy itself lives in <see cref="UiCopy"/>, in one place, because it is
    /// Slovenian and it is the client's call (M7) - these are only the means of drawing it.
    /// </summary>
    public static class UiPrimitives
    {
        private static Sprite _box;
        private static Sprite _disc;
        private static Sprite _ring;
        private static Font _font;
        private static Font _bodyFont;
        private static Font _builtin;

        // Palette tokens from the Phase 10 brief. Project tokens, not verified official
        // Renault specifications.
        public static readonly Color Yellow   = new Color(1f, 0.8f, 0f, 1f);                 // #FFCC00
        public static readonly Color Charcoal = new Color(17f / 255f, 19f / 255f, 23f / 255f, 1f); // #111317
        public static readonly Color White    = Color.white;                                  // #FFFFFF
        public static readonly Color Silver   = new Color(174f / 255f, 183f / 255f, 194f / 255f, 1f); // #AEB7C2

        /// <summary>Resources path of the heavy weight (titles, buttons, numbers).</summary>
        public const string BoldFontPath = "Fonts/RRMontserrat-ExtraBold";

        /// <summary>Resources path of the body weight (taglines, captions, units).</summary>
        public const string BodyFontPath = "Fonts/RRMontserrat-Medium";

        private static Font Builtin
        {
            get
            {
                if (_builtin == null) _builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return _builtin;
            }
        }

        /// <summary>
        /// The UI font: RR Montserrat ExtraBold, a subset of Montserrat (SIL OFL 1.1) whose
        /// default digits are its tabular figures, so a running clock never changes width
        /// (see Tools/ArtPrep/prepare_ui.py). Falls back to Unity's built-in font if the
        /// asset is missing, so a broken import is ugly rather than invisible.
        /// </summary>
        public static Font Font
        {
            get
            {
                if (_font == null) _font = Resources.Load<Font>(BoldFontPath);
                if (_font == null) _font = Builtin;
                return _font;
            }
        }

        /// <summary>The body weight, RR Montserrat Medium. Same fallback.</summary>
        public static Font BodyFont
        {
            get
            {
                if (_bodyFont == null) _bodyFont = Resources.Load<Font>(BodyFontPath);
                if (_bodyFont == null) _bodyFont = Builtin;
                return _bodyFont;
            }
        }

        /// <summary>A plain white square. Tint it; never scale the texture.</summary>
        public static Sprite Box
        {
            get
            {
                if (_box == null) _box = SolidSprite(8, 8);
                return _box;
            }
        }

        /// <summary>A filled, antialiased white circle - the brake button's body.</summary>
        public static Sprite Disc
        {
            get
            {
                if (_disc == null) _disc = CircleSprite(96, 0f);
                return _disc;
            }
        }

        /// <summary>An antialiased white ring - the brake button's outline.</summary>
        public static Sprite Ring
        {
            get
            {
                if (_ring == null) _ring = CircleSprite(96, 0.86f);
                return _ring;
            }
        }

        private static Sprite SolidSprite(int w, int h)
        {
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.name = "UiBox";
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color32[] px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px);
            tex.Apply();
            Sprite s = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f);
            s.hideFlags = HideFlags.HideAndDontSave;
            return s;
        }

        /// <summary>
        /// <paramref name="innerRadius"/> 0 gives a filled disc; a value near 1 gives a
        /// thin ring. Edges are antialiased across one texel so the button does not look
        /// like a jagged placeholder on a high-DPI phone.
        /// </summary>
        private static Sprite CircleSprite(int size, float innerRadius)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = innerRadius > 0f ? "UiRing" : "UiDisc";
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            Color32[] px = new Color32[size * size];
            float half = size * 0.5f;
            float outer = half - 1f;
            float inner = outer * innerRadius;
            float aa = 1.25f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - half;
                    float dy = y + 0.5f - half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    float a = Mathf.Clamp01((outer - d) / aa);
                    if (innerRadius > 0f) a = Mathf.Min(a, Mathf.Clamp01((d - inner) / aa));

                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            Sprite s = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            s.hideFlags = HideFlags.HideAndDontSave;
            return s;
        }

        /// <summary>Create a non-interactive UI Image under <paramref name="parent"/>.</summary>
        public static Image Panel(Transform parent, string name, Sprite sprite, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Image img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            // Nothing here is clicked: touch is read from raw screen coordinates by
            // TouchDriveInput, so the graphics must never intercept a press. It also
            // keeps the UI raycaster out of the per-frame cost entirely.
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Stretch a RectTransform to fill its parent, with an optional inset.</summary>
        public static void Fill(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        /// <summary>Anchor a RectTransform to a normalised sub-rect of its parent.</summary>
        public static void Anchor(RectTransform rect, float xMin, float yMin, float xMax, float yMax)
        {
            rect.anchorMin = new Vector2(xMin, yMin);
            rect.anchorMax = new Vector2(xMax, yMax);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// A non-interactive text label. Like <see cref="Panel"/>, it never takes a
        /// raycast: touch is read from raw screen coordinates, so a label drifting over
        /// a steering gutter must not swallow the press.
        /// </summary>
        public static Text Label(Transform parent, string name, string text, int size,
                                 TextAnchor align, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Text t = go.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        /// <summary>
        /// A label with a drop shadow behind it. The HUD sits over a road that is
        /// sometimes pale grass and sometimes dark tarmac, and a single flat colour is
        /// unreadable against one of them whichever is chosen.
        /// </summary>
        public static Text ReadableLabel(Transform parent, string name, string text, int size,
                                         TextAnchor align, Color color)
        {
            Text t = Label(parent, name, text, size, align, color);
            Shadow sh = t.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.75f);
            sh.effectDistance = new Vector2(2f, -2f);
            return t;
        }

        /// <summary>A label in the body weight. Same non-interactive rules as <see cref="Label"/>.</summary>
        public static Text BodyLabel(Transform parent, string name, string text, int size,
                                     TextAnchor align, Color color)
        {
            Text t = Label(parent, name, text, size, align, color);
            t.font = BodyFont;
            return t;
        }

        /// <summary>
        /// An Image showing atlas sprite <paramref name="spriteName"/>. Sprites with 9-slice
        /// borders are drawn Sliced, so they can be sized freely without stretching their
        /// corners. Without art, falls back to <paramref name="fallback"/> tinted
        /// <paramref name="fallbackColor"/>, so a missing atlas degrades to the plain shapes.
        /// </summary>
        public static Image Art(Transform parent, string name, string spriteName, Color color,
                                Sprite fallback, Color fallbackColor)
        {
            Sprite s = UiArt.Get(spriteName);
            Image img = Panel(parent, name, s != null ? s : fallback, s != null ? color : fallbackColor);
            if (s != null && s.border.sqrMagnitude > 0f)
            {
                img.type = Image.Type.Sliced;
                img.fillCenter = true;
            }
            return img;
        }

        /// <summary>
        /// Swap an Image's sprite only if it differs - a same-sprite assignment still
        /// dirties the mesh. Used for idle/pressed and lamp states, which never change
        /// the Image's rect.
        /// </summary>
        public static void SetSprite(Image img, Sprite sprite)
        {
            if (img != null && sprite != null && img.sprite != sprite) img.sprite = sprite;
        }

        /// <summary>Anchor a RectTransform to one point, sized in UI units.</summary>
        public static void Place(RectTransform rect, Vector2 anchor, Vector2 pivot,
                                 Vector2 offset, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = offset;
            rect.sizeDelta = size;
        }
    }
}
