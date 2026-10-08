using System.Collections.Generic;
using UnityEngine;

namespace RenaultRacer.Track
{
    /// <summary>
    /// Authors a track with the reference racer's readable vocabulary, so a layout
    /// change is a few lines of code rather than a level editor (plan section 11).
    ///
    /// Build-time only. Runtime driving never touches this.
    /// </summary>
    public sealed class TrackBuilder
    {
        // ------------------------------------------------------------------
        // CURVE STRENGTHS - re-derived against the CURRENT Production profile.
        //
        // The old block on this spot was written for the rate-steering car and
        // topped out at 5.5 "because beyond 6 nothing holds". That is no longer
        // true, and the numbers below are not opinions - they are the solution of
        // the force balance the model actually evaluates every step.
        //
        // A corner pushes the car outward with
        //     push  = curve * speedPct^2 * centrifugalStrength          (1.36)
        // and the wheel answers with
        //     steer = sin(lock * authority(v) * maxYawAngle * (0.35 + 0.65v))
        //             * max(v, lowSpeedLateralFloor) * lateralAcceleration  (29)
        //
        // Setting them equal on Production (maxSpeed 12000, maxYawAngle 0.45,
        // lateralAcceleration 29, centrifugalStrength 1.36, highSpeedSteering 0.75,
        // lowSpeedSteering 1.3):
        //
        //   full lock, TOP speed      authority 0.750  ->  curve  7.06   <- flat-out limit
        //   full lock, HALF speed     authority 0.964  ->  curve 12.31
        //   0.65 lock, TOP speed                       ->  curve  4.64
        //   0.40 lock, TOP speed                       ->  curve  2.87
        //
        // Two things follow, and the whole circuit is built on them.
        //
        // FIRST: braking pays, and it pays QUADRATICALLY. The push carries speed
        // squared while the steering force is close to linear in speed, so a third
        // off the entry speed is more than half off the push. Anything past 7.06 has
        // no flat-out line at all and the brake is the only answer - which is what
        // makes T01, T05 and T14 real braking zones instead of decoration.
        //
        // SECOND: the ladder below is a LOCK ladder, not a difficulty adjective.
        // Each named band is the curvature that needs roughly that much steering at
        // top speed, measured by stepping the real simulation for six seconds from
        // the centre line (the same test HandlingProbe [9e] runs):
        //
        //     CurveEasy   2.0   0.20 lock      CurveLong    5.5   0.70 lock
        //     CurveFast   4.0   0.50 lock      CurveTight   6.0   0.80 lock
        //     CurveMedium 4.6   0.60 lock      CurveHard    6.8   0.90 lock
        //
        // CurveHard sits just under the 7.06 limit on purpose: it is the hardest
        // corner that still HAS a flat-out line, and only barely. The circuit's three
        // braking corners are deliberately ABOVE it (7.5, 7.8, 8.0) and are written
        // as literals in the turn table, because a band name would imply they belong
        // to the same ladder and they do not - no lock holds them at top speed.
        public const float CurveNone   = 0f;
        public const float CurveEasy   = 2.0f;   // 0.20 lock - a shape, not a demand
        public const float CurveFast   = 4.0f;   // 0.50 lock - a true sweeper
        public const float CurveMedium = 4.6f;   // 0.60 lock - the comfortable band
        public const float CurveLong   = 5.5f;   // 0.70 lock - real speed management
        public const float CurveTight  = 6.0f;   // 0.80 lock - strong steering
        public const float CurveHard   = 6.8f;   // 0.90 lock - the flat-out limit, barely

        /// <summary>
        /// The measured curvature full lock can just hold at top speed. Past this no
        /// steering input keeps the car on the road and the brake is the only answer.
        /// </summary>
        public const float CurveFlatOutLimit = 7.06f;

        // ------------------------------------------------------------------
        // HILL HEIGHTS.
        //
        // targetHeight is multiplied by SegmentLength (200), so the old
        // HillLow/Medium/High of 20/40/60 authored climbs of 4000-12000 units
        // against a camera height of 1000. That is the rollercoaster the client
        // asked us to remove.
        //
        // The budget now is a GRADE, not a height: elevation is authored as
        // targetHeight = grade * segments. AddRoad eases the hill with a cosine, so
        // the steepest point of a run is (pi/2) x the average, which makes the peak
        // slope exactly (pi/2) * grade. HillGrade below therefore caps every slope
        // on the circuit at 0.044 - under three degrees.
        public const float HillFlat  = 0f;
        public const float HillGrade = 0.028f;   // peak slope = pi/2 * 0.028 = 0.044

