using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.InputSystem;
using RenaultRacer.Vehicle;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The landscape gate (plan section 14, ROTATE_GATE).
    ///
    /// The game is landscape-only. On mobile web we cannot force an orientation - the
    /// Screen Orientation API is unavailable or ignored in Safari and inside in-app
    /// browsers - so the honest answer is to detect portrait, say so, and stop the world
    /// until the player turns the phone.
    ///
    /// "Stop the world" has to mean it: the gate pauses the simulation itself rather than
    /// just hiding the screen. The lap clock advances from simulated steps, so pausing the
    /// sim pauses the clock for free - and no Time.timeScale is involved, which would also
    /// stop the unscaled animation on this very panel.
    ///
    /// Phase 10 presentation, after screen-rotate-phone-v1.png: opaque charcoal, yellow
    /// corner brackets, a phone outline that turns portrait -> landscape on a loop with the
    /// yellow turn arrow beside it, and Slovenian copy (UiCopy.Rotate*). The picture still carries the message without the words.
    ///
    /// Detection, debounce, pause and input suspension are unchanged.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public sealed class RotateGate : MonoBehaviour
    {
        [SerializeField] private InputRouter input;
        [SerializeField] private CarController car;
        [SerializeField] private Canvas canvas;

        [Header("Detection")]
        [Tooltip("Aspect below which the screen counts as portrait. Slightly under 1 so " +
                 "a near-square window, and the moment mid-rotation, do not flicker the gate.")]
        [Range(0.6f, 1.2f)] public float portraitAspectThreshold = 0.95f;

        [Tooltip("Seconds the screen must stay portrait before the gate appears. Absorbs " +
                 "the transient resize a browser reports while the device is turning.")]
        [Range(0f, 1f)] public float debounceSeconds = 0.25f;

        [Header("Appearance")]
        [Tooltip("Seconds for one portrait-to-landscape turn of the icon.")]
        public float turnSeconds = 1.6f;

        /// <summary>
        /// The content is laid out in this box (UI units) and scaled to fit the canvas,
        /// whatever the CanvasScaler did to a portrait screen it was authored for in
        /// landscape.
        /// </summary>
        private static readonly Vector2 ContentSize = new Vector2(620f, 760f);

        private RenaultRacer.Core.GameDirector director;
        private GameObject _root;
        private RectTransform _rootRect, _content, _phone;
        private float _portraitTimer;
        private float _animTime;
        private Vector2 _lastRootSize;

        /// <summary>True while the gate is up and the game is held.</summary>
        public bool IsGated { get; private set; }

        private void Awake()
        {
            if (input == null) input = FindAnyObjectByType<InputRouter>();
            if (car == null) car = FindAnyObjectByType<CarController>();
            if (canvas == null) canvas = GetComponentInParent<Canvas>();
            if (canvas == null) canvas = FindAnyObjectByType<Canvas>();
            if (canvas == null)
            {
                Debug.LogError("[RotateGate] no Canvas to build under.");
                enabled = false;
                return;
            }
            Build();
            SetGated(false);
        }

        private void Build()
        {
            _root = new GameObject("RotateGatePanel", typeof(RectTransform));
            _root.transform.SetParent(canvas.transform, false);
            _rootRect = (RectTransform)_root.transform;
            UiPrimitives.Fill(_rootRect, 0f);
            // Last sibling: the gate is modal, so it draws over everything.
            _root.transform.SetAsLastSibling();

            // Opaque, like the reference: the game underneath is held anyway.
            Image bg = UiPrimitives.Panel(_root.transform, "Backdrop", UiPrimitives.Box, UiPrimitives.Charcoal);
            UiPrimitives.Fill(bg.rectTransform, 0f);

            // Corner brackets on the SCREEN corners, inset a little.
            AddBracket(new Vector2(0f, 1f), 1f, -1f);
            AddBracket(new Vector2(1f, 1f), -1f, -1f);
            AddBracket(new Vector2(0f, 0f), 1f, 1f);
            AddBracket(new Vector2(1f, 0f), -1f, 1f);

            GameObject content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(_root.transform, false);
            _content = (RectTransform)content.transform;
            UiPrimitives.Place(_content, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                               Vector2.zero, ContentSize);

            // Phone: the drawn outline sprite, or the old solid block without art.
            bool art = UiArt.Get("icon_phone") != null;
            Image phone = UiPrimitives.Art(_content, "Phone", "icon_phone", Color.white,
                                           UiPrimitives.Box, new Color(1f, 1f, 1f, 0.92f));
            _phone = phone.rectTransform;
            UiPrimitives.Place(_phone, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
                               new Vector2(-40f, -200f), art ? new Vector2(120f, 200f) : new Vector2(110f, 190f));

            if (UiArt.Get("icon_rotate_arrow") != null)
            {
                Image arrow = UiPrimitives.Art(_content, "Arrow", "icon_rotate_arrow", Color.white,
                                               UiPrimitives.Box, UiPrimitives.Yellow);
                UiPrimitives.Place(arrow.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
                                   new Vector2(150f, -110f), UiArt.Size("icon_rotate_arrow", new Vector2(100f, 83f)));
            }

            // Copy, universal Slovenian: a large yellow line, then one calm light line.
            Text title = UiPrimitives.Label(_content, "Title", UiCopy.RotatePhone, 54,
                                            TextAnchor.MiddleCenter, UiPrimitives.Yellow);
            title.horizontalOverflow = HorizontalWrapMode.Wrap;
            UiPrimitives.Place(title.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f),
                               new Vector2(0f, 300f), new Vector2(600f, 140f));

            Text body = UiPrimitives.BodyLabel(_content, "Body", UiCopy.RotateBody, 28,
                                               TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.78f));
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            UiPrimitives.Place(body.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f),
                               new Vector2(0f, 160f), new Vector2(560f, 110f));
        }

        private void AddBracket(Vector2 corner, float sx, float sy)
        {
            const float inset = 28f, length = 48f, thick = 7f;
            Color c = UiPrimitives.Yellow;

            Image h = UiPrimitives.Panel(_root.transform, "BracketH", UiPrimitives.Box, c);
            h.rectTransform.anchorMin = h.rectTransform.anchorMax = corner;
            h.rectTransform.pivot = new Vector2(sx > 0f ? 0f : 1f, sy > 0f ? 0f : 1f);
            h.rectTransform.anchoredPosition = new Vector2(sx * inset, sy * inset);
            h.rectTransform.sizeDelta = new Vector2(length, thick);

            Image v = UiPrimitives.Panel(_root.transform, "BracketV", UiPrimitives.Box, c);
            v.rectTransform.anchorMin = v.rectTransform.anchorMax = corner;
            v.rectTransform.pivot = h.rectTransform.pivot;
            v.rectTransform.anchoredPosition = new Vector2(sx * inset, sy * inset);
            v.rectTransform.sizeDelta = new Vector2(thick, length);
        }

