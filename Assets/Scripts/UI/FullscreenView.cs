using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.Web;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The fullscreen button: top of the right-edge column (fullscreen, sound, pause), same
    /// disc, size and opacity as the others. Self-contained: GameUi only calls <see cref="Create"/>.
    ///
    /// Unity does NOT toggle fullscreen. The Fullscreen API needs a user activation, which
    /// touch browsers give on pointerup/click and not on touchstart (where Unity sees the
    /// press). So this view draws the button, shows the REAL fullscreen state, and publishes
    /// its screen rect to the page (<see cref="WebViewport.PublishFullscreenRect"/>); the
    /// page's own pointerup listener does the toggle. Pressing it never touches GameDirector.
    ///
    /// Not built where the browser cannot do fullscreen (iPhone Safari): <see cref="Present"/>
    /// is false and Sound/Pause take the top of the column.
    ///
    /// Icon: four corner brackets drawn from UI rectangles. Pointing OUT = "go fullscreen",
    /// pointing IN = "leave". It follows only the browser's state, polled every 0.2 s, so a
    /// refused request never shows as active and Esc flips it back.
    /// </summary>
    public sealed class FullscreenView : MonoBehaviour
    {
        /// <summary>Test/capture hook: -1 ask the browser, 0 force absent, 1 force present.</summary>
        public static int Override = -1;

        private static int _supportedCache = -1;

        /// <summary>True when the button exists (and the column is three high).</summary>
        public static bool Present
        {
            get
            {
                if (Override >= 0) return Override == 1;
                if (_supportedCache < 0) _supportedCache = WebViewport.FullscreenSupported ? 1 : 0;
                return _supportedCache == 1;
            }
        }

        public static Vector2 ButtonSize { get { return SoundToggleView.ButtonSize; } }

        /// <summary>Top of the column, same right edge.</summary>
        public static Vector2 ButtonOffset { get { return new Vector2(-HudView.EdgeMargin, -HudView.EdgeMargin); } }

        /// <summary>Unity order (left, top, right, bottom), negative grows, screen px. The bottom stops half the gap short of the sound button.</summary>
        public static Vector4 HitMargin(float canvasScale)
        {
            return new Vector4(-6f, -14f, -14f, -HudControls.HalfGapPx(canvasScale));
        }

        private const float PollSeconds = 0.2f;

        private static FullscreenView _instance;

        private RectTransform _rect;
        private Canvas _canvas;
        private readonly RectTransform[] _bars = new RectTransform[8];
        private readonly Vector3[] _corners = new Vector3[4];
        private bool _fullscreen;
        private bool _shapeSet;
        private float _nextPoll;
        private Rect _published;
        private bool _publishedOnce;
        private int _lastW, _lastH;

        public bool ShownFullscreen { get { return _fullscreen; } }
        public RectTransform ButtonRect { get { return _rect; } }

        public static FullscreenView Create(Transform host)
        {
            if (!Present) return null;
            GameObject go = new GameObject("FullscreenToggle", typeof(RectTransform));
            go.transform.SetParent(host, false);
            FullscreenView v = go.AddComponent<FullscreenView>();
            v.Build();
            return v;
        }

        private void Build()
        {
            _instance = this;
            _rect = (RectTransform)transform;
            _canvas = GetComponentInParent<Canvas>();
            UiPrimitives.Place(_rect, new Vector2(1f, 1f), new Vector2(1f, 1f), ButtonOffset, ButtonSize);

            Image bg = UiPrimitives.Panel(_rect, "Bg", UiPrimitives.Disc, new Color(0.067f, 0.075f, 0.09f, 0.62f));
            UiPrimitives.Fill(bg.rectTransform, 0f);
            bg.raycastTarget = false;

            for (int i = 0; i < _bars.Length; i++)
            {
                Image bar = UiPrimitives.Panel(_rect, "Bracket" + i, UiPrimitives.Box, Color.white);
                bar.raycastTarget = false;
                _bars[i] = bar.rectTransform;
            }
            SetShape(false);
        }

        private void OnDestroy() { if (_instance == this) _instance = null; }

        /// <summary>Lays the eight bars out as brackets pointing out (false) or in (true).</summary>
        private void SetShape(bool fullscreen)
        {
            const float arm = 8f, thick = 3f;
            float corner = fullscreen ? 5f : 12f;
            // Expand: the corner of each L is at the outer corner and its arms run inward.
            // Collapse: the corner is near the centre and its arms run outward.
            float dir = fullscreen ? 1f : -1f;
            int k = 0;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    Vector2 h = new Vector2(sx * (corner + dir * arm * 0.5f), sy * corner);
                    Vector2 v = new Vector2(sx * corner, sy * (corner + dir * arm * 0.5f));
                    UiPrimitives.Place(_bars[k++], new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), h, new Vector2(arm, thick));
                    UiPrimitives.Place(_bars[k++], new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), v, new Vector2(thick, arm));
                }
            _shapeSet = true;
        }

        /// <summary>Screen-pixel rect (y up) of the button grown by its hit margin.</summary>
        private Rect HitRect()
        {
            _rect.GetWorldCorners(_corners);
            Camera cam = (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? _canvas.worldCamera : null;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, _corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, _corners[2]);
            Vector4 m = HitMargin(_canvas != null ? _canvas.scaleFactor : 1f);
            return Rect.MinMaxRect(a.x + m.x, a.y + m.w, b.x - m.z, b.y - m.y);
        }

        /// <summary>True if a screen-space point is on the button (with a finger-sized margin).</summary>
        public static bool IsOverButton(Vector2 screenPoint)
        {
            FullscreenView v = _instance;
            if (v == null || v._rect == null || !v.isActiveAndEnabled) return false;
            Camera cam = (v._canvas != null && v._canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? v._canvas.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(v._rect, screenPoint, cam,
                       HitMargin(v._canvas != null ? v._canvas.scaleFactor : 1f));
        }

        private void Update()
        {
            bool resized = Screen.width != _lastW || Screen.height != _lastH;
            if (!resized && Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollSeconds;
            _lastW = Screen.width; _lastH = Screen.height;

            bool fs = WebViewport.IsFullscreen;   // the real state, never what was asked for
            if (fs != _fullscreen || !_shapeSet) { _fullscreen = fs; SetShape(fs); }

            Rect hit = HitRect();
            float w = Mathf.Max(1f, Screen.width), h = Mathf.Max(1f, Screen.height);
            Rect n = new Rect(hit.xMin / w, 1f - hit.yMax / h, hit.width / w, hit.height / h);   // y from the top
            if (!_publishedOnce || !Approx(n, _published))
            {
                _published = n; _publishedOnce = true;
                WebViewport.PublishFullscreenRect(n, true);
            }
        }

        private static bool Approx(Rect a, Rect b)
        {
            return Mathf.Abs(a.x - b.x) < 1e-4f && Mathf.Abs(a.y - b.y) < 1e-4f
                && Mathf.Abs(a.width - b.width) < 1e-4f && Mathf.Abs(a.height - b.height) < 1e-4f;
        }

        private void OnDisable()
        {
            if (_publishedOnce) { WebViewport.PublishFullscreenRect(_published, false); _publishedOnce = false; }
        }
    }
}
