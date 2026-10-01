using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace RenaultRacer.Web
{
    /// <summary>
    /// The safe area, correct on mobile web (plan section 14).
    ///
    /// <see cref="Screen.safeArea"/> is reliable on native iOS and Android. On WebGL it
    /// is not implemented at all: it returns the whole canvas on every browser, so a
    /// notch, a Dynamic Island or an Android cutout is invisible to C#. On a landscape
    /// phone the cutout is at one END of the screen, which is exactly where the brake
    /// button and the lap time live - so trusting Screen.safeArea on web would put the
    /// one button the player must hit underneath the camera housing for half the
    /// audience, depending only on which way they turned the phone.
    ///
    /// The insets exist in CSS, as env(safe-area-inset-*), and only when the page asks
    /// for them with viewport-fit=cover. The ShellRacer template measures them and this
    /// reads them back through SafeArea.jslib.
    ///
    /// Everything degrades to "the whole screen is safe", which is the right answer on
    /// a desktop browser, in the Editor, on a phone without a cutout, and on any host
    /// page that is not our template.
    /// </summary>
    public static class WebSafeArea
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern float RRGetSafeInset(int side);
        [DllImport("__Internal")] private static extern int RRHasSafeArea();
#endif

        /// <summary>
        /// Editor-only override, so the layout can be checked against a notch without a
        /// device. Order is top, right, bottom, left, in pixels. Zero disables it.
        /// </summary>
        public static Vector4 EditorSimulatedInsets;

        /// <summary>True when real insets were measured by the page.</summary>
        public static bool IsMeasured
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return RRHasSafeArea() != 0;
#else
                return EditorSimulatedInsets != Vector4.zero;
#endif
            }
        }

        /// <summary>
        /// The usable screen rect, in the same pixel space as Screen.width/height.
        /// </summary>
        public static Rect Get()
        {
            float w = Screen.width;
            float h = Screen.height;
            if (w <= 0f || h <= 0f) return new Rect(0f, 0f, 1f, 1f);

            float top, right, bottom, left;

#if UNITY_WEBGL && !UNITY_EDITOR
            if (RRHasSafeArea() == 0) return new Rect(0f, 0f, w, h);
            top    = RRGetSafeInset(0);
            right  = RRGetSafeInset(1);
            bottom = RRGetSafeInset(2);
            left   = RRGetSafeInset(3);
#else
            if (EditorSimulatedInsets == Vector4.zero)
            {
                // Native platforms do implement this properly; web never reaches here.
                return Screen.safeArea;
            }
            top    = EditorSimulatedInsets.x;
            right  = EditorSimulatedInsets.y;
            bottom = EditorSimulatedInsets.z;
            left   = EditorSimulatedInsets.w;
#endif

            // A browser mid-rotation can report nonsense for a frame. Never let that
            // collapse the playable area - a bad frame here would move the brake button.
            float maxInsetX = w * 0.35f;
            float maxInsetY = h * 0.35f;
            left   = Mathf.Clamp(left,   0f, maxInsetX);
            right  = Mathf.Clamp(right,  0f, maxInsetX);
            top    = Mathf.Clamp(top,    0f, maxInsetY);
            bottom = Mathf.Clamp(bottom, 0f, maxInsetY);

            // Unity screen space has y UP; CSS insets have y DOWN, so top and bottom swap.
            return new Rect(left, bottom,
                            w - left - right,
                            h - top - bottom);
        }
    }
}
