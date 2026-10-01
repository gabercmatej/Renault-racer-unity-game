using UnityEngine;
using UnityEngine.UI;

namespace RenaultRacer.UI
{
    /// <summary>
    /// Streaks that fly outward past the edges of the frame at pace (plan section 16,
    /// checklist 8.6).
    ///
    /// The cheapest "feels fast" trick after the FOV stretch, and it earns its place on
    /// a phone for a specific reason: the road renderer's sense of speed comes almost
    /// entirely from the rumble strips scrolling, so on a long clean straight the car
    /// can be at top speed and look becalmed. These put the speed back at the edges of
    /// the frame, where peripheral vision reads motion, without touching the middle of
    /// the screen where the player is actually looking.
    ///
    /// WHY THIS WAS REWRITTEN. The first version could not be seen in the running game
    /// at all, for three reasons that all had to be fixed together:
    ///
    /// 1. THE ART. fx_atlas's streak was cut with the glow stripped off, leaving a
    ///    124x21 hairline. Stretched across a fifth of the screen it read as a scratch.
    ///    prepare_fx.py now keeps the halo and cuts it at 248x44.
    /// 2. THE SIZE AND THE ALPHA. Ten 120x12 streaks at a peak alpha of 0.38, over a
    ///    bright sky, is below the threshold of noticing. Fourteen streaks at 280x50 and
    ///    0.55 are not.
    /// 3. THEY DID NOT MOVE. Length and alpha followed speed, but each streak sat at a
    ///    fixed point on the frame, so the effect read as a vignette rather than as
    ///    motion. Each streak now runs its own phase: it appears inboard, flies OUTWARD
    ///    past the screen edge, stretching as it goes, and fades in and out over the
    ///    trip. The phase advances with speed, so faster genuinely looks faster.
    ///
    /// A fourth reason it was never SEEN: the tier table had them off on Weak, and the
    /// browser sessions that went looking for them had stepped down to Weak. They are on
    /// for every tier now - see QualityDirector.speedLinesOn.
    ///
    /// Built once, then only ever tinted, moved and scaled. Every streak shares one
    /// sprite and one material so uGUI batches the lot into a single draw call, and
    /// nothing here allocates after Build (plan 17.4, 17.5).
    /// </summary>
    public sealed class SpeedLines : MonoBehaviour
    {
        private const int Count = 14;

        [Tooltip("Fraction of top speed below which nothing is drawn. The HUD reads " +
                 "220 km/h at 1.0, so 0.5 is about 110 km/h: the streaks are faint " +
                 "there and unmistakable by 200. Still held well up the range on " +
                 "purpose - streaks that are always on stop meaning anything.")]
        [Range(0f, 0.9f)] public float threshold = 0.5f;

        [Tooltip("Peak alpha at top speed. This is the ONLY high-speed cue - the cyan " +
                 "crackle that used to sit on the car is gone - so it carries the whole " +
                 "effect and is drawn to be seen rather than merely sensed.")]
        [Range(0f, 1f)] public float maxAlpha = 0.55f;

        [Tooltip("Colour the streaks drift toward over the top of the speed range. A " +
                 "pale electric blue, at the edge of the frame and never over the car: " +
                 "the nod to an electric Renault that the middle of the screen is the " +
                 "wrong place for.")]
        public Color topSpeedTint = new Color(0.78f, 0.93f, 1f, 1f);

        [Tooltip("Fraction of top speed at which the tint is fully applied.")]
        [Range(0.6f, 1f)] public float tintFrom = 0.88f;

        [Tooltip("How quickly the streaks follow speed, per second. Slow enough that " +
                 "they swell and fade rather than flicker at the threshold.")]
        [Range(0.5f, 12f)] public float smoothing = 3.5f;

        [Tooltip("How far inboard of the screen edge a streak is born, reference pixels " +
                 "(the canvas is authored at 1280x720). It travels from here out past " +
                 "the edge. Keep it well inside a quarter of the width: the brief is " +
                 "marks at the LEFT AND RIGHT EDGES, never over the car.")]
        [Range(80f, 500f)] public float travel = 300f;

        [Tooltip("Trips past the edge per second, at the threshold and at top speed. " +
                 "The gap between the two is what makes faster look faster.")]
        [Range(0.2f, 6f)] public float rateSlow = 1.1f;
        [Range(0.2f, 12f)] public float rateFast = 3.6f;

        // Streak rect at scale 1. The sprite is 248x44, so this is its own aspect - a
        // streak is never stretched out of shape, only scaled along its length.
        private static readonly Vector2 StreakSize = new Vector2(280f, 50f);
        private const float LengthMin = 0.55f, LengthMax = 2.4f;

        private RectTransform[] _streaks;
        private Image[] _images;
        private float[] _reach;          // per-streak strength, 0..1
        private float[] _phase;          // per-streak position on its trip, 0..1
        private float[] _side;           // +1 left, -1 right
        private float[] _mirror;         // -1 for mirrored streak art
        private float _level;
        private bool _enabled = true;
        private bool _hasArt;
        private Color _tint = Color.white;

