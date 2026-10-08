using System.Globalization;
using RenaultRacer.Race;

namespace RenaultRacer.UI
{
    /// <summary>What a results value means, independent of any colour type.</summary>
    public enum ResultTone { Neutral, Gain, Loss, Highlight }

    /// <summary>Which headline the best-time row carries.</summary>
    public enum BestKind { FirstRecord, NewBest, Best }

    /// <summary>
    /// One number on the results card, already decided: its final text, its tone, and
    /// enough to count up to that text exactly. Seconds are held as integer units
    /// (tenths, or hundredths when a nonzero value would round to 0,0), so the count-up
    /// ends on the very string the model built and never on a re-rounded float.
    /// </summary>
    public struct ResultValue
    {
        public bool Present;
        public string Text;
        public ResultTone Tone;
        public int FinalUnits;     // magnitude in 10^-Decimals seconds
        public int Decimals;       // 1 (tenths) or 2 (hundredths)
        public char Sign;          // '+', '-' or '\0' for none

        /// <summary>The units to show when the count-up is <paramref name="k"/> of the way (0..1).</summary>
        public int UnitsAt(float k)
        {
            if (k >= 1f) return FinalUnits;
            if (k <= 0f) return 0;
            return (int)(FinalUnits * k);
        }

        /// <summary>The text for a count-up step. At k = 1 this is exactly <see cref="Text"/>.</summary>
        public string TextAt(float k)
        {
            return TextForUnits(UnitsAt(k));
        }

        public string TextForUnits(int units)
        {
            if (units >= FinalUnits) return Text;
            // A partial count never carries a sign over a zero it has not left yet.
            return ResultsModel.Compose(units == 0 ? '\0' : Sign, units, Decimals);
        }
    }

    /// <summary>
    /// The finish card's content as pure data: no Unity UI, no clocks, so a probe can
    /// assert exactly what the player will read.
    ///
    /// Inputs are the lap time, the sector splits (they sum to the lap - SectorTiming
    /// telescopes) and the <see cref="PersonalBests"/> as snapshotted BEFORE this attempt
    /// - the caller must build the model first and commit the lap afterwards.
    ///
    /// Conventions: positive = time gained, so a faster checkpoint reads "+1,5 s" (green) and a
    /// slower one "-0,4 s" (red). With no baseline the row shows the cumulative time in
    /// neutral white, never an invented +0,0. A nonzero difference is never printed as
    /// 0,0: it gets two decimals. The decimal separator is a comma, and every digit is
    /// assembled by hand so no culture can change it. The minus is ASCII '-': the UI font
    /// subset has no U+2212.
    /// </summary>
    public sealed class ResultsModel
    {
        /// <summary>Differences under this many seconds are float noise, not a result.</summary>
        public const float ZeroEpsilon = 0.0005f;

        public float LapSeconds;
        public string LapText;
        public string[] SectorLabels;
        public ResultValue[] Sectors;
        public bool[] SectorHasBaseline;

        public BestKind Kind;
        public string BestHeading;
        public string BestTimeText;
        public ResultValue BestDiff;       // Present false when there is nothing to compare

        public static ResultsModel Build(float lap, SectorTiming sectors, PersonalBests snapshot)
        {
            int n = sectors != null ? sectors.SectorCount : 0;
            float[] splits = new float[n];
            for (int i = 0; i < n; i++)
                splits[i] = sectors.Completed(i) ? sectors.Split(i) : float.NaN;
            return Build(lap, splits, snapshot);
        }

