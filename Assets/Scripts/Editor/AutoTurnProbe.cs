using System.Text;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;
using RenaultRacer.InputSystem;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// THE TEST THE WHOLE HANDLING REBUILD EXISTS FOR: does the road turn the car?
    ///
    /// Every earlier guard on this asked the question indirectly - "does an untouched car
    /// eventually leave the road" - and every one of them PASSED while the car was
    /// following the road almost perfectly. It could pass, because an untouched car was
    /// also being pushed outward by a separate centrifugal term: it left the road while
    /// still rotating through the whole corner. The player was handed the rotation and
    /// charged a small drift outward for it, and no test in the suite could tell that
    /// apart from a car that genuinely has to be steered.
    ///
    /// So this one measures the rotation itself, in WORLD terms, against the rotation the
    /// ROAD does over the same stretch:
    ///
    ///     inherited body %       = (car world heading change) / (road heading change)
    ///     inherited trajectory % = (car travel direction change) / (road heading change)
    ///
    /// A car that is genuinely not being steered scores ZERO on both. A car that is
    /// secretly being driven round the corner scores 100%. The old model scored about
    /// 100% body and 93% trajectory, which is the number this file was written to fail.
    ///
    /// It is deliberately separate from HandlingProbe: that suite grades a profile's FEEL
    /// against targets that are legitimately allowed to differ between profiles, while
    /// this is a structural invariant of the MODEL that every profile must hold.
    /// </summary>
    public static class AutoTurnProbe
    {
        private const float Step = 1f / 60f;

        // ---- hard acceptance thresholds (the session brief's numbers) ----
        public const float MaxBodyInheritance       = 0.10f;
        public const float MaxTrajectoryInheritance = 0.15f;
        public const float MaxSecondsToEdge         = 0.45f;

        /// <summary>Curvature the synthetic corner is built at - CurveMedium, the comfortable band.</summary>
        public const float SyntheticCurve = 4.6f;

        private sealed class NoInput : IDriveInput
        {
            public float SteerAxis { get { return 0f; } }
            public bool BrakeHeld { get { return false; } }
            public bool HandbrakeHeld { get { return false; } }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// A true straight, then a constant-radius corner of <paramref name="curve"/> long
        /// enough to turn through <paramref name="degrees"/>, then more of the same so a
        /// measurement never runs off the end of the corner.
        ///
        /// No entry ramp. A ramp is what a real circuit has and it is the right thing for
        /// driving, but it makes "how much has the road turned by now" a function of where
        /// in the ramp the car is, and this test wants one unambiguous number.
        /// </summary>
        private static TrackModel StraightThenConstantCorner(int straight, float curve,
                                                             float degrees, out int cornerStart)
        {
            float perSegment = Mathf.Abs(curve) / RoadGeometry.SegmentLength;   // radians
            int cornerSegs = Mathf.CeilToInt(degrees * Mathf.Deg2Rad / Mathf.Max(1e-6f, perSegment));
            int total = straight + cornerSegs * 2;

            Segment[] segs = new Segment[total];
            for (int i = 0; i < total; i++)
            {
                segs[i].curve = i < straight ? 0f : curve;
                segs[i].y = 0f;
                segs[i].surface = Surface.Road;
                // NO BARRIER WITHIN REACH, deliberately. This test measures rotation and
                // nothing else, and a car that has run wide and reached the wall is then
                // being rotated by the wall: the anti-trap response turns the nose back
                // toward the road, exactly as it should, and the ratio at 1.0 s and 1.5 s
                // stops being a statement about the corner at all. It read 44% and 58%
                // that way, all of it barrier recovery. Barriers have their own tests.
                segs[i].barrierLeft = -40f;
                segs[i].barrierRight = 40f;
                segs[i].checkpointId = -1;
            }
            cornerStart = straight;
            return new TrackModel(segs);
        }

        /// <summary>
        /// Put the car on the centre line at <paramref name="z"/>, at
        /// <paramref name="speedFraction"/> of top speed, pointed exactly down the road,
        /// with nothing moving sideways.
        ///
        /// Set rather than driven to: a car driven up to the mark carries whatever lateral
        /// state the approach left it with, and this test's whole job is to attribute
        /// rotation to the corner and to nothing else.
        /// </summary>
        private static CarSimulation Poised(TrackModel track, HandlingProfile p,
                                            float z, float speedFraction)
        {
            var sim = new CarSimulation(track, p);
            sim.Reset(z);
            sim.Speed = p.maxSpeed * speedFraction;
            sim.ThrottleEnabled = true;
            sim.ControlsEnabled = true;
            // One step lets the derived state (yaw, surface, telemetry) catch up with the
            // state that was set by hand, so the first sample is not a half-built car.
            sim.Step(Step, new NoInput());
            return sim;
        }

        private struct Reading
        {
            public float seconds;
            public float roadRotation;      // radians the ROAD turned over the window
            public float bodyRotation;      // radians the CAR's world heading turned
            public float travelRotation;    // radians the car's DIRECTION OF TRAVEL turned
            public float x;
            public float speed;
        }

        private static Reading Measure(CarSimulation sim, TrackWorldFrame frame,
                                       float z0, float world0, float travel0, float t)
        {
            Reading r;
            r.seconds = t;
            r.roadRotation   = frame.HeadingAt(sim.Z) - frame.HeadingAt(z0);
            r.bodyRotation   = sim.WorldHeading - world0;
            r.travelRotation = sim.TravelHeading - travel0;
            r.x = sim.X;
            r.speed = sim.Speed;
            return r;
        }

        private static float Ratio(float part, float whole)
        {
            return Mathf.Abs(whole) < 1e-5f ? 0f : part / whole;
        }

        // ------------------------------------------------------------------ [A1]

        /// <summary>
        /// THE SYNTHETIC CORNER. Straight entry, one 90 degree constant-radius corner at
        /// CurveMedium, full race speed, and the controls never touched.
        /// </summary>
        public static bool SyntheticCorner(HandlingProfile p, StringBuilder sb)
        {
            int cornerStart;
            TrackModel track = StraightThenConstantCorner(200, SyntheticCurve, 90f, out cornerStart);
            TrackWorldFrame frame = track.WorldFrame;

            float z0 = cornerStart * RoadGeometry.SegmentLength;
            CarSimulation sim = Poised(track, p, z0 - 400f, 1f);

            var input = new NoInput();
            // Roll up to the corner mouth with nothing touched, so the entry is genuinely
            // settled rather than merely initialised.
            while (sim.Z < z0) sim.Step(Step, input);

            float zEntry  = sim.Z;
            float world0  = sim.WorldHeading;
            float travel0 = sim.TravelHeading;

            sb.AppendLine("[A1] SYNTHETIC 90 deg CORNER, curve " + SyntheticCurve.ToString("F1")
                          + ", zero input, " + p.maxSpeed.ToString("F0") + " units/s entry");
            sb.AppendLine("      time |  road rot |  body rot | travel rot | body % | travel % |      X");
            sb.AppendLine("     ------+-----------+-----------+------------+--------+----------+-------");

            float[] marks = { 0.5f, 1.0f, 1.5f };
            int next = 0;
            float t = 0f;
            float edgeTime = -1f;
            bool bodyOk = true, travelOk = true;

            while (t < 2.0f)
            {
                sim.Step(Step, input);
                t += Step;

                if (edgeTime < 0f && Mathf.Abs(sim.X) >= 1f) edgeTime = t;

                if (next < marks.Length && t >= marks[next] - Step * 0.5f)
                {
                    Reading r = Measure(sim, frame, zEntry, world0, travel0, marks[next]);
                    float bodyPct   = Ratio(r.bodyRotation, r.roadRotation);
                    float travelPct = Ratio(r.travelRotation, r.roadRotation);
                    if (Mathf.Abs(bodyPct)   > MaxBodyInheritance)       bodyOk = false;
                    if (Mathf.Abs(travelPct) > MaxTrajectoryInheritance) travelOk = false;

                    sb.AppendLine("     " + r.seconds.ToString("F2").PadLeft(5)
                        + "s | " + Deg(r.roadRotation).PadLeft(9)
                        + " | " + Deg(r.bodyRotation).PadLeft(9)
                        + " | " + Deg(r.travelRotation).PadLeft(10)
                        + " | " + Pct(bodyPct).PadLeft(6)
                        + " | " + Pct(travelPct).PadLeft(8)
                        + " | " + r.x.ToString("F2").PadLeft(6));
                    next++;
                }
            }

            bool edgeOk = edgeTime > 0f && edgeTime <= MaxSecondsToEdge;
            sb.AppendLine("     time to the road edge: "
                          + (edgeTime > 0f ? edgeTime.ToString("F2") + " s" : "NEVER within 2 s")
                          + "   (limit " + MaxSecondsToEdge.ToString("F2") + " s)  " + Pass(edgeOk));
            sb.AppendLine("     body inheritance   <= " + Pct(MaxBodyInheritance) + "  " + Pass(bodyOk));
            sb.AppendLine("     travel inheritance <= " + Pct(MaxTrajectoryInheritance) + "  " + Pass(travelOk));

            bool ok = bodyOk && travelOk && edgeOk;
            sb.AppendLine("     [A1] THE ROAD DOES NOT TURN THE CAR = " + Pass(ok));
            sb.AppendLine();
            return ok;
        }

        // ------------------------------------------------------------------ [A2]

        /// <summary>
        /// EVERY NUMBERED CORNER ON THE REAL CIRCUIT, driven at race speed with the
        /// controls untouched.
        ///
        /// The window is entry to the moment the car first crosses the road edge, because
        /// that is the interval the player is actually being helped or not helped over;
        /// measuring on past it would grade grass physics instead. A corner the car never
        /// leaves in three seconds is reported as such and graded over the full window -
        /// on a gentle enough kink that is the honest answer, not a failure.
        /// </summary>
        public static bool RealCorners(HandlingProfile p, TrackModel track, StringBuilder sb)
        {
            TrackWorldFrame frame = track.WorldFrame;
            TurnInfo[] turns = track.Layout.Turns;

            sb.AppendLine("[A2] ALL 15 CORNERS OF THE PRODUCTION CIRCUIT, zero input from the entry ramp");
            sb.AppendLine("     turn | curve | road rot | body rot | travel rot | body % | travel % | to edge");
            sb.AppendLine("     -----+-------+----------+----------+------------+--------+----------+--------");

            bool ok = true;
            var input = new NoInput();

            for (int i = 0; i < turns.Length; i++)
            {
                TurnInfo t = turns[i];
                float zEntry = t.startSegment * RoadGeometry.SegmentLength;
                CarSimulation sim = Poised(track, p, zEntry - 600f, 1f);
                while (sim.Z < zEntry) sim.Step(Step, input);

                float z0 = sim.Z, world0 = sim.WorldHeading, travel0 = sim.TravelHeading;
                float zExit = t.endSegment * RoadGeometry.SegmentLength;
                float elapsed = 0f, edge = -1f;

                // The window CLOSES at the corner's own exit, not at a fixed time. Run on
                // past it and the next corner's rotation lands in the denominator: T09 and
                // T10 are equal and opposite, so a three-second window across the pair
                // measured a road that had turned 0.4 degrees and produced a ratio out of
                // noise. The corner is the unit of measurement here, so the corner is the
                // window.
                while (elapsed < 3f && sim.Z < zExit)
                {
                    sim.Step(Step, input);
                    elapsed += Step;
                    if (Mathf.Abs(sim.X) >= 1f) { edge = elapsed; break; }
                }

                Reading r = Measure(sim, frame, z0, world0, travel0, elapsed);
                float bodyPct   = Ratio(r.bodyRotation, r.roadRotation);
                float travelPct = Ratio(r.travelRotation, r.roadRotation);

                // A corner that turns less than 5 degrees over the window cannot produce a
                // meaningful ratio - the denominator is noise - so it is reported and not
                // graded. Saying so is the point; quietly passing it would be cheating.
                bool graded = Mathf.Abs(r.roadRotation) > 5f * Mathf.Deg2Rad;
                bool pass = !graded || (Mathf.Abs(bodyPct) <= MaxBodyInheritance
                                     && Mathf.Abs(travelPct) <= MaxTrajectoryInheritance);
                if (!pass) ok = false;

                sb.AppendLine("      T" + t.number.ToString("00")
                    + " | " + t.curve.ToString("F1").PadLeft(5)
                    + " | " + Deg(r.roadRotation).PadLeft(8)
                    + " | " + Deg(r.bodyRotation).PadLeft(8)
                    + " | " + Deg(r.travelRotation).PadLeft(10)
                    + " | " + (graded ? Pct(bodyPct) : "  n/a").PadLeft(6)
                    + " | " + (graded ? Pct(travelPct) : "  n/a").PadLeft(8)
                    + " | " + (edge > 0f ? edge.ToString("F2") + "s" : "  none").PadLeft(7)
                    + (graded ? "" : "   <- under 5 deg over the window, ratio not graded"));
            }

            sb.AppendLine("     [A2] NO CORNER IS COMPLETED FOR THE PLAYER = " + Pass(ok));
            sb.AppendLine();
            return ok;
        }

        // ------------------------------------------------------------------ [A3]

        /// <summary>
        /// The straight must still be a straight. A world-heading car that quietly drifts
        /// off line with nothing touched would fail the player in the opposite direction,
        /// and the straight re-alignment that keeps release feeling composed is exactly
        /// the kind of term that could do it.
        /// </summary>
        public static bool StraightIsStraight(HandlingProfile p, StringBuilder sb)
        {
            int unused;
            TrackModel track = StraightThenConstantCorner(3000, 0f, 1f, out unused);
            CarSimulation sim = Poised(track, p, 0f, 1f);
            var input = new NoInput();

            float world0 = sim.WorldHeading;
            for (int i = 0; i < 60 * 8; i++) sim.Step(Step, input);

            float drift = Mathf.Abs(sim.WorldHeading - world0) * Mathf.Rad2Deg;
            bool ok = Mathf.Abs(sim.X) < 0.01f && drift < 0.1f;

            sb.AppendLine("[A3] EIGHT SECONDS OF STRAIGHT, nothing touched");
            sb.AppendLine("     X " + sim.X.ToString("F4")
                          + "   world heading drift " + drift.ToString("F3") + " deg"
                          + "   yaw " + (sim.Yaw * Mathf.Rad2Deg).ToString("F3") + " deg"
                          + "   lateral " + sim.LateralVel.ToString("F4"));
            sb.AppendLine("     [A3] A STRAIGHT IS STILL A STRAIGHT = " + Pass(ok));
            sb.AppendLine();
            return ok;
        }

        // ------------------------------------------------------------------ [A4]

        /// <summary>
        /// The straight-only re-alignment must be OFF inside real corners. It is the one
        /// term in the model that could reintroduce road-following by the back door, so it
        /// is measured directly: with the car deliberately pointed away from the road and
        /// the controls released, a corner must not straighten it and a straight must.
        /// </summary>
        public static bool AlignmentIsStraightOnly(HandlingProfile p, StringBuilder sb)
        {
            int cornerStart;
            TrackModel corner = StraightThenConstantCorner(200, SyntheticCurve, 120f, out cornerStart);
            int unused;
            TrackModel straight = StraightThenConstantCorner(3000, 0f, 1f, out unused);

            float onStraight = YawBledOver(straight, p, 0f, 0.5f);
            float inCorner   = YawBledOver(corner, p, cornerStart * RoadGeometry.SegmentLength + 2000f, 0.5f);

            // "Bled" is measured as the road-relative yaw the alignment removed, so in the
            // corner it has to be separated from the yaw the corner itself is adding: the
            // comparison is against the same run with the term's gate closed, which is
            // what a curvature well past the gate threshold gives.
            bool ok = onStraight > 0.05f && inCorner < 0.02f;
            sb.AppendLine("[A4] STRAIGHT-ONLY RE-ALIGNMENT");
            sb.AppendLine("     yaw removed over 0.5 s on a straight: " + (onStraight * Mathf.Rad2Deg).ToString("F2") + " deg");
            sb.AppendLine("     yaw removed over 0.5 s in a corner:   " + (inCorner * Mathf.Rad2Deg).ToString("F2") + " deg");
            sb.AppendLine("     [A4] IT ONLY WORKS ON STRAIGHTS = " + Pass(ok));
            sb.AppendLine();
            return ok;
        }

        /// <summary>
        /// Point the car 12 degrees off the road, release everything, and report how far
        /// the BODY rotates back toward the road on its own over the window.
        ///
        /// World rotation is the right thing to measure and road-relative yaw is not: the
        /// road-relative yaw changes in a corner whatever the car does, simply because the
        /// road is moving. Only the body's own world rotation can be the alignment term's
        /// doing, and with no input and no drift there is nothing else that could produce
        /// any. Signed so that positive means "turned back toward the road".
        /// </summary>
        private static float YawBledOver(TrackModel track, HandlingProfile p, float z, float seconds)
        {
            var sim = new CarSimulation(track, p);
            sim.Reset(z);
            sim.Speed = p.maxSpeed;
            var input = new NoInput();
            sim.Step(Step, input);

            // 12 degrees of nose-LEFT, imposed on the WORLD heading so the road-relative
            // yaw is exactly -12 degrees at the start whatever the road is doing.
            sim.WorldHeading = track.WorldFrame.HeadingAt(sim.Z) - 12f * Mathf.Deg2Rad;

            float world0 = sim.WorldHeading;
            int n = Mathf.RoundToInt(seconds / Step);
            for (int i = 0; i < n; i++) sim.Step(Step, input);

            return sim.WorldHeading - world0;
        }

        // ------------------------------------------------------------------ [A5]

        /// <summary>
        /// WHAT EACH CORNER ACTUALLY COSTS, now that the player has to drive it.
        ///
        /// The curvature difficulty bands in TrackBuilder were solved against the old
        /// force balance, where a corner was hard because it PUSHED the car outward and
        /// steering had to cancel the push. Nothing about that is true any more: a corner
        /// is hard because following it needs rotation, and the rotation available falls
        /// with speed while the rotation required rises with it. So the whole ladder has
        /// to be re-measured, and this is the measurement.
        ///
        /// For each corner it solves the one question that decides everything else - what
        /// is the highest speed at which FULL LOCK still turns as fast as the road does:
        ///
        ///     hold at v  when  maxYawRate * authority(v) * gate(v)  >=  curvature * v
        ///
        /// and reports it as a fraction of top speed. Above about 97% that is flat out;
        /// below about 88% the brake is the only way to arrive at that speed, and in
        /// between a lift will do it. Nothing is asserted: the brief asks for the circuit
        /// to be MEASURED against the new car and left alone until a human has driven it.
        /// </summary>
        public static void CornerDifficulty(HandlingProfile p, TrackModel track, StringBuilder sb)
        {
            sb.AppendLine("[A5] WHAT EACH CORNER COSTS THE PLAYER  (measured, not asserted)");
            sb.AppendLine("     turn | curve |  degrees | holdable up to | what it asks for");
            sb.AppendLine("     -----+-------+----------+----------------+------------------");

            TurnInfo[] turns = track.Layout.Turns;
            int flat = 0, lift = 0, brake = 0;

            for (int i = 0; i < turns.Length; i++)
            {
                float curve = Mathf.Abs(turns[i].curve);

                // Walk down from top speed until full lock can hold this curvature.
                float frac = 1f;
                while (frac > 0.30f && p.HoldableCurve(frac) < curve) frac -= 0.01f;

                string verdict;
                if (frac >= 0.97f)      { verdict = "flat out";              flat++;  }
                else if (frac >= 0.88f) { verdict = "a lift";                lift++;  }
                else                    { verdict = "THE BRAKE";             brake++; }

                sb.AppendLine("      T" + turns[i].number.ToString("00")
                    + " | " + turns[i].curve.ToString("F1").PadLeft(5)
                    + " | " + (turns[i].degrees.ToString("F0") + " deg").PadLeft(8)
                    + " | " + ((frac * 100f).ToString("F0") + "% of top").PadLeft(14)
                    + " | " + verdict);
            }

            sb.AppendLine("     " + flat + " flat out, " + lift + " want a lift, "
                          + brake + " want the brake.");
            sb.AppendLine("     Full lock holds curvature " + p.HoldableCurve(1f).ToString("F1")
                          + " at top speed, " + p.HoldableCurve(0.8f).ToString("F1") + " at 80%, "
                          + p.HoldableCurve(0.7f).ToString("F1") + " at 70% - which is why the brake");
            sb.AppendLine("     pays better than linearly: the rotation needed falls with speed while");
            sb.AppendLine("     the rotation available RISES, because authority is higher lower down.");
            sb.AppendLine();
        }

        // ------------------------------------------------------------------ report

        public static string RunAll(HandlingProfile profile, TrackModel productionTrack)
        {
            StringBuilder sb = new StringBuilder(8192);
            if (profile == null) return "no HandlingProfile";

            sb.AppendLine("================ AUTO-TURN REGRESSION ================");
            sb.AppendLine("profile: " + profile.name);
            sb.AppendLine("the question: does the ROAD supply rotation the PLAYER should be supplying?");
            sb.AppendLine();

            bool a1 = SyntheticCorner(profile, sb);
            bool a2 = RealCorners(profile, productionTrack, sb);
            bool a3 = StraightIsStraight(profile, sb);
            bool a4 = AlignmentIsStraightOnly(profile, sb);
            CornerDifficulty(profile, productionTrack, sb);

            sb.AppendLine("AUTO-TURN VERDICT = " + Pass(a1 && a2 && a3 && a4));
            sb.AppendLine("======================================================");
            return sb.ToString();
        }

        private static string Deg(float radians) { return (radians * Mathf.Rad2Deg).ToString("F1") + " deg"; }
        private static string Pct(float f)       { return (f * 100f).ToString("F1") + "%"; }
        private static string Pass(bool b)       { return b ? "PASS" : "**FAIL**"; }
    }
}
