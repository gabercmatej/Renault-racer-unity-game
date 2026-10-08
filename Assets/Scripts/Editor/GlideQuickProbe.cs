using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;
using RenaultRacer.InputSystem;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Quick "ice puck" glide harness. Not a gate - a measuring stick. Reports how far the car
    /// carries across the road after a lock-and-release, on straights, bends, corners, after a
    /// grass rejoin, and in a ping-pong driver. Parameter overrides go in by reflection so the
    /// same file measures the before and after builds.
    /// </summary>
    public static class GlideQuickProbe
    {
        private const float Step = 1f / 60f;
        private const float Hw = RoadGeometry.RoadHalfWidth;

        private sealed class Held : IDriveInput
        {
            public float Steer;
            public float SteerAxis { get { return Steer; } }
            public bool BrakeHeld { get { return false; } }
            public bool HandbrakeHeld { get { return false; } }
        }

        public static HandlingProfile LoadProduction()
        {
            string[] guids = AssetDatabase.FindAssets("t:HandlingProfile Production", new[] { "Assets/ScriptableObjects/Handling" });
            for (int i = 0; i < guids.Length; i++)
            {
                var p = AssetDatabase.LoadAssetAtPath<HandlingProfile>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (p != null && p.name == "Production") return p;
            }
            return null;
        }

        public static void Override(HandlingProfile p, string name, float v)
        {
            FieldInfo f = typeof(HandlingProfile).GetField(name);
            if (f == null) return;
            f.SetValue(p, v);
        }

        // ---- track scan ----
        private static int FindRun(TrackModel t, float lo, float hi, int minLen, bool sign, bool wantPositive)
        {
            for (int i = 0; i + minLen < t.Count; i++)
            {
                bool ok = true;
                for (int j = 0; j < minLen; j++)
                {
                    float c = t[i + j].curve;
                    if (sign) c = wantPositive ? c : -c;
                    if (!(c >= lo && c <= hi)) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        private struct Glide { public float yawRelease, travel, settle, peakX, peakYaw; public bool wall; public float latAtRelease; }

        private static Glide LockRelease(HandlingProfile p, TrackModel t, int seg, float speedPct, float dir, float lockSecs, float window)
        {
            var sim = new CarSimulation(t, p);
            var inp = new Held();
            sim.Reset(seg * RoadGeometry.SegmentLength);
            sim.Speed = p.maxSpeed * speedPct;
            Glide g = new Glide { settle = 99f };
            int n = Mathf.RoundToInt(lockSecs / Step);
            for (int i = 0; i < n; i++) { inp.Steer = dir; sim.Step(Step, inp); g.peakYaw = Mathf.Max(g.peakYaw, Mathf.Abs(sim.Yaw)); g.peakX = Mathf.Max(g.peakX, Mathf.Abs(sim.X)); }
            g.yawRelease = Mathf.Abs(sim.Yaw) * Mathf.Rad2Deg;
            g.latAtRelease = Mathf.Abs(sim.LateralVel);
            float x0 = sim.X, calm = 0f, tt = 0f;
            int m = Mathf.RoundToInt(window / Step);
            for (int i = 0; i < m; i++)
            {
                inp.Steer = 0f; sim.Step(Step, inp); tt += Step;
                g.peakX = Mathf.Max(g.peakX, Mathf.Abs(sim.X));
                g.peakYaw = Mathf.Max(g.peakYaw, Mathf.Abs(sim.Yaw));
                if (Mathf.Abs(sim.X) >= t.BarrierRightAt(sim.Z) - 0.04f) g.wall = true;
                if (Mathf.Abs(sim.LateralVel) < 0.3f) { calm += Step; if (calm >= 0.25f && g.settle > 90f) { g.settle = tt - 0.25f; g.travel = Mathf.Abs(sim.X - x0); } }
                else calm = 0f;
            }
            if (g.settle > 90f) g.travel = Mathf.Abs(sim.X - x0);
            return g;
        }

        // grass -> road with the lock held toward the road. holdAfter = seconds lock stays after the boundary.
        private struct Re { public float cross, peakYaw, yawAtRejoin; public bool farGrass, ok; }

        private static Re GrassRejoin(HandlingProfile p, TrackModel t, int seg, float speedPct, float holdAfter, float entryX)
        {
            var sim = new CarSimulation(t, p);
            var inp = new Held();
            sim.Reset(seg * RoadGeometry.SegmentLength);
            sim.Speed = p.maxSpeed * speedPct;
            sim.X = entryX;
            Re r = new Re();
            sim.PointAt(10f * Mathf.Deg2Rad);
            int guard = 0;
            float tg = 0f;
            while (sim.X > 1f && guard++ < 600)
            {
                inp.Steer = tg < 0.2f ? 0f : -1f; sim.Step(Step, inp); tg += Step;
            }
            if (sim.X > 1f) return r;
            r.ok = true;
            r.yawAtRejoin = -sim.Yaw * Mathf.Rad2Deg;
            float x0 = sim.X;
            float th = 0f;
            int n = Mathf.RoundToInt(3f / Step);
            float minX = x0;
            for (int i = 0; i < n; i++)
            {
                inp.Steer = th < holdAfter ? -1f : 0f; th += Step;
                sim.Step(Step, inp);
                minX = Mathf.Min(minX, sim.X);
                r.peakYaw = Mathf.Max(r.peakYaw, Mathf.Abs(sim.Yaw) * Mathf.Rad2Deg);
            }
            r.cross = x0 - minX;
            r.farGrass = minX <= -1f;
            return r;
        }

        // ping-pong: whenever the car is outside |X|>0.55 and not already locking, hold full
        // lock toward the centre for holdSecs then release; speed pinned to speedPct.
        private static void PingPong(HandlingProfile p, TrackModel t, int seg, float speedPct, float holdSecs, float secs,
                                     out int crossings, out float meanYawRel)
        {
            var sim = new CarSimulation(t, p);
            var inp = new Held();
            sim.Reset(seg * RoadGeometry.SegmentLength);
            sim.Speed = p.maxSpeed * speedPct;
            sim.X = 0.75f;
            crossings = 0; meanYawRel = 0f;
            float lockT = 0f, dir = 0f; int lastSide = 1; int relCount = 0; float yawSum = 0f;
            int n = Mathf.RoundToInt(secs / Step);
            for (int i = 0; i < n; i++)
            {
                if (dir == 0f && Mathf.Abs(sim.X) > 0.55f) { dir = -Mathf.Sign(sim.X); lockT = 0f; }
                inp.Steer = dir;
                if (dir != 0f) { lockT += Step; if (lockT >= holdSecs) { dir = 0f; yawSum += Mathf.Abs(sim.Yaw) * Mathf.Rad2Deg; relCount++; } }
                sim.Speed = p.maxSpeed * speedPct;
                sim.Step(Step, inp);
                if (sim.X > 0.7f && lastSide < 0) { crossings++; lastSide = 1; }
                else if (sim.X < -0.7f && lastSide > 0) { crossings++; lastSide = -1; }
            }
            meanYawRel = relCount > 0 ? yawSum / relCount : 0f;
        }

        public static string Trace(HandlingProfile p, TrackModel t, int seg, float speedPct, float dir)
        {
            var sim = new CarSimulation(t, p);
            var inp = new Held();
            var sb = new StringBuilder();
            sim.Reset(seg * RoadGeometry.SegmentLength);
            sim.Speed = p.maxSpeed * speedPct;
            for (int i = 0; i < 90; i++)
            {
                inp.Steer = i < 24 ? dir : 0f;
                sim.Step(Step, inp);
                if (i % 3 == 2 && i < 60)
                    sb.AppendLine(string.Format("   t {0:F2} {1} yaw {2,6:F1} yawRate {3,6:F0} slip {4,6:F2}hw/s lat {5,6:F2} X {6:F2} carry {7:F0} align {8:F0}",
                        (i + 1) * Step, i < 24 ? "LOCK" : "free", sim.Yaw * Mathf.Rad2Deg, sim.WorldYawRate * Mathf.Rad2Deg, sim.SlipVel / Hw, sim.LateralVel, sim.X, sim.CarryLimitRate * Mathf.Rad2Deg, sim.StraightAlignRate * Mathf.Rad2Deg));
            }
            return sb.ToString();
        }

        public static string BuildReport(HandlingProfile p)
        {
            TrackModel t = TrackBuilder.BuildMainTrack();
            var sb = new StringBuilder();
            sb.AppendLine("TRACE gentleR 40% lock-"); sb.Append(Trace(p, t, 476, 0.4f, -1f));
            sb.AppendLine("TRACE gentleR 40% lock+"); sb.Append(Trace(p, t, 476, 0.4f, 1f));
            sb.AppendLine("TRACE hardR 40% lock-"); sb.Append(Trace(p, t, 372, 0.4f, -1f));
            int straight = FindRun(t, 0f, 0f, 60, false, true);
            int gentleR = FindRun(t, 2f, 3.2f, 6, true, true);
            int gentleL = FindRun(t, 2f, 3.2f, 6, true, false);
            int hard = FindRun(t, 4f, 99f, 6, true, true);
            int hardL = FindRun(t, 4f, 99f, 6, true, false);
            sb.AppendLine("segs: straight " + straight + " gentleR " + gentleR + " gentleL " + gentleL + " hardR " + hard + " hardL " + hardL);
            float[] sp = { 0.4f, 0.9f };
            sb.AppendLine("(a/b) lock 0.4s then release. yaw@rel deg | travel hw | settle s | peakX | wall");
            string[] names = { "straight", "gentleR", "gentleL", "hardR", "hardL" };
            int[] segs = { straight, gentleR, gentleL, hard, hardL };
            for (int k = 0; k < names.Length; k++)
            {
                if (segs[k] < 0) continue;
                for (int s = 0; s < sp.Length; s++)
                    for (int d = -1; d <= 1; d += 2)
                    {
                        if (k == 0 && d < 0) continue;
                        Glide g = LockRelease(p, t, segs[k] + 1, sp[s], d, 0.4f, k == 0 ? 5f : 1.5f);
                        sb.AppendLine(string.Format("  {0,-8} {1,3:F0}% lock{2}: yaw {3,5:F1}  travel {4:F2}  settle {5,4:F2}  peakX {6:F2}  wall {7}",
                            names[k], sp[s] * 100f, d > 0 ? "+" : "-", g.yawRelease, g.travel, g.settle, g.peakX, g.wall ? "Y" : "n"));
                    }
            }
            sb.AppendLine("(c) grass->road, lock held toward road. cross = half-widths carried across after rejoin; far = reached far grass");
            float[] spc = { 0.4f, 0.65f };
            float[] hold = { 0f, 0.2f };
            for (int s = 0; s < spc.Length; s++)
                for (int h = 0; h < hold.Length; h++)
                {
                    Re r = GrassRejoin(p, t, straight + 1, spc[s], hold[h], 1.05f);
                    sb.AppendLine(string.Format("  {0,3:F0}% hold+{1:F1}s: ok {2} yawAtRejoin {3:F1}  cross {4:F2}  far {5}  peakYaw {6:F1}",
                        spc[s] * 100f, hold[h], r.ok ? "Y" : "n", r.yawAtRejoin, r.cross, r.farGrass ? "Y" : "n", r.peakYaw));
                }
            sb.AppendLine("(d) ping-pong 10s: wall-to-wall crossings (lower is better), mean yaw at release deg");
            float[] holds = { 0.3f, 0.45f, 0.6f };
            int tot = 0;
            for (int s = 0; s < 2; s++)
                for (int h = 0; h < holds.Length; h++)
                {
                    int c; float my;
                    PingPong(p, t, straight + 1, s == 0 ? 0.4f : 0.65f, holds[h], 10f, out c, out my);
                    tot += c;
                    sb.AppendLine(string.Format("  {0,3:F0}% hold {1:F2}s: crossings {2}  yaw@rel {3:F1}", (s == 0 ? 40 : 65), holds[h], c, my));
                }
            sb.AppendLine("  total crossings " + tot);
            return sb.ToString();
        }

        public static void RunFromCommandLine()
        {
            try
            {
                HandlingProfile p = LoadProduction();
                string report = BuildReport(p);
                Directory.CreateDirectory("Logs");
                File.WriteAllText("Logs/glide-quick.txt", report);
                Console.WriteLine(report);
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Console.WriteLine("glide quick threw: " + e); EditorApplication.Exit(2); }
        }

        public static void RunGrassTrace()
        {
            try
            {
                HandlingProfile p = LoadProduction();
                TrackModel t = TrackBuilder.BuildMainTrack();
                int corner = GrassRecoveryProbe.FindCorner(t, 4f, 5f);
                int outside = t[corner].curve > 0f ? -1 : 1;
                var sb = new StringBuilder();
                var tr = new StringBuilder();
                GrassRecoveryProbe.Result a = GrassRecoveryProbe.Run(p, t, corner, -outside, 0.60f, 10f, GrassRecoveryProbe.GrassReaction, GrassRecoveryProbe.Post.Release, null, tr);
                sb.AppendLine("GRASS cross " + a.cross); sb.Append(tr.ToString());
                tr = new StringBuilder();
                GrassRecoveryProbe.Result b = GrassRecoveryProbe.Run(GrassRecoveryProbe.GrassAsRoad(p), t, corner, -outside, 0.60f, 10f, GrassRecoveryProbe.GrassReaction, GrassRecoveryProbe.Post.Release, null, tr);
                sb.AppendLine("TARMAC cross " + b.cross); sb.Append(tr.ToString());
                File.WriteAllText("Logs/grass-trace.txt", sb.ToString());
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Console.WriteLine("threw: " + e); EditorApplication.Exit(2); }
        }

        public static void RunSweepW1()
        {
            try
            {
                string[] lines = File.ReadAllLines("Logs/glide-sweep-in.txt");
                var sb = new StringBuilder();
                TrackModel t = TrackBuilder.BuildMainTrack();
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    HandlingProfile p = UnityEngine.Object.Instantiate(LoadProduction());
                    foreach (string kv in line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] a = kv.Split('=');
                        Override(p, a[0], float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    sb.AppendLine("=== " + line);
                    sb.AppendLine(GlideProbe.QuickSummary(p, t));
                }
                Directory.CreateDirectory("Logs");
                File.WriteAllText("Logs/glide-sweep-w1.txt", sb.ToString());
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Console.WriteLine("glide sweep threw: " + e); EditorApplication.Exit(2); }
        }

        /// <summary>Logs/glide-sweep-in.txt: one config per line, "name=value name=value". Writes Logs/glide-sweep.txt.</summary>
        public static void RunSweep()
        {
            try
            {
                string[] lines = File.ReadAllLines("Logs/glide-sweep-in.txt");
                var sb = new StringBuilder();
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    HandlingProfile p = UnityEngine.Object.Instantiate(LoadProduction());
                    foreach (string kv in line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] a = kv.Split('=');
                        Override(p, a[0], float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    sb.AppendLine("=== " + line);
                    sb.Append(BuildReport(p));
                }
                Directory.CreateDirectory("Logs");
                File.WriteAllText("Logs/glide-sweep.txt", sb.ToString());
                Console.WriteLine(sb.ToString());
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Console.WriteLine("glide sweep threw: " + e); EditorApplication.Exit(2); }
        }
    }
}