        public static ResultsModel Build(float lap, float[] splits, PersonalBests snapshot)
        {
            ResultsModel m = new ResultsModel();
            m.LapSeconds = lap;
            m.LapText = RaceSession.FormatTime(lap);

            // Two checkpoint rows: the cumulative time at each sector line (the last sector
            // ends at the finish, which KONČNI ČAS already shows). Compared against the BEST
            // LAP's own cumulative times so rows 1, 2 and the best row share one reference.
            int n = splits != null ? splits.Length - 1 : 0;
            if (n < 0) n = 0;
            m.SectorLabels = new string[n];
            m.Sectors = new ResultValue[n];
            m.SectorHasBaseline = new bool[n];
            float cum = 0f;
            bool lapOk = lap > 0f && !float.IsInfinity(lap);   // NaN fails `> 0f`
            bool valid = lapOk;
            for (int i = 0; i < n; i++)
            {
                m.SectorLabels[i] = UiCopy.CheckpointLabel(i);
                float split = splits[i];
                if (!(split > 0f) || float.IsInfinity(split)) valid = false;
                else cum += split;
                if (valid && cum >= lap) valid = false;     // a checkpoint at or past the finish is not a checkpoint
                if (!valid)
                {
                    m.Sectors[i] = Missing();
                    continue;
                }
                bool has = snapshot != null && snapshot.HasBaselineCheckpoint(i);
                m.SectorHasBaseline[i] = has;
                m.Sectors[i] = has ? Signed(snapshot.CheckpointDelta(i, cum)) : Duration(cum);
            }

            bool hadLap = snapshot != null && snapshot.HasBaselineLap;
            if (!hadLap)
            {
                m.Kind = BestKind.FirstRecord;
                m.BestHeading = UiCopy.BestFirst;
                m.BestTimeText = m.LapText;
            }
            else
            {
                float best = snapshot.BaselineLap;
                if (!lapOk)
                {
                    m.Kind = BestKind.Best;
                    m.BestHeading = UiCopy.BestTitle;
                    m.BestTimeText = RaceSession.FormatTime(best);   // no BestDiff: nothing to compare
                }
                else if (lap < best)
                {
                    m.Kind = BestKind.NewBest;
                    m.BestHeading = UiCopy.BestNew;
                    m.BestTimeText = m.LapText;
                    m.BestDiff = Signed(best - lap);
                }
                else
                {
                    m.Kind = BestKind.Best;
                    m.BestHeading = UiCopy.BestTitle;
                    m.BestTimeText = RaceSession.FormatTime(best);
                    m.BestDiff = Signed(best - lap);
                }
            }
            return m;
        }

        // ------------------------------------------------------------------ values

        private static ResultValue Missing()
        {
            ResultValue v = new ResultValue();
            v.Present = true; v.Text = "--"; v.Tone = ResultTone.Neutral; v.Decimals = 1;
            return v;
        }

        /// <summary>A plain duration, "18,4 s", neutral.</summary>
        public static ResultValue Duration(float seconds)
        {
            ResultValue v = new ResultValue();
            v.Present = true; v.Decimals = 1; v.Tone = ResultTone.Neutral;
            v.FinalUnits = (int)(seconds * 10f + 0.5f);
            v.Text = Compose('\0', v.FinalUnits, 1);
            return v;
        }

        /// <summary>
        /// A signed difference in seconds GAINED. Zero (within noise) is neutral "0,0 s";
        /// a nonzero one never rounds to zero - it falls back to hundredths, at least 0,01.
        /// </summary>
        public static ResultValue Signed(float delta)
        {
            ResultValue v = new ResultValue();
            v.Present = true;
            float mag = delta < 0f ? -delta : delta;
            if (mag < ZeroEpsilon)
            {
                v.Decimals = 1; v.Tone = ResultTone.Neutral; v.Sign = '\0'; v.FinalUnits = 0;
                v.Text = Compose('\0', 0, 1);
                return v;
            }
            v.Sign = delta > 0f ? '+' : '-';
            v.Tone = delta > 0f ? ResultTone.Gain : ResultTone.Loss;
            int tenths = (int)(mag * 10f + 0.5f);
            if (tenths > 0) { v.Decimals = 1; v.FinalUnits = tenths; }
            else
            {
                v.Decimals = 2;
                int h = (int)(mag * 100f + 0.5f);
                v.FinalUnits = h < 1 ? 1 : h;
            }
            v.Text = Compose(v.Sign, v.FinalUnits, v.Decimals);
            return v;
        }

        /// <summary>Sign, whole seconds, a comma, the fraction, " s" - culture-proof.</summary>
        public static string Compose(char sign, int units, int decimals)
        {
            int div = decimals == 2 ? 100 : 10;
            int whole = units / div;
            int frac = units % div;
            string s = whole.ToString(CultureInfo.InvariantCulture) + ",";
            s += decimals == 2 ? (frac < 10 ? "0" : "") + frac.ToString(CultureInfo.InvariantCulture)
                               : frac.ToString(CultureInfo.InvariantCulture);
            s += " s";
            return sign != '\0' ? sign + s : s;
        }
    }
}
