using UnityEngine;
using UnityEngine.UI;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The sector split readout: a small pill under the lap clock that appears when a
    /// sector line is crossed, says how the sector compared with the player's own best,
    /// and gets out of the way.
    ///
    /// SIGN CONVENTION, and it is the whole point of the feature: **positive is time
    /// gained**. A sector 1.4 s faster than your best reads <c>+1.4 s</c> in green; 1.6 s
    /// slower reads <c>-1.6 s</c> in red. That is the opposite of a stopwatch's arithmetic
    /// and the same as every "you are up on your best" readout a player has ever seen, so
    /// it is chosen for the reader rather than for the subtraction.
    ///
    /// WITH NO BASELINE THERE IS NO NUMBER. The first valid lap on a circuit has nothing
    /// to be compared with, so the pill shows the sector and a plain <c>--</c>, tagged
    /// NEW. Showing "+0.0" or comparing against zero would be a fabricated result, and a
    /// fabricated result in the one element whose entire job is to be trusted.
    ///
    /// ALLOCATION. Nothing on the racing path builds a string. Both labels are served from
    /// tables built once: the sector names, and every tenth of a second from 0.0 to 29.9
    /// with its sign already attached. Beyond that the two overflow constants say so
    /// rather than pretending to a tenth. The tables are assembled digit by digit, never
    /// through a float's ToString, for the reason spelled out on
    /// <see cref="RenaultRacer.Race.RaceSession.FormatTime"/>: the running culture's
    /// decimal separator is the player's, not ours, and a comma would appear on a
    /// Slovenian phone and nowhere in testing.
    /// </summary>
    public sealed class SectorDeltaView : MonoBehaviour
    {
        /// <summary>Seconds at full opacity before the fade starts.</summary>
        public const float HoldSeconds = 1.8f;

        /// <summary>Seconds the fade itself takes. Total on screen is Hold + Fade.</summary>
        public const float FadeSeconds = 0.7f;

        /// <summary>Largest delta shown to a tenth. Above this the overflow text is used.</summary>
        private const int MaxTenths = 300;          // 29.9 s

        /// <summary>Time GAINED. Green.</summary>
        public static readonly Color Gain = new Color(0.25f, 0.85f, 0.48f, 1f);   // #3FD87A

        /// <summary>Time LOST. Red - the sector-3 red off the art sheet, lifted enough to
        /// stay legible on the charcoal pill.</summary>
        public static readonly Color Loss = new Color(1f, 0.18f, 0.22f, 1f);      // #FF2E38

        /// <summary>No baseline yet, so no verdict. Silver.</summary>
        public static readonly Color Neutral = UiPrimitives.Silver;

        private static string[] _plus;              // "+0.0 s" .. "+29.9 s"
        private static string[] _minus;             // "-0.0 s" .. "-29.9 s"
        private static string[] _sectorName;        // "S1" ..
        private const string OverflowPlus = "> +30 s";
        private const string OverflowMinus = "< -30 s";
        private const string NoBaseline = "--";

        /// <summary>
        /// DRAFT copy, and deliberately NOT in <see cref="UiCopy"/> yet: that file is the
        /// client's sign-off surface (M7) and is owned elsewhere this cycle. "NOVO" is the
        /// Slovenian for the NEW tag the brief's minimap sheet shows in English; it is
        /// plain ASCII, so it is inside the subset the UI font ships with. Move it to
        /// UiCopy with the rest of the copy review.
        /// </summary>
        private const string NewTag = "NOVO";

        private CanvasGroup _group;
        private Text _label;
        private Text _value;
        private Text _tag;                          // "NEW", only on a first-ever sector
        private float _age = -1f;                   // <0 means nothing is showing
        private float _alpha = -1f;

        // Layout, UI units at the 1280x720 reference. The timer panel is 72 tall, hung
        // 10 below the top edge, so its bottom sits at -82; this clears it by 6.
        private static readonly Vector2 PillSize = new Vector2(168f, 34f);
        private const float TopOffset = -88f;

        /// <summary>
        /// Build the pill under <paramref name="parent"/>. The caller is expected to be
        /// inside the safe area already - HudView builds under the same host GameUi gives
        /// every panel, so a notch pushes this down with everything else.
        /// </summary>
        public void Build(Transform parent, int sectorCount)
        {
            BuildStringTables(sectorCount);

            GameObject go = new GameObject("SectorDelta", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rr = (RectTransform)go.transform;
            UiPrimitives.Place(rr, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                               new Vector2(0f, TopOffset), PillSize);

            _group = go.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;
            _group.alpha = 0f;

            Image body = UiPrimitives.Art(rr, "Body", "rr_fill", new Color(0.067f, 0.075f, 0.09f, 0.88f),
                                          UiPrimitives.Box, new Color(0.067f, 0.075f, 0.09f, 0.88f));
            UiPrimitives.Fill(body.rectTransform, 0f);

            _label = UiPrimitives.Label(rr, "Sector", _sectorName[0], 20,
                                        TextAnchor.MiddleLeft, Color.white);
            UiPrimitives.Place(_label.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                               new Vector2(12f, 0f), new Vector2(46f, 26f));

            _value = UiPrimitives.Label(rr, "Delta", NoBaseline, 24,
                                        TextAnchor.MiddleRight, Neutral);
            UiPrimitives.Place(_value.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                               new Vector2(-12f, 0f), new Vector2(110f, 28f));

            _tag = UiPrimitives.BodyLabel(rr, "New", NewTag, 13,
                                          TextAnchor.MiddleCenter, UiPrimitives.Yellow);
            UiPrimitives.Place(_tag.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f),
                               new Vector2(0f, -2f), new Vector2(120f, 18f));
            _tag.gameObject.SetActive(false);
        }

        /// <summary>
        /// A sector line has been crossed. <paramref name="delta"/> is seconds GAINED
        /// against the pre-attempt best; <paramref name="hasBaseline"/> is false on the
        /// first lap, when there is nothing to compare with and no number is shown.
        /// Zero allocation: both labels are assigned from the tables.
        /// </summary>
        public void Show(int sector, float delta, bool hasBaseline)
        {
            if (_value == null) return;

            _label.text = _sectorName[Mathf.Clamp(sector, 0, _sectorName.Length - 1)];

            _value.text = TextFor(delta, hasBaseline);
            _value.color = ColourFor(delta, hasBaseline);
            _tag.gameObject.SetActive(!hasBaseline);

            _age = 0f;
            SetAlpha(1f);
        }

        /// <summary>
        /// Exactly the string <see cref="Show"/> would put on screen. Public and static so
        /// SectorProbe can assert the READOUT, not just the arithmetic behind it - "+1.4 s
        /// in green" is the requirement, and a test that only checks the sign of a float
        /// would pass with the sign convention inverted on screen.
        /// </summary>
        public static string TextFor(float delta, bool hasBaseline)
        {
            if (!hasBaseline) return NoBaseline;

            EnsureDeltaTable();
            bool gained = delta >= 0f;
            // Round half-up on the MAGNITUDE, so the tenth shown is the same distance
            // from zero whichever side of it the delta falls.
            int tenths = Mathf.RoundToInt(Mathf.Abs(delta) * 10f);
            if (tenths >= MaxTenths) return gained ? OverflowPlus : OverflowMinus;
            return gained ? _plus[tenths] : _minus[tenths];
        }

        /// <summary>The colour <see cref="Show"/> would use. See <see cref="TextFor"/>.</summary>
        public static Color ColourFor(float delta, bool hasBaseline)
        {
            if (!hasBaseline) return Neutral;
            return delta >= 0f ? Gain : Loss;
        }

        /// <summary>
        /// Age the pill. <paramref name="unscaledDt"/> because it must keep fading if the
        /// simulation is stopped underneath it. Costs nothing once the pill is hidden.
        /// </summary>
        public void Tick(float unscaledDt)
        {
            if (_age < 0f) return;

            _age += unscaledDt;
            if (_age <= HoldSeconds) return;

            float k = 1f - (_age - HoldSeconds) / FadeSeconds;
            if (k <= 0f)
            {
                _age = -1f;
                SetAlpha(0f);
                return;
            }
            SetAlpha(k);
        }

        /// <summary>Hide it immediately - a restart must not leave the last lap's split up.</summary>
        public void ResetDisplay()
        {
            _age = -1f;
            SetAlpha(0f);
            if (_tag != null) _tag.gameObject.SetActive(false);
        }

        private void SetAlpha(float a)
        {
            // CanvasGroup marks its children dirty on every assignment, so only write when
            // the value the player can see has actually moved.
            if (_group == null || Mathf.Abs(a - _alpha) < 0.004f) return;
            _alpha = a;
            _group.alpha = a;
        }

        // ------------------------------------------------------------------ strings

        private static void EnsureDeltaTable()
        {
            if (_plus != null) return;

            _plus = new string[MaxTenths];
            _minus = new string[MaxTenths];
            char[] buf = new char[10];
            for (int t = 0; t < MaxTenths; t++)
            {
                _plus[t] = Compose(buf, '+', t);
                _minus[t] = Compose(buf, '-', t);
            }
        }

        private static void BuildStringTables(int sectorCount)
        {
            EnsureDeltaTable();

            int n = Mathf.Max(1, sectorCount);
            if (_sectorName != null && _sectorName.Length == n) return;
            _sectorName = new string[n];
            for (int i = 0; i < n; i++) _sectorName[i] = "S" + Digits[(i + 1) % 10];
        }

        private const string Digits = "0123456789";

        /// <summary>
        /// Sign, whole seconds, a point, a tenth, then " s" - assembled a character at a
        /// time. No float formatting and no int.ToString, so no culture can put a comma
        /// or a different digit glyph in it.
        /// </summary>
        private static string Compose(char[] buf, char sign, int tenths)
        {
            int whole = tenths / 10;
            int i = 0;
            buf[i++] = sign;
            if (whole >= 10) buf[i++] = Digits[whole / 10];
            buf[i++] = Digits[whole % 10];
            buf[i++] = '.';
            buf[i++] = Digits[tenths % 10];
            buf[i++] = ' ';
            buf[i++] = 's';
            return new string(buf, 0, i);
        }
    }
}