        /// <summary>
        /// Multiplier on every authored height in the circuit. THE ONE PLACE the track's
        /// vertical scale is set.
        ///
        /// The elevation data was correct, closed and, on screen, invisible: the whole
        /// lap's biggest effect on the far road was about 13 pixels of a 720-pixel virtual
        /// screen, which reads as flat. The instinct is to reach for the renderer, and
        /// that would be the wrong lever - it would exaggerate every hill on every track
        /// and change the projection that the car, the props and the skid marks are all
        /// drawn against. What was actually too small is the AUTHORED height, so that is
        /// what is scaled, at the point where targetHeight becomes world units.
        ///
        /// It multiplies a budget that sums to exactly zero around the lap, so the lap
        /// still closes on its own datum at any value, and it scales the peak SLOPE with
        /// it: 3.5 took the steepest gradient on the circuit from 0.044 to 0.154, about
        /// nine degrees. Raised 25% to 4.375 (exactly 35/8, so the eighth-unit budget
        /// still sums to zero and the lap closes exactly): steepest climb 0.192 (10.9
        /// degrees, mid-T07), tightest crest radius 11,690 -> 9,350 (the T02-T03 crest),
        /// longest hidden stretch 149 -> 185 segments against a limit of 190. Still a
        /// rolling circuit; the cost is T03's apex coming into view about 1.1 s out
        /// instead of 1.7. Above about 5 the crests start hiding road the player needs to
        /// see, which CrestProbe measures - do not raise it without reading that report.
        ///
        /// 2026-10: raised another 25% to 5.46875 (exactly 175/32, still an exact binary
        /// fraction, so the lap still closes on its datum) because the 4.375 bump was not
        /// noticeable in play. Measured (VisualProbe [V1] sweep): steepest climb 10.9 ->
        /// 13.5 degrees; longest hidden band 185 -> 200 segments (beyond the T14 hairpin,
        /// not on a braking approach - T01/T05/T14 approaches stay at 4 segments hidden);
        /// average hidden 32.4 -> 37.9 of 300. This value FAILS two VisualProbe gates that
        /// 4.375 passes: [V1] "no blinder than flat x 1.25 + 2" (200 > 190.75) and [V2]
        /// "worst prop-opacity move <= 8" (15.1, at the T08-T09 summit). [V2] models the
        /// pre-crest-clip prop rule; CrestRevealProbe, which drives the shipped rule, still
        /// counts 0 pops, and rendered frame-by-frame the summit changes about 14% more
        /// pixels per frame than at 4.375. The highest value that passes every gate is
        /// 4.625 (+6%). Two lightning pickups were moved off the crests this exposes.
        /// </summary>
        /// <remarks>
        /// A static rather than a const so CrestProbe can sweep it and show what each
        /// value costs in hidden road. Build-time only - the track is authored once at
        /// Awake and nothing reads this again - and it is not serialized anywhere, so the
        /// value in this file is the value that ships.
        /// </remarks>
        public static float ElevationScale = 5.46875f;

        // Section lengths, in segments
        public const int LenShort  = 25;
        public const int LenMedium = 50;
        public const int LenLong   = 100;

        private readonly List<Segment> _segments = new List<Segment>(4096);

        /// <summary>
        /// Where the PREVIOUS run was authored to end, as opposed to the height of
        /// the last segment it actually emitted.
        ///
        /// These differ, and the difference is why elevation used to drift. AddRoad
        /// samples its cosine at i/total for i in [0, total), so the final sample
        /// stops just short of endY; chaining the next run off that sample loses a
        /// sliver of every hill. Over this circuit's 31 runs that came to roughly one
        /// world unit of drift, which is invisible but means a lap authored to sum to
        /// exactly zero does not actually return to its own datum. Chaining off the
        /// AUTHORED end instead makes the elevation budget exact, at the cost of a
        /// sub-unit step at each run boundary (worst case here 0.5 of 200).
        /// </summary>
        private float _authoredY;

        /// <summary>Default lateral limit. 1 = road edge, so this leaves a grass band.</summary>
        public const float DefaultBarrier = 2.4f;