#if UNITY_EDITOR
        /// <summary>Editor-only test hook (LifecycleProbe): pretend the screen is portrait. Compiled out of builds.</summary>
        public static bool ForcePortrait;
#endif

        private void Update()
        {
            bool portrait = Screen.height > 0
                         && (float)Screen.width / Screen.height < portraitAspectThreshold;
#if UNITY_EDITOR
            if (ForcePortrait) portrait = true;
#endif

            if (portrait) _portraitTimer += Time.unscaledDeltaTime;
            else _portraitTimer = 0f;

            bool shouldGate = _portraitTimer >= debounceSeconds;
            if (shouldGate != IsGated) SetGated(shouldGate);

            if (IsGated) Animate();
        }

        /// <summary>
        /// The icon turns portrait to landscape, holds, and repeats. Unscaled time, so it
        /// keeps moving no matter what the rest of the game is doing.
        /// </summary>
        private void Animate()
        {
            FitContent();

            _animTime += Time.unscaledDeltaTime;
            float period = turnSeconds * 2f;
            float t = (_animTime % period) / turnSeconds;

            // 0..1 turn, then a hold at landscape before snapping back.
            float k = t < 1f ? Mathf.SmoothStep(0f, 1f, t) : 1f;
            _phone.localRotation = Quaternion.Euler(0f, 0f, -90f * k);
        }

        /// <summary>
        /// Scale the content box to the canvas rect, so it fills a portrait phone however
        /// the landscape-authored CanvasScaler has scaled it. Only on a size change.
        /// </summary>
        private void FitContent()
        {
            Vector2 size = _rootRect.rect.size;
            if (size == _lastRootSize || size.x <= 0f || size.y <= 0f) return;
            _lastRootSize = size;
            float fit = Mathf.Min(size.x * 0.9f / ContentSize.x, size.y * 0.8f / ContentSize.y);
            fit = Mathf.Clamp(fit, 0.3f, 3f);
            _content.localScale = new Vector3(fit, fit, 1f);
        }

        private void SetGated(bool gated)
        {
            IsGated = gated;
            if (_root != null)
            {
                _root.SetActive(gated);
                // Modal: re-assert the top of the canvas on every show, in case anything
                // was parented to the canvas after this gate was built.
                if (gated) _root.transform.SetAsLastSibling();
            }
            // The director combines this with a user pause (held = user || gate), so the
            // gate closing can never clear the player's pause. Without a director (tests)
            // the gate holds the car and input directly.
            if (director == null) director = FindAnyObjectByType<RenaultRacer.Core.GameDirector>();
            if (director != null) director.SetGateHeld(gated);
            else
            {
                if (input != null) input.Suspend(gated);
                if (car != null) car.Paused = gated;
            }
            _animTime = 0f;
            _lastRootSize = Vector2.zero;

            Debug.Log("[RotateGate] " + (gated ? "PORTRAIT - game held" : "landscape - running")
                      + "  screen " + Screen.width + "x" + Screen.height);
        }
    }
}
