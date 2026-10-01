using RenaultRacer.Track;

namespace RenaultRacer.Race
{
    /// <summary>
    /// One race attempt: the timer, the checkpoints and the finish test
    /// (plan section 12). V1 is a single lap (plan 3.1).
    ///
    /// Pure class, no Unity dependencies, exactly like CarSimulation - so the race
    /// rules can be verified without entering Play Mode. Allocates only in the
    /// constructor.
    ///
    /// WHY THE FINISH TEST IS SO SIMPLE. The reference game needed wrap-safe
    /// forward-crossing logic because its position wrapped and its car could be
    /// nudged backwards over the line. Ours cannot: CarSimulation clamps Speed to
    /// >= 0, so Z is monotonically non-decreasing, and Z is never wrapped (only
    /// lookups wrap). For a single lap that reduces "did we finish" to "is Z past
    /// the end", which cannot be fooled by stopping or sitting on the line.
    /// If V1 ever grows a second lap this assumption must be revisited.
    /// </summary>
    public sealed class RaceSession
    {
        private readonly TrackModel _track;
        private readonly float[] _checkpointZ;   // ascending, one per checkpoint
        private readonly float _maxPlausibleSpeed;
        private readonly SectorTiming _sectors;
        private float _lastZ;

        /// <summary>
        /// How much further than physically possible a single tick may advance before
        /// we stop believing it. Generous, because this is a sanity check on the
        /// caller, not a precision measurement.
        /// </summary>
        private const float ContinuityTolerance = 1.5f;

        /// <summary>Elapsed race time, accumulated in whole fixed steps.</summary>
        public float ElapsedSeconds { get; private set; }

        /// <summary>How many checkpoints have been passed, in order.</summary>
        public int CheckpointsPassed { get; private set; }

        public int CheckpointCount { get { return _checkpointZ.Length; } }

        /// <summary>True once the car has crossed the line.</summary>
        public bool Finished { get; private set; }

        /// <summary>True while the timer is running.</summary>
        public bool Running { get; private set; }

        /// <summary>Identifies this attempt end to end; sent with the result in Phase 12.</summary>
        public string SessionId { get; private set; }

        /// <summary>Set when the race actually started from a countdown (plan 13 item 4).</summary>
        public bool StartedFromCountdown { get; private set; }

        /// <summary>0..1 progress around the lap - drives the Phase 7 minimap.</summary>
        public float LapProgress { get; private set; }

        /// <summary>
        /// This attempt's sector splits. Never null, and never a second clock: it is
        /// handed <see cref="ElapsedSeconds"/> and the same <c>lastZ -> z</c> pair the
        /// checkpoints are tested against, on the same tick. See <see cref="SectorTiming"/>.
        /// </summary>
        public SectorTiming Sectors { get { return _sectors; } }

        /// <summary>Sector the car is in, 0-based. Convenience for the HUD.</summary>
        public int CurrentSector { get { return _sectors.CurrentSector; } }

        /// <summary>Sector lines crossed so far this attempt.</summary>
        public int SectorsCompleted { get { return _sectors.SectorsCompleted; } }

        /// <summary>
        /// Sum of the closed sector splits. Equal to <see cref="ElapsedSeconds"/> once the
        /// lap is finished, to within float rounding - the splits telescope.
        /// </summary>
        public float SectorSplitTotal
        {
            get
            {
                float total = 0f;
                for (int i = 0; i < _sectors.SectorsCompleted; i++) total += _sectors.Split(i);
                return total;
            }
        }

        /// <summary>
        /// Set if position ever jumped further in one tick than the car could possibly
        /// travel. Legitimate driving advances by at most maxSpeed*dt; anything larger
        /// means Z did not come from the simulation.
        /// </summary>
        public bool ContinuityBroken { get; private set; }

