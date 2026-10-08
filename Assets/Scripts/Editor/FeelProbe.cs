using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// HANDLING FEEL METRICS - the "driving on ice" numbers. Not a gate: a measurement
    /// sheet for the shipping profile, written to Logs/feel-probe.txt.
    ///
    ///   tap       heading the steering itself generates (integral of WorldYawRate, so
    ///             the straight re-alignment is excluded), peak road-relative yaw, peak
    ///             sideways speed and total sideways travel, on a straight at top speed
    ///   release   from steady full lock in a corner the car can hold (X pinned so the
    ///             surface never changes): time until WorldYawRate is under 20% / 5% of
    ///             what was held, and heading the car keeps adding after release
    ///   straight  release on a straight: time until yaw under 1 degree and sideways
    ///             speed under 0.05 half-widths/s, and travel after release
    ///   drift     handbrake + full lock 1 s, then everything released: time until slip
    ///             under 1 degree and yaw rate under 5%
    ///   hold      curvature held at full lock (HoldableCurve)
    ///   long lock full lock held 30 s on the real circuit (the separate stop/circle bug)
    /// </summary>
    public static class FeelProbe
    {
        private const float Dt = 1f / 60f;
        private const string LogPath = "Logs/feel-probe.txt";

        public static void RunFromCommandLine()
        {
            string r;
            try { r = Run(); }
            catch (Exception e) { Console.WriteLine("[FeelProbe] threw: " + e); EditorApplication.Exit(2); return; }
            Console.WriteLine(r);
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath, r);
            EditorApplication.Exit(0);
        }

        private static TrackModel Road(float curve)
        {
            var segs = new Segment[60000];
            for (int i = 0; i < segs.Length; i++)
            {
                segs[i].curve = curve; segs[i].y = 0f; segs[i].surface = Surface.Road;
                segs[i].barrierLeft = -400f; segs[i].barrierRight = 400f; segs[i].checkpointId = -1;
            }
            return new TrackModel(segs);
        }

        private static CarSimulation Cruise(HandlingProfile p, float curve, HandlingProbe.ScriptedInput inp)
        {
            var sim = new CarSimulation(Road(curve), p);
            sim.Reset(0f);
            for (int i = 0; i < 1800; i++) { sim.Step(Dt, inp); sim.X = 0f; sim.Settle(); }
            return sim;
        }

        public static string Run()
        {
            return Run(AssetDatabase.LoadAssetAtPath<HandlingProfile>("Assets/ScriptableObjects/Handling/Production.asset"));
        }

        /// <summary>
        /// Sweep entry: each line of Logs/feel-sweep-in.txt is
        /// key=value pairs (ret yrel align resp yr trace) applied to a copy of Production; for each, the feel
        /// sheet plus the shipping HandlingProbe's failures, ladder and thumb lap.
        /// </summary>
        private static int _n;
        public static void SweepFromCommandLine()
        {
            var basep = AssetDatabase.LoadAssetAtPath<HandlingProfile>("Assets/ScriptableObjects/Handling/Production.asset");
            var sb = new StringBuilder();
            foreach (string line in File.ReadAllLines("Logs/feel-sweep-in.txt"))
            {
                if (line.Trim().Length == 0 || line.StartsWith("#")) continue;
                var p = UnityEngine.Object.Instantiate(basep);
                p.name = "Production";
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                float trace = 0f;
                // key=value pairs: ret yrel align resp yr trace
                foreach (string kv in line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] a = kv.Split('=');
                    float v = float.Parse(a[1], ci);
                    switch (a[0])
                    {
                        case "ret": p.steeringReturnSpeed = v; break;
                        case "yrel": p.yawRateRelease = v; break;
                        case "align": p.straightAlignRate = v; break;
                        case "resp": p.steeringResponse = v; break;
                        case "yr": p.yawRateResponse = v; break;
                        case "trace": trace = v; break;
                        default: throw new Exception("unknown key " + a[0]);
                    }
                }
                sb.AppendLine("=================== " + line);
                sb.AppendLine(Run(p));
                sb.AppendLine(Robust(p));
                if (trace > 0f) sb.AppendLine(TraceThumb(p, trace, 1.5f));
                string rep = HandlingProbe.RunAll(p, true);
                File.WriteAllText("Logs/sweep-full-" + (++_n) + ".txt", "# " + line + System.Environment.NewLine + rep);
                foreach (string l in rep.Split((char)10))
                    if (l.Contains("**FAIL**") || l.Contains("curve") && l.Contains("thumb") || l.Contains("    lap:"))
                        sb.AppendLine("  | " + l.TrimEnd());
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/feel-sweep.txt", sb.ToString());
            EditorApplication.Exit(0);
        }

        private static float ThumbCorner(HandlingProfile p, float curve, float brakeSeconds, float latency)
        {
            var segs = new Segment[40000];
            for (int i = 0; i < segs.Length; i++)
            {
                segs[i].curve = i < 300 ? 0f : curve; segs[i].y = 0f; segs[i].surface = Surface.Road;
                segs[i].barrierLeft = -400f; segs[i].barrierRight = 400f; segs[i].checkpointId = -1;
            }
            float cornerZ = 300f * RoadGeometry.SegmentLength;
            var sim = new CarSimulation(new TrackModel(segs), p);
            sim.Reset(0f);
            var inp = new HandlingProbe.ScriptedInput();
            for (int i = 0; i < 60 * 25 && sim.Z < cornerZ - p.maxSpeed * 0.5f; i++) sim.Step(Dt, inp);
            sim.X = 0f; sim.Settle();
            var driver = new HandlingProbe.ThumbDriver { latencySeconds = latency };
            while (sim.Z < cornerZ) { inp.Steer = driver.Tick(sim, Dt); sim.Step(Dt, inp); }
            float t = 0f;
            while (t < 6f)
            {
                inp.Steer = driver.Tick(sim, Dt);
                inp.Brake = t < brakeSeconds;
                sim.Step(Dt, inp); t += Dt;
                if (Mathf.Abs(sim.X) > 1f) break;
            }
            return t;
        }

        private static float ThumbLap(HandlingProfile p, float latency, out float lapTime)
        {
            TrackModel main = TrackBuilder.BuildMainTrack();
            var sim = new CarSimulation(main, p);
            sim.Reset(0f);
            var inp = new HandlingProbe.ScriptedInput();
            var driver = new HandlingProbe.ThumbDriver { latencySeconds = latency };
            float t = 0f, off = 0f;
            while (sim.Z < main.Length && t < 200f)
            {
                inp.Steer = driver.Tick(sim, Dt);
                inp.Brake = sim.RoadYawRateAhead(0.55f) > sim.YawRateCeiling * 0.85f;
                sim.Step(Dt, inp); t += Dt;
                if (sim.OffRoad) off += Dt;
            }
            lapTime = sim.Z >= main.Length ? t : 999f;
            return off / t * 100f;
        }

        /// <summary>Thumb-driver robustness across latencies: the gating rungs and the lap.</summary>
        public static string Robust(HandlingProfile p)
        {
            float[] lat = { 0.07f, 0.085f, 0.10f, 0.115f, 0.13f };
            float[] curves = { TrackBuilder.CurveEasy, TrackBuilder.CurveFast, TrackBuilder.CurveMedium, TrackBuilder.CurveLong, TrackBuilder.CurveTight };
            string[] names = { "EasyFlat", "FastBrk", "MedBrk", "LongBrk", "TightBrk" };
            var sb = new StringBuilder("  ROBUST (latency 70..130ms):");
            int fails = 0;
            for (int c = 0; c < curves.Length; c++)
            {
                float mn = 99f, sum = 0f;
                foreach (float l in lat)
                {
                    float h = ThumbCorner(p, curves[c], c == 0 ? 0f : 1.5f, l);
                    mn = Mathf.Min(mn, h); sum += h;
                    if (h < 2.5f) fails++;
                }
                sb.Append(string.Format("  {0} mean {1:0.0} min {2:0.0}", names[c], sum / lat.Length, mn));
            }
            float offSum = 0f, offMax = 0f, lapSum = 0f;
            foreach (float l in lat)
            {
                float lt; float o = ThumbLap(p, l, out lt);
                offSum += o; offMax = Mathf.Max(offMax, o); lapSum += lt;
            }
            sb.Append(string.Format("  | lap off mean {0:0.0}% max {1:0.0}% time {2:0.0}s | rung fails {3}/25",
                offSum / lat.Length, offMax, lapSum / lat.Length, fails));
            return sb.ToString();
        }

        public static string TraceThumb(HandlingProfile p, float curve, float brakeSeconds)
        {
            var sb = new StringBuilder();
            var segs = new Segment[40000];
            for (int i = 0; i < segs.Length; i++)
            {
                segs[i].curve = i < 300 ? 0f : curve; segs[i].y = 0f; segs[i].surface = Surface.Road;
                segs[i].barrierLeft = -400f; segs[i].barrierRight = 400f; segs[i].checkpointId = -1;
            }
            var track = new TrackModel(segs);
            float cornerZ = 300f * RoadGeometry.SegmentLength;
            var sim = new CarSimulation(track, p);
            sim.Reset(0f);
            var inp = new HandlingProbe.ScriptedInput();
            for (int i = 0; i < 60 * 25 && sim.Z < cornerZ - p.maxSpeed * 0.5f; i++) sim.Step(Dt, inp);
            sim.X = 0f; sim.Settle();
            var driver = new HandlingProbe.ThumbDriver();
            while (sim.Z < cornerZ) { inp.Steer = driver.Tick(sim, Dt); sim.Step(Dt, inp); }
            float t = 0f; int k = 0;
            while (t < 6f)
            {
                inp.Steer = driver.Tick(sim, Dt);
                inp.Brake = t < brakeSeconds;
                sim.Step(Dt, inp); t += Dt;
                if (k++ % 4 == 0)
                    sb.AppendLine(string.Format("   t {0:0.00} in {1,2:0} steer {2:0.00} rate {3,5:0.00} road {4,5:0.00} ceil {5:0.00} yaw {6,6:0.0} x {7,6:0.00} lat {8,5:0.00} spd {9:0.00}",
                        t, inp.Steer, sim.Steer, sim.WorldYawRate, sim.RoadYawRate, sim.YawRateCeiling, sim.Yaw * Mathf.Rad2Deg, sim.X, sim.LateralVel, sim.SpeedPct));
                if (Mathf.Abs(sim.X) > 1f) break;
            }
            sb.AppendLine("   held " + t.ToString("0.00"));
            return sb.ToString();
        }

        public static string Run(HandlingProfile p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("FEEL PROBE - " + p.name);
            sb.AppendLine(string.Format("  steeringResponse {0} return {1} yawRateResponse {2} maxYawRate {3} straightAlign {4} grip {5}x{6} selfAlign {7}",
                p.steeringResponse, p.steeringReturnSpeed, p.yawRateResponse, p.maxYawRate,
                p.straightAlignRate, p.grip, p.lateralDamping, p.yawSelfAlign));

            // ---- taps on a straight ----
            sb.AppendLine("TAP (straight, top speed)");
            float[] taps = { 0.05f, 0.10f, 0.15f, 0.25f, 0.40f };
            foreach (float tap in taps)
            {
                var inp = new HandlingProbe.ScriptedInput();
                var sim = Cruise(p, 0f, inp);
                float x0 = sim.X, steerHeading = 0f, peakYaw = 0f, peakLat = 0f, settle = -1f;
                int n = Mathf.RoundToInt(tap / Dt), total = n + 180;
                for (int i = 0; i < total; i++)
                {
                    inp.Steer = i < n ? 1f : 0f;
                    sim.Step(Dt, inp);
                    steerHeading += sim.WorldYawRate * Dt;
                    peakYaw = Mathf.Max(peakYaw, Mathf.Abs(sim.Yaw));
                    peakLat = Mathf.Max(peakLat, Mathf.Abs(sim.LateralVel));
                    if (i >= n && settle < 0f && Mathf.Abs(sim.LateralVel) < 0.05f) settle = (i - n + 1) * Dt;
                }
                sb.AppendLine(string.Format("  tap {0:0.00}s: steer heading {1,5:0.0} deg  peak yaw {2,5:0.0} deg  peak lat {3:0.00} hw/s  travel {4:0.000} hw  settled {5}",
                    tap, steerHeading * Mathf.Rad2Deg, peakYaw * Mathf.Rad2Deg, peakLat, sim.X - x0,
                    settle < 0 ? ">3s" : settle.ToString("0.00") + "s"));
            }

            // ---- release in a corner from steady full lock ----
            sb.AppendLine("RELEASE FROM FULL LOCK (corner the car can hold, X pinned)");
            foreach (float frac in new[] { 1.0f, 0.8f })
            {
                float curve = p.HoldableCurve(frac) * 0.98f;
                var inp = new HandlingProbe.ScriptedInput();
                var sim = new CarSimulation(Road(curve), p);
                sim.Reset(0f);
                for (int i = 0; i < 3000 && sim.SpeedPct < frac - 0.001f; i++) { sim.Step(Dt, inp); sim.X = 0f; sim.Settle(); }
                bool pinSpeed = frac < 0.999f;
                inp.Steer = 1f;
                for (int i = 0; i < 90; i++) { sim.Step(Dt, inp); sim.X = 0f; if (pinSpeed) sim.Speed = frac * p.maxSpeed; }
                float held = sim.WorldYawRate;
                inp.Steer = 0f;
                float t5 = -1f, t20 = -1f, extra = 0f;
                for (int i = 0; i < 120; i++)
                {
                    sim.Step(Dt, inp); sim.X = 0f; if (pinSpeed) sim.Speed = frac * p.maxSpeed;
                    extra += sim.WorldYawRate * Dt;
                    if (t20 < 0f && Mathf.Abs(sim.WorldYawRate) < 0.20f * Mathf.Abs(held)) t20 = (i + 1) * Dt;
                    if (t5 < 0f && Mathf.Abs(sim.WorldYawRate) < 0.05f * Mathf.Abs(held)) t5 = (i + 1) * Dt;
                }
                sb.AppendLine(string.Format("  {0:0}% speed, curve {1:0.00}: held {2:0} deg/s; yaw rate <20% in {3:0.000}s, <5% in {4:0.000}s, heading added after release {5:0.0} deg",
                    frac * 100f, curve, held * Mathf.Rad2Deg, t20, t5, extra * Mathf.Rad2Deg));
            }

            // ---- release on a straight ----
            sb.AppendLine("RELEASE ON A STRAIGHT (top speed)");
            foreach (float hold in new[] { 0.15f, 0.25f, 0.5f })
            {
                var inp = new HandlingProbe.ScriptedInput();
                var sim = Cruise(p, 0f, inp);
                inp.Steer = 1f;
                int n = Mathf.RoundToInt(hold / Dt);
                for (int i = 0; i < n; i++) sim.Step(Dt, inp);
                float yawR = sim.Yaw, latR = sim.LateralVel, xR = sim.X;
                inp.Steer = 0f;
                float latGone = -1f, yawGone = -1f;
                for (int i = 0; i < 180; i++)
                {
                    sim.Step(Dt, inp);
                    if (latGone < 0f && Mathf.Abs(sim.LateralVel) < 0.05f) latGone = (i + 1) * Dt;
                    if (yawGone < 0f && Mathf.Abs(sim.Yaw) < 1f * Mathf.Deg2Rad) yawGone = (i + 1) * Dt;
                }
                sb.AppendLine(string.Format("  hold {0:0.00}s: at release yaw {1:0.0} deg lat {2:0.00} hw/s; yaw<1deg in {3}, lat<0.05 in {4}, travel after release {5:0.000} hw",
                    hold, yawR * Mathf.Rad2Deg, latR,
                    yawGone < 0 ? ">3s" : yawGone.ToString("0.00") + "s",
                    latGone < 0 ? ">3s" : latGone.ToString("0.00") + "s", sim.X - xR));
            }

            // ---- drift release ----
            sb.AppendLine("DRIFT RELEASE (handbrake + full lock 1 s at top speed, curve 3, then all released, X pinned)");
            {
                var inp = new HandlingProbe.ScriptedInput();
                var sim = Cruise(p, 3f, inp);
                inp.Steer = 1f; inp.Handbrake = true;
                float peakSlip = 0f;
                for (int i = 0; i < 60; i++) { sim.Step(Dt, inp); sim.X = 0f; peakSlip = Mathf.Max(peakSlip, Mathf.Abs(sim.SlipAngle)); }
                float held = sim.WorldYawRate, slipHeld = sim.SlipAngle;
                inp.Steer = 0f; inp.Handbrake = false;
                float slipGone = -1f, rateGone = -1f;
                for (int i = 0; i < 300; i++)
                {
                    sim.Step(Dt, inp); sim.X = 0f;
                    if (slipGone < 0f && Mathf.Abs(sim.SlipAngle) < 1f * Mathf.Deg2Rad) slipGone = (i + 1) * Dt;
                    if (rateGone < 0f && Mathf.Abs(sim.WorldYawRate) < 0.05f * Mathf.Abs(held)) rateGone = (i + 1) * Dt;
                }
                sb.AppendLine(string.Format("  peak slip {0:0.0} deg, at release slip {1:0.0} deg rate {2:0} deg/s; slip<1deg in {3}, rate<5% in {4}",
                    peakSlip * Mathf.Rad2Deg, slipHeld * Mathf.Rad2Deg, held * Mathf.Rad2Deg,
                    slipGone < 0 ? ">5s" : slipGone.ToString("0.00") + "s",
                    rateGone < 0 ? ">5s" : rateGone.ToString("0.00") + "s"));
            }

            // ---- holdable curvature ----
            sb.AppendLine(string.Format("HOLDABLE CURVE at full lock: 100% {0:0.00}  80% {1:0.00}  70% {2:0.00}",
                p.HoldableCurve(1f), p.HoldableCurve(0.8f), p.HoldableCurve(0.7f)));

            // ---- long full lock on the real circuit ----
            sb.AppendLine("LONG FULL LOCK ON THE CIRCUIT (30 s, right)");
            {
                var track = TrackBuilder.BuildMainTrack();
                var inp = new HandlingProbe.ScriptedInput();
                var sim = new CarSimulation(track, p);
                sim.Reset(0f);
                for (int i = 0; i < 180; i++) sim.Step(Dt, inp);
                inp.Steer = 1f;
                float z0 = sim.Z, minAlong = float.MaxValue, minSpeed = float.MaxValue;
                for (int i = 0; i < 1800; i++)
                {
                    sim.Step(Dt, inp);
                    minAlong = Mathf.Min(minAlong, sim.AlongVel);
                    if (i > 300) minSpeed = Mathf.Min(minSpeed, sim.Speed);
                }
                sb.AppendLine(string.Format("  progress {0:0} units, min AlongVel {1:0}, min speed after 5 s {2:0} ({3:0}%), end speed {4:0}",
                    sim.Z - z0, minAlong, minSpeed, minSpeed / p.maxSpeed * 100f, sim.Speed));
            }
            return sb.ToString();
        }
    }
}
