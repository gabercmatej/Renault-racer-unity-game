using UnityEngine;
using RenaultRacer.Track;

namespace RenaultRacer.Race
{
    /// <summary>One authored pickup: where it goes, relative to the circuit's own layout.</summary>
    [System.Serializable]
    public struct BoostPlacement
    {
        [Tooltip("Turn number (1-based) whose EXIT this follows. 0 = the start/finish line.")]
        public int afterTurn;

        [Tooltip("Segments after that turn's exit (or after the line). One segment is one metre.")]
        public int segmentsAfter;

        [Tooltip("Lateral position, road half-widths, +1 = right edge. Ignored when autoLine is on.")]
        [Range(-0.9f, 0.9f)] public float lateral;

        [Tooltip("Put it where the line runs for the NEXT corner: right of centre before a " +
                 "left-hander, left before a right-hander, centre on the run to the flag.")]
        public bool autoLine;

        public BoostPlacement(int afterTurn, int segmentsAfter, bool autoLine, float lateral = 0f)
        {
            this.afterTurn = afterTurn; this.segmentsAfter = segmentsAfter;
            this.autoLine = autoLine; this.lateral = lateral;
        }
    }

    /// <summary>Every number the lightning boost is tuned by, in one serialisable place.</summary>
    [System.Serializable]
    public sealed class BoostTuning
    {
        [Tooltip("Top speed added at full boost, fraction of maxSpeed. 0.12 = 220 -> 246 km/h.")]
        [Range(0f, 0.3f)] public float addedTopSpeed = 0.12f;

        [Tooltip("Acceleration added at full boost, fraction of maxSpeed per second. Tapered " +
                 "into the boosted ceiling, so it is a kick that arrives rather than a wall.")]
        [Range(0f, 1f)] public float addedAccel = 0.25f;

        [Tooltip("Seconds at full boost.")]
        [Range(0.2f, 6f)] public float holdSeconds = 2.5f;

        [Tooltip("Seconds over which the ceiling smoothsteps back to maxSpeed. The car is " +
                 "brought down by the ceiling, so this IS the deceleration: 1.0 s peaks at " +
                 "about a third of full braking.")]
        [Range(0.1f, 3f)] public float fadeSeconds = 1.0f;

        [Tooltip("Collection half-width, road half-widths. The car is 0.35 half-widths either " +
                 "side of its centre and the bolt's body ~0.10 at the car, so 0.45 is where the " +
                 "two are drawn touching. 2026-10-08: 0.40 (was 0.45) - the car has to overlap the " +
                 "bolt's body by ~0.05, a clear touch, not a graze; with lane 0.77 a side pickup " +
                 "needs the car centre 0.37 off the centre line (the 0.82-lane demand at 0.45).")]
        [Range(0.1f, 1f)] public float radius = 0.40f;

        [Tooltip("How far off centre an autoLine pickup sits, road half-widths. The same " +
                 "as the Left/Right lanes, so a car on the centre line misses it.")]
        [Range(0f, 0.9f)] public float lineOffset = Lane;

        /// <summary>
        /// The three pickup lanes, road half-widths (+1 = right edge), chosen against
        /// <see cref="radius"/>. 0.35 sat inside it (every pickup taken from the centre);
        /// 0.55 cleared it by only 0.10, so a car a little off centre still took a side
        /// pickup without meaning to (Maj, 2026-10-07: too easy). At 0.70 the car's centre
        /// has to be 0.25 half-widths off the centre line - a deliberate lane change - and
        /// centred on the pickup its own body (0.35 each side) still has a wheel on the
        /// tarmac. 2026-10-08: 0.75 (was 0.70) - the car's centre has to be 0.30 off the
        /// centre line, and a car centred on the pickup is still on asphalt (the kerb
        /// starts at 0.92, the tyre wall is out in the run-off beyond 1.0). The bolt's body, drawn at its distance size, stays inside the road edge
        /// all the way in (BoostPickups pulls it in while the size floor enlarges it;
        /// PickupVisibilityProbe [asphalt]). 2026-10-08 (second pass, Maj: side pickups still too easy): 0.77 (was 0.75). The car's
        /// centre has to be 0.32 off the centre line (lane - radius). 0.82-0.85 was tried and
        /// rejected on geometry: the kerb starts at 0.92 and the bolt body is ~0.10 wide at the car,
        /// but the bolt's foot, drawn on a nearer road row while it is lifted and size-floored,
        /// reaches 1.02-1.04 of that row's half-width at 0.80-0.82 (PickupVisibilityProbe [asphalt]
        /// fails on #3, the descent after T14); 0.78 is 1.00, 0.77 clears it. The clamp in FromLayout
        /// was 0.8 and is now 0.9 so a wider lane is not silently cut back.
        /// The extra lane change Maj asked for is taken from the radius instead (0.45 -> 0.40):
        /// lane - radius = 0.37.
        /// </summary>
        public const float Lane = 0.77f;
        public const float Left = -Lane, Centre = 0f, Right = Lane;