        /// <summary>Segments authored so far.</summary>
        public int Count { get { return _segments.Count; } }

        private void AddSegment(float curve, float y)
        {
            Segment s = new Segment();
            s.curve = curve;
            s.y = y;
            s.surface = Surface.Road;
            s.barrierLeft = -DefaultBarrier;
            s.barrierRight = DefaultBarrier;
            s.isFinishGate = false;
            s.checkpointId = -1;
            _segments.Add(s);
        }

        /// <summary>
        /// A run of road: an entry ramp onto <paramref name="curve"/>, a hold at it,
        /// an exit ramp off it, and one smooth hill across the whole thing.
        ///
        /// Both ramps use EaseInOut. The entry used to use EaseIn (t^2), and the
        /// difference is worth stating because it changes how a corner READS: t^2
        /// leaves the first half of the entry almost straight and then arrives at
        /// full curvature with its steepest slope, so the turn-in is late and there
        /// is a kink in curvature where the ramp meets the hold. EaseInOut starts and
        /// ends flat, so the road's curvature is continuous with the straight before
        /// it AND with the hold after it, and the turn-in is visible from the start.
        ///
        /// It also changes the arithmetic, so the corner lengths in BuildMainTrack
        /// were solved against the new shape and not carried over:
        ///     mean multiplier over an EaseIn  entry = 1/3
        ///     mean multiplier over an EaseInOut entry = 1/2
        /// so effective curvature is now  hold + (enter + leave)/2  rather than
        /// enter/3 + hold + leave/2, and heading delivered is
        ///     effective * curve / SegmentLength  radians.
        /// </summary>
        public TrackBuilder AddRoad(int enter, int hold, int leave, float curve, float targetHeight)
        {
            float startY = _authoredY;
            float endY = startY + targetHeight * RoadGeometry.SegmentLength * ElevationScale;
            _authoredY = endY;

            int total = enter + hold + leave;
            if (total <= 0) return this;

            for (int i = 0; i < enter; i++)
                AddSegment(RoadGeometry.EaseInOut(0f, curve, (float)i / enter),
                           RoadGeometry.EaseInOut(startY, endY, (float)i / total));

            for (int i = 0; i < hold; i++)
                AddSegment(curve,
                           RoadGeometry.EaseInOut(startY, endY, (float)(enter + i) / total));

            for (int i = 0; i < leave; i++)
                AddSegment(RoadGeometry.EaseInOut(curve, 0f, (float)i / leave),
                           RoadGeometry.EaseInOut(startY, endY, (float)(enter + hold + i) / total));

            return this;
        }

        /// <summary>
        /// Exactly <paramref name="segments"/> segments of TRUE straight - curve is
        /// bit-for-bit zero on every one of them, because EaseInOut(0, 0, t) is 0 by
        /// construction and there are no ramps to round.
        ///
        /// This is the unit the circuit's links and reset straights are built from.
        /// AddStraight(n) is NOT that: it is AddRoad(n, n, n, ...) and therefore 3n
        /// segments, which is a trap when a layout table says "45".
        /// </summary>
        public TrackBuilder AddLink(int segments, float targetHeight)
        {
            return AddRoad(0, segments, 0, CurveNone, targetHeight);
        }

        public TrackBuilder AddStraight(int length)
        {
            return AddRoad(length, length, length, CurveNone, HillFlat);
        }

        public TrackBuilder AddCurve(int length, float curve, float height)
        {
            return AddRoad(length, length, length, curve, height);
        }

        public TrackBuilder AddHill(int length, float height)
        {
            return AddRoad(length, length, length, CurveNone, height);
        }

        public TrackBuilder AddSCurves()
        {
            AddRoad(LenMedium, LenMedium, LenMedium, -CurveEasy,   HillFlat);
            AddRoad(LenMedium, LenMedium, LenMedium,  CurveMedium, 1f);
            AddRoad(LenMedium, LenMedium, LenMedium,  CurveEasy,  -0.5f);
            AddRoad(LenMedium, LenMedium, LenMedium, -CurveEasy,   1f);
            AddRoad(LenMedium, LenMedium, LenMedium, -CurveMedium, -1f);
            return this;
        }

