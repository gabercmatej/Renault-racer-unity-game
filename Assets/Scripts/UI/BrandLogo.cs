using UnityEngine;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The brand logo. ONE source file drives every place it appears:
    /// <c>Assets/Art/Resources/UI/brand_logo.png</c> (transparent PNG, imported as a Sprite).
    /// The HUD loads it from here; the web loader (WebGLTemplates/ShellRacer/index.html,
    /// #bootEmblem) gets the same file inlined by <c>BrandLogoSync</c> (menu
    /// Renault Racer > Brand > Sync logo into index.html). To swap the logo: overwrite
    /// that PNG, run the sync, done.
    ///
    /// The file is the RENAULT RACER wordmark (about 3.5:1, white letters with a black
    /// outline). Overwrite the PNG and run the sync to change it everywhere.
    /// </summary>
    public static class BrandLogo
    {
        public const string ResourcePath = "UI/brand_logo";
        private static Sprite _sprite;
        private static bool _tried;

        public static Sprite Sprite
        {
            get
            {
                if (!_tried) { _tried = true; _sprite = Resources.Load<Sprite>(ResourcePath); }
                return _sprite;
            }
        }
    }

    /// <summary>
    /// Phone vs everything else, for UI that should be a touch bigger on a handset only.
    /// WebGL asks the page (CSS short side + coarse pointer) through DeviceClass.jslib,
    /// because Unity only sees canvas pixels = CSS px x devicePixelRatio and cannot tell
    /// a DPR-2 phone from a DPR-1 desktop window. Elsewhere (Editor captures, standalone)
    /// the short side in pixels is used.
    /// </summary>
    public static class DeviceClass
    {
        /// <summary>Short side, CSS px, at or below which a touch device counts as a phone.</summary>
        public const float PhoneMaxShortSideCss = 520f;

        /// <summary>Test/capture hook: -1 auto, 0 force desktop, 1 force phone.</summary>
        public static int Override = -1;

#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern float RR_CssShortSide();
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern int RR_CoarsePointer();
#endif

        public static bool IsPhone()
        {
            if (Override >= 0) return Override == 1;
#if UNITY_WEBGL && !UNITY_EDITOR
            float s = RR_CssShortSide();
            return s > 0f && s <= PhoneMaxShortSideCss && RR_CoarsePointer() != 0;
#else
            return Mathf.Min(Screen.width, Screen.height) <= PhoneMaxShortSideCss;
#endif
        }
    }
}