        public BoostPlacement[] placements = DefaultPlacements();

        /// <summary>
        /// Five, all on TRUE straights (curve exactly 0 - TrackLayoutProbe [T2]) - the car
        /// is drawn without the road's accumulated curve, so on a straight the drawn car
        /// and the drawn pickup agree exactly - and FromLayout holds each at least
        /// ClearSegments clear of the corners either side.
        ///
        /// Spread over all three lanes (2 left, 1 centre, 2 right) so the line has to be
        /// CHOSEN. Every side change is timed against the measured handling, not guessed:
        /// a full-lock press moved the car 1.1 half-widths (the old lane to lane) in about
        /// 0.5 s, ~30 segments at top speed (lanes are 1.50 apart since 2026-10-08, but the
        /// car only has to reach lane - radius, 0.30 from centre), and every pickup that sits on the far side from the
        /// corner it follows has more than 2x that from the corner's exit. None follows a
        /// blind crest (PickupVisibilityProbe: readable >= 1.5 s, continuously) and none
        /// sits in front of a braking corner close enough to carry the boost into it (#3,
        /// 28 segments before T15, is where it always was).
        /// </summary>
        public static BoostPlacement[] DefaultPlacements()
        {
            return new[]
            {
                // seg   90, pit straight: from the grid (x 0) to the T01 (R) outside, at
                //            launch speed. Left.
                new BoostPlacement(0, 90, false, Left),
                // seg 1459, the T08 exit climb: T08 (R) exits wide LEFT, so the pickup is
                //            the middle of the road 45 segments on - reachable from any exit,
                //            missed by a car still hugging the left edge. Centre. Not nearer
                //            the exit: 1439-1444 sit on the summit's crest line and drop out
                //            of view mid-approach.
                new BoostPlacement(8, 45, false, Centre),
                // seg 1868, the long T11-T12 descent: T11 (R) exits left and the pickup is
                //            on the right, 115 segments (~1.9 s) on - the setup side for
                //            T12 (L). Right. Low on the descent on purpose: 1808-1858 are
                //            hidden by the T11 crest until ~1.2 s out.
                new BoostPlacement(11, 115, false, Right),
                // seg 2367, after the T14 hairpin, far enough down the descent to be seen
                //            past it, on the outside line for T15 (R). Left.
                new BoostPlacement(14, 95, false, Left),
                // seg 2578, the run to the flag: T15 (R) exits left, the pickup is right,
                //            70 segments (~1.2 s) on. Right.
                new BoostPlacement(15, 70, false, Right),
            };
        }

        /// <summary>
        /// Upper bound on the lap time boosts can save against the validator's floor lap
        /// (the whole lap at maxSpeed). A boost window of T seconds runs at no more than
        /// (1 + f) x maxSpeed, so it saves at most f x T; re-triggering only shortens the
        /// previous window, so the windows cannot add up to more than this.
        /// </summary>
        public float MaxSecondsSaved(int pickupsPerLap)
        {
            return pickupsPerLap * Mathf.Max(0f, addedTopSpeed) * (holdSeconds + fadeSeconds);
        }
    }

    /// <summary>
    /// Where the lightning pickups are, which have been taken, and the one test that takes
    /// them. Pure, exactly like RaceSession: no Unity objects, allocates only when built,
    /// so the rule can be verified in batch mode without a scene.
    ///
    /// A pickup is taken once PER LAP: _takenOnLap[i] holds the lap index it was taken on,
    /// so it is back the next time round, and ResetAll (race restart) brings every one
    /// back. The race is <see cref="Laps"/> long (V1: one, RaceSession), and a lap past
    /// it is neither drawn nor taken - so in V1 a taken pickup is gone for the rest of
    /// the race, and the start-area pickups of a "lap 2" that will never be raced do not
    /// show up ahead on the run to the flag (the road itself draws over the line).
    /// </summary>
    public sealed class BoostPickupSet
    {
        private const int NotTaken = int.MinValue;

        /// <summary>Segments a pickup keeps clear of any corner's ramps.</summary>
        public const int ClearSegments = 12;

        private readonly int[] _segment;
        private readonly float[] _z;          // absolute z on lap 0
        private readonly float[] _x;          // normalised, +1 = right edge
        private readonly int[] _takenOnLap;
        private readonly float _length;

        /// <summary>Collection half-width, road half-widths. Live-tunable.</summary>
        public float Radius;

        /// <summary>
        /// Laps in the race. RaceSession is a single lap (plan 3.1); if that ever changes,
        /// set this from the same source.
        /// </summary>
        public int Laps = 1;

        public int Count { get { return _z.Length; } }
        public int TakenCount { get; private set; }
        public int LastTaken { get; private set; }

        public int SegmentOf(int i) { return _segment[i]; }
        public float LateralOf(int i) { return _x[i]; }
        public float ZOf(int i) { return _z[i]; }
        public bool TakenOnLap(int i, int lap) { return _takenOnLap[i] == lap; }