        /// <param name="maxPlausibleSpeed">
        /// The car's top speed. Used only to sanity-check that position advances
        /// continuously - see <see cref="ContinuityBroken"/>.
        /// </param>
        public RaceSession(TrackModel track, float maxPlausibleSpeed)
        {
            _track = track;
            _maxPlausibleSpeed = maxPlausibleSpeed;

            // Find the checkpoints once. They are authored by TrackBuilder.PlaceGates
            // and never move, so this is the only allocation this class ever makes.
            int count = 0;
            for (int i = 0; i < track.Count; i++)
                if (track[i].checkpointId >= 0) count++;

            _checkpointZ = new float[count];
            for (int i = 0; i < track.Count; i++)
            {
                int id = track[i].checkpointId;
                if (id >= 0 && id < count)
                    _checkpointZ[id] = i * RoadGeometry.SegmentLength;
            }

            // The sector lines come from the track's own authored layout, which is never
            // null (TrackModel guarantees it), so retuning the circuit moves them without
            // a line changing here. Built once, with everything else this class allocates.
            _sectors = new SectorTiming(track.Layout, track.Length);
        }

        /// <summary>Arm a fresh attempt. Called when the countdown begins.</summary>
        public void Begin()
        {
            ElapsedSeconds = 0f;
            CheckpointsPassed = 0;
            LapProgress = 0f;
            _sectors.Reset();          // restart clears this attempt's splits
            _lastZ = 0f;
            ContinuityBroken = false;
            Finished = false;
            Running = false;
            StartedFromCountdown = false;
            SessionId = System.Guid.NewGuid().ToString("N");
        }

        /// <summary>The countdown has gone green: start timing.</summary>
        public void StartRacing()
        {
            Running = true;
            StartedFromCountdown = true;
        }

        /// <summary>
        /// Advance the race. <paramref name="dt"/> must be a whole number of fixed
        /// simulation steps, so the clock cannot drift with frame rate (plan 12).
        /// </summary>
        public void Tick(float dt, float z)
        {
            if (!Running || Finished) return;

            ElapsedSeconds += dt;
            LapProgress = _track.Length > 0f ? UnityEngine.Mathf.Clamp01(z / _track.Length) : 0f;

            // Position must advance CONTINUOUSLY. At top speed a tick covers
            // maxSpeed*dt; the checkpoints are tens of thousands of units apart. A
            // larger jump did not come from the simulation.
            float advance = z - _lastZ;
            float maxAdvance = _maxPlausibleSpeed * dt * ContinuityTolerance
                             + RoadGeometry.SegmentLength;
            if (advance > maxAdvance) ContinuityBroken = true;

            // Credit a checkpoint only when this tick actually CROSSED it, and never
            // more than one per tick.
            //
            // The obvious "while (z >= next) pass++" is wrong and was caught by
            // RaceProbe test [3]: a single jump to the finish line satisfies every
            // checkpoint at once, so the check that exists to catch skipping the lap
            // would have waved it straight through.
            if (CheckpointsPassed < _checkpointZ.Length)
            {
                float next = _checkpointZ[CheckpointsPassed];
                if (_lastZ < next && z >= next) CheckpointsPassed++;
            }

            // Sector lines, on the SAME tick, from the SAME pair of positions and the
            // SAME clock. Deliberately before `_lastZ = z` and after ElapsedSeconds has
            // been advanced, so the last gate - the finish line - reads exactly the time
            // this lap is recorded at and the three splits add up to it.
            _sectors.Tick(_lastZ, z, ElapsedSeconds);

            _lastZ = z;

            if (z >= _track.Length)
            {
                Finished = true;
                Running = false;
                LapProgress = 1f;
            }
        }

        /// <summary>
        /// mm:ss.f - the format shown to the player, and the basis of the string the
        /// backend is sent (plan 2.1, 2.5 item 2).
        ///
        /// **Culture-invariant on purpose.** `rest.ToString("00.0")` follows the running
        /// culture's decimal separator, so the same lap reads "43.8" on one machine and
        /// "43,8" on another - and a browser's culture is the player's, not ours. As a
        /// display quirk that is merely inconsistent; in a value that ends up in a POST
        /// body it is a parsing bug waiting for a Slovenian phone to find it. The digits
        /// are assembled by hand instead, so the output cannot depend on a locale.
        /// </summary>
        public static string FormatTime(float seconds)
        {
            if (seconds < 0f) seconds = 0f;

            int tenths = (int)(seconds * 10f + 0.0001f);
            int minutes = tenths / 600;
            int secs = (tenths / 10) % 60;
            int tenth = tenths % 10;

            return (minutes < 10 ? "0" : "") + minutes
                 + ":" + (secs < 10 ? "0" : "") + secs
                 + "." + tenth;
        }
    }
}
