using UnityEngine;
using RenaultRacer.Core;

namespace RenaultRacer.UI
{
    /// <summary>
    /// One panel per state, exactly one visible at a time (plan section 14).
    ///
    /// Deliberately not a framework. It is an array, a switch and a CanvasGroup fade:
    /// there are five panels and they change about once a minute. The rotate gate is
    /// NOT one of them - it is a modal overlay that remembers the state underneath
    /// (plan section 7) and is owned by RotateGate.
    ///
    /// The fade runs on a plain timer rather than a coroutine, for the same reason the
    /// countdown does: coroutines allocate, and plan 17.5 wants none while racing.
    /// </summary>
    public sealed class UIRouter
    {
        /// <summary>The panels this router owns. Ordered as they appear in the flow.</summary>
        public enum Panel { None = 0, Ready, Countdown, Hud, Finish, Invalid }

        private readonly CanvasGroup[] _groups = new CanvasGroup[6];
        private readonly float[] _alpha = new float[6];
        private readonly RectTransform[] _rects = new RectTransform[6];
        private Panel _current = Panel.None;
        private Panel _held = Panel.None;
        private float _holdTimer;

        /// <summary>
        /// How far an incoming panel is scaled down at the start of its fade
        /// (checklist 8.4). Small on purpose: at 0.96 it reads as the panel settling
        /// into place, and anything more starts to feel like a slideshow on a phone
        /// where panels change every few seconds.
        /// </summary>
        public float riseFrom = 0.96f;

        /// <summary>How fast a panel fades, in alpha per second.</summary>
        public float fadeSpeed = 6f;

        public Panel Current { get { return _current; } }

        public void Register(Panel panel, GameObject go)
        {
            if (go == null) return;
            CanvasGroup cg = go.GetComponent<CanvasGroup>();
            if (cg == null) cg = go.AddComponent<CanvasGroup>();
            // Nothing in this UI is clicked - touch is read from raw screen coordinates
            // by TouchDriveInput - so no panel may ever intercept a press.
            cg.interactable = false;
            cg.blocksRaycasts = false;
            cg.alpha = 0f;
            go.SetActive(false);
            _groups[(int)panel] = cg;
            _alpha[(int)panel] = 0f;
            _rects[(int)panel] = go.GetComponent<RectTransform>();
        }

        public void Show(Panel panel)
        {
            Show(panel, 0f);
        }

        /// <summary>
        /// Show <paramref name="panel"/>, but keep the OUTGOING panel fully visible for
        /// <paramref name="holdOutgoingSeconds"/> before it fades. Used for exactly one
        /// moment: green lights + GO stay up briefly over the first second of racing
        /// while the HUD fades in underneath. Without the hold GO faded out in ~0.17 s
        /// on the frame it appeared. Presentation only - the race has already started.
        /// </summary>
        public void Show(Panel panel, float holdOutgoingSeconds)
        {
            if (_current == panel) return;
            _held = holdOutgoingSeconds > 0f ? _current : Panel.None;
            _holdTimer = holdOutgoingSeconds;
            _current = panel;

            // Activate the incoming panel immediately so it can fade IN; outgoing ones
            // stay active until their alpha reaches zero, then switch off so they cost
            // nothing (plan 17.4 - an invisible panel still batches if it is enabled).
            CanvasGroup cg = _groups[(int)panel];
            if (cg != null) cg.gameObject.SetActive(true);
        }

        /// <summary>Drive the fades. Unscaled, so a frozen simulation still animates.</summary>
        public void Tick(float unscaledDeltaTime)
        {
            float step = fadeSpeed * unscaledDeltaTime;
            if (_holdTimer > 0f)
            {
                _holdTimer -= unscaledDeltaTime;
                if (_holdTimer <= 0f) _held = Panel.None;
            }

            for (int i = 1; i < _groups.Length; i++)
            {
                CanvasGroup cg = _groups[i];
                if (cg == null) continue;

                bool held = _held != Panel.None && i == (int)_held;
                float target = (i == (int)_current || held) ? 1f : 0f;
                if (Mathf.Approximately(_alpha[i], target))
                {
                    if (target == 0f && cg.gameObject.activeSelf) cg.gameObject.SetActive(false);
                    continue;
                }

                _alpha[i] = Mathf.MoveTowards(_alpha[i], target, step);
                cg.alpha = _alpha[i];

                // The incoming panel rises into place as it fades in. The outgoing one
                // only fades - scaling something away while it disappears reads as a
                // glitch rather than as a transition.
                RectTransform rt = _rects[i];
                if (rt != null)
                {
                    float sc = (target > 0f && !held) ? Mathf.Lerp(riseFrom, 1f, _alpha[i]) : 1f;
                    rt.localScale = new Vector3(sc, sc, 1f);
                }

                if (_alpha[i] <= 0f) cg.gameObject.SetActive(false);
            }
        }

        /// <summary>The panel a game state should be showing.</summary>
        public static Panel For(GameState state)
        {
            switch (state)
            {
                case GameState.Ready:     return Panel.Ready;
                case GameState.Countdown: return Panel.Countdown;
                case GameState.Racing:    return Panel.Hud;
                case GameState.Finished:  return Panel.Finish;
                case GameState.Invalid:   return Panel.Invalid;
                default:                  return Panel.None;
            }
        }
    }
}
