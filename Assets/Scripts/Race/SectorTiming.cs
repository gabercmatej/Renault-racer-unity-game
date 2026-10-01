using RenaultRacer.Track;

namespace RenaultRacer.Race
{
    /// <summary>
    /// The three sector splits of one lap attempt.
    ///
    /// Pure class, no Unity dependencies, exactly like <see cref="RaceSession"/> - the
    /// gate rules can be verified without entering Play Mode.
    ///
    /// TWO THINGS THIS TYPE EXISTS TO GUARANTEE.
    ///
    /// **1. One clock.** A sector time is never measured; it is a DIFFERENCE between two
    /// readings of <see cref="RaceSession.ElapsedSeconds"/>, which advances in whole fixed
    /// simulation steps. There is no Time.deltaTime here, no stopwatch of its own and no
    /// second accumulator that could drift away from the lap clock at a different frame
    /// rate. That is also what makes the splits sum to the lap time: the sum telescopes,
    /// because every split is (elapsed at this line) - (elapsed at the previous one) and
    /// the last line IS the finish line.
    ///
    /// **2. One crossing, in order.** The gate test is the same "did this tick actually
    /// cross it" form <see cref="RaceSession"/> uses for checkpoints:
    /// <c>lastZ &lt; gate &amp;&amp; z &gt;= gate</c>, against ONE gate - the next one - per
    /// tick. The obvious <c>while (z &gt;= next) close++</c> is wrong for exactly the
    /// reason RaceProbe test [3] caught it on the checkpoints: a single jump to the finish
    /// line satisfies every gate at once, so sector 1 and sector 2 would be "crossed"
    /// retrospectively with a split of zero and the lap would look sector-legal. Testing
    /// only the next gate makes skipping one impossible - if the car never crosses line 2,
    /// line 3 is never even examined - and makes a second crossing of the same line
    /// impossible, because the index has already moved on.
    ///
    /// Where the lines ARE is not this type's business: they come from
    /// <see cref="TrackLayout.SectorStartSegment"/>, which TrackBuilder authors. Nothing
    /// here is a hand-typed distance, so retuning the circuit moves the sector lines with
    /// it automatically.
    /// </summary>
    public sealed class SectorTiming
    {
        /// <summary>Distance along the lap of the line that CLOSES each sector.</summary>
        private readonly float[] _gateZ;

        /// <summary>ElapsedSeconds at which each sector closed. Only valid below _next.</summary>
        private readonly float[] _closedAt;

        private readonly float[] _split;

        private int _next;

        /// <summary>
        /// Build the gates from an authored layout. Sector i is closed by the line that
        /// OPENS sector i+1, and the last sector is closed by the finish line - which is
        /// why there are exactly as many gates as sectors and the last one is the lap
        /// length. A layout with no sector lines degrades to one sector covering the whole
        /// lap, so callers never have to guard against a track built without a layout.
        /// </summary>
        public SectorTiming(TrackLayout layout, float lapLength)
        {
            int[] starts = (layout != null) ? layout.SectorStartSegment : null;
            int count = (starts != null && starts.Length > 0) ? starts.Length : 1;

            _gateZ = new float[count];
            for (int i = 0; i < count - 1; i++)
                _gateZ[i] = starts[i + 1] * RoadGeometry.SegmentLength;
            _gateZ[count - 1] = lapLength;

            // A layout whose lines are not strictly ascending, or which puts one at or
            // past the finish, is a TrackBuilder bug rather than something to time
            // around. Collapse to a single sector rather than produce splits nobody can
            // interpret - and say so once, loudly, at construction.
            if (!Ascending(_gateZ, lapLength))
            {
                UnityEngine.Debug.LogError(
                    "[SectorTiming] sector lines are not strictly ascending inside the lap - "
                    + "treating the lap as a single sector. Check TrackLayout.SectorStartSegment.");
                _gateZ = new float[] { lapLength };
                count = 1;
            }

            _closedAt = new float[count];
            _split = new float[count];
        }

        private static bool Ascending(float[] gates, float lapLength)
        {
            if (lapLength <= 0f) return false;
            float previous = 0f;
            for (int i = 0; i < gates.Length; i++)
            {
                if (gates[i] <= previous) return false;
                if (gates[i] > lapLength) return false;
                previous = gates[i];
            }
            return true;
        }

        /// <summary>How many sectors this lap is divided into. Always at least 1.</summary>
        public int SectorCount { get { return _gateZ.Length; } }

        /// <summary>Sectors closed so far this attempt, 0..<see cref="SectorCount"/>.</summary>
        public int SectorsCompleted { get { return _next; } }

        /// <summary>
        /// The sector the car is in, 0-based. Clamped at the last one, so it stays
        /// meaningful on the tick the lap finishes.
        /// </summary>
        public int CurrentSector
        {
            get { return _next < _gateZ.Length ? _next : _gateZ.Length - 1; }
        }

        /// <summary>
        /// Increments once per sector line crossed. The HUD polls this instead of
        /// subscribing to an event: a delegate call per tick would allocate a closure and
        /// an int comparison does not (plan 17.5).
        /// </summary>
        public int CrossCount { get; private set; }

        /// <summary>The sector closed by the most recent crossing, or -1.</summary>
        public int LastClosedSector { get; private set; }

        /// <summary>The split of <see cref="LastClosedSector"/>, seconds.</summary>
        public float LastSplit { get; private set; }

        /// <summary>True once sector <paramref name="sector"/> has been closed this attempt.</summary>
        public bool Completed(int sector)
        {
            return sector >= 0 && sector < _next;
        }

        /// <summary>
        /// Sector <paramref name="sector"/>'s split in seconds, or 0 if it has not been
        /// closed yet. Splits sum to the lap time.
        /// </summary>
        public float Split(int sector)
        {
            return Completed(sector) ? _split[sector] : 0f;
        }

        /// <summary>Elapsed race time at which a closed sector's line was crossed.</summary>
        public float ClosedAt(int sector)
        {
            return Completed(sector) ? _closedAt[sector] : 0f;
        }

        /// <summary>Distance along the lap of the line that closes a sector.</summary>
        public float GateZ(int sector)
        {
            return (sector >= 0 && sector < _gateZ.Length) ? _gateZ[sector] : 0f;
        }

        /// <summary>Arm a fresh attempt. Called from <see cref="RaceSession.Begin"/>.</summary>
        public void Reset()
        {
            _next = 0;
            CrossCount = 0;
            LastClosedSector = -1;
            LastSplit = 0f;
            for (int i = 0; i < _split.Length; i++)
            {
                _split[i] = 0f;
                _closedAt[i] = 0f;
            }
        }

        /// <summary>
        /// Offer one simulation tick. <paramref name="lastZ"/> and <paramref name="z"/> are
        /// the SAME pair <see cref="RaceSession"/> uses for its checkpoints, and
        /// <paramref name="elapsed"/> is the lap clock AFTER this tick's dt has been added,
        /// so the final gate reads exactly the time the lap is recorded at.
        /// Returns true on the tick a sector line is crossed.
        /// </summary>
        public bool Tick(float lastZ, float z, float elapsed)
        {
            if (_next >= _gateZ.Length) return false;

            float gate = _gateZ[_next];
            if (!(lastZ < gate && z >= gate)) return false;

            _closedAt[_next] = elapsed;
            _split[_next] = _next == 0 ? elapsed : elapsed - _closedAt[_next - 1];

            LastClosedSector = _next;
            LastSplit = _split[_next];
            _next++;
            CrossCount++;
            return true;
        }
    }
}