        public TrackBuilder AddBumps()
        {
            AddRoad(10, 10, 10, 0f,  0.5f);
            AddRoad(10, 10, 10, 0f, -0.2f);
            AddRoad(10, 10, 10, 0f, -0.5f);
            AddRoad(10, 10, 10, 0f,  0.8f);
            AddRoad(10, 10, 10, 0f,  0.5f);
            AddRoad(10, 10, 10, 0f, -0.7f);
            AddRoad(10, 10, 10, 0f,  0.5f);
            AddRoad(10, 10, 10, 0f, -0.9f);
            return this;
        }

        /// <summary>
        /// Return the surface to its datum over one run.
        ///
        /// NOT USED BY THE PRODUCTION CIRCUIT, and that is deliberate. Bolting this
        /// on the end is what produced V1's final drop: whatever elevation the lap
        /// had accumulated was dumped into the last stretch of road before the line,
        /// at whatever gradient that took. The production lap authors its elevation
        /// closed instead - the targetHeights sum to exactly zero around the circuit -
        /// so nothing has to be corrected at the end. Kept only for the Phase 2 test
        /// track, where the shape of the hills is the point and closure is not.
        /// </summary>
        public TrackBuilder AddDownhillToEnd(int length)
        {
            return AddRoad(length, length, length, CurveNone,
                           -_authoredY / (RoadGeometry.SegmentLength * ElevationScale));
        }

        /// <summary>
        /// Linked S-bends - the rhythm section. Each direction change costs lateral
        /// momentum, so flowing through with one committed line is visibly faster than
        /// sawing at the wheel. Rewards early turn-in (plan 11.2).
        /// </summary>
        public TrackBuilder AddEsses(int lobe, float curve, float height)
        {
            AddRoad(lobe, lobe / 2, lobe,  curve,         height);
            AddRoad(lobe, lobe / 2, lobe, -curve,        -height * 0.5f);
            AddRoad(lobe, lobe / 2, lobe,  curve * 0.8f,  0f);
            return this;
        }

        /// <summary>
        /// A tight corner at the end of a fast approach - the lap's main braking point.
        /// Short entry and exit ramps make the direction change abrupt on purpose, so
        /// arriving too fast runs you wide instead of merely costing a little time.
        /// </summary>
        public TrackBuilder AddHairpin(float curve, int hold)
        {
            return AddRoad(16, hold, 24, curve, 0f);
        }

        /// <summary>
        /// A quick left-right flick. Too much speed here cannot be recovered by
        /// steering alone - lifting, or a controlled drift, is genuinely faster.
        /// </summary>
        public TrackBuilder AddChicane(float curve)
        {
            AddRoad(16, 22, 16,  curve, 0f);
            AddRoad(16, 22, 16, -curve, 0f);
            return this;
        }

        /// <summary>
        /// One NUMBERED corner, recorded as it is built.
        ///
        /// The delivered heading is MEASURED out of the segments that were just
        /// emitted rather than copied from the design table, so TurnInfo.degrees can
        /// never disagree with the road. That matters because the minimap, the corner
        /// signage and the sector timing all read TurnInfo and nothing else.
        /// </summary>
        private TurnInfo AddTurn(int number, int enter, int hold, int leave,
                                 float curve, float targetHeight)
        {
            TurnInfo t = new TurnInfo();
            t.number = number;
            t.startSegment = _segments.Count;
            t.holdSegment = t.startSegment + enter;
            t.curve = curve;

            AddRoad(enter, hold, leave, curve, targetHeight);

            t.endSegment = _segments.Count;

            float radians = 0f;
            for (int i = t.startSegment; i < t.endSegment; i++)
                radians += _segments[i].curve;
            t.degrees = radians / RoadGeometry.SegmentLength * Mathf.Rad2Deg;
            return t;
        }

        /// <summary>
        /// Widen or narrow the run-off across a range of already-added segments.
        /// Used to be generous exactly where first-timers are most likely to run
        /// wide, so a mistake costs time without ending the lap (plan 11.2).
        /// </summary>
        public TrackBuilder SetBarriers(int fromIndex, int count, float halfWidth)
        {
            return SetBarriers(fromIndex, count, -halfWidth, halfWidth);
        }

