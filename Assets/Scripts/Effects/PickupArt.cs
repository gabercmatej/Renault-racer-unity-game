using UnityEngine;

namespace RenaultRacer.Effects
{
    /// <summary>
    /// THE ONE PLACE THE PICKUP'S LOOK COMES FROM. Mechanic code asks for
    /// PickupArt.Lightning / PickupArt.Arcs and never learns whether it got real art or a
    /// fallback.
    ///
    /// The real art is prepared by Tools/ArtPrep/prepare_pickups.py from the "Lightning
    /// pickup pack" source folder and lives at Assets/Art/Resources/FX/pickup_lightning.png
    /// (the bolt, glow baked in) and FX/pickup_arcs.png (the collection overlay). Both are
    /// plain textures, as every atlas in this project is; the sprites are made here, once,
    /// Full Rect so the soft glow is never cut. Both are in ArtIntegrity.Manifest, so a
    /// production build without them fails rather than quietly drawing the placeholder.
    /// The placeholder bolt is still drawn here if the PNG is missing (Editor, or a half
    /// imported checkout); the arcs have no placeholder - no art, no burst.
    /// </summary>
    public static class PickupArt
    {
        public const string SpritePath = "FX/pickup_lightning";
        public const string ArcsPath = "FX/pickup_arcs";

        /// <summary>
        /// Height of the transparent glow below the bolt's foot, fraction of the texture
        /// (prepare_pickups.py prints it: 0.090). The sprite is pivoted HERE, at the foot
        /// of the bolt itself, so "hover above the road" means the bolt and not its halo.
        /// </summary>
        public const float BoltFoot = 0.09f;

        /// <summary>The bolt's opaque body as a fraction of the sprite's height (0.81).</summary>
        public const float BoltBodyHeight = 0.81f;

        /// <summary>
        /// The bolt's opaque body as a fraction of the sprite's WIDTH (alpha >= 128 spans
        /// 0.75 of the 170-px texture; the glow reaches 0.86). What "on the asphalt" and
        /// "looks like contact" are measured against.
        /// </summary>
        public const float BoltBodyWidth = 0.75f;

        /// <summary>
        /// Where the overlay's lower arc tips sit, fraction of its height from the bottom
        /// (the visible arcs end 0.055 up). The pivot goes slightly above that, so the tips
        /// reach just under the car's rear contact line - around the wheels and bumper.
        /// </summary>
        public const float ArcsPivotY = 0.10f;

        private static Sprite _lightning;
        private static Sprite _arcs;

        /// <summary>True when the generated placeholder is in use.</summary>
        public static bool IsPlaceholder { get; private set; }

        public static Sprite Lightning
        {
            get
            {
                if (_lightning != null) return _lightning;
                Sprite real = Resources.Load<Sprite>(SpritePath);
                if (real == null)
                {
                    Texture2D tex = Resources.Load<Texture2D>(SpritePath);
                    if (tex != null)
                    {
                        real = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                             new Vector2(0.5f, BoltFoot), 100f, 0, SpriteMeshType.FullRect);
                        real.name = "pickup_lightning";
                    }
                }
                IsPlaceholder = real == null;
                _lightning = real != null ? real : BuildPlaceholder();
                return _lightning;
            }
        }

        /// <summary>The electric-arc collection overlay, or null when the art is missing.</summary>
        public static Sprite Arcs
        {
            get
            {
                if (_arcs != null) return _arcs;   // Unity null: survives a destroyed sprite
                Texture2D tex = Resources.Load<Texture2D>(ArcsPath);
                if (tex != null)
                {
                    _arcs = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                          new Vector2(0.5f, ArcsPivotY), 100f, 0, SpriteMeshType.FullRect);
                    _arcs.name = "pickup_arcs";
                }
                return _arcs;
            }
        }

        // A bolt, normalised 0..1 with y up. A simple (non-self-intersecting) polygon.
        private static readonly Vector2[] Bolt =
        {
            new Vector2(0.55f, 1.00f), new Vector2(0.15f, 0.45f), new Vector2(0.45f, 0.45f),
            new Vector2(0.30f, 0.00f), new Vector2(0.85f, 0.60f), new Vector2(0.55f, 0.60f),
            new Vector2(0.80f, 1.00f),
        };

        /// <summary>
        /// Electric-yellow bolt with a white-hot core, a thick dark outline and a cyan halo,
        /// 96x144, built once (54 KB). High contrast on grey tarmac, green grass and blue
        /// sky alike - the outline is what keeps it readable against all three.
        /// </summary>
        private static Sprite BuildPlaceholder()
        {
            const int W = 96, H = 144, Pad = 12;
            float sx = W - 2f * Pad, sy = H - 2f * Pad;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.name = "pickup_lightning (placeholder)";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Color32 fill = new Color32(255, 214, 32, 255);
            Color32 core = new Color32(255, 250, 200, 255);
            Color32 edge = new Color32(24, 22, 40, 255);
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float d = SignedDistance(x + 0.5f - Pad, y + 0.5f - Pad, sx, sy);
                    Color32 c;
                    if (d <= -9f) c = core;
                    else if (d <= -4.5f) c = fill;
                    else if (d <= 0.5f) c = edge;
                    else c = new Color32(110, 225, 255, (byte)(Mathf.Clamp01(1f - d / Pad) * 170f));
                    px[y * W + x] = c;
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);   // no longer readable: halves its memory

            Sprite s = Sprite.Create(tex, new Rect(0f, 0f, W, H), new Vector2(0.5f, (float)Pad / H),
                                     100f, 0, SpriteMeshType.FullRect);
            s.name = "pickup_lightning (placeholder)";
            return s;
        }

        /// <summary>Pixels to the bolt's outline; negative inside (even-odd test).</summary>
        private static float SignedDistance(float px, float py, float sx, float sy)
        {
            float best = float.MaxValue;
            bool inside = false;
            for (int i = 0, j = Bolt.Length - 1; i < Bolt.Length; j = i++)
            {
                float ax = Bolt[j].x * sx, ay = Bolt[j].y * sy;
                float bx = Bolt[i].x * sx, by = Bolt[i].y * sy;
                if ((ay > py) != (by > py) && px < (bx - ax) * (py - ay) / (by - ay) + ax) inside = !inside;
                float ex = bx - ax, ey = by - ay;
                float t = Mathf.Clamp01(((px - ax) * ex + (py - ay) * ey) / (ex * ex + ey * ey));
                float dx = px - (ax + ex * t), dy = py - (ay + ey * t);
                best = Mathf.Min(best, dx * dx + dy * dy);
            }
            float dist = Mathf.Sqrt(best);
            return inside ? -dist : dist;
        }
    }
}
