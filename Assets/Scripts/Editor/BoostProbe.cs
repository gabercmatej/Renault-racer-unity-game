//
//   Unity.exe -batchmode -quit -projectPath <COPY> \
//     -executeMethod RenaultRacer.EditorTools.BoostProbe.RunFromCommandLine
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Race;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// The lightning boost, verified without a scene: BoostPickupSet (the rule) and
    /// CarSimulation.TriggerBoost (the effect) are both pure, so this drives the SAME code
    /// the game does, swept per step at the drawn car exactly as CarController does.
    /// </summary>
    public static class BoostProbe
    {
        private const float Step = 1f / 60f;
        private const float DistanceAhead = 1700f;   // CarView.distanceAhead in Main.unity
        private const string ProfilePath = "Assets/ScriptableObjects/Handling/Production.asset";
        private const string LogPath = "Logs/boost-probe.txt";

        [MenuItem("Renault Racer/Boost pickup probe", false, 47)]
        public static void RunFromMenu() { Debug.Log(Run()); }

        public static void RunFromCommandLine()
        {
            string report;
            try { report = Run(); }
            catch (Exception e) { Console.WriteLine("[BoostProbe] threw: " + e); EditorApplication.Exit(2); return; }
            Console.WriteLine(report);
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath, report);
            EditorApplication.Exit(report.Contains("**FAIL**") ? 1 : 0);
        }

        private static string Pass(bool ok) { return ok ? "PASS" : "**FAIL**"; }

        public static string Run()
        {
            HandlingProfile p = AssetDatabase.LoadAssetAtPath<HandlingProfile>(ProfilePath);
            if (p == null) return "**FAIL** no Production profile at " + ProfilePath;
            var tuning = new BoostTuning();
            var sb = new StringBuilder(4096);
            sb.AppendLine("================ BOOST PICKUP PROBE ================");
            sb.AppendLine("+" + (tuning.addedTopSpeed * 100f).ToString("F0") + "% top speed, +"
                          + tuning.addedAccel.ToString("F2") + " accel, " + tuning.holdSeconds + "s + "
                          + tuning.fadeSeconds + "s fade, radius " + tuning.radius);
            sb.AppendLine();

            Placement(tuning, sb);
            TakenOnce(p, tuning, sb);
            MissedWide(p, tuning, sb);
            RaisesThenExpires(p, tuning, sb);
            StillACar(p, tuning, sb);
            RestartResets(p, tuning, sb);
            BoostedLapAccepted(p, tuning, sb);
            BrakeSkidGating(p, sb);
            return sb.ToString();
        }

        // [B8] -----------------------------------------------------------------------
        /// <summary>
        /// BRAKE + STEER LAYS RUBBER, AND ONLY WHEN IT SHOULD. BrakeSkid is visual only:
        /// it must appear for a hard brake into a turn above ~90 km/h on tarmac, must not
        /// appear at parking speed, on grass, or for a dab - and must change nothing the
        /// physics feels (no drift, no grip loss).
        /// </summary>
        private static void BrakeSkidGating(HandlingProfile p, StringBuilder sb)
        {
            float peak, drift, grip;
            BrakeSkidRun(p, 1f, 0f, 0.6f, 30, out peak, out drift, out grip);
            bool fast = peak > 0.3f && drift == 0f && Mathf.Abs(grip - 1f) < 1e-4f;
            float plainGrip;
            float pd, pdr;
            BrakeSkidRun(p, 1f, 0f, 0f, 30, out pd, out pdr, out plainGrip);   // brake only, no steer
            bool straightNone = pd == 0f;
            float slowPeak, d2, g2;
            BrakeSkidRun(p, 0.3f, 0f, 1f, 30, out slowPeak, out d2, out g2);      // ~66 km/h
            bool slowNone = slowPeak == 0f;
            float grassPeak, d3, g3;
            BrakeSkidRun(p, 1f, 1.5f, 1f, 30, out grassPeak, out d3, out g3);     // on the grass
            bool grassNone = grassPeak == 0f;
            float dabPeak, d4, g4;
            BrakeSkidRun(p, 1f, 0f, 1f, 2, out dabPeak, out d4, out g4);          // a 2-frame dab
            bool dabNone = dabPeak == 0f;

            sb.AppendLine("[B8] BRAKE + STEER SKID MARKS (visual signal only)");
            sb.AppendLine("    220 km/h, brake + 0.6 steer: brake skid " + peak.ToString("F2") + ", drift " + drift.ToString("F2")
                          + ", grip " + grip.ToString("F2") + "  lays rubber, physics untouched=" + Pass(fast));
            sb.AppendLine("    brake alone, no steer: " + pd.ToString("F2") + "=" + Pass(straightNone)
                          + "   66 km/h: " + slowPeak.ToString("F2") + "=" + Pass(slowNone)
                          + "   on grass: " + grassPeak.ToString("F2") + "=" + Pass(grassNone)
                          + "   2-frame dab: " + dabPeak.ToString("F2") + "=" + Pass(dabNone));
            sb.AppendLine();
        }

        private static void BrakeSkidRun(HandlingProfile p, float speedFraction, float x, float steer, int brakeSteps,
                                         out float peak, out float worstDrift, out float minGrip)
        {
            TrackModel t = HandlingProbe.FlatTrack(4000);
            var sim = new CarSimulation(t, p);
            sim.Reset(2000f);
            sim.Speed = p.maxSpeed * speedFraction;
            sim.X = x;
            var inp = new HandlingProbe.ScriptedInput { Steer = steer };
            // Let the wheel wind on before the brake goes down, as a player turning in would.
            for (int i = 0; i < 12; i++) { sim.Step(Step, inp); sim.X = x; }
            peak = 0f; worstDrift = 0f; minGrip = 1f;
            inp.Brake = true;
            for (int i = 0; i < 60; i++)
            {
                if (i == brakeSteps) inp.Brake = false;
                sim.Step(Step, inp);
                sim.X = x;   // hold it on the surface under test, whatever the steering does
                peak = Mathf.Max(peak, sim.BrakeSkid);
                worstDrift = Mathf.Max(worstDrift, sim.DriftAmount);
                if (x == 0f) minGrip = Mathf.Min(minGrip, sim.CurrentGrip);
            }
        }

        private static BoostPickupSet One(TrackModel t, int seg, float x, float radius)
        {
            return new BoostPickupSet(new[] { seg }, new[] { x }, t.Length, radius);
        }

        /// <summary>Up to top speed on a straight, hands off.</summary>
        private static CarSimulation AtTopSpeed(HandlingProfile p, TrackModel t, HandlingProbe.ScriptedInput inp)
        {
            var sim = new CarSimulation(t, p);
            sim.Reset(0f);
            for (int i = 0; i < 60 * 10; i++) sim.Step(Step, inp);
            return sim;
        }

        /// <summary>One step, swept the way CarController sweeps it.</summary>
        private static int StepAndSweep(CarSimulation sim, HandlingProbe.ScriptedInput inp,
                                        BoostPickupSet set, BoostTuning tu)
        {
            float z0 = sim.Z, x0 = sim.X;
            sim.Step(Step, inp);
            int i = set.Sweep(z0 + DistanceAhead, sim.Z + DistanceAhead, x0, sim.X);
            if (i >= 0) sim.TriggerBoost(tu.addedTopSpeed, tu.addedAccel, tu.holdSeconds, tu.fadeSeconds);
            return i;
        }

        // [B1] -----------------------------------------------------------------------
        private static void Placement(BoostTuning tu, StringBuilder sb)
        {
            TrackModel t = TrackBuilder.BuildMainTrack();
            BoostPickupSet set = BoostPickupSet.FromLayout(t, tu);
            bool countOk = set.Count >= 3 && set.Count <= 5;
            bool allStraight = true, allOnRoad = true;
            sb.AppendLine("[B1] PICKUPS SIT ON TRUE STRAIGHTS, ON THE ROAD");
            for (int i = 0; i < set.Count; i++)
            {
                int s = set.SegmentOf(i);
                bool straight = true;
                for (int d = -10; d <= 10; d++) if (t[s + d].curve != 0f) straight = false;
                // Lanes are +/-0.77 (2026-10-08); PickupVisibilityProbe [asphalt] checks the
                // drawn bolt stays inside the road edge.
                bool onRoad = Mathf.Abs(set.LateralOf(i)) <= 0.92f; // on-road bound: inside the kerb start
                allStraight &= straight; allOnRoad &= onRoad;
                sb.AppendLine("    #" + i + "  segment " + s + "  x " + set.LateralOf(i).ToString("+0.00;-0.00")
                              + "  lap " + (t.Layout.FractionOf(s) * 100f).ToString("F0") + "%"
                              + "  straight(+/-10)=" + Pass(straight));
            }
            sb.AppendLine("    count " + set.Count + " in 3..5=" + Pass(countOk)
                          + "  all straight=" + Pass(allStraight) + "  all |x|<=0.92 (kerb)=" + Pass(allOnRoad));
            sb.AppendLine();
        }

        // [B2] -----------------------------------------------------------------------
        private static void TakenOnce(HandlingProfile p, BoostTuning tu, StringBuilder sb)
        {
            TrackModel t = HandlingProbe.FlatTrack(4000);
            var inp = new HandlingProbe.ScriptedInput();
            CarSimulation sim = AtTopSpeed(p, t, inp);
            BoostPickupSet set = One(t, sim.SegmentAhead(60), 0f, tu.radius);
            int taken = 0; bool armed = false;
            float zA = 0f, zB = 0f;
            for (int i = 0; i < 60 * 6; i++)
            {
                float z0 = sim.Z, x0 = sim.X;
                if (StepAndSweep(sim, inp, set, tu) >= 0)
                {
                    taken++;
                    armed = sim.BoostTimeLeft > 0f;    // handed over on the taking step
                    zA = z0 + DistanceAhead; zB = sim.Z + DistanceAhead;
                }
            }
            bool again = set.Sweep(zA, zB, 0f, 0f) < 0;   // the same crossing, offered twice
            sb.AppendLine("[B2] A PICKUP IS TAKEN ONCE");
            sb.AppendLine("    taken " + taken + "   TakenCount " + set.TakenCount
                          + "   re-sweep of the same crossing ignored=" + Pass(again));
            sb.AppendLine("    exactly once=" + Pass(taken == 1 && set.TakenCount == 1)
                          + "  boost armed=" + Pass(armed));
            sb.AppendLine();
        }

        // [B3] -----------------------------------------------------------------------
        private static void MissedWide(HandlingProfile p, BoostTuning tu, StringBuilder sb)
        {
            TrackModel t = HandlingProbe.FlatTrack(4000);
            var inp = new HandlingProbe.ScriptedInput();
            int wide = 0, edge = 0;
            {
                CarSimulation sim = AtTopSpeed(p, t, inp);
                BoostPickupSet set = One(t, sim.SegmentAhead(60), 0.8f, tu.radius);
                for (int i = 0; i < 60 * 6; i++) if (StepAndSweep(sim, inp, set, tu) >= 0) wide++;
            }
            {
                CarSimulation sim = AtTopSpeed(p, t, inp);
                BoostPickupSet set = One(t, sim.SegmentAhead(60), tu.radius - 0.02f, tu.radius);
                for (int i = 0; i < 60 * 6; i++) if (StepAndSweep(sim, inp, set, tu) >= 0) edge++;
            }
            // The side lanes (+/-BoostTuning.Lane, 2026-10-08: 0.77). A car driving the centre
            // line must NOT take one; a car whose centre is just inside reach (lane - radius +
            // 0.02) must, and that centre is still on asphalt (the kerb starts at 0.92).
            int sideCentre = 0, sideReach = 0;
            float reachX = BoostTuning.Lane - tu.radius + 0.02f;
            foreach (float lane in new[] { BoostTuning.Left, BoostTuning.Right })
            {
                CarSimulation sim = AtTopSpeed(p, t, inp);
                BoostPickupSet set = One(t, sim.SegmentAhead(60), lane, tu.radius);
                for (int i = 0; i < 60 * 6; i++) if (StepAndSweep(sim, inp, set, tu) >= 0) sideCentre++;
                BoostPickupSet set2 = One(t, 1000, lane, tu.radius);
                float s = Mathf.Sign(lane) * reachX;
                if (set2.Sweep(set2.ZOf(0) - 10f, set2.ZOf(0) + 10f, s, s) == 0) sideReach++;
            }
            bool laneOk = Mathf.Abs(BoostTuning.Lane - 0.77f) < 1e-6f && BoostTuning.Centre == 0f;
            bool laneClears = BoostTuning.Lane - tu.radius >= 0.25f && BoostTuning.Lane - (0.35f + 0.10f) > 0f;
            bool reachOnAsphalt = reachX < 0.92f;
            sb.AppendLine("[B3] DRIVING PAST IS NOT TAKING");
            sb.AppendLine("    x=0.80 vs car 0: taken " + wide + "=" + Pass(wide == 0)
                          + "   x=radius-0.02: taken " + edge + "=" + Pass(edge == 1));
            sb.AppendLine("    side lanes +/-" + BoostTuning.Lane.ToString("F2") + " (centre 0)=" + Pass(laneOk)
                          + "   centre-line car takes a side bolt: " + sideCentre + "=" + Pass(sideCentre == 0)
                          + "   car centre " + reachX.ToString("F2") + " (on asphalt, < 0.92 kerb)=" + Pass(reachOnAsphalt)
                          + " takes it: " + sideReach + "/2=" + Pass(sideReach == 2)
                          + "   lane - radius " + (BoostTuning.Lane - tu.radius).ToString("F2") + " >= 0.25, > car half-width + bolt body=" + Pass(laneClears));
            sb.AppendLine();
        }

        // [B4] -----------------------------------------------------------------------
        private static void RaisesThenExpires(HandlingProfile p, BoostTuning tu, StringBuilder sb)
        {
            TrackModel t = HandlingProbe.FlatTrack(6000);
            var inp = new HandlingProbe.ScriptedInput();
            CarSimulation sim = AtTopSpeed(p, t, inp);
            float before = sim.Speed;
            // Snapshot of the profile: a boost must not write to the asset.
            float m0 = p.maxSpeed, a0 = p.acceleration, y0 = p.maxYawRate, g0 = p.maxLateralAccel;

            sim.TriggerBoost(tu.addedTopSpeed, tu.addedAccel, tu.holdSeconds, tu.fadeSeconds);
            float peak = 0f, reach = -1f, worstDecel = 0f, last = sim.Speed;
            float total = tu.holdSeconds + tu.fadeSeconds + 1.0f;
            for (int i = 0; i < Mathf.RoundToInt(total * 60f); i++)
            {
                sim.Step(Step, inp);
                peak = Mathf.Max(peak, sim.Speed);
                if (reach < 0f && sim.Speed >= p.maxSpeed * (1f + tu.addedTopSpeed * 0.9f)) reach = (i + 1) * Step;
                worstDecel = Mathf.Max(worstDecel, (last - sim.Speed) / Step);
                last = sim.Speed;
            }
            float cap = p.maxSpeed * (1f + tu.addedTopSpeed);
            bool rose = peak > p.maxSpeed * 1.10f && peak <= cap + 0.5f;
            bool quick = reach > 0f && reach <= 1.0f;
            bool gentle = worstDecel <= 0.5f * p.brakeStrength * p.maxSpeed;
            bool expired = sim.BoostLevel == 0f && sim.BoostTimeLeft == 0f && sim.Speed <= p.maxSpeed + 0.01f;
            bool untouched = p.maxSpeed == m0 && p.acceleration == a0 && p.maxYawRate == y0 && p.maxLateralAccel == g0;
            sb.AppendLine("[B4] THE BOOST RAISES SPEED, THEN GOES AWAY");
            sb.AppendLine("    before " + (before / p.maxSpeed * 220f).ToString("F0") + " km/h   peak "
                          + (peak / p.maxSpeed * 220f).ToString("F0") + " km/h   90% of the gain in "
                          + reach.ToString("F2") + " s   worst fade decel " + (worstDecel / p.maxSpeed).ToString("F2")
                          + " maxSpeed/s (brake is " + p.brakeStrength.ToString("F2") + ")");
            sb.AppendLine("    rose (>110%, <=cap)=" + Pass(rose) + "  within 1 s=" + Pass(quick)
                          + "  fade gentler than half the brake=" + Pass(gentle)
                          + "  expired=" + Pass(expired) + "  profile untouched=" + Pass(untouched));
            sb.AppendLine();
        }

        // [B5] -----------------------------------------------------------------------
        private static void StillACar(HandlingProfile p, BoostTuning tu, StringBuilder sb)
        {
            TrackModel t = HandlingProbe.FlatTrack(6000);
            var inp = new HandlingProbe.ScriptedInput();
            CarSimulation plain = AtTopSpeed(p, t, inp);
            CarSimulation boosted = AtTopSpeed(p, t, inp);
            boosted.TriggerBoost(tu.addedTopSpeed, tu.addedAccel, tu.holdSeconds, tu.fadeSeconds);
            for (int i = 0; i < 60; i++) { plain.Step(Step, inp); boosted.Step(Step, inp); }

            inp.Steer = 1f;
            // 0.4 s: long enough to load the tyres, short enough to stay on the tarmac of a
            // straight road, so what is measured is the boost and not the grass.
            float maxSlip = 0f, maxDrift = 0f, ceilPlain = 0f, ceilBoost = 0f;
            bool stayedOn = true;
            for (int i = 0; i < 24; i++)
            {
                plain.Step(Step, inp); boosted.Step(Step, inp);
                maxSlip = Mathf.Max(maxSlip, Mathf.Abs(boosted.SlipAngle));
                maxDrift = Mathf.Max(maxDrift, boosted.DriftAmount);
                stayedOn &= !boosted.OffRoad;
                if (i == 0) { ceilPlain = plain.YawRateCeiling; ceilBoost = boosted.YawRateCeiling; }
            }
            // Holdable curvature is yaw rate / speed: the risk the boost is meant to carry.
            float holdRatio = (ceilBoost / Mathf.Max(1f, boosted.Speed)) / (ceilPlain / Mathf.Max(1f, plain.Speed));
            sb.AppendLine("[B5] A BOOSTED CAR IS STILL A CAR (full lock, 0.4 s)");
            sb.AppendLine("    peak slip " + (maxSlip * Mathf.Rad2Deg).ToString("F1") + " deg   drift "
                          + maxDrift.ToString("F2") + "   rotation ceiling boosted/plain "
                          + (ceilBoost / Mathf.Max(0.0001f, ceilPlain)).ToString("F3")
                          + "   holdable curvature " + (holdRatio * 100f).ToString("F0") + "% of unboosted (read)");
            sb.AppendLine("    stayed on tarmac (setup)=" + Pass(stayedOn) + "  no drift=" + Pass(maxDrift == 0f) + "  no spin (<15 deg)=" + Pass(maxSlip < 15f * Mathf.Deg2Rad)
                          + "  no extra authority=" + Pass(ceilBoost <= ceilPlain + 1e-5f));
            sb.AppendLine();
        }

        // [B6] -----------------------------------------------------------------------
        private static void RestartResets(HandlingProfile p, BoostTuning tu, StringBuilder sb)
        {
            TrackModel t = HandlingProbe.FlatTrack(4000);
            var inp = new HandlingProbe.ScriptedInput();
            CarSimulation sim = AtTopSpeed(p, t, inp);
            BoostPickupSet set = One(t, sim.SegmentAhead(60), 0f, tu.radius);
            for (int i = 0; i < 60 * 3; i++) StepAndSweep(sim, inp, set, tu);
            bool tookFirst = set.TakenCount == 1;

            set.ResetAll();          // what BoostPickups.ResetAll does
            sim.Reset(0f);           // what CarController.ResetCar does
            bool cleared = set.TakenCount == 0 && sim.BoostLevel == 0f && sim.BoostTimeLeft == 0f;
            for (int i = 0; i < 60 * 12; i++) StepAndSweep(sim, inp, set, tu);
            sb.AppendLine("[B6] RESTART BRINGS THE PICKUPS BACK");
            sb.AppendLine("    first run took it=" + Pass(tookFirst) + "  reset cleared=" + Pass(cleared)
                          + "  taken again after restart=" + Pass(set.TakenCount == 1));
            sb.AppendLine();
        }

        // [B7] -----------------------------------------------------------------------
        private static void BoostedLapAccepted(HandlingProfile p, BoostTuning tu, StringBuilder sb)
        {
            TrackModel track = TrackBuilder.BuildMainTrack();
            BoostPickupSet set = BoostPickupSet.FromLayout(track, tu);
            float plain = Lap(p, track, null, tu);
            float boosted = Lap(p, track, set, tu);
            float allowance = tu.MaxSecondsSaved(set.Count);
            float floor = RaceValidator.PhysicalFloorSeconds(track, p);
            sb.AppendLine("[B7] A LAP THROUGH EVERY PICKUP");
            sb.AppendLine("    plain " + plain.ToString("F2") + " s   boosted " + boosted.ToString("F2") + " s   taken "
                          + set.TakenCount + "/" + set.Count + "   saved " + (plain - boosted).ToString("F2")
                          + " s (bound " + allowance.ToString("F2") + ")   floor " + floor.ToString("F1") + " s");
            string at = "    car x at each pickup:";
            for (int i = 0; i < set.Count; i++)
                at += "  #" + i + " " + _crossX[i].ToString("F2") + " (want " + set.LateralOf(i).ToString("F2") + ")";
            sb.AppendLine(at);
            sb.AppendLine("    finished=" + Pass(boosted > 0f) + "  took some (a scripted thumb threads " + set.TakenCount + " of " + set.Count + "; taking is proven exactly by [B2]/[B3])=" + Pass(set.TakenCount >= 1)
                          // Not gated: the plain and boosted laps are two runs of a chaotic thumb
                          // driver, so their difference is mostly how much grass each hit. The
                          // bound is against the FLOOR lap, and that is what the next check holds.
                          + "  (lap-to-lap difference " + (plain - boosted).ToString("F2") + " s, informational)"
                          + "  above the boosted floor=" + Pass(boosted >= floor - allowance));
            sb.AppendLine();
        }

        private static readonly float[] _crossX = new float[16];

        /// <summary>
        /// HandlingProbe [22]'s two-thumb driver (9 Hz, 100 ms late, -1/0/+1, braking for
        /// what it sees coming), aiming for the next pickup when one is near. The thumb, not
        /// the proportional line-holder: a phone player presses whole buttons, and the
        /// line-holder's small corrections on a straight are mostly absorbed by the
        /// straight-only re-alignment, so it never crosses the road to a pickup at all.
        /// </summary>
        private static float Lap(HandlingProfile p, TrackModel track, BoostPickupSet set, BoostTuning tu)
        {
            var sim = new CarSimulation(track, p);
            sim.Reset(0f);
            var inp = new HandlingProbe.ScriptedInput();
            var thumb = new HandlingProbe.ThumbDriver();
            float time = 0f;
            while (sim.Z < track.Length && time < 180f)
            {
                float target = 0f;
                if (set != null)
                    for (int i = 0; i < set.Count; i++)
                    {
                        float dz = set.ZOf(i) - (sim.Z + DistanceAhead);
                        if (dz > 0f && dz < 14000f) target = set.LateralOf(i);   // ~1.2 s of look-ahead
                    }
                thumb.targetX = target;
                inp.Steer = thumb.Tick(sim, Step);
                inp.Brake = sim.RoadYawRateAhead(0.55f) > sim.YawRateCeiling * 0.85f;
                float zBefore = sim.Z + DistanceAhead;
                if (set != null) StepAndSweep(sim, inp, set, tu); else sim.Step(Step, inp);
                if (set != null)
                    for (int i = 0; i < set.Count && i < _crossX.Length; i++)
                        if (zBefore < set.ZOf(i) && sim.Z + DistanceAhead >= set.ZOf(i)) _crossX[i] = sim.X;
                time += Step;
            }
            return sim.Z >= track.Length ? time : -1f;
        }
    }

    internal static class BoostProbeSimExtensions
    {
        /// <summary>A segment comfortably ahead of where the car is DRAWN.</summary>
        public static int SegmentAhead(this CarSimulation sim, int segments)
        {
            return Mathf.FloorToInt((sim.Z + 1700f) / RoadGeometry.SegmentLength) + segments;
        }
    }
}