        /// <summary>
        /// Asymmetric run-off: the standard circuit tool for making a corner demand
        /// speed management. A positive curve throws the car toward -x, so tightening
        /// barrierLeft puts a wall on the OUTSIDE of a right-hander. Entering too fast
        /// then costs real momentum instead of a harmless excursion - which is what
        /// makes braking and corner entry worth doing well (plan 11.2).
        ///
        /// This narrows the RUN-OFF, never the road: the driving surface is still
        /// +/-1 everywhere, so the circuit never becomes a narrow-road gauntlet.
        /// </summary>
        public TrackBuilder SetBarriers(int fromIndex, int count, float left, float right)
        {
            // The road surface is ALWAYS +/-1, so a corridor that does not contain it
            // is a authoring mistake, not a hard corner. Left unchecked it is invisible
            // in the layout code and vicious in play: the collision block clamps X onto
            // the nearest limit, so the first car to reach such a segment is snapped
            // sideways in a single step. That is exactly what shipped in Phase 5 - the
            // chicane was written (2.0, 2.0) instead of (-2.0, 2.0), giving a zero-width
            // corridor two half-widths right of the centre line, and every lap teleported
            // the car into the right-hand wall there (M5 playtest).
            if (left > -1f || right < 1f || left >= right)
            {
                Debug.LogError("[TrackBuilder] SetBarriers(" + fromIndex + ", " + count
                    + ", " + left + ", " + right + ") does not contain the road surface "
                    + "(+/-1). Barriers narrow the RUN-OFF, never the road. Clamping.");
                left = Mathf.Min(left, -1f);
                right = Mathf.Max(right, 1f);
                if (left >= right) { left = -DefaultBarrier; right = DefaultBarrier; }
            }

            for (int i = 0; i < count; i++)
            {
                int idx = fromIndex + i;
                if (idx < 0 || idx >= _segments.Count) continue;
                Segment s = _segments[idx];
                s.barrierLeft = left;
                s.barrierRight = right;
                _segments[idx] = s;
            }
            return this;
        }

        /// <summary>Override the surface across a range of already-added segments.</summary>
        public TrackBuilder SetSurface(int fromIndex, int count, Surface surface)
        {
            for (int i = 0; i < count; i++)
            {
                int idx = fromIndex + i;
                if (idx < 0 || idx >= _segments.Count) continue;
                Segment s = _segments[idx];
                s.surface = surface;
                _segments[idx] = s;
            }
            return this;
        }

        /// <summary>
        /// Place the finish gate and evenly spaced validation checkpoints
        /// (plan sections 12 and 13). Call once, last.
        /// </summary>
        public TrackBuilder PlaceGates(int checkpointCount)
        {
            return PlaceGates(checkpointCount, null, 0);
        }

        /// <summary>
        /// As above, but nudged clear of <paramref name="keepAwayFrom"/>.
        ///
        /// The checkpoints are invisible anti-cheat gates (section 13) and the sector
        /// lines are a visible, timed feature. They are different things, and a
        /// checkpoint that happens to land on a sector line makes them look like one
        /// thing that sometimes fires twice - in the logs, in the timing code and to
        /// anyone reading a probe. Spacing is unchanged otherwise; a nudge moves a
        /// checkpoint by at most <paramref name="minGap"/> segments out of ~550.
        /// </summary>
        public TrackBuilder PlaceGates(int checkpointCount, int[] keepAwayFrom, int minGap)
        {
            if (_segments.Count == 0) return this;

            Segment first = _segments[0];
            first.isFinishGate = true;
            _segments[0] = first;

            for (int c = 0; c < checkpointCount; c++)
            {
                // Spread across the lap, skipping 0 so a checkpoint never lands on the gate.
                int idx = (int)(_segments.Count * ((float)(c + 1) / (checkpointCount + 1)));
                idx = Mathf.Clamp(idx, 1, _segments.Count - 1);

                if (keepAwayFrom != null)
                {
                    for (int k = 0; k < keepAwayFrom.Length; k++)
                    {
                        int d = idx - keepAwayFrom[k];
                        if (Mathf.Abs(d) < minGap)
                            idx = Mathf.Clamp(keepAwayFrom[k] + (d >= 0 ? minGap : -minGap),
                                              1, _segments.Count - 1);
                    }
                }

                Segment s = _segments[idx];
                s.checkpointId = c;
                _segments[idx] = s;
            }
            return this;
        }

        public TrackModel Build()
        {
            return new TrackModel(_segments.ToArray());
        }

        /// <summary>Build with the authored corner and sector record attached.</summary>
        public TrackModel Build(TurnInfo[] turns, int[] sectorStartSegment)
        {
            return new TrackModel(_segments.ToArray(),
                                  new TrackLayout(turns, sectorStartSegment, _segments.Count));
        }

