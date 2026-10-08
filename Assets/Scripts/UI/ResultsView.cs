using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.Core;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The animated results card shown in GameState.Finished (replaces the static finish
    /// card). Built in code like the rest of the UI, from UiPrimitives / UiArt and the
    /// shared card helpers; no animation library, no particles, no new art.
    ///
    /// ONE FUNCTION OF TIME. Every animated property is set by <see cref="Apply"/> as a pure
    /// function of the reveal clock <c>t</c>. That makes the three awkward cases the same
    /// case: a replay resets to Apply(0), a skip jumps to Apply(Total), and a rotate gate
    /// simply stops the clock - the card resumes exactly where it was.
    ///
    /// TIMELINE (seconds, unscaled, frame dt clamped to <see cref="MaxDt"/>):
    ///   0.00-0.35  scrim fades in; card eases 0.96 -> 1 with a fade
    ///   0.00-0.42  thick diagonals draw from the screen edge (ease-out); thin ones follow
    ///              0.06 later (right pair a further 0.04); each flashes bright as it lands
    ///   0.25-0.50  CILJ! scales 1.3 -> 1 and fades in
    ///   0.30-0.85  flag fades/scales in with a damped two-cycle wobble (max 6 degrees)
    ///   0.40-0.65  underline draws (scale X 0 -> 1)
    ///   0.55 / 0.72 (+0.30 each)  PO SEKTORJU 1 / PO SEKTORJU 2 rows slide 24 units from
    ///              the left and fade; each value (delta vs the best lap's cumulative split,
    ///              or the cumulative time) counts to its exact text over 0.25 s, 0.12 in
    ///   0.95-1.20  divider draws
    ///   1.10-1.40  KONCNI CAS block appears; the time counts up 1.20-1.70 to its exact string;
    ///              one bright glint travels the yellow border 1.25-1.75
    ///   1.60-1.85  best row appears; its difference counts 1.75-2.05 (new best: heading
    ///              scales in, row tinted yellow, one flash/pulse, green count 1.75-2.15)
    ///   1.90-2.15  IGRAJ ZNOVA fades/scales in; 2.15-2.35 one brightness + scale pulse
    ///   2.35       done - everything static
    ///
    /// INTERACTION. GameDirector asks <see cref="ConsumeTapAsSkip"/> on the tap that would
    /// restart. While the reveal runs, a tap that is not on the button completes it at once
    /// and is consumed; a tap on the button, or any tap after the end, restarts as before.
    ///
    /// The view is idle (inactive panel) while racing, and a finished reveal does nothing
    /// per frame. Strings built during the reveal come only from count-ups and are rebuilt
    /// only when the shown unit changes.
    /// </summary>
    public sealed class ResultsView : MonoBehaviour, IRevealSkippable
    {
        public const float Total = 2.35f;
        public const float MaxDt = 1f / 20f;

        // Card geometry, UI units at the 1280x720 reference (laid out from the mock).
        private const float CardW = 620f, CardH = 572f;
        private const float RowW = 446f, RowH = 56f;
        private const float BlockW = 491f;
        private const float RowTop = 142f, RowStep = 64f;     // two checkpoint rows
        private const float DividerY = 285f;
        private const float BlockTop = 304f, BlockH = 88f;
        private const float BestTop = 412f;
        private const float ButtonTop = 486f;

        private static readonly Vector4 ButtonHitMargin = new Vector4(-16f, -14f, -16f, -10f);
        private static readonly Color Dark = new Color(0.047f, 0.053f, 0.067f, 0.92f);
        private static readonly Color GreyLine = new Color(0.34f, 0.37f, 0.42f, 0.95f);
        private static readonly Color Flash = new Color(1f, 0.95f, 0.62f, 1f);

        private sealed class Stripe
        {
            public Image img; public Color col; public float start, end;
        }

        private sealed class Row
        {
            public RectTransform rt; public CanvasGroup g; public Text label, value; public int shown = -1;
        }

        private GameDirector _director;
        private Canvas _canvas;
        private RectTransform _panel, _fit, _card, _button;
        private CanvasGroup _cardGroup, _buttonGroup;
        private Image _scrim, _buttonImg;
        private Color _scrimBase;
        private readonly Stripe[] _stripes = new Stripe[4];
        private RectTransform _titleRt, _flagRt, _underRt, _divRt, _timeRt, _bestRt, _bestLabelRt;
        private CanvasGroup _titleGroup, _flagGroup, _timeGroup, _bestGroup, _divGroup;
        private readonly Row[] _rows = new Row[2];
        private Text _timeText, _bestLabel, _bestTime, _bestDiff;
        private Image _glintTop, _glintBot, _bestHi, _bestFlash;
        private Vector2 _lastPanelSize;

        private ResultsModel _model;
        private bool _running, _complete;
        private float _t;
        private int _timeShown = -1, _diffShown = -1;

        // ----------------------------------------------------------------- state (probe)
        public float Elapsed { get { return _t; } }
        public bool IsRunning { get { return _running; } }
        public bool IsComplete { get { return _complete; } }
        public float ScrimAlpha { get { return _scrim != null ? _scrim.color.a : 0f; } }
        public float CardAlpha { get { return _cardGroup != null ? _cardGroup.alpha : 0f; } }
        public float TitleAlpha { get { return _titleGroup != null ? _titleGroup.alpha : 0f; } }
        public float RowAlpha(int i) { return _rows[i].g.alpha; }
        public string RowText(int i) { return _rows[i].value.text; }
        public float TimeAlpha { get { return _timeGroup.alpha; } }
        public string TimeShown { get { return _timeText.text; } }
        public float BestAlpha { get { return _bestGroup.alpha; } }
        public string BestHeadingShown { get { return _bestLabel.text; } }
        public string BestTimeShown { get { return _bestTime.text; } }
        public string BestDiffShown { get { return _bestDiff.text; } }
        public Color BestDiffColour { get { return _bestDiff.color; } }
        public Color RowColour(int i) { return _rows[i].value.color; }
        public float ButtonAlpha { get { return _buttonGroup.alpha; } }
        public float ButtonScale { get { return _button.localScale.x; } }
        public float StripeFill(int i) { return _stripes[i].img.fillAmount; }
        public float UnderlineScale { get { return _underRt.localScale.x; } }
        public float FitScale { get { return _fit.localScale.x; } }
        public RectTransform ButtonRect { get { return _button; } }
        public RectTransform CardRect { get { return _card; } }

        /// <summary>Colour for a tone: the sector pill's green/red, white, or brand yellow.</summary>
        public static Color ColourFor(ResultTone tone)
        {
            switch (tone)
            {
                case ResultTone.Gain: return SectorDeltaView.Gain;
                case ResultTone.Loss: return SectorDeltaView.Loss;
                case ResultTone.Highlight: return UiPrimitives.Yellow;
                default: return Color.white;
            }
        }

        // ------------------------------------------------------------------- build

        /// <summary>
        /// Build under <paramref name="panel"/> (the Panel_Finish object, already holding its
        /// scrim). Registers as <see cref="ResultsReveal.Current"/>.
        /// </summary>
        public static ResultsView Create(RectTransform panel, Image scrim, GameDirector director)
        {
            ResultsView v = panel.gameObject.AddComponent<ResultsView>();
            v.Build(panel, scrim, director);
            return v;
        }

        private void Build(RectTransform panel, Image scrim, GameDirector director)
        {
            _panel = panel;
            _scrim = scrim;
            _scrimBase = scrim != null ? scrim.color : new Color(0.067f, 0.075f, 0.09f, 0.72f);
            _director = director;
            _canvas = panel.GetComponentInParent<Canvas>();

            BuildStripes(panel);

            // The fit root scales the whole card uniformly on a short safe area; the card
            // inside it carries the entrance animation.
            GameObject fit = new GameObject("CardFit", typeof(RectTransform));
            fit.transform.SetParent(panel, false);
            _fit = (RectTransform)fit.transform;
            UiPrimitives.Place(_fit, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero,
                               new Vector2(CardW, CardH));

            _card = GameUi.AddCard(_fit, new Vector2(CardW, CardH));
            _cardGroup = Group(_card.gameObject);

            BuildTitle(_card);
            BuildSectors(_card);
            BuildTime(_card);
            BuildBest(_card);

            _button = GameUi.AddButton(_card, "PlayAgain", "rr_fill", UiCopy.PlayAgain,
                                       new Vector2(0.5f, 1f), new Vector2(BlockW, 68f), 34);
            _button.pivot = new Vector2(0.5f, 1f);
            _button.anchoredPosition = new Vector2(0f, -ButtonTop);
            _buttonImg = _button.GetComponent<Image>();
            _buttonGroup = Group(_button.gameObject);

            ResultsReveal.Current = this;
            Apply(0f);
        }

        private static CanvasGroup Group(GameObject go)
        {
            CanvasGroup g = go.AddComponent<CanvasGroup>();
            g.interactable = false;
            g.blocksRaycasts = false;
            return g;
        }

        private static RectTransform Slot(Transform parent, string name, float top, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform r = (RectTransform)go.transform;
            UiPrimitives.Place(r, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, -top - size.y * 0.5f), size);
            return r;
        }

        private static void Plate(RectTransform parent, Color line, float glow)
        {
            Image fill = UiPrimitives.Art(parent, "Fill", "rr_fill", Dark, UiPrimitives.Box, Dark);
            UiPrimitives.Fill(fill.rectTransform, 0f);
            if (glow > 0f && UiArt.Get("rr_line") != null)
            {
                Color gc = line; gc.a = glow;
                Image g = UiPrimitives.Art(parent, "Glow", "rr_line", gc, UiPrimitives.Box, Color.clear);
                UiPrimitives.Fill(g.rectTransform, -5f);
            }
            if (UiArt.Get("rr_line") != null)
            {
                Image l = UiPrimitives.Art(parent, "Line", "rr_line", line, UiPrimitives.Box, Color.clear);
                UiPrimitives.Fill(l.rectTransform, 0f);
            }
        }

        private static Text Cell(Transform parent, string name, int size, TextAnchor align, Color col,
                                 float leftPad, float rightPad)
        {
            Text t = UiPrimitives.Label(parent, name, "", size, align, col);
            RectTransform r = t.rectTransform;
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(leftPad, 0f); r.offsetMax = new Vector2(-rightPad, 0f);
            return t;
        }

        private void BuildStripes(RectTransform panel)
        {
            GameObject host = new GameObject("Stripes", typeof(RectTransform));
            host.transform.SetParent(panel, false);
            UiPrimitives.Fill((RectTransform)host.transform, 0f);
            Transform h = host.transform;

            Color y = new Color(1f, 0.8f, 0f, 0.9f);
            // Left pair rises from the bottom-left edge; right pair falls from the top-right.
            _stripes[0] = MakeStripe(h, "L_Thick", new Vector2(0f, 0f), new Vector2(12f, -30f), 463f, 30f, -31.8f, y, 0f);
            _stripes[1] = MakeStripe(h, "L_Thin", new Vector2(0f, 0f), new Vector2(60f, -30f), 383f, 8f, -31.8f, y, 0.06f);
            _stripes[2] = MakeStripe(h, "R_Thick", new Vector2(1f, 1f), new Vector2(-40f, 25f), 410f, 30f, 142f, y, 0.04f);
            _stripes[3] = MakeStripe(h, "R_Thin", new Vector2(1f, 1f), new Vector2(-88f, 25f), 312f, 8f, 142f, y, 0.10f);
        }

        private static Stripe MakeStripe(Transform parent, string name, Vector2 anchor, Vector2 offset,
                                         float length, float width, float angle, Color col, float start)
        {
            Image s = UiPrimitives.Panel(parent, name, UiPrimitives.Box, col);
            RectTransform r = s.rectTransform;
            UiPrimitives.Place(r, anchor, new Vector2(0.5f, 0f), offset, new Vector2(width, length));
            r.localRotation = Quaternion.Euler(0f, 0f, angle);
            s.type = Image.Type.Filled;
            s.fillMethod = Image.FillMethod.Vertical;
            s.fillOrigin = 0;               // from the pivot end, which sits at the screen edge
            s.fillAmount = 0f;
            Stripe st = new Stripe();
            st.img = s; st.col = col; st.start = start; st.end = start + 0.42f;
            return st;
        }

        private void BuildTitle(RectTransform card)
        {
            Text title = UiPrimitives.Label(card, "Title", UiCopy.FinishTitle, 80, TextAnchor.MiddleCenter, Color.white);
            _titleRt = title.rectTransform;
            _titleGroup = Group(title.gameObject);

            float tw = title.preferredWidth;
            if (tw < 80f) tw = 215f;
            Vector2 flagSize = UiArt.Get("icon_checkered") != null ? new Vector2(66f, 64f) : Vector2.zero;
            float gap = flagSize.x > 0f ? 8f : 0f;
            float total = tw + gap + flagSize.x;
            float left = -total * 0.5f;
            UiPrimitives.Place(_titleRt, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
                               new Vector2(left + tw * 0.5f, -66f), new Vector2(tw + 20f, 100f));

            if (flagSize.x > 0f)
            {
                Image flag = UiPrimitives.Art(card, "Flag", "icon_checkered", Color.white,
                                              UiPrimitives.Box, Color.clear);
                _flagRt = flag.rectTransform;
                UiPrimitives.Place(_flagRt, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
                                   new Vector2(left + tw + gap + flagSize.x * 0.5f, -64f), flagSize);
                _flagGroup = Group(flag.gameObject);
            }

            Image u = UiPrimitives.Panel(card, "Underline", UiPrimitives.Box, UiPrimitives.Yellow);
            _underRt = u.rectTransform;
            UiPrimitives.Place(_underRt, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, -118f),
                               new Vector2(196f, 3f));
        }

        private void BuildSectors(RectTransform card)
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                Row r = new Row();
                r.rt = Slot(card, "Checkpoint" + (i + 1), RowTop + RowStep * i, new Vector2(RowW, RowH));
                Plate(r.rt, GreyLine, 0f);
                r.g = Group(r.rt.gameObject);
                r.label = Cell(r.rt, "Label", 24, TextAnchor.MiddleLeft, Color.white, 26f, 20f);
                r.value = Cell(r.rt, "Value", 30, TextAnchor.MiddleRight, Color.white, 20f, 33f);
                _rows[i] = r;
            }

            Image d = UiPrimitives.Panel(card, "Divider", UiPrimitives.Box, new Color(1f, 0.8f, 0f, 0.8f));
            _divRt = d.rectTransform;
            UiPrimitives.Place(_divRt, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, -DividerY),
                               new Vector2(RowW, 2f));
            _divGroup = Group(d.gameObject);
        }

        private void BuildTime(RectTransform card)
        {
            _timeRt = Slot(card, "FinalTime", BlockTop, new Vector2(BlockW, BlockH));
            Plate(_timeRt, UiPrimitives.Yellow, 0.22f);
            _timeGroup = Group(_timeRt.gameObject);
            Text label = Cell(_timeRt, "Label", 21, TextAnchor.MiddleLeft, Color.white, 26f, 20f);
            label.text = UiCopy.FinalTime;
            _timeText = Cell(_timeRt, "Time", 60, TextAnchor.MiddleRight, Color.white, 20f, 27f);

            _glintTop = MakeGlint(_timeRt, "GlintTop", 1f);
            _glintBot = MakeGlint(_timeRt, "GlintBottom", 0f);
        }

        private static Image MakeGlint(RectTransform parent, string name, float edge)
        {
            Image g = UiPrimitives.Panel(parent, name, UiPrimitives.Box, new Color(1f, 0.96f, 0.7f, 0f));
            UiPrimitives.Place(g.rectTransform, new Vector2(0.5f, edge), new Vector2(0.5f, 0.5f),
                               Vector2.zero, new Vector2(90f, 3f));
            return g;
        }

        private void BuildBest(RectTransform card)
        {
            _bestRt = Slot(card, "Best", BestTop, new Vector2(BlockW, RowH));
            Plate(_bestRt, GreyLine, 0f);
            _bestGroup = Group(_bestRt.gameObject);

            _bestHi = UiPrimitives.Art(_bestRt, "Highlight", "rr_fill", new Color(1f, 0.8f, 0f, 0f),
                                       UiPrimitives.Box, new Color(1f, 0.8f, 0f, 0f));
            UiPrimitives.Fill(_bestHi.rectTransform, 0f);
            _bestFlash = UiPrimitives.Art(_bestRt, "Flash", "rr_fill", new Color(1f, 1f, 1f, 0f),
                                          UiPrimitives.Box, new Color(1f, 1f, 1f, 0f));
            UiPrimitives.Fill(_bestFlash.rectTransform, 0f);

            _bestLabel = UiPrimitives.Label(_bestRt, "Heading", UiCopy.BestTitle, 18, TextAnchor.MiddleLeft, UiPrimitives.Silver);
            _bestLabelRt = _bestLabel.rectTransform;
            UiPrimitives.Place(_bestLabelRt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(21f, 0f),
                               new Vector2(200f, 36f));

            _bestTime = UiPrimitives.Label(_bestRt, "Time", "", 32, TextAnchor.MiddleLeft, Color.white);
            UiPrimitives.Place(_bestTime.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                               new Vector2(223f, 0f), new Vector2(150f, 44f));

            _bestDiff = Cell(_bestRt, "Diff", 29, TextAnchor.MiddleRight, Color.white, 300f, 22f);
        }

        // -------------------------------------------------------------------- drive

        /// <summary>
        /// Reset every element to its t = 0 state and start the reveal for
        /// <paramref name="model"/>. Safe to call on a panel that is still inactive.
        /// </summary>
        public void Begin(ResultsModel model)
        {
            _model = model;
            ResultsReveal.Current = this;

            int n = _rows.Length;
            for (int i = 0; i < n; i++)
            {
                Row r = _rows[i];
                bool has = model != null && model.Sectors != null && i < model.Sectors.Length;
                r.rt.gameObject.SetActive(has);
                if (!has) continue;
                r.label.text = model.SectorLabels[i];
                r.value.color = ColourFor(model.Sectors[i].Tone);
            }

            if (model != null)
            {
                _bestLabel.text = model.BestHeading;
                // The long heading shrinks to fit the label slot (best-fit text misbehaves at small canvas scales).
                _bestLabel.fontSize = model.BestHeading.Length > 14 ? 15 : 18;
                _bestLabel.color = model.Kind == BestKind.Best ? UiPrimitives.Silver : UiPrimitives.Yellow;
                _bestTime.text = model.BestTimeText;
                _bestDiff.color = ColourFor(model.BestDiff.Tone);
            }

            _timeShown = -1;
            _diffShown = -1;
            for (int i = 0; i < n; i++) _rows[i].shown = -1;
            _t = 0f;
            _running = true;
            _complete = false;
            Refit();
            Apply(0f);
        }

        /// <summary>Leaving the finished state: stop the clock. The card is left as it was while it fades out.</summary>
        public void End()
        {
            _running = false;
        }

        private void OnDestroy()
        {
            if (ResultsReveal.Current == (IRevealSkippable)this) ResultsReveal.Current = null;
        }

        private void Update()
        {
            if (!_running) return;
            if (_panel.rect.size != _lastPanelSize) Refit();
            if (_complete) return;
            Step(Time.unscaledDeltaTime, _director != null && _director.GateHeld);
        }

        /// <summary>
        /// Advance the reveal by one frame. The clock does not move while the rotate gate is
        /// up, and one frame can never move it by more than <see cref="MaxDt"/>, so a hitch
        /// cannot skip a step.
        /// </summary>
        public void Step(float rawDt, bool gateHeld)
        {
            if (!_running || _complete || gateHeld) return;
            float dt = rawDt < 0f ? 0f : (rawDt > MaxDt ? MaxDt : rawDt);
            _t += dt;
            if (_t >= Total) Complete();
            else Apply(_t);
        }

        /// <summary>Jump every element to its final state.</summary>
        public void Complete()
        {
            _t = Total;
            Apply(Total);
            _complete = true;
        }

        private void Refit()
        {
            Vector2 sz = _panel.rect.size;
            _lastPanelSize = sz;
            float s = 1f;
            if (sz.y > 1f && sz.x > 1f)
                s = Mathf.Min(1f, 0.88f * sz.y / CardH, 0.94f * sz.x / CardW);
            _fit.localScale = new Vector3(s, s, 1f);
            FitStripes(s);
        }

        /// <summary>How far each stripe runs on under the card's edge, UI units.</summary>
        private const float StripeTuck = 40f;

        /// <summary>
        /// Length each stripe so it runs from its screen edge all the way in and ends just
        /// UNDER the card (built after the stripes, so it covers them). A fixed length stopped
        /// short of the card on some aspects and left a visible gap; this follows the
        /// stripe's own line to the card rect at the card's current scale.
        /// </summary>
        private void FitStripes(float cardScale)
        {
            Rect pr = _panel.rect;
            Vector2 c = pr.center;
            float hx = CardW * cardScale * 0.5f, hy = CardH * cardScale * 0.5f;
            for (int i = 0; i < _stripes.Length; i++)
            {
                Stripe st = _stripes[i];
                if (st == null) continue;
                RectTransform r = st.img.rectTransform;
                Vector2 a = r.anchorMin;
                Vector2 p = new Vector2(Mathf.Lerp(pr.xMin, pr.xMax, a.x), Mathf.Lerp(pr.yMin, pr.yMax, a.y))
                          + r.anchoredPosition - c;
                float ang = r.localEulerAngles.z * Mathf.Deg2Rad;
                Vector2 d = new Vector2(-Mathf.Sin(ang), Mathf.Cos(ang));   // the stripe's local up

                // Ray against the card rect (slab test): distance to where the line enters it.
                float tMin = 0f, tMax = float.MaxValue;
                if (!Slab(p.x, d.x, hx, ref tMin, ref tMax) || !Slab(p.y, d.y, hy, ref tMin, ref tMax))
                    continue;   // the line misses the card on this aspect: keep the authored length
                Vector2 size = r.sizeDelta;
                size.y = Mathf.Min(tMin + StripeTuck, tMax);
                r.sizeDelta = size;
            }
        }

        private static bool Slab(float p, float d, float h, ref float tMin, ref float tMax)
        {
            if (Mathf.Abs(d) < 1e-5f) return p >= -h && p <= h;
            float t0 = (-h - p) / d, t1 = (h - p) / d;
            if (t0 > t1) { float tmp = t0; t0 = t1; t1 = tmp; }
            tMin = Mathf.Max(tMin, t0);
            tMax = Mathf.Min(tMax, t1);
            return tMin <= tMax;
        }

        // -------------------------------------------------------------- interaction

        public bool ConsumeTapAsSkip()
        {
            if (!_running || _complete) return false;
            Vector2 p;
            if (!TryReadTap(out p)) return false;
            return ConsumeTap(p);
        }

        /// <summary>The skip rule for a tap at <paramref name="screenPoint"/> (testable without input).</summary>
        public bool ConsumeTap(Vector2 screenPoint)
        {
            if (!_running || _complete) return false;
            if (OverButton(screenPoint)) return false;
            Complete();
            return true;
        }

        public bool OverButton(Vector2 screenPoint)
        {
            Camera cam = (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                       ? _canvas.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(_button, screenPoint, cam, ButtonHitMargin);
        }

        /// <summary>The same tap GameDirector.TapStarted saw: a touch Began or a mouse press, off the HUD controls.</summary>
        private static bool TryReadTap(out Vector2 pos)
        {
            int n = Input.touchCount;
            for (int i = 0; i < n; i++)
            {
                Touch t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began && !HudControls.IsOverInteractive(t.position))
                { pos = t.position; return true; }
            }
            if (n == 0 && Input.GetMouseButtonDown(0) && !HudControls.IsOverInteractive(Input.mousePosition))
            { pos = Input.mousePosition; return true; }
            pos = Vector2.zero;
            return false;
        }

        // ---------------------------------------------------------------- timeline

        private static float P(float t, float a, float b)
        {
            if (t <= a) return 0f;
            if (t >= b) return 1f;
            return (t - a) / (b - a);
        }

        private static float Out2(float u) { return 1f - (1f - u) * (1f - u); }
        private static float Out3(float u) { float v = 1f - u; return 1f - v * v * v; }

        private static void SetAlpha(Image img, float a)
        {
            Color c = img.color; c.a = a; img.color = c;
        }

        private static void Scale(RectTransform r, float s) { r.localScale = new Vector3(s, s, 1f); }

        private void Apply(float t)
        {
            // --- scrim and card
            float cardK = Out2(P(t, 0f, 0.35f));
            if (_scrim != null)
            {
                Color sc = _scrimBase; sc.a = _scrimBase.a * cardK; _scrim.color = sc;
            }
            _cardGroup.alpha = cardK;
            Scale(_card, Mathf.Lerp(0.96f, 1f, cardK));

            // --- diagonals
            for (int i = 0; i < _stripes.Length; i++)
            {
                Stripe s = _stripes[i];
                s.img.fillAmount = Out3(P(t, s.start, s.end));
                float f = Mathf.Sin(Mathf.PI * P(t, s.end - 0.06f, s.end + 0.16f));
                s.img.color = Color.Lerp(s.col, Flash, f);
            }

            // --- title, flag, underline
            float tk = Out3(P(t, 0.25f, 0.50f));
            _titleGroup.alpha = tk;
            Scale(_titleRt, Mathf.Lerp(1.3f, 1f, tk));
            if (_flagRt != null)
            {
                _flagGroup.alpha = P(t, 0.30f, 0.45f);
                float u = P(t, 0.30f, 0.85f);
                Scale(_flagRt, Mathf.Lerp(0.8f, 1f, Out2(u)));
                float damp = (1f - u) * (1f - u);
                _flagRt.localRotation = Quaternion.Euler(0f, 0f, 6f * damp * Mathf.Sin(u * Mathf.PI * 4f));
            }
            _underRt.localScale = new Vector3(Out2(P(t, 0.40f, 0.65f)), 1f, 1f);

            // --- checkpoint rows
            for (int i = 0; i < _rows.Length; i++)
            {
                Row r = _rows[i];
                float s0 = 0.55f + 0.17f * i;
                float k = Out2(P(t, s0, s0 + 0.30f));
                r.g.alpha = k;
                r.rt.anchoredPosition = new Vector2(-24f * (1f - k), r.rt.anchoredPosition.y);
                if (_model != null && _model.Sectors != null && i < _model.Sectors.Length)
                {
                    ResultValue v = _model.Sectors[i];
                    float kc = Out2(P(t, s0 + 0.12f, s0 + 0.37f));
                    int units = v.UnitsAt(kc);
                    if (units != r.shown) { r.shown = units; r.value.text = v.TextForUnits(units); }
                }
            }
            _divRt.localScale = new Vector3(Out2(P(t, 0.95f, 1.20f)), 1f, 1f);

            // --- final time
            float bk = Out2(P(t, 1.10f, 1.40f));
            _timeGroup.alpha = bk;
            Scale(_timeRt, Mathf.Lerp(0.97f, 1f, bk));
            if (_model != null)
            {
                float kc = Out3(P(t, 1.20f, 1.70f));
                if (kc >= 1f) { if (_timeShown != int.MaxValue) { _timeShown = int.MaxValue; _timeText.text = _model.LapText; } }
                else
                {
                    int tenths = (int)(_model.LapSeconds * 10f + 0.0001f);
                    int shown = (int)(tenths * kc);
                    if (shown != _timeShown) { _timeShown = shown; _timeText.text = RenaultRacer.Race.RaceSession.FormatTime(shown / 10f); }
                }
            }
            float gu = P(t, 1.25f, 1.75f);
            float env = Mathf.Sin(Mathf.PI * gu);
            float half = BlockW * 0.5f - 60f;
            SetAlpha(_glintTop, env * 0.95f);
            SetAlpha(_glintBot, env * 0.95f);
            _glintTop.rectTransform.anchoredPosition = new Vector2(Mathf.Lerp(-half, half, gu), 0f);
            _glintBot.rectTransform.anchoredPosition = new Vector2(Mathf.Lerp(half, -half, gu), 0f);

            // --- best row
            bool special = _model != null && _model.Kind != BestKind.Best;     // first record or new best
            bool isNew = _model != null && _model.Kind == BestKind.NewBest;    // the celebration
            float rk = Out2(P(t, 1.60f, 1.85f));
            _bestGroup.alpha = rk;
            float pulse = isNew ? 0.03f * Mathf.Sin(Mathf.PI * P(t, 1.75f, 2.00f)) : 0f;
            Scale(_bestRt, Mathf.Lerp(0.97f, 1f, rk) + pulse);
            float hs = special ? Mathf.Lerp(1.3f, 1f, Out3(P(t, 1.60f, 1.85f))) : 1f;
            Scale(_bestLabelRt, hs);
            if (isNew)
            {
                float hiIn = Out2(P(t, 1.65f, 1.80f));
                float hiOut = Out2(P(t, 1.85f, 2.30f));
                SetAlpha(_bestHi, Mathf.Lerp(0.34f * hiIn, 0.08f, hiOut));
                SetAlpha(_bestFlash, 0.30f * Mathf.Sin(Mathf.PI * P(t, 1.75f, 2.00f)));
            }
            else { SetAlpha(_bestHi, 0f); SetAlpha(_bestFlash, 0f); }

            if (_model != null && _model.BestDiff.Present)
            {
                float kd = Out2(P(t, 1.75f, isNew ? 2.15f : 2.05f));
                ResultValue d = _model.BestDiff;
                float alpha = P(t, 1.70f, 1.85f);
                Color c = ColourFor(d.Tone); c.a = alpha; _bestDiff.color = c;
                int units = d.UnitsAt(kd);
                if (units != _diffShown) { _diffShown = units; _bestDiff.text = d.TextForUnits(units); }
            }
            else if (_bestDiff.text.Length != 0) _bestDiff.text = "";

            // --- play again
            float pk = Out2(P(t, 1.90f, 2.15f));
            float pp = Mathf.Sin(Mathf.PI * P(t, 2.15f, 2.35f));
            _buttonGroup.alpha = pk;
            Scale(_button, Mathf.Lerp(0.94f, 1f, pk) + 0.045f * pp);
            _buttonImg.color = Color.Lerp(UiPrimitives.Yellow, Flash, 0.55f * pp);
        }
    }
}
