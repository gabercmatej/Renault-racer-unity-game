using System;
using System.Text;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;
using RenaultRacer.InputSystem;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// GRASS RECOVERY. What state does the grass leave the car in when it comes back
    /// onto the tarmac, and does the tarmac then hold it?
    ///
    /// Maj, playtest of a501969: drive onto the grass, hold the steering hard to get
    /// back, rejoin - and the car does not settle; it glides on across the road toward
    /// the other side. This drives the real CarSimulation on the real circuit through
    /// exactly that: off the road at a shallow angle, a human beat of reaction on the
    /// grass, full lock back toward the road, rejoin, a human beat of reaction on the
    /// tarmac, then either let go or steer the nose back down the road.
    ///
    /// Everything is measured at the rejoin (heading off the road, rotation, sideways
    /// slide, and how much of the car's road-crossing speed is HEADING and how much is
    /// SLIDE) and after it (how far across the road the car carries, how long until it
    /// stops crossing). The same inputs are also run on a copy of the profile whose
    /// grass is tarmac - the same manoeuvre entirely on the road - so the grass's own
    /// contribution is a difference, not a guess.
    /// </summary>
    public static class GrassRecoveryProbe
    {
        private const float Step = 1f / 60f;
        private const float Hw = RoadGeometry.RoadHalfWidth;

        // ---- the manoeuvre ----
        /// <summary>Heading off the road, degrees, as the car rolls onto the grass.</summary>
        public const float EntryYawDeg = 10f;
        /// <summary>Seconds on the grass before the player reacts and steers back.</summary>
        public const float GrassReaction = 0.20f;
        /// <summary>Seconds the lock stays held after the car is back on the tarmac.</summary>
        public static float RoadReaction = 0.15f;
        /// <summary>How long after the rejoin is graded.</summary>
        public const float PostSeconds = 3.0f;
        /// <summary>Road crossing speed, half-widths/s, that counts as settled.</summary>
        public const float SettledLatVel = 0.20f;
        /// <summary>How long it must stay settled to count.</summary>
        public const float SettledHold = 0.30f;

        public enum Post { Release, Correct }

        private sealed class Held : IDriveInput
        {
            public float Steer;
            public float SteerAxis { get { return Steer; } }
            public bool BrakeHeld { get { return false; } }
            public bool HandbrakeHeld { get { return false; } }
        }

        public struct Result
        {
            public bool reachedGrass, cameBack, hitWall;
            public float grassSlideTime;   // seconds on the grass with |slip angle| > 5 deg
            public float grassTime, grassPeakYaw, grassMaxSlip, grassDepth, grassEntrySpeed, grassMinSpeed;
            // at the rejoin. Signs: + = pointing / moving ACROSS the road, away from the
            // grass the car came from.
            public float reYaw, reYawRate, reLatVel, reHeadingLat, reSlipLat, reSlipAngle, reSpeed;
            // at the end of the road reaction (the moment the player lets go / corrects)
            public float rlYaw, rlLatVel, rlSlipLat;
            // after the rejoin
            public float cross;          // furthest the car carried across the road, half-widths, from the rejoin point
            public float settle;         // seconds from the rejoin until |lateral vel| stays < SettledLatVel
            public bool farGrass;        // left the road on the FAR side
            public bool backOnGrass;     // went back onto the grass it came from
            public float peakYawAfter;   // worst |yaw| after the reaction, degrees
            public float bite;           // seconds from the rejoin until the slide is under 0.1 half-widths/s
        }

        public static Result Run(HandlingProfile p, TrackModel t, int seg, int side, float speedPct,
                                 float entryYawDeg, float grassDelay, Post post,
                                 Action<CarSimulation> atRejoin, StringBuilder trace)
        {
            var sim = new CarSimulation(t, p);
            var input = new Held();
            sim.Reset(seg * RoadGeometry.SegmentLength);
            sim.Speed = p.maxSpeed * speedPct;
            sim.X = side * 0.95f;
            sim.PointAt(side * entryYawDeg * Mathf.Deg2Rad);

            Result r = new Result { settle = 99f, grassMinSpeed = 9f, bite = 99f };
            float time = 0f;
            int n;

            // ---- roll onto the grass, hands off ----
            n = Mathf.RoundToInt(2f / Step);
            for (int i = 0; i < n && !sim.OffRoad; i++) { sim.Step(Step, input); time += Step; }
            if (!sim.OffRoad) return r;
            r.reachedGrass = true;
            r.grassEntrySpeed = sim.SpeedPct;
            if (trace != null) trace.AppendLine("      --- on the grass ---");

            // ---- on the grass: a beat of reaction, then full lock back to the road ----
            float tg = 0f;
            n = Mathf.RoundToInt(8f / Step);
            for (int i = 0; i < n && sim.OffRoad; i++)
            {
                input.Steer = tg < grassDelay ? 0f : -side;
                sim.Step(Step, input); time += Step; tg += Step;
                r.grassPeakYaw = Mathf.Max(r.grassPeakYaw, Mathf.Abs(sim.Yaw) * Mathf.Rad2Deg);
                r.grassMaxSlip = Mathf.Max(r.grassMaxSlip, Mathf.Abs(sim.SlipVel) / Hw);
                r.grassDepth = Mathf.Max(r.grassDepth, Mathf.Abs(sim.X));
                r.grassMinSpeed = Mathf.Min(r.grassMinSpeed, sim.SpeedPct);
                if (Mathf.Abs(sim.SlipAngle) > 5f * Mathf.Deg2Rad) r.grassSlideTime += Step;
                Segment sg = t[t.SegmentIndexAt(sim.Z)];
                if (sim.X <= sg.barrierLeft + 0.03f || sim.X >= sg.barrierRight - 0.03f) r.hitWall = true;
                Trace(trace, time, input.Steer, sim);
            }
            if (sim.OffRoad) return r;
            r.cameBack = true;
            r.grassTime = tg;
            if (atRejoin != null) atRejoin(sim);

            float across = -side;     // the direction of the far side of the road
            float x0 = sim.X;
            r.reYaw = sim.Yaw * across * Mathf.Rad2Deg;
            r.reYawRate = sim.WorldYawRate * across;
            r.reLatVel = sim.LateralVel * across;
            r.reHeadingLat = sim.Speed * Mathf.Sin(sim.Yaw) / Hw * across;
            r.reSlipLat = sim.SlipVel * Mathf.Cos(sim.Yaw) / Hw * across;
            r.reSlipAngle = sim.SlipAngle * Mathf.Rad2Deg;
            r.reSpeed = sim.SpeedPct;
            if (trace != null) trace.AppendLine("      --- back on the tarmac ---");

            // ---- on the tarmac: the lock stays on for a beat, then release / correct ----
            float tp = 0f, calm = 0f;
            if (Mathf.Abs(sim.SlipVel) < 0.1f * Hw) r.bite = 0f;
            bool reacted = false;
            n = Mathf.RoundToInt(PostSeconds / Step);
            for (int i = 0; i < n; i++)
            {
                if (tp < RoadReaction) input.Steer = -side;
                else
                {
                    if (!reacted)
                    {
                        reacted = true;
                        r.rlYaw = sim.Yaw * across * Mathf.Rad2Deg;
                        r.rlLatVel = sim.LateralVel * across;
                        r.rlSlipLat = sim.SlipVel * Mathf.Cos(sim.Yaw) / Hw * across;
                    }
                    if (post == Post.Release) input.Steer = 0f;
                    else
                    {
                        // What a player does: point the nose back down the road. Digital, like
                        // the buttons, with a dead band so it lets go once the car is straight.
                        float y = sim.Yaw * Mathf.Rad2Deg;
                        input.Steer = y > 2f ? -1f : (y < -2f ? 1f : 0f);
                    }
                }
                sim.Step(Step, input); time += Step; tp += Step;
                if (r.bite > 90f && Mathf.Abs(sim.SlipVel) < 0.1f * Hw) r.bite = tp;
                if (reacted) r.peakYawAfter = Mathf.Max(r.peakYawAfter, Mathf.Abs(sim.Yaw) * Mathf.Rad2Deg);
                r.cross = Mathf.Max(r.cross, (sim.X - x0) * across);
                if (sim.X * across > 1f) r.farGrass = true;
                if (sim.X * side > 1f) r.backOnGrass = true;
                if (Mathf.Abs(sim.LateralVel) < SettledLatVel)
                {
                    calm += Step;
                    if (calm >= SettledHold && r.settle > 90f) r.settle = tp - calm;
                }
                else { calm = 0f; r.settle = 99f; }
                if (tp < 1.6f) Trace(trace, time, input.Steer, sim);
            }
            return r;
        }

        private static void Trace(StringBuilder sb, float time, float steer, CarSimulation s)
        {
            if (sb == null) return;
            int k = Mathf.RoundToInt(time / Step);
            if (k % 3 != 0) return;
            sb.AppendLine(string.Format(
                "      t {0,5:F2} {1,-6} steer {2,5:F2} z {3,7:F0}  x {4,6:F2}  yaw {5,6:F1}  wYawR {6,6:F2}  roadYawR {7,6:F2}"
                + "  latV {8,6:F2}  slipV {9,6:F2}  slipA {10,6:F1}  spd {11:F2}  grip {12:F2}  align {13,6:F2}",
                time, s.CurrentSurface, steer, s.Z, s.X, s.Yaw * Mathf.Rad2Deg, s.WorldYawRate, s.RoadYawRate,
                s.LateralVel, s.SlipVel / Hw, s.SlipAngle * Mathf.Rad2Deg, s.SpeedPct, s.SurfaceGrip,
                s.StraightAlignRate));
        }

        // ------------------------------------------------------------------ report

        public static HandlingProfile GrassAsRoad(HandlingProfile p)
        {
            HandlingProfile q = UnityEngine.Object.Instantiate(p);
            q.name = p.name + " (grass = tarmac)";
            q.offRoadGrip = 1f;
            q.offRoadDrag = 0f;
            q.offRoadSpeedCap = 1f;
            return q;
        }

        private static string Pass(bool ok) { return ok ? "PASS" : "**FAIL**"; }

        public static int FindStraight(TrackModel t)
        {
            // The pit straight: the first long run of curvature exactly zero.
            for (int i = 10; i < t.Count - 200; i++)
            {
                bool ok = true;
                for (int j = 0; j < 200 && ok; j++) ok = t[i + j].curve == 0f;
                if (ok) return i;
            }
            return 20;
        }

        public static int FindCorner(TrackModel t, float minCurve, float maxCurve)
        {
            for (int i = 0; i < t.Count - 40; i++)
            {
                float c = Mathf.Abs(t[i].curve);
                if (c < minCurve || c > maxCurve) continue;
                bool ok = true;
                for (int j = 0; j < 30 && ok; j++) ok = Mathf.Abs(t[i + j].curve - t[i].curve) < 0.01f;
                if (ok) return i;
            }
            return -1;
        }

        public static string Row(string label, Result r)
        {
            if (!r.reachedGrass) return "    " + label + "  never reached the grass";
            if (!r.cameBack) return "    " + label + "  never came back off the grass";
            return string.Format(
                "    {0,-34} {19}grass {1,4:F2}s slid {20,4:F2}s pkYaw {2,5:F1} slip {3,4:F2} spd {4:F2}->{5:F2} | REJOIN yaw {6,5:F1} yawR {7,5:F2} latV {8,5:F2} (head {9,5:F2} slide {10,5:F2}) spd {11:F2}"
                + " | +{12:F2}s yaw {13,5:F1} latV {14,5:F2} | bite {21,4:F2}s cross {15,4:F2}hw settle {16,5:F2}s{17}{18}",
                label, r.grassTime, r.grassPeakYaw, r.grassMaxSlip, r.grassEntrySpeed, r.grassMinSpeed,
                r.reYaw, r.reYawRate, r.reLatVel, r.reHeadingLat, r.reSlipLat, r.reSpeed,
                RoadReaction, r.rlYaw, r.rlLatVel,
                r.cross, r.settle, r.farGrass ? "  FAR-GRASS" : "", r.backOnGrass ? "  back-on-grass" : "",
                r.hitWall ? "WALL " : "     ", r.grassSlideTime, r.bite);
        }

        // ------------------------------------------------------------ thresholds
        /// <summary>[G1] Sideways SLIDE carried onto the tarmac, half-widths/s, clean excursion.</summary>
        public const float MaxRejoinSlide = 0.35f;
        /// <summary>
        /// [G1] The same after the car has struck the barrier out on the grass. Set to what
        /// SHIPS (measured 0.90 on Production, 2026-10-07): grass damps a slide at offRoadGrip
        /// (0.4) of the tarmac rate, so the barrier's inward slide survives onto the tarmac.
        /// offRoadGrip 0.55 brings it to 0.43 and 0.7 to 0.18, but both push HandlingProbe's
        /// two-thumb lap over its 20% off-road bound (21.7% / 27.3%), so neither shipped. This
        /// bound guards against it getting WORSE.
        /// </summary>
        public const float MaxRejoinSlideAfterWall = 1.0f;
        /// <summary>[G1] Seconds after the rejoin by which the tarmac has caught any slide (under 0.1 hw/s), clean excursion.</summary>
        public const float MaxBite = 0.10f;
        /// <summary>[G1] The same after a barrier hit out on the grass. Shipping value measured 0.40 s - see MaxRejoinSlideAfterWall.</summary>
        public const float MaxBiteAfterWall = 0.45f;
        /// <summary>[G2] Heading the grass may add to the angle the car left the road at, degrees.</summary>
        public const float MaxAddedYawDeg = 4f;
        /// <summary>[G3] How much further than the same manoeuvre on tarmac the car may carry, half-widths.</summary>
        public const float MaxExtraCross = 0.10f;
        /// <summary>[G5] Crossing allowed after a prompt release at the edge of a straight, half-widths.</summary>
        public const float MaxPromptCross = 0.80f;
        /// <summary>[G5] Seconds to settle after a prompt release at the edge of a straight.</summary>
        public const float MaxPromptSettle = 0.60f;

        private struct Place { public string name; public int seg; public int side; }

        public static string RunAll(HandlingProfile p, TrackModel track)
        {
            StringBuilder sb = new StringBuilder(65536);
            sb.AppendLine("=================== GRASS RECOVERY PROBE ===================");
            sb.AppendLine("profile: " + p.name + "   offRoadGrip " + p.offRoadGrip + "  offRoadYawLoss " + p.offRoadYawLoss
                          + "  offRoadDrag " + p.offRoadDrag + "  offRoadSpeedCap " + p.offRoadSpeedCap
                          + "  surfaceGripBlend " + p.surfaceGripBlend + "  maxLateralAccel " + p.maxLateralAccel
                          + "  grip " + p.grip + " x " + p.lateralDamping);
            int straight = FindStraight(track) + 10;
            int corner = FindCorner(track, 4f, 5f);
            if (corner < 0) corner = FindCorner(track, 2f, 9f);
            // A positive curve throws the car toward -x, so for a right-hander the outside is the left.
            int outside = track[corner].curve > 0f ? -1 : 1;
            sb.AppendLine("straight seg " + straight + " (curve " + track[straight].curve + ", barriers "
                          + track[straight].barrierLeft + "/" + track[straight].barrierRight + ")   corner seg "
                          + corner + " (curve " + track[corner].curve + ", barriers "
                          + track[corner].barrierLeft + "/" + track[corner].barrierRight + ", outside = "
                          + (outside < 0 ? "left" : "right") + ")");
            sb.AppendLine("manoeuvre: roll onto the grass at an angle, " + GrassReaction + "s hands off on the grass, full lock back,"
                          + " the lock held " + RoadReaction + "s more on the tarmac, then RELEASE or CORRECT (digital, nose back"
                          + " down the road). latV / slide in half-widths/s, + = toward the FAR side. The road is 2 half-widths wide.");
            sb.AppendLine("'TARMAC' = the same inputs on a copy of the profile whose grass is tarmac (grip 1, no drag, no cap).");
            sb.AppendLine();

            HandlingProfile road = GrassAsRoad(p);
            Place[] places =
            {
                new Place { name = "straight L", seg = straight, side = -1 },
                new Place { name = "straight R", seg = straight, side = 1 },
                new Place { name = "corner inside", seg = corner, side = -outside },
                new Place { name = "corner outside", seg = corner, side = outside },
            };
            float[] speeds = { 0.95f, 0.60f };
            float[] angles = { 10f, 20f };

            bool g1 = true, g2 = true, g3 = true, g4 = true, g5 = true, ran = true;
            float worstSlide = 0f, worstWallSlide = 0f, worstBite = 0f, worstWallBite = 0f, worstAdded = -99f, worstExtra = -99f, worstMirror = 0f;

            sb.AppendLine("[G] THE GRADED MATRIX - every place x speed x angle x (release, correct), real grass and TARMAC");
            foreach (Place pl in places)
                foreach (float spd in speeds)
                    foreach (float ang in angles)
                        foreach (Post post in new[] { Post.Release, Post.Correct })
                        {
                            Result a = Run(p, track, pl.seg, pl.side, spd, ang, GrassReaction, post, null, null);
                            Result b = Run(road, track, pl.seg, pl.side, spd, ang, GrassReaction, post, null, null);
                            string lbl = string.Format("{0} {1:F2} {2,2}deg {3}", pl.name, spd, ang, post);
                            sb.AppendLine(Row(lbl, a));
                            sb.AppendLine(Row("  TARMAC", b));
                            if (!a.reachedGrass || !a.cameBack || !b.reachedGrass || !b.cameBack) { ran = false; continue; }

                            float slide = Mathf.Abs(a.reSlipLat);
                            if (a.hitWall) worstWallSlide = Mathf.Max(worstWallSlide, slide);
                            else worstSlide = Mathf.Max(worstSlide, slide);
                            if (a.hitWall) worstWallBite = Mathf.Max(worstWallBite, a.bite);
                            else worstBite = Mathf.Max(worstBite, a.bite);
                            g1 &= a.hitWall ? (slide <= MaxRejoinSlideAfterWall && a.bite <= MaxBiteAfterWall)
                                            : (slide <= MaxRejoinSlide && a.bite <= MaxBite);

                            if (pl.seg == straight)
                            {
                                float added = Mathf.Abs(a.reYaw) - ang;
                                worstAdded = Mathf.Max(worstAdded, added);
                                g2 &= added <= MaxAddedYawDeg;
                            }

                            float extra = a.cross - b.cross;
                            worstExtra = Mathf.Max(worstExtra, extra);
                            g3 &= extra <= MaxExtraCross;

                            if (pl.side < 0 && pl.seg == straight)
                            {
                                Result m = Run(p, track, pl.seg, 1, spd, ang, GrassReaction, post, null, null);
                                float d = Mathf.Abs(m.cross - a.cross) + Mathf.Abs(m.reYaw - a.reYaw) * 0.01f;
                                worstMirror = Mathf.Max(worstMirror, d);
                                g4 &= d <= 0.02f;
                            }
                        }
            sb.AppendLine();

            sb.AppendLine(string.Format("[G1] NO SLIDE IS CARRIED ONTO THE TARMAC: clean excursion - worst slide at the rejoin {0:F2} hw/s"
                + " (<= {1:F2}), caught within {2:F2}s (<= {3:F2});  after a barrier hit on the grass - {4:F2} hw/s (<= {5:F2}),"
                + " caught within {6:F2}s (<= {7:F2})   {8}",
                worstSlide, MaxRejoinSlide, worstBite, MaxBite, worstWallSlide, MaxRejoinSlideAfterWall, worstWallBite,
                MaxBiteAfterWall, Pass(g1)));
            sb.AppendLine(string.Format("[G2] THE GRASS ADDS NO HEADING (straights): rejoin yaw minus the angle the car left at, worst"
                + " {0:+0.0;-0.0} deg (<= {1:F0})   {2}", worstAdded, MaxAddedYawDeg, Pass(g2)));
            sb.AppendLine(string.Format("[G3] GRASS NEVER CARRIES THE CAR FURTHER ACROSS THAN THE SAME INPUTS ON TARMAC: worst extra"
                + " crossing {0:+0.00;-0.00} hw (<= {1:F2})   {2}", worstExtra, MaxExtraCross, Pass(g3)));
            sb.AppendLine(string.Format("[G4] LEFT AND RIGHT ARE MIRRORS: worst difference {0:F3} (<= 0.02)   {1}", worstMirror, Pass(g4)));

            // ---- G5: a prompt release at the edge of a straight -----------------------
            sb.AppendLine("[G5] A PROMPT RELEASE AT THE EDGE OF A STRAIGHT SETTLES ON THE ROAD (no reaction on the tarmac)");
            float saved = RoadReaction;
            RoadReaction = 0f;
            foreach (float spd in speeds)
                foreach (float ang in new[] { 5f, 10f, 20f, 30f })
                {
                    Result r = Run(p, track, straight, -1, spd, ang, GrassReaction, Post.Release, null, null);
                    bool ok = r.cameBack && !r.farGrass && r.cross <= MaxPromptCross && r.settle <= MaxPromptSettle;
                    g5 &= ok;
                    sb.AppendLine(Row(string.Format("straight L {0:F2} {1,2}deg", spd, ang), r) + "   " + Pass(ok));
                }
            RoadReaction = saved;
            sb.AppendLine(string.Format("    cross <= {0:F2} hw, settled (|latV| < {1:F2} for {2:F2}s) within {3:F2}s, never onto the far grass   {4}",
                MaxPromptCross, SettledLatVel, SettledHold, MaxPromptSettle, Pass(g5)));
            sb.AppendLine();

            // ---- READ, not graded: the glide Maj reported ---------------------------
            sb.AppendLine("[R] READ, NOT GRADED - the glide Maj reported. It is the heading the held lock builds, and the same");
            sb.AppendLine("    inputs on TARMAC glide as far or further, so no grass value can remove it (see the diagnosis).");
            foreach (Place pl in places)
            {
                if (pl.side > 0 && pl.seg == straight) continue;
                foreach (Post post in new[] { Post.Release, Post.Correct })
                {
                    Result a = Run(p, track, pl.seg, pl.side, 0.95f, 10f, GrassReaction, post, null, null);
                    Result b = Run(road, track, pl.seg, pl.side, 0.95f, 10f, GrassReaction, post, null, null);
                    sb.AppendLine(string.Format("    {0,-15} {1,-8} grass: rejoin yaw {2,5:F1} -> {3,5:F1} after {4:F2}s, cross {5:F2} hw{6}"
                        + "    tarmac: rejoin yaw {7,5:F1} -> {8,5:F1}, cross {9:F2} hw{10}",
                        pl.name, post, a.reYaw, a.rlYaw, RoadReaction, a.cross, a.farGrass ? " FAR-GRASS" : "",
                        b.reYaw, b.rlYaw, b.cross, b.farGrass ? " FAR-GRASS" : ""));
                }
            }
            sb.AppendLine();

            // ---- diagnostics --------------------------------------------------------
            sb.AppendLine("[D3] COUNTERFACTUALS AT THE REJOIN - which state carries the glide? (0.95, 10 deg, release)");
            foreach (Place pl in new[] { places[0], places[2], places[3] })
            {
                sb.AppendLine(Row(pl.name + " as is", Run(p, track, pl.seg, pl.side, 0.95f, 10f, GrassReaction, Post.Release, null, null)));
                sb.AppendLine(Row("  slide zeroed", Run(p, track, pl.seg, pl.side, 0.95f, 10f, GrassReaction, Post.Release,
                    s => { s.SlipVel = 0f; }, null)));
                sb.AppendLine(Row("  rotation zeroed", Run(p, track, pl.seg, pl.side, 0.95f, 10f, GrassReaction, Post.Release,
                    s => { s.WorldYawRate = 0f; }, null)));
                sb.AppendLine(Row("  heading zeroed", Run(p, track, pl.seg, pl.side, 0.95f, 10f, GrassReaction, Post.Release,
                    s => { s.PointAt(0f); }, null)));
            }
            sb.AppendLine();

            sb.AppendLine("[D5] REACTION ON THE TARMAC x ENTRY ANGLE, straight L 0.95 - real grass vs TARMAC");
            foreach (float ang in new[] { 5f, 10f, 20f, 30f })
                foreach (float rr in new[] { 0f, 0.15f, 0.30f })
                {
                    RoadReaction = rr;
                    foreach (Post post in new[] { Post.Release, Post.Correct })
                    {
                        sb.AppendLine(Row(string.Format("{0,2}deg react {1:F2} {2}", ang, rr, post),
                            Run(p, track, straight, -1, 0.95f, ang, GrassReaction, post, null, null)));
                        sb.AppendLine(Row("  TARMAC", Run(road, track, straight, -1, 0.95f, ang, GrassReaction, post, null, null)));
                    }
                }
            RoadReaction = saved;
            sb.AppendLine();

            sb.AppendLine("[D4] PROFILE VARIANTS - diagnostic only, none applied (0.95, 10 deg)");
            Variant(sb, track, places, p, "as shipped", q => { });
            Variant(sb, track, places, p, "offRoadGrip 0.7 + offRoadYawLoss 0.7 (same grass rotation)", q => { q.offRoadGrip = 0.7f; q.offRoadYawLoss = 0.7f; });
            Variant(sb, track, places, p, "offRoadYawLoss 0.8 (grass rotates 52%)", q => { q.offRoadYawLoss = 0.8f; });
            Variant(sb, track, places, p, "surfaceGripBlend 60", q => { q.surfaceGripBlend = 60f; });
            sb.AppendLine();

            sb.AppendLine("[T1] TRACE straight L 0.95 10deg, release");
            StringBuilder tr = new StringBuilder();
            Run(p, track, straight, -1, 0.95f, 10f, GrassReaction, Post.Release, null, tr);
            sb.Append(tr);
            sb.AppendLine("[T2] TRACE corner outside 0.95 10deg, correct");
            tr.Length = 0;
            Run(p, track, corner, outside, 0.95f, 10f, GrassReaction, Post.Correct, null, tr);
            sb.Append(tr);
            UnityEngine.Object.DestroyImmediate(road);

            bool all = g1 && g2 && g3 && g4 && g5 && ran;
            if (!ran) sb.AppendLine("a scenario never reached the grass or never came back   **FAIL**");
            sb.AppendLine("GRASS RECOVERY = " + Pass(all));
            return sb.ToString();
        }

        private static void Variant(StringBuilder sb, TrackModel t, Place[] places, HandlingProfile p,
                                    string name, Action<HandlingProfile> edit)
        {
            HandlingProfile q = UnityEngine.Object.Instantiate(p);
            edit(q);
            sb.AppendLine("  " + name);
            foreach (Place pl in places)
                foreach (Post post in new[] { Post.Release, Post.Correct })
                    sb.AppendLine(Row(string.Format("{0} {1}", pl.name, post), Run(q, t, pl.seg, pl.side, 0.95f, 10f, GrassReaction, post, null, null)));
            UnityEngine.Object.DestroyImmediate(q);
        }
    }
}