        /// <summary>
        /// THE PRODUCTION CIRCUIT - the client's traced layout, built to close.
        ///
        /// 15 numbered corners, clockwise, net +360 degrees. Every number below was
        /// solved rather than typed, and the three things it was solved against are:
        ///
        ///   1. THE HEADING. heading changes by curve / SegmentLength radians per
        ///      segment, so a corner delivers
        ///          (hold + (enter + leave)/2) * curve / 200  radians.
        ///      entry:hold:exit stay near 1:3:1.5 - deliberately asymmetric, because
        ///      a symmetric AddCurve(n,n,n) spends two thirds of a corner on its ramps
        ///      and delivers only ~61% of the nominal angle. That is exactly how V1's
        ///      curvature got diluted, and it is why nothing here calls AddCurve.
        ///
        ///   2. THE LOOP CLOSING. The straight lengths are not free: with the corner
        ///      radii fixed by (1), the 16 straights carry two linear constraints -
        ///      sum of (sin h, cos h) * length over the whole lap must be zero. They
        ///      were solved for that, subject to each leg's design minimum, and then
        ///      searched for a solution that does not cross itself. Plan 11.5 makes
        ///      this non-negotiable: an open arc cannot honestly be drawn as a loop.
        ///
        ///   3. THE ELEVATION CLOSING. Every targetHeight is a multiple of 1/8 and
        ///      they sum to exactly zero, so the lap returns to its own datum with no
        ///      correcting drop at the end. Peak slope 0.044.
        ///
        /// Sectors: 1 is the line to the T05 braking zone, 2 is T05 to T11, 3 is T12
        /// to the line.
        ///
        /// A note on size. 2773 segments is larger than the first estimate, and the
        /// reason is (2): 15 corners at these radii, joined by straights no shorter
        /// than the design minimums, do not close in under ~2660 segments. The corner
        /// runs alone are 1178. Shrinking the lap means either fewer degrees of
        /// turning or curvatures past the flat-out limit, and both change the layout
        /// the client traced.
        /// </summary>
        public static TrackModel BuildMainTrack()
        {
            TrackBuilder b = new TrackBuilder();
            TurnInfo[] t = new TurnInfo[15];

            // ---- SECTOR 1 -------------------------------------------------
            // The pit straight, second half. Dead flat, dead straight: the car is
            // released onto it from a standing start and the grandstands are here.
            b.AddLink(366, HillFlat);

            // T01  the lap's first braking point. 7.5 is ABOVE the 7.06 flat-out
            //      limit, so there is no lock that holds it at top speed - the brake
            //      is the answer, and it is meant to be obvious. Flat, because a
            //      braking point you cannot see is a trap (plan 11.2).
            t[0] = b.AddTurn(1, 9, 30, 15, 7.5f, HillFlat);
            b.AddLink(46, +1.25f);                       // climb link

            // T02  an easy right kink that lets the climb continue.
            t[1] = b.AddTurn(2, 12, 36, 18, 2.4f, +1.75f);
            b.AddLink(25, +0.625f);

            // T03  the long right loop, and the crest of this end of the circuit.
            //      6.8 = CurveHard: holdable flat out at 0.90 lock, nothing spare.
            t[2] = b.AddTurn(3, 15, 45, 23, 6.8f, +2.25f);
            b.AddLink(65, -1.125f);                      // R->L reset, true straight

            // T04  left, downhill, and the corner that sets up the back straight.
            t[3] = b.AddTurn(4, 14, 41, 20, -4.2f, -1.25f);

            // The BACK STRAIGHT. Descends gently and levels off before the braking
            // zone - EaseInOut ends with zero slope, so there is no crest in front of
            // T05 and the braking point is visible the whole way down.
            b.AddLink(150, -2.5f);

            // ---- SECTOR 2 -------------------------------------------------
            // T05  the second braking zone and the hardest corner on the lap: 160
            //      degrees left at curve 8.0, which needs about 83% of top speed even
            //      at full lock. Dead flat.
            t[4] = b.AddTurn(5, 17, 49, 25, -8.0f, HillFlat);
            b.AddLink(62, HillFlat);

            // T06  a 20 degree left kink. Easy on purpose - a breath before the climb.
            t[5] = b.AddTurn(6, 8, 25, 12, -2.0f, HillFlat);
            b.AddLink(55, +1.5f);                        // L->R reset, true straight

            // T07, T08  two long rights climbing to the lap's high point.
            t[6] = b.AddTurn(7, 16, 52, 26, 4.4f, +2.625f);
            b.AddLink(35, +0.875f);
            t[7] = b.AddTurn(8, 19, 55, 28, 4.0f, +2.75f);
            b.AddLink(70, +1.875f);                      // top straight, to the summit

            // T09 / T10  equal 40 degree angles in opposite directions - the pair most
            //            at risk of cancelling each other out. They are kept apart by
            //            strength (2.5 against 4.0) AND by a 50-segment true straight,
            //            exactly as the layout brief requires. Plateau, no elevation.
            t[8] = b.AddTurn(9, 13, 39, 20, -2.5f, HillFlat);
            b.AddLink(50, HillFlat);                     // L->R reset, true straight
            t[9] = b.AddTurn(10, 8, 25, 12, 4.0f, HillFlat);
            b.AddLink(28, HillFlat);                     // technical link

            // T11  the descent begins.
            t[10] = b.AddTurn(11, 14, 40, 20, 5.5f, -1.25f);
            b.AddLink(150, -2.5f);                       // R->L reset, true straight

            // ---- SECTOR 3 -------------------------------------------------
            t[11] = b.AddTurn(12, 14, 42, 22, -4.2f, -1.375f);
            b.AddLink(55, -0.875f);
            t[12] = b.AddTurn(13, 17, 50, 25, -5.8f, -1.625f);
            b.AddLink(50, -0.875f);                      // L->R reset, true straight

            // T14  the top-right hairpin: 163 degrees at curve 7.8, the third braking
            //      zone. Flat, and approached on a descent rather than over a crest.
            t[13] = b.AddTurn(14, 16, 52, 26, 7.8f, HillFlat);
            b.AddLink(123, -2.125f);                     // the last of the descent

            // T15  the final sweep onto the pit straight. The descent is finished
            //      BEFORE it, so the corner itself and its exit are dead flat.
            t[14] = b.AddTurn(15, 20, 62, 31, 4.6f, HillFlat);

            // The pit straight, first half, back to the start/finish line.
            b.AddLink(265, HillFlat);

            // ---- run-off --------------------------------------------------
            // GENEROUS at the three braking corners. This is where a first-timer gets
            // it wrong, and the brief is explicit that it must cost time rather than
            // the lap. A positive curve throws the car toward -x, so the outside of a
            // right-hander is the LEFT limit and vice versa.
            Widen(b, t[0],  -3.4f, DefaultBarrier);      // T01, right-hander
            Widen(b, t[4],  -DefaultBarrier, 3.4f);      // T05, left-hander
            Widen(b, t[13], -3.4f, DefaultBarrier);      // T14, right-hander

            // TIGHT on the OUTSIDE of two medium corners, so carrying too much speed
            // into them costs momentum instead of being free. Both are fast enough to
            // be taken flat and long enough to punish it, which is what makes speed
            // management worth anything on this lap.
            Widen(b, t[7],  -1.7f, DefaultBarrier);      // T08, right-hander
            Widen(b, t[11], -DefaultBarrier, 1.7f);      // T12, left-hander

            // ---- sectors and gates ---------------------------------------
            // Line 2 opens the T05 braking zone: 40 segments (8000 units) of back
            // straight before the entry ramp, so the split is taken at the point the
            // driver has to commit, not halfway round the corner.
            int sector2 = t[4].startSegment - 40;
            // Line 3 sits between T11's exit and T12's entry, in the middle of the
            // reset straight that separates them.
            int sector3 = (t[10].endSegment + t[11].startSegment) / 2;
            int[] sectors = { 0, sector2, sector3 };

            b.PlaceGates(4, sectors, 60);
            return b.Build(t, sectors);
        }

