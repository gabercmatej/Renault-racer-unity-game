using UnityEngine;
using UnityEngine.UI;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The F1-style start lights (plan sections 12 and 14): three lamps light one per
    /// second, then all go green and the race starts.
    ///
    /// Phase 10 art, built the way the brief recommends: ONE stable housing (the all-off
    /// housing from ui-countdown-go-v1.png) with three independently switched lamp
    /// sprites laid exactly on its lenses, and the GO artwork as a separate image. The
    /// housing never changes, so nothing about the geometry can jump between states -
    /// only the three lamp sprites swap (off / red / green) on rects that never move.
    ///
    /// Sequence, driven by GameDirector.LampsLit exactly as before: off -> 1 red ->
    /// 2 red -> 3 red -> all green + GO. Timing is untouched; this is presentation only.
    ///
    /// Without the atlas it falls back to the Phase 7 look: a dark board with tinted discs.
    /// </summary>
    public sealed class CountdownView : MonoBehaviour
    {
        private const int LampCount = 3;

        private Image[] _lamps;
        private RectTransform[] _lampRects;
        private float[] _lampPop;          // per-lamp pop timer, seconds remaining
        private int[] _lampState;          // 0 off, 1 red, 2 green
        private Graphic _go;
        private RectTransform _goRect;
        private float _goAge = -1f;
        private int _lastLit = -1;
        private bool _lastGreen;

        private bool _art;
        private Sprite _off, _red, _green;

        /// <summary>How long a lamp's pop lasts, seconds (checklist 8.1).</summary>
        private const float PopTime = 0.26f;

        /// <summary>
        /// Pop size. Small with art: the lens sits inside a chrome ring, and a lens that
        /// swells far past its ring reads as misregistered rather than as a flash.
        /// </summary>
        private float PopAmount { get { return _art ? 0.12f : 0.38f; } }

        private static readonly Color LampOff   = new Color(0.16f, 0.16f, 0.18f, 0.85f);
        private static readonly Color LampRed   = new Color(0.92f, 0.16f, 0.14f, 1f);
        private static readonly Color LampGreen = new Color(0.20f, 0.90f, 0.35f, 1f);

        public void Build(Transform parent)
        {
            GameObject root = new GameObject("Countdown", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            UiPrimitives.Fill((RectTransform)root.transform, 0f);

            _off = UiArt.Get("countdown_lamp_off");
            _red = UiArt.Get("countdown_lamp_red");
            _green = UiArt.Get("countdown_lamp_green");
            _art = UiArt.Get("countdown_housing") != null && _off != null && _red != null && _green != null;

            Vector2 housingSize = _art ? UiArt.Size("countdown_housing", new Vector2(317f, 134f))
                                       : new Vector2(230f, 84f);

            Image board = UiPrimitives.Art(root.transform, "Housing", "countdown_housing", Color.white,
                                           UiPrimitives.Box, new Color(0f, 0f, 0f, 0.45f));
            RectTransform br = board.rectTransform;
            UiPrimitives.Place(br, new Vector2(0.5f, 0.70f), new Vector2(0.5f, 0.5f),
                               Vector2.zero, housingSize);

            _lamps = new Image[LampCount];
            _lampRects = new RectTransform[LampCount];
            _lampPop = new float[LampCount];
            _lampState = new int[LampCount];

            for (int i = 0; i < LampCount; i++)
            {
                Image lamp;
                if (_art)
                {
                    // Anchored on the measured lens centre (normalised in the housing),
                    // sized as a fraction of the housing width: registration holds at
                    // any housing size.
                    lamp = UiPrimitives.Panel(br, "Lamp" + i, _off, Color.white);
                    float d = UiArt.LampDiameter * housingSize.x;
                    UiPrimitives.Place(lamp.rectTransform, UiArt.LampCentre(i), new Vector2(0.5f, 0.5f),
                                       Vector2.zero, new Vector2(d, d));
                }
                else
                {
                    const float size = 56f;
                    const float gap = 16f;
                    float total = LampCount * size + (LampCount - 1) * gap;
                    float x = -total * 0.5f + size * 0.5f + i * (size + gap);
                    lamp = UiPrimitives.Panel(br, "Lamp" + i, UiPrimitives.Disc, LampOff);
                    UiPrimitives.Place(lamp.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                                       new Vector2(x, 0f), new Vector2(size, size));
                }
                _lamps[i] = lamp;
                _lampRects[i] = lamp.rectTransform;
            }

            // GO: the supplied artwork (it reads "GO!"), or a text fallback.
            Sprite goSprite = UiArt.Get("countdown_go");
            if (goSprite != null)
            {
                Image go = UiPrimitives.Panel(root.transform, "Go", goSprite, Color.white);
                UiPrimitives.Place(go.rectTransform, new Vector2(0.5f, 0.42f), new Vector2(0.5f, 0.5f),
                                   Vector2.zero, UiArt.Size("countdown_go", new Vector2(400f, 170f)));
                _go = go;
            }
            else
            {
                Text go = UiPrimitives.ReadableLabel(root.transform, "Go", UiCopy.Go, 64,
                                                     TextAnchor.MiddleCenter, LampGreen);
                UiPrimitives.Place(go.rectTransform, new Vector2(0.5f, 0.42f), new Vector2(0.5f, 0.5f),
                                   Vector2.zero, new Vector2(400f, 90f));
                _go = go;
            }
            _goRect = _go.rectTransform;
            _go.gameObject.SetActive(false);
        }

        /// <summary>
        /// The pop and the GO swell. Unscaled, so it still animates while the simulation
        /// is frozen for the countdown, and it costs nothing once every timer has run out.
        /// </summary>
        private void Update()
        {
            float dt = Time.unscaledDeltaTime;

            if (_lampPop != null)
            {
                float amount = PopAmount;
                for (int i = 0; i < _lampPop.Length; i++)
                {
                    if (_lampPop[i] <= 0f) continue;
                    _lampPop[i] -= dt;
                    float k = Mathf.Clamp01(_lampPop[i] / PopTime);
                    // Overshoot then settle: a lamp that punches reads as an EVENT.
                    float s = 1f + amount * k * k;
                    _lampRects[i].localScale = new Vector3(s, s, 1f);
                    if (_lampPop[i] <= 0f) _lampRects[i].localScale = Vector3.one;
                }
            }

            if (_goAge >= 0f && _goRect != null)
            {
                _goAge += dt;
                float k = Mathf.Clamp01(_goAge / 0.45f);
                // Ease-out with a slight overshoot, so GO lands rather than slides in.
                float e = 1f - (1f - k) * (1f - k);
                float s = Mathf.LerpUnclamped(0.6f, 1f, e) + 0.08f * Mathf.Sin(k * Mathf.PI);
                _goRect.localScale = new Vector3(s, s, 1f);
                if (k >= 1f)
                {
                    _goRect.localScale = Vector3.one;
                    _goAge = -1f;
                }
            }
        }

        /// <summary>
        /// <paramref name="lampsLit"/> is 0..3 during the count; <paramref name="green"/>
        /// is the moment the race actually starts. Written only on change.
        /// </summary>
        public void Tick(int lampsLit, bool green)
        {
            if (lampsLit == _lastLit && green == _lastGreen) return;
            _lastLit = lampsLit;
            _lastGreen = green;

            for (int i = 0; i < _lamps.Length; i++)
            {
                int state = green ? 2 : (i < lampsLit ? 1 : 0);
                int was = _lampState[i];
                _lampState[i] = state;

                if (_art)
                    UiPrimitives.SetSprite(_lamps[i], state == 2 ? _green : (state == 1 ? _red : _off));
                else
                    _lamps[i].color = state == 2 ? LampGreen : (state == 1 ? LampRed : LampOff);

                // Pop only on the frame a lamp actually lights or changes colour.
                if (state != 0 && state != was && _lampPop != null) _lampPop[i] = PopTime;
            }

            if (_go != null && _go.gameObject.activeSelf != green)
            {
                _go.gameObject.SetActive(green);
                if (green)
                {
                    _goAge = 0f;
                    _goRect.localScale = new Vector3(0.6f, 0.6f, 1f);
                }
            }
        }

        public void ResetDisplay()
        {
            _lastLit = -1;
            _lastGreen = true;
            _goAge = -1f;
            if (_goRect != null) _goRect.localScale = Vector3.one;
            if (_lampPop != null)
                for (int i = 0; i < _lampPop.Length; i++)
                {
                    _lampPop[i] = 0f;
                    _lampState[i] = 0;
                    _lampRects[i].localScale = Vector3.one;
                }
            Tick(0, false);
        }
    }
}
