using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.InputSystem;
using RenaultRacer.Web;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The visible half of the touch controls (plan section 9). Presentation only - it
    /// reads <see cref="InputRouter"/> and never produces input, so the art can change
    /// without touching a line of control logic.
    ///
    /// Three jobs:
    ///
    /// 1. **Put the brake where the thumb already is**, drawn at exactly the rect
    ///    TouchDriveInput hit-tests (<see cref="TouchDriveInput.BrakeRect"/>, screen
    ///    pixels, converted through the canvas scale factor). Button and hit area are the
    ///    same rect by construction. Idle and pressed are two sprites prepared on the SAME
    ///    canvas with the SAME outer diameter (prepare_ui.py), swapped on the same Image,
    ///    so pressing never changes the button's geometry.
    /// 2. **Make the steering halves discoverable, without lighting them.** The steering
    ///    halves have NO drawn overlay: an earlier version tinted the whole held half of
    ///    the screen, and a half-screen glow over the road on every press is the thing a
    ///    racer least wants to see. A chevron hint sits in each half: visible during
    ///    the intro hint, faint otherwise, and swapped to its pressed sprite while that
    ///    half is held - every press confirms itself. The chevrons are decoration: they do
    ///    not define or change the steering zones, which are still "left/right of
    ///    steerSplit".
    /// 3. **Show the drift gesture.** Brake+steer held long enough to mean handbrake tints
    ///    the pressed brake, or the handbrake is invisible and feels like a bug.
    ///
    /// Zero per-frame allocation: everything is built once in Awake and only colours,
    /// sprite references and RectTransforms (on layout change) are written afterwards.
    /// </summary>
    public sealed class TouchControlsView : MonoBehaviour
    {
        // Idle must read on dark asphalt AND bright sky/grass on a phone, where the
        // chevron is only ~65 px wide; pressed switches to the lit sprite at full alpha,
        // so idle vs pressed differs in art AND opacity.
        public const float DefaultChevronRestAlpha = 0.5f;
        public const float DefaultChevronHintAlpha = 0.85f;
        public const float DefaultChevronPressedAlpha = 1f;

        [SerializeField] private InputRouter input;

        [Tooltip("Canvas to build the controls under. Defaults to the one in the parents.")]
        [SerializeField] private Canvas canvas;

        [Header("Steering hint")]
        [Tooltip("Seconds the chevrons stay lit at startup, to teach the scheme.")]
        public float introHintSeconds = 2.5f;

        [Header("Button art (Phase 10)")]
        [Tooltip("Brake alpha at rest. The idle art is a translucent charcoal disc already.")]
        [Range(0f, 1f)] public float brakeRestAlpha = 0.9f;

        [Tooltip("Tint on the PRESSED brake sprite once brake+steer means handbrake.")]
        public Color handbrakeArtTint = new Color(1f, 0.72f, 0.45f, 1f);

        [Tooltip("Chevron alpha while its half is held.")]
        [Range(0f, 1f)] public float chevronPressedAlpha = DefaultChevronPressedAlpha;

        [Tooltip("Chevron alpha during the intro hint.")]
        [Range(0f, 1f)] public float chevronHintAlpha = DefaultChevronHintAlpha;

        [Tooltip("Chevron alpha at rest while racing. Faint on purpose: the road matters more.")]
        [Range(0f, 1f)] public float chevronRestAlpha = DefaultChevronRestAlpha;

        [Tooltip("Chevron size in UI units.")]
        [Range(48f, 200f)] public float chevronSize = 112f;

        [Tooltip("Chevron centre in from the safe-area side edge, fraction of screen width.")]
        [Range(0f, 0.3f)] public float chevronInset = 0.09f;

        [Tooltip("Chevron centre height, fraction of screen height.")]
        [Range(0.2f, 0.8f)] public float chevronHeight = 0.55f;

        [Tooltip("How quickly a tint follows its target, per second. High = snappy.")]
        [Range(4f, 40f)] public float tintSpeed = 22f;

        // Plain-shape fallback colours, used only if the UI atlas is missing.
        private static readonly Color FallbackBrakeIdle   = new Color(0.86f, 0.24f, 0.20f, 0.42f);
        private static readonly Color FallbackBrakeActive = new Color(1f, 0.42f, 0.30f, 0.90f);
        private static readonly Color FallbackHandbrake   = new Color(1f, 0.78f, 0.20f, 0.95f);

        /// <summary>
        /// Whether the driving controls should be on screen at all. Set by GameUi from the
        /// game state: driving controls belong to the driving states.
        /// </summary>
        public bool ControlsVisible { get; set; }

        private Image _brake, _leftChevron, _rightChevron;
        private RectTransform _brakeRect, _leftChevronRect, _rightChevronRect;
        private Sprite _brakeIdle, _brakePressed, _leftIdle, _leftPressed, _rightIdle, _rightPressed;
        private bool _art;
        private Color _brakeColor, _leftChevronColor, _rightChevronColor;
        private float _introTimer;
        private Rect _lastBrakeScreenRect;
        private Rect _lastSafe;
        private int _lastW, _lastH;
        private float _lastScale;

        private void Awake()
        {
            if (input == null) input = FindAnyObjectByType<InputRouter>();
            if (canvas == null) canvas = GetComponentInParent<Canvas>();
            if (canvas == null) canvas = FindAnyObjectByType<Canvas>();
            if (canvas == null)
            {
                Debug.LogError("[TouchControlsView] no Canvas to build under.");
                enabled = false;
                return;
            }

            Build();
            _introTimer = introHintSeconds;
            ControlsVisible = true;
        }

        private void Build()
        {
            GameObject holder = new GameObject("TouchControls", typeof(RectTransform));
            holder.transform.SetParent(canvas.transform, false);
            RectTransform holderRect = (RectTransform)holder.transform;
            UiPrimitives.Fill(holderRect, 0f);
            // Behind every panel, so a HUD readout always draws over a control.
            holder.transform.SetAsFirstSibling();

            _brakeIdle = UiArt.Get("brake_idle");
            _brakePressed = UiArt.Get("brake_pressed");
            _leftIdle = UiArt.Get("steer_left_idle");
            _leftPressed = UiArt.Get("steer_left_pressed");
            _rightIdle = UiArt.Get("steer_right_idle");
            _rightPressed = UiArt.Get("steer_right_pressed");
            _art = _brakeIdle != null && _brakePressed != null && _leftIdle != null
                && _leftPressed != null && _rightIdle != null && _rightPressed != null;

            if (_art)
            {
                _leftChevron = UiPrimitives.Panel(holder.transform, "ChevronLeft", _leftIdle, Transparent(Color.white));
                _rightChevron = UiPrimitives.Panel(holder.transform, "ChevronRight", _rightIdle, Transparent(Color.white));
                _leftChevronRect = _leftChevron.rectTransform;
                _rightChevronRect = _rightChevron.rectTransform;
                _leftChevronRect.anchorMin = _leftChevronRect.anchorMax = Vector2.zero;
                _rightChevronRect.anchorMin = _rightChevronRect.anchorMax = Vector2.zero;
                _leftChevronRect.pivot = _rightChevronRect.pivot = new Vector2(0.5f, 0.5f);
            }

            _brake = UiPrimitives.Panel(holder.transform, "Brake",
                                        _art ? _brakeIdle : UiPrimitives.Disc,
                                        _art ? Color.white : FallbackBrakeIdle);
            _brakeRect = _brake.rectTransform;
            _brakeRect.anchorMin = _brakeRect.anchorMax = Vector2.zero;
            _brakeRect.pivot = new Vector2(0.5f, 0.5f);
            // The brake sprite is square and fills the rect, like the hit area.
            _brake.preserveAspect = false;

            _brakeColor = Transparent(_brake.color);
            _leftChevronColor = _rightChevronColor = Transparent(Color.white);
        }

        private static Color Transparent(Color c)
        {
            c.a = 0f;
            return c;
        }

        private void LateUpdate()
        {
            if (input == null || _brake == null) return;

            bool suspended = input.Suspended;
            TouchDriveInput touch = input.Touch;

            // Keep the drawn button on the rect that is actually tested, including after
            // a browser resize or an orientation flip.
            Rect r = touch.BrakeRect;
            float scale = canvas.scaleFactor;
            if (r != _lastBrakeScreenRect || scale != _lastScale)
            {
                _lastBrakeScreenRect = r;
                PlaceBrake(r);
            }
            PlaceChevronsIfNeeded(scale);
            _lastScale = scale;

            if (_introTimer > 0f) _introTimer -= Time.unscaledDeltaTime;
            bool hinting = _introTimer > 0f && !suspended;

            float steer = suspended ? 0f : input.SteerAxis;
            bool brake = !suspended && input.BrakeHeld;
            bool handbrake = !suspended && input.HandbrakeHeld;
            bool left = steer < 0f, right = steer > 0f;

            Color brakeTarget;
            if (_art)
            {
                // Same Image, same rect: only the sprite and the tint change.
                UiPrimitives.SetSprite(_brake, brake ? _brakePressed : _brakeIdle);
                brakeTarget = handbrake ? handbrakeArtTint : Color.white;
                brakeTarget.a = brake ? 1f : brakeRestAlpha;

                UiPrimitives.SetSprite(_leftChevron, left ? _leftPressed : _leftIdle);
                UiPrimitives.SetSprite(_rightChevron, right ? _rightPressed : _rightIdle);
            }
            else
            {
                brakeTarget = handbrake ? FallbackHandbrake : (brake ? FallbackBrakeActive : FallbackBrakeIdle);
            }
            if (suspended) brakeTarget.a *= 0.3f;

            float restChevron = hinting ? chevronHintAlpha : chevronRestAlpha;
            Color leftChevronTarget = new Color(1f, 1f, 1f, left ? chevronPressedAlpha : restChevron);
            Color rightChevronTarget = new Color(1f, 1f, 1f, right ? chevronPressedAlpha : restChevron);

            // Faded out rather than switched off, so the same tint lerp carries it and
            // nothing pops when a race starts.
            if (!ControlsVisible)
            {
                brakeTarget.a = 0f;
                leftChevronTarget.a = 0f;
                rightChevronTarget.a = 0f;
            }

            float k = 1f - Mathf.Exp(-tintSpeed * Time.unscaledDeltaTime);
            _brakeColor = Color.Lerp(_brakeColor, brakeTarget, k);

            _brake.color = _brakeColor;

            if (_art)
            {
                _leftChevronColor = Color.Lerp(_leftChevronColor, leftChevronTarget, k);
                _rightChevronColor = Color.Lerp(_rightChevronColor, rightChevronTarget, k);
                _leftChevron.color = _leftChevronColor;
                _rightChevron.color = _rightChevronColor;
            }
        }

        /// <summary>
        /// Convert the screen-pixel hit rect into canvas space. Going through the canvas
        /// scale factor rather than re-deriving the layout in UI units is what keeps the
        /// graphic and the hit test from ever drifting apart.
        /// </summary>
        private void PlaceBrake(Rect screenRect)
        {
            float scale = canvas.scaleFactor;
            if (scale <= 0f) scale = 1f;

            _brakeRect.sizeDelta = new Vector2(screenRect.width / scale, screenRect.height / scale);
            _brakeRect.anchoredPosition = new Vector2(screenRect.center.x / scale,
                                                      screenRect.center.y / scale);
        }

        /// <summary>
        /// Chevrons sit in from the SAFE-area side edges at mid height, clear of the
        /// brake (bottom-left), minimap (top-right) and speed panel (bottom-right).
        /// Recomputed only when the screen, the safe area or the canvas scale changes.
        /// </summary>
        private void PlaceChevronsIfNeeded(float scale)
        {
            if (!_art) return;
            Rect safe = WebSafeArea.Get();
            int w = Screen.width, h = Screen.height;
            if (w == _lastW && h == _lastH && safe == _lastSafe && scale == _lastScale) return;
            _lastW = w;
            _lastH = h;
            _lastSafe = safe;

            if (scale <= 0f) scale = 1f;
            Vector2 lc, rc;
            TouchLayout.ChevronCentres(w, h, safe, chevronInset, chevronHeight, out lc, out rc);
            Vector2 size = new Vector2(chevronSize, chevronSize);

            _leftChevronRect.sizeDelta = size;
            _rightChevronRect.sizeDelta = size;
            _leftChevronRect.anchoredPosition = lc / scale;
            _rightChevronRect.anchoredPosition = rc / scale;
        }

        /// <summary>Re-show the intro hint, e.g. on restart.</summary>
        public void ShowHint()
        {
            _introTimer = introHintSeconds;
        }
    }
}