        /// <summary>Apply a run-off corridor across a whole numbered corner.</summary>
        private static void Widen(TrackBuilder b, TurnInfo t, float left, float right)
        {
            b.SetBarriersTapered(t.startSegment, t.endSegment - t.startSegment, left, right,
                                 BarrierTaperSegments);
        }

        /// <summary>
        /// Segments over which a run-off corridor eases from one width to another.
        ///
        /// THE 90 DEGREE TYRE-WALL CORNERS. Corridors used to be a STEP: barrier 2.4 on one
        /// segment, 3.4 on the next. The tyre wall is drawn from each segment's barrier to
        /// the next one's, so a step of 1.0 half-width (1,100 units) across a 200-unit
        /// segment drew a wall piece at atan(5.5) = 80 degrees to the road - a corner
        /// standing in the grass where the road does nothing (TyreWallContinuityProbe [W1]:
        /// ten of them, at both ends of T01, T05, T08, T12 and T14). Collision held the
        /// segment's own value for the whole segment, so across that piece the car met an
        /// invisible wall on the way in and drove through the drawn one on the way out,
        /// then was snapped up to 1.0 half-width sideways in one step when the next
        /// segment's limit applied - the glitch.
        ///
        /// 30 segments of cosine ease: the steepest piece is atan(pi/2 / 30 * 5.5) = 16
        /// degrees for a 1.0 change and 11 for 0.7, the way a real run-off's tyre wall
        /// angles in, and adjacent pieces differ by under 2 degrees.
        /// </summary>
        public const int BarrierTaperSegments = 30;

