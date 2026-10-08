using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;
using RenaultRacer.InputSystem;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// GLIDE PROBE - the yardstick for Maj's "ice glide" / wall-to-wall ping-pong.
    ///
    /// Evidence (sliding bug video, 0:17-0:29, debug HUD transcribed frame by frame): at
    /// only 15-50% speed the car crosses the whole road in well under a second, again and
    /// again. At every sample the crossing is HEADING (slip is ~0 on tarmac): the player
    /// holds full lock for ~1 s, yaw runs up to the 70 deg steer-envelope limit, the car
    /// then drives across the road at Speed * sin(70) and meets the other wall.
    ///
    /// This drives the real CarSimulation on the real circuit with a given HandlingProfile
    /// through scripted manoeuvres, measures what the car does after the player lets go, and
    /// traces +/-1 s around every grass transition. RunAll takes the profile so a fix can be
    /// re-measured by passing a modified copy.
    ///
    /// Sign convention: dir = +1 steers right. Yaw is relative to the road, + = nose right.
    /// </summary>
    public static class GlideProbe
    {
        private const float Step = 1f / 60f;
        private const float Hw = RoadGeometry.RoadHalfWidth;
        private const float Seg = RoadGeometry.SegmentLength;

        // ---- how a run is graded ----
        public const float SettledLatVel = 0.30f;    // hw/s: |LateralVel| below this = settled
        public const float SettledHold = 0.30f;      // s it must stay under
        public const float PostSeconds = 4.0f;       // observation window after release
        public const float WideTravel = 1.5f;        // hw: travel after release that counts as "crossed the road"

        // ---- tunable PASS/FAIL bars (INFORMATION ONLY until EnforceBars is set; the lead sets the final numbers) ----
        public static bool EnforceBars = false;
        public const float BarMaxTravelAfterRelease = 1.0f;   // hw, road-only hard steer, held 0.6 s
        public const float BarMaxPingPongCrossings = 1;       // wall-to-wall crossings in 10 s at ~40%
        public const float BarMaxPeakYawDeg = 50f;            // deg, ping-pong

        // ---- discontinuity flags, applied to ONE 1/60 s step at a surface transition ----
        public const float DiscCeil = 0.15f;      // rad/s
        public const float DiscGrip = 0.20f;
        public const float DiscLatV = 0.15f;      // hw/s per step, SECOND difference (jerk): steady rotation at speed already changes latV every step
        public const float DiscYawRate = 0.30f;   // rad/s per step, second difference

        private sealed class Held : IDriveInput
        {
            public float Steer;
            public float SteerAxis { get { return Steer; } }
            public bool BrakeHeld { get { return false; } }
            public bool HandbrakeHeld { get { return false; } }
        }

        public struct Sample
        {
            public float t, cmd, steer, x, z, spdPct, speed, wh, rh, yaw, wyr, latV, slipV, along, grip, ceil, gate, align, curve, envelope;
            public bool off, impact, wall;
            public Surface surf;
        }

        public sealed class Run
        {
            public string name;
            public int seg, dir;
            public float x0, speedPct, yaw0Deg, duration;
            public float releaseT = -1f;
            public bool traceIt;
            public Func<CarSimulation, Run, float> driver;
            public float time;
            public List<Sample> s = new List<Sample>(900);
        }

        private struct Place { public string name; public int seg; }

        public struct M
        {
            public bool released, settled, wallAfter, crossed, farGrass, grassAfter, everGrass;
            public float yawRel, peakYaw, settle, travel, maxSlipHw, depth, returnT, endT;
            public float headLatAtMax, slipLatAtMax, maxLat;
            public float impactYawBefore, impactYawAfter, impactT;
            public int impacts;
        }

        public struct Trans
        {
            public string kind, run;
            public float t, ceilB, ceilA, gripB, gripA, spdB, spdA, yawB, yawA, steerB;
            public float dCeil1, dGrip1, dLat1, dYR1, dCeilW, dLatW, dYRW;   // dLatW/dYRW = worst second difference in the window
            public bool flag, impactInWindow;
        }

        // ------------------------------------------------------------------ executing

        public static void Execute(HandlingProfile p, TrackModel t, Run r)
        {
            var sim = new CarSimulation(t, p);
            var inp = new Held();
            sim.Reset(r.seg * Seg);
            sim.Speed = p.maxSpeed * r.speedPct;
            sim.X = r.x0;
            sim.PointAt(r.yaw0Deg * Mathf.Deg2Rad);
            r.s.Clear();
            r.releaseT = -1f;
            r.time = 0f;
            r.s.Add(Make(sim, t, 0f, 0f, 0f));
            int n = Mathf.RoundToInt(r.duration / Step);
            for (int i = 0; i < n; i++)
            {
                r.time = i * Step;
                float cmd = r.driver(sim, r);
                inp.Steer = cmd;
                sim.Step(Step, inp);
                float imp = sim.ConsumeImpact();
                r.s.Add(Make(sim, t, (i + 1) * Step, cmd, imp));
            }
        }

        private static Sample Make(CarSimulation sim, TrackModel t, float time, float cmd, float imp)
        {
            Sample a = new Sample();
            a.t = time; a.cmd = cmd; a.steer = sim.Steer; a.x = sim.X; a.z = sim.Z;
            a.spdPct = sim.SpeedPct; a.speed = sim.Speed;
            a.wh = sim.WorldHeading; a.rh = sim.RoadHeading; a.yaw = sim.Yaw; a.wyr = sim.WorldYawRate;
            a.latV = sim.LateralVel; a.slipV = sim.SlipVel; a.along = sim.AlongVel;
            a.grip = sim.SurfaceGrip; a.ceil = sim.YawRateCeiling; a.gate = sim.StraightGate;
            a.align = sim.StraightAlignRate; a.envelope = sim.SteerYawEnvelope;
            a.curve = t[t.SegmentIndexAt(sim.Z)].curve;
            a.off = Mathf.Abs(sim.X) > 1f; a.impact = imp > 0f; a.surf = sim.CurrentSurface;
            a.wall = sim.X <= t.BarrierLeftAt(sim.Z) + CarSimulation.BarrierContactBand
                  || sim.X >= t.BarrierRightAt(sim.Z) - CarSimulation.BarrierContactBand;
            return a;
        }

        private static bool Off(CarSimulation s) { return Mathf.Abs(s.X) > 1f; }

        // ------------------------------------------------------------------ measuring

        public static M Measure(Run r)
        {
            M m = new M { settle = -1f, returnT = -1f, impactT = -1f };
            var s = r.s;
            for (int i = 0; i < s.Count; i++)
            {
                m.peakYaw = Mathf.Max(m.peakYaw, Mathf.Abs(s[i].yaw) * Mathf.Rad2Deg);
                m.maxSlipHw = Mathf.Max(m.maxSlipHw, Mathf.Abs(s[i].slipV) / Hw);
                if (s[i].off) m.everGrass = true;
                if (s[i].impact)
                {
                    m.impacts++;
                    if (m.impactT < 0f && (r.releaseT < 0f || s[i].t <= r.releaseT + 0.01f))
                    {
                        m.impactT = s[i].t;
                        m.impactYawBefore = s[Mathf.Max(0, i - 1)].yaw * Mathf.Rad2Deg;
                        m.impactYawAfter = s[Mathf.Min(s.Count - 1, i + 6)].yaw * Mathf.Rad2Deg;
                    }
                }
            }
            if (r.releaseT < 0f) return m;
            m.released = true;
            int ri = Mathf.Min(s.Count - 1, Mathf.RoundToInt(r.releaseT / Step));
            m.yawRel = s[ri].yaw * Mathf.Rad2Deg * r.dir;
            float xRel = s[ri].x;
            float calm = 0f, maxLat = 0f;
            int end = s.Count - 1;
            for (int i = ri; i < s.Count; i++)
            {
                float el = s[i].t - r.releaseT;
                if (el > PostSeconds) { end = i; break; }
                if (i > ri && s[i].impact) { m.wallAfter = true; end = i; break; }
                m.travel = Mathf.Max(m.travel, Mathf.Abs(s[i].x - xRel));
                m.depth = Mathf.Max(m.depth, Mathf.Abs(s[i].x));
                if (s[i].x * r.dir > 1f) m.farGrass = true;
                if (s[i].off && i > ri) m.grassAfter = true;
                if (m.returnT < 0f && s[ri].off && !s[i].off) m.returnT = el;
                float al = Mathf.Abs(s[i].latV);
                if (al > maxLat)
                {
                    maxLat = al;
                    m.headLatAtMax = s[i].speed * Mathf.Sin(s[i].yaw) / Hw;
                    m.slipLatAtMax = s[i].slipV * Mathf.Cos(s[i].yaw) / Hw;
                }
                if (al < SettledLatVel)
                {
                    calm += Step;
                    if (calm >= SettledHold && !m.settled) { m.settled = true; m.settle = el - calm + Step; end = i; break; }
                }
                else calm = 0f;
            }
            m.maxLat = maxLat;
            m.endT = s[end].t - r.releaseT;
            m.crossed = m.travel >= WideTravel;
            return m;
        }

        public static List<Trans> Transitions(Run r, StringBuilder trace)
        {
            var res = new List<Trans>();
            var s = r.s;
            for (int i = 1; i < s.Count - 1; i++)
            {
                if (i < 6 || s[i].off == s[i - 1].off) continue;   // i < 6: the initial placement of the car is not a transition
                Trans q = new Trans();
                q.kind = s[i].off ? "ROAD->GRASS" : "GRASS->ROAD";
                q.run = r.name; q.t = s[i].t;
                q.ceilB = s[i - 1].ceil; q.gripB = s[i - 1].grip; q.spdB = s[i - 1].spdPct; q.yawB = s[i - 1].yaw * Mathf.Rad2Deg; q.steerB = s[i - 1].steer;
                int j = Mathf.Min(s.Count - 1, i + 4);
                q.ceilA = s[j].ceil; q.gripA = s[j].grip; q.spdA = s[j].spdPct; q.yawA = s[j].yaw * Mathf.Rad2Deg;
                q.dCeil1 = s[i].ceil - s[i - 1].ceil;
                q.dGrip1 = s[i].grip - s[i - 1].grip;
                q.dLat1 = s[i].latV - s[i - 1].latV;
                q.dYR1 = s[i].wyr - s[i - 1].wyr;
                for (int k = Mathf.Max(2, i - 1); k <= j; k++)
                {
                    q.dLatW = Mathf.Max(q.dLatW, Mathf.Abs((s[k].latV - s[k - 1].latV) - (s[k - 1].latV - s[k - 2].latV)));
                    q.dYRW = Mathf.Max(q.dYRW, Mathf.Abs((s[k].wyr - s[k - 1].wyr) - (s[k - 1].wyr - s[k - 2].wyr)));
                    if (s[k].impact) q.impactInWindow = true;
                }
                q.dCeilW = s[j].ceil - s[i - 1].ceil;
                q.flag = !q.impactInWindow && (Mathf.Abs(q.dCeil1) > DiscCeil || Mathf.Abs(q.dGrip1) > DiscGrip
                      || q.dLatW > DiscLatV || q.dYRW > DiscYawRate);
                res.Add(q);

                if (trace != null && r.traceIt)
                {
                    trace.AppendLine(string.Format("  == {0}  {1} at t={2:F2}s  (flag {3}: dCeil1 {4:+0.00;-0.00} dGrip1 {5:+0.00;-0.00} latV jerk max {6:F2} yawRate jerk max {7:F2})",
                        r.name, q.kind, q.t, q.flag ? "DISC" : (q.impactInWindow ? "ok, wall impact in window" : "ok"), q.dCeil1, q.dGrip1, q.dLatW, q.dYRW));
                    trace.AppendLine("     dt    surf   spd%  wHead  rHead   yaw   wYawR cmd  steer  latV  slipV  alongV grip  ceil  gate  align");
                    for (int k = Mathf.Max(0, i - 60); k <= Mathf.Min(s.Count - 1, i + 60); k += 3)
                    {
                        Sample a = s[k];
                        trace.AppendLine(string.Format(
                            "    {0,5:+0.00;-0.00} {1,-6} {2,4:F0} {3,7:F1} {4,6:F1} {5,6:F1} {6,6:F2} {7,4:F1} {8,5:F2} {9,6:F2} {10,6:F2} {11,6:F0} {12,4:F2} {13,5:F2} {14,4:F2} {15,6:F2}{16}",
                            a.t - s[i].t, a.surf, a.spdPct * 100f, a.wh * Mathf.Rad2Deg, a.rh * Mathf.Rad2Deg, a.yaw * Mathf.Rad2Deg,
                            a.wyr, a.cmd, a.steer, a.latV, a.slipV / Hw, a.along, a.grip, a.ceil, a.gate, a.align,
                            a.impact ? "  IMPACT" : (k == i ? "  <-- transition" : "")));
                    }
                }
            }
            return res;
        }

        // ------------------------------------------------------------------ scenario factories

        private static Run Base(string name, int seg, int dir, float pct, float x0, float yaw0Deg, float dur)
        {
            return new Run { name = name, seg = seg, dir = dir, speedPct = pct, x0 = x0, yaw0Deg = yaw0Deg, duration = dur };
        }

        /// <summary>1. Road only: full lock from the centre, held, then released.</summary>
        public static Run RoadHardSteer(int seg, int dir, float pct, float hold, string name)
        {
            Run r = Base(name, seg, dir, pct, 0f, 0f, hold + PostSeconds);
            r.driver = (sim, rr) =>
            {
                if (rr.time < hold) return dir;
                if (rr.releaseT < 0f) rr.releaseT = rr.time;
                return 0f;
            };
            return r;
        }

        /// <summary>2. Road -> grass with lock held toward the grass; released g seconds after the grass; then hands off.</summary>
        public static Run RoadToGrassRelease(int seg, int side, float pct, float g, string name)
        {
            Run r = Base(name, seg, side, pct, side * 0.8f, 0f, 6f);
            float grassT = -1f;
            r.driver = (sim, rr) =>
            {
                if (rr.releaseT >= 0f) return 0f;
                if (Off(sim) && grassT < 0f) grassT = rr.time;
                if (grassT >= 0f && rr.time - grassT >= g - 1e-4f) { rr.releaseT = rr.time; return 0f; }
                return side;
            };
            return r;
        }

        /// <summary>3. Grass -> road with lock held toward the road; released d seconds after the tarmac is reached.</summary>
        public static Run GrassToRoad(int seg, int side, float pct, float d, string name)
        {
            Run r = Base(name, seg, -side, pct, side * 1.3f, 0f, 1.5f + d + PostSeconds);
            float inT = -1f;
            r.driver = (sim, rr) =>
            {
                if (rr.releaseT >= 0f) return 0f;
                if (!Off(sim))
                {
                    if (inT < 0f) inT = rr.time;
                    if (rr.time - inT >= d - 1e-4f) { rr.releaseT = rr.time; return 0f; }
                }
                return -side;
            };
            return r;
        }

        /// <summary>4. Road -> grass -> road: leave at entryYaw, hands off react s on the grass, lock back, release d s after re-entry.</summary>
        public static Run RoadGrassRoad(int seg, int side, float pct, float entryYawDeg, float react, float d, string name)
        {
            Run r = Base(name, seg, -side, pct, side * 0.97f, side * entryYawDeg, 8f);
            float grassT = -1f, inT = -1f; bool locked = false;
            r.driver = (sim, rr) =>
            {
                if (rr.releaseT >= 0f) return 0f;
                if (Off(sim) && grassT < 0f) grassT = rr.time;
                if (grassT >= 0f && rr.time - grassT >= react) locked = true;
                if (!locked) return 0f;
                if (!Off(sim))
                {
                    if (inT < 0f) inT = rr.time;
                    if (rr.time - inT >= d - 1e-4f) { rr.releaseT = rr.time; return 0f; }
                }
                return -side;
            };
            return r;
        }

        /// <summary>6. A barrier hit on the grass at angleDeg into the wall, lock held AWAY from the wall until back on tarmac + 0.15 s.</summary>
        public static Run BarrierHit(TrackModel t, int seg, int side, float pct, float angleDeg, string name)
        {
            float limit = side > 0 ? t[seg].barrierRight : -t[seg].barrierLeft;
            float x0 = side * Mathf.Max(1.1f, Mathf.Abs(limit) - 0.35f);
            Run r = Base(name, seg, -side, pct, x0, side * angleDeg, 8f);
            float inT = -1f;
            r.driver = (sim, rr) =>
            {
                if (rr.releaseT >= 0f) return 0f;
                if (!Off(sim))
                {
                    if (inT < 0f) inT = rr.time;
                    if (rr.time - inT >= 0.15f) { rr.releaseT = rr.time; return 0f; }
                }
                return -side;
            };
            return r;
        }

        /// <summary>
        /// 5. Maj's ping-pong, as read off the video: lock toward the road from the moment the car has
        /// been on the grass (or wall) for <paramref name="react"/> s, and keep it until the car is
        /// past the centre line (yawReleaseDeg &lt; 0) or - the careful human - until the nose points
        /// yawReleaseDeg back across the road. Hands off in between.
        /// </summary>
        public static Run PingPong(int seg, float pct, float react, float yawReleaseDeg, float dur, string name)
        {
            Run r = Base(name, seg, 1, pct, 0.95f, 0f, dur);
            bool lockOn = true; int lockSide = 1; float grassT = -1f;
            r.driver = (sim, rr) =>
            {
                if (lockOn)
                {
                    bool center = sim.X * lockSide <= 0f;
                    bool yawOk = yawReleaseDeg > 0f && (-lockSide * sim.Yaw * Mathf.Rad2Deg) >= yawReleaseDeg;
                    if (center || yawOk) { lockOn = false; grassT = -1f; return 0f; }
                    return -lockSide;
                }
                if (Off(sim))
                {
                    if (grassT < 0f) grassT = rr.time;
                    if (rr.time - grassT >= react) { lockOn = true; lockSide = sim.X > 0f ? 1 : -1; return -lockSide; }
                }
                else grassT = -1f;
                return 0f;
            };
            return r;
        }

        public struct PP
        {
            public int crossings, impacts, locks;
            public float avgCross, minCross, grassPct, peakYaw, holdAvg, holdMax, yaw60Pct, meanSpd, minSpd, maxSpd, fastLatPct;
        }

        public static PP MeasurePP(Run r)
        {
            PP o = new PP { minCross = 99f, minSpd = 9f };
            var s = r.s;
            int lastSide = 0, prevSd = 0; float reT = 0f; float crossSum = 0f; float lastImpactT = -9f;
            int grass = 0, y60 = 0, fast = 0; float spdSum = 0f;
            float holdStart = -1f; float holdSum = 0f;
            for (int i = 0; i < s.Count; i++)
            {
                Sample a = s[i];
                int sd = a.x > 1f ? 1 : (a.x < -1f ? -1 : 0);
                if (sd == 0) { if (prevSd != 0) reT = a.t; }
                else
                {
                    if (lastSide != 0 && sd != lastSide)
                    {
                        o.crossings++; float ct = a.t - reT; crossSum += ct; o.minCross = Mathf.Min(o.minCross, ct);
                    }
                    lastSide = sd; grass++;
                }
                prevSd = sd;
                if (a.impact && a.t - lastImpactT > 0.25f) o.impacts++;
                if (a.impact) lastImpactT = a.t;
                float ay = Mathf.Abs(a.yaw) * Mathf.Rad2Deg;
                o.peakYaw = Mathf.Max(o.peakYaw, ay);
                if (ay > 60f) y60++;
                if (Mathf.Abs(a.latV) > 2f) fast++;
                spdSum += a.spdPct; o.minSpd = Mathf.Min(o.minSpd, a.spdPct); o.maxSpd = Mathf.Max(o.maxSpd, a.spdPct);
                bool lk = Mathf.Abs(a.cmd) > 0.5f;
                if (lk && holdStart < 0f) { holdStart = a.t; o.locks++; }
                if (!lk && holdStart >= 0f)
                {
                    float h = a.t - holdStart; holdSum += h; o.holdMax = Mathf.Max(o.holdMax, h); holdStart = -1f;
                }
            }
            if (holdStart >= 0f) { float h = s[s.Count - 1].t - holdStart; holdSum += h; o.holdMax = Mathf.Max(o.holdMax, h); }
            int n = s.Count;
            o.avgCross = o.crossings > 0 ? crossSum / o.crossings : 0f;
            if (o.crossings == 0) o.minCross = 0f;
            o.grassPct = 100f * grass / n; o.yaw60Pct = 100f * y60 / n; o.fastLatPct = 100f * fast / n;
            o.meanSpd = spdSum / n; o.holdAvg = o.locks > 0 ? holdSum / o.locks : 0f;
            return o;
        }

        // ------------------------------------------------------------------ report helpers

        public static HandlingProfile GrassAsRoad(HandlingProfile p)
        {
            HandlingProfile q = UnityEngine.Object.Instantiate(p);
            q.name = p.name + " (grass = tarmac)";
            q.offRoadGrip = 1f; q.offRoadDrag = 0f; q.offRoadSpeedCap = 1f;
            return q;
        }

        public static int FindStraight(TrackModel t)
        {
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

        private static string Rowf(string label, Run r, M m, List<Trans> tr)
        {
            bool disc = false;
            for (int i = 0; i < tr.Count; i++) disc |= tr[i].flag;
            if (!m.released) return string.Format("    {0,-26} never released (grass reached: {1})", label, m.everGrass);
            return string.Format("    {0,-26} yawRel {1,6:F1} pkYaw {2,5:F1} | settle {3,5} travel {4,4:F2}hw{5} end {6,4:F2}s{7}{8} | maxLat {9,4:F2} (head {10,5:F2} slip {11,5:F2}) maxSlip {12,4:F2}hw/s{13}{14}",
                label, m.yawRel, m.peakYaw, m.settled ? m.settle.ToString("F2") : ">" + PostSeconds.ToString("F1"),
                m.travel, m.crossed ? " CROSSED" : "        ", m.endT, m.wallAfter ? " WALL" : "", m.farGrass ? " FAR-GRASS" : "",
                m.maxLat, m.headLatAtMax, m.slipLatAtMax, m.maxSlipHw,
                m.returnT >= 0f ? string.Format(" back@{0:F2}s", m.returnT) : "", disc ? "  DISC" : "");
        }

        private static string PPRow(string label, PP o)
        {
            return string.Format("    {0,-34} crossings {1,2} (avg {2:F2}s min {3:F2}s) wall hits {4,2} locks {5,2} hold avg {6:F2}s max {7:F2}s | pkYaw {8,5:F1} yaw>60 {9,4:F1}% latV>2hw/s {10,4:F1}% grass {11,4:F1}% | spd mean {12:F2} [{13:F2}..{14:F2}]",
                label, o.crossings, o.avgCross, o.minCross, o.impacts, o.locks, o.holdAvg, o.holdMax, o.peakYaw, o.yaw60Pct, o.fastLatPct, o.grassPct, o.meanSpd, o.minSpd, o.maxSpd);
        }

        private static string Ok(bool ok) { return EnforceBars ? (ok ? "PASS" : "**FAIL**") : (ok ? "(bar ok)" : "(bar missed, info)"); }

        private static float Ceiling(HandlingProfile p, TrackModel t, float pct, float x, int seg)
        {
            var sim = new CarSimulation(t, p);
            var inp = new Held();
            sim.Reset(seg * Seg);
            sim.X = x;
            for (int i = 0; i < 30; i++) { sim.Speed = p.maxSpeed * pct; sim.Step(Step, inp); sim.X = x; }
            return sim.YawRateCeiling;
        }

        // ------------------------------------------------------------------ the report

        public static string RunAll(HandlingProfile p, TrackModel track, out string traces)
        {
            StringBuilder sb = new StringBuilder(1 << 16);
            StringBuilder tb = new StringBuilder(1 << 16);
            var allTrans = new List<Trans>();

            int straight = FindStraight(track) + 10;
            int bend = FindCorner(track, 1.5f, 2.5f);
            if (bend < 0) bend = FindCorner(track, 1f, 3f);
            int corner = FindCorner(track, 4f, 5f);
            if (corner < 0) corner = FindCorner(track, 2.5f, 9f);
            Place[] places =
            {
                new Place { name = "straight", seg = straight },
                new Place { name = "bend", seg = bend },
                new Place { name = "corner", seg = corner },
            };
            float[] speeds = { 0.40f, 0.65f, 0.95f };
            HandlingProfile road = GrassAsRoad(p);

            sb.AppendLine("=================== GLIDE PROBE ===================");
            sb.AppendLine("profile " + p.name + ": steeringStrength " + p.steeringStrength + " lowSpeedSteering " + p.lowSpeedSteering + " highSpeedSteering " + p.highSpeedSteering
                + " maxYawRate " + p.maxYawRate + " yawAuthoritySpeed " + p.yawAuthoritySpeed + " yawRateResponse " + p.yawRateResponse + " yawRateRelease " + p.yawRateRelease
                + " envelope " + (p.steerYawSoftLimit * Mathf.Rad2Deg).ToString("F0") + "->" + (p.steerYawHardLimit * Mathf.Rad2Deg).ToString("F0") + "deg"
                + " straightAlignRate " + p.straightAlignRate + " straightGateCurve " + p.straightGateCurve
                + " offRoadGrip " + p.offRoadGrip + " offRoadYawLoss " + p.offRoadYawLoss + " offRoadDrag " + p.offRoadDrag + " offRoadSpeedCap " + p.offRoadSpeedCap
                + " surfaceGripBlend " + p.surfaceGripBlend + " collisionYawKick " + p.collisionYawKick + " barrierYawRecovery " + p.barrierYawRecovery
                + " collisionBounce " + p.collisionBounce + " collisionSpeedLoss " + p.collisionSpeedLoss);
            foreach (Place pl in places)
                sb.AppendLine("place " + pl.name + ": seg " + pl.seg + " curve " + track[pl.seg].curve + " barriers " + track[pl.seg].barrierLeft + "/" + track[pl.seg].barrierRight);
            sb.AppendLine("lock = full steer (+1 right). yawRel = yaw at release in the direction of the lock (deg). travel = furthest |dX| after release (hw), window ends at settle (|latV|<"
                + SettledLatVel + " for " + SettledHold + "s) / wall impact / " + PostSeconds + "s. CROSSED = travel >= " + WideTravel + " hw. latV/slip in hw/s (road is 2 hw wide).");
            sb.AppendLine("'head'/'slip' at the instant of peak |latV| after release: lateral hw/s from HEADING (Speed*sin yaw) vs SLIP (SlipVel*cos yaw).");
            sb.AppendLine();

            // ---- [A] authority --------------------------------------------------------
            sb.AppendLine("[A1] STEERING AUTHORITY (YawRateCeiling at full lock) road vs grass, hands off, speed held");
            sb.AppendLine("      spd%   road rad/s (deg/s)   grass rad/s (deg/s)  grass/road   time to 45deg / 70deg at the ROAD ceiling (lag-free)");
            foreach (float pct in new[] { 0.15f, 0.20f, 0.25f, 0.30f, 0.40f, 0.65f, 0.95f })
            {
                float cr = Ceiling(p, track, pct, 0f, straight), cg = Ceiling(p, track, pct, 1.5f, straight);
                sb.AppendLine(string.Format("      {0,4:F2}   {1,5:F2} ({2,5:F0})        {3,5:F2} ({4,5:F0})         {5,4:F2}        {6,4:F2}s / {7,4:F2}s",
                    pct, cr, cr * Mathf.Rad2Deg, cg, cg * Mathf.Rad2Deg, cg / cr, 45f / (cr * Mathf.Rad2Deg), 70f / (cr * Mathf.Rad2Deg)));
            }
            sb.AppendLine("[A2] SPEED ON GRASS (hands off, throttle on, X=1.5, straight): speed% and ceiling vs time -> authority RISES as the grass bleeds speed");
            foreach (float pct in new[] { 0.95f, 0.65f, 0.40f })
            {
                var sim = new CarSimulation(track, p); var inp = new Held();
                sim.Reset(straight * Seg); sim.Speed = p.maxSpeed * pct; sim.X = 1.5f;
                StringBuilder row = new StringBuilder(string.Format("      start {0:F2}: ", pct));
                for (int i = 0; i <= 180; i++)
                {
                    if (i % 30 == 0) row.Append(string.Format("t{0:F1}s spd {1:F2} ceil {2:F2} | ", i * Step, sim.SpeedPct, sim.YawRateCeiling));
                    sim.Step(Step, inp); sim.X = 1.5f;
                }
                sb.AppendLine(row.ToString());
            }
            sb.AppendLine();

            // ---- [V0] where the video happened ----------------------------------------
            sb.AppendLine("[V0] TRACK CURVATURE AT THE VIDEO'S Z VALUES (HUD z, seg = z/200). |curve| < straightGateCurve " + p.straightGateCurve + " = gate open");
            float[] vz = { 79097, 84507, 87101, 89502, 94122, 96052, 99488, 100901, 103018, 104884, 106140, 107210, 109004, 111487, 114100, 115512 };
            StringBuilder vs = new StringBuilder("      ");
            foreach (float z in vz)
            {
                int si = track.SegmentIndexAt(z);
                vs.Append(string.Format("z{0:F0}: seg {1} curve {2:F2} gate {3} | ", z, si, track[si].curve, Mathf.Abs(track[si].curve) < p.straightGateCurve ? "open" : "SHUT"));
            }
            sb.AppendLine(vs.ToString());
            sb.AppendLine("      barriers there: " + track[track.SegmentIndexAt(79097)].barrierLeft + "/" + track[track.SegmentIndexAt(79097)].barrierRight
                + " (z79097), " + track[track.SegmentIndexAt(100901)].barrierLeft + "/" + track[track.SegmentIndexAt(100901)].barrierRight + " (z100901)");
            sb.AppendLine();

            // ---- [S1] road only hard steer --------------------------------------------
            sb.AppendLine("[S1] ROAD ONLY: full lock from the centre line, held 0.3 / 0.6 s, released.  Every place x speed x side");
            foreach (Place pl in places)
                foreach (float pct in speeds)
                    foreach (float hold in new[] { 0.3f, 0.6f })
                        foreach (int dir in new[] { 1, -1 })
                        {
                            Run r = RoadHardSteer(pl.seg, dir, pct, hold, string.Format("{0} {1:F2} {2:F1}s {3}", pl.name, pct, hold, dir > 0 ? "R" : "L"));
                            Execute(p, track, r);
                            var tr = Transitions(r, null); allTrans.AddRange(tr);
                            sb.AppendLine(Rowf(r.name, r, Measure(r), tr));
                        }
            sb.AppendLine();

            // ---- [S2]..[S4], [S6] ------------------------------------------------------
            sb.AppendLine("[S2] ROAD -> GRASS, lock held toward the grass from X=0.8, RELEASED g s after the grass starts, hands off after (no drive-back input; straight gate decides). TARMAC = same inputs, grass = tarmac");
            foreach (Place pl in places)
                foreach (float pct in speeds)
                    foreach (float g in new[] { 0.0f, 0.3f })
                        foreach (int side in new[] { 1, -1 })
                        {
                            string nm = string.Format("{0} {1:F2} g{2:F1} {3}", pl.name, pct, g, side > 0 ? "R" : "L");
                            Run r = RoadToGrassRelease(pl.seg, side, pct, g, nm);
                            r.traceIt = pct == 0.40f && side > 0 && g > 0f && pl.seg == straight;
                            Execute(p, track, r);
                            var tr = Transitions(r, tb); allTrans.AddRange(tr);
                            sb.AppendLine(Rowf(nm, r, Measure(r), tr));
                            if (pct == 0.40f && pl.seg == straight && g > 0f)
                            {
                                Run q = RoadToGrassRelease(pl.seg, side, pct, g, nm); Execute(road, track, q);
                                sb.AppendLine(Rowf("  TARMAC", q, Measure(q), new List<Trans>()));
                            }
                        }
            sb.AppendLine();

            sb.AppendLine("[S3] GRASS -> ROAD, lock held toward the road from X=+/-1.3 (on the grass), released d s after the tarmac is reached");
            foreach (Place pl in places)
                foreach (float pct in speeds)
                    foreach (float d in new[] { 0.0f, 0.15f, 0.30f })
                        foreach (int side in new[] { 1, -1 })
                        {
                            string nm = string.Format("{0} {1:F2} d{2:F2} {3}", pl.name, pct, d, side > 0 ? "fromR" : "fromL");
                            Run r = GrassToRoad(pl.seg, side, pct, d, nm);
                            r.traceIt = pct == 0.40f && side > 0 && d == 0.15f && pl.seg == straight;
                            Execute(p, track, r);
                            var tr = Transitions(r, tb); allTrans.AddRange(tr);
                            sb.AppendLine(Rowf(nm, r, Measure(r), tr));
                            if (pct == 0.40f && pl.seg == straight && d == 0.15f)
                            {
                                Run q = GrassToRoad(pl.seg, side, pct, d, nm); Execute(road, track, q);
                                sb.AppendLine(Rowf("  TARMAC", q, Measure(q), new List<Trans>()));
                            }
                        }
            sb.AppendLine();

            sb.AppendLine("[S4] ROAD -> GRASS -> ROAD with the lock held continuously: leave at 10/20 deg, hands off 0.2 s on the grass, lock back, release 0.2 s after re-entry");
            float worstS4 = 0f;
            foreach (Place pl in places)
                foreach (float pct in speeds)
                    foreach (float ang in new[] { 10f, 20f })
                        foreach (int side in new[] { 1, -1 })
                        {
                            string nm = string.Format("{0} {1:F2} {2:F0}deg {3}", pl.name, pct, ang, side > 0 ? "R" : "L");
                            Run r = RoadGrassRoad(pl.seg, side, pct, ang, 0.2f, 0.2f, nm);
                            r.traceIt = pct == 0.40f && side > 0 && ang == 10f && pl.seg == straight;
                            Execute(p, track, r);
                            var tr = Transitions(r, tb); allTrans.AddRange(tr);
                            M m = Measure(r);
                            if (pl.seg == straight) worstS4 = Mathf.Max(worstS4, m.travel);
                            sb.AppendLine(Rowf(nm, r, m, tr));
                            if (pct == 0.40f && pl.seg == straight && ang == 10f)
                            {
                                Run q = RoadGrassRoad(pl.seg, side, pct, ang, 0.2f, 0.2f, nm); Execute(road, track, q);
                                sb.AppendLine(Rowf("  TARMAC", q, Measure(q), new List<Trans>()));
                            }
                        }
            sb.AppendLine();

            sb.AppendLine("[S6] BARRIER HIT ON THE GRASS at 15 / 45 deg into the wall, lock held AWAY from the wall until back on tarmac + 0.15 s. impact yaw = before -> 0.1 s after the first hit, + = nose pointing INTO the wall (impact t -1 = no impact before release)");
            foreach (Place pl in places)
                foreach (float pct in speeds)
                    foreach (float ang in new[] { 15f, 45f })
                        foreach (int side in new[] { 1, -1 })
                        {
                            string nm = string.Format("{0} {1:F2} {2:F0}deg {3}", pl.name, pct, ang, side > 0 ? "wallR" : "wallL");
                            Run r = BarrierHit(track, pl.seg, side, pct, ang, nm);
                            r.traceIt = pct == 0.40f && side > 0 && pl.seg == straight;
                            Execute(p, track, r);
                            var tr = Transitions(r, tb); allTrans.AddRange(tr);
                            M m = Measure(r);
                            sb.AppendLine(Rowf(nm, r, m, tr) + string.Format("  | impact t {0:F2}s yaw {1:+0.0;-0.0}->{2:+0.0;-0.0} ({3})", m.impactT, m.impactYawBefore * side, m.impactYawAfter * side, "+ = nose into the wall"));
                        }
            sb.AppendLine();

            sb.AppendLine("[S6b] WALL KICK ALONE: hands off, nose 65 deg into the wall on the grass. yaw (+ = into the wall) just before / +0.1 / +0.3 / +0.6 s after the first hit; speed before -> +0.1 s. A NEGATIVE value = the hit swung the nose past zero toward the road");
            foreach (Place pl in new[] { places[0], places[1] })
                foreach (float pct in speeds)
                    foreach (int side in new[] { 1, -1 })
                    {
                        string nm = string.Format("{0} {1:F2} 65deg {2}", pl.name, pct, side > 0 ? "wallR" : "wallL");
                        float limit = side > 0 ? track[pl.seg].barrierRight : -track[pl.seg].barrierLeft;
                        Run r = Base(nm, pl.seg, side, pct, side * (limit - 0.30f), side * 65f, 3f);
                        r.driver = (sim, rr) => 0f;
                        Execute(p, track, r);
                        int ii = -1;
                        for (int k = 1; k < r.s.Count; k++) if (r.s[k].impact) { ii = k; break; }
                        if (ii < 0) { sb.AppendLine("    " + nm + "  no impact"); continue; }
                        Func<int, float> yw = k => r.s[Mathf.Min(r.s.Count - 1, k)].yaw * Mathf.Rad2Deg * side;
                        sb.AppendLine(string.Format("    {0,-26} impact t {1:F2}s  yaw {2,6:F1} -> {3,6:F1} -> {4,6:F1} -> {5,6:F1}   speed {6:F2} -> {7:F2}   yawRate before {8:F2} after {9:F2} rad/s",
                            nm, r.s[ii].t, yw(ii - 1), yw(ii + 6), yw(ii + 18), yw(ii + 36), r.s[ii - 1].spdPct, r.s[ii + 6].spdPct, r.s[ii - 1].wyr * side, r.s[ii + 6].wyr * side));
                    }
            sb.AppendLine();

            // ---- [S5] ping-pong -------------------------------------------------------
            sb.AppendLine("[S5] MAJ'S PING-PONG, 10 s: lock toward the road once on the grass/wall for `react` s; hold until past the centre line (Maj) or until the nose points Y deg back across the road (careful)");
            Run main = null; PP mainPP = default(PP);
            foreach (Place pl in new[] { places[0], places[1] })
                foreach (float pct in new[] { 0.40f, 0.25f })
                    foreach (float react in new[] { 0.15f, 0.25f, 0.40f })
                        foreach (float yr in new[] { -1f, 25f })
                        {
                            string nm = string.Format("{0} {1:F2} react {2:F2} {3}", pl.name, pct, react, yr < 0f ? "hold-to-centre" : "release@" + yr.ToString("F0") + "deg");
                            Run r = PingPong(pl.seg, pct, react, yr, 10f, nm);
                            bool headline = pl.seg == straight && pct == 0.40f && react == 0.25f && yr < 0f;
                            r.traceIt = headline;
                            Execute(p, track, r);
                            Transitions(r, tb);
                            PP o = MeasurePP(r);
                            sb.AppendLine(PPRow(nm, o));
                            if (headline)
                            {
                                main = r; mainPP = o;
                                Run q = PingPong(pl.seg, pct, react, yr, 10f, nm); Execute(road, track, q);
                                sb.AppendLine(PPRow("  TARMAC (grass = tarmac)", MeasurePP(q)));
                            }
                        }
            sb.AppendLine(string.Format("    headline bar: crossings in 10 s {0} (<= {1}) {2};  peak yaw {3:F1} (<= {4:F0}) {5}",
                mainPP.crossings, BarMaxPingPongCrossings, Ok(mainPP.crossings <= BarMaxPingPongCrossings),
                mainPP.peakYaw, BarMaxPeakYawDeg, Ok(mainPP.peakYaw <= BarMaxPeakYawDeg)));
            sb.AppendLine();

            // ---- [T] transitions: discontinuity aggregate ------------------------------
            sb.AppendLine("[T] SURFACE-TRANSITION DISCONTINUITY SUMMARY over every run above (single 1/60 s step at the transition; window = step -1..+4)");
            foreach (string kind in new[] { "ROAD->GRASS", "GRASS->ROAD" })
            {
                int n = 0, fl = 0; float mc = 0, mg = 0, ml = 0, my = 0, sc = 0, sCeilW = 0, sSpd = 0; float maxCeilW = 0f, minCeilW = 0f;
                for (int i = 0; i < allTrans.Count; i++)
                {
                    Trans q = allTrans[i]; if (q.kind != kind) continue;
                    n++; if (q.flag) fl++;
                    mc = Mathf.Max(mc, Mathf.Abs(q.dCeil1)); mg = Mathf.Max(mg, Mathf.Abs(q.dGrip1));
                    ml = Mathf.Max(ml, q.dLatW); my = Mathf.Max(my, q.dYRW);
                    sCeilW += q.dCeilW; sSpd += q.spdA - q.spdB;
                    maxCeilW = Mathf.Max(maxCeilW, q.dCeilW); minCeilW = Mathf.Min(minCeilW, q.dCeilW);
                }
                sb.AppendLine(string.Format("    {0}: n {1}, flagged {2}; max one-step dCeil {3:F2} rad/s, dGrip {4:F2}, dLatV {5:F2} hw/s, dYawRate {6:F2} rad/s;  ceiling across the blend (-1..+4 steps): mean {7:+0.00;-0.00} [{8:+0.00;-0.00}..{9:+0.00;-0.00}] rad/s; speed change over the window mean {10:+0.000;-0.000}",
                    kind, n, fl, mc, mg, ml, my, n > 0 ? sCeilW / n : 0f, minCeilW, maxCeilW, n > 0 ? sSpd / n : 0f));
            }
            sb.AppendLine("    biggest single-step jumps (any run):");
            allTrans.Sort((a, b) => (Mathf.Max(b.dLatW / DiscLatV, b.dYRW / DiscYawRate)).CompareTo(Mathf.Max(a.dLatW / DiscLatV, a.dYRW / DiscYawRate)));
            for (int i = 0; i < Mathf.Min(8, allTrans.Count); i++)
            {
                Trans q = allTrans[i];
                sb.AppendLine(string.Format("      {0,-34} {1} t {2:F2}  dLatV/step {3:F2}  dYawRate/step {4:F2}  ceil {5:F2}->{6:F2}  grip {7:F2}->{8:F2}  spd {9:F2}->{10:F2}  yaw {11:F1}->{12:F1}",
                    q.run, q.kind, q.t, q.dLatW, q.dYRW, q.ceilB, q.ceilA, q.gripB, q.gripA, q.spdB, q.spdA, q.yawB, q.yawA));
            }
            sb.AppendLine();

            // ---- [V] profile variants -------------------------------------------------
            sb.AppendLine("[V] PROFILE WHAT-IFS (diagnostic only, none applied), all at 0.40: hard steer held 0.6 s on the straight and on the 2.4 BEND (the video's stretch, gate shut); S4 on the bend; ping-pong 10 s (react 0.25, hold-to-centre) on both");
            sb.AppendLine(QuickRow(p, track, straight, "baseline"));
            Variant(sb, p, track, straight, "envelope 25->45 deg", q => { q.steerYawSoftLimit = 25f * Mathf.Deg2Rad; q.steerYawHardLimit = 45f * Mathf.Deg2Rad; });
            Variant(sb, p, track, straight, "envelope 20->35 deg", q => { q.steerYawSoftLimit = 20f * Mathf.Deg2Rad; q.steerYawHardLimit = 35f * Mathf.Deg2Rad; });
            Variant(sb, p, track, straight, "lowSpeedSteering 0.75 (flat authority)", q => { q.lowSpeedSteering = q.highSpeedSteering; });
            Variant(sb, p, track, straight, "lowSpeedSteering 0.55", q => { q.lowSpeedSteering = 0.55f; });
            Variant(sb, p, track, straight, "envelope 25->45 + lowSpeedSteering 0.75", q => { q.steerYawSoftLimit = 25f * Mathf.Deg2Rad; q.steerYawHardLimit = 45f * Mathf.Deg2Rad; q.lowSpeedSteering = q.highSpeedSteering; });
            Variant(sb, p, track, straight, "collisionYawKick 0", q => { q.collisionYawKick = 0f; });
            Variant(sb, p, track, straight, "barrierYawRecovery 6", q => { q.barrierYawRecovery = 6f; });
            Variant(sb, p, track, straight, "yawAuthoritySpeed 0.5 (ceiling ramps to 50% speed)", q => { q.yawAuthoritySpeed = 0.5f; });
            Variant(sb, p, track, straight, "envelope 25->45 + yawAuthoritySpeed 0.5", q => { q.steerYawSoftLimit = 25f * Mathf.Deg2Rad; q.steerYawHardLimit = 45f * Mathf.Deg2Rad; q.yawAuthoritySpeed = 0.5f; });
            Variant(sb, p, track, straight, "straightGateCurve 2.6 (gate open on the 2.4 bend; autosteer-ish, info)", q => { q.straightGateCurve = 2.6f; });
            Variant(sb, p, track, straight, "yawRateRelease 40 (stop rotating at once)", q => { q.yawRateRelease = 40f; });
            Variant(sb, p, track, straight, "offRoadYawLoss 0.7", q => { q.offRoadYawLoss = 0.7f; });
            Variant(sb, p, track, straight, "offRoadSpeedCap 0.7 + drag 0.04", q => { q.offRoadSpeedCap = 0.7f; q.offRoadDrag = 0.04f; });
            sb.AppendLine();
            sb.AppendLine(string.Format("[BARS] road-only hard steer 0.6 s @0.40 straight travel <= {0:F1} hw; S4 worst straight travel {1:F2}; ping-pong headline above. EnforceBars = {2}. No PASS/FAIL gating in this baseline.", BarMaxTravelAfterRelease, worstS4, EnforceBars));

            // ---- headline ping-pong time series (compact) ----------------------------
            if (main != null)
            {
                sb.AppendLine();
                sb.AppendLine("[T5] HEADLINE PING-PONG time series every 0.25 s (straight 0.40 react 0.25 hold-to-centre)");
                sb.AppendLine("      t    surf  x      spd%  yaw   cmd  latV   slipV  wYawR ceil  gate");
                for (int i = 0; i < main.s.Count; i += 15)
                {
                    Sample a = main.s[i];
                    sb.AppendLine(string.Format("    {0,5:F2} {1,-6} {2,5:F2} {3,4:F0} {4,6:F1} {5,4:F1} {6,6:F2} {7,6:F2} {8,6:F2} {9,5:F2} {10,4:F2}{11}",
                        a.t, a.surf, a.x, a.spdPct * 100f, a.yaw * Mathf.Rad2Deg, a.cmd, a.latV, a.slipV / Hw, a.wyr, a.ceil, a.gate, a.impact ? " IMPACT" : ""));
                }
            }

            UnityEngine.Object.DestroyImmediate(road);
            traces = "=================== GLIDE PROBE TRACES (+/-1 s around each surface transition, every 0.05 s) ===================\n" + tb;
            sb.AppendLine("GLIDE PROBE BASELINE DONE");
            return sb.ToString();
        }

        private static string QuickRow(HandlingProfile p, TrackModel t, int straight, string label)
        {
            int bend = FindCorner(t, 1.5f, 2.5f);
            if (bend < 0) bend = FindCorner(t, 1f, 3f);
            Run a = RoadHardSteer(straight, 1, 0.40f, 0.6f, "a"); Execute(p, t, a); M ma = Measure(a);
            Run a2 = RoadHardSteer(bend, 1, 0.40f, 0.6f, "a2"); Execute(p, t, a2); M ma2 = Measure(a2);
            Run b = RoadGrassRoad(bend, 1, 0.40f, 10f, 0.2f, 0.2f, "b"); Execute(p, t, b); M mb = Measure(b);
            Run d = PingPong(straight, 0.40f, 0.25f, -1f, 10f, "d"); Execute(p, t, d); PP o = MeasurePP(d);
            Run e = PingPong(bend, 0.40f, 0.25f, -1f, 10f, "e"); Execute(p, t, e); PP o2 = MeasurePP(e);
            return string.Format("    {0,-40} hard0.6s straight: yaw {1,4:F0} travel {2,4:F2} | bend: yaw {3,4:F0} travel {4,4:F2} settle {5,4} | S4 bend 10deg: yaw {6,4:F0} travel {7,4:F2} | pingpong straight: X{8} walls {9} pk {10:F0} | pingpong BEND: X{11} (avg {12:F2}s) walls {13} pkYaw {14:F0} yaw>60 {15:F0}% latV>2 {16:F0}% spd {17:F2}",
                label, ma.yawRel, ma.travel, ma2.yawRel, ma2.travel, ma2.settled ? ma2.settle.ToString("F1") : ">4", mb.yawRel, mb.travel,
                o.crossings, o.impacts, o.peakYaw, o2.crossings, o2.avgCross, o2.impacts, o2.peakYaw, o2.yaw60Pct, o2.fastLatPct, o2.meanSpd);
        }

        private static void Variant(StringBuilder sb, HandlingProfile p, TrackModel t, int straight, string name, Action<HandlingProfile> edit)
        {
            HandlingProfile q = UnityEngine.Object.Instantiate(p);
            edit(q);
            sb.AppendLine(QuickRow(q, t, straight, name));
            UnityEngine.Object.DestroyImmediate(q);
        }

        /// <summary>Compact one-line-per-profile summary for re-measuring a fix.</summary>
        public static string QuickSummary(HandlingProfile p, TrackModel t)
        {
            return QuickRow(p, t, FindStraight(t) + 10, p.name);
        }
    }
}
