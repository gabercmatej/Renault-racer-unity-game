//
//   Unity.exe -batchmode -quit -projectPath <COPY> \
//     -executeMethod RenaultRacer.EditorTools.BrakeFeelProbe.RunFromCommandLine
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
    /// NORMAL BRAKING, before and after (2026-10-08: brakeStrength 0.55 -> 0.484, -12%).
    /// Steps CarSimulation directly on a dead-straight flat road at the Production profile,
    /// once with a clone of the profile at the OLD value and once at the SHIPPED value
    /// (the asset itself is never written). Measures what the player feels - speed lost on
    /// a tap / hold, the full stop - then brake + steer (steering response must be the
    /// same car, only slower to slow) and the handbrake drift (which does not read
    /// brakeStrength, so it must be bit-identical).
    /// </summary>
    public static class BrakeFeelProbe
    {
        private const float Step = 1f / 60f;
        private const float OldBrake = 0.55f;
        private const string ProfilePath = "Assets/ScriptableObjects/Handling/Production.asset";
        private const string LogPath = "Logs/brake-feel-probe.txt";

        [MenuItem("Renault Racer/Brake feel probe", false, 48)]
        public static void RunFromMenu() { Debug.Log(Run()); }

        public static void RunFromCommandLine()
        {
            string report;
            try { report = Run(); }
            catch (Exception e) { Console.WriteLine("[BrakeFeelProbe] threw: " + e); EditorApplication.Exit(2); return; }
            Console.WriteLine(report);
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath, report);
            bool pass = !report.Contains("**FAIL**");
            if (pass) Console.WriteLine("[brake feel probe] ALL PASS");
            EditorApplication.Exit(pass ? 0 : 1);
        }

        private static string Pass(bool ok) { return ok ? "PASS" : "**FAIL**"; }

        private static HandlingProfile With(HandlingProfile src, float brake)
        {
            HandlingProfile c = UnityEngine.Object.Instantiate(src);
            c.brakeStrength = brake;
            return c;
        }

        private static CarSimulation Fresh(HandlingProfile p, float speedPct)
        {
            TrackModel t = HandlingProbe.FlatTrack(12000);
            var sim = new CarSimulation(t, p);
            sim.Reset(2000f);
            sim.Speed = p.maxSpeed * speedPct;
            return sim;
        }

        /// <summary>Speed lost, as a fraction of maxSpeed, to brake held for <paramref name="seconds"/>.</summary>
        private static float Lost(HandlingProfile p, float speedPct, float seconds)
        {
            CarSimulation sim = Fresh(p, speedPct);
            var inp = new HandlingProbe.ScriptedInput { Brake = true };
            float v0 = sim.Speed;
            int n = Mathf.RoundToInt(seconds / Step);
            for (int i = 0; i < n; i++) sim.Step(Step, inp);
            return (v0 - sim.Speed) / p.maxSpeed;
        }

        private static void FullStop(HandlingProfile p, out float seconds, out float metres)
        {
            CarSimulation sim = Fresh(p, 1f);
            var inp = new HandlingProbe.ScriptedInput { Brake = true };
            float z0 = sim.Z; seconds = -1f;
            for (int i = 0; i < 60 * 20; i++)
            {
                sim.Step(Step, inp);
                if (sim.Speed <= 0.5f) { seconds = (i + 1) * Step; break; }
            }
            metres = (sim.Z - z0) / RoadGeometry.SegmentLength;   // one segment is one metre
        }

        private struct SteerResult { public float lost, dx, peakYaw, heading, slip; }

        private static SteerResult BrakeSteer(HandlingProfile p)
        {
            CarSimulation sim = Fresh(p, 1f);
            var inp = new HandlingProbe.ScriptedInput { Steer = 1f, Brake = true };
            float v0 = sim.Speed, x0 = sim.X, h0 = sim.WorldHeading;
            var r = new SteerResult();
            for (int i = 0; i < 30; i++)
            {
                sim.Step(Step, inp);
                r.peakYaw = Mathf.Max(r.peakYaw, Mathf.Abs(sim.Yaw));
                r.slip = Mathf.Max(r.slip, Mathf.Abs(sim.SlipAngle));
            }
            r.lost = (v0 - sim.Speed) / p.maxSpeed;
            r.dx = sim.X - x0;
            r.heading = sim.WorldHeading - h0;
            return r;
        }

        /// <summary>A handbrake drift run; returns a digest of every step's state, exact bits.</summary>
        private static string HandbrakeRun(HandlingProfile p)
        {
            CarSimulation sim = Fresh(p, 1f);
            var inp = new HandlingProbe.ScriptedInput { Steer = 1f, Handbrake = true };
            var sb = new StringBuilder();
            long h = 1469598103934665603L;
            for (int i = 0; i < 60 * 3; i++)
            {
                if (i == 90) { inp.Handbrake = false; inp.Steer = -1f; }
                sim.Step(Step, inp);
                float[] v = { sim.X, sim.Z, sim.Speed, sim.Yaw, sim.WorldHeading, sim.SlipAngle, sim.DriftAmount };
                foreach (float f in v) { h ^= BitConverter.ToInt32(BitConverter.GetBytes(f), 0); h *= 1099511628211L; }
            }
            sb.Append(h.ToString("X16")).Append(" end X ").Append(sim.X.ToString("F6")).Append(" speed ").Append(sim.Speed.ToString("F3"))
              .Append(" heading ").Append(sim.WorldHeading.ToString("F6"));
            return sb.ToString();
        }

        public static string Run()
        {
            HandlingProfile prod = AssetDatabase.LoadAssetAtPath<HandlingProfile>(ProfilePath);
            if (prod == null) return "**FAIL** no Production profile at " + ProfilePath;
            HandlingProfile oldP = With(prod, OldBrake);
            HandlingProfile newP = With(prod, prod.brakeStrength);
            var sb = new StringBuilder(4096);
            sb.AppendLine("================ BRAKE FEEL PROBE ================");
            sb.AppendLine("Production profile; brakeStrength old " + OldBrake.ToString("F3") + " vs shipped " + prod.brakeStrength.ToString("F3")
                          + " (" + ((prod.brakeStrength / OldBrake - 1f) * 100f).ToString("F1") + "%); brakeLowSpeedFactor "
                          + prod.brakeLowSpeedFactor.ToString("F2") + "; straight flat road. Loss = % of maxSpeed (100% = 220 km/h).");
            sb.AppendLine();

            bool shippedIs = Mathf.Abs(prod.brakeStrength - 0.484f) < 1e-5f;
            sb.AppendLine("[K0] Production.asset brakeStrength = 0.484 (-12%): " + prod.brakeStrength.ToString("F3") + " " + Pass(shippedIs));

            bool all = shippedIs;
            sb.AppendLine("[K1] SPEED LOST (% of maxSpeed)");
            sb.AppendLine("    case                  old      new     change");
            var cases = new[]
            {
                new { n = "0.1 s tap from 70%", pct = 0.7f, s = 0.1f, tap = true },
                new { n = "0.1 s tap from 100%", pct = 1.0f, s = 0.1f, tap = true },
                new { n = "0.3 s hold from 100%", pct = 1.0f, s = 0.3f, tap = false },
                new { n = "1.0 s hold from 100%", pct = 1.0f, s = 1.0f, tap = false },
            };
            foreach (var c in cases)
            {
                float a = Lost(oldP, c.pct, c.s), b = Lost(newP, c.pct, c.s);
                float change = (b / a - 1f) * 100f;
                bool ok = c.tap ? (change <= -8f && change >= -15f) : (change < -5f && change > -16f);
                all &= ok;
                sb.AppendLine("    " + c.n.PadRight(22) + (a * 100f).ToString("F2").PadLeft(6) + "%  " + (b * 100f).ToString("F2").PadLeft(6)
                              + "%  " + change.ToString("+0.0;-0.0") + "%  (tap/hold loss reduced 8-15%: " + Pass(ok) + ")");
            }

            float ts0, ms0, ts1, ms1;
            FullStop(oldP, out ts0, out ms0);
            FullStop(newP, out ts1, out ms1);
            bool stops = ts0 > 0f && ts1 > 0f && ts1 < 8f;
            all &= stops;
            sb.AppendLine("[K2] FULL STOP from top speed: old " + ts0.ToString("F2") + " s / " + ms0.ToString("F0") + " m   new "
                          + ts1.ToString("F2") + " s / " + ms1.ToString("F0") + " m   (+" + ((ts1 / ts0 - 1f) * 100f).ToString("F1") + "% time, +"
                          + ((ms1 / ms0 - 1f) * 100f).ToString("F1") + "% distance); still stops within 8 s: " + Pass(stops));

            SteerResult s0 = BrakeSteer(oldP), s1 = BrakeSteer(newP);
            bool sameSide = Mathf.Sign(s0.dx) == Mathf.Sign(s1.dx) && Mathf.Sign(s0.heading) == Mathf.Sign(s1.heading);
            all &= sameSide;
            sb.AppendLine("[K3] BRAKE + FULL STEER for 0.5 s from top speed (short of the tyre wall) (read; steering must go the same way)");
            sb.AppendLine("    old: lost " + (s0.lost * 100f).ToString("F1") + "%  dX " + s0.dx.ToString("+0.000;-0.000") + "  heading " + (s0.heading * Mathf.Rad2Deg).ToString("F1")
                          + " deg  peak yaw " + (s0.peakYaw * Mathf.Rad2Deg).ToString("F1") + " deg  peak slip " + (s0.slip * Mathf.Rad2Deg).ToString("F1") + " deg");
            sb.AppendLine("    new: lost " + (s1.lost * 100f).ToString("F1") + "%  dX " + s1.dx.ToString("+0.000;-0.000") + "  heading " + (s1.heading * Mathf.Rad2Deg).ToString("F1")
                          + " deg  peak yaw " + (s1.peakYaw * Mathf.Rad2Deg).ToString("F1") + " deg  peak slip " + (s1.slip * Mathf.Rad2Deg).ToString("F1") + " deg   same direction=" + Pass(sameSide));

            string h0 = HandbrakeRun(oldP), h1 = HandbrakeRun(newP);
            bool same = h0 == h1;
            all &= same;
            sb.AppendLine("[K4] HANDBRAKE DRIFT RUN (1.5 s handbrake at full lock, then a flick, 3 s) is bit-identical across brakeStrength");
            sb.AppendLine("    old " + h0);
            sb.AppendLine("    new " + h1 + "   identical=" + Pass(same));

            UnityEngine.Object.DestroyImmediate(oldP);
            UnityEngine.Object.DestroyImmediate(newP);
            sb.AppendLine();
            sb.AppendLine("BRAKE FEEL VERDICT = " + Pass(all));
            return sb.ToString();
        }
    }
}