        public void Build(Transform parent)
        {
            GameObject root = new GameObject("SpeedLines", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            RectTransform rr = (RectTransform)root.transform;
            UiPrimitives.Fill(rr, 0f);
            // Behind everything else in the HUD: streaks belong at the edge of vision,
            // never over the numbers the player is trying to read. Done here rather than
            // by the caller because the caller holds the COMPONENT, which lives on the
            // HUD root - moving that transform would reorder the whole panel.
            rr.SetAsFirstSibling();

            _streaks = new RectTransform[Count];
            _images = new Image[Count];
            _reach = new float[Count];
            _phase = new float[Count];
            _side = new float[Count];
            _mirror = new float[Count];

            RenaultRacer.Effects.FxArt.Load();
            Sprite streak = RenaultRacer.Effects.FxArt.Streak;
            _hasArt = streak != null;
            Vector2 size = _hasArt ? StreakSize : new Vector2(200f, 4f);

            for (int i = 0; i < Count; i++)
            {
                // Alternate sides and stagger the height, so the pattern does not read
                // as a repeating fan. Deterministic, not random: it must look the same
                // on every run and on every device.
                bool left = (i % 2) == 0;
                float t = (i / 2) / Mathf.Max(1f, (Count / 2f) - 1f);
                float y = Mathf.Lerp(0.10f, 0.86f, t);
                _reach[i] = Mathf.Lerp(0.62f, 1f, ((i * 7) % 5) / 4f);
                _side[i] = left ? 1f : -1f;
                // Spread the starting phases so the streaks do not all fly past in
                // lockstep. Irrational-ish step, so no two share a phase.
                _phase[i] = (i * 0.618034f) % 1f;

                Image img = UiPrimitives.Panel(root.transform, "Streak" + i,
                                               _hasArt ? streak : UiPrimitives.Box,
                                               new Color(1f, 1f, 1f, 0f));
                RectTransform r = img.rectTransform;
                // The PIVOT is the streak's INBOARD end, so scaling along its length
                // grows it outward, toward the edge it is flying to, and never inward
                // over the car. Left and right both pivot at the rect's right edge; the
                // right-hand ones are then mirrored, which flips the rect to extend
                // rightward AND puts the artwork's bright point at the leading end.
                UiPrimitives.Place(r, new Vector2(left ? 0f : 1f, y), new Vector2(1f, 0.5f),
                                   Vector2.zero, size);
                if (_hasArt && !left) r.localScale = new Vector3(-1f, 1f, 1f);
                _mirror[i] = left ? 1f : -1f;
                // Angle them along the direction the road opens out from the horizon,
                // so they agree with the perspective instead of fighting it.
                float angle = (left ? 1f : -1f) * Mathf.Lerp(26f, -8f, t);
                r.localRotation = Quaternion.Euler(0f, 0f, angle);

                _streaks[i] = r;
                _images[i] = img;
                img.raycastTarget = false;
            }
        }

        /// <summary>
        /// Drive from the car's speed. Called once per frame while racing; costs one
        /// lerp plus a colour, a position and a scale per streak, and nothing at all
        /// below the threshold once the level has settled at zero.
        /// </summary>
        public void Tick(float speedPct, float dt)
        {
            if (_streaks == null || !_enabled) return;

            float target = Mathf.InverseLerp(threshold, 1f, Mathf.Clamp01(speedPct));
            float next = Mathf.Lerp(_level, target, 1f - Mathf.Exp(-smoothing * dt));

            // Settled at nothing: skip the whole loop rather than push identical values.
            if (next <= 0.0005f && _level <= 0.0005f) { _level = 0f; return; }
            _level = next;

            // One lerp for the whole set, not one per streak.
            float t = Mathf.InverseLerp(tintFrom, 1f, Mathf.Clamp01(speedPct));
            _tint.r = 1f + (topSpeedTint.r - 1f) * t;
            _tint.g = 1f + (topSpeedTint.g - 1f) * t;
            _tint.b = 1f + (topSpeedTint.b - 1f) * t;

            float step = Mathf.Lerp(rateSlow, rateFast, _level) * dt;

            for (int i = 0; i < _streaks.Length; i++)
            {
                float p = _phase[i] + step;
                if (p >= 1f) p -= (int)p;
                _phase[i] = p;

                // Fade in over the first fifth of the trip and out over the last third,
                // so a streak never pops into or out of existence mid-frame.
                float fade = Mathf.Min(p / 0.2f, (1f - p) / 0.34f);
                fade = Mathf.Clamp01(fade);
                _tint.a = maxAlpha * _level * _reach[i] * fade;
                _images[i].color = _tint;

                // Outward: born `travel` in from the edge, gone by the time it reaches
                // it. The pivot is the inboard end, so the rect always lies between the
                // pivot and the edge - a streak can leave the frame but never cross it.
                float d = travel * (1f - p);
                _streaks[i].anchoredPosition = new Vector2(_side[i] * d, 0f);

                float len = Mathf.Lerp(LengthMin, LengthMax, p) * (0.45f + 0.55f * _level);
                _streaks[i].localScale = new Vector3(_mirror[i] * len, 1f, 1f);
            }
        }

        /// <summary>
        /// Turn the whole effect off for a quality tier (plan 20). Switching it off
        /// clears the streaks first - leaving them frozen at whatever alpha they
        /// happened to hold would be worse than either state.
        /// </summary>
        public void SetEnabled(bool on)
        {
            if (_enabled == on) return;
            _enabled = on;
            if (!on) ResetDisplay();
        }

        public void ResetDisplay()
        {
            _level = 0f;
            if (_images == null) return;
            _tint = Color.white;
            _tint.a = 0f;
            for (int i = 0; i < _images.Length; i++)
            {
                _images[i].color = _tint;
                _streaks[i].anchoredPosition = new Vector2(_side[i] * travel, 0f);
                _streaks[i].localScale = new Vector3(_mirror[i] * LengthMin, 1f, 1f);
            }
        }
    }
}