        /// <summary>
        /// Is pickup <paramref name="i"/>'s copy on <paramref name="lap"/> still there to be
        /// drawn and taken? Not if it was taken on that lap, and never on a lap the race
        /// does not have.
        /// </summary>
        public bool IsLive(int i, int lap) { return lap >= 0 && lap < Laps && _takenOnLap[i] != lap; }

        public BoostPickupSet(int[] segments, float[] lateral, float trackLength, float radius)
        {
            int n = segments != null ? segments.Length : 0;
            _segment = new int[n];
            _z = new float[n];
            _x = new float[n];
            _takenOnLap = new int[n];
            for (int i = 0; i < n; i++)
            {
                _segment[i] = segments[i];
                _z[i] = segments[i] * RoadGeometry.SegmentLength;
                _x[i] = lateral != null && i < lateral.Length ? lateral[i] : 0f;
            }
            _length = trackLength;
            Radius = radius;
            ResetAll();
        }

        /// <summary>Everything back. Race restart.</summary>
        public void ResetAll()
        {
            for (int i = 0; i < _takenOnLap.Length; i++) _takenOnLap[i] = NotTaken;
            TakenCount = 0;
            LastTaken = -1;
        }

        /// <summary>
        /// Did the car cross a pickup between these two positions? Returns its index, or -1.
        /// Called once per SIMULATION step, so it is the same step at any frame rate. The
        /// lateral test is made at the crossing, interpolated, not at either end - at
        /// racing speed one step is a whole segment. At most one per call: the pickups are
        /// hundreds of segments apart.
        /// </summary>
        public int Sweep(float zFrom, float zTo, float xFrom, float xTo)
        {
            if (!(zTo > zFrom) || _length <= 0f) return -1;
            for (int i = 0; i < _z.Length; i++)
            {
                int lap = Mathf.FloorToInt((zTo - _z[i]) / _length);
                float pz = _z[i] + lap * _length;
                if (pz <= zFrom || pz > zTo) continue;          // not crossed this step
                if (!IsLive(i, lap)) continue;                  // taken this lap, or past the flag
                float t = (pz - zFrom) / (zTo - zFrom);
                float x = xFrom + (xTo - xFrom) * t;
                if (Mathf.Abs(x - _x[i]) > Radius) continue;    // drove past it
                _takenOnLap[i] = lap;
                TakenCount++;
                LastTaken = i;
                return i;
            }
            return -1;
        }

        /// <summary>
        /// Build from the circuit's own layout (TrackModel.Layout), never from hand-typed
        /// segment numbers - the rule RoadsideSprites follows, for the same reason: a
        /// rebuilt circuit must not leave a pickup inside a corner. A placement naming a
        /// corner this circuit does not have is DROPPED, not guessed.
        /// </summary>
        public static BoostPickupSet FromLayout(TrackModel t, BoostTuning tuning)
        {
            BoostPlacement[] placements = tuning != null ? tuning.placements : null;
            TurnInfo[] turns = t.Layout.Turns;
            int n = t.Count;
            int count = placements != null ? placements.Length : 0;
            int[] seg = new int[count];
            float[] lat = new float[count];
            int used = 0;

            for (int k = 0; k < count; k++)
            {
                BoostPlacement pl = placements[k];
                int s, gapStart, gapEnd;
                if (pl.afterTurn <= 0) { s = pl.segmentsAfter; gapStart = 0; }
                else if (pl.afterTurn <= turns.Length)
                {
                    gapStart = turns[pl.afterTurn - 1].endSegment;
                    s = gapStart + pl.segmentsAfter;
                }
                else continue;
                // Keep it on the STRAIGHT: at least ClearSegments from the corner behind
                // and the corner ahead, or the middle of a straight too short for both.
                gapEnd = n;
                for (int i = 0; i < turns.Length; i++)
                    if (turns[i].startSegment >= gapStart && turns[i].startSegment > s - ClearSegments)
                    { gapEnd = turns[i].startSegment; break; }
                if (gapEnd - gapStart >= 2 * ClearSegments)
                    s = Mathf.Clamp(s, gapStart + ClearSegments, gapEnd - ClearSegments);
                else
                    s = (gapStart + gapEnd) / 2;
                s = Mathf.Clamp(s, 1, n - 1);

                float x = pl.lateral;
                if (pl.autoLine)
                {
                    int next = -1;
                    for (int i = 0; i < turns.Length; i++)
                        if (turns[i].startSegment > s) { next = i; break; }
                    x = next < 0 ? 0f : (turns[next].IsLeft ? tuning.lineOffset : -tuning.lineOffset);
                }
                seg[used] = s;
                lat[used] = Mathf.Clamp(x, -0.9f, 0.9f);
                used++;
            }
            if (used < count)
            {
                System.Array.Resize(ref seg, used);
                System.Array.Resize(ref lat, used);
            }
            return new BoostPickupSet(seg, lat, t.Length, tuning != null ? tuning.radius : 0.40f);
        }
    }
}
