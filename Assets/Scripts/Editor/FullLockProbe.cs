using System.Text;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;
using RenaultRacer.InputSystem;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// THE FULL-LOCK HOLD. What happens when the player simply holds one steering button
    /// down, for a long time, anywhere on the circuit?
    ///
    /// Maj found it in the browser: the car slowed to a crawl, went round in circles and
    /// appeared to drive back up the course. Measured on the real circuit, 92 runs (every
    /// 60th segment, both directions, 30 s from race speed), the model before the fix:
    ///
    ///   * BACKWARDS. Rate steering held at full lock rotates the car for as long as the
    ///     button is held, and nothing bounded how far from the road that took it: up to
    ///     136 degrees off the road, AlongVel -6360, Z running back 2104 units (ten
    ///     segments). RaceSession assumes Z never decreases.
    ///   * PINNED. Every run ended against a barrier at 8-11% of top speed, nose 17-19
    ///     degrees INTO the wall, crabbing, for as long as the button was held - because
    ///     the anti-trap yaw recovery only fired on steps where the car was strictly
    ///     beyond the barrier, which (the bounce having just pushed it off) was one step
    ///     in fourteen.
    ///
    /// Heavy scrub, losing the line, losing a lot of speed and grinding along the wall are
    /// all honest consequences of holding full lock and are allowed. Going backwards,
    /// pointing across the road, circling and crawling are not.
    ///
    /// Separate from HandlingProbe for the same reason AutoTurnProbe is: this is a
    /// structural property of the MODEL, not a feel target.
    /// </summary>
    public static class FullLockProbe
    {
        private const float Step = 1f / 60f;

        // ---- acceptance thresholds ----
        /// <summary>Road-relative yaw a held input may ever reach. 90 is the car going nowhere.</summary>
        public const float MaxYawDeg = 85f;
        /// <summary>Mean speed, fraction of top, while the lock is held (last 2/3). Old model: 0.08-0.11.</summary>
        public const float MinMeanSpeed = 0.15f;
        /// <summary>Backwards travel along the course allowed, units. Old model: up to 2104.</summary>
        public const float MaxBackwardsZ = 0f;

        private sealed class Held : IDriveInput
        {
            public float Steer; public bool Brake; public bool Handbrake;
            public float SteerAxis { get { return Steer; } }
            public bool BrakeHeld { get { return Brake; } }
            public bool HandbrakeHeld { get { return Handbrake; } }
        }

        private struct Result
        {
            public float maxYaw, backZ, minAlong, meanSpeed, minSpeed, progressPerSec;
        }

        private static Result Hold(HandlingProfile p, TrackModel t, int seg, float dir,
                                   float startSpeedPct, bool handbrake, float seconds)
        {
            var sim = new CarSimulation(t, p);
            var input = new Held { Steer = dir, Handbrake = handbrake };
            sim.Reset(seg * RoadGeometry.SegmentLength);
            sim.Speed = p.maxSpeed * startSpeedPct;

            Result r = new Result { minSpeed = 9f };
            float z0 = sim.Z, sum = 0f;
            int n = Mathf.RoundToInt(seconds / Step), counted = 0;
            for (int i = 0; i < n; i++)
            {
                float zb = sim.Z;
                sim.Step(Step, input);
                if (sim.Z < zb) r.backZ += zb - sim.Z;
                r.maxYaw = Mathf.Max(r.maxYaw, Mathf.Abs(sim.Yaw));
                r.minAlong = Mathf.Min(r.minAlong, sim.AlongVel);
                float tt = (i + 1) * Step;
                if (tt > seconds / 3f)
                {
                    sum += sim.SpeedPct; counted++;
                    r.minSpeed = Mathf.Min(r.minSpeed, sim.SpeedPct);
                }
            }
            r.meanSpeed = counted > 0 ? sum / counted : 0f;
            r.progressPerSec = (sim.Z - z0) / seconds;
            return r;
        }

        /// <summary>
        /// [F0] EXACTLY WHAT MAJ RECORDED (driving bug.mkv, 2026-10-07): the race starts
        /// at z 0 from a standstill, the lights go green, and one steering button is held
        /// down. On the old model the car pivoted in circles on the start straight -
        /// 0 to 115 km/h and back, recrossing the line. What is wanted instead: it runs
        /// across to the wall and carries on DOWN THE ROAD along it.
        ///
        /// Traced every half second so a reader can see the path, and graded on the
        /// thing a circle cannot do: after the first 3 s, every 2 s window must make
        /// real forward progress (average along-road speed at least MinWindowAlong of
        /// top), and the car must never move backwards or point across the road.
        /// </summary>
        public const float MinWindowAlong = 0.12f;

        private static bool RaceStart(StringBuilder sb, HandlingProfile p, TrackModel t)
        {
            sb.AppendLine("[F0] RACE START AS RECORDED: z 0, from rest, one button held 20 s");
            bool all = true;
            for (int d = 1; d >= -1; d -= 2)
            {
                var sim = new CarSimulation(t, p);
                var input = new Held { Steer = d };
                sim.Reset(0f);
                int n = Mathf.RoundToInt(20f / Step);
                float back = 0f, maxYaw = 0f, worstWindow = 9f, windowZ = 0f;
                sb.AppendLine("    holding " + (d > 0 ? "RIGHT" : "LEFT") + ":");
                for (int i = 0; i < n; i++)
                {
                    float zb = sim.Z;
                    sim.Step(Step, input);
                    if (sim.Z < zb) back += zb - sim.Z;
                    maxYaw = Mathf.Max(maxYaw, Mathf.Abs(sim.Yaw));
                    int k = i + 1;
                    if (k % 30 == 0)
                        sb.AppendLine(string.Format("      t {0,4:F1}s  speed {1:F2}  yaw {2,6:F1} deg  x {3,6:F2}  z {4,7:F0}",
                            k * Step, sim.SpeedPct, sim.Yaw * Mathf.Rad2Deg, sim.X, sim.Z));
                    if (k == 180) windowZ = sim.Z;
                    if (k > 180 && (k - 180) % 120 == 0)
                    {
                        worstWindow = Mathf.Min(worstWindow, (sim.Z - windowZ) / 2f / p.maxSpeed);
                        windowZ = sim.Z;
                    }
                }
                bool ok = back <= MaxBackwardsZ && maxYaw * Mathf.Rad2Deg <= MaxYawDeg
                       && worstWindow >= MinWindowAlong;
                all &= ok;
                sb.AppendLine(string.Format(
                    "    backwards {0:F0}   worst |yaw| {1:F1} deg   worst 2s window along-road {2:F2} of top (>= {3:F2})   {4}",
                    back, maxYaw * Mathf.Rad2Deg, worstWindow, MinWindowAlong, Pass(ok)));
            }
            sb.AppendLine("    THE CAR RUNS DOWN THE ROAD, IT DOES NOT CIRCLE = " + Pass(all));
            sb.AppendLine();
            return all;
        }

        private static string Pass(bool ok) { return ok ? "PASS" : "**FAIL**"; }

        public static string RunAll(HandlingProfile p, TrackModel track)
        {
            StringBuilder sb = new StringBuilder(16384);
            sb.AppendLine("=================== FULL-LOCK HOLD PROBE ===================");
            sb.AppendLine("profile: " + p.name + "   track segments " + track.Count
                          + "   steering yaw envelope " + (p.steerYawSoftLimit * Mathf.Rad2Deg).ToString("F0")
                          + "->" + (p.steerYawHardLimit * Mathf.Rad2Deg).ToString("F0") + " deg");
            sb.AppendLine("thresholds: |yaw| <= " + MaxYawDeg + " deg, backwards Z <= " + MaxBackwardsZ
                          + ", mean speed while held >= " + MinMeanSpeed.ToString("F2"));
            sb.AppendLine();

            bool all = true;
            all &= RaceStart(sb, p, track);
            all &= Sweep(sb, "[F1] RACE SPEED, 30s HELD, EVERY 60th SEGMENT OF THE CIRCUIT",
                         p, track, 0.95f, false, 30f, 60, true);
            all &= Sweep(sb, "[F2] FROM REST (race start), 30s HELD",
                         p, track, 0f, false, 30f, 240, true);
            // The handbrake is the drift; held with the lock it must still never send the
            // car backwards or across the road. Speed is not graded - a held handbrake is
            // a request to scrub.
            all &= Sweep(sb, "[F3] HANDBRAKE + FULL LOCK, 10s HELD (speed not graded)",
                         p, track, 0.95f, true, 10f, 240, false);
            all &= Release(sb, p, track);

            sb.AppendLine("FULL-LOCK HOLD IS SANE = " + Pass(all));
            return sb.ToString();
        }

        private static bool Sweep(StringBuilder sb, string title, HandlingProfile p, TrackModel t,
                                  float startPct, bool handbrake, float seconds, int stride,
                                  bool gradeSpeed)
        {
            sb.AppendLine(title);
            float worstYaw = 0f, worstBack = 0f, worstMean = 9f, worstAlong = 0f;
            int runs = 0, fails = 0;
            for (int seg = 11; seg < t.Count; seg += stride)
                for (int d = -1; d <= 1; d += 2)
                {
                    Result r = Hold(p, t, seg, d, startPct, handbrake, seconds);
                    runs++;
                    bool ok = r.maxYaw * Mathf.Rad2Deg <= MaxYawDeg && r.backZ <= MaxBackwardsZ
                           && (!gradeSpeed || r.meanSpeed >= MinMeanSpeed);
                    if (!ok)
                    {
                        fails++;
                        if (fails <= 8)
                            sb.AppendLine(string.Format(
                                "    seg {0,4} dir {1,2} curve {2,5:F1}: maxYaw {3,5:F0} deg  backZ {4,6:F0}"
                                + "  minAlong {5,6:F0}  meanSpd {6:F2}  minSpd {7:F2}  progress {8:F0}/s   **FAIL**",
                                seg, d, t[seg].curve, r.maxYaw * Mathf.Rad2Deg, r.backZ, r.minAlong,
                                r.meanSpeed, r.minSpeed, r.progressPerSec));
                    }
                    worstYaw = Mathf.Max(worstYaw, r.maxYaw);
                    worstBack = Mathf.Max(worstBack, r.backZ);
                    worstAlong = Mathf.Min(worstAlong, r.minAlong);
                    worstMean = Mathf.Min(worstMean, r.meanSpeed);
                }
            bool yawOk = worstYaw * Mathf.Rad2Deg <= MaxYawDeg;
            bool backOk = worstBack <= MaxBackwardsZ;
            bool speedOk = !gradeSpeed || worstMean >= MinMeanSpeed;
            sb.AppendLine("    " + runs + " runs: worst |yaw| " + (worstYaw * Mathf.Rad2Deg).ToString("F1")
                          + " deg   worst backwards Z " + worstBack.ToString("F0")
                          + "   worst AlongVel " + worstAlong.ToString("F0")
                          + "   worst mean speed " + worstMean.ToString("F2")
                          + (fails > 0 ? "   (" + fails + " runs failed)" : ""));
            sb.AppendLine("    never across the road=" + Pass(yawOk) + "  never backwards=" + Pass(backOk)
                          + "  never crawls=" + (gradeSpeed ? Pass(speedOk) : "n/a")
                          + "   = " + Pass(yawOk && backOk && speedOk));
            sb.AppendLine();
            return yawOk && backOk && speedOk;
        }

        /// <summary>
        /// [F4] Letting go after a long hold must give the car back: it keeps going
        /// forward, at a real speed (at least MinMeanSpeed of top 5 s later). Rejoining the road is the player's job - nothing
        /// may steer the car back onto it.
        /// </summary>
        private static bool Release(StringBuilder sb, HandlingProfile p, TrackModel t)
        {
            sb.AppendLine("[F4] 30s HELD, THEN RELEASED FOR 5s");
            bool ok = true;
            int[] segs = { 11, 971, 2231 };
            foreach (int seg in segs)
                for (int d = -1; d <= 1; d += 2)
                {
                    var sim = new CarSimulation(t, p);
                    var input = new Held { Steer = d };
                    sim.Reset(seg * RoadGeometry.SegmentLength);
                    sim.Speed = p.maxSpeed * 0.95f;
                    int n = Mathf.RoundToInt(30f / Step);
                    for (int i = 0; i < n; i++) sim.Step(Step, input);
                    float spdHeld = sim.SpeedPct, z0 = sim.Z, back = 0f;
                    input.Steer = 0f;
                    int m = Mathf.RoundToInt(5f / Step);
                    for (int i = 0; i < m; i++)
                    {
                        float zb = sim.Z;
                        sim.Step(Step, input);
                        if (sim.Z < zb) back += zb - sim.Z;
                    }
                    // Not "faster than when held": released in a corner the car carries
                    // straight on, as it must, and may well meet the wall. The claim is
                    // only that it is not stuck - forward, moving, at a real speed.
                    // Speed bar = MinMeanSpeed (0.15), not 0.25, since the 15% steering cut
                    // (2026-10-07): released mid-corner the car now reaches the outside wall
                    // and the 5 s snapshot can land on a scrape (seg 2231: 0.21, 29263 units
                    // of progress in those 5 s). Progress and no-backwards are what prove it.
                    bool runOk = back <= MaxBackwardsZ && sim.Z - z0 > 5000f && sim.SpeedPct >= MinMeanSpeed;
                    ok &= runOk;
                    sb.AppendLine(string.Format(
                        "    seg {0,4} dir {1,2}: speed {2:F2} held -> {3:F2} 5s after release, progress {4:F0}, backwards {5:F0}   {6}",
                        seg, d, spdHeld, sim.SpeedPct, sim.Z - z0, back, Pass(runOk)));
                }
            sb.AppendLine("    RELEASE GIVES THE CAR BACK = " + Pass(ok));
            sb.AppendLine();
            return ok;
        }
    }
}
