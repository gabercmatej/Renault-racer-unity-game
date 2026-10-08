using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.Audio;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The speaker button. Self-contained: GameUi only calls <see cref="Create"/>.
    ///
    /// Rect (reference canvas units, inside the safe-area host): anchored TOP-RIGHT,
    /// 64 x 64, offset (-10, -10) - flush with the HUD margin at the safe area's right edge,
    /// below the fullscreen button (offset (-10, -84)) when that exists, otherwise top-aligned with the minimap, which stands to its LEFT (24 units clear). The pause
    /// button sits directly BELOW it in the same column. A hint line ("tap for sound" /
    /// "sound blocked") is right-aligned to the column, 240 x 22, hung below the pause button
    /// (and below the minimap where that is lower, on phones); with the fullscreen button
    /// present it sits left of the pause button instead (see HintPlacement). It is clear of the top-centre
    /// timer, the top-left logo and the bottom steering and brake zones.
    ///
    /// States (from <see cref="AudioDirector.Status"/>, which reads the REAL AudioContext):
    ///   On            white speaker with waves
    ///   Muted         grey speaker with a slash   (the player's choice, persisted)
    ///   WaitingForTap yellow pulsing speaker; after the race has started a hint says tap
    ///   Blocked       orange slashed speaker + hint (a tap after the context failed to run)
    ///
    /// Touch is polled raw, like the rest of the game (no raycast graphics). The tap that
    /// lands on the button is reported through <see cref="IsOverButton"/> so GameDirector
    /// does not also treat it as "start the race".
    /// </summary>
    public sealed class SoundToggleView : MonoBehaviour
    {
        public const string HintWaiting = "TAPNI ZA ZVOK";
        public const string HintBlocked = "ZVOK BLOKIRAN - TAPNI";

        public static readonly Vector2 ButtonSize = new Vector2(64f, 64f);
        /// <summary>
        /// Top-right corner of the safe area, flush with the HUD margin. The minimap is
        /// built to the left of this column (HudView.MinimapRightInset), so they cannot overlap.
        /// </summary>
        public static Vector2 ButtonOffset
        {
            get { return new Vector2(-HudView.EdgeMargin, -HudView.EdgeMargin - (FullscreenView.Present ? ButtonSize.y + PauseView.Gap : 0f)); }
        }

        /// <summary>
        /// Hit margin, Unity order (left, top, right, bottom), negative grows, screen px. Both
        /// vertical edges stop half the gap short of the neighbour (fullscreen above, pause
        /// below) so no two hit areas overlap; with no fullscreen button the top may grow.
        /// </summary>
        public static Vector4 HitMargin(float canvasScale)
        {
            float hg = HudControls.HalfGapPx(canvasScale);
            return new Vector4(-6f, FullscreenView.Present ? -hg : -14f, -14f, -hg);
        }

        /// <summary>
        /// Where the caption goes, as (right edge offset from the button column's right edge,
        /// top distance from the safe area's top), in UI units. Two buttons: right-aligned
        /// under the column (and under the minimap where that hangs lower). Three buttons: the
        /// column is tall enough to reach the right steer button on a phone, so the caption sits
        /// LEFT of the pause button, below the minimap, and never goes under the column.
        /// </summary>
        public static void HintPlacement(out float rightOffset, out float top)
        {
            float edge = HudView.EdgeMargin;
            if (FullscreenView.Present)
            {
                float pauseTop = edge + 2f * (ButtonSize.y + PauseView.Gap);
                rightOffset = -(ButtonSize.x + 8f);
                top = Mathf.Max(HudView.MinimapBottomInset() + 4f, pauseTop);
            }
            else
            {
                rightOffset = 0f;
                top = Mathf.Max(edge + 2f * ButtonSize.y + PauseView.Gap, HudView.MinimapBottomInset()) + 4f;
            }
        }

        private static SoundToggleView _instance;

        private AudioDirector _audio;
        private RectTransform _rect;
        private Canvas _canvas;
        private Image _bg;
        private Image _icon;
        private Text _hint;
        private float _lastTap = -1f;

        private static Sprite _spriteOn;
        private static Sprite _spriteOff;

        /// <summary>The Status shown last frame, for the probe.</summary>
        public SoundStatus Shown { get; private set; }

        public static SoundToggleView Create(Transform host, AudioDirector audio)
        {
            if (audio == null) return null;

            GameObject go = new GameObject("SoundToggle", typeof(RectTransform));
            go.transform.SetParent(host, false);
            go.transform.SetAsLastSibling();   // above the screen panels
            SoundToggleView v = go.AddComponent<SoundToggleView>();
            v.Build(audio);
            return v;
        }

        private void Build(AudioDirector audio)
        {
            _audio = audio;
            _instance = this;
            _rect = (RectTransform)transform;
            _canvas = GetComponentInParent<Canvas>();
            UiPrimitives.Place(_rect, new Vector2(1f, 1f), new Vector2(1f, 1f), ButtonOffset, ButtonSize);

            _bg = UiPrimitives.Panel(_rect, "Bg", UiPrimitives.Disc, new Color(0.067f, 0.075f, 0.09f, 0.62f));
            UiPrimitives.Fill(_bg.rectTransform, 0f);
            _bg.raycastTarget = false;

            EnsureSprites();
            _icon = UiPrimitives.Panel(_rect, "Icon", _spriteOn, Color.white);
            UiPrimitives.Fill(_icon.rectTransform, 8f);
            _icon.raycastTarget = false;

            _hint = UiPrimitives.BodyLabel(_rect, "Hint", "", 15, TextAnchor.MiddleRight, UiPrimitives.Yellow);
            float hintX, hintTop;
            HintPlacement(out hintX, out hintTop);
            UiPrimitives.Place(_hint.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                               new Vector2(hintX, -(hintTop + ButtonOffset.y)), new Vector2(240f, 22f));
            _hint.raycastTarget = false;
            _hint.enabled = false;
        }

        private void OnDestroy() { if (_instance == this) _instance = null; }

        /// <summary>True if a screen-space point is on the button (with a finger-sized margin).</summary>
        public static bool IsOverButton(Vector2 screenPoint)
        {
            SoundToggleView v = _instance;
            if (v == null || v._rect == null || !v.isActiveAndEnabled) return false;
            Camera cam = (v._canvas != null && v._canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                       ? v._canvas.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(v._rect, screenPoint, cam, HitMargin(v._canvas != null ? v._canvas.scaleFactor : 1f));
        }

        /// <summary>The tap rule, pure: which action a tap performs in a given status.</summary>
        public static bool TapTogglesMute(SoundStatus status, bool unlocked)
        {
            // Before the race starts the context is suspended by design, so the only
            // useful tap is mute. After it, Waiting/Blocked mean "retry"; the gesture
            // itself is what resumes the context (AudioUnlock.jslib).
            if (!unlocked) return true;
            return status == SoundStatus.On || status == SoundStatus.Muted;
        }

        /// <summary>Applies one tap. Returns true if it changed the mute setting.</summary>
        public static bool ApplyTap(AudioDirector audio)
        {
            if (audio == null) return false;
            if (TapTogglesMute(audio.Status, audio.Unlocked)) { audio.ToggleMuted(); return true; }
            AudioUnlockBridge.Resume();
            return false;
        }

        private void Update()
        {
            if (_audio == null) return;

            if (PressedThisFrame(out Vector2 pos) && IsOverButton(pos) && Time.unscaledTime - _lastTap > 0.25f)
            {
                _lastTap = Time.unscaledTime;
                ApplyTap(_audio);
            }

            SoundStatus st = _audio.Status;
            Shown = st;
            bool slashed = st == SoundStatus.Muted || st == SoundStatus.Blocked;
            _icon.sprite = slashed ? _spriteOff : _spriteOn;

            Color c;
            switch (st)
            {
                case SoundStatus.Muted: c = UiPrimitives.Silver; break;
                case SoundStatus.Blocked: c = new Color(1f, 0.45f, 0.1f, 1f); break;
                case SoundStatus.WaitingForTap:
                    c = Color.Lerp(UiPrimitives.Silver, UiPrimitives.Yellow,
                                   0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5f));
                    break;
                default: c = Color.white; break;
            }
            _icon.color = c;

            bool hint = _audio.Unlocked && (st == SoundStatus.WaitingForTap || st == SoundStatus.Blocked);
            if (_hint.enabled != hint) _hint.enabled = hint;
            if (hint)
            {
                string t = st == SoundStatus.Blocked ? HintBlocked : HintWaiting;
                if (_hint.text != t) _hint.text = t;
                _hint.color = st == SoundStatus.Blocked ? c : UiPrimitives.Yellow;
            }
        }

        internal static bool PressedThisFrame(out Vector2 pos)
        {
            // Touches first: with simulateMouseWithTouches a tap is ALSO a mouse press, and
            // counting both would toggle twice.
            int n = Input.touchCount;
            if (n > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    Touch t = Input.GetTouch(i);
                    if (t.phase == TouchPhase.Began) { pos = t.position; return true; }
                }
                pos = Vector2.zero;
                return false;
            }
            if (Input.GetMouseButtonDown(0)) { pos = Input.mousePosition; return true; }
            pos = Vector2.zero;
            return false;
        }

        // ------------------------------------------------------------- generated icon

        private static void EnsureSprites()
        {
            if (_spriteOn != null && _spriteOff != null) return;
            _spriteOn = MakeIcon(false);
            _spriteOff = MakeIcon(true);
        }

        /// <summary>64x64 white-on-transparent speaker, anti-aliased by distance. Generated
        /// once at boot: no texture in the download.</summary>
        private static Sprite MakeIcon(bool slashed)
        {
            const int S = 64;
            Texture2D tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            Color32[] px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float a = 0f;

                    // body: box 10..22 x 24..40 ; cone: 22..36 widening to 12..52
                    if (fx >= 10f && fx <= 22f && fy >= 24f && fy <= 40f) a = 1f;
                    else if (fx > 22f && fx <= 36f)
                    {
                        float k = (fx - 22f) / 14f;
                        float half = Mathf.Lerp(8f, 20f, k);
                        a = Mathf.Max(a, Cover(half - Mathf.Abs(fy - 32f)));
                    }

                    if (!slashed)
                    {
                        float dx = fx - 36f, dy = fy - 32f;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        if (dx > 2f && Mathf.Abs(dy) < dx * 1.1f)
                        {
                            float w1 = Cover(2f - Mathf.Abs(r - 13f));
                            float w2 = Cover(2f - Mathf.Abs(r - 21f));
                            a = Mathf.Max(a, Mathf.Max(w1, w2));
                        }
                    }
                    else
                    {
                        // slash from lower-left to upper-right, 4 px thick
                        float d = Mathf.Abs(fx - fy) / 1.4142f;
                        bool inRange = fx > 6f && fx < 58f;
                        if (inRange) a = Mathf.Max(a, Cover(2.2f - d));
                    }

                    byte b = (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f);
                    px[y * S + x] = new Color32(255, 255, 255, b);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
        }

        private static float Cover(float signedDistance) { return Mathf.Clamp01(signedDistance + 0.5f); }
    }
}