        /// <summary>
        /// <see cref="SetBarriers(int,int,float,float)"/> with eased ends instead of steps,
        /// each side on its own. Where a side WIDENS the ease sits outside the range, so
        /// the authored run-off is never reduced; where it NARROWS the ease is centred on
        /// the range's ends, so a tight corridor keeps its authored length on average.
        /// Each eased segment blends from whatever that segment already had, so nearby
        /// corridors cannot cut each other off.
        /// </summary>
        public TrackBuilder SetBarriersTapered(int fromIndex, int count, float left, float right, int taper)
        {
            if (taper <= 0) return SetBarriers(fromIndex, count, left, right);
            if (left > -1f || right < 1f || left >= right)
                return SetBarriers(fromIndex, count, left, right);   // logs and clamps

            int n = _segments.Count;
            // Snapshot of what each side had before this corridor, for the blend.
            float[] oldL = new float[n], oldR = new float[n];
            for (int i = 0; i < n; i++) { oldL[i] = _segments[i].barrierLeft; oldR[i] = _segments[i].barrierRight; }

            for (int side = -1; side <= 1; side += 2)
            {
                float target = side < 0 ? left : right;
                float outside = side < 0 ? oldL[Mathf.Clamp(fromIndex, 0, n - 1)] : oldR[Mathf.Clamp(fromIndex, 0, n - 1)];
                bool widens = Mathf.Abs(target) >= Mathf.Abs(outside);
                // Where the full value starts and stops, and the eases either side.
                int lead = widens ? taper : taper / 2;
                int fullFrom = fromIndex + (widens ? 0 : taper - lead);
                int fullTo = fromIndex + count - (widens ? 0 : taper - lead);   // exclusive
                for (int i = fullFrom - taper; i < fullTo + taper; i++)
                {
                    if (i < 0 || i >= n) continue;
                    float w;
                    if (i < fullFrom) w = (float)(i - (fullFrom - taper) + 1) / (taper + 1);
                    else if (i >= fullTo) w = (float)(fullTo + taper - i) / (taper + 1);
                    else w = 1f;
                    w = 0.5f - 0.5f * Mathf.Cos(Mathf.Clamp01(w) * Mathf.PI);
                    Segment s = _segments[i];
                    float old = side < 0 ? oldL[i] : oldR[i];
                    float v = old + (target - old) * w;
                    if (side < 0) s.barrierLeft = v; else s.barrierRight = v;
                    _segments[i] = s;
                }
            }
            return this;
        }

        /// <summary>
        /// Short Phase 2 test track: straight, one curve, one hill, one S-bend.
        /// Enough to prove projection, curvature and hill occlusion.
        /// </summary>
        public static TrackModel BuildTestTrack()
        {
            TrackBuilder b = new TrackBuilder();
            b.AddStraight(LenShort);
            b.AddHill(LenMedium, 1f);
            b.AddCurve(LenMedium, CurveMedium, HillFlat);
            b.AddStraight(LenShort);
            b.AddCurve(LenMedium, -CurveEasy, 0.5f);
            b.AddSCurves();
            b.AddStraight(LenShort);
            b.AddDownhillToEnd(LenMedium);
            b.PlaceGates(4);
            return b.Build();
        }
    }
}
