using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.Core;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The pause button and the PREMOR overlay. Self-contained: GameUi only calls
    /// <see cref="Create"/> (before the sound button, so the sound button stays above the
    /// overlay and can still be used while paused).
    ///
    /// Button: top-right, the same disc, size and opacity as the sound button, directly
    /// BELOW it in the column at the safe area's right edge, right of the minimap (<see cref="Gap"/> units between them), with two bars drawn from UI
    /// rectangles - no texture. Shown only while the player can pause (countdown or racing,
    /// rotate gate down, not already paused).
    ///
    /// Overlay: dim backdrop, title, one line, and a NADALJUJ pill. Only that button
    /// resumes (with a generous hit margin); a stray tap anywhere else does nothing. Esc also
    /// toggles (GameDirector).
    ///
    /// Touch is polled raw, like the sound button. The state lives in GameDirector
    /// (UserPaused) - this view only asks it to change.
    /// </summary>
    public sealed class PauseView : MonoBehaviour
    {
        /// <summary>Clear space between the sound button and the pause button, UI units.</summary>
        public const float Gap = 10f;

        public static Vector2 ButtonSize { get { return SoundToggleView.ButtonSize; } }

        /// <summary>Directly under the sound button, same right edge (the column right of the minimap).</summary>
        public static Vector2 ButtonOffset
        {
            get { return SoundToggleView.ButtonOffset + new Vector2(0f, -(ButtonSize.y + Gap)); }
        }

        private static readonly Vector2 ResumeSize = new Vector2(320f, 72f);

        // Unity order: left, top, right, bottom - negative grows. The top stops half the gap
        // short of the sound button's own hit area; the bottom grows only 8 px, which keeps it off the right steer button on phones (about 15 units clear of it).
        public static Vector4 ButtonHitMargin(float canvasScale)
        {
            return new Vector4(-6f, -HudControls.HalfGapPx(canvasScale), -14f, -8f);
        }
        private static readonly Vector4 ResumeHitMargin = new Vector4(-40f, -40f, -40f, -40f);

        private static PauseView _instance;

        private GameDirector _director;
        private Canvas _canvas;
        private RectTransform _button, _overlay, _resume;
        private int _resumeFrame = -1;

        public bool ButtonVisible { get; private set; }
        public bool OverlayVisible { get; private set; }

        /// <summary>The button and resume rects, for tests and captures.</summary>
        public RectTransform ButtonRect { get { return _button; } }
        public RectTransform ResumeRect { get { return _resume; } }

        public static PauseView Create(Transform host, GameDirector director)
        {
            if (director == null) return null;
            GameObject go = new GameObject("PauseView", typeof(RectTransform));
            go.transform.SetParent(host, false);
            UiPrimitives.Fill((RectTransform)go.transform, 0f);
            PauseView v = go.AddComponent<PauseView>();
            v.Build(director);
            return v;
        }

        private void Build(GameDirector director)
        {
            _director = director;
            _instance = this;
            _canvas = GetComponentInParent<Canvas>();

            // --- overlay (never visible together with the button) -----------------------
            GameObject ov = new GameObject("PauseOverlay", typeof(RectTransform));
            ov.transform.SetParent(transform, false);
            _overlay = (RectTransform)ov.transform;
            UiPrimitives.Fill(_overlay, 0f);

            Color c = UiPrimitives.Charcoal; c.a = 0.62f;
            Image scrim = UiPrimitives.Panel(_overlay, "Scrim", UiPrimitives.Box, c);
            RectTransform sr = scrim.rectTransform;   // whole screen, past the notch insets
            sr.anchorMin = new Vector2(-0.2f, -0.2f); sr.anchorMax = new Vector2(1.2f, 1.2f);
            sr.offsetMin = Vector2.zero; sr.offsetMax = Vector2.zero;
            scrim.raycastTarget = false;

            Text title = UiPrimitives.ReadableLabel(_overlay, "Title", UiCopy.PauseTitle, 64,
                                                    TextAnchor.MiddleCenter, Color.white);
            UiPrimitives.Place(title.rectTransform, new Vector2(0.5f, 0.64f), new Vector2(0.5f, 0.5f),
                               Vector2.zero, new Vector2(900f, 84f));
            Text body = UiPrimitives.BodyLabel(_overlay, "Body", UiCopy.PauseBody, 26,
                                               TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.85f));
            UiPrimitives.Place(body.rectTransform, new Vector2(0.5f, 0.54f), new Vector2(0.5f, 0.5f),
                               Vector2.zero, new Vector2(900f, 40f));
            _resume = GameUi.AddButton(_overlay, "ResumeButton", "pill", UiCopy.Resume,
                                       new Vector2(0.5f, 0.38f), ResumeSize, 32);
            _overlay.gameObject.SetActive(false);

            // --- button -------------------------------------------------------------------
            GameObject b = new GameObject("PauseButton", typeof(RectTransform));
            b.transform.SetParent(transform, false);
            _button = (RectTransform)b.transform;
            UiPrimitives.Place(_button, new Vector2(1f, 1f), new Vector2(1f, 1f), ButtonOffset, ButtonSize);
            Image bg = UiPrimitives.Panel(_button, "Bg", UiPrimitives.Disc, new Color(0.067f, 0.075f, 0.09f, 0.62f));
            UiPrimitives.Fill(bg.rectTransform, 0f);
            bg.raycastTarget = false;
            for (int i = 0; i < 2; i++)
            {
                Image bar = UiPrimitives.Panel(_button, "Bar" + i, UiPrimitives.Box, Color.white);
                UiPrimitives.Place(bar.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                                   new Vector2(i == 0 ? -8f : 8f, 0f), new Vector2(9f, 26f));
                bar.raycastTarget = false;
            }
            _button.gameObject.SetActive(false);
        }

        private void OnDestroy() { if (_instance == this) _instance = null; }

        private bool Over(RectTransform r, Vector2 p, Vector4 margin)
        {
            Camera cam = (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                       ? _canvas.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(r, p, cam, margin);
        }

        /// <summary>True on the pause button (while shown) or the overlay's resume button.</summary>
        public static bool IsOverControl(Vector2 p)
        {
            PauseView v = _instance;
            if (v == null || !v.isActiveAndEnabled) return false;
            if (v.ButtonVisible && v.Over(v._button, p, ButtonHitMargin(v._canvas != null ? v._canvas.scaleFactor : 1f))) return true;
            // The frame of the resume press still counts, so the finger that resumed is
            // never classified as steering by a reader that runs after this view.
            if ((v.OverlayVisible || v._resumeFrame == Time.frameCount) && v.Over(v._resume, p, ResumeHitMargin))
                return true;
            return false;
        }

        private void Update()
        {
            if (_director == null) return;

            bool paused = _director.UserPaused;
            bool showButton = _director.CanUserPause && !paused;
            bool showOverlay = paused;
            if (showButton != ButtonVisible) { ButtonVisible = showButton; _button.gameObject.SetActive(showButton); }
            if (showOverlay != OverlayVisible) { OverlayVisible = showOverlay; _overlay.gameObject.SetActive(showOverlay); }

            Vector2 pos;
            if (!_director.GateHeld && SoundToggleView.PressedThisFrame(out pos))
            {
                if (ButtonVisible && Over(_button, pos, ButtonHitMargin(_canvas != null ? _canvas.scaleFactor : 1f))) _director.SetUserPaused(true);
                else if (OverlayVisible && Over(_resume, pos, ResumeHitMargin))
                {
                    _resumeFrame = Time.frameCount;
                    _director.SetUserPaused(false);
                }
            }
        }
    }
}

