using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.Core;
using RenaultRacer.Race;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.UI
{
    /// <summary>
    /// Builds and drives the whole game UI (plan sections 6 and 14): READY, COUNTDOWN,
    /// HUD, FINISH and the invalid-lap panel, plus the minimap.
    ///
    /// **Built in code rather than authored in the scene**, which is a deliberate
    /// deviation from the plan's section 6 hierarchy. Three reasons: the Phase 6 control
    /// UI already works this way and it would be odd for half the UI to live in scene
    /// YAML; hand-authoring scene YAML is the one part of this project Claude cannot do
    /// safely (plan section 27); and panel *structure* stays reviewable in git this way
    /// instead of hiding in a binary-ish asset.
    ///
    /// Phase 10 art arrives through <see cref="UiArt"/> (Resources/UI/ui_atlas) and the
    /// UI font through <see cref="UiPrimitives.Font"/> (Resources/Fonts) - still no scene
    /// references, so nothing in Main.unity changes. Missing art degrades to plain shapes.
    ///
    /// Zero per-frame allocation: everything is constructed once in Start, and after
    /// that only colours, cached strings, sprite references and a few RectTransforms
    /// change. The one string built after startup, the finish time, is built once on
    /// the state change, never per frame; the invalid reason is a constant lookup.
    /// </summary>
    [DefaultExecutionOrder(-10)]
    public sealed class GameUi : MonoBehaviour
    {
        [SerializeField] private GameDirector director;
        [SerializeField] private CarController car;
        [SerializeField] private TrackRuntime trackRuntime;

        [Tooltip("Canvas to build under. Defaults to the one in the parents, then any.")]
        [SerializeField] private Canvas canvas;

        [Tooltip("Parent for panels that must respect notches and the home indicator. " +
                 "Defaults to the SafeAreaFitter's rect if there is one.")]
        [SerializeField] private RectTransform safeArea;

        [Tooltip("Where touch comes from, so the READY hints can name the right controls.")]
        [SerializeField] private RenaultRacer.InputSystem.InputRouter input;

        [Tooltip("The on-screen driving controls, hidden outside the driving states.")]
        [SerializeField] private TouchControlsView touchControls;

        private readonly UIRouter _router = new UIRouter();
        private HudView _hud;
        private CountdownView _countdown;
        private ResultsView _results;
        private Text _invalidBody;
        private GameState _lastState = GameState.Boot;

        // --- sector splits and personal bests --------------------------------------
        // GameUi owns these because it is the one component that sees BOTH the session's
        // splits and the director's verdict on the lap, and because PlayerPrefs has no
        // business inside the pure race classes.
        private PersonalBests _bests;
        private int _lastSectorCross;

        public UIRouter Router { get { return _router; } }
        public HudView Hud { get { return _hud; } }

        /// <summary>The player's local bests for this circuit. Null before Start.</summary>
        public PersonalBests Bests { get { return _bests; } }

        /// <summary>
        /// References in Awake, construction in Start. The split matters: the minimap
        /// outline is derived from TrackModel, and TrackRuntime builds the track in its
        /// own Awake. This component runs early on purpose (it has to drive the panels
        /// before anything reads them), so in Awake the track does not exist yet.
        /// </summary>
        private void Awake()
        {
            if (director == null) director = FindAnyObjectByType<GameDirector>();
            if (car == null) car = FindAnyObjectByType<CarController>();
            if (trackRuntime == null) trackRuntime = FindAnyObjectByType<TrackRuntime>();
            if (input == null) input = FindAnyObjectByType<RenaultRacer.InputSystem.InputRouter>();
            if (touchControls == null) touchControls = FindAnyObjectByType<TouchControlsView>();
            if (canvas == null) canvas = GetComponentInParent<Canvas>();
            if (canvas == null) canvas = FindAnyObjectByType<Canvas>();

            if (canvas == null)
            {
                Debug.LogError("[GameUi] no Canvas to build under.");
                enabled = false;
                return;
            }

            if (safeArea == null)
            {
                SafeAreaFitter fitter = canvas.GetComponentInChildren<SafeAreaFitter>(true);
                if (fitter != null) safeArea = (RectTransform)fitter.transform;
            }
        }

        private void Start()
        {
            if (!enabled) return;

            Transform host = safeArea != null ? safeArea.transform : canvas.transform;

            // Keyed to the track's own layout, so a retuned circuit does not inherit
            // times set on a different shape. TrackModel.Layout is never null.
            TrackModel track = trackRuntime != null ? trackRuntime.Track : null;
            _bests = new PersonalBests(track != null ? track.Layout : null);

            BuildReady(host);
            BuildCountdown(host);
            BuildHud(host);
            BuildFinish(host);
            BuildInvalid(host);
            PauseView.Create(host, director);
            SoundToggleView.Create(host, FindAnyObjectByType<RenaultRacer.Audio.AudioDirector>());
            FullscreenView.Create(host);

            // The director may already have entered its opening state during Start, so
            // adopt it now rather than waiting for a change that has been and gone.
            _lastState = director != null ? director.State : GameState.Boot;
            OnStateEntered(_lastState);
        }

        // ------------------------------------------------------------------ panels
        //
        // Phase 10: the screens follow screen-start / screen-finish / screen-invalid-lap
        // as REFERENCES. Nothing is a full-screen picture: each is a charcoal scrim over the
        // live game plus live text and a few atlas sprites. Every "button" is a visual
        // affordance only - the whole screen is the tap target, exactly as before
        // (GameDirector reads raw taps; no graphic here takes a raycast).

        /// <summary>Seconds green + GO stay up over the start of racing (presentation only).</summary>
        private const float GoHoldSeconds = 0.8f;

        private void BuildReady(Transform host)
        {
            GameObject go = NewPanel(host, "Panel_Ready", 0.55f);
            Transform t = go.transform;

            // The Renault Racer wordmark is the title: the same PNG as the HUD logo and the
            // loader. The old emblem + text title remain only as the no-art fallback.
            Sprite logo = BrandLogo.Sprite;
            if (logo != null)
            {
                Image img = UiPrimitives.Panel(t, "Logo", logo, Color.white);
                img.raycastTarget = false;
                img.preserveAspect = true;
                float h = 150f;
                float aspect = logo.rect.width / Mathf.Max(1f, logo.rect.height);
                UiPrimitives.Place(img.rectTransform, new Vector2(0.5f, 0.70f), new Vector2(0.5f, 0.5f),
                                   Vector2.zero, new Vector2(h * aspect, h));
            }
            else
            {
                if (UiArt.Get("icon_emblem") != null)
                {
                    Image emblem = UiPrimitives.Art(t, "Emblem", "icon_emblem", Color.white,
                                                    UiPrimitives.Box, Color.clear);
                    UiPrimitives.Place(emblem.rectTransform, new Vector2(0.5f, 0.80f), new Vector2(0.5f, 0.5f),
                                       Vector2.zero, UiArt.Size("icon_emblem", new Vector2(74f, 96f)));
                }

                // One label, rich text for the two colours, so the pair always centres as one.
                Text title = UiPrimitives.ReadableLabel(t, "Title",
                    UiCopy.TitleLead + " <color=#FFCC00>" + UiCopy.TitleAccent + "</color>", 64,
                    TextAnchor.MiddleCenter, Color.white);
                title.supportRichText = true;
                UiPrimitives.Place(title.rectTransform, new Vector2(0.5f, 0.625f), new Vector2(0.5f, 0.5f),
                                   Vector2.zero, new Vector2(900f, 84f));
            }

            Text tagline = UiPrimitives.BodyLabel(t, "Tagline", UiCopy.Tagline, 26,
                                                  TextAnchor.MiddleCenter, Color.white);
            UiPrimitives.Place(tagline.rectTransform, new Vector2(0.5f, 0.53f), new Vector2(0.5f, 0.5f),
                               Vector2.zero, new Vector2(900f, 40f));

            RectTransform button = AddButton(t, "StartButton", "pill", UiCopy.Start,
                                             new Vector2(0.5f, 0.35f), new Vector2(320f, 72f), 32);
            go.AddComponent<PulseScale>().target = button;

            // How to drive: icons on a touch device, one line of keys on a desktop.
            bool touch = (input == null || input.touchEnabled) && UnityEngine.Input.touchSupported;
            if (touch && UiArt.Get("steer_left_idle") != null)
            {
                AddIcon(t, "steer_left_idle", new Vector2(-150f, 0f), 50f);
                AddIcon(t, "steer_right_idle", new Vector2(-90f, 0f), 50f);
                AddIcon(t, "brake_idle", new Vector2(120f, 0f), 50f);
                AddCaption(t, UiCopy.HintSteer, new Vector2(-120f, -42f));
                AddCaption(t, UiCopy.HintBrake, new Vector2(120f, -42f));
            }
            else
            {
                Text keys = UiPrimitives.BodyLabel(t, "Keys", UiCopy.HintKeys, 17,
                                                   TextAnchor.MiddleCenter, UiPrimitives.Silver);
                UiPrimitives.Place(keys.rectTransform, new Vector2(0.5f, 0.17f), new Vector2(0.5f, 0.5f),
                                   Vector2.zero, new Vector2(1000f, 26f));
            }

            _router.Register(UIRouter.Panel.Ready, go);
        }

        private static void AddIcon(Transform parent, string sprite, Vector2 offset, float size)
        {
            Image img = UiPrimitives.Art(parent, sprite, sprite, new Color(1f, 1f, 1f, 0.9f),
                                         UiPrimitives.Disc, new Color(1f, 1f, 1f, 0.3f));
            UiPrimitives.Place(img.rectTransform, new Vector2(0.5f, 0.19f), new Vector2(0.5f, 0.5f),
                               offset, new Vector2(size, size));
        }

        private static void AddCaption(Transform parent, string text, Vector2 offset)
        {
            Text c = UiPrimitives.BodyLabel(parent, "Caption", text, 15, TextAnchor.MiddleCenter,
                                            UiPrimitives.Silver);
            UiPrimitives.Place(c.rectTransform, new Vector2(0.5f, 0.19f), new Vector2(0.5f, 0.5f),
                               offset, new Vector2(200f, 22f));
        }

        /// <summary>
        /// A button-shaped cue: a yellow (9-slice) shape with a charcoal label. Not
        /// clickable - see the note at the top of this section.
        /// </summary>
        internal static RectTransform AddButton(Transform parent, string name, string sprite, string label,
                                               Vector2 anchor, Vector2 size, int fontSize)
        {
            Image body = UiPrimitives.Art(parent, name, sprite, UiPrimitives.Yellow,
                                          UiPrimitives.Box, UiPrimitives.Yellow);
            RectTransform r = body.rectTransform;
            UiPrimitives.Place(r, anchor, new Vector2(0.5f, 0.5f), Vector2.zero, size);

            Text text = UiPrimitives.Label(r, "Label", label, fontSize, TextAnchor.MiddleCenter,
                                           UiPrimitives.Charcoal);
            UiPrimitives.Fill(text.rectTransform, 0f);
            return r;
        }

        /// <summary>The yellow-bordered charcoal card the result screens sit on.</summary>
        internal static RectTransform AddCard(Transform parent, Vector2 size)
        {
            Color fillColor = new Color(0.067f, 0.075f, 0.09f, 0.94f);
            Image fill = UiPrimitives.Art(parent, "Card", "rr_fill", fillColor, UiPrimitives.Box, fillColor);
            RectTransform r = fill.rectTransform;
            UiPrimitives.Place(r, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);

            if (UiArt.Get("rr_line") != null)
            {
                Image line = UiPrimitives.Art(r, "CardLine", "rr_line", UiPrimitives.Yellow,
                                              UiPrimitives.Box, Color.clear);
                UiPrimitives.Fill(line.rectTransform, 0f);
            }
            return r;
        }

        /// <summary>
        /// Thin yellow diagonals at the screen edges, the reference screens' motif. Plain
        /// boxes: no texture of their own, and they batch with the rest of the panel.
        /// </summary>
        private static void AddEdgeStripes(Transform parent)
        {
            AddStripe(parent, new Vector2(0f, 0.5f), new Vector2(30f, 0f), 16f);
            AddStripe(parent, new Vector2(0f, 0.5f), new Vector2(70f, 0f), 4f);
            AddStripe(parent, new Vector2(1f, 0.5f), new Vector2(-30f, 0f), 16f);
            AddStripe(parent, new Vector2(1f, 0.5f), new Vector2(-70f, 0f), 4f);
        }

        private static void AddStripe(Transform parent, Vector2 anchor, Vector2 offset, float width)
        {
            Image s = UiPrimitives.Panel(parent, "Stripe", UiPrimitives.Box, new Color(1f, 0.8f, 0f, 0.85f));
            UiPrimitives.Place(s.rectTransform, anchor, new Vector2(0.5f, 0.5f), offset, new Vector2(width, 1000f));
            s.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -24f);
        }

        private void BuildCountdown(Transform host)
        {
            GameObject go = NewPanel(host, "Panel_Countdown", 0f);
            _countdown = go.AddComponent<CountdownView>();
            _countdown.Build(go.transform);
            _router.Register(UIRouter.Panel.Countdown, go);
        }

        private void BuildHud(Transform host)
        {
            GameObject go = NewPanel(host, "Panel_Hud", 0f);
            _hud = go.AddComponent<HudView>();
            _hud.Build(go.transform, trackRuntime != null ? trackRuntime.Track : null);
            _router.Register(UIRouter.Panel.Hud, go);
        }

        private void BuildFinish(Transform host)
        {
            GameObject go = NewPanel(host, "Panel_Finish", 0.72f);
            Transform scrim = go.transform.Find("Scrim");
            _results = ResultsView.Create((RectTransform)go.transform,
                                          scrim != null ? scrim.GetComponent<Image>() : null, director);
            _router.Register(UIRouter.Panel.Finish, go);
        }

        private void BuildInvalid(Transform host)
        {
            GameObject go = NewPanel(host, "Panel_Invalid", 0.78f);
            AddEdgeStripes(go.transform);
            RectTransform card = AddCard(go.transform, new Vector2(720f, 440f));

            if (UiArt.Get("icon_warning") != null)
            {
                Image warn = UiPrimitives.Art(card, "Warning", "icon_warning", Color.white,
                                              UiPrimitives.Box, Color.clear);
                UiPrimitives.Place(warn.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
                                   new Vector2(0f, -70f), UiArt.Size("icon_warning", new Vector2(80f, 70f)));
            }

            Text title = UiPrimitives.Label(card, "Title", UiCopy.InvalidTitle, 52,
                                            TextAnchor.MiddleCenter, Color.white);
            UiPrimitives.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
                               new Vector2(0f, -160f), new Vector2(680f, 70f));

            // Reason-specific: filled from director.Rejection when the state is entered.
            _invalidBody = UiPrimitives.BodyLabel(card, "Body", UiCopy.InvalidMissedCheckpoints, 26,
                                                  TextAnchor.MiddleCenter, Color.white);
            _invalidBody.horizontalOverflow = HorizontalWrapMode.Wrap;
            UiPrimitives.Place(_invalidBody.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
                               new Vector2(0f, -232f), new Vector2(640f, 70f));

            RectTransform again = AddButton(card, "TryAgain", "rr_fill", UiCopy.TryAgain,
                                            new Vector2(0.5f, 0f), new Vector2(400f, 72f), 30);
            again.anchoredPosition = new Vector2(0f, 72f);
            go.AddComponent<PulseScale>().target = again;

            _router.Register(UIRouter.Panel.Invalid, go);
        }

        /// <summary>
        /// A full-screen panel, optionally over a charcoal scrim. The scrim is what makes
        /// white text legible over a road that is sometimes bright grass - and it reads as
        /// "the game is paused", which is true on every panel that uses one.
        /// </summary>
        private GameObject NewPanel(Transform host, string name, float scrimAlpha)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(host, false);
            UiPrimitives.Fill((RectTransform)go.transform, 0f);

            if (scrimAlpha > 0f)
            {
                Color c = UiPrimitives.Charcoal;
                c.a = scrimAlpha;
                Image scrim = UiPrimitives.Panel(go.transform, "Scrim", UiPrimitives.Box, c);
                // The scrim covers the whole SCREEN, not just the safe area, or a notch
                // shows an unscrimmed strip of road through the middle of a title card.
                RectTransform sr = scrim.rectTransform;
                sr.anchorMin = new Vector2(-0.2f, -0.2f);
                sr.anchorMax = new Vector2(1.2f, 1.2f);
                sr.offsetMin = Vector2.zero;
                sr.offsetMax = Vector2.zero;
            }
            return go;
        }

        // -------------------------------------------------------------------- drive

        private void LateUpdate()
        {
            if (director == null || _hud == null) return;

            GameState state = director.State;
            if (state != _lastState)
            {
                _lastState = state;
                OnStateEntered(state);
            }

            switch (state)
            {
                case GameState.Countdown:
                    _countdown.Tick(director.LampsLit, false);
                    break;

                case GameState.Racing:
                    // Held (paused, or the rotate gate up): the HUD stands still too.
                    if (car != null && car.Sim != null && director.Session != null && !director.Held)
                    {
                        _hud.Tick(director.Session.ElapsedSeconds,
                                  car.Sim.SpeedRatio,
                                  director.Session.LapProgress,
                                  car.Sim.OffRoad,
                                  director.Session.CheckpointsPassed,
                                  car.Sim.BoostLevel);
                        PollSectorCrossing(director.Session);
                    }
                    break;

            }

            _router.Tick(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Show the split when a sector line is crossed.
        ///
        /// Polled, not evented: comparing one int per frame costs nothing and allocates
        /// nothing, whereas a delegate on RaceSession would put a managed callback on the
        /// simulation path for three events a lap (plan 17.5).
        ///
        /// The comparison is against the best as it stood BEFORE this attempt - see
        /// <see cref="PersonalBests"/>. Nothing is written here: a split is only banked
        /// once the whole lap has been accepted.
        /// </summary>
        private void PollSectorCrossing(RaceSession session)
        {
            SectorTiming sectors = session.Sectors;
            if (sectors.CrossCount == _lastSectorCross) return;

            _lastSectorCross = sectors.CrossCount;
            int sector = sectors.LastClosedSector;
            if (sector < 0) return;

            bool hasBaseline = _bests != null && _bests.HasBaselineSector(sector);
            float delta = hasBaseline ? _bests.SectorDelta(sector, sectors.LastSplit) : 0f;
            _hud.ShowSectorDelta(sector, delta, hasBaseline);
        }

        /// <summary>
        /// Build the results card's content and bank the lap.
        ///
        /// ORDER MATTERS, and it is the whole reason this is one method: the content is
        /// read FIRST, against the bests as they stood before the attempt (the snapshot
        /// PersonalBests took at the countdown), and only then is the lap committed.
        /// Committing first would make every valid lap its own baseline and every
        /// difference would read zero forever.
        ///
        /// THIS IS THE ONLY PLACE A PERSONAL BEST IS WRITTEN, and it is reached only from
        /// GameState.Finished - which GameDirector enters only when RaceValidator returned
        /// None, and which the state-change guard in LateUpdate enters once per finish. A
        /// rejected lap goes to GameState.Invalid instead and leaves every best untouched.
        /// </summary>
        private void ShowFinishResults(RaceSession session)
        {
            float lap = session.ElapsedSeconds;
            ResultsModel model = ResultsModel.Build(lap, session.Sectors, _bests);
            if (_bests != null) _bests.CommitValidLap(lap, session.Sectors);
            if (_results != null) _results.Begin(model);
        }

        private void OnStateEntered(GameState state)
        {
            if (state != GameState.Finished && _results != null) _results.End();

            switch (state)
            {
                case GameState.Countdown:
                    _countdown.ResetDisplay();
                    _hud.ResetDisplay();
                    // GameDirector calls Session.Begin() on this same transition, which
                    // clears the attempt's splits. Snapshot the bests to match: every
                    // delta this lap is measured against what was stored a moment ago,
                    // not against a record this lap is busy setting.
                    if (_bests != null) _bests.BeginAttempt();
                    _lastSectorCross = 0;
                    break;

                case GameState.Racing:
                    // Green, briefly, on the frame the lights go out - then the HUD.
                    _countdown.Tick(3, true);
                    break;

                case GameState.Finished:
                    // Once per finish: the results card is built from the pre-attempt bests,
                    // then the lap is committed, then the reveal starts from t = 0.
                    if (director.Session != null) ShowFinishResults(director.Session);
                    break;

                case GameState.Invalid:
                    // The line must be true for THIS rejection (brief, INVALID LAP SCREEN).
                    if (_invalidBody != null)
                        _invalidBody.text = UiCopy.InvalidReason(director.Rejection);
                    break;
            }

            if (touchControls != null)
                touchControls.ControlsVisible = state == GameState.Countdown
                                             || state == GameState.Racing;

            // Green + GO linger briefly over the first moment of racing.
            _router.Show(UIRouter.For(state), state == GameState.Racing ? GoHoldSeconds : 0f);
        }
    }

    /// <summary>
    /// A slow, small scale "breath" on one element - the start/again button cue. It is
    /// here for a functional reason: a waiting screen has to look like it is waiting for
    /// you, or players sit on it. Unscaled time; writes one localScale per frame.
    /// </summary>
    public sealed class PulseScale : MonoBehaviour
    {
        public RectTransform target;
        public float speed = 2.6f;
        public float amount = 0.035f;

        private void LateUpdate()
        {
            if (target == null) return;
            float t = (Mathf.Sin(Time.unscaledTime * speed) + 1f) * 0.5f;
            float s = 1f + amount * t;
            target.localScale = new Vector3(s, s, 1f);
        }
    }
}
