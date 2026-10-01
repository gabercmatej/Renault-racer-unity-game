using UnityEngine;
using RenaultRacer.Web;

namespace RenaultRacer.UI
{
    /// <summary>
    /// Insets a RectTransform to <see cref="Screen.safeArea"/> (plan section 14).
    ///
    /// On a landscape phone the unsafe region is at the SIDES, not the top: the
    /// Dynamic Island / notch eats one end and the home indicator the other, and which
    /// end depends on which way the player turned the phone. Anything anchored to a
    /// screen corner - the brake button, the lap time, the minimap - has to live
    /// inside this rect or it will be under a cutout for half the audience.
    ///
    /// Recomputed only when the safe area or the resolution actually changes, so it
    /// costs one comparison a frame. That matters on mobile web, where the safe area
    /// changes whenever the browser's URL bar slides away.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class SafeAreaFitter : MonoBehaviour
    {
        [Tooltip("Apply the horizontal inset. The usual cause of a clipped landscape UI.")]
        public bool applyX = true;

        [Tooltip("Apply the vertical inset.")]
        public bool applyY = true;

        [Tooltip("Log the applied rect once per change. Useful on a real device, where " +
                 "there is no Inspector to look at.")]
        public bool logChanges = true;

        private RectTransform _rect;
        private Rect _lastSafeArea = new Rect(0f, 0f, 0f, 0f);
        private int _lastWidth, _lastHeight;

        /// <summary>The inset last applied, in normalised anchor units. Debug/HUD.</summary>
        public Vector4 LastInset { get; private set; }

        private void Awake()
        {
            _rect = GetComponent<RectTransform>();
            Apply();
        }

        private void Update()
        {
            if (WebSafeArea.Get() == _lastSafeArea
                && Screen.width == _lastWidth
                && Screen.height == _lastHeight)
                return;
            Apply();
        }

        private void Apply()
        {
            if (_rect == null) _rect = GetComponent<RectTransform>();

            // WebSafeArea, not Screen.safeArea: the latter is not implemented on
            // WebGL and always reports the whole canvas (see WebSafeArea).
            Rect safe = WebSafeArea.Get();
            int w = Screen.width, h = Screen.height;
            if (w <= 0 || h <= 0) return;

            _lastSafeArea = safe;
            _lastWidth = w;
            _lastHeight = h;

            Vector2 min = new Vector2(safe.xMin / w, safe.yMin / h);
            Vector2 max = new Vector2(safe.xMax / w, safe.yMax / h);

            if (!applyX) { min.x = 0f; max.x = 1f; }
            if (!applyY) { min.y = 0f; max.y = 1f; }

            // A browser that reports a degenerate safe area mid-resize must not be
            // allowed to collapse the UI to nothing.
            if (max.x - min.x < 0.2f || max.y - min.y < 0.2f)
            {
                min = Vector2.zero;
                max = Vector2.one;
            }

            _rect.anchorMin = min;
            _rect.anchorMax = max;
            _rect.offsetMin = Vector2.zero;
            _rect.offsetMax = Vector2.zero;

            LastInset = new Vector4(min.x, min.y, 1f - max.x, 1f - max.y);

            if (logChanges)
            {
                Debug.Log("[SafeAreaFitter] screen " + w + "x" + h
                          + "  safe " + safe
                          + "  anchors " + min + " .. " + max);
            }
        }
    }
}
