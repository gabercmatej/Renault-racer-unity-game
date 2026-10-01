using System.IO;
using System.Text;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Scripted verification of the CIRCUIT, as opposed to the car (HandlingProbe) or
    /// the rules (RaceProbe). Editor-only, never ships.
    ///
    /// It exists because every claim a track layout makes is checkable, and the two
    /// that bit V1 were checkable and were not checked:
    ///
    ///   * the lap did not close. Final heading 258.7 degrees instead of 360, and the
    ///     last point 79.6% of the map's own extent away from the first (plan 11.5).
    ///     The minimap drew a closing chord across a road nobody drives.
    ///   * corners delivered about 61% of their nominal angle, because AddCurve(n,n,n)
    ///     spends two thirds of a corner ramping on and off.
    ///
    /// Neither is a matter of taste. Both are integrals over the segment list, and
    /// both are measured below, alongside two behavioural tests that are the point of
    /// the whole exercise: every numbered corner has to REQUIRE the player, and every
    /// straight has to be exactly inert.
    /// </summary>
    public static class TrackLayoutProbe
    {
        private const float Step = 1f / 60f;
        private const string OutputDir = "Builds/TrackDebug";

        public static string RunAll(HandlingProfile profile)
        {
            TrackModel track = TrackBuilder.BuildMainTrack();
            StringBuilder sb = new StringBuilder(16384);

            sb.AppendLine("================= TRACK LAYOUT VERIFICATION =================");
            Shape(track, sb);
            Straights(track, sb);
            Turns(track, sb);
            Sectors(track, sb);
            Elevation(track, sb);
            if (profile != null)
            {
                HandsOff(track, profile, sb);
                TrueStraights(track, profile, sb);
                LapTime(track, profile, sb);
            }
            else
            {
                sb.AppendLine("no HandlingProfile - the driving tests were skipped");
                sb.AppendLine();
            }
            Export(track, sb);
            sb.AppendLine("=============================================================");
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// Walk the segment list the way the renderer and the minimap do
        /// (heading += curve / SegmentLength per segment) and require the walk to
        /// come back to where it started, pointing the way it started.
        /// </summary>
        private static void Shape(TrackModel track, StringBuilder sb)
        {
            float heading = 0f, left = 0f, right = 0f;
            float x = 0f, z = 0f;
            float minX = 0f, maxX = 0f, minZ = 0f, maxZ = 0f;

            for (int i = 0; i < track.Count; i++)
            {
                float c = track[i].curve;
                heading += c / RoadGeometry.SegmentLength;
                if (c > 0f) right += c / RoadGeometry.SegmentLength;
                else if (c < 0f) left += c / RoadGeometry.SegmentLength;

                x += Mathf.Sin(heading) * RoadGeometry.SegmentLength;
                z += Mathf.Cos(heading) * RoadGeometry.SegmentLength;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            float extent = Mathf.Max(maxX - minX, maxZ - minZ);
            float gap = Mathf.Sqrt(x * x + z * z);
            float headingDeg = heading * Mathf.Rad2Deg;
            float headingErr = headingDeg - 360f;

            sb.AppendLine("[T1] THE LAP CLOSES");
            sb.AppendLine("    segments " + track.Count
                          + "   length " + track.Length.ToString("F0") + " units"
                          + "   extent " + extent.ToString("F0") + " units");
            sb.AppendLine("    start (0, 0)   end (" + x.ToString("F1") + ", " + z.ToString("F1") + ")");
            sb.AppendLine("    closure error " + gap.ToString("F1") + " units = "
                          + (100f * gap / Mathf.Max(extent, 1f)).ToString("F3") + "% of extent");
            sb.AppendLine("    heading  start 0.000 deg   end " + headingDeg.ToString("F3")
                          + " deg   error " + headingErr.ToString("F3") + " deg");
            sb.AppendLine("    rotation  right +" + (right * Mathf.Rad2Deg).ToString("F2")
                          + " deg   left " + (left * Mathf.Rad2Deg).ToString("F2")
                          + " deg   net " + headingDeg.ToString("F2") + " deg");
            sb.AppendLine("    closes on itself (<2% of extent)=" + Pass(gap < extent * 0.02f)
                          + "  net rotation 360 +/-2=" + Pass(Mathf.Abs(headingErr) <= 2f));
            sb.AppendLine();
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// A "straight" here means curve == 0 EXACTLY, not "nearly zero". The
        /// distinction is the whole point of a reset straight: a pair of opposite
        /// lobes with a bit of residual curvature between them still cancels, and
        /// that is how V1's esses neutralised themselves.
        /// </summary>
        private static void Straights(TrackModel track, StringBuilder sb)
        {
            int zeroed = 0;
            for (int i = 0; i < track.Count; i++)
                if (track[i].curve == 0f) zeroed++;

            sb.AppendLine("[T2] TRUE STRAIGHTS");
            sb.AppendLine("    segments with curve == 0 exactly: " + zeroed + " of " + track.Count
                          + " (" + (100f * zeroed / track.Count).ToString("F1") + "%)");
            sb.AppendLine("    runs of true straight, in driving order:");

            int runs = 0, longest = 0, longestAt = -1;
            int i2 = 0;
            while (i2 < track.Count)
            {
                if (track[i2].curve != 0f) { i2++; continue; }
                int start = i2;
                while (i2 < track.Count && track[i2].curve == 0f) i2++;
                int len = i2 - start;
                runs++;
                if (len > longest) { longest = len; longestAt = start; }
                sb.AppendLine("      [" + start.ToString().PadLeft(5) + ".."
                              + i2.ToString().PadLeft(5) + ")  " + len.ToString().PadLeft(4)
                              + " seg  " + (len * RoadGeometry.SegmentLength).ToString("F0").PadLeft(7)
                              + " u   " + (len / 60f).ToString("F2") + " s flat out");
            }
            sb.AppendLine("    " + runs + " runs   longest " + longest + " segments at " + longestAt);
            sb.AppendLine();
        }

        // ------------------------------------------------------------------
        private static void Turns(TrackModel track, StringBuilder sb)
        {
            TurnInfo[] turns = track.Layout.Turns;
            sb.AppendLine("[T3] NUMBERED CORNERS  (" + turns.Length + ")");
            sb.AppendLine("      #  dir    curve  entry hold exit total   curve range"
                          + "      delivered    segments");

            float net = 0f;
            bool ordered = true;
            int prevEnd = 0;
            for (int i = 0; i < turns.Length; i++)
            {
                TurnInfo t = turns[i];
                float lo = float.MaxValue, hi = float.MinValue, sum = 0f;
                int held = 0;
                for (int s = t.startSegment; s < t.endSegment; s++)
                {
                    float c = track[s].curve;
                    if (c < lo) lo = c;
                    if (c > hi) hi = c;
                    if (c == t.curve) held++;
                    sum += c;
                }
                float delivered = sum / RoadGeometry.SegmentLength * Mathf.Rad2Deg;
                net += delivered;
                if (t.startSegment < prevEnd) ordered = false;
                prevEnd = t.endSegment;

                int enter = t.holdSegment - t.startSegment;
                int exit = t.endSegment - t.holdSegment - held;
                sb.AppendLine(string.Format(
                    "     {0,2}  {1,-5} {2,6:0.0}  {3,4} {4,4} {5,4} {6,5}  {7,6:0.00}..{8,5:0.00}"
                    + "  {9,8:+0.00;-0.00} deg  [{10,5}..{11,5})",
                    t.number, t.IsLeft ? "LEFT" : "RIGHT", t.curve,
                    enter, held, exit, t.endSegment - t.startSegment,
                    lo, hi, delivered, t.startSegment, t.endSegment));

                // TurnInfo.degrees is what every other system reads. It must agree
                // with the road, or the minimap and the signage describe a corner
                // the car does not drive.
                if (Mathf.Abs(t.degrees - delivered) > 0.01f)
                    sb.AppendLine("      **TurnInfo.degrees " + t.degrees.ToString("F3")
                                  + " disagrees with the segments (" + delivered.ToString("F3") + ")**");
            }

            sb.AppendLine("    net from the turn records " + net.ToString("F2") + " deg");
            sb.AppendLine("    15 corners=" + Pass(turns.Length == 15)
                          + "  in driving order, non-overlapping=" + Pass(ordered)
                          + "  net 360 +/-2=" + Pass(Mathf.Abs(net - 360f) <= 2f));
            sb.AppendLine();
        }

        // ------------------------------------------------------------------
        private static void Sectors(TrackModel track, StringBuilder sb)
        {
            int[] s = track.Layout.SectorStartSegment;
            sb.AppendLine("[T4] SECTORS");
            for (int i = 0; i < s.Length; i++)
            {
                int end = (i + 1 < s.Length) ? s[i + 1] : track.Count;
                sb.AppendLine("    sector " + (i + 1) + "  [" + s[i] + ".." + end + ")   "
                              + (end - s[i]) + " segments   "
                              + (100f * (end - s[i]) / track.Count).ToString("F1") + "% of the lap"
                              + "   opens at z " + track.Layout.SectorStartZ(i).ToString("F0"));
            }

            // A checkpoint is an invisible anti-cheat gate; a sector line is a visible
            // timed feature. They must not be the same piece of road.
            int nearest = int.MaxValue;
            for (int i = 0; i < track.Count; i++)
            {
                if (track[i].checkpointId < 0) continue;
                for (int k = 0; k < s.Length; k++)
                    nearest = Mathf.Min(nearest, Mathf.Abs(i - s[k]));
            }
            sb.AppendLine("    closest checkpoint-to-sector-line gap: " + nearest + " segments");
            sb.AppendLine("    3 sectors=" + Pass(s.Length == 3)
                          + "  ascending=" + Pass(s.Length < 2 || (s[0] < s[1] && s[1] < s[2]))
                          + "  no checkpoint on a sector line (>=30)=" + Pass(nearest >= 30));
            sb.AppendLine();
        }

        // ------------------------------------------------------------------
        private static void Elevation(TrackModel track, StringBuilder sb)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            int loAt = 0, hiAt = 0;
            float up = 0f, down = 0f;
            int upAt = 0, downAt = 0;

            for (int i = 0; i < track.Count; i++)
            {
                float y = track[i].y;
                if (y < lo) { lo = y; loAt = i; }
                if (y > hi) { hi = y; hiAt = i; }
                float slope = (track[i + 1].y - y) / RoadGeometry.SegmentLength;
                if (slope > up) { up = slope; upAt = i; }
                if (slope < down) { down = slope; downAt = i; }
            }

            float start = track[0].y;
            float end = track[track.Count - 1].y;

            // The pit straight, the start/finish line and T01 are required to be dead
            // flat: a braking point you cannot see over is the one unfair thing on a
            // circuit that is otherwise readable (plan 11.2).
            TurnInfo[] turns = track.Layout.Turns;
            int t1End = turns.Length > 0 ? turns[0].endSegment : 0;
            int lastTurnEnd = turns.Length > 0 ? turns[turns.Length - 1].endSegment : 0;
            float pitWorst = 0f;
            for (int i = lastTurnEnd; i < track.Count; i++)
                pitWorst = Mathf.Max(pitWorst, Mathf.Abs(track[i + 1].y - track[i].y));
            for (int i = 0; i < t1End; i++)
                pitWorst = Mathf.Max(pitWorst, Mathf.Abs(track[i + 1].y - track[i].y));

            sb.AppendLine("[T5] ELEVATION");
            sb.AppendLine("    start " + start.ToString("F4") + "   end " + end.ToString("F4")
                          + "   closure error " + (end - start).ToString("F6") + " units");
            sb.AppendLine("    high " + hi.ToString("F0") + " at segment " + hiAt
                          + "   low " + lo.ToString("F0") + " at segment " + loAt
                          + "   total rise " + (hi - lo).ToString("F0") + " units"
                          + "   (camera height " + RoadGeometry.CameraHeight.ToString("F0") + ")");
            sb.AppendLine("    steepest climb  " + up.ToString("F4") + " at segment " + upAt);
            sb.AppendLine("    steepest descent " + down.ToString("F4") + " at segment " + downAt);
            sb.AppendLine("    pit straight + T01: largest height step "
                          + pitWorst.ToString("F6") + " units");
            // THE SLOPE BOUND IS THE AUTHORED BUDGET, not a number typed once.
            //
            // Elevation is authored as grade x segments and AddRoad eases each run with a
            // cosine, so the steepest point of a run is (pi/2) times its average - and the
            // whole circuit is then multiplied by ElevationScale. The bound is therefore
            // HillGrade * pi/2 * ElevationScale, with a little over for the ramps that sit
            // either side of a run boundary. Written as 0.05 it was really a bound on the
            // elevation SCALE, and it failed the moment the scale moved for reasons that
            // had nothing to do with the gradient budget being exceeded.
            float slopeBound = TrackBuilder.HillGrade * (Mathf.PI * 0.5f)
                             * TrackBuilder.ElevationScale * 1.05f;
            sb.AppendLine("    budget: grade " + TrackBuilder.HillGrade.ToString("F3")
                          + " x pi/2 x elevation scale " + TrackBuilder.ElevationScale.ToString("F2")
                          + " = " + slopeBound.ToString("F4")
                          + " (" + (Mathf.Atan(slopeBound) * Mathf.Rad2Deg).ToString("F1") + " degrees)");
            sb.AppendLine("    closes at its own datum=" + Pass(Mathf.Abs(end - start) < 0.001f)
                          + "  |slope| within the authored budget="
                          + Pass(up <= slopeBound && down >= -slopeBound)
                          + "  pit straight dead flat=" + Pass(pitWorst < 0.001f));
            sb.AppendLine();
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// THE TEST THE CIRCUIT EXISTS TO PASS.
        ///
        /// Drive up to each corner on the real track, then let go of everything and
        /// report how far the corner carries the car. A corner a do-nothing car
        /// completes on the road is not a corner; it is scenery the player steers
        /// through out of habit.
        ///
        /// Two numbers are reported and they answer different questions. Whether the
        /// car LEFT THE ROAD is the strict form. How far it was DISPLACED is the one
        /// that stays meaningful for a short corner: displacement from the centre
        /// line goes roughly as (angle^2 / curve), so a 20 degree kink cannot throw a
        /// car off the road at any curvature this model allows - it is over before
        /// the outward push has had time to move anything. Both are printed so the
        /// number is actionable instead of a bare verdict.
        ///
        /// "Clawed back" is the auto-steering check, and it must be zero: a car
        /// nobody is steering may be carried outward, but nothing in the model is
        /// allowed to bring it back toward the middle (HandlingProbe 9b, 9c).
        /// </summary>
        private static void HandsOff(TrackModel track, HandlingProfile p, StringBuilder sb)
        {
            sb.AppendLine("[T6] EVERY NUMBERED CORNER REQUIRES THE DRIVER");
            sb.AppendLine("     driven up to the corner, then nothing touched at all");

            TurnInfo[] turns = track.Layout.Turns;
            int offRoad = 0, clawed = 0, diluted = 0;

            for (int i = 0; i < turns.Length; i++)
            {
                TurnInfo t = turns[i];
                var sim = new CarSimulation(track, p);
                sim.Reset(0f);
                var inp = new HandlingProbe.ScriptedInput();

                float releaseZ = Mathf.Max(0, t.startSegment - 45) * RoadGeometry.SegmentLength;
                float guard = 0f;
                while (sim.Z < releaseZ && guard < 200f)
                {
                    inp.Steer = HandlingProbe.HoldLine(sim, 0f);
                    sim.Step(Step, inp);
                    guard += Step;
                }

                // Hand the corner the most generous start there is: centred, settled,
                // at whatever speed the approach bought.
                float entrySpeed = sim.Speed;
                sim.X = 0f; sim.Settle(); sim.Steer = 0f;
                inp.Steer = 0f;

                float endZ = t.endSegment * RoadGeometry.SegmentLength;
                float tt = 0f, left = -1f, peak = 0f, back = 0f;
                // "Clawed back" is looking for a MODEL that steers an untouched car back
                // toward the middle. A barrier does exactly that, deliberately and by
                // invariant - contact must always redirect inward, or the boundary could
                // trap the car - so contact steps are excluded. Without that exclusion
                // T02 and T09 reported half a road width of recovery, and every unit of it
                // was the wall doing its job.
                bool touched = false;
                while (sim.Z < endZ && tt < 30f)
                {
                    Segment here = track[track.SegmentIndexAt(sim.Z)];
                    sim.Step(Step, inp);
                    tt += Step;

                    float ax = Mathf.Abs(sim.X);
                    bool onWall = sim.X <= here.barrierLeft + 0.001f
                               || sim.X >= here.barrierRight - 0.001f;
                    if (onWall) touched = true;

                    if (ax > peak) peak = ax;
                    else if (!touched) back = Mathf.Max(back, peak - ax);
                    if (left < 0f && ax > 1f) left = tt;
                }

                // How long this corner is worth AT ITS OWN CURVATURE - the ramps
                // counted at what they actually deliver, not at their peak - against
                // how long a CONSTANT corner of that curvature takes to run an
                // untouched car off the road. The comparison is what separates a
                // corner that is short from a corner that is DILUTED, and dilution
                // is the V1 failure this probe exists to catch: a long corner that a
                // do-nothing car completes has had its curvature spent on ramps.
                float integral = 0f;
                for (int k = t.startSegment; k < t.endSegment; k++)
                    integral += Mathf.Abs(track[k].curve);
                float effectiveSeconds = integral / Mathf.Abs(t.curve) / 60f;
                float reference = SecondsUnaided(p, Mathf.Abs(t.curve));

                bool honest = left > 0f || effectiveSeconds < reference;
                if (left > 0f) offRoad++;
                if (back > 0.01f) clawed++;
                if (!honest) diluted++;

                sb.AppendLine(string.Format(
                    "    T{0:00} curve {1,5:0.0}  {2,3} seg   worth {3,4:0.00}s at that curvature"
                    + " (a constant one needs {4,4:0.00}s)   entry {5,3:0}% speed"
                    + "   exit |x| {6,5:0.00}   peak {7,5:0.00}   clawed back {8,6:0.000}"
                    + "   off the road after {9,-7}  {10}",
                    t.number, t.curve, t.endSegment - t.startSegment,
                    effectiveSeconds, reference,
                    entrySpeed / p.maxSpeed * 100f,
                    Mathf.Abs(sim.X), peak, back,
                    left > 0f ? left.ToString("F2") + "s" : "NEVER",
                    honest ? "" : "**DILUTED**"));
            }

            sb.AppendLine("    an untouched car runs out of road in " + offRoad
                          + " of " + turns.Length + " corners; the rest are SHORT rather than"
                          + " weak - a 20 degree kink is over before the outward push has moved"
                          + " anything, at any curvature this model allows.");
            sb.AppendLine("    no corner is diluted by its own ramps=" + Pass(diluted == 0)
                          + "  nothing but a barrier ever brings it back=" + Pass(clawed == 0));
            sb.AppendLine();
        }

        /// <summary>
        /// Seconds an untouched car stays on the road through a CONSTANT corner of
        /// this curvature, entered at top speed from the centre line. The yardstick
        /// the real corners are measured against, taken from the model rather than a
        /// table so it cannot go stale when the handling profile moves.
        /// </summary>
        private static float SecondsUnaided(HandlingProfile p, float curve)
        {
            const int Lead = 300;
            Segment[] segs = new Segment[20000];
            for (int i = 0; i < segs.Length; i++)
            {
                segs[i].curve = i < Lead ? 0f : curve;
                segs[i].y = 0f;
                segs[i].surface = Surface.Road;
                segs[i].barrierLeft = -400f;
                segs[i].barrierRight = 400f;
                segs[i].checkpointId = -1;
            }
            var sim = new CarSimulation(new TrackModel(segs), p);
            sim.Reset(0f);
            var inp = new HandlingProbe.ScriptedInput();
            for (int i = 0; i < 60 * 25 && sim.Z < Lead * RoadGeometry.SegmentLength; i++)
                sim.Step(Step, inp);
            sim.X = 0f; sim.Settle();

            float t = 0f;
            while (t < 8f)
            {
                sim.Step(Step, inp);
                t += Step;
                if (Mathf.Abs(sim.X) > 1f) return t;
            }
            return 8f;
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// The other half of the same claim. On a true straight, with nothing touched
        /// and nothing touching the car, every lateral state must be BIT-FOR-BIT zero.
        /// Not small - zero. Nothing in the model can move a centred, unsteered car on
        /// a curve==0 segment, so a tolerance here would be hiding something.
        /// </summary>
        private static void TrueStraights(TrackModel track, HandlingProfile p, StringBuilder sb)
        {
            sb.AppendLine("[T7] EVERY TRUE STRAIGHT IS EXACTLY INERT");

            int runs = 0, bad = 0;
            float worst = 0f;
            int worstAt = -1;

            int i = 0;
            while (i < track.Count)
            {
                if (track[i].curve != 0f) { i++; continue; }
                int start = i;
                while (i < track.Count && track[i].curve == 0f) i++;
                int end = i;
                if (end - start < 8) continue;
                runs++;

                var sim = new CarSimulation(track, p);
                sim.Reset(start * RoadGeometry.SegmentLength);
                sim.Speed = p.maxSpeed * 0.8f;
                var inp = new HandlingProbe.ScriptedInput();

                float x0 = sim.X;
                float w = 0f;
                float stopZ = end * RoadGeometry.SegmentLength;
                int guard = 0;
                while (sim.Z < stopZ && guard < 20000)
                {
                    sim.Step(Step, inp);
                    guard++;
                    w = Mathf.Max(w, Mathf.Abs(sim.Yaw));
                    w = Mathf.Max(w, Mathf.Abs(sim.YawRate));
                    w = Mathf.Max(w, Mathf.Abs(sim.LateralVel));
                    w = Mathf.Max(w, Mathf.Abs(sim.X - x0));
                }
                if (w != 0f) { bad++; if (w > worst) { worst = w; worstAt = start; } }
            }

            sb.AppendLine("    " + runs + " straight runs driven with no input, no contact, no drift");
            sb.AppendLine("    worst |yaw| / |yawRate| / |lateralVel| / |dx| across all of them: "
                          + worst.ToString("E2") + (worstAt >= 0 ? " at segment " + worstAt : ""));
            sb.AppendLine("    exactly zero on every straight=" + Pass(bad == 0));
            sb.AppendLine();
        }

        // ------------------------------------------------------------------
        private static void LapTime(TrackModel track, HandlingProfile p, StringBuilder sb)
        {
            sb.AppendLine("[T8] LAP TIME");
            sb.AppendLine("    physical floor (the whole lap at maxSpeed, which nothing can beat) "
                          + (track.Length / p.maxSpeed).ToString("F1") + " s");

            float flat = ScriptedLap(track, p, 2600f, 99f);
            float braked = ScriptedLap(track, p, 3400f, 4.2f);
            sb.AppendLine("    scripted racing line, no braking     " + flat.ToString("F1") + " s");
            sb.AppendLine("    scripted racing line, with braking   " + braked.ToString("F1") + " s");
            sb.AppendLine("    (these are bots on a fixed look-ahead, not a skill ceiling -"
                          + " read them as a band, not a target)");
            sb.AppendLine();
        }

        private static float ScriptedLap(TrackModel track, HandlingProfile p,
                                         float lookAhead, float brakeGain)
        {
            var sim = new CarSimulation(track, p);
            sim.Reset(0f);
            var inp = new HandlingProbe.ScriptedInput();
            float t = 0f;
            while (sim.Z < track.Length && t < 300f)
            {
                float ahead = 0f;
                for (int i = 1; i <= 6; i++) ahead += track.CurveAt(sim.Z + lookAhead * i / 6f);
                ahead /= 6f;
                float far = track.CurveAt(sim.Z + lookAhead * 2f);
                inp.Steer = HandlingProbe.HoldLine(sim, Mathf.Clamp(-ahead * 0.11f, -0.75f, 0.75f));
                inp.Brake = Mathf.Abs(far) * sim.SpeedPct * sim.SpeedPct > brakeGain;
                sim.Step(Step, inp);
                t += Step;
            }
            return t;
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// Two SVGs, because a closure number is a number and a circuit is a shape -
        /// and the V1 failure was one anybody would have spotted in a picture. SVG
        /// rather than a PNG on purpose: it is text, it is a few kilobytes, it diffs,
        /// and it needs no Texture2D round-trip.
        /// </summary>
        private static void Export(TrackModel track, StringBuilder sb)
        {
            sb.AppendLine("[T9] DEBUG EXPORT");
            try
            {
                Directory.CreateDirectory(OutputDir);
                string a = Path.Combine(OutputDir, "track-centreline.svg");
                string b = Path.Combine(OutputDir, "track-elevation.svg");
                File.WriteAllText(a, CentrelineSvg(track));
                File.WriteAllText(b, ElevationSvg(track));
                sb.AppendLine("    " + a);
                sb.AppendLine("    " + b);
            }
            catch (System.Exception e)
            {
                sb.AppendLine("    **could not write the debug export: " + e.Message + "**");
            }
            sb.AppendLine();
        }

        private static readonly System.Globalization.CultureInfo Inv =
            System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>
        /// SVG is not culture-aware: "30,0" is two numbers, not one, and on a machine
        /// with a comma decimal separator every coordinate this file writes would be
        /// silently corrupt. Every number that lands in the markup goes through here.
        /// </summary>
        private static string N(float v) { return v.ToString("0.0", Inv); }

        private static string CentrelineSvg(TrackModel track)
        {
            int n = track.Count;
            float[] px = new float[n + 1];
            float[] pz = new float[n + 1];
            float heading = 0f, x = 0f, z = 0f;
            float minX = 0f, maxX = 0f, minZ = 0f, maxZ = 0f;
            for (int i = 0; i < n; i++)
            {
                heading += track[i].curve / RoadGeometry.SegmentLength;
                x += Mathf.Sin(heading);
                z += Mathf.Cos(heading);
                px[i + 1] = x; pz[i + 1] = z;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            const float Pad = 30f, W = 1000f;
            float s = Mathf.Min((W - 2f * Pad) / Mathf.Max(maxX - minX, 0.001f),
                                (W - 2f * Pad) / Mathf.Max(maxZ - minZ, 0.001f));
            float h = (maxZ - minZ) * s + 2f * Pad;

            StringBuilder o = new StringBuilder(n * 20 + 4096);
            o.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(N(W))
             .Append("\" height=\"").Append(N(h)).Append("\">\n");
            o.Append("<rect width=\"100%\" height=\"100%\" fill=\"#101418\"/>\n<path d=\"");
            for (int i = 0; i <= n; i++)
            {
                o.Append(i == 0 ? "M " : " L ");
                o.Append(N(Pad + (px[i] - minX) * s)).Append(' ');
                o.Append(N(h - Pad - (pz[i] - minZ) * s));
            }
            o.Append("\" fill=\"none\" stroke=\"#4ec3ff\" stroke-width=\"3\"/>\n");

            TurnInfo[] turns = track.Layout.Turns;
            for (int i = 0; i < turns.Length; i++)
            {
                int m = turns[i].holdSegment;
                float cx = Pad + (px[m] - minX) * s;
                float cy = h - Pad - (pz[m] - minZ) * s;
                o.Append("<circle cx=\"").Append(N(cx)).Append("\" cy=\"").Append(N(cy))
                 .Append("\" r=\"4\" fill=\"")
                 .Append(turns[i].IsLeft ? "#ffb23e" : "#ff5f4e").Append("\"/>\n");
                o.Append("<text x=\"").Append(N(cx + 7f)).Append("\" y=\"").Append(N(cy - 6f))
                 .Append("\" fill=\"#e8eef4\" font-family=\"monospace\" font-size=\"14\">T")
                 .Append(turns[i].number.ToString("00", Inv)).Append("</text>\n");
            }

            int[] sec = track.Layout.SectorStartSegment;
            for (int i = 0; i < sec.Length; i++)
            {
                float cx = Pad + (px[sec[i]] - minX) * s;
                float cy = h - Pad - (pz[sec[i]] - minZ) * s;
                o.Append("<circle cx=\"").Append(N(cx)).Append("\" cy=\"").Append(N(cy))
                 .Append("\" r=\"7\" fill=\"none\" stroke=\"#8cff7a\" stroke-width=\"2\"/>\n");
            }
            o.Append("<circle cx=\"").Append(N(Pad + (px[0] - minX) * s))
             .Append("\" cy=\"").Append(N(h - Pad - (pz[0] - minZ) * s))
             .Append("\" r=\"7\" fill=\"#ffffff\"/>\n");
            o.Append("</svg>\n");
            return o.ToString();
        }

        private static string ElevationSvg(TrackModel track)
        {
            int n = track.Count;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float y = track[i].y;
                if (y < lo) lo = y;
                if (y > hi) hi = y;
            }
            float span = Mathf.Max(hi - lo, 1f);

            const float W = 1200f, H = 300f, Pad = 30f;
            StringBuilder o = new StringBuilder(n * 16 + 2048);
            o.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1200\" height=\"300\">\n");
            o.Append("<rect width=\"100%\" height=\"100%\" fill=\"#101418\"/>\n");

            float datumY = H - Pad - (0f - lo) / span * (H - 2f * Pad);
            o.Append("<line x1=\"").Append(N(Pad)).Append("\" y1=\"").Append(N(datumY))
             .Append("\" x2=\"").Append(N(W - Pad)).Append("\" y2=\"").Append(N(datumY))
             .Append("\" stroke=\"#3a4552\" stroke-width=\"1\"/>\n");

            TurnInfo[] turns = track.Layout.Turns;
            for (int i = 0; i < turns.Length; i++)
            {
                float cx = Pad + (float)turns[i].holdSegment / n * (W - 2f * Pad);
                o.Append("<line x1=\"").Append(N(cx)).Append("\" y1=\"").Append(N(Pad))
                 .Append("\" x2=\"").Append(N(cx)).Append("\" y2=\"").Append(N(H - Pad))
                 .Append("\" stroke=\"#2b3440\" stroke-width=\"1\"/>\n");
                o.Append("<text x=\"").Append(N(cx + 2f))
                 .Append("\" y=\"20\" fill=\"#93a3b4\" font-family=\"monospace\" font-size=\"11\">")
                 .Append(turns[i].number.ToString("00", Inv)).Append("</text>\n");
            }

            o.Append("<path d=\"");
            for (int i = 0; i <= n; i++)
            {
                o.Append(i == 0 ? "M " : " L ");
                o.Append(N(Pad + (float)i / n * (W - 2f * Pad))).Append(' ');
                o.Append(N(H - Pad - (track[i].y - lo) / span * (H - 2f * Pad)));
            }
            o.Append("\" fill=\"none\" stroke=\"#8cff7a\" stroke-width=\"2\"/>\n");

            o.Append("<text x=\"34\" y=\"290\" fill=\"#93a3b4\" font-family=\"monospace\" ")
             .Append("font-size=\"12\">low ").Append(lo.ToString("0", Inv))
             .Append(" u   high ").Append(hi.ToString("0", Inv))
             .Append(" u   (camera height ").Append(RoadGeometry.CameraHeight.ToString("0", Inv))
             .Append(")</text>\n</svg>\n");
            return o.ToString();
        }

        private static string Pass(bool b) { return b ? "PASS" : "**FAIL**"; }
    }
}
